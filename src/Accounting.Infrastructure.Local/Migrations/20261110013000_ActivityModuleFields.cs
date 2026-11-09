using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261110013000_ActivityModuleFields")]
public sealed class ActivityModuleFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "CreditLimit",
            table: "BusinessPartners",
            type: "TEXT",
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<int>(
            name: "ItemKind",
            table: "Items",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CreditLimit", table: "BusinessPartners");
        migrationBuilder.DropColumn(name: "ItemKind", table: "Items");
    }
}
