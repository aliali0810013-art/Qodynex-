using Accounting.Application;
using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.UI.Windows;

public sealed partial class WindowsAccountingFacade
{
    internal UserContext WebUserContext => _user;

    internal Task<string> NextWebDocumentNumberAsync(string documentType, CancellationToken ct = default) =>
        _numbers.NextAsync(_user, documentType, ct);

    internal Task<PostingResult> PostExistingInvoiceAsync(Guid documentId, bool sales, CancellationToken ct = default)
    {
        var context = new PostingCommandContext(_user, UuidV7.NewGuid(), $"web:{(sales ? "sales" : "purchase")}:{documentId:N}:{UuidV7.NewGuid():N}");
        return sales
            ? _posting.PostSalesInvoiceAsync(new PostSalesInvoiceCommand(documentId, context), ct)
            : _posting.PostPurchaseInvoiceAsync(new PostPurchaseInvoiceCommand(documentId, context), ct);
    }

    internal Task<PostingResult> PostExistingVoucherAsync(Guid documentId, bool receipt, CancellationToken ct = default)
    {
        var context = new PostingCommandContext(_user, UuidV7.NewGuid(), $"web:{(receipt ? "receipt" : "payment")}:{documentId:N}:{UuidV7.NewGuid():N}");
        return receipt
            ? _posting.PostReceiptVoucherAsync(new PostReceiptVoucherCommand(documentId, context), ct)
            : _posting.PostPaymentVoucherAsync(new PostPaymentVoucherCommand(documentId, context), ct);
    }
}