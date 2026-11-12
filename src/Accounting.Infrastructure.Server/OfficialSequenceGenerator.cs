using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Accounting.Infrastructure.Server;

public sealed record OfficialSequenceAllocation(string Number, long Sequence);

public sealed class OfficialSequenceGenerator
{
    private readonly ServerAccountingDbContext _db;
    public OfficialSequenceGenerator(ServerAccountingDbContext db) => _db = db;

    public async Task<OfficialSequenceAllocation> NextAsync(Guid companyId, Guid branchId, string documentType, DateTimeOffset documentDateUtc, CancellationToken cancellationToken = default)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Official sequence allocation requires an active PostgreSQL transaction.");
        if (companyId == Guid.Empty || branchId == Guid.Empty)
            throw new ArgumentException("A company and branch are required for official numbering.");
        if (string.IsNullOrWhiteSpace(documentType))
            throw new ArgumentException("Document type is required.", nameof(documentType));

        var year = documentDateUtc.UtcDateTime.Year;
        var lockKey = $"{companyId:N}:{branchId:N}:{documentType}:{year}";
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));",
            cancellationToken).ConfigureAwait(false);

        // The unique index and UPSERT allocate numbers atomically in the same
        // financial transaction. A rolled-back posting also rolls back the number.
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = """
INSERT INTO "SequenceCounters" ("Id", "CompanyId", "BranchId", "DocumentType", "SequenceYear", "CurrentValue")
VALUES (@id, @companyId, @branchId, @documentType, @year, 1)
ON CONFLICT ("CompanyId", "BranchId", "DocumentType", "SequenceYear")
DO UPDATE SET "CurrentValue" = "SequenceCounters"."CurrentValue" + 1
RETURNING "CurrentValue";
""";

        void AddParameter(string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        AddParameter("id", UuidV7.NewGuid());
        AddParameter("companyId", companyId);
        AddParameter("branchId", branchId);
        AddParameter("documentType", documentType);
        AddParameter("year", year);

        var allocated = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("PostgreSQL did not return an official sequence.");
        var sequence = Convert.ToInt64(allocated, System.Globalization.CultureInfo.InvariantCulture);
        return new OfficialSequenceAllocation($"{Prefix(documentType)}-{year}-{sequence:000000}", sequence);
    }

    // Official number allocation and COMMIT must be retried as one unit:
    // retrying only NextAsync would leave an aborted serializable transaction.
    public Task<OfficialSequenceAllocation> NextInTransactionAsync(
        Guid companyId, Guid branchId, string documentType, DateTimeOffset date,
        TenantPostgresSession session, CancellationToken cancellationToken = default) =>
        PostgresTransactionalRetry.ExecuteAsync(_db, async ct =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            await session.ApplyAsync(ct).ConfigureAwait(false);
            var allocation = await NextAsync(companyId, branchId, documentType, date, ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return allocation;
        }, cancellationToken);

    private static string Prefix(string documentType) => documentType switch
    {
        "SalesInvoice" => "INV",
        "PurchaseInvoice" => "PINV",
        "ReceiptVoucher" => "RCV",
        "PaymentVoucher" => "PAY",
        _ => "DOC"
    };
}
