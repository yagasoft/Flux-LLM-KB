using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

/// <summary>Explicit opt-in for writes confined to a newly created synthetic folder in the running provider.</summary>
public sealed class OneDriveQualificationFactAttribute : FactAttribute
{
    public OneDriveQualificationFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_ONEDRIVE_ROOT")))
            Skip = "Set FLUXKNOWLEDGE_TEST_ONEDRIVE_ROOT to the existing OneDriveCommercial root for explicit provider qualification.";
    }
}
