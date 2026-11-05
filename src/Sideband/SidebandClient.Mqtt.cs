namespace Sideband;

public sealed partial class SidebandClient
{
    private const int MqttLocalRefreshSeconds = 60;
    private SidebandMqttBridge? _mqtt;
    private Lazy<SidebandGeoid>? _mqttGeoid;

    private void StartMqtt(SidebandMqttConfiguration configuration, CancellationToken token)
    {
        var directory = Environment.GetEnvironmentVariable("TELEMETER_GEOID_PATH") ??
            Path.Combine(_store.DataDirectory, "geoids");
        var geoid = new Lazy<SidebandGeoid>(() => SidebandGeoid.Open(Path.Combine(directory, "egm2008-5.pgm")),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _mqttGeoid = geoid;
        var bridge = new SidebandMqttBridge(Report, geoidOffset: (latitude, longitude) => geoid.Value.Offset(latitude, longitude));
        bridge.Start(configuration, token);
        _mqtt = bridge;
    }

    private async Task StopMqttAsync()
    {
        var bridge = Interlocked.Exchange(ref _mqtt, null);
        var geoid = Interlocked.Exchange(ref _mqttGeoid, null);
        try { if (bridge is not null) await bridge.DisposeAsync().ConfigureAwait(false); }
        finally { if (geoid?.IsValueCreated == true) geoid.Value.Dispose(); }
    }

    private void QueueTelemetryToMqtt(string source, SidebandTelemetryReport report)
    {
        if (_mqtt is not { } bridge) return;
        var admission = bridge.Enqueue(source, report);
        if (admission == SidebandMqttAdmission.Full)
            Report("The bounded MQTT queue is full; this newly stored report was not queued for forwarding.");
        else if (admission == SidebandMqttAdmission.Stopped && _runCancellation?.IsCancellationRequested == false)
            Report("The MQTT bridge is stopped; reconnect before forwarding new telemetry.");
    }
}
