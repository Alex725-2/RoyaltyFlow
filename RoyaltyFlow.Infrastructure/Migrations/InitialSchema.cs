using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RoyaltyFlow.Infrastructure;

[DbContext(typeof(RoyaltyDbContext))]
[Migration("20261121000100_InitialSchema")]
public sealed class InitialSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var resource = typeof(InitialSchema).Assembly.GetManifestResourceStream("RoyaltyFlow.InitialSchema.sql")!;
        using var reader = new StreamReader(resource);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            DROP TABLE royalty_accruals, usage_records, usage_imports, royalty_shares,
                royalty_contracts, rights_holders, tracks, artists;
            """);

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => InitialModel.Configure(modelBuilder);
}

[DbContext(typeof(RoyaltyDbContext))]
public sealed class RoyaltyDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => InitialModel.Configure(modelBuilder);
}
