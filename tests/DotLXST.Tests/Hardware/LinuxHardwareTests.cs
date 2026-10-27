using System.Buffers.Binary;
using System.Text.Json;
using DotLXST.Hardware;

namespace DotLXST.Tests.Hardware;

[Trait("Category", "Unit")]
public sealed class LinuxHardwareTests
{
    private sealed class Io : ILinuxHardwareIo
    {
        public readonly List<(int Descriptor, uint Request, byte[] Data)> Calls = [];
        public readonly List<int> Closed = [];
        public readonly List<byte> Written = [];
        public bool Fail;
        public Action? BeforeControl;
        public int Open(string path) => 7;
        public void Close(int descriptor) => Closed.Add(descriptor);
        public void Control(int descriptor, uint request, Span<byte> data)
        {
            Calls.Add((descriptor, request, data.ToArray()));
            BeforeControl?.Invoke();
            if (Fail) throw new IOException("injected hardware failure");
            if (request == 0xc250b407) BinaryPrimitives.WriteInt32LittleEndian(data[588..], 19);
            if (request == 0xc010b40e) BinaryPrimitives.WriteUInt64LittleEndian(data, 1);
        }
        public void ControlValue(int descriptor, uint request, nuint value)
        {
            if (Fail) throw new IOException("injected hardware failure");
            Calls.Add((descriptor, request, BitConverter.GetBytes((ulong)value)));
        }
        public void Write(int descriptor, ReadOnlySpan<byte> data) => Written.AddRange(data.ToArray());
    }

    [Fact]
    public void GpioV2RequestRetainsLeaseAcrossReconfigurationAndReleasesEveryDescriptor()
    {
        var io = new Io();
        using (var pins = new LinuxDigitalPins(io: io))
        {
            using var abi = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "linux-hardware-abi.json")));
            var observed = abi.RootElement;
            pins.Configure(21, PinDirection.Output);
            var request = io.Calls.Single().Data;
            Assert.Equal(observed.GetProperty("request_size").GetInt32(), request.Length);
            Assert.Equal(observed.GetProperty("request_ioctl").GetUInt32(), io.Calls.Single().Request);
            Assert.Equal(21, BinaryPrimitives.ReadInt32LittleEndian(request));
            Assert.Equal(1UL << 3 | 1UL << 10, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(observed.GetProperty("config_offset").GetInt32())));
            Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(observed.GetProperty("count_offset").GetInt32())));
            Assert.All(request[296..560], value => Assert.Equal(0, value));
            pins.Write(21, true);
            Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(io.Calls[^1].Data));
            pins.Configure(21, PinDirection.Input, PinPull.Up);
            Assert.Equal(observed.GetProperty("config_ioctl").GetUInt32(), io.Calls[^1].Request);
            Assert.Equal(1UL << 2 | 1UL << 8, BinaryPrimitives.ReadUInt64LittleEndian(io.Calls[^1].Data));
            Assert.True(pins.Read(21));
            Assert.Throws<InvalidOperationException>(() => pins.Write(21, false));
            Assert.Throws<InvalidOperationException>(() => pins.Read(22));
        }
        Assert.Equal([19, 7], io.Closed);
    }

    [Fact]
    public void FailedGpioRequestDoesNotCreateALeaseAndLaterRequestCanRecover()
    {
        var io = new Io { Fail = true };
        var pins = new LinuxDigitalPins(io: io);
        Assert.Throws<IOException>(() => pins.Configure(5, PinDirection.Input, PinPull.Up));
        io.Fail = false;
        pins.Configure(5, PinDirection.Input, PinPull.Up);
        pins.Dispose();
        pins.Dispose();
        Assert.Equal([19, 7], io.Closed);
        Assert.Throws<ObjectDisposedException>(() => pins.Read(5));
    }

    [Fact]
    public void I2cAddressSelectionIsNonForcingCachedAndFailedSelectionDoesNotWrite()
    {
        var io = new Io { Fail = true };
        var bus = new LinuxI2cByteBus(io: io);
        Assert.Throws<IOException>(() => bus.WriteByte(0x27, 8));
        Assert.Empty(io.Written);
        io.Fail = false;
        bus.WriteByte(0x27, 8);
        bus.WriteByte(0x27, 12);
        bus.WriteByte(0x28, 4);
        Assert.Equal([8, 12, 4], io.Written);
        Assert.All(io.Calls, call => Assert.Equal((uint)0x0703, call.Request));
        Assert.Equal(2, io.Calls.Count);
        bus.Dispose();
        bus.Dispose();
        Assert.Equal([7], io.Closed);
        Assert.Throws<ObjectDisposedException>(() => bus.WriteByte(0x27, 1));
    }

    [Fact]
    public async Task DisposalCannotReleaseALeaseWhileItsReadIsInFlight()
    {
        var io = new Io();
        var pins = new LinuxDigitalPins(io: io);
        pins.Configure(5, PinDirection.Input);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.BeforeControl = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        var read = Task.Run(() => pins.Read(5));
        await entered.Task;
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposal = Task.Run(() => { disposing.TrySetResult(); pins.Dispose(); });
        await disposing.Task;
        Assert.False(disposal.IsCompleted);
        Assert.Empty(io.Closed);
        release.TrySetResult();
        Assert.True(await read);
        await disposal;
        Assert.Equal([19, 7], io.Closed);
    }
}
