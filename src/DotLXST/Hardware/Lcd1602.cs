using System.Text;

namespace DotLXST.Hardware;

public interface IByteBus : IDisposable
{
    void WriteByte(byte address, byte value);
}

/// <summary>LCD1602 four-bit I2C expander commands; no platform bus implementation is assumed.</summary>
public sealed class Lcd1602 : IDisposable
{
    private readonly object _gate = new();
    private readonly IByteBus _bus;
    private readonly byte _address;
    private readonly bool _ownsBus;
    private readonly Action<TimeSpan> _delay;
    private bool _initialized;
    private bool _sleeping;
    private bool _disposed;

    public Lcd1602(IByteBus bus, byte address = 0x27, bool ownsBus = false, Action<TimeSpan>? delay = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        if (address > 0x7f) throw new ArgumentOutOfRangeException(nameof(address));
        _address = address;
        _ownsBus = ownsBus;
        _delay = delay ?? Thread.Sleep;
    }

    public bool IsSleeping { get { lock (_gate) return _sleeping; } }

    public void Initialize()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized) return;
            InitializeCore();
            _initialized = true;
        }
    }

    private void InitializeCore()
    {
        foreach (var command in new byte[] { 0x33, 0x32, 0x28, 0x0c, 0x01 }) Send(command, data: false);
        _delay(TimeSpan.FromMilliseconds(.5));
    }

    private void Send(int value, bool data)
    {
        var flags = data ? 1 : 0;
        var light = _sleeping ? 0 : 8;
        foreach (var nibble in new[] { value & 0xf0, (value & 0x0f) << 4 })
        {
            _bus.WriteByte(_address, (byte)(nibble | flags | light | 4));
            _delay(TimeSpan.FromMilliseconds(.5));
            _bus.WriteByte(_address, (byte)(nibble | flags | light));
        }
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Initialize the display before issuing commands.");
    }

    public void WriteText(string text, int x = 0, int y = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            EnsureReady();
            if (_sleeping) Wake();
            Send(0x80 + 0x40 * Math.Clamp(y, 0, 1) + Math.Clamp(x, 0, 15), data: false);
            var characters = text.EnumerateRunes().Take(16).Select(rune => rune.Value).ToArray();
            for (var index = 0; index < 16; index++) Send(index < characters.Length ? characters[index] : ' ', data: true);
        }
    }

    public void Clear()
    {
        lock (_gate) { WriteText("", y: 0); WriteText("", y: 1); }
    }

    public void Sleep()
    {
        lock (_gate)
        {
            EnsureReady();
            _sleeping = true;
            Send(1, data: false);
        }
    }

    public void Wake()
    {
        lock (_gate) { EnsureReady(); _sleeping = false; InitializeCore(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { if (_initialized) Sleep(); }
            finally { _disposed = true; if (_ownsBus) _bus.Dispose(); }
        }
    }
}
