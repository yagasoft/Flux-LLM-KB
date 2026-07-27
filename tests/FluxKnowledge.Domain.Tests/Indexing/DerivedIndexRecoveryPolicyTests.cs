using FluxKnowledge.Application.Indexing;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Indexing;

public sealed class DerivedIndexRecoveryPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 30)]
    public void Recoverable_failure_schedules_the_configured_bounded_delay(
        int failedAttemptCount, int seconds)
    {
        var decision = DerivedIndexRecoveryPolicy.Decide(
            DerivedIndexRecoveryFailureCategory.TransientIo,
            failedAttemptCount);

        Assert.True(decision.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(seconds), decision.Delay);
        Assert.Equal(DerivedIndexRecoveryState.RetryScheduled, decision.NextState);
    }

    [Fact]
    public void Invalid_sql_membership_requires_operator_action_without_retry()
    {
        var decision = DerivedIndexRecoveryPolicy.Decide(
            DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid,
            failedAttemptCount: 1);

        Assert.False(decision.ShouldRetry);
        Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, decision.NextState);
    }

    [Fact]
    public void Fifth_recoverable_failure_requires_operator_action_without_retry()
    {
        var decision = DerivedIndexRecoveryPolicy.Decide(
            DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex,
            failedAttemptCount: 5);

        Assert.False(decision.ShouldRetry);
        Assert.Null(decision.Delay);
        Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, decision.NextState);
        Assert.Equal(DerivedIndexRecoveryFailureCategory.RetryExhausted, decision.FailureCategory);
    }

    [Theory]
    [InlineData(DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid)]
    [InlineData(DerivedIndexRecoveryFailureCategory.SqlSchemaInvalid)]
    [InlineData(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid)]
    [InlineData(DerivedIndexRecoveryFailureCategory.PermissionsDenied)]
    public void Permanent_failure_requires_operator_action_without_retry(
        DerivedIndexRecoveryFailureCategory category)
    {
        var decision = DerivedIndexRecoveryPolicy.Decide(category, failedAttemptCount: 1);

        Assert.False(decision.ShouldRetry);
        Assert.Null(decision.Delay);
        Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, decision.NextState);
        Assert.Equal(category, decision.FailureCategory);
    }
}
