using Microsoft.EntityFrameworkCore;

namespace Advocacia.Bot.Infrastructure.Persistence;

public enum IdempotencyState
{
    Processing,
    Done,
}

/// <summary>Uma operação já reivindicada (e, se concluída, o seu resultado). Ver <c>IIdempotencyLedger</c>.</summary>
public sealed class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;

    public IdempotencyState State { get; set; }

    public string? Result { get; set; }

    public DateTimeOffset ClaimedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Banco PRÓPRIO do Bot (<c>advocacia_bot_db</c>). Isolado do banco do Core por regra: o Bot nunca
/// o acessa — toda informação do Core chega pela API REST. Aqui só mora o que é do Bot: o livro de
/// idempotência de webhooks recebidos e de envios pedidos pelo Core.
/// </summary>
public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdempotencyRecord>(builder =>
        {
            builder.ToTable("idempotency_records");
            builder.HasKey(record => record.Key);
            builder.Property(record => record.Key).HasMaxLength(200);
            builder.Property(record => record.State).HasConversion<string>().HasMaxLength(20).IsRequired();
            builder.Property(record => record.Result).HasMaxLength(500);
            builder.Property(record => record.ClaimedAt).IsRequired();

            // Para a limpeza periódica de registros concluídos antigos.
            builder.HasIndex(record => new { record.State, record.CompletedAt }).HasDatabaseName("ix_idempotency_state_completed");
        });
    }
}
