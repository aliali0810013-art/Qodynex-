using System.Data;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Infrastructure.Local;

public sealed class LocalTempNumberGenerator
{
    private readonly LocalAccountingDbContext _db;
    public LocalTempNumberGenerator(LocalAccountingDbContext db) => _db = db;

    public async Task<string> NextAsync(UserContext userContext, string documentType, CancellationToken cancellationToken = default)
    {
        if (!userContext.IsAuthenticated || string.IsNullOrWhiteSpace(documentType))
            throw new InvalidOperationException("Authenticated user context and document type are required.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var counter = await _db.LocalSequenceCounters.SingleOrDefaultAsync(
            x => x.CompanyId == userContext.CompanyId && x.BranchId == userContext.BranchId
                 && x.DeviceId == userContext.DeviceId && x.DocumentType == documentType,
            cancellationToken).ConfigureAwait(false);

        if (counter is null)
        {
            counter = new LocalSequenceCounter
            {
                Id = UuidV7.NewGuid(),
                CompanyId = userContext.CompanyId,
                BranchId = userContext.BranchId,
                DeviceId = userContext.DeviceId,
                DocumentType = documentType,
                CurrentValue = 0
            };
            _db.LocalSequenceCounters.Add(counter);
        }

        checked { counter.CurrentValue++; }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        var device = userContext.DeviceId.ToString("N")[..6].ToUpperInvariant();
        return $"LOC-{device}-{counter.CurrentValue:000000}";
    }
}
