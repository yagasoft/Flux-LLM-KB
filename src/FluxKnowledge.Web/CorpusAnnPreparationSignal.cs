using System.Threading.Channels;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Web;

/// <summary>Independent, coalesced publication wake; resolving it never constructs hosted services.</summary>
internal sealed class CorpusAnnPreparationSignal : ICorpusAnnPreparationSignal
{
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private CorpusAnnPreparationState _state = new("warming");

    internal ChannelReader<bool> Requests => _requests.Reader;
    internal CorpusAnnPreparationState State => Volatile.Read(ref _state);
    internal void SetState(CorpusAnnPreparationState state) => Volatile.Write(ref _state, state);
    public void RequestPreparation() => _requests.Writer.TryWrite(true);
    public string? FailureFor(Guid generation) => State is { Status: "failed" } state && state.Generation == generation ? state.Reason : null;
}
