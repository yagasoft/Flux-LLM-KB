using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class HostedDatabaseFailureClassifierTests
{
    [Fact]
    public void Every_provider_error_must_be_an_availability_failure()
    {
        var classifier = new SqlHostedDatabaseFailureClassifier();
        var temporary = RetainedProcessorActivationHostedServiceTests.SqlFailure(-2, 258, 10054);
        Assert.True(classifier.TryClassify(temporary, out var error));
        Assert.Equal(-2, error);
        Assert.False(classifier.TryClassify(RetainedProcessorActivationHostedServiceTests.SqlFailure(258, 2627), out _));
        Assert.False(classifier.TryClassify(RetainedProcessorActivationHostedServiceTests.SqlFailure(258, 18456), out _));
        Assert.False(classifier.TryClassify(RetainedProcessorActivationHostedServiceTests.SqlFailure(1222), out _));
    }

    [Fact]
    public void Only_named_provider_wrappers_are_unwrapped()
    {
        var classifier = new SqlHostedDatabaseFailureClassifier();
        var temporary = RetainedProcessorActivationHostedServiceTests.SqlFailure(258);
        Assert.True(classifier.TryClassify(new DbUpdateException("wrapper", new RetryLimitExceededException("wrapper", temporary)), out _));
        Assert.False(classifier.TryClassify(new InvalidOperationException("invariant failure", temporary), out _));
        Assert.False(classifier.TryClassify(new OperationCanceledException(), out _));
    }

    [Fact]
    public void Production_registers_a_separate_policy_without_broadening_worker_retries()
    {
        using var provider = new ServiceCollection().AddFluxKnowledgeOutboxWorkers().BuildServiceProvider();
        var temporary = RetainedProcessorActivationHostedServiceTests.SqlFailure(258);
        Assert.True(provider.GetRequiredService<IHostedDatabaseFailureClassifier>().TryClassify(temporary, out _));
        Assert.False(provider.GetRequiredService<IRetainedProcessorFailureClassifier>().TryClassify(temporary, out _));
    }
}
