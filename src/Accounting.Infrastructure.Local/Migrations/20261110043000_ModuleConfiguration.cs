using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261110043000_ModuleConfiguration")]
public sealed class ModuleConfigurationMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LocalModuleConfigurations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                ModuleKey = table.Column<string>(type: "TEXT", nullable: false),
                ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_LocalModuleConfigurations", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_LocalModuleConfigurations_Tenant_Module",
            table: "LocalModuleConfigurations",
            columns: new[] { "CompanyId", "BranchId", "ModuleKey" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "LocalModuleConfigurations");
}
