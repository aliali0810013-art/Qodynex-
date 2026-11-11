using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Accounting.Infrastructure.Local.Migrations;

[DbContext(typeof(LocalAccountingDbContext))]
[Migration("20261111193000_CompanyBrandingSettings")]
public sealed class CompanyBrandingSettingsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LocalCompanyBrandingSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                ArabicName = table.Column<string>(type: "TEXT", nullable: false),
                EnglishName = table.Column<string>(type: "TEXT", nullable: false),
                BusinessDescription = table.Column<string>(type: "TEXT", nullable: false),
                LogoBytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                LogoFileName = table.Column<string>(type: "TEXT", nullable: true),
                LogoMimeType = table.Column<string>(type: "TEXT", nullable: true),
                City = table.Column<string>(type: "TEXT", nullable: false),
                Address = table.Column<string>(type: "TEXT", nullable: false),
                Phones = table.Column<string>(type: "TEXT", nullable: false),
                Email = table.Column<string>(type: "TEXT", nullable: false),
                TaxNumber = table.Column<string>(type: "TEXT", nullable: false),
                CommercialRegistration = table.Column<string>(type: "TEXT", nullable: false),
                FooterNotes = table.Column<string>(type: "TEXT", nullable: false),
                CurrencyCode = table.Column<string>(type: "TEXT", nullable: false),
                CurrencySymbol = table.Column<string>(type: "TEXT", nullable: false),
                TaxEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                TaxRate = table.Column<decimal>(type: "TEXT", precision: 38, scale: 12, nullable: false),
                AllowNegativeStock = table.Column<bool>(type: "INTEGER", nullable: false),
                DarkThemeEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_LocalCompanyBrandingSettings", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_LocalCompanyBrandingSettings_Tenant",
            table: "LocalCompanyBrandingSettings",
            columns: new[] { "CompanyId", "BranchId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "LocalCompanyBrandingSettings");
}
