using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Sources;
using System.Text.Json;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class DeploymentValidationHoldTests
{
    [Theory]
    [InlineData("legacy")]
    [InlineData("invalid-json")]
    [InlineData("bad-version")]
    [InlineData("empty-operation")]
    [InlineData("no-release")]
    public void Existing_or_malformed_holds_never_permit_pipeline_work(string fault)
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxHoldTests_" + Guid.NewGuid().ToString("N"));
        var layout = LiveRootLayout.CreateForIsolatedTests(root);
        Directory.CreateDirectory(layout.RuntimeRoot);
        var path = Path.Combine(layout.RuntimeRoot, "deployment-validation-hold.json");
        File.WriteAllText(path, fault switch
        {
            "legacy" => "\"release-1\"", "invalid-json" => "{",
            "bad-version" => JsonSerializer.Serialize(new { version = 2, releaseId = "release-1", corpusRebuildOperationId = Guid.NewGuid() }),
            "empty-operation" => JsonSerializer.Serialize(new { version = 1, releaseId = "release-1", corpusRebuildOperationId = Guid.Empty }),
            _ => JsonSerializer.Serialize(new { version = 1, corpusRebuildOperationId = Guid.NewGuid() })
        });
        try
        {
            var state = ((IDeploymentValidationHold)new FileDeploymentValidationHold(layout)).ReadAdmissionState();
            Assert.True(state.IsHeld);
            Assert.Null(state.PermittedCorpusRebuildOperationId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Exact_rebuild_permission_is_read_afresh_and_retains_the_source_hold()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxHoldTests_" + Guid.NewGuid().ToString("N"));
        var layout = LiveRootLayout.CreateForIsolatedTests(root);
        Directory.CreateDirectory(layout.RuntimeRoot);
        var path = Path.Combine(layout.RuntimeRoot, "deployment-validation-hold.json");
        var operation = Guid.NewGuid();
        File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, releaseId = "release-1", corpusRebuildOperationId = operation }));
        try
        {
            IDeploymentValidationHold hold = new FileDeploymentValidationHold(layout);
            Assert.Equal(new DeploymentHoldAdmission(true, operation), hold.ReadAdmissionState());
            Assert.True(hold.IsHeld);
            File.WriteAllText(path, "\"release-1\"");
            Assert.Equal(new DeploymentHoldAdmission(true, null), hold.ReadAdmissionState());
            File.Delete(path);
            Assert.Equal(new DeploymentHoldAdmission(false, null), hold.ReadAdmissionState());
        }
        finally { Directory.Delete(root, true); }
    }
}
