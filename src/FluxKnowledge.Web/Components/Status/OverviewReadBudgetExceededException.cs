namespace FluxKnowledge.Web.Components.Status;

public sealed class OverviewReadBudgetExceededException(Exception innerException)
    : TimeoutException("Overview exceeded its read budget.", innerException);
