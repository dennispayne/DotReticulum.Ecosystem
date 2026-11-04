using System.Text.Json;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandConsoleSyntaxTests
{
    // Executed primary d069122 registry/parser/conversions; generator records every source SHA-256.
    [Fact]
    public void EveryPrimaryRegistrationHasExactPathSourceBoundsAndNoInventedAliases()
    {
        using var fixture = Fixture();
        var commands = fixture.RootElement.GetProperty("commands").EnumerateArray().ToArray();
        Assert.Equal(56, commands.Length);
        foreach (var expected in commands)
        {
            var actual = Assert.Single(SidebandConsoleSyntax.Commands, c => c.Name == expected.GetProperty("command").GetString());
            Assert.Equal(expected.GetProperty("min").GetInt32(), actual.MinimumArguments);
            Assert.Equal(expected.GetProperty("max").ValueKind == JsonValueKind.Null ? null : expected.GetProperty("max").GetInt32(), actual.MaximumArguments);
            Assert.Equal(expected.GetProperty("source").GetString(), actual.Source);
            Assert.Empty(expected.GetProperty("aliases").EnumerateArray());
            if (!expected.GetProperty("handler").GetBoolean())
            {
                Assert.Equal("Not yet implemented", expected.GetProperty("result").GetString());
                Assert.Equal("native-extension", actual.Status);
            }
        }
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.RootElement.GetProperty("sideband_sha").GetString());
        Assert.Equal(166, fixture.RootElement.GetProperty("configuration").GetArrayLength());
        Assert.Equal(9, fixture.RootElement.GetProperty("source_sha256").EnumerateObject().Count());
        foreach (var name in new[] { "raw", "log", "plugins", "plugins reload" })
            Assert.Equal("unsupported", Assert.Single(SidebandConsoleSyntax.Commands, c => c.Name == name).Status);
        foreach (var name in new[] { "log stream", "config list", "config get", "config set", "telemetry sensors" })
            Assert.Equal("partial", Assert.Single(SidebandConsoleSyntax.Commands, c => c.Name == name).Status);
    }

    [Fact]
    public void ParserMatchesExecutedShlexAndLongestPrefixExceptExplicitStrictMalformedInput()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("parsing").EnumerateArray())
        {
            var line = item.GetProperty("line").GetString()!;
            if (line.Contains("unclosed", StringComparison.Ordinal) || line.EndsWith('\\'))
            {
                Assert.Throws<ArgumentException>(() => SidebandConsoleSyntax.Parse(line));
                continue;
            }
            var tokens = SidebandConsoleSyntax.Parse(line);
            Assert.Equal(item.GetProperty("tokens").EnumerateArray().Select(v => v.GetString()).ToArray(), tokens);
            var expected = item.GetProperty("command").GetString();
            if (expected is null && tokens.Length > 0) Assert.Throws<ArgumentException>(() => SidebandConsoleSyntax.Resolve(tokens));
            else
            {
                var invocation = SidebandConsoleSyntax.Resolve(tokens);
                Assert.Equal(expected, invocation?.Command.Name);
                if (invocation is not null)
                    Assert.Equal(item.GetProperty("remaining").EnumerateArray().Select(v => v.GetString()).ToArray(), invocation.Arguments);
            }
        }
    }

    [Theory]
    [InlineData("conv send", false)]
    [InlineData("status extra", false)]
    [InlineData("help a b c d e f g h i j k", false)]
    [InlineData("voice dial id profile extra", false)]
    [InlineData("conv sync status", true)]
    [InlineData("conv create 0123456789abcdef0123456789abcdef ''", true)]
    [InlineData("conv send name \"Unicode Ω 雪 ; $(not-a-shell)\"", true)]
    public void ExactArgumentBoundsAndNonShellText(string line, bool valid)
    {
        if (valid) Assert.NotNull(SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(line)));
        else Assert.Throws<ArgumentException>(() => SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(line)));
    }

    [Fact]
    public void InputHasExplicitCharacterTokenEscapeAndControlBounds()
    {
        Assert.Single(SidebandConsoleSyntax.Parse(new string('x', 8192)));
        Assert.Throws<ArgumentException>(() => SidebandConsoleSyntax.Parse(new string('x', 8193)));
        Assert.Equal(256, SidebandConsoleSyntax.Parse(string.Join(' ', Enumerable.Repeat("x", 256))).Length);
        Assert.Throws<ArgumentException>(() => SidebandConsoleSyntax.Parse(string.Join(' ', Enumerable.Repeat("x", 257))));
        Assert.Throws<ArgumentException>(() => SidebandConsoleSyntax.Parse("conv send x '\0'"));
        Assert.Throws<ArgumentException>(() => SidebandConsoleSyntax.Parse("conv send x \\"));
        Assert.DoesNotContain('\u001b', SidebandConsoleSyntax.Safe("remote\u001b[2J"));
        Assert.DoesNotContain('\u202e', SidebandConsoleSyntax.Safe("remote\u202e"));
    }

    [Theory]
    [InlineData("enabled", true)]
    [InlineData("on", true)]
    [InlineData("disable", false)]
    [InlineData("0", false)]
    public void ReferenceBooleanSpellings(string value, bool expected) =>
        Assert.Equal(expected, SidebandConsoleConfiguration.Boolean(value));

    [Fact]
    public void ConversionsRejectInvalidOverflowAndDoNotTurnNullIntoZero()
    {
        Assert.Null(SidebandConsoleConfiguration.NullableInteger("none"));
        Assert.Null(SidebandConsoleConfiguration.NullableInteger("null"));
        Assert.Equal(-1, SidebandConsoleConfiguration.NullableInteger("-1"));
        Assert.Throws<ArgumentException>(() => SidebandConsoleConfiguration.NullableInteger("2147483648"));
        Assert.Throws<ArgumentException>(() => SidebandConsoleConfiguration.NullableInteger("true"));
        Assert.Throws<ArgumentException>(() => SidebandConsoleConfiguration.Boolean("invalid"));
    }

    [Fact]
    public void DestinationNamesAreCaseInsensitiveButAmbiguityRequiresExplicitHash()
    {
        const string first = "0123456789abcdef0123456789abcdef";
        const string second = "fedcba9876543210fedcba9876543210";
        var state = SidebandState.Empty("unused") with { Contacts = [new(first, "Ω Peer"), new(second, "Ω PEER")] };
        Assert.Throws<ArgumentException>(() => SidebandConsole.ResolveDestination(state, "Ω peer"));
        Assert.Equal(first, SidebandConsole.ResolveDestination(state, first.ToUpperInvariant()));
        Assert.Equal(first, SidebandConsole.ResolveDestination(state with { Contacts = [state.Contacts[0]] }, "ω pEeR"));
        Assert.Throws<ArgumentException>(() => SidebandConsole.ResolveDestination(state, "unknown"));
    }

    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-cli.json")));
}
