using Microsoft.EntityFrameworkCore;
using Npgsql;
using RoyaltyFlow.Infrastructure;

namespace RoyaltyFlow.Api;

public static class DatabaseDeployment
{
    private const string DeploymentDatabase = "royaltyflow";

    public static NpgsqlConnectionStringBuilder ReadSettings(IConfiguration configuration)
    {
        var host = configuration["Postgres:Host"];
        if (!string.IsNullOrWhiteSpace(host))
        {
            var database = configuration["Postgres:Database"];
            var user = configuration["Postgres:User"];
            var password = configuration["Postgres:Password"];
            if (!string.Equals(database, DeploymentDatabase, StringComparison.Ordinal) ||
                !string.Equals(user, "royaltyflow_user", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(password))
                throw new DatabaseUnavailableException();

            // Assign fields individually so characters in the password cannot change connection options.
            return new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Database = database,
                Username = user,
                Password = password,
                SearchPath = "public",
                Timeout = 5
            };
        }

        var connectionString = configuration.GetConnectionString("RoyaltyFlow");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new DatabaseUnavailableException();
        return new NpgsqlConnectionStringBuilder(connectionString);
    }

    public static async Task MigrateAsync(RoyaltyDbContext db, CancellationToken token)
    {
        var connection = TargetConnection(db);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await ValidateTargetAsync(connection, token);

            // EF Core 8 has no cross-process migration locking. The session lock is released when closed.
            await using var migrationLock = new NpgsqlCommand("SELECT pg_try_advisory_lock(7249852235060016)", connection);
            if (await migrationLock.ExecuteScalarAsync(token) is not true)
                throw new InvalidOperationException("Another RoyaltyFlow migration is running.");

            try
            {
                await db.Database.MigrateAsync(token);
            }
            finally
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(7249852235060016)", connection)
                {
                    CommandTimeout = 3
                };
                await unlock.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public static async Task<bool> HasPendingMigrationsAsync(RoyaltyDbContext db, CancellationToken token)
    {
        var connection = TargetConnection(db);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await ValidateTargetAsync(connection, token);
            var applied = await db.Database.GetAppliedMigrationsAsync(token);
            var known = db.Database.GetMigrations().ToArray();
            if (applied.Except(known, StringComparer.Ordinal).Any())
                throw new InvalidOperationException("The database contains migrations unknown to this release.");
            return known.Except(applied, StringComparer.Ordinal).Any();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static NpgsqlConnection TargetConnection(RoyaltyDbContext db)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (!string.Equals(connection.Database, DeploymentDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException("The migration command requires the royaltyflow database.");
        return connection;
    }

    private static async Task ValidateTargetAsync(NpgsqlConnection connection, CancellationToken token)
    {
        // OpenConnectionAsync must succeed before EF can consider creating a missing database.
        await using var target = new NpgsqlCommand("SELECT current_database()", connection);
        if (!string.Equals(await target.ExecuteScalarAsync(token) as string, DeploymentDatabase,
                StringComparison.Ordinal) || connection.PostgreSqlVersion.Major != 16)
            throw new InvalidOperationException("The migration target must be royaltyflow on PostgreSQL 16.");
    }
}
