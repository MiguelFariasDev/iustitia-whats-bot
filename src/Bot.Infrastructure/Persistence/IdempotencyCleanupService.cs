using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Advocacia.Bot.Infrastructure.Persistence;

/// <summary>
/// Apaga registros concluídos há mais de <see cref="Retention"/>. A Meta reentrega um webhook por
/// até 7 dias; passado isso, o registro não protege contra mais nada e só ocuparia espaço. Guarda
/// só chaves e ids — nenhum telefone nem texto de mensagem.
/// </summary>
public sealed class IdempotencyCleanupService(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<IdempotencyCleanupService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(8);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                var cutoff = clock.GetUtcNow() - Retention;

                var removed = await dbContext.IdempotencyRecords
                    .Where(record => record.State == IdempotencyState.Done && record.CompletedAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (removed > 0)
                {
                    logger.LogInformation("Limpeza de idempotência removeu {Count} registros.", removed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Falha na limpeza de registros de idempotência.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
