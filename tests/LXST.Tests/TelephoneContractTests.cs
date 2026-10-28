using System.Text.Json;
using DotReticulum.Core;

namespace LXST.Tests;

/// <summary>Executed primary 0.4.5 Telephone contracts; source digest is stored with the fixture.</summary>
[Trait("Category", "Unit")]
public sealed class TelephoneContractTests
{
    [Fact]
    public void CallerPoliciesMatchActualReferenceIncludingBlockedPrecedence()
    {
        using var fixture = LoadVectors();
        using var caller = Identity.Generate();
        using var other = Identity.Generate();
        foreach (var vector in fixture.RootElement.GetProperty("policies").EnumerateArray())
        {
            var mode = vector.GetProperty("allowed").GetString();
            ReadOnlyMemory<byte>[]? allowed = mode switch
            {
                "none" => [],
                "list-match" => [caller.Hash.ToArray()],
                "list-other" => [other.Hash.ToArray()],
                _ => null,
            };
            Func<Identity, bool>? authorize = mode switch
            {
                "callback-match" => identity => identity.Hash.SequenceEqual(caller.Hash),
                "callback-other" => identity => identity.Hash.SequenceEqual(other.Hash),
                _ => null,
            };
            var blocked = vector.GetProperty("blocked").GetBoolean();
            var policy = new TelephoneCallerPolicy(allowed, blocked ? [caller.Hash.ToArray()] : null, authorize);
            Assert.Equal(vector.GetProperty("result").GetBoolean(), policy.IsAllowed(caller));
        }
        var mutableHash = caller.Hash.ToArray();
        var snapshot = new TelephoneCallerPolicy([mutableHash]);
        mutableHash.AsSpan().Clear();
        Assert.True(snapshot.IsAllowed(caller));
        Assert.Throws<ArgumentException>(() => new TelephoneCallerPolicy([new byte[15]]));
    }

    [Fact]
    public void ProfileOrderingLabelsDurationsAndWrapMatchExecutedReference()
    {
        using var fixture = LoadVectors();
        var expected = fixture.RootElement.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, TelephoneProfiles.Available.Count);
        foreach (var vector in expected)
        {
            var profile = (TelephoneProfile)vector.GetProperty("profile").GetInt32();
            var actual = TelephoneProfiles.Get(profile);
            Assert.Equal(vector.GetProperty("name").GetString(), actual.Name);
            Assert.Equal(vector.GetProperty("abbreviation").GetString(), actual.Abbreviation);
            Assert.Equal(vector.GetProperty("frame_ms").GetInt32(), actual.FrameMilliseconds);
            Assert.Equal(profile, TelephoneProfiles.Available[vector.GetProperty("index").GetInt32()].Profile);
            Assert.Equal((TelephoneProfile)vector.GetProperty("next").GetInt32(), TelephoneProfiles.Next(profile));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => TelephoneProfiles.Next((TelephoneProfile)0));
    }

    private static JsonDocument LoadVectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-telephone.json")));
}
