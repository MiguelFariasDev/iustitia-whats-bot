using Advocacia.Bot.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Advocacia.Bot.Infrastructure.Persistence;

/// <summary>
/// Livro de idempotência sobre PostgreSQL. A reivindicação é um único <c>INSERT … ON CONFLICT DO
/// NOTHING</c> — atômico mesmo com duas réplicas do Bot recebendo o mesmo webhook ao mesmo tempo
/// (a Meta às vezes entrega em duplicidade em paralelo).
///
/// Uma reivindicação "em andamento" há mais de <see cref="StaleAfter"/> é considerada abandonada
/// (processo morreu no meio) e pode ser retomada pela reentrega seguinte.
/// </summary>
public sealed class EfIdempotencyLedger(BotDbContext dbContext, TimeProvider clock) : IIdempotencyLedger
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public async Task<ClaimResult> ClaimAsync(string key, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO idempotency_records (key, state, claimed_at)
            VALUES ({key}, 'Processing', {now})
            ON CONFLICT (key) DO NOTHING
            """,
            cancellationToken);

        if (inserted == 1)
        {
            return new ClaimResult(ClaimOutcome.Claimed);
        }

        var existing = await dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(record => record.Key == key, cancellationToken);

        if (existing is null)
        {
            // Liberado entre o INSERT e a leitura: quem chegar agora reivindica.
            return await ClaimAsync(key, cancellationToken);
        }

        if (existing.State == IdempotencyState.Done)
        {
            return new ClaimResult(ClaimOutcome.AlreadyDone, existing.Result);
        }

        var cutoff = now - StaleAfter;
        var takenOver = await dbContext.IdempotencyRecords
            .Where(record => record.Key == key && record.State == IdempotencyState.Processing && record.ClaimedAt < cutoff)
            .ExecuteUpdateAsync(setters => setters.SetProperty(record => record.ClaimedAt, now), cancellationToken);

        return takenOver == 1
            ? new ClaimResult(ClaimOutcome.Claimed)
            : new ClaimResult(ClaimOutcome.InProgress);
    }

    public async Task CompleteAsync(string key, string? result, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await dbContext.IdempotencyRecords
            .Where(record => record.Key == key)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.State, IdempotencyState.Done)
                    .SetProperty(record => record.Result, result)
                    .SetProperty(record => record.CompletedAt, now),
                cancellationToken);
    }

    public async Task ReleaseAsync(string key, CancellationToken cancellationToken) =>
        await dbContext.IdempotencyRecords
            .Where(record => record.Key == key && record.State == IdempotencyState.Processing)
            .ExecuteDeleteAsync(cancellationToken);
}
