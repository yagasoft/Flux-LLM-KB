namespace FluxKnowledge.Web.Components.Status;

public sealed class OverviewReadBudgetExceededException(Exception innerException)
    : TimeoutException("Overview coordination exceeded its read budget.", innerException);
