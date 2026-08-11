using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.OutlookHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => RunAsync(
        args,
        new DefaultOutlookHostApplicationFactory(),
        CancellationToken.None).GetAwaiter().GetResult();

    internal static async Task<int> RunAsync(
        string[] args,
        IOutlookHostApplicationFactory factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(factory);
        if (args.Length > 1 || args is [not "--run-once"] && args.Length != 0)
        {
            return 2;
        }

        var options = new OutlookHostOptions { Enabled = args.Length == 1 };
        await using var application = factory.Create(options);
        var result = await application.RunOnceAsync(cancellationToken).ConfigureAwait(false);
        return result.Reason is OutlookHostExitReason.Disabled or
            OutlookHostExitReason.NoDurableWork or
            OutlookHostExitReason.Completed ? 0 : 1;
    }
}

internal interface IOutlookHostApplicationFactory
{
    IOutlookHostApplication Create(OutlookHostOptions options);
}

internal interface IOutlookHostApplication : IAsyncDisposable
{
    ValueTask<OutlookHostRunResult> RunOnceAsync(CancellationToken cancellationToken);
}

internal sealed class DefaultOutlookHostApplicationFactory : IOutlookHostApplicationFactory
{
    public IOutlookHostApplication Create(OutlookHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return new DisabledOutlookHostApplication();
        }

        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new UnavailableOutlookHostApplication();
        }

        var dbOptions = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
            .Options;
        IDbContextFactory<FluxKnowledgeDbContext> contextFactory = new OutlookDbContextFactory(dbOptions);
        IOutlookCaptureStore store = new SqlOutlookCaptureStore(contextFactory);
        var controlPlane = new SqlOutlookHostControlPlane(store, contextFactory);
        var dispatcher = new OutlookStaDispatcher();
        var environment = new WindowsOutlookHostEnvironment();
        var singleton = new StaOutlookSessionSingletonFactory(new WindowsSessionSingletonFactory(), dispatcher);
        var adapterFactory = new GatedClassicOutlookAdapterFactory(new ClassicOutlookComActivator(), dispatcher);
        var ingestion = new OutlookExportIngestionBridge(
            new SqlOutlookReadyExportIngestionService(new SqlOutlookExportIngestionService(contextFactory)));
        var catchUp = new OutlookHostLoop(
            options,
            environment,
            singleton,
            controlPlane,
            adapterFactory,
            ingestion);
        var browse = new OutlookFolderBrowser(
            options,
            environment,
            singleton,
            controlPlane,
            adapterFactory);
        return new ComposedOutlookHostApplication(catchUp, browse, dispatcher);
    }

    private sealed class OutlookDbContextFactory(DbContextOptions<FluxKnowledgeDbContext> options)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}

internal sealed class ComposedOutlookHostApplication(
    OutlookHostLoop catchUp,
    OutlookFolderBrowser browse,
    OutlookStaDispatcher dispatcher) : IOutlookHostApplication
{
    public async ValueTask<OutlookHostRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var catchUpResult = await catchUp.RunOnceAsync(cancellationToken).ConfigureAwait(false);
        return catchUpResult.Reason == OutlookHostExitReason.NoDurableWork
            ? await browse.RunOnceAsync(cancellationToken).ConfigureAwait(false)
            : catchUpResult;
    }

    public ValueTask DisposeAsync() => dispatcher.DisposeAsync();
}

internal sealed class DisabledOutlookHostApplication : IOutlookHostApplication
{
    public ValueTask<OutlookHostRunResult> RunOnceAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new OutlookHostRunResult(OutlookHostExitReason.Disabled));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class UnavailableOutlookHostApplication : IOutlookHostApplication
{
    public ValueTask<OutlookHostRunResult> RunOnceAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new OutlookHostRunResult(OutlookHostExitReason.DurableClaimDisabled));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class WindowsOutlookHostEnvironment : IOutlookHostEnvironment
{
    private readonly OutlookHostIdentity _identity = CreateIdentity();

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsInteractiveSession =>
        IsWindows && Environment.UserInteractive && _identity.SessionId > 0;

    public OutlookHostIdentity Identity => _identity;

    private static OutlookHostIdentity CreateIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new OutlookHostIdentity("not-windows", 0, $"host-{Environment.ProcessId}");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new InvalidOperationException("A signed-in Windows user identity is required.");
        }

        return new OutlookHostIdentity(
            sid,
            Process.GetCurrentProcess().SessionId,
            $"host-{Environment.ProcessId}");
    }
}

internal sealed class WindowsSessionSingletonFactory : IOutlookSessionSingletonFactory
{
    public ValueTask<IAsyncDisposable?> TryAcquireAsync(
        OutlookHostIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var suffix = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{identity.WindowsUserSid}|{identity.SessionId}")));
        var mutex = new Mutex(initiallyOwned: false, $"Local\\FluxKnowledge.OutlookHost.{suffix}");
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return ValueTask.FromResult<IAsyncDisposable?>(null);
        }

        return ValueTask.FromResult<IAsyncDisposable?>(new MutexLease(mutex));
    }

    private sealed class MutexLease(Mutex mutex) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class StaOutlookSessionSingletonFactory(
    IOutlookSessionSingletonFactory inner,
    OutlookStaDispatcher dispatcher) : IOutlookSessionSingletonFactory
{
    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(
        OutlookHostIdentity identity,
        CancellationToken cancellationToken)
    {
        var lease = await dispatcher.InvokeAsync(
            () => inner.TryAcquireAsync(identity, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return lease is null ? null : new DispatcherLease(lease, dispatcher);
    }

    private sealed class DispatcherLease(
        IAsyncDisposable innerLease,
        OutlookStaDispatcher dispatcher) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispatcher.InvokeAsync(innerLease.DisposeAsync);
    }
}
