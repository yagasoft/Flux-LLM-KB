namespace FluxKnowledge.Domain.Sources;

public sealed record SourceVerificationPolicy
{
    public int Version { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public string TimeZoneId { get; init; } = "UTC";
    public DayOfWeek Weekday { get; init; } = DayOfWeek.Sunday;
    public TimeOnly LocalStart { get; init; } = new(2, 0);
    public TimeSpan WindowDuration { get; init; } = TimeSpan.FromHours(6);
    public long MaximumBytesPerSecond { get; init; } = 2L * 1024 * 1024;

    public static SourceVerificationPolicy Default(string installationTimeZoneId) => new() { TimeZoneId = installationTimeZoneId };

    public void Validate()
    {
        if (Version != 1) throw new ArgumentOutOfRangeException(nameof(Version));
        ArgumentException.ThrowIfNullOrWhiteSpace(TimeZoneId);
        if (TimeZoneId.Length > 128) throw new ArgumentOutOfRangeException(nameof(TimeZoneId));
        if (!Enum.IsDefined(Weekday)) throw new ArgumentOutOfRangeException(nameof(Weekday));
        if (LocalStart.Ticks % TimeSpan.TicksPerMinute != 0) throw new ArgumentOutOfRangeException(nameof(LocalStart));
        if (WindowDuration <= TimeSpan.Zero || WindowDuration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(WindowDuration));
        if (MaximumBytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumBytesPerSecond));
    }
}

public sealed record SourceVerificationWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;
}

/// <summary>New cycles are weekly; an unfinished cycle continues in daily local quiet windows.</summary>
public static class SourceVerificationSchedule
{
    public static SourceVerificationWindow CurrentOrNextWindow(SourceVerificationPolicy policy, DateTimeOffset now, TimeZoneInfo zone)
    {
        ValidateZone(policy, zone);
        var localDate = TimeZoneInfo.ConvertTime(now, zone).Date;
        var windows = Enumerable.Range(-1, 4).Select(day => Window(policy, localDate.AddDays(day), zone)).OrderBy(x => x.Start).ToArray();
        return windows.LastOrDefault(x => x.Contains(now)) ?? windows.First(x => x.Start > now);
    }

    public static DateTimeOffset? DueCycle(SourceVerificationPolicy policy, DateTimeOffset now,
        DateTimeOffset? lastStarted, bool hasActiveCycle, TimeZoneInfo zone)
    {
        var window = CurrentOrNextWindow(policy, now, zone);
        if (!policy.Enabled || hasActiveCycle || !window.Contains(now)) return null;
        var localDate = TimeZoneInfo.ConvertTime(now, zone).Date;
        var days = ((int)localDate.DayOfWeek - (int)policy.Weekday + 7) % 7;
        var weeklyDate = localDate.AddDays(-days);
        var due = Start(policy, weeklyDate, zone);
        if (due > now) due = Start(policy, weeklyDate.AddDays(-7), zone);
        return lastStarted is null || lastStarted < due ? due : null;
    }

    private static SourceVerificationWindow Window(SourceVerificationPolicy policy, DateTime localDate, TimeZoneInfo zone)
    {
        var start = Start(policy, localDate, zone);
        return new(start, start.Add(policy.WindowDuration));
    }

    private static DateTimeOffset Start(SourceVerificationPolicy policy, DateTime localDate, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate.Add(policy.LocalStart.ToTimeSpan()), DateTimeKind.Unspecified);
        // The persisted start has minute precision. Skip the DST gap without inventing a UTC time.
        for (var minutes = 0; zone.IsInvalidTime(local); minutes++)
        {
            if (minutes >= 24 * 60) throw new InvalidTimeZoneException("Verification quiet window has no valid local start.");
            local = local.AddMinutes(1);
        }
        // A repeated wall-clock start represents one cycle, at its first occurrence.
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static void ValidateZone(SourceVerificationPolicy policy, TimeZoneInfo zone)
    {
        policy.Validate();
        if (!string.Equals(policy.TimeZoneId, zone.Id, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Verification timezone does not match the persisted policy.", nameof(zone));
    }
}
