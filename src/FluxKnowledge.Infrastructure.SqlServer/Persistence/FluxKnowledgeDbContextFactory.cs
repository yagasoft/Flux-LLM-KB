using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class FluxKnowledgeDbContextFactory : IDesignTimeDbContextFactory<FluxKnowledgeDbContext>
{
    private const string DesignTimeConnection =
        "Server=localhost;Initial Catalog=FluxKnowledge_DesignTimeOnly;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";

    public FluxKnowledgeDbContext CreateDbContext(string[] args)
    {
        _ = args;
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(DesignTimeConnection)
            .Options;
        return new FluxKnowledgeDbContext(options);
    }
}
