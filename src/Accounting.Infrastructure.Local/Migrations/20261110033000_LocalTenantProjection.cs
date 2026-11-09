using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261110033000_LocalTenantProjection")]
public sealed class LocalTenantProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "CompanyName", table: "LocalSyncStates", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "BranchName", table: "LocalSyncStates", type: "TEXT", nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "EnabledModulesCsv", table: "LocalSyncStates", type: "TEXT", nullable: false, defaultValue: "RETAIL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CompanyName", table: "LocalSyncStates");
        migrationBuilder.DropColumn(name: "BranchName", table: "LocalSyncStates");
        migrationBuilder.DropColumn(name: "EnabledModulesCsv", table: "LocalSyncStates");
    }
}
