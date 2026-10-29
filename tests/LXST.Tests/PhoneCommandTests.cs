namespace LXST.Tests;

[Trait("Category", "Unit")]
public class PhoneCommandTests
{
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
