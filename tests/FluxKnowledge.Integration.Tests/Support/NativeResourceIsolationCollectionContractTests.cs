using System.Reflection;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Workers;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class NativeResourceIsolationCollectionContractTests
{
    [Fact]
    public void Native_usearch_file_tests_are_serialised_in_one_nonparallel_collection()
    {
        var usearchTests = new[]
        {
            typeof(UsearchGenerationTests),
            typeof(DerivedIndexRecoveryIntegrationTests),
            typeof(SqlToUsearchRebuildTests)
        };

        Assert.All(usearchTests, type => Assert.Equal("sql-full-text", CollectionName(type)));
        AssertNonparallelCollection(typeof(UsearchGenerationTests).Assembly, "sql-full-text");
    }

    [Fact]
    public void Sql_hosted_recovery_tests_are_serialised_in_a_nonparallel_collection()
    {
        Assert.Equal("sql-hosted-recovery", CollectionName(typeof(SqlGpuExecutorDispatchRecoveryServiceTests)));
        AssertNonparallelCollection(typeof(SqlGpuExecutorDispatchRecoveryServiceTests).Assembly, "sql-hosted-recovery");
    }

    private static string? CollectionName(Type type) =>
        type.CustomAttributes
            .SingleOrDefault(attribute => attribute.AttributeType == typeof(CollectionAttribute))
            ?.ConstructorArguments.Single().Value as string;

    private static void AssertNonparallelCollection(Assembly assembly, string collectionName)
    {
        var definition = assembly
            .GetTypes()
            .SelectMany(type => type.CustomAttributes)
            .SingleOrDefault(attribute =>
                attribute.AttributeType == typeof(CollectionDefinitionAttribute) &&
                attribute.ConstructorArguments.Single().Value is string name && name == collectionName);

        Assert.NotNull(definition);
        Assert.Equal(
            true,
            definition.NamedArguments.Single(argument => argument.MemberName == "DisableParallelization").TypedValue.Value);
    }
}
