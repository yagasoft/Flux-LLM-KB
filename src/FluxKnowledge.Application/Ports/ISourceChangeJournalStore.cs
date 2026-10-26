namespace FluxKnowledge.Application.Ports;

public sealed record JournalReadClaim(JournalCursor Cursor, string Owner, long Generation, long RegistrationGeneration);
public interface ISourceChangeJournalStore
{
    ValueTask<JournalReadClaim?> ClaimAsync(DateTimeOffset now, CancellationToken cancellationToken);
    ValueTask<JournalReadClaim?> ClaimVolumeAsync(string volumeIdentity, DateTimeOffset now, bool ignoreIdleDelay, CancellationToken cancellationToken);
    ValueTask MarkUnavailableAsync(JournalReadClaim claim, DateTimeOffset retryAt, CancellationToken cancellationToken);
    ValueTask AcceptAsync(JournalReadClaim claim, JournalPage page, CancellationToken cancellationToken);
}
