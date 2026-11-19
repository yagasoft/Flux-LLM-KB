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
        TimeSpan unresponsiveDiagnosticAge,
        GpuWorkloadPolicy? workloadPolicy = null,
        IReadOnlyList<GpuResidentModelKey>? residentEntryModels = null)
    {
        MaxBatchItems = maxBatchItems;
        MaxBatchEstimatedBytes = maxBatchEstimatedBytes;
        CapacityDeferralCap = capacityDeferralCap;
        FallbackInterval = fallbackInterval;
        UnresponsiveDiagnosticAge = unresponsiveDiagnosticAge;
        WorkloadPolicy = workloadPolicy;
        ResidentEntryModels = Array.AsReadOnly(residentEntryModels?.ToArray() ?? []);
        Validate();
    }

    public int MaxBatchItems { get; }

    public long MaxBatchEstimatedBytes { get; }

    public TimeSpan CapacityDeferralCap { get; }

    public TimeSpan FallbackInterval { get; }

    public TimeSpan UnresponsiveDiagnosticAge { get; }
    public GpuWorkloadPolicy? WorkloadPolicy { get; }
    public IReadOnlyList<GpuResidentModelKey> ResidentEntryModels { get; }

    public GpuResidentModelKey? GetResidentEntryModel(string runtimeKey, string settingsFingerprint) =>
        ResidentEntryModels.SingleOrDefault(model => model.RuntimeKey == runtimeKey && model.SettingsFingerprint == settingsFingerprint);

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

        if (ResidentEntryModels.Count > 0 && (MaxBatchItems != 1 || WorkloadPolicy is null))
            throw new ArgumentException("Resident GPU models require a configured workload policy and single-item admission.");
        foreach (var model in ResidentEntryModels)
        {
            model.Validate();
            if (WorkloadPolicy?.Profiles.Any(profile => profile.ModelRuntimeKey == model.RuntimeKey &&
                profile.SettingsFingerprint == model.SettingsFingerprint) != true)
                throw new ArgumentException("A resident GPU model must match an admitted runtime profile.");
        }
        if (ResidentEntryModels.Select(model => (model.RuntimeKey, model.SettingsFingerprint)).Distinct().Count() != ResidentEntryModels.Count)
            throw new ArgumentException("A GPU runtime profile must have a single resident entry model.");
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
