using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Admits only the fixed one-page local PaddleOCR-VL workload to the one local slot.</summary>
public sealed class PaddleOcrVlmAdmissionGate : IGpuAdmissionGate
{
    public ValueTask<GpuAdmissionDecision> DecideAsync(
        GpuBatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.ItemCount != 1 ||
            candidate.EstimatedBytes != PaddleOcrVlmRuntimeContract.EstimatedDocumentBytes ||
            !string.Equals(candidate.ModelRuntimeKey, PaddleOcrVlmRuntimeContract.ModelRuntimeKey, StringComparison.Ordinal) ||
            !string.Equals(candidate.SettingsFingerprint, PaddleOcrVlmRuntimeContract.SettingsFingerprint, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Busy, null, null, null));
        }

        return ValueTask.FromResult(new GpuAdmissionDecision(
            GpuAdmissionDisposition.Admit,
            PaddleOcrVlmRuntimeContract.CapacitySlotKey,
            PaddleOcrVlmRuntimeContract.CapacityOwnerKey,
            null,
            PaddleOcrVlmRuntimeContract.ExecutorKey));
    }
}

/// <summary>
/// Adds the fixed local slot once when OCR is deliberately enabled. Existing reserved or
/// uncertain state is preserved: startup never assumes a prior execution completed.
/// </summary>
public sealed class PaddleOcrVlmCapacityBootstrapService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PaddleOcrVlmCapacityBootstrapService>? logger = null,
    IDatabaseStartupReadiness? databaseStartupReadiness = null) : BackgroundService
{
    private readonly ILogger<PaddleOcrVlmCapacityBootstrapService> _logger =
        logger ?? NullLogger<PaddleOcrVlmCapacityBootstrapService>.Instance;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => PrepareCapacityAsync(stoppingToken);

    internal async Task PrepareCapacityAsync(CancellationToken cancellationToken)
    {
        await (databaseStartupReadiness?.WaitAsync(cancellationToken) ?? ValueTask.CompletedTask).ConfigureAwait(false);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
            await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (await context.GpuCapacitySlots.AnyAsync(
                    slot => slot.SlotKey == PaddleOcrVlmRuntimeContract.CapacitySlotKey,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
            {
                SlotKey = PaddleOcrVlmRuntimeContract.CapacitySlotKey,
                State = (int)GpuCapacitySlotState.Available,
                UpdatedAtUtc = timeProvider.GetUtcNow()
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException failure) when (failure.InnerException is SqlException sql &&
            sql.Errors.Count > 0 && sql.Errors.Cast<SqlError>().All(error => error.Number is 2601 or 2627))
        {
            // A concurrent app start may have inserted the same durable slot. The unique slot
            // key and a fresh authoritative read are required; never replace its state.
            await using var scope = scopeFactory.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
            await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            if (!await context.GpuCapacitySlots.AsNoTracking().AnyAsync(
                    slot => slot.SlotKey == PaddleOcrVlmRuntimeContract.CapacitySlotKey, cancellationToken).ConfigureAwait(false))
                throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The fixed PaddleOCR-VL capacity slot could not be prepared.");
            throw;
        }
    }

}

/// <summary>Resumes only stored local OCR completions after a process interruption.</summary>
public sealed class PaddleOcrVlmCompletionRecoveryService(
    PaddleOcrVlmCompletionCoordinator completionCoordinator,
    TimeProvider timeProvider,
    ILogger<PaddleOcrVlmCompletionRecoveryService>? logger = null,
    IDatabaseStartupReadiness? databaseStartupReadiness = null) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(30);
    private readonly ILogger<PaddleOcrVlmCompletionRecoveryService> _logger =
        logger ?? NullLogger<PaddleOcrVlmCompletionRecoveryService>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await (databaseStartupReadiness?.WaitAsync(stoppingToken) ?? ValueTask.CompletedTask).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await completionCoordinator.RecoverAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Stored PaddleOCR-VL completion recovery failed; durable state was retained.");
            }

            try
            {
                await Task.Delay(Cadence, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
