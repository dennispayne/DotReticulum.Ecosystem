using SharedPrinting;

namespace NomadNet.Tests;

[Trait("Category", "Unit")]
public sealed class NomadNetWindowsAuthorizationEnvironmentTests
{
    [Fact, Trait("TestId", "T1066")]
    public void CustomWindowsEnvironmentIsValidatedSortedUnicodeAndDoubleTerminated()
    {
        Assert.Null(WindowsPrintProcess.EnvironmentBlock(null));
        Assert.Equal("\0\0", WindowsPrintProcess.EnvironmentBlock(new Dictionary<string, string?>()));
        Assert.Equal("Alpha=Å 🧭\0zeta=last\0\0", WindowsPrintProcess.EnvironmentBlock(new Dictionary<string, string?>
        {
            ["zeta"] = "last", ["Alpha"] = "Å 🧭",
        }));
        foreach (var values in new Dictionary<string, string?>[]
        {
            new() { [""] = "value" }, new() { ["bad=name"] = "value" }, new() { ["bad\0name"] = "value" },
            new() { ["name"] = "bad\0value" }, new() { ["name"] = null },
            new() { ["name"] = "one", ["NAME"] = "two" },
            new() { [new string('n', 32768)] = "value" }, new() { ["name"] = new string('v', 32768) },
        })
            Assert.ThrowsAny<ArgumentException>(() => WindowsPrintProcess.EnvironmentBlock(values));
        var oversized = Enumerable.Range(0, 100).ToDictionary(index => "field_" + index, _ => (string?)new string('x', 32767));
        Assert.ThrowsAny<ArgumentException>(() => WindowsPrintProcess.EnvironmentBlock(oversized));
    }
}
