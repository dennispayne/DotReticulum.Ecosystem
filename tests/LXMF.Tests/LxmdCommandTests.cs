namespace LXMF.Tests;

[Trait("Category", "Unit")]
public class LxmdCommandTests
{
    [Theory]
    [InlineData("--unknown")]
    [InlineData("--config")]
    [InlineData("--timeout", "not-a-number")]
    public async Task InvalidArgumentsReportUsageError(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var result = await Lxmd.LxmdApp.RunAsync(args, output, error, CancellationToken.None);

        Assert.Equal(2, result);
        Assert.StartsWith("lxmd: ", error.ToString());
        Assert.NotEmpty(error.ToString().Trim());
        Assert.Empty(output.ToString());
    }
}
