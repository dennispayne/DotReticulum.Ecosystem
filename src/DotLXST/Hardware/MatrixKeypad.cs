namespace DotLXST.Hardware;

public enum PinDirection { Input, Output }
public enum PinPull { Disabled, Down, Up }
public enum KeyTransition { Up, Down }

public interface IDigitalPins
{
    void Configure(int pin, PinDirection direction, PinPull pull = PinPull.Disabled);
    void Write(int pin, bool high);
    bool Read(int pin);
}

/// <summary>Four-by-four matrix scanning and hook debounce; the caller owns the GPIO backend.</summary>
public sealed class MatrixKeypad : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IDigitalPins _pins;
    private readonly int[] _rows;
    private readonly int[] _columns;
    private readonly string[,] _map;
    private readonly Dictionary<string, bool> _states = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private int? _hook;
    private TimeSpan _hookTime;
    private TimeSpan _lastScan;
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;
    private Task? _disposal;
    private bool _disposed;

    public MatrixKeypad(IDigitalPins pins, IReadOnlyList<int>? rows = null, IReadOnlyList<int>? columns = null,
        string[,]? keyMap = null, TimeProvider? timeProvider = null)
    {
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _rows = rows?.ToArray() ?? [21, 20, 16, 12];
        _columns = columns?.ToArray() ?? [26, 19, 13, 6];
        if (_rows.Length != 4 || _columns.Length != 4 ||
            _rows.Concat(_columns).Any(pin => pin < 0) || _rows.Concat(_columns).Distinct().Count() != 8)
            throw new ArgumentException("Supply four distinct row and four distinct column pins.");
        _map = keyMap is null ? new[,] { { "1", "2", "3", "A" }, { "4", "5", "6", "B" },
            { "7", "8", "9", "C" }, { "*", "0", "#", "D" } } : (string[,])keyMap.Clone();
        if (_map.GetLength(0) != 4 || _map.GetLength(1) != 4) throw new ArgumentException("A key map must contain four rows and columns.", nameof(keyMap));
        foreach (var key in _map)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            _states[key] = false;
        }
        _clock = timeProvider ?? TimeProvider.System;
    }

    public event Action<string, KeyTransition>? KeyChanged;
    public Action<string, Exception?>? Error { get; set; }

    public bool IsDown(string key) { lock (_gate) return _states.GetValueOrDefault(key); }
    public bool IsUp(string key) { lock (_gate) return _states.TryGetValue(key, out var down) && !down; }

    public void SetKeyMap(string[,] map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.GetLength(0) != 4 || map.GetLength(1) != 4) throw new ArgumentException("A key map must contain four rows and columns.", nameof(map));
        var replacement = (string[,])map.Clone();
        foreach (var key in replacement) ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Array.Copy(replacement, _map, replacement.Length);
            _states.Clear();
            foreach (var key in _map) _states[key] = false;
            if (_hook is not null) _states["hook"] = false;
        }
    }

    public void EnableHook(int pin = 5)
    {
        if (pin < 0 || _rows.Contains(pin) || _columns.Contains(pin)) throw new ArgumentOutOfRangeException(nameof(pin));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pins.Configure(pin, PinDirection.Input, PinPull.Up);
            _hook = pin;
            _states["hook"] = false;
        }
    }

    public void Scan(TimeSpan elapsed)
    {
        var changes = new List<(string Key, KeyTransition Transition)>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (elapsed < _lastScan) throw new ArgumentOutOfRangeException(nameof(elapsed));
            _lastScan = elapsed;
            var active = new List<string>(5);
            for (var row = 0; row < 4; row++)
            {
                var pin = _rows[row];
                _pins.Configure(pin, PinDirection.Output);
                try
                {
                    _pins.Write(pin, true);
                    for (var column = 0; column < 4; column++)
                        if (_pins.Read(_columns[column])) active.Add(_map[row, column]);
                }
                finally
                {
                    _pins.Write(pin, false);
                    _pins.Configure(pin, PinDirection.Input);
                }
            }
            if (_hook is { } hook)
            {
                var onHook = !_pins.Read(hook);
                if (onHook) { active.Add("hook"); _hookTime = elapsed; }
                else if (_states["hook"])
                {
                    if (elapsed - _hookTime < TimeSpan.FromMilliseconds(150)) active.Add("hook");
                    else _hookTime = elapsed;
                }
            }
            if (active.Count > 4) return;
            foreach (var key in _states.Keys)
            {
                var down = active.Contains(key);
                if (_states[key] == down) continue;
                _states[key] = down;
                changes.Add((key, down ? KeyTransition.Down : KeyTransition.Up));
            }
        }
        foreach (var change in changes)
            if (KeyChanged is { } observers)
                foreach (Action<string, KeyTransition> observer in observers.GetInvocationList())
                    try { observer(change.Key, change.Transition); }
                    catch (Exception ex) { Report("Keypad observer failed.", ex); }
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_worker.IsCompleted) throw new InvalidOperationException("The keypad scanner is already running.");
            foreach (var row in _rows) _pins.Configure(row, PinDirection.Output);
            foreach (var column in _columns) _pins.Configure(column, PinDirection.Input, PinPull.Down);
            _stop?.Dispose();
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _stop.Token;
            var started = _clock.GetTimestamp();
            var offset = _lastScan;
            _worker = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        try { Scan(offset + _clock.GetElapsedTime(started)); }
                        catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
                        catch (Exception ex) when (ex is not OperationCanceledException) { Report("Keypad scan failed.", ex); }
                        await Task.Delay(TimeSpan.FromMilliseconds(20), _clock, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            });
        }
    }

    private void Report(string context, Exception exception)
    {
        try
        {
            if (Error is { } observer) observer(context, exception);
            else System.Diagnostics.Trace.TraceError($"{context} {exception}");
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"{context} {exception}; error observer: {ex}"); }
    }

    public async ValueTask StopAsync()
    {
        Task worker;
        lock (_gate) { _stop?.Cancel(); worker = _worker; }
        await worker.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _stop?.Cancel();
            _disposed = true;
            return new(_disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _worker.ConfigureAwait(false);
        lock (_gate) { _stop?.Dispose(); _stop = null; }
    }
}
