using System.Globalization;
using System.Text;
using System.Text.Json;
using Accounting.Application;
using Accounting.Domain;
using Accounting.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Windows;

public sealed partial class WebErpServer
{
    private async Task<object?> SaveInvoiceAsync(WebInvoiceRequest x, bool post, CancellationToken ct)
    {
        var sales = x.Kind.Equals("sale", StringComparison.OrdinalIgnoreCase);
        if (!sales && !x.Kind.Equals("purchase", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("نوع الفاتورة غير صالح.");
        if (x.Lines.Count == 0) throw new InvalidOperationException("أضف صنفاً واحداً على الأقل.");
        if (x.Lines.Any(l => l.Quantity <= 0m || l.UnitPrice < 0m || l.Discount < 0m)) throw new InvalidOperationException("الكمية والسعر والخصم غير صالحين.");

        var policy = await _facade.GetPolicyAsync(ct).ConfigureAwait(false);
        var configuredTax = policy.TaxPolicy.Enabled ? policy.TaxPolicy.Rate * 100m : 0m;
        if (Math.Abs(configuredTax - x.TaxRate) > 0.0001m)
            throw new InvalidOperationException($"نسبة الضريبة يجب أن تطابق إعدادات الشركة ({configuredTax:N2}%).");

        Guid settlement = x.SettlementAccountId;
        if (x.PaymentMode.Equals("credit", StringComparison.OrdinalIgnoreCase))
        {
            if (!x.PartnerId.HasValue) throw new InvalidOperationException($"اختر {(sales ? "العميل" : "المورد")} عند البيع/الشراء الآجل.");
            var p = await _db.BusinessPartners.AsNoTracking().SingleAsync(z => z.Id == x.PartnerId && z.CompanyId == _user.CompanyId && z.BranchId == _user.BranchId, ct).ConfigureAwait(false);
            if ((sales && p.PartnerType == PartnerType.Supplier) || (!sales && p.PartnerType == PartnerType.Customer))
                throw new InvalidOperationException("نوع الطرف لا يطابق نوع الفاتورة.");
            settlement = p.ControlAccountId;
        }

        var unitRows = new Dictionary<Guid, Dictionary<string, object?>>();
        await using (var readCon = await OpenAsync(ct).ConfigureAwait(false))
        {
            foreach (var line in x.Lines)
            {
                var row = await QueryOneAsync(readCon, """
                    SELECT wu.Id id,wu.ItemId item_id,wu.UnitOfMeasureId unit_id,wu.Factor factor,wu.SalesPrice sales_price,i.Name item_name
                    FROM LocalWebItemUnits wu JOIN Items i ON i.Id=wu.ItemId
                    WHERE wu.Id=$unit AND wu.ItemId=$item AND i.CompanyId=$company AND i.BranchId=$branch
                    """, ct, ("$unit", line.ItemUnitId.ToString()), ("$item", line.ItemId.ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString())).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("وحدة الصنف غير موجودة.");
                unitRows[line.ItemUnitId] = row;
            }
        }

        var raw = x.Lines.Select(l =>
        {
            var before = l.Quantity * l.UnitPrice;
            if (l.Discount > before) throw new InvalidOperationException("خصم السطر أكبر من قيمته.");
            return before - l.Discount;
        }).ToArray();
        var beforeInvoiceDiscount = raw.Sum();
        if (x.Discount < 0m || x.Discount > beforeInvoiceDiscount) throw new InvalidOperationException("خصم الفاتورة غير صالح.");
        var effectiveTotals = raw.Select(v => beforeInvoiceDiscount == 0m ? 0m : v - (x.Discount * v / beforeInvoiceDiscount)).ToArray();
        var subtotal = effectiveTotals.Sum();
        var tax = effectiveTotals.Sum(v => policy.TaxPolicy.Enabled ? Math.Round(v * policy.TaxPolicy.Rate, policy.CurrencyScale, policy.RoundingMode) : 0m);
        var total = subtotal + tax;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        Guid documentId;
        try
        {
            await using var con = await OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(con, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                documentId = x.Id ?? UuidV7.NewGuid();
                var lineTable = sales ? "SalesInvoiceLines" : "PurchaseInvoiceLines";
                var parentColumn = sales ? "SalesInvoiceId" : "PurchaseInvoiceId";
                var documentType = sales ? nameof(SalesInvoice) : nameof(PurchaseInvoice);
                var exists = await ScalarLongAsync(con, "SELECT COUNT(*) FROM Documents WHERE Id=$id", ct, ("$id", documentId.ToString())).ConfigureAwait(false) > 0;
                string number;
                if (exists)
                {
                    var status = await ScalarLongAsync(con, "SELECT Status FROM Documents WHERE Id=$id", ct, ("$id", documentId.ToString())).ConfigureAwait(false);
                    if (status != 0) throw new InvalidOperationException("لا يمكن تعديل إلا مسودة الفاتورة.");
                    var existingType = await ScalarStringAsync(con, "SELECT DocumentType FROM Documents WHERE Id=$id", ct, ("$id", documentId.ToString())).ConfigureAwait(false);
                    if (!string.Equals(existingType, documentType, StringComparison.Ordinal)) throw new InvalidOperationException("نوع المسودة لا يطابق نوع الفاتورة.");
                    number = await ScalarStringAsync(con, "SELECT LocalTempNumber FROM Documents WHERE Id=$id", ct, ("$id", documentId.ToString())).ConfigureAwait(false) ?? "-";
                    await ExecuteAsync(con, $"DELETE FROM LocalWebDocumentLineMetadata WHERE DocumentId=$id; DELETE FROM {lineTable} WHERE {parentColumn}=$id;", ct, ("$id", documentId.ToString())).ConfigureAwait(false);
                    await ExecuteAsync(con, """
                        UPDATE Documents SET WarehouseId=$warehouse,SettlementAccountId=$settlement,BusinessPartnerId=$partner,ModuleKey=$module,ModuleMetadataJson=$metadata
                        WHERE Id=$id
                        """, ct, ("$warehouse", x.WarehouseId.ToString()), ("$settlement", settlement.ToString()), ("$partner", x.PartnerId?.ToString()),
                        ("$module", ModuleKey), ("$metadata", JsonSerializer.Serialize(new { x.PaymentMode, x.Notes })), ("$id", documentId.ToString())).ConfigureAwait(false);
                }
                else
                {
                    number = await _facade.NextWebDocumentNumberAsync(documentType, ct).ConfigureAwait(false);
                    await ExecuteAsync(con, """
                        INSERT INTO Documents(Id,CompanyId,BranchId,LocalTempNumber,ModuleKey,ModuleMetadataJson,Status,DocumentDateUtc,PostedAtUtc,PostingOperationId,PostingIdempotencyKey,DocumentType,BusinessPartnerId,WarehouseId,SettlementAccountId)
                        VALUES($id,$company,$branch,$number,$module,$metadata,0,$date,NULL,NULL,NULL,$type,$partner,$warehouse,$settlement)
                        """, ct, ("$id", documentId.ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                        ("$number", number), ("$module", ModuleKey), ("$metadata", JsonSerializer.Serialize(new { x.PaymentMode, x.Notes })),
                        ("$date", DateTimeOffset.UtcNow.UtcDateTime.Ticks), ("$type", documentType), ("$partner", x.PartnerId?.ToString()),
                        ("$warehouse", x.WarehouseId.ToString()), ("$settlement", settlement.ToString())).ConfigureAwait(false);
                }

                await ExecuteAsync(con, """
                    INSERT INTO LocalWebDocumentMetadata(DocumentId,CompanyId,BranchId,PaymentMode,InvoiceDiscount,TaxRatePercent,Subtotal,Tax,Total,Notes,UpdatedAtUtcTicks)
                    VALUES($id,$company,$branch,$payment,$discount,$rate,$subtotal,$tax,$total,$notes,$ticks)
                    ON CONFLICT(DocumentId) DO UPDATE SET PaymentMode=excluded.PaymentMode,InvoiceDiscount=excluded.InvoiceDiscount,TaxRatePercent=excluded.TaxRatePercent,
                    Subtotal=excluded.Subtotal,Tax=excluded.Tax,Total=excluded.Total,Notes=excluded.Notes,UpdatedAtUtcTicks=excluded.UpdatedAtUtcTicks
                    """, ct, ("$id", documentId.ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                    ("$payment", x.PaymentMode), ("$discount", Dec(x.Discount)), ("$rate", Dec(x.TaxRate)), ("$subtotal", Dec(subtotal)),
                    ("$tax", Dec(tax)), ("$total", Dec(total)), ("$notes", x.Notes ?? ""), ("$ticks", DateTimeOffset.UtcNow.UtcDateTime.Ticks)).ConfigureAwait(false);

                for (var index = 0; index < x.Lines.Count; index++)
                {
                    var l = x.Lines[index]; var u = unitRows[l.ItemUnitId];
                    var factor = Convert.ToDecimal(u["factor"], CultureInfo.InvariantCulture);
                    var effectivePrice = l.Quantity == 0m ? 0m : effectiveTotals[index] / l.Quantity;
                    if (effectivePrice < 0m) throw new InvalidOperationException("السعر الصافي للسطر غير صالح.");
                    var lineId = UuidV7.NewGuid();
                    await ExecuteAsync(con, $"""
                        INSERT INTO {lineTable}(Id,CompanyId,BranchId,{parentColumn},ItemId,UnitOfMeasureId,Quantity,BaseUnitsPerUnit,UnitPrice)
                        VALUES($id,$company,$branch,$document,$item,$unit,$qty,$factor,$price)
                        """, ct, ("$id", lineId.ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                        ("$document", documentId.ToString()), ("$item", l.ItemId.ToString()), ("$unit", (string)u["unit_id"]!),
                        ("$qty", Dec(l.Quantity)), ("$factor", Dec(factor)), ("$price", Dec(effectivePrice))).ConfigureAwait(false);
                    await ExecuteAsync(con, """
                        INSERT INTO LocalWebDocumentLineMetadata(LineId,DocumentId,ItemUnitId,DisplayUnitPrice,Discount,LineTotal,Factor)
                        VALUES($line,$document,$itemUnit,$price,$discount,$total,$factor)
                        """, ct, ("$line", lineId.ToString()), ("$document", documentId.ToString()), ("$itemUnit", l.ItemUnitId.ToString()),
                        ("$price", Dec(l.UnitPrice)), ("$discount", Dec(l.Discount)), ("$total", Dec(raw[index])), ("$factor", Dec(factor))).ConfigureAwait(false);
                }
                await ExecuteAsync(con, "COMMIT;", ct).ConfigureAwait(false);
                _db.ChangeTracker.Clear();
            }
            catch
            {
                await ExecuteAsync(con, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { _writeGate.Release(); }

        if (post)
        {
            var result = await _facade.PostExistingInvoiceAsync(documentId, sales, ct).ConfigureAwait(false);
            return new { id = documentId, number = await GetDocumentNumberAsync(documentId, ct).ConfigureAwait(false), status = "Posted", total, journalEntryId = result.JournalEntryId };
        }
        return new { id = documentId, subtotal, tax, total };
    }

    private async Task<object?> ListInvoicesAsync(string kind, string status, CancellationToken ct)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        var where = new StringBuilder("d.CompanyId=$company AND d.BranchId=$branch AND d.DocumentType IN ('SalesInvoice','PurchaseInvoice')");
        var args = new List<(string, object?)> { ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()) };
        if (!string.IsNullOrWhiteSpace(kind)) { where.Append(" AND d.DocumentType=$type"); args.Add(("$type", kind.Equals("sale", StringComparison.OrdinalIgnoreCase) ? nameof(SalesInvoice) : nameof(PurchaseInvoice))); }
        if (!string.IsNullOrWhiteSpace(status)) { where.Append(" AND d.Status=$status"); args.Add(("$status", status.Equals("Posted", StringComparison.OrdinalIgnoreCase) ? 1 : 0)); }
        var docs = await QueryAsync(con, $"""
            SELECT d.Id id,d.LocalTempNumber number,d.DocumentType,d.Status,d.DocumentDateUtc date,d.WarehouseId warehouse_id,
                   d.SettlementAccountId settlement_account_id,d.BusinessPartnerId partner_id,p.Name partner_name,w.Name warehouse_name,a.Name settlement_name,
                   COALESCE(m.PaymentMode,'cash') payment_mode,COALESCE(m.InvoiceDiscount,'0') discount,COALESCE(m.TaxRatePercent,'0') tax_rate,
                   COALESCE(m.Subtotal,'0') subtotal,COALESCE(m.Tax,'0') tax,COALESCE(m.Total,'0') total,COALESCE(m.Notes,'') notes,
                   CASE WHEN r.Id IS NULL THEN 0 ELSE 1 END reversed
            FROM Documents d LEFT JOIN LocalWebDocumentMetadata m ON m.DocumentId=d.Id
            LEFT JOIN DocumentReversalRecords r ON r.SourceDocumentId=d.Id
            LEFT JOIN BusinessPartners p ON p.Id=d.BusinessPartnerId LEFT JOIN Warehouses w ON w.Id=d.WarehouseId LEFT JOIN Accounts a ON a.Id=d.SettlementAccountId
            WHERE {where} ORDER BY d.DocumentDateUtc DESC,d.Id DESC
            """, ct, args.ToArray()).ConfigureAwait(false);
        foreach (var d in docs)
        {
            d["kind"] = string.Equals((string?)d["DocumentType"], nameof(SalesInvoice), StringComparison.Ordinal) ? "sale" : "purchase";
            d["status"] = Convert.ToInt64(d["reversed"] ?? 0, CultureInfo.InvariantCulture) == 1 ? "Reversed" : (Convert.ToInt64(d["Status"], CultureInfo.InvariantCulture) == 1 ? "Posted" : "Draft");
            d["date"] = TicksToIso(d["date"]); d.Remove("DocumentType"); d.Remove("Status"); d.Remove("reversed");
            var table = (string)d["kind"]! == "sale" ? "SalesInvoiceLines" : "PurchaseInvoiceLines";
            var parent = (string)d["kind"]! == "sale" ? "SalesInvoiceId" : "PurchaseInvoiceId";
            d["lines"] = await QueryAsync(con, $"""
                SELECT l.Id id,l.ItemId item_id,wm.ItemUnitId item_unit_id,l.Quantity quantity,COALESCE(wm.DisplayUnitPrice,l.UnitPrice) unit_price,
                       COALESCE(wm.Discount,'0') discount,l.BaseUnitsPerUnit factor,COALESCE(wm.LineTotal,(CAST(l.Quantity AS REAL)*CAST(l.UnitPrice AS REAL))) line_total,
                       i.Name item_name,i.Sku sku,u.Name unit_name,wu.Barcode barcode
                FROM {table} l JOIN Items i ON i.Id=l.ItemId JOIN UnitOfMeasures u ON u.Id=l.UnitOfMeasureId
                LEFT JOIN LocalWebDocumentLineMetadata wm ON wm.LineId=l.Id LEFT JOIN LocalWebItemUnits wu ON wu.Id=wm.ItemUnitId
                WHERE l.{parent}=$id ORDER BY l.Id
                """, ct, ("$id", d["id"]!.ToString())).ConfigureAwait(false);
        }
        return docs;
    }

    private async Task<object?> ReverseDocumentAsync(Guid id, CancellationToken ct)
    {
        var result = await _facade.ReverseDocumentAsync(id, ct).ConfigureAwait(false);
        return new { reversed = true, journalEntryId = result.JournalEntryId, operationId = result.OperationId };
    }

    private async Task<object?> SaveVoucherAsync(WebVoucherRequest x, CancellationToken ct)
    {
        var receipt = x.Kind.Equals("receipt", StringComparison.OrdinalIgnoreCase);
        if (!receipt && !x.Kind.Equals("payment", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("نوع السند غير صالح.");
        if (x.Amount <= 0m) throw new InvalidOperationException("المبلغ يجب أن يكون أكبر من صفر.");
        var documentType = receipt ? nameof(ReceiptVoucher) : nameof(PaymentVoucher); var id = x.Id ?? UuidV7.NewGuid();

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var con = await OpenAsync(ct).ConfigureAwait(false);
            var exists = await ScalarLongAsync(con, "SELECT COUNT(*) FROM Documents WHERE Id=$id", ct, ("$id", id.ToString())).ConfigureAwait(false) > 0;
            if (exists)
            {
                var st = await ScalarLongAsync(con, "SELECT Status FROM Documents WHERE Id=$id", ct, ("$id", id.ToString())).ConfigureAwait(false);
                if (st != 0) throw new InvalidOperationException("لا يمكن تعديل إلا مسودة السند.");
                await ExecuteAsync(con, """
                    UPDATE Documents SET CashOrBankAccountId=$cash,CounterAccountId=$counter,BusinessPartnerId=$partner,VoucherAmount=$amount,VoucherDescription=$description WHERE Id=$id
                    """, ct, ("$cash", x.CashAccountId.ToString()), ("$counter", x.CounterAccountId.ToString()), ("$partner", x.PartnerId?.ToString()),
                    ("$amount", Dec(x.Amount)), ("$description", x.Description ?? ""), ("$id", id.ToString())).ConfigureAwait(false);
            }
            else
            {
                var number = await _facade.NextWebDocumentNumberAsync(documentType, ct).ConfigureAwait(false);
                await ExecuteAsync(con, """
                    INSERT INTO Documents(Id,CompanyId,BranchId,LocalTempNumber,ModuleKey,ModuleMetadataJson,Status,DocumentDateUtc,PostedAtUtc,PostingOperationId,PostingIdempotencyKey,DocumentType,
                                          CashOrBankAccountId,CounterAccountId,BusinessPartnerId,VoucherAmount,VoucherDescription)
                    VALUES($id,$company,$branch,$number,'CORE','{}',0,$date,NULL,NULL,NULL,$type,$cash,$counter,$partner,$amount,$description)
                    """, ct, ("$id", id.ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                    ("$number", number), ("$date", DateTimeOffset.UtcNow.UtcDateTime.Ticks), ("$type", documentType),
                    ("$cash", x.CashAccountId.ToString()), ("$counter", x.CounterAccountId.ToString()), ("$partner", x.PartnerId?.ToString()),
                    ("$amount", Dec(x.Amount)), ("$description", x.Description ?? "")).ConfigureAwait(false);
            }
            _db.ChangeTracker.Clear();
        }
        finally { _writeGate.Release(); }

        if (!x.Draft)
        {
            var posted = await _facade.PostExistingVoucherAsync(id, receipt, ct).ConfigureAwait(false);
            return new { id, status = "Posted", journalEntryId = posted.JournalEntryId };
        }
        return new { id, status = "Draft" };
    }

    private async Task<object?> ListVouchersAsync(CancellationToken ct)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        var data = await QueryAsync(con, """
            SELECT d.Id id,d.LocalTempNumber number,d.DocumentType,d.Status,d.DocumentDateUtc date,d.CashOrBankAccountId cash_account_id,
                   d.CounterAccountId counter_account_id,d.BusinessPartnerId partner_id,d.VoucherAmount amount,d.VoucherDescription description,
                   CASE WHEN r.Id IS NULL THEN 0 ELSE 1 END reversed,p.Name partner_name,ca.Name cash_name,co.Name counter_name
            FROM Documents d LEFT JOIN DocumentReversalRecords r ON r.SourceDocumentId=d.Id LEFT JOIN BusinessPartners p ON p.Id=d.BusinessPartnerId
            JOIN Accounts ca ON ca.Id=d.CashOrBankAccountId JOIN Accounts co ON co.Id=d.CounterAccountId
            WHERE d.CompanyId=$company AND d.BranchId=$branch AND d.DocumentType IN ('ReceiptVoucher','PaymentVoucher')
            ORDER BY d.DocumentDateUtc DESC,d.Id DESC
            """, ct, ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString())).ConfigureAwait(false);
        foreach (var row in data)
        {
            row["kind"] = string.Equals((string?)row["DocumentType"], nameof(ReceiptVoucher), StringComparison.Ordinal) ? "receipt" : "payment";
            row["status"] = Convert.ToInt64(row["reversed"] ?? 0, CultureInfo.InvariantCulture) == 1 ? "Reversed" : (Convert.ToInt64(row["Status"], CultureInfo.InvariantCulture) == 1 ? "Posted" : "Draft");
            row["date"] = TicksToIso(row["date"]); row.Remove("DocumentType"); row.Remove("Status"); row.Remove("reversed");
        }
        return data;
    }

    private async Task<object?> ListJournalsAsync(CancellationToken ct)
    {
        var headers = await _facade.GetRecentJournalsAsync(250, ct).ConfigureAwait(false); var list = new List<object>();
        foreach (var j in headers.Take(250))
        {
            var lines = await _facade.GetJournalDetailsAsync(j.Id, ct).ConfigureAwait(false);
            list.Add(new
            {
                id = j.Id, date = j.DateUtc, reference = j.Reference, description = j.Description, source_type = j.SourceType, source_id = j.SourceDocumentId,
                lines = lines.Select(l => new { id = l.Id, account_id = l.AccountId, account_code = l.AccountCode, account_name = l.AccountName, memo = l.Memo, debit = l.Debit, credit = l.Credit }).ToArray()
            });
        }
        return list;
    }

    private async Task<object?> SaveJournalAsync(WebJournalRequest x, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(x.Description)) throw new InvalidOperationException("بيان القيد مطلوب.");
        if (x.Lines.Count < 2) throw new InvalidOperationException("أدخل سطرين على الأقل.");
        var lines = x.Lines.Select(l => new ManualJournalLine(l.AccountId, l.Debit, l.Credit, l.Memo ?? "")).ToArray();
        var id = await _facade.CreateManualJournalAsync(x.Description, x.Reference ?? "", lines, ct).ConfigureAwait(false);
        return new { id };
    }

    private async Task<object?> ReverseJournalAsync(Guid id, CancellationToken ct)
    {
        var headers = await _facade.GetRecentJournalsAsync(ct).ConfigureAwait(false);
        var j = headers.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("القيد غير موجود.");
        if (!j.SourceType.Equals("ManualJournal", StringComparison.OrdinalIgnoreCase) && j.SourceDocumentId != Guid.Empty)
            return await ReverseDocumentAsync(j.SourceDocumentId, ct).ConfigureAwait(false);
        var details = await _facade.GetJournalDetailsAsync(id, ct).ConfigureAwait(false);
        var reverse = details.Select(l => new ManualJournalLine(l.AccountId, l.Credit, l.Debit, $"عكس: {l.Memo}")).ToArray();
        var rid = await _facade.CreateManualJournalAsync($"عكس القيد: {j.Description}", $"REV-{j.Reference}", reverse, ct).ConfigureAwait(false);
        return new { id = rid };
    }

    private async Task<string> GetDocumentNumberAsync(Guid id, CancellationToken ct)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        return await ScalarStringAsync(con, "SELECT LocalTempNumber FROM Documents WHERE Id=$id", ct, ("$id", id.ToString())).ConfigureAwait(false) ?? id.ToString("N");
    }
}