using DotLXST.Hardware;

namespace DotLXST.Tests.Hardware;

[Trait("Category", "Integration")]
public sealed class LinuxHardwareIntegrationTests
{
    [Fact]
    public void ActualProviderRejectsUnsupportedPlatformsOrMissingDevicesWithContext()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxDigitalPins());
            Assert.Throws<PlatformNotSupportedException>(() => new LinuxI2cByteBus());
        }
        else
        {
            var gpio = Assert.Throws<IOException>(() => new LinuxDigitalPins("/dev/dotlxst-device-does-not-exist"));
            Assert.Contains("Opening hardware device", gpio.Message);
            var i2c = Assert.Throws<IOException>(() => new LinuxI2cByteBus("/dev/dotlxst-device-does-not-exist"));
            Assert.Contains("errno", i2c.Message);
            using var pins = new LinuxDigitalPins("/dev/null");
            Assert.Throws<IOException>(() => pins.Configure(5, PinDirection.Input));
            using var bus = new LinuxI2cByteBus("/dev/null");
            Assert.Throws<IOException>(() => bus.WriteByte(0x27, 8));
        }
    }
}
