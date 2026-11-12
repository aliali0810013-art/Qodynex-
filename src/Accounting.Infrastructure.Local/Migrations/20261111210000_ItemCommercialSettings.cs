using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261111210000_ItemCommercialSettings")]
public sealed class ItemCommercialSettingsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LocalItemCommercialSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                BaseSalesPrice = table.Column<decimal>(type: "TEXT", precision: 38, scale: 12, nullable: false),
                OpeningUnitCost = table.Column<decimal>(type: "TEXT", precision: 38, scale: 12, nullable: false),
                CartonUnitId = table.Column<Guid>(type: "TEXT", nullable: true),
                CartonBarcode = table.Column<string>(type: "TEXT", nullable: true),
                CartonSalesPrice = table.Column<decimal>(type: "TEXT", precision: 38, scale: 12, nullable: false),
                UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_LocalItemCommercialSettings", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_LocalItemCommercialSettings_Tenant_Item",
            table: "LocalItemCommercialSettings",
            columns: new[] { "CompanyId", "BranchId", "ItemId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LocalItemCommercialSettings_Tenant_CartonBarcode",
            table: "LocalItemCommercialSettings",
            columns: new[] { "CompanyId", "BranchId", "CartonBarcode" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "LocalItemCommercialSettings");
}
