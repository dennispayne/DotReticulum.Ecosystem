namespace LXST.Tests;

[Trait("Category", "Unit")]
public class PhoneCommandTests
{
    // Executed primary LXST 0.5.4 run/main_menu, with disclosed device/input substitutes.
    [Fact]
    public void TerminalStatePrecedenceMatchesActualReference()
    {
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-terminal.json")));
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var state = row.GetProperty("state").GetString();
            var command = row.GetProperty("command").GetString()!;
            var action = state == "available" ? Rnphone.TerminalCommands.IdleAction(command) :
                Rnphone.TerminalCommands.CallAction(state switch
                {
                    "ringing" => CallState.Ringing,
                    "connecting" => CallState.Connecting,
                    _ => CallState.Established,
                }, true, command);
            Assert.Equal(row.GetProperty("actions").EnumerateArray().Select(value => value.GetString()),
                action is null ? [] : new[] { action });
        }
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--config")]
    [InlineData("--rnsconfig")]
    public async Task InvalidArgumentsReportUsageErrorBeforeOpeningAudioOrNetwork(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var input = new StringReader(string.Empty);

        var result = await Rnphone.PhoneApp.RunAsync(args, input, output, error, CancellationToken.None);

        Assert.Equal(2, result);
        Assert.StartsWith("rnphone: ", error.ToString());
        Assert.NotEmpty(error.ToString().Trim());
        Assert.Empty(output.ToString());
    }
}
