using System.Text.Json;
using DotLXST.Hardware;

namespace DotLXST.Tests;

[Trait("Category", "Unit")]
public sealed class HardwareTests
{
    private sealed class Pins : IDigitalPins
    {
        public readonly List<object?[]> Operations = [];
        public readonly HashSet<(int Row, int Column)> Active = [];
        private readonly HashSet<int> _driven = [];
        public bool Hook = true;
        public void Configure(int pin, PinDirection direction, PinPull pull = PinPull.Disabled) =>
            Operations.Add(["setup", pin, direction == PinDirection.Input ? "in" : "out",
                pull == PinPull.Up ? "up" : pull == PinPull.Down ? "down" : direction == PinDirection.Input ? "off" : null]);
        public void Write(int pin, bool high)
        {
            Operations.Add(["write", pin, high ? 1 : 0]);
            if (high) _driven.Add(pin);
            else _driven.Remove(pin);
        }

        [Trait("Category", "Integration")]
        public sealed class MatrixKeypadLifecycleTests
        {
            private sealed class RecoveringPins : IDigitalPins
            {
                private int _reads;
                public TaskCompletionSource Scanned = new(TaskCreationOptions.RunContinuationsAsynchronously);
                public void Configure(int pin, PinDirection direction, PinPull pull = PinPull.Disabled) { }
                public void Write(int pin, bool high) { }
                public bool Read(int pin)
                {
                    var reads = Interlocked.Increment(ref _reads);
                    if (reads == 1) throw new IOException("injected bus failure");
                    if (reads > 17) Scanned.TrySetResult();
                    return false;
                }
            }

            [Fact]
            public async Task ScannerRecoversAfterBusAndErrorObserversFailAndEveryDisposerWaits()
            {
                var pins = new RecoveringPins();
                await using var keypad = new MatrixKeypad(pins);
                var failures = 0;
                keypad.Error = (_, _) => { Interlocked.Increment(ref failures); throw new InvalidOperationException("injected logger failure"); };
                keypad.Start();
                Assert.Throws<InvalidOperationException>(() => keypad.Start());
                await pins.Scanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, failures);
                await keypad.StopAsync();
                pins.Scanned = new(TaskCreationOptions.RunContinuationsAsynchronously);
                keypad.Start();
                await pins.Scanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(keypad.DisposeAsync().AsTask(), keypad.DisposeAsync().AsTask());
                Assert.Throws<ObjectDisposedException>(() => keypad.Start());
            }
        }
        public bool Read(int pin) => pin == 5 ? Hook : _driven.Any(row => Active.Contains((row, pin)));
    }

    private sealed class Bus : IByteBus
    {
        public readonly List<object[]> Writes = [];
        public void WriteByte(byte address, byte value) => Writes.Add([address, value]);
        public void Dispose() => Writes.Add(["close"]);
    }

    [Fact]
    public async Task KeyTransitionsHookDebounceAndRolloverMatchActualPrimaryWithoutHardware()
    {
        using var fixture = Load();
        var pins = new Pins();
        await using var keypad = new MatrixKeypad(pins);
        keypad.EnableHook();
        var events = new List<object[]>();
        var errors = 0;
        keypad.Error = (_, _) => errors++;
        keypad.KeyChanged += (_, _) => throw new InvalidOperationException("test observer");
        keypad.KeyChanged += (key, transition) => events.Add([key, (int)transition]);
        foreach (var vector in fixture.RootElement.GetProperty("keypad").EnumerateArray())
        {
            pins.Active.Clear();
            foreach (var pair in vector.GetProperty("active").EnumerateArray()) pins.Active.Add((pair[0].GetInt32(), pair[1].GetInt32()));
            pins.Hook = vector.GetProperty("hook").GetInt32() != 0;
            pins.Operations.Clear();
            events.Clear();
            keypad.Scan(TimeSpan.FromSeconds(vector.GetProperty("elapsed").GetDouble()));
            Assert.Equal(JsonSerializer.Serialize(vector.GetProperty("events")), JsonSerializer.Serialize(events));
            Assert.Equal(JsonSerializer.Serialize(vector.GetProperty("operations")), JsonSerializer.Serialize(pins.Operations));
            foreach (var key in vector.GetProperty("down").EnumerateArray()) Assert.True(keypad.IsDown(key.GetString()!));
            Assert.False(keypad.IsDown("unknown"));
            Assert.False(keypad.IsUp("unknown"));
        }
        Assert.True(errors > 0);
        await Task.WhenAll(keypad.DisposeAsync().AsTask(), keypad.DisposeAsync().AsTask());
        Assert.Throws<ObjectDisposedException>(() => keypad.Scan(TimeSpan.FromSeconds(1)));
        var unsubscribedPins = new Pins();
        await using var unsubscribed = new MatrixKeypad(unsubscribedPins);
        unsubscribedPins.Active.Add((21, 26));
        unsubscribed.Scan(TimeSpan.Zero);
        Assert.True(unsubscribed.IsDown("1"));
    }

    [Fact]
    public void LcdCommandsTextClampingBacklightAndBusOwnershipMatchActualPrimary()
    {
        using var fixture = Load();
        var bus = new Bus();
        var delays = new List<double>();
        using var lcd = new Lcd1602(bus, ownsBus: true, delay: duration => delays.Add(duration.TotalSeconds));
        foreach (var vector in fixture.RootElement.GetProperty("lcd").EnumerateArray())
        {
            bus.Writes.Clear();
            delays.Clear();
            switch (vector.GetProperty("action").GetString())
            {
                case "init": lcd.Initialize(); break;
                case "print": lcd.WriteText(vector.GetProperty("text").GetString()!, vector.GetProperty("x").GetInt32(), vector.GetProperty("y").GetInt32()); break;
                case "sleep": lcd.Sleep(); break;
                case "clear": lcd.Clear(); break;
                case "close": lcd.Dispose(); break;
            }
            Assert.Equal(JsonSerializer.Serialize(vector.GetProperty("writes")), JsonSerializer.Serialize(bus.Writes));
            Assert.Equal(vector.GetProperty("delays").EnumerateArray().Select(value => value.GetDouble()), delays);
            Assert.Equal(vector.GetProperty("sleeping").GetBoolean(), lcd.IsSleeping);
        }
        Assert.Throws<ObjectDisposedException>(() => lcd.WriteText("closed"));
        var external = new Bus();
        using (var display = new Lcd1602(external, delay: _ => { }))
        {
            Assert.Throws<InvalidOperationException>(() => display.Clear());
            display.Initialize();
        }
        Assert.DoesNotContain(external.Writes, value => value[0] is string);
    }

    private static JsonDocument Load() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-hardware.json")));
}
