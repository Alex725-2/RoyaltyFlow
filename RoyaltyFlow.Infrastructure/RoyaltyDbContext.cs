using Microsoft.EntityFrameworkCore;
using RoyaltyFlow.Application;
using RoyaltyFlow.Domain;

namespace RoyaltyFlow.Infrastructure;

public sealed class RoyaltyDbContext(DbContextOptions<RoyaltyDbContext> options) : DbContext(options)
{
    public DbSet<Artist> Artists => Set<Artist>();
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<RightsHolder> RightsHolders => Set<RightsHolder>();
    public DbSet<ContractRow> Contracts => Set<ContractRow>();
    public DbSet<ImportRow> Imports => Set<ImportRow>();
    public DbSet<UsageRecord> Usage => Set<UsageRecord>();
    public DbSet<RoyaltyAccrual> Accruals => Set<RoyaltyAccrual>();

    protected override void OnModelCreating(ModelBuilder model) => InitialModel.Configure(model);
}

public sealed record ContractRow(Guid Id, Guid TrackId, DateOnly ValidFrom, DateOnly? ValidTo)
{
    public List<ShareRow> Shares { get; } = [];
}

public sealed record ShareRow(Guid ContractId, Guid RightsHolderId, decimal Percentage);

public sealed class ImportRow
{
    public Guid Id { get; init; }
    public string Sha256 { get; init; } = "";
    public ImportStatus Status { get; init; }
    public long Total { get; init; }
    public long Inserted { get; init; }
    public long Duplicate { get; init; }
    public long Invalid { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public long? DurationMs { get; init; }

    public ImportReceipt ToReceipt() => new(Id, Sha256, Status, new(Total, Inserted, Duplicate, Invalid),
        CreatedAt, StartedAt, FinishedAt, DurationMs);
}
