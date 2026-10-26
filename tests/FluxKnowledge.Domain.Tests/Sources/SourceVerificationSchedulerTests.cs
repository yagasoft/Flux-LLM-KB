using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceVerificationSchedulerTests
{
    [Fact]
    public void Defaults_use_the_explicit_installation_timezone_and_combined_read_budget()
    {
        var policy = SourceVerificationPolicy.Default("UTC");
        Assert.Equal(DayOfWeek.Sunday, policy.Weekday);
        Assert.Equal(new TimeOnly(2, 0), policy.LocalStart);
        Assert.Equal(TimeSpan.FromHours(6), policy.WindowDuration);
        Assert.Equal(2 * 1024 * 1024, policy.MaximumBytesPerSecond);
        Assert.Equal("UTC", policy.TimeZoneId);
        policy.Validate();
    }

    [Fact]
    public void Weekly_cycle_starts_once_and_an_active_cycle_never_overlaps_a_new_week()
    {
        var policy = SourceVerificationPolicy.Default("UTC");
        var sunday = DateTimeOffset.Parse("2026-10-25T02:00:00Z");
        Assert.Null(SourceVerificationSchedule.DueCycle(policy, sunday.AddTicks(-1), null, false, TimeZoneInfo.Utc));
        Assert.Equal(sunday, SourceVerificationSchedule.DueCycle(policy, sunday, null, false, TimeZoneInfo.Utc));
        Assert.Null(SourceVerificationSchedule.DueCycle(policy, sunday.AddMinutes(1), sunday, false, TimeZoneInfo.Utc));
        Assert.Null(SourceVerificationSchedule.DueCycle(policy, sunday.AddDays(7), sunday, true, TimeZoneInfo.Utc));
        Assert.Equal(sunday.AddDays(7), SourceVerificationSchedule.DueCycle(policy, sunday.AddDays(7), sunday, false, TimeZoneInfo.Utc));
        Assert.Null(SourceVerificationSchedule.DueCycle(policy with { Enabled = false }, sunday, null, false, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Unfinished_cycle_has_daily_continuation_windows_and_exact_end_is_closed()
    {
        var policy = SourceVerificationPolicy.Default("UTC");
        var monday = DateTimeOffset.Parse("2026-10-26T03:00:00Z");
        var open = SourceVerificationSchedule.CurrentOrNextWindow(policy, monday, TimeZoneInfo.Utc);
        Assert.True(open.Contains(monday));
        Assert.Equal(monday.Date.AddHours(2), open.Start.UtcDateTime);
        Assert.Equal(monday.Date.AddHours(8), open.End.UtcDateTime);
        var closed = SourceVerificationSchedule.CurrentOrNextWindow(policy, open.End, TimeZoneInfo.Utc);
        Assert.False(closed.Contains(open.End));
        Assert.Equal(open.Start.AddDays(1), closed.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T02:00:00Z"),
            SourceVerificationSchedule.DueCycle(policy, monday, null, false, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Overnight_continuation_uses_the_previous_local_dates_window()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("verification-plus-two", TimeSpan.FromHours(2), "test", "test");
        var policy = SourceVerificationPolicy.Default(zone.Id) with { LocalStart = new(23, 0), WindowDuration = TimeSpan.FromHours(4) };
        var now = DateTimeOffset.Parse("2026-10-26T00:00:00Z"); // Local02:00, still in Sunday's window.
        var window = SourceVerificationSchedule.CurrentOrNextWindow(policy, now, zone);
        Assert.True(window.Contains(now));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T21:00:00Z"), window.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T01:00:00Z"), window.End);
    }

    [Fact]
    public void Invalid_DST_start_advances_to_the_next_valid_instant()
    {
        var zone = DstZone();
        var policy = SourceVerificationPolicy.Default(zone.Id) with { LocalStart = new(2, 30) };
        var now = DateTimeOffset.Parse("2026-03-08T06:50:00Z");
        var window = SourceVerificationSchedule.CurrentOrNextWindow(policy, now, zone);
        Assert.Equal(DateTimeOffset.Parse("2026-03-08T07:00:00Z"), window.Start); //03:00 local.
        Assert.Equal(TimeSpan.FromHours(6), window.End - window.Start);
        Assert.Null(SourceVerificationSchedule.DueCycle(policy, now, null, false, zone));
        Assert.Equal(window.Start, SourceVerificationSchedule.DueCycle(policy, window.Start, null, false, zone));
    }

    [Fact]
    public void Ambiguous_DST_start_selects_the_first_occurrence_and_cannot_create_a_second_cycle()
    {
        var zone = DstZone();
        var policy = SourceVerificationPolicy.Default(zone.Id) with { LocalStart = new(1, 30) };
        var first = DateTimeOffset.Parse("2026-11-01T05:30:00Z");
        var second = first.AddHours(1);
        Assert.Equal(first, SourceVerificationSchedule.DueCycle(policy, first, null, false, zone));
        Assert.Equal(first, SourceVerificationSchedule.CurrentOrNextWindow(policy, second, zone).Start);
        Assert.Null(SourceVerificationSchedule.DueCycle(policy, second, first, false, zone));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(25, 1)]
    [InlineData(6, 0)]
    [InlineData(6, -1)]
    public void Invalid_window_or_read_rate_is_rejected(int hours, long rate)
    {
        var policy = SourceVerificationPolicy.Default("UTC") with { WindowDuration = TimeSpan.FromHours(hours), MaximumBytesPerSecond = rate };
        Assert.Throws<ArgumentOutOfRangeException>(policy.Validate);
    }

    private static TimeZoneInfo DstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("verification-dst", TimeSpan.FromHours(-5), "test", "standard", "daylight", [rule]);
    }
}
