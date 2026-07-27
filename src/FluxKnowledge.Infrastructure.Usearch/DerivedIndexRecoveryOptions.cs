namespace FluxKnowledge.Infrastructure.Usearch;

public sealed record DerivedIndexRecoveryOptions(
    TimeSpan ProbeInterval,
    TimeSpan StagingRetention,
    TimeSpan QuarantineRetention)
{
    public static DerivedIndexRecoveryOptions Default { get; } = new(
        TimeSpan.FromSeconds(60), TimeSpan.FromHours(24), TimeSpan.FromDays(7));
}
