using Advocacia.Bot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Advocacia.Bot.IntegrationTests;

/// <summary>
/// PostgreSQL real para o banco PRÓPRIO do Bot, com as migrations aplicadas. Por padrão sobe um
/// container (Testcontainers); sem Docker, aponte <c>ADVOCACIA_TEST_PG_SERVER</c> para um servidor
/// PostgreSQL 16 já em pé (superusuário) e a fixture cria e remove um banco descartável nele.
/// </summary>
public sealed class BotPostgresFixture : IAsyncLifetime
{
    private const string ExternalServerVariable = "ADVOCACIA_TEST_PG_SERVER";

    private readonly string? _externalServer = Environment.GetEnvironmentVariable(ExternalServerVariable);
    private readonly string _externalDatabase = $"advocacia_bot_it_{Guid.NewGuid():N}";
    private readonly PostgreSqlContainer? _container;

    public BotPostgresFixture()
    {
        if (string.IsNullOrWhiteSpace(_externalServer))
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").WithDatabase("advocacia_bot_db").Build();
        }
    }

    public string ConnectionString => _container is not null
        ? _container.GetConnectionString()
        : new NpgsqlConnectionStringBuilder(_externalServer!) { Database = _externalDatabase }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (_container is not null)
        {
            await _container.StartAsync();
        }
        else
        {
            await using var admin = new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(_externalServer!) { Database = "postgres" }.ConnectionString);
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{_externalDatabase}\"";
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<BotDbContext>().UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention().Options;
        await using var context = new BotDbContext(options);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(_externalServer!) { Database = "postgres" }.ConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_externalDatabase}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }
}
