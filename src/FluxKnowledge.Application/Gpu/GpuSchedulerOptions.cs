namespace FluxKnowledge.Application.Gpu;

public sealed record GpuSchedulerOptions
{
    public static GpuSchedulerOptions Default { get; } = new(
        maxBatchItems: 32,
        maxBatchEstimatedBytes: 512L * 1024 * 1024,
        capacityDeferralCap: TimeSpan.FromMinutes(5),
        fallbackInterval: TimeSpan.FromMinutes(1),
        unresponsiveDiagnosticAge: TimeSpan.FromMinutes(10));

    public GpuSchedulerOptions(
        int maxBatchItems,
        long maxBatchEstimatedBytes,
        TimeSpan capacityDeferralCap,
        TimeSpan fallbackInterval,
        TimeSpan unresponsiveDiagnosticAge)
    {
        MaxBatchItems = maxBatchItems;
        MaxBatchEstimatedBytes = maxBatchEstimatedBytes;
        CapacityDeferralCap = capacityDeferralCap;
        FallbackInterval = fallbackInterval;
        UnresponsiveDiagnosticAge = unresponsiveDiagnosticAge;
        Validate();
    }

    public int MaxBatchItems { get; }

    public long MaxBatchEstimatedBytes { get; }

    public TimeSpan CapacityDeferralCap { get; }

    public TimeSpan FallbackInterval { get; }

    public TimeSpan UnresponsiveDiagnosticAge { get; }

    public void Validate()
    {
        if (MaxBatchItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBatchItems));
        }

        if (MaxBatchEstimatedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBatchEstimatedBytes));
        }

        if (CapacityDeferralCap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CapacityDeferralCap));
        }

        if (FallbackInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(FallbackInterval));
        }

        if (UnresponsiveDiagnosticAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(UnresponsiveDiagnosticAge));
        }
    }

    public TimeSpan CapRetryDelay(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        }

        return retryAfter > CapacityDeferralCap ? CapacityDeferralCap : retryAfter;
    }
}
