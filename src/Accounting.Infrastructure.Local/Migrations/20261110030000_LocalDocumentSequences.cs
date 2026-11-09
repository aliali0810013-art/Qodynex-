using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261110030000_LocalDocumentSequences")]
public sealed class LocalDocumentSequences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LocalSequenceCounters",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentType = table.Column<string>(type: "TEXT", nullable: false),
                CurrentValue = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_LocalSequenceCounters", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_LocalSequenceCounters_Tenant_Device_Document",
            table: "LocalSequenceCounters",
            columns: new[] { "CompanyId", "BranchId", "DeviceId", "DocumentType" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "LocalSequenceCounters");
}
