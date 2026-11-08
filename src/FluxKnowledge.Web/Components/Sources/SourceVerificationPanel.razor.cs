using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace FluxKnowledge.Web.Components.Sources;

public partial class SourceVerificationPanel
{
    [Parameter] public Guid RootId { get; set; }
    [Parameter] public string RootState { get; set; } = "Enabled";
    private SourceVerificationPanelState _state = null!;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _poll;
    private Guid _loadedRoot;
    private bool _refreshing;
    private bool ControlsDisabled => _state.IsSubmitting || _state.HasPendingCommit || _state.ReadError is not null || RootState == "Deleting";

    protected override void OnInitialized() => _state = new(VerificationStore, CorpusCommands);
    protected override async Task OnParametersSetAsync()
    {
        if (_loadedRoot == RootId) return;
        _loadedRoot = RootId;
        await _state.LoadAsync(RootId, _stopping.Token);
    }
    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) _poll = PollAsync();
        return Task.CompletedTask;
    }
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
                await InvokeAsync(async () => { await RefreshAsync(); StateHasChanged(); });
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }
    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { await _state.LoadAsync(RootId, _stopping.Token); }
        finally { _refreshing = false; }
    }
    private Task FullRescanAsync() => _state.FullRescanAsync(RootId);
    private Task RetryAsync() => _state.RetryAsync();
    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string When(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Not yet recorded";
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_poll is not null) await _poll;
        _stopping.Dispose();
    }
}
