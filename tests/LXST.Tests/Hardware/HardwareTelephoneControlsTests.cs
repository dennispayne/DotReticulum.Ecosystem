using System.Text.Json;
using LXST.Hardware;

namespace LXST.Tests.Hardware;

[Trait("Category", "Unit")]
public sealed class HardwareTelephoneControlsTests
{
    [Fact]
    public void KeypadDialEditingAnswerRejectAndHookActionsMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-hardware-utility.json")));
        var controls = new HardwareTelephoneControls
        {
            Phonebook = [new("0123456789abcdef0123456789abcdef", "Alice", "12")],
        };
        foreach (var item in fixture.RootElement.GetProperty("events").EnumerateArray())
        {
            var state = item.GetProperty("state").GetString() switch
            {
                "ringing" => CallState.Ringing, "in-call" => CallState.Established,
                "connecting" => CallState.Connecting, _ => CallState.Available,
            };
            var command = controls.HandleKey(item.GetProperty("key").GetString()!,
                item.GetProperty("transition").GetString() == "down" ? KeyTransition.Down : KeyTransition.Up,
                TimeSpan.FromSeconds(item.GetProperty("elapsed").GetDouble()), state, incoming: state == CallState.Ringing);
            Assert.Equal(item.GetProperty("input").GetString(), controls.Input);
            Assert.Equal(item.GetProperty("dialing").GetBoolean(), controls.State == HardwareDialState.Dialing);
            Assert.Equal(item.GetProperty("lines")[0].GetString(), controls.FirstLine);
            Assert.Equal(item.GetProperty("lines")[1].GetString(), controls.SecondLine);
            var actions = item.GetProperty("actions").EnumerateArray().ToArray();
            if (actions.Length == 0) Assert.Null(command);
            else
            {
                Assert.NotNull(command);
                Assert.Equal(actions[0].GetProperty("kind").GetString(), command.Kind.ToString().ToLowerInvariant());
                if (actions[0].TryGetProperty("identity", out var identity)) Assert.Equal(identity.GetString(), command.Identity);
            }
        }
    }

    [Fact]
    public void IdleDisplaySleepsOnlyWhenAvailableAndAnyKeyWakesWithoutLosingItsAction()
    {
        var controls = new HardwareTelephoneControls();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-hardware-utility.json")));
        foreach (var item in fixture.RootElement.GetProperty("sleep").EnumerateArray())
        {
            controls.Ready(TimeSpan.Zero);
            Assert.Equal(item.GetProperty("sleeping").GetBoolean(),
                controls.Tick(TimeSpan.FromSeconds(item.GetProperty("elapsed").GetDouble()), CallState.Available, busy: false));
        }
        controls.Ready(TimeSpan.Zero);
        Assert.False(controls.Tick(TimeSpan.FromSeconds(15), CallState.Established, busy: false));
        Assert.False(controls.Tick(TimeSpan.FromSeconds(15), CallState.Available, busy: true));
        Assert.True(controls.Tick(TimeSpan.FromSeconds(15), CallState.Available, busy: false));
        Assert.Equal(HardwareDialState.Sleeping, controls.State);
        controls.HandleKey("1", KeyTransition.Down, TimeSpan.FromSeconds(16), CallState.Available);
        Assert.Equal(HardwareDialState.Dialing, controls.State);
        Assert.Equal("1", controls.FirstLine);
        Assert.Equal("Unknown", controls.SecondLine);
        Assert.False(controls.Tick(TimeSpan.FromSeconds(60), CallState.Available, busy: false));
    }
}
