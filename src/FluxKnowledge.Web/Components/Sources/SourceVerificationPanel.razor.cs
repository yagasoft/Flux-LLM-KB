using System.ComponentModel.DataAnnotations;
using System.Globalization;
using FluxKnowledge.Domain.Sources;
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
    private bool _editing;
    private string? _formError;
    private PolicyDraft _draft = new();

    private bool ControlsDisabled => _state.IsSubmitting || _state.HasPendingCommit || _state.ReadError is not null || RootState == "Deleting";
    private bool NeedsBaseline => _state.Status?.Scanning is not { BaselineEpoch: > 0, RecoveryRequired: false };
    private bool CanVerify => !ControlsDisabled && RootState == "Enabled" && !NeedsBaseline &&
        _state.Status is { FeatureEnabled: true, Paused: false };

    protected override void OnInitialized() => _state = new(VerificationStore, CorpusCommands);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedRoot == RootId) return;
        _loadedRoot = RootId;
        _editing = false;
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

    private Task VerifyAsync() => _state.VerifyAsync(RootId);
    private Task TogglePauseAsync() => _state.SetPausedAsync(RootId, !(_state.Status?.Paused ?? false));
    private Task RetryAsync() => _state.RetryAsync();

    private void BeginEdit()
    {
        if (_state.Status is not { } status) return;
        _draft = PolicyDraft.From(status.Policy);
        _formError = null;
        _editing = true;
    }

    private void CancelEdit() { _editing = false; _formError = null; }

    private async Task SaveAsync()
    {
        try
        {
            var policy = _draft.ToPolicy();
            policy.Validate();
            _ = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId);
            _formError = null;
            await _state.SavePolicyAsync(RootId, policy);
            if (!_state.HasPendingCommit && _state.Status?.Policy == policy) _editing = false;
        }
        catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        {
            _formError = "Enter a valid time zone, quiet window and positive read limit.";
        }
    }

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string When(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Not yet recorded";

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_poll is not null) await _poll;
        _stopping.Dispose();
    }

    private sealed class PolicyDraft
    {
        public bool Enabled { get; set; }
        public DayOfWeek Weekday { get; set; }
        public TimeOnly LocalStart { get; set; }
        [Required, StringLength(128)] public string TimeZoneId { get; set; } = "UTC";
        [Range(typeof(decimal), "0.25", "24")] public decimal WindowHours { get; set; }
        [Range(typeof(long), "1", "9223372036854775807")] public long BytesPerSecond { get; set; }
        public static PolicyDraft From(SourceVerificationPolicy policy) => new()
        {
            Enabled = policy.Enabled, Weekday = policy.Weekday, LocalStart = policy.LocalStart, TimeZoneId = policy.TimeZoneId,
            WindowHours = (decimal)policy.WindowDuration.TotalHours, BytesPerSecond = policy.MaximumBytesPerSecond
        };
        public SourceVerificationPolicy ToPolicy() => new()
        {
            Enabled = Enabled, Weekday = Weekday, LocalStart = LocalStart, TimeZoneId = TimeZoneId.Trim(),
            WindowDuration = TimeSpan.FromHours((double)WindowHours), MaximumBytesPerSecond = BytesPerSecond
        };
    }
}
