using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Sideband.Tests;

internal sealed class SidebandMqttPrimaryBroker : IAsyncDisposable
{
    private const string Publisher = "synthetic-primary-mqtt-user";
    private const string Observer = "synthetic-mqtt-observer";
    private const string Password = "synthetic-mqtt-password";
    private readonly IMqttClient _observer = new MqttClientFactory().CreateMqttClient();
    private readonly Channel<SidebandMqttObservedMessage> _messages = Channel.CreateBounded<SidebandMqttObservedMessage>(4096);
    private readonly Process _process;
    private readonly Task _log;
    internal int Port { get; }

    private SidebandMqttPrimaryBroker(Process process, int port, string log)
    {
        _process = process;
        Port = port;
        _log = CaptureAsync(process.StandardError, log);
        _observer.ApplicationMessageReceivedAsync += received =>
        {
            var message = received.ApplicationMessage;
            // The broker ACL grants publication only to this synthetic primary identity.
            if (!_messages.Writer.TryWrite(new(message.Topic, message.Payload.ToArray(),
                message.QualityOfServiceLevel, message.Retain, Publisher)))
                _messages.Writer.TryComplete(new InvalidOperationException("The primary MQTT observation queue is full."));
            return Task.CompletedTask;
        };
    }

    internal static async Task<SidebandMqttPrimaryBroker> StartAsync(string directory, CancellationToken token)
    {
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_MOSQUITTO_BINARY")
            ?? throw new InvalidOperationException("Select an isolated Mosquitto binary accepting primary's empty MQTT 3.1.1 client ID.");
        var expected = Environment.GetEnvironmentVariable("DOTRETICULUM_MOSQUITTO_SHA256")
            ?? throw new InvalidOperationException("Select the exact Mosquitto binary hash.");
        if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))) != expected)
            throw new InvalidOperationException("The primary MQTT broker hash changed.");
        Directory.CreateDirectory(directory);
        var passwords = Path.Combine(directory, "synthetic-passwords");
        await PasswordFileAsync(true, Publisher);
        await PasswordFileAsync(false, Observer);
        var acl = Path.Combine(directory, "synthetic-acl");
        await File.WriteAllTextAsync(acl, $"user {Publisher}\ntopic write lxmf/telemetry/#\n" +
            $"user {Observer}\ntopic read lxmf/telemetry/#\n", token);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var config = Path.Combine(directory, "mosquitto.conf");
        await File.WriteAllTextAsync(config, $"listener {port} 127.0.0.1\nallow_anonymous false\n" +
            $"password_file {passwords}\nacl_file {acl}\npersistence false\nlog_type all\nlog_dest stderr\n", token);
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(config);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated primary MQTT broker.");
        var broker = new SidebandMqttPrimaryBroker(process, port, Path.Combine(directory, "broker.log"));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException("The primary MQTT broker exited before readiness.");
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(IPAddress.Loopback, port, deadline.Token); break; }
                catch (SocketException) { await Task.Delay(25, deadline.Token); }
            }
            var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port)
                .WithProtocolVersion(MqttProtocolVersion.V311).WithCredentials(Observer, Password).WithCleanSession().Build();
            var connected = await broker._observer.ConnectAsync(options, deadline.Token);
            if (connected.ResultCode != MqttClientConnectResultCode.Success)
                throw new InvalidOperationException("The primary MQTT observer was not authenticated.");
            var subscriptions = await broker._observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter.WithTopic("lxmf/telemetry/#")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build(), deadline.Token);
            if (subscriptions.Items.Single().ResultCode != MqttClientSubscribeResultCode.GrantedQoS1)
                throw new InvalidOperationException("The primary MQTT observation subscription was rejected.");
            return broker;
        }
        catch { await broker.DisposeAsync(); throw; }

        async Task PasswordFileAsync(bool create, string user)
        {
            var startPassword = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(binary)!, "mosquitto_passwd.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in create ? new[] { "-b", "-c", passwords, user, Password } : ["-b", passwords, user, Password])
                startPassword.ArgumentList.Add(argument);
            using var command = Process.Start(startPassword) ?? throw new InvalidOperationException("Could not create synthetic broker credentials.");
            await command.WaitForExitAsync(token);
            if (command.ExitCode != 0) throw new InvalidOperationException("Synthetic broker credential creation failed.");
        }
    }

    internal async Task<SidebandMqttObservedMessage> ReadUntilAsync(Func<SidebandMqttObservedMessage, bool> predicate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var message = await _messages.Reader.ReadAsync(deadline.Token);
            if (predicate(message)) return message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _observer.Dispose(); }
        finally
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            await _log;
            _process.Dispose();
            _messages.Writer.TryComplete();
        }
    }

    private static async Task CaptureAsync(StreamReader source, string destination)
    {
        await using var target = new StreamWriter(destination);
        var count = 0;
        while (await source.ReadLineAsync() is { } line)
        {
            if (++count > 4096) throw new InvalidOperationException("The synthetic primary broker log limit was exceeded.");
            await target.WriteLineAsync(line);
            await target.FlushAsync();
        }
    }
}
