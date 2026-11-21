using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

public sealed class OneDriveRepairResidencyTests
{
    [Fact]
    public void Complete_coverage_requires_an_exact_positive_eof_and_contiguous_ranges()
    {
        Assert.True(OneDriveRepairResidency.CoversFile(9, [0, 4, 4, 5]));
        Assert.True(OneDriveRepairResidency.CoversFile(9, [0, 9]));
        Assert.False(OneDriveRepairResidency.CoversFile(9, []));
        Assert.False(OneDriveRepairResidency.CoversFile(9, [0, 4, 5, 4]));
        Assert.False(OneDriveRepairResidency.CoversFile(9, [0, 5, 4, 5]));
        Assert.False(OneDriveRepairResidency.CoversFile(9, [0, 10]));
        Assert.False(OneDriveRepairResidency.CoversFile(9, [0, 4, 4]));
        Assert.False(OneDriveRepairResidency.CoversFile(0, []));
        Assert.False(OneDriveRepairResidency.CoversFile(long.MaxValue, [0, 42, 42, long.MaxValue]));
    }
}
