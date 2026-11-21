using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using RoyaltyFlow.Domain;

namespace RoyaltyFlow.Infrastructure;

// Keep this historical mapping fixed when introducing a later schema version.
internal static class InitialModel
{
    public static void Configure(ModelBuilder model)
    {
        model.HasPostgresExtension("btree_gist");
        MapScalars<Artist>(model);
        MapScalars<Track>(model);
        MapScalars<RightsHolder>(model);
        MapScalars<ContractRow>(model, nameof(ContractRow.Shares));
        MapScalars<ShareRow>(model);
        MapScalars<ImportRow>(model);
        MapScalars<UsageRecord>(model);
        MapScalars<RoyaltyAccrual>(model);
        model.Entity<Artist>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.ToTable("artists", table => table.HasCheckConstraint("artists_name_check", "btrim(name) <> ''"));
            entity.Property(row => row.Name).HasMaxLength(200).IsRequired();
        });
        model.Entity<Track>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.ToTable("tracks", table => table.HasCheckConstraint("tracks_title_check", "btrim(title) <> ''"));
            entity.Property(row => row.Title).HasMaxLength(200).IsRequired();
            entity.HasOne<Artist>().WithMany().HasForeignKey(row => row.ArtistId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(row => row.ArtistId).HasDatabaseName("ix_tracks_artist_id");
        });
        model.Entity<RightsHolder>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.ToTable("rights_holders", table => table.HasCheckConstraint("rights_holders_name_check", "btrim(name) <> ''"));
            entity.Property(row => row.Name).HasMaxLength(200).IsRequired();
        });
        model.Entity<ContractRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.ToTable("royalty_contracts", table => table.HasCheckConstraint("ck_contract_period",
                "valid_to IS NULL OR valid_to > valid_from"));
            entity.HasOne<Track>().WithMany().HasForeignKey(row => row.TrackId).OnDelete(DeleteBehavior.NoAction);
            entity.HasMany(row => row.Shares).WithOne().HasForeignKey(row => row.ContractId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(row => row.TrackId).HasDatabaseName("ix_royalty_contracts_track_id");
        });
        model.Entity<ShareRow>(entity =>
        {
            entity.ToTable("royalty_shares", table => table.HasCheckConstraint("royalty_shares_percentage_check",
                "percentage > 0 AND percentage <= 100"));
            entity.HasKey(row => new { row.ContractId, row.RightsHolderId });
            entity.HasOne<RightsHolder>().WithMany().HasForeignKey(row => row.RightsHolderId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(row => row.RightsHolderId).HasDatabaseName("ix_royalty_shares_rights_holder_id");
            entity.Property(row => row.Percentage).HasPrecision(7, 4);
        });
        model.Entity<ImportRow>(entity =>
        {
            entity.HasKey(row => row.Id);
            entity.ToTable("usage_imports", table =>
            {
                table.HasCheckConstraint("usage_imports_sha256_check", "sha256 ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("usage_imports_status_check", "status IN ('Pending','Processing','Completed','Failed')");
                table.HasCheckConstraint("ck_import_statistics", "total >= 0 AND inserted >= 0 AND duplicate >= 0 AND invalid >= 0 AND total = inserted + duplicate + invalid");
                table.HasCheckConstraint("ck_import_timing", """
                    (status = 'Pending' AND started_at IS NULL AND finished_at IS NULL AND duration_ms IS NULL)
                    OR (status = 'Processing' AND started_at IS NOT NULL AND finished_at IS NULL AND duration_ms IS NULL)
                    OR (status IN ('Completed', 'Failed') AND started_at IS NOT NULL AND finished_at IS NOT NULL
                        AND duration_ms IS NOT NULL AND duration_ms >= 0)
                    """);
            });
            entity.Property(row => row.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Status).HasConversion<string>().HasMaxLength(10);
            entity.HasIndex(row => row.Sha256).IsUnique().HasDatabaseName("usage_imports_sha256_key");
            entity.HasIndex(row => new { row.CreatedAt, row.Id }).IsDescending().HasDatabaseName("ix_usage_imports_created_id");
            entity.Property(row => row.CreatedAt).HasDefaultValueSql("clock_timestamp()");
            entity.Property(row => row.CreatedAt).HasColumnType("timestamptz");
            entity.Property(row => row.StartedAt).HasColumnType("timestamptz");
            entity.Property(row => row.FinishedAt).HasColumnType("timestamptz");
            entity.Property(row => row.Total).HasDefaultValue(0L);
            entity.Property(row => row.Inserted).HasDefaultValue(0L);
            entity.Property(row => row.Duplicate).HasDefaultValue(0L);
            entity.Property(row => row.Invalid).HasDefaultValue(0L);
        });
        model.Entity<UsageRecord>(entity =>
        {
            entity.ToTable("usage_records", table =>
            {
                table.HasCheckConstraint("usage_records_platform_check", "btrim(platform) <> ''");
                table.HasCheckConstraint("usage_records_country_check", "country ~ '^[A-Z]{2}$'");
                table.HasCheckConstraint("usage_records_play_count_check", "play_count BETWEEN 0 AND 1000000000");
            });
            entity.HasKey(row => row.EventId);
            entity.Property<Guid>("ImportId");
            entity.HasOne<ImportRow>().WithMany().HasForeignKey("ImportId").OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<Track>().WithMany().HasForeignKey(row => row.TrackId).OnDelete(DeleteBehavior.NoAction);
            entity.Property(row => row.Platform).HasMaxLength(64).IsRequired();
            entity.Property(row => row.Country).HasMaxLength(2).IsRequired();
            entity.HasIndex(row => new { row.UsageDate, row.EventId }).IsDescending().HasDatabaseName("ix_usage_records_date_event");
            entity.HasIndex(row => new { row.TrackId, row.UsageDate, row.EventId }).IsDescending(false, true, true)
                .HasDatabaseName("ix_usage_records_track_date_event");
            entity.HasIndex("ImportId", nameof(UsageRecord.EventId)).HasDatabaseName("ix_usage_records_import_event");
        });
        model.Entity<RoyaltyAccrual>(entity =>
        {
            entity.ToTable("royalty_accruals", table => table.HasCheckConstraint("royalty_accruals_amount_check",
                "amount >= 0 AND amount <= 92233720368547758.07"));
            entity.HasKey(row => new { row.EventId, row.ContractId, row.RightsHolderId });
            entity.HasOne<UsageRecord>().WithMany().HasForeignKey(row => row.EventId).OnDelete(DeleteBehavior.NoAction);
            entity.HasOne<ShareRow>().WithMany().HasForeignKey(row => new { row.ContractId, row.RightsHolderId })
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(row => new { row.ContractId, row.RightsHolderId }).HasDatabaseName("ix_royalty_accruals_contract_holder");
            entity.Property(row => row.Amount).HasPrecision(19, 2);
            entity.Property<DateTime>("CreatedAt").HasColumnType("timestamptz").HasDefaultValueSql("clock_timestamp()");
        });

        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(string.Concat(property.Name.Select((character, index) =>
                    index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString())));
            foreach (var key in entity.GetKeys())
            {
                key.SetName(entity.GetTableName() + "_pkey");
                foreach (var property in key.Properties.Where(property => property.ClrType == typeof(Guid)))
                    property.ValueGenerated = ValueGenerated.Never;
            }
            foreach (var foreignKey in entity.GetForeignKeys())
                foreignKey.SetConstraintName($"{entity.GetTableName()}_{string.Join("_", foreignKey.Properties.Select(property => property.GetColumnName()))}_fkey");
        }
    }

    private static void MapScalars<TEntity>(ModelBuilder model, string? navigation = null) where TEntity : class
    {
        foreach (var property in typeof(TEntity).GetProperties().Where(property => property.Name != navigation))
            model.Entity<TEntity>().Property(property.PropertyType, property.Name);
    }
}
