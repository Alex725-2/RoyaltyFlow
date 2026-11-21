using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RoyaltyFlow.Api;
using RoyaltyFlow.Application;
using RoyaltyFlow.Domain;
using RoyaltyFlow.Infrastructure;
using Testcontainers.PostgreSql;
using Xunit;

namespace RoyaltyFlow.PostgresTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder().WithImage("postgres:16.16")
        .WithDatabase("royaltyflow").Build();
    public NpgsqlDataSource Source { get; private set; } = null!;
    public string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        Source = NpgsqlDataSource.Create(ConnectionString);
        await using var db = CreateContext();
        await DatabaseDeployment.MigrateAsync(db, default);
        await DatabaseDeployment.MigrateAsync(db, default);
    }

    public RoyaltyDbContext CreateContext() => new(new DbContextOptionsBuilder<RoyaltyDbContext>()
        .UseNpgsql(Source).Options);

    public async Task DisposeAsync()
    {
        if (Source is not null)
            await Source.DisposeAsync();
        await container.DisposeAsync();
    }
}

public sealed class PostgresImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ConcurrentIdenticalFilesReuseReceiptAndExistingEventIsDuplicate()
    {
        var track = await SeedTrackAsync();
        var eventId = Guid.NewGuid();
        var unknownTrack = Guid.NewGuid();
        var csv = $"{UsageCsvReader.Header}\n{Line(eventId, track)}\n{Line(eventId, track)}\n{Line(Guid.NewGuid(), unknownTrack)}\n";
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var preview = await client.PostAsync("/api/imports/preview", Body(csv));
        var forecast = await preview.Content.ReadFromJsonAsync<ImportPreview>();
        Assert.Equal(new ImportStatistics(3, 1, 1, 1), forecast!.Statistics);

        var uploads = await Task.WhenAll(client.PostAsync("/api/imports/upload", Body(csv)),
            client.PostAsync("/api/imports/upload", Body(csv)));
        using var created = Assert.Single(uploads, response => response.StatusCode == HttpStatusCode.Created);
        using var reused = Assert.Single(uploads, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(await created.Content.ReadAsStringAsync(), await reused.Content.ReadAsStringAsync());
        using var details = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);

        using var repeatedEvent = await client.PostAsync("/api/imports/upload", Body(
            $"{UsageCsvReader.Header}\n{Line(eventId, track)}\n"));
        Assert.Equal(HttpStatusCode.Created, repeatedEvent.StatusCode);
        using var result = JsonDocument.Parse(await repeatedEvent.Content.ReadAsStringAsync());
        Assert.Equal(1, result.RootElement.GetProperty("statistics").GetProperty("duplicate").GetInt64());

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.Usage.CountAsync(row => row.EventId == eventId));
        Assert.Equal(0.02m, await db.Accruals.Where(row => row.EventId == eventId).SumAsync(row => row.Amount));
        var report = await new PostgresRoyaltyAnalytics(fixture.Source).SummarizeAsync(new(2025, 1, 1),
            new(2025, 1, 31), 0, 100, default);
        Assert.Equal(0.02m, Assert.Single(report, row => row.TrackId == track).RoyaltyAmount);
    }

    [Fact]
    public async Task ConcurrentDifferentFilesWithSameEventInsertExactlyOnce()
    {
        var track = await SeedTrackAsync();
        var eventId = Guid.NewGuid();
        var row = Line(eventId, track);
        var lf = $"{UsageCsvReader.Header}\n{row}\n";
        var crlf = $"{UsageCsvReader.Header}\r\n{row}\r\n";
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var responses = await Task.WhenAll(client.PostAsync("/api/imports/upload", Body(lf)),
            client.PostAsync("/api/imports/upload", Body(crlf)));
        using var first = responses[0];
        using var second = responses[1];
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.NotEqual(first.Headers.Location, second.Headers.Location);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(1, Count(firstJson, "inserted") + Count(secondJson, "inserted"));
        Assert.Equal(1, Count(firstJson, "duplicate") + Count(secondJson, "duplicate"));

        static long Count(JsonDocument json, string name) =>
            json.RootElement.GetProperty("statistics").GetProperty(name).GetInt64();
    }

    [Fact]
    public async Task FailedTransactionRollsBackUsageAndCanRetrySameBytes()
    {
        var track = await SeedTrackAsync(createShares: false);
        var eventId = Guid.NewGuid();
        var csv = $"{UsageCsvReader.Header}\n{Line(eventId, track)}\n";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv))).ToLowerInvariant();
        var importer = new PostgresUsageImporter(fixture.Source, new RoyaltyCalculator());
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(csv)))
            await Assert.ThrowsAsync<ArgumentException>(() => importer.UploadAsync(input, hash, default));

        await using var db = fixture.CreateContext();
        var failed = await db.Imports.AsNoTracking().SingleAsync(row => row.Sha256 == hash);
        Assert.Equal(ImportStatus.Failed, failed.Status);
        Assert.False(await db.Usage.AnyAsync(row => row.EventId == eventId));
        Assert.False(await db.Accruals.AnyAsync(row => row.EventId == eventId));

        var contract = await db.Contracts.SingleAsync(row => row.TrackId == track);
        var holder = new RightsHolder(Guid.NewGuid(), "Recovered rights");
        db.RightsHolders.Add(holder);
        contract.Shares.Add(new ShareRow(contract.Id, holder.Id, 100));
        await db.SaveChangesAsync();

        using var retry = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var recovered = await importer.UploadAsync(retry, hash, default);
        Assert.Equal(failed.Id, recovered.Import.Id);
        Assert.Equal(ImportStatus.Completed, recovered.Import.Status);
        Assert.Equal(1, recovered.Import.Statistics.Inserted);
    }

    [Fact]
    public async Task DatabaseRejectsOverlappingContractVersionsButAllowsAdjacentDates()
    {
        var track = await SeedTrackAsync();
        await using var db = fixture.CreateContext();
        db.Contracts.Add(new ContractRow(Guid.NewGuid(), track, new(2025, 2, 1), new(2025, 3, 1)));
        await db.SaveChangesAsync();
        db.Contracts.Add(new ContractRow(Guid.NewGuid(), track, new(2025, 1, 31), new(2025, 3, 1)));
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:RoyaltyFlow"] = fixture.ConnectionString
            })));

    private async Task<Guid> SeedTrackAsync(bool createShares = true)
    {
        await using var db = fixture.CreateContext();
        var artist = new Artist(Guid.NewGuid(), "Demo Artist");
        var track = new Track(Guid.NewGuid(), artist.Id, "Synthetic Track");
        var holder = new RightsHolder(Guid.NewGuid(), "Demo Rights");
        var contract = new ContractRow(Guid.NewGuid(), track.Id, new(2025, 1, 1), new(2025, 2, 1));
        if (createShares)
            contract.Shares.Add(new ShareRow(contract.Id, holder.Id, 100));
        db.AddRange(artist, track, holder, contract);
        await db.SaveChangesAsync();
        return track.Id;
    }

    private static string Line(Guid eventId, Guid track) => $"{eventId:D},{track:D},Demo,US,2025-01-01,1";
    private static StringContent Body(string csv) => new(csv, Encoding.UTF8, "text/csv");
}
