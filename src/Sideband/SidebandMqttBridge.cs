using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Exceptions;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Sideband;

internal enum SidebandMqttAdmission { Queued, Disabled, Full, Stopped }

internal sealed class SidebandMqttBridge(
    Action<string> report,
    Func<SidebandMqttConfiguration, IReadOnlyList<SidebandMqttMessage>, CancellationToken, Task>? publish = null,
    Func<double, double, double>? geoidOffset = null,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    internal const int QueueCapacity = 8;
    private readonly object _gate = new();
    private readonly Action<string> _report = report ?? throw new ArgumentNullException(nameof(report));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Func<SidebandMqttConfiguration, IReadOnlyList<SidebandMqttMessage>, CancellationToken, Task> _publish = publish ?? PublishAsync;
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(QueueCapacity)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private SidebandMqttConfiguration _configuration = new();
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private TaskCompletionSource? _dispose;
    private Batch? _active;
    private long _generation;
    private bool _disposed;

    internal void Start(SidebandMqttConfiguration configuration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null) throw new InvalidOperationException("The MQTT bridge is already started.");
            token.ThrowIfCancellationRequested();
            _configuration = configuration;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _worker = RunAsync(_stop.Token);
        }
    }

    internal void Configure(SidebandMqttConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        lock (_gate)
        {
            if (_disposed || _configuration == configuration) return;
            _configuration = configuration;
            _generation = checked(_generation + 1);
            if (_active is { } active) active.Cancellation ??= active.Stop.CancelAsync();
        }
    }

    internal SidebandMqttAdmission Enqueue(string source, SidebandTelemetryReport telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (!SidebandContact.TryNormalizeDeliveryHash(source, out var hash))
            throw new ArgumentException("MQTT context must be a delivery hash.", nameof(source));
        lock (_gate)
        {
            if (_disposed || _worker is null || _worker.IsCompleted || _stop!.IsCancellationRequested) return SidebandMqttAdmission.Stopped;
            if (!_configuration.Enabled) return SidebandMqttAdmission.Disabled;
            return _queue.Writer.TryWrite(new(hash, telemetry, _generation))
                ? SidebandMqttAdmission.Queued : SidebandMqttAdmission.Full;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                Batch batch;
                SidebandMqttConfiguration configuration;
                lock (_gate)
                {
                    if (work.Generation != _generation || !_configuration.Enabled) continue;
                    configuration = _configuration;
                    _active = batch = new(CancellationTokenSource.CreateLinkedTokenSource(token));
                }
                try
                {
                    SidebandMqttProjection projection;
                    try { projection = SidebandMqtt.Project(work.Source, work.Telemetry, geoidOffset); }
                    catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException)
                    {
                        Report("MQTT report rejected before publishing: " + error.GetType().Name);
                        continue;
                    }
                    foreach (var error in projection.Errors) Report(error);
                    if (projection.Messages.Count == 0) continue;
                    while (!batch.Stop.IsCancellationRequested)
                    {
                        try
                        {
                            await _publish(configuration, projection.Messages, batch.Stop.Token).ConfigureAwait(false);
                            break;
                        }
                        catch (OperationCanceledException) when (batch.Stop.IsCancellationRequested) { break; }
                        catch (Exception error) when (error is IOException or SocketException or MqttCommunicationException or
                            TimeoutException or OperationCanceledException)
                        {
                            Report("MQTT broker operation failed (" + error.GetType().Name +
                                "); the bounded report remains queued for retry. Credentials and payloads are not logged.");
                            await Task.Delay(TimeSpan.FromSeconds(1), _clock, batch.Stop.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (batch.Stop.IsCancellationRequested) { }
                finally
                {
                    Task? cancellation;
                    lock (_gate)
                    {
                        if (ReferenceEquals(_active, batch)) _active = null;
                        cancellation = batch.Cancellation;
                    }
                    if (cancellation is not null)
                    {
                        try { await cancellation.ConfigureAwait(false); }
                        catch (AggregateException error) { Report("An MQTT cancellation callback failed: " + error.GetType().Name); }
                    }
                    batch.Stop.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Report("The MQTT bridge stopped after an unexpected failure: " + error.GetType().Name);
            throw;
        }
    }

    private void Report(string message)
    {
        try { _report(message); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("An MQTT error observer failed: {0}", error.GetType().Name);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task? worker;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            if (_dispose is { } pending) return new(pending.Task);
            _disposed = true;
            completion = _dispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Writer.TryComplete();
            worker = _worker;
            stop = _stop;
        }
        _ = FinishDisposalAsync(completion, worker, stop);
        return new(completion.Task);
    }

    private async Task FinishDisposalAsync(TaskCompletionSource completion, Task? worker, CancellationTokenSource? stop)
    {
        try
        {
            try
            {
                if (stop is not null)
                {
                    try { await stop.CancelAsync().ConfigureAwait(false); }
                    catch (AggregateException error) { Report("An MQTT stop callback failed: " + error.GetType().Name); }
                }
                if (worker is not null) await worker.ConfigureAwait(false);
            }
            finally { stop?.Dispose(); }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private static async Task PublishAsync(SidebandMqttConfiguration configuration,
        IReadOnlyList<SidebandMqttMessage> messages, CancellationToken token)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        var password = configuration.Password is null ? null : Encoding.UTF8.GetBytes(configuration.Password);
        try
        {
            var builder = new MqttClientOptionsBuilder().WithTcpServer(configuration.Host!, configuration.EffectivePort)
                .WithProtocolVersion(MqttProtocolVersion.V311).WithCleanSession();
            if (configuration.Username is { } username) builder.WithCredentials(username, password);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                var result = await client.ConnectAsync(builder.Build(), connect.Token).ConfigureAwait(false);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                    throw new MqttCommunicationException("MQTT broker refused the connection.");
            }
            foreach (var message in messages)
            {
                using var publish = CancellationTokenSource.CreateLinkedTokenSource(token);
                publish.CancelAfter(TimeSpan.FromSeconds(10));
                var packet = new MqttApplicationMessageBuilder().WithTopic(message.Topic).WithPayload(message.Payload.ToArray())
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build();
                await client.PublishAsync(packet, publish.Token).ConfigureAwait(false);
            }
            using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(token);
            disconnect.CancelAfter(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), disconnect.Token).ConfigureAwait(false);
        }
        finally
        {
            try { client.Dispose(); }
            finally { if (password is not null) CryptographicOperations.ZeroMemory(password); }
        }
    }

    private sealed record Work(string Source, SidebandTelemetryReport Telemetry, long Generation);
    private sealed class Batch(CancellationTokenSource stop)
    {
        internal CancellationTokenSource Stop { get; } = stop;
        internal Task? Cancellation { get; set; }
    }
}
