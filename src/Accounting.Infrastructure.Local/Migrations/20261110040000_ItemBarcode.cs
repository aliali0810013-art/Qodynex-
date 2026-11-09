using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261110040000_ItemBarcode")]
public sealed class ItemBarcode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Barcode", table: "Items", type: "TEXT", nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_Items_CompanyId_BranchId_Barcode",
            table: "Items",
            columns: new[] { "CompanyId", "BranchId", "Barcode" },
            unique: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Items_CompanyId_BranchId_Barcode", table: "Items");
        migrationBuilder.DropColumn(name: "Barcode", table: "Items");
    }
}
