using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Integration.Tests.Support;

/// <summary>Explicitly models an already initialised host for pre-existing worker fixtures.</summary>
internal sealed class FixtureSourceAdmission(IDbContextFactory<FluxKnowledgeDbContext> factory) : ISourceWorkAdmission
{
    public IReadOnlyCollection<SourceWorkAdmittedRoot> ReadAdmittedRoots()
    {
        using var db = factory.CreateDbContext();
        // A caller may deliberately own the only pooled connection. A real host
        // reads its in-memory admission snapshot; keep this fixture-only metadata
        // read in a separate pool so it cannot consume that caller's slot.
        var binding = new SqlConnectionStringBuilder(db.Database.GetConnectionString());
        binding.ApplicationName = "FluxKnowledge.FixtureSourceAdmission";
        db.Database.SetConnectionString(binding.ConnectionString);
        // Fixture roots may be seeded in the caller's open transaction. This
        // test-only metadata read must not block that transaction on another connection.
        return db.Database.SqlQuery<SourceWorkAdmittedRoot>($"""
            SELECT [Id] AS [RootId], [ConfigurationRevision]
            FROM [SourceRootConfigurations] WITH (READUNCOMMITTED)
            """).ToArray();
    }
}
