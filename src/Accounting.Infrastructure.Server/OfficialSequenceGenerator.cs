using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

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
        if (string.IsNullOrWhiteSpace(documentType))
            throw new ArgumentException("Document type is required.", nameof(documentType));

        var year = documentDateUtc.UtcDateTime.Year;
        var lockKey = $"{companyId:N}:{branchId:N}:{documentType}:{year}";
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0));",
            cancellationToken).ConfigureAwait(false);

        var counter = await _db.SequenceCounters.SingleOrDefaultAsync(
            x => x.CompanyId == companyId && x.BranchId == branchId && x.DocumentType == documentType && x.SequenceYear == year,
            cancellationToken).ConfigureAwait(false);

        if (counter is null)
        {
            counter = new SequenceCounter
            {
                Id = UuidV7.NewGuid(),
                CompanyId = companyId,
                BranchId = branchId,
                DocumentType = documentType,
                SequenceYear = year,
                CurrentValue = 0
            };
            _db.SequenceCounters.Add(counter);
        }

        checked { counter.CurrentValue++; }
        var prefix = Prefix(documentType);
        return new OfficialSequenceAllocation($"{prefix}-{year}-{counter.CurrentValue:000000}", counter.CurrentValue);
    }

    private static string Prefix(string documentType) => documentType switch
    {
        "SalesInvoice" => "INV",
        "PurchaseInvoice" => "PINV",
        "ReceiptVoucher" => "RCV",
        "PaymentVoucher" => "PAY",
        _ => "DOC"
    };
}
