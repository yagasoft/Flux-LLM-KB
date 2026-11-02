using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

// Keep asynchronous full-text consumers and fixtures with process-wide native
// resources isolated. Catalogue/root-isolated tests use bounded class parallelism.
[CollectionDefinition("sql-full-text", DisableParallelization = true)]
public sealed class SqlFullTextCollection;
