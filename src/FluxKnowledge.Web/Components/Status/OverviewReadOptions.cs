namespace FluxKnowledge.Web.Components.Status;

public sealed record OverviewReadOptions(bool UseSnapshotIsolation = false)
{
    public static OverviewReadOptions FromConfiguration(IConfiguration configuration) => configuration["Dashboard:ReadConsistency"] switch
    {
        null or "LegacyFenced" => new(),
        "Snapshot" => new(true),
        _ => throw new InvalidOperationException("Dashboard:ReadConsistency must be LegacyFenced or Snapshot.")
    };
}
