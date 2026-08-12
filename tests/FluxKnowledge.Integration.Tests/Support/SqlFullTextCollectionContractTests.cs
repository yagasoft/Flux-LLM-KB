using System.Reflection;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class SqlFullTextCollectionContractTests
{
    [Fact]
    public void Full_text_polling_tests_are_serialised_in_one_nonparallel_collection()
    {
        var pollingTests = new[]
        {
            typeof(SqlToUsearchRebuildTests),
            typeof(Task5RegressionTests),
            typeof(HybridSearchIntegrationTests)
        };

        Assert.All(
            pollingTests,
            type => Assert.Equal(
                "sql-full-text",
                type.CustomAttributes
                    .SingleOrDefault(attribute => attribute.AttributeType == typeof(CollectionAttribute))
                    ?.ConstructorArguments.Single().Value));

        var definition = typeof(SqlToUsearchRebuildTests).Assembly
            .GetTypes()
            .SelectMany(type => type.CustomAttributes)
            .SingleOrDefault(attribute =>
                attribute.AttributeType == typeof(CollectionDefinitionAttribute) &&
                attribute.ConstructorArguments.Single().Value is "sql-full-text");

        Assert.NotNull(definition);
        Assert.Equal(
            true,
            definition.NamedArguments.Single(argument => argument.MemberName == "DisableParallelization").TypedValue.Value);
    }
}
