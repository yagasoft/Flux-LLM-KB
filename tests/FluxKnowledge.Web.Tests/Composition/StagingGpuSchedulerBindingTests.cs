using Xunit;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingGpuSchedulerBindingTests
{
    private const string Valid="Server=localhost;Database=FluxKnowledge_Phase1Tests_0123456789abcdef0123456789abcdef;User ID=restricted;Password=synthetic;Encrypt=True;TrustServerCertificate=True";
    [Fact]
    public void Exact_disposable_binding_does_not_replace_corpus_configuration()
    {
        var binding=new StagingGpuSchedulerBinding(Valid);
        using var db=binding.CreateDbContext();
        Assert.EndsWith("Phase1Tests_0123456789abcdef0123456789abcdef",db.Database.GetDbConnection().Database);
    }
    [Theory]
    [InlineData("Database=FluxKnowledge")]
    [InlineData("Database=FluxKnowledge_Staging_ScopedSemantic_20261029")]
    [InlineData("Server=elsewhere")]
    [InlineData("Integrated Security=true")]
    [InlineData("Failover Partner=elsewhere")]
    [InlineData("AttachDbFilename=I:\\foreign.mdf")]
    public void Production_corpus_or_redirected_bindings_are_rejected(string change) =>
        Assert.Throws<ArgumentException>(() => new StagingGpuSchedulerBinding(Valid+";"+change));
}
