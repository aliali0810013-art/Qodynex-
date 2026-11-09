using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261110021500_LocalSyncState")]
public sealed class LocalSyncStateMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LocalSyncStates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                LastPulledWatermark = table.Column<long>(type: "INTEGER", nullable: false),
                LastSuccessfulSyncUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_LocalSyncStates", x => x.Id));

        migrationBuilder.CreateTable(
            name: "LocalSyncConflicts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                ConflictCode = table.Column<string>(type: "TEXT", nullable: false),
                Message = table.Column<string>(type: "TEXT", nullable: false),
                ServerPayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                ServerTimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                IsResolved = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_LocalSyncConflicts", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_LocalSyncStates_CompanyId_BranchId_DeviceId",
            table: "LocalSyncStates",
            columns: new[] { "CompanyId", "BranchId", "DeviceId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LocalSyncConflicts_CompanyId_BranchId_OperationId",
            table: "LocalSyncConflicts",
            columns: new[] { "CompanyId", "BranchId", "OperationId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "LocalSyncConflicts");
        migrationBuilder.DropTable(name: "LocalSyncStates");
    }
}
