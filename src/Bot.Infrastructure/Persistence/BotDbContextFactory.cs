using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Advocacia.Bot.Infrastructure.Persistence;

/// <summary>Permite `dotnet ef migrations` sem o composition root real.</summary>
public sealed class BotDbContextFactory : IDesignTimeDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ADVOCACIA_BOT_DB_CONNECTION")
            ?? "Host=localhost;Port=5435;Database=advocacia_bot_db;Username=postgres;Password=postgres";

        var builder = new DbContextOptionsBuilder<BotDbContext>();
        builder.UseNpgsql(connectionString);
        builder.UseSnakeCaseNamingConvention();

        return new BotDbContext(builder.Options);
    }
}
