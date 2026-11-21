using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RoyaltyFlow.Api;
using RoyaltyFlow.Infrastructure;
using Xunit;

namespace RoyaltyFlow.Tests;

public sealed class DatabaseDeploymentTests
{
    [Fact]
    public void DatabaseSettingsTreatPasswordAsOneValue()
    {
        var value = string.Concat("synthetic", ";Database=other$", "\"'");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Postgres:Host"] = "securities-repository-postgres-1",
            ["Postgres:Database"] = "royaltyflow",
            ["Postgres:User"] = "royaltyflow_user",
            ["Postgres:Password"] = value
        }).Build();

        var settings = new NpgsqlConnectionStringBuilder(DatabaseDeployment.ReadSettings(configuration).ConnectionString);
        Assert.Equal("royaltyflow", settings.Database);
        Assert.Equal(value, settings.Password);
        Assert.Equal("royaltyflow_user", settings.Username);
    }

    [Fact]
    public async Task MigrationRejectsOtherDatabaseBeforeOpeningConnection()
    {
        await using var db = new RoyaltyDbContext(new DbContextOptionsBuilder<RoyaltyDbContext>()
            .UseNpgsql("Host=invalid.invalid;Database=other;Timeout=1").Options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseDeployment.MigrateAsync(db, default));
        Assert.Equal("The migration command requires the royaltyflow database.", error.Message);
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
