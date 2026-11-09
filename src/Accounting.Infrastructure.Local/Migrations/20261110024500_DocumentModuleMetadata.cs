using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261110024500_DocumentModuleMetadata")]
public sealed class DocumentModuleMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ModuleMetadataJson",
            table: "Documents",
            type: "TEXT",
            nullable: false,
            defaultValue: "{}");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ModuleMetadataJson", table: "Documents");
    }
}
