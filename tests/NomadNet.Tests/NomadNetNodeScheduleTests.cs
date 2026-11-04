namespace NomadNet.Tests;

// Node.__jobs at primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2 uses strict
// elapsed-minute comparisons, independently disabled refreshes and five-second polling.
[Trait("Category", "Unit")]
public sealed class NomadNetNodeScheduleTests
{
    [Fact]
    public void RefreshesAndAnnouncesHaveIndependentStrictMinuteDeadlines()
    {
        var start = DateTimeOffset.UnixEpoch;
        var schedule = new NomadNetNodeSchedule(start, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
        Assert.Equal(default, schedule.Due(start + TimeSpan.FromMinutes(1)));
        Assert.Equal(new(false, true, false), schedule.Due(start + TimeSpan.FromSeconds(61)));
        schedule.Completed(start + TimeSpan.FromSeconds(61), new(false, true, false));
        Assert.Equal(new(false, false, true), schedule.Due(start + TimeSpan.FromSeconds(121)));
        Assert.Equal(new(true, true, true), schedule.Due(start + TimeSpan.FromSeconds(181)));
        schedule.Completed(start + TimeSpan.FromSeconds(181), new(true, false, false));
        Assert.Equal(new(false, true, true), schedule.Due(start + TimeSpan.FromSeconds(182)));
    }

    [Fact]
    public void ZeroDisablesAutomaticRefreshAndFailureDoesNotAcknowledgeADeadline()
    {
        var start = DateTimeOffset.UnixEpoch;
        var schedule = new NomadNetNodeSchedule(start, TimeSpan.FromMinutes(1), TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(new(true, false, false), schedule.Due(start + TimeSpan.FromHours(5)));
        Assert.Equal(new(true, false, false), schedule.Due(start + TimeSpan.FromHours(6)));
        schedule.Completed(start + TimeSpan.FromHours(6), new(true, false, false));
        Assert.Equal(default, schedule.Due(start + TimeSpan.FromHours(6)));
        Assert.Equal(default, schedule.Due(start - TimeSpan.FromDays(1)));
    }

    [Fact]
    public void InvalidIntervalsFailInsteadOfSchedulingAnUnboundedLoop()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NomadNetNodeSchedule(DateTimeOffset.UnixEpoch,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NomadNetNodeSchedule(DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(1), TimeSpan.FromTicks(-1), TimeSpan.Zero));
    }
}
