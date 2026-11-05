using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using MQTTnet.Formatter;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMqttBrokerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActualBrokerObservesV311QosOneNonRetainedScalarPayloadsAndAuthentication()
    {
        await using var broker = await SidebandMqttTestBroker.StartAsync(Token);
        var errors = new ConcurrentQueue<string>();
        await using var bridge = new SidebandMqttBridge(errors.Enqueue);
        bridge.Start(broker.Configuration, Token);
        Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context,
            SidebandTelemetryCodec.Create(1700000000L)));
        var expected = SidebandMqtt.Project(SidebandMqttVectorTests.Context, SidebandTelemetryCodec.Create(1700000000L));
        foreach (var message in expected.Messages)
        {
            var actual = await broker.ReadAsync(Token);
            Assert.Equal(message.Topic, actual.Topic);
            Assert.Equal(message.Payload.ToArray(), actual.Payload);
            Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, actual.Quality);
            Assert.False(actual.Retain);
        }
        Assert.Equal(MqttProtocolVersion.V311, await broker.Protocol.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.True(broker.AuthenticationAccepted);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ActualRejectedCredentialsRecoverWithChangedConfigurationAndRemainRedacted()
    {
        await using var broker = await SidebandMqttTestBroker.StartAsync(Token);
        var errors = new ConcurrentQueue<string>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var bridge = new SidebandMqttBridge(error =>
        {
            errors.Enqueue(error);
            failed.TrySetResult();
        });
        bridge.Start(broker.Configuration with { Password = "synthetic-wrong-password" }, Token);
        Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context,
            SidebandTelemetryCodec.Create(1700000000L)));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.False(broker.AuthenticationAccepted);
        bridge.Configure(broker.Configuration);
        Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context,
            SidebandTelemetryCodec.Create(1700000001L)));
        var actual = await broker.ReadAsync(Token);
        Assert.EndsWith("/time/utc", actual.Topic);
        Assert.Equal("1700000001", Encoding.ASCII.GetString(actual.Payload));
        Assert.True(broker.AuthenticationAccepted);
        Assert.All(errors, error => Assert.DoesNotContain("synthetic-", error));
    }

    [Fact]
    public async Task ActualBrokerOutageRetainsTheReportAndRecoversAfterRepeatedRestart()
    {
        await using var broker = await SidebandMqttTestBroker.StartAsync(Token);
        var failures = Channel.CreateUnbounded<string>();
        await using var bridge = new SidebandMqttBridge(error => failures.Writer.TryWrite(error));
        bridge.Start(broker.Configuration, Token);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await broker.StopAsync(Token);
            Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context,
                SidebandTelemetryCodec.Create(1700000100L + cycle)));
            await failures.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
            await broker.RestartAsync(Token);
            var expected = (1700000100L + cycle).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await broker.ReadUntilAsync(message => message.Topic.EndsWith("/time/utc", StringComparison.Ordinal) &&
                Encoding.ASCII.GetString(message.Payload) == expected, Token);
        }
    }

    [Fact]
    public async Task UnexpectedMqttFailureStillDisposesTransportAndWorkersBeforeRethrowAndReconnect()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "mqtt-fault-" + Guid.NewGuid().ToString("N"));
        try
        {
            int port;
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            var config = Path.Combine(root, "reticulum");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n" +
                $" [[Fault recovery]]\n type = TCPServerInterface\n enabled = yes\n listen_ip = 127.0.0.1\n listen_port = {port}\n", Token);
            await using var client = new SidebandClient(new(root));
            await client.ConnectAsync(config, Token);
            var hash = client.ConnectionInfo!.DeliveryHash;
            using (var probe = new TcpClient()) await probe.ConnectAsync(IPAddress.Loopback, port, Token);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var field = typeof(SidebandClient).GetField("_mqtt", flags)!;
            await Assert.IsType<SidebandMqttBridge>(field.GetValue(client)).DisposeAsync();
            var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bridge = new SidebandMqttBridge(_ => reported.TrySetResult(), (_, _, _) =>
                Task.FromException(new InvalidOperationException("Synthetic unexpected publication failure.")));
            field.SetValue(client, bridge);
            bridge.Start(new() { Enabled = true, Host = "synthetic.invalid" }, Token);
            Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context,
                SidebandTelemetryCodec.Create(1700000000L)));
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.DisconnectAsync());
            Assert.Null(client.ConnectionInfo);
            foreach (var name in new[] { "_mqtt", "_telemetryOutboundTask", "_telemetryScheduleTask", "_inboundTask", "_periodicTask" })
                Assert.Null(typeof(SidebandClient).GetField(name, flags)!.GetValue(client));
            using (var probe = new TcpClient())
                await Assert.ThrowsAsync<SocketException>(async () => await probe.ConnectAsync(IPAddress.Loopback, port, Token));
            await client.ConnectAsync(config, Token);
            Assert.Equal(hash, client.ConnectionInfo!.DeliveryHash);
            using (var probe = new TcpClient()) await probe.ConnectAsync(IPAddress.Loopback, port, Token);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClientForwardsNewLocalReportsWithoutAnySubscribersAndPersistsSettingsOverReconnect()
    {
        await using var broker = await SidebandMqttTestBroker.StartAsync(Token);
        var root = Path.Combine(AppContext.BaseDirectory, "mqtt-client-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = Path.Combine(root, "reticulum");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                "[reticulum]\nshare_instance=No\nenable_transport=No\n[interfaces]\n", Token);
            string hash;
            await using (var client = new SidebandClient(new(root)))
            {
                await client.ConfigureTelemetryAsync(new() { Enabled = true, Mqtt = broker.Configuration }, Token);
                await client.ConnectAsync(config, Token);
                hash = client.ConnectionInfo!.DeliveryHash;
                var report = await client.RefreshLocalTelemetryAsync(Token);
                var payload = SidebandMqttPayload.Encode(report!.TimestampValue!);
                var actual = await broker.ReadUntilAsync(message => message.Topic == $"lxmf/telemetry/{hash}/time/utc" &&
                    message.Payload.AsSpan().SequenceEqual(payload), Token);
                Assert.NotEmpty(actual.Payload);
            }
            await using var reopened = new SidebandClient(new(root));
            Assert.Equal(broker.Configuration, (await reopened.LoadAsync(Token)).Telemetry.Mqtt);
            await reopened.ConnectAsync(config, Token);
            Assert.Equal(hash, reopened.ConnectionInfo!.DeliveryHash);
            var prepared = await reopened.RefreshLocalTelemetryAsync(Token);
            var expected = SidebandMqttPayload.Encode(prepared!.TimestampValue!);
            var next = await broker.ReadUntilAsync(message => message.Topic == $"lxmf/telemetry/{hash}/time/utc" &&
                message.Payload.AsSpan().SequenceEqual(expected), Token);
            Assert.NotEmpty(next.Payload);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

internal sealed class SidebandMqttTestBroker : IAsyncDisposable
{
    private readonly MqttServer _server;
    private readonly Channel<SidebandMqttObservedMessage> _messages = Channel.CreateBounded<SidebandMqttObservedMessage>(4096);
    private readonly ConcurrentQueue<SidebandMqttObservedMessage> _history = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private int _connections;
    private int _authenticated;
    internal TaskCompletionSource<MqttProtocolVersion> Protocol { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool AuthenticationAccepted => Volatile.Read(ref _authenticated) != 0;
    internal IReadOnlyList<SidebandMqttObservedMessage> History => _history.ToArray();
    internal IReadOnlyList<string> Errors => _errors.ToArray();
    internal SidebandMqttConfiguration Configuration { get; }

    private SidebandMqttTestBroker(int port)
    {
        Configuration = new()
        {
            Enabled = true, Host = "127.0.0.1", Port = port,
            Username = "synthetic-mqtt-user", Password = "synthetic-mqtt-password"
        };
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port)
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).Build();
        var logger = new MqttNetEventLogger();
        logger.LogMessagePublished += (sender, args) =>
        {
            if (args.LogMessage.Level < MqttNetLogLevel.Warning &&
                !args.LogMessage.Message.Contains("disconnect", StringComparison.OrdinalIgnoreCase)) return;
            if (_errors.Count >= 128) _errors.TryDequeue(out _);
            _errors.Enqueue(args.LogMessage.Message + " " + args.LogMessage.Exception);
        };
        _server = new MqttServerFactory(logger).CreateMqttServer(options);
        _server.ValidatingConnectionAsync += connection =>
        {
            Protocol.TrySetResult(connection.ProtocolVersion);
            if (connection.UserName != Configuration.Username && connection.UserName != "synthetic-primary-mqtt-user" ||
                connection.Password != Configuration.Password)
                connection.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            else
            {
                if (string.IsNullOrEmpty(connection.ClientId))
                    connection.AssignedClientIdentifier = Guid.NewGuid().ToString("N");
                if (Interlocked.Increment(ref _connections) > 2048)
                    throw new InvalidOperationException("Synthetic broker client observation limit reached.");
                Volatile.Write(ref _authenticated, 1);
            }
            return Task.CompletedTask;
        };
        _server.InterceptingPublishAsync += publish =>
        {
            var message = publish.ApplicationMessage;
            var observed = new SidebandMqttObservedMessage(message.Topic, message.Payload.ToArray(), message.QualityOfServiceLevel,
                message.Retain, publish.UserName);
            if (_history.Count >= 4096 || !_messages.Writer.TryWrite(observed))
                throw new InvalidOperationException("The synthetic MQTT acceptance queue is full.");
            _history.Enqueue(observed);
            return Task.CompletedTask;
        };
    }

    internal static async Task<SidebandMqttTestBroker> StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var broker = new SidebandMqttTestBroker(port);
        try { await broker._server.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), token); return broker; }
        catch { await broker.DisposeAsync(); throw; }
    }

    internal async Task<SidebandMqttObservedMessage> ReadAsync(CancellationToken token) =>
        await _messages.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), token);

    internal Task StopAsync(CancellationToken token) => _server.StopAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
    internal Task RestartAsync(CancellationToken token) => _server.StartAsync().WaitAsync(TimeSpan.FromSeconds(10), token);

    internal async Task<SidebandMqttObservedMessage> ReadUntilAsync(Func<SidebandMqttObservedMessage, bool> predicate,
        CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var message = await _messages.Reader.ReadAsync(deadline.Token);
            if (predicate(message)) return message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_server.IsStarted) await _server.StopAsync(); }
        finally { _server.Dispose(); _messages.Writer.TryComplete(); }
    }
}

internal sealed record SidebandMqttObservedMessage(string Topic, byte[] Payload, MqttQualityOfServiceLevel Quality,
    bool Retain, string Username);
