using System.Globalization;
using Accounting.Application;
using Accounting.Domain;
using Accounting.SharedKernel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Accounting.UI.Windows;

public sealed partial class WebErpServer
{
    private async Task<object?> ListItemsAsync(string q, CancellationToken ct)
    {
        q = q.Trim();
        var items = await _db.Items.AsNoTracking()
            .Where(i => i.CompanyId == _user.CompanyId && i.BranchId == _user.BranchId && i.IsActive
                     && (q == "" || i.Sku.Contains(q) || i.Name.Contains(q) || (i.Barcode != null && i.Barcode.Contains(q))))
            .OrderBy(i => i.Name).Take(100).ToListAsync(ct).ConfigureAwait(false);
        var wh = (await _facade.GetWarehousesAsync(ct).ConfigureAwait(false)).FirstOrDefault();
        var result = new List<object>();
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            var baseUnit = await _db.UnitOfMeasures.AsNoTracking().Where(u => u.Id == item.BaseUnitOfMeasureId).Select(u => u.Name).SingleAsync(ct).ConfigureAwait(false);
            var units = await QueryAsync(con, """
                SELECT wu.Id id,wu.ItemId item_id,wu.UnitOfMeasureId unit_id,wu.Factor factor,wu.Barcode barcode,
                       wu.SalesPrice sales_price,wu.OpeningCost opening_cost,wu.IsBase is_base,u.Code unit_code,u.Name unit_name
                FROM LocalWebItemUnits wu JOIN UnitOfMeasures u ON u.Id=wu.UnitOfMeasureId
                WHERE wu.ItemId=$item ORDER BY CAST(wu.Factor AS REAL)
                """, ct, ("$item", item.Id.ToString())).ConfigureAwait(false);
            foreach (var unit in units)
            {
                unit["factor"] = DbDecimal(unit["factor"]);
                unit["sales_price"] = DbDecimal(unit["sales_price"]);
                unit["opening_cost"] = DbDecimal(unit["opening_cost"]);
                unit["is_base"] = Convert.ToInt64(unit["is_base"], CultureInfo.InvariantCulture) == 1;
            }
            var pos = wh is null ? null : await _facade.GetStockPositionAsync(item.Id, wh.Id, ct).ConfigureAwait(false);
            result.Add(new
            {
                id = item.Id, sku = item.Sku, name = item.Name, base_unit_id = item.BaseUnitOfMeasureId, base_unit_name = baseUnit,
                inventory_account_id = item.InventoryAccountId, sales_account_id = item.SalesRevenueAccountId, cogs_account_id = item.CogsAccountId,
                units, stock = pos is null ? new { quantity = 0m, value = 0m, averageCost = 0m }
                    : new { quantity = pos.QuantityOnHand, value = pos.InventoryValue, averageCost = pos.AverageUnitCost }
            });
        }
        return result;
    }

    private async Task<object?> ScanItemAsync(string barcode, CancellationToken ct)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        var row = await QueryOneAsync(con, """
            SELECT i.Id item_id,i.Sku sku,i.Name name,wu.Id item_unit_id,wu.UnitOfMeasureId unit_id,
                   wu.Factor factor,wu.Barcode barcode,wu.SalesPrice sales_price,u.Name unit_name
            FROM LocalWebItemUnits wu JOIN Items i ON i.Id=wu.ItemId JOIN UnitOfMeasures u ON u.Id=wu.UnitOfMeasureId
            WHERE wu.Barcode=$barcode AND i.IsActive=1 AND i.CompanyId=$company AND i.BranchId=$branch
            """, ct, ("$barcode", barcode.Trim()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString())).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"الباركود غير موجود: {barcode}");
        row["factor"] = DbDecimal(row["factor"]);
        row["sales_price"] = DbDecimal(row["sales_price"]);
        var itemId = Guid.Parse((string)row["item_id"]!);
        var wh = (await _facade.GetWarehousesAsync(ct).ConfigureAwait(false)).First();
        var pos = await _facade.GetStockPositionAsync(itemId, wh.Id, ct).ConfigureAwait(false);
        row["stock"] = new { quantity = pos.QuantityOnHand, value = pos.InventoryValue, averageCost = pos.AverageUnitCost };
        return row;
    }

    private async Task<object?> SaveItemAsync(WebItemRequest x, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(x.Sku) || string.IsNullOrWhiteSpace(x.Name)) throw new InvalidOperationException("كود الصنف والاسم مطلوبان.");
        if (x.Units.Count == 0 || x.Units.Count(u => u.IsBase) != 1) throw new InvalidOperationException("حدد وحدة أساسية واحدة وأضف وحدة واحدة على الأقل.");
        if (x.Units.Any(u => u.Factor <= 0m || string.IsNullOrWhiteSpace(u.Barcode) || u.SalesPrice < 0m || u.OpeningCost < 0m))
            throw new InvalidOperationException("بيانات الوحدة أو الباركود أو السعر غير صالحة.");
        if (x.Units.Select(u => u.Barcode.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != x.Units.Count)
            throw new InvalidOperationException("لا يمكن تكرار نفس الباركود بين وحدات الصنف.");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var con = await OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(con, "BEGIN IMMEDIATE;", ct).ConfigureAwait(false);
            try
            {
                var id = x.Id ?? UuidV7.NewGuid();
                if (await ScalarLongAsync(con, "SELECT COUNT(*) FROM Items WHERE CompanyId=$c AND BranchId=$b AND Sku=$sku AND Id<>$id", ct,
                    ("$c", _user.CompanyId.ToString()), ("$b", _user.BranchId.ToString()), ("$sku", x.Sku.Trim()), ("$id", id.ToString())).ConfigureAwait(false) > 0)
                    throw new InvalidOperationException("كود الصنف مستخدم مسبقاً.");
                foreach (var u in x.Units)
                {
                    if (await ScalarLongAsync(con, "SELECT COUNT(*) FROM LocalWebItemUnits WHERE CompanyId=$c AND BranchId=$b AND Barcode=$barcode AND ItemId<>$item", ct,
                        ("$c", _user.CompanyId.ToString()), ("$b", _user.BranchId.ToString()), ("$barcode", u.Barcode.Trim()), ("$item", id.ToString())).ConfigureAwait(false) > 0)
                        throw new InvalidOperationException($"الباركود مستخدم مسبقاً: {u.Barcode}");
                }

                var exists = await ScalarLongAsync(con, "SELECT COUNT(*) FROM Items WHERE Id=$id", ct, ("$id", id.ToString())).ConfigureAwait(false) > 0;
                if (exists)
                {
                    var moved = await ScalarLongAsync(con, "SELECT COUNT(*) FROM StockLedgerEntries WHERE ItemId=$id", ct, ("$id", id.ToString())).ConfigureAwait(false) > 0;
                    if (moved)
                    {
                        var old = await QueryAsync(con, "SELECT UnitOfMeasureId unit_id,Factor factor FROM LocalWebItemUnits WHERE ItemId=$id", ct, ("$id", id.ToString())).ConfigureAwait(false);
                        foreach (var unit in x.Units)
                        {
                            var prior = old.FirstOrDefault(r => string.Equals((string?)r["unit_id"], unit.UnitId.ToString(), StringComparison.OrdinalIgnoreCase));
                            if (prior is not null && Math.Abs(Convert.ToDecimal(prior["factor"], CultureInfo.InvariantCulture) - unit.Factor) > 0.000001m)
                                throw new InvalidOperationException("لا يمكن تغيير معامل تحويل وحدة تحرك عليها المخزون.");
                        }
                    }
                    await ExecuteAsync(con, """
                        UPDATE Items SET Sku=$sku,Name=$name,BaseUnitOfMeasureId=$base,InventoryAccountId=$inv,
                        SalesRevenueAccountId=$sales,CogsAccountId=$cogs,Barcode=$barcode WHERE Id=$id
                        """, ct, ("$sku", x.Sku.Trim()), ("$name", x.Name.Trim()), ("$base", x.BaseUnitId.ToString()),
                        ("$inv", x.InventoryAccountId.ToString()), ("$sales", x.SalesAccountId.ToString()), ("$cogs", x.CogsAccountId.ToString()),
                        ("$barcode", x.Units.Single(u => u.IsBase).Barcode.Trim()), ("$id", id.ToString())).ConfigureAwait(false);
                    await ExecuteAsync(con, "DELETE FROM ItemUnitConversions WHERE ItemId=$id; DELETE FROM LocalWebItemUnits WHERE ItemId=$id;", ct, ("$id", id.ToString())).ConfigureAwait(false);
                }
                else
                {
                    await ExecuteAsync(con, """
                        INSERT INTO Items(Id,CompanyId,BranchId,Sku,Name,Barcode,BaseUnitOfMeasureId,InventoryAccountId,SalesRevenueAccountId,CogsAccountId,ItemKind,IsActive)
                        VALUES($id,$company,$branch,$sku,$name,$barcode,$base,$inv,$sales,$cogs,1,1)
                        """, ct, ("$id", id.ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                        ("$sku", x.Sku.Trim()), ("$name", x.Name.Trim()), ("$barcode", x.Units.Single(u => u.IsBase).Barcode.Trim()),
                        ("$base", x.BaseUnitId.ToString()), ("$inv", x.InventoryAccountId.ToString()), ("$sales", x.SalesAccountId.ToString()), ("$cogs", x.CogsAccountId.ToString())).ConfigureAwait(false);
                }

                foreach (var u in x.Units)
                {
                    await ExecuteAsync(con, """
                        INSERT INTO LocalWebItemUnits(Id,CompanyId,BranchId,ItemId,UnitOfMeasureId,Factor,Barcode,SalesPrice,OpeningCost,IsBase)
                        VALUES($id,$company,$branch,$item,$unit,$factor,$barcode,$price,$cost,$base)
                        """, ct, ("$id", UuidV7.NewGuid().ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                        ("$item", id.ToString()), ("$unit", u.UnitId.ToString()), ("$factor", Dec(u.Factor)), ("$barcode", u.Barcode.Trim()),
                        ("$price", Dec(u.SalesPrice)), ("$cost", Dec(u.OpeningCost)), ("$base", u.IsBase ? 1 : 0)).ConfigureAwait(false);
                    if (!u.IsBase)
                    {
                        await ExecuteAsync(con, """
                            INSERT INTO ItemUnitConversions(Id,CompanyId,BranchId,ItemId,UnitOfMeasureId,BaseUnitsPerUnit)
                            VALUES($id,$company,$branch,$item,$unit,$factor)
                            """, ct, ("$id", UuidV7.NewGuid().ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                            ("$item", id.ToString()), ("$unit", u.UnitId.ToString()), ("$factor", Dec(u.Factor))).ConfigureAwait(false);
                    }
                }
                await ExecuteAsync(con, "COMMIT;", ct).ConfigureAwait(false);
                _db.ChangeTracker.Clear();
                return new { id };
            }
            catch
            {
                await ExecuteAsync(con, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { _writeGate.Release(); }
    }

    private async Task<object?> DeleteItemAsync(Guid id, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var con = await OpenAsync(ct).ConfigureAwait(false);
            var moved = await ScalarLongAsync(con, "SELECT COUNT(*) FROM StockLedgerEntries WHERE ItemId=$id", ct, ("$id", id.ToString())).ConfigureAwait(false);
            var docs = await ScalarLongAsync(con, "SELECT (SELECT COUNT(*) FROM SalesInvoiceLines WHERE ItemId=$id)+(SELECT COUNT(*) FROM PurchaseInvoiceLines WHERE ItemId=$id)", ct, ("$id", id.ToString())).ConfigureAwait(false);
            if (moved + docs > 0) throw new InvalidOperationException("لا يمكن حذف صنف تحرك محاسبياً؛ يمكن تعديله فقط.");
            await ExecuteAsync(con, "DELETE FROM LocalWebItemUnits WHERE ItemId=$id; DELETE FROM LocalItemCommercialSettings WHERE ItemId=$id; DELETE FROM ItemUnitConversions WHERE ItemId=$id; DELETE FROM Items WHERE Id=$id;", ct, ("$id", id.ToString())).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            return new { deleted = true };
        }
        finally { _writeGate.Release(); }
    }

    private async Task<object?> GenerateBarcodeAsync(CancellationToken ct)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        var seed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 10_000_000_000L;
        var code = "29" + seed.ToString("D10", CultureInfo.InvariantCulture);
        while (await ScalarLongAsync(con, "SELECT COUNT(*) FROM LocalWebItemUnits WHERE Barcode=$b", ct, ("$b", code)).ConfigureAwait(false) > 0)
            code = (long.Parse(code, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        return new { barcode = code };
    }

    private async Task<object?> StockCardAsync(Guid itemId, CancellationToken ct)
    {
        var wh = (await _facade.GetWarehousesAsync(ct).ConfigureAwait(false)).First();
        var pos = await _facade.GetStockPositionAsync(itemId, wh.Id, ct).ConfigureAwait(false);
        var rows = await _facade.GetItemMovementAsync(itemId, wh.Id, ct).ConfigureAwait(false);
        decimal running = 0m;
        var data = rows.Select(r =>
        {
            running += r.QuantityDelta;
            return new { date = r.DateUtc, source_type = r.SourceType, quantity_delta = r.QuantityDelta, value_delta = r.InventoryValueDelta, unit_cost = r.UnitCost, running_quantity = running };
        }).ToArray();
        return new { position = new { quantity = pos.QuantityOnHand, value = pos.InventoryValue, averageCost = pos.AverageUnitCost }, rows = data };
    }

    private async Task<object?> ListPartnersAsync(string type, string q, CancellationToken ct)
    {
        var partnerType = type.Equals("Customer", StringComparison.OrdinalIgnoreCase) ? PartnerType.Customer
            : type.Equals("Supplier", StringComparison.OrdinalIgnoreCase) ? PartnerType.Supplier : (PartnerType?)null;
        q = q.Trim();
        var partners = await _db.BusinessPartners.AsNoTracking()
            .Where(p => p.CompanyId == _user.CompanyId && p.BranchId == _user.BranchId && p.IsActive
                     && (!partnerType.HasValue || p.PartnerType == partnerType || p.PartnerType == PartnerType.Both)
                     && (q == "" || p.Name.Contains(q) || p.Code.Contains(q) || (p.Phone != null && p.Phone.Contains(q))))
            .OrderBy(p => p.Name).Take(100).ToListAsync(ct).ConfigureAwait(false);
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        var result = new List<object>();
        foreach (var p in partners)
        {
            var profile = await QueryOneAsync(con, "SELECT WhatsApp whatsapp,City city,Address address,TaxNumber tax_number FROM LocalWebPartnerProfiles WHERE PartnerId=$id", ct, ("$id", p.Id.ToString())).ConfigureAwait(false);
            var balance = (await _facade.GetLedgerAsync(p.ControlAccountId, ct).ConfigureAwait(false)).LastOrDefault()?.RunningBalance ?? 0m;
            result.Add(new
            {
                id = p.Id, code = p.Code, type = p.PartnerType == PartnerType.Supplier ? "Supplier" : "Customer",
                name = p.Name, phone = p.Phone ?? "", whatsapp = profile?["whatsapp"]?.ToString() ?? "", city = profile?["city"]?.ToString() ?? "",
                address = profile?["address"]?.ToString() ?? "", tax_number = profile?["tax_number"]?.ToString() ?? "", credit_limit = p.CreditLimit,
                control_account_id = p.ControlAccountId, balance
            });
        }
        return result;
    }

    private async Task<object?> SavePartnerAsync(WebPartnerRequest x, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(x.Name)) throw new InvalidOperationException("اسم العميل/المورد مطلوب.");
        if (x.CreditLimit < 0m) throw new InvalidOperationException("الحد الائتماني لا يمكن أن يكون سالباً.");
        var type = x.Type.Equals("Supplier", StringComparison.OrdinalIgnoreCase) ? PartnerType.Supplier
            : x.Type.Equals("Customer", StringComparison.OrdinalIgnoreCase) ? PartnerType.Customer
            : throw new InvalidOperationException("نوع الطرف غير صالح.");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Guid partnerId; Guid controlAccountId;
            if (x.Id.HasValue)
            {
                partnerId = x.Id.Value;
                var existing = await _db.BusinessPartners.AsNoTracking().SingleOrDefaultAsync(p => p.Id == partnerId && p.CompanyId == _user.CompanyId && p.BranchId == _user.BranchId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("الطرف غير موجود.");
                controlAccountId = existing.ControlAccountId;
                await using var updateCon = await OpenAsync(ct).ConfigureAwait(false);
                await ExecuteAsync(updateCon, "UPDATE BusinessPartners SET Name=$name,Phone=$phone,CreditLimit=$limit WHERE Id=$id", ct,
                    ("$name", x.Name.Trim()), ("$phone", string.IsNullOrWhiteSpace(x.Phone) ? null : x.Phone.Trim()), ("$limit", Dec(x.CreditLimit)), ("$id", partnerId.ToString())).ConfigureAwait(false);
            }
            else
            {
                var parentCode = type == PartnerType.Customer ? "1130" : "2110";
                var parent = await _db.Accounts.AsNoTracking().SingleAsync(a => a.CompanyId == _user.CompanyId && a.BranchId == _user.BranchId && a.Code == parentCode, ct).ConfigureAwait(false);
                var prefix = type == PartnerType.Customer ? "CUS" : "SUP";
                var partnerCode = await NextMasterCodeAsync(prefix, "BusinessPartners", ct).ConfigureAwait(false);
                var accountCode = await NextMasterCodeAsync(parentCode + "-", "Accounts", ct).ConfigureAwait(false);
                var account = new Account(_user.CompanyId, _user.BranchId, accountCode, $"{(type == PartnerType.Customer ? "عميل" : "مورد")} - {x.Name.Trim()}", parent.AccountType, parent.NormalBalance, true, parent.Id);
                _db.Accounts.Add(account);
                var partner = new BusinessPartner(_user.CompanyId, _user.BranchId, partnerCode, x.Name.Trim(), type, account.Id, x.Phone, x.CreditLimit);
                _db.BusinessPartners.Add(partner);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                partnerId = partner.Id; controlAccountId = account.Id;

                if (x.OpeningBalance != 0m)
                {
                    var equity = await _db.Accounts.AsNoTracking().SingleAsync(a => a.CompanyId == _user.CompanyId && a.BranchId == _user.BranchId && a.Code == "3100", ct).ConfigureAwait(false);
                    var amount = Math.Abs(x.OpeningBalance);
                    IReadOnlyList<ManualJournalLine> lines = type == PartnerType.Customer
                        ? new[] { new ManualJournalLine(controlAccountId, amount, 0m, x.Name), new ManualJournalLine(equity.Id, 0m, amount, "رصيد افتتاحي") }
                        : new[] { new ManualJournalLine(equity.Id, amount, 0m, "رصيد افتتاحي"), new ManualJournalLine(controlAccountId, 0m, amount, x.Name) };
                    await _facade.CreateManualJournalAsync($"رصيد افتتاحي - {x.Name}", $"OPEN-{partnerCode}", lines, ct).ConfigureAwait(false);
                }
            }

            await using var con = await OpenAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(con, """
                INSERT INTO LocalWebPartnerProfiles(PartnerId,CompanyId,BranchId,WhatsApp,City,Address,TaxNumber)
                VALUES($id,$company,$branch,$wa,$city,$address,$tax)
                ON CONFLICT(PartnerId) DO UPDATE SET WhatsApp=excluded.WhatsApp,City=excluded.City,Address=excluded.Address,TaxNumber=excluded.TaxNumber
                """, ct, ("$id", partnerId.ToString()), ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()),
                ("$wa", x.WhatsApp.Trim()), ("$city", x.City.Trim()), ("$address", x.Address.Trim()), ("$tax", x.TaxNumber.Trim())).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            return new { id = partnerId, control_account_id = controlAccountId };
        }
        finally { _writeGate.Release(); }
    }

    private async Task<object?> PartnerStatementAsync(Guid partnerId, string from, string to, CancellationToken ct)
    {
        var p = await _db.BusinessPartners.AsNoTracking().SingleOrDefaultAsync(x => x.Id == partnerId && x.CompanyId == _user.CompanyId && x.BranchId == _user.BranchId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("الطرف غير موجود.");
        var rows = FilterLedger(await _facade.GetLedgerAsync(p.ControlAccountId, ct).ConfigureAwait(false), from, to);
        return new
        {
            partner = new { id = p.Id, code = p.Code, name = p.Name, control_account_id = p.ControlAccountId },
            rows = rows.Select(r => new { date = r.DateUtc, reference = r.Reference, description = r.Description, debit = r.Debit, credit = r.Credit, running_balance = r.RunningBalance }).ToArray(),
            balance = rows.LastOrDefault()?.RunningBalance ?? 0m
        };
    }

    private async Task<object?> SaveAccountAsync(WebAccountRequest x, CancellationToken ct)
    {
        if (!Enum.TryParse<AccountType>(x.Type, true, out var type)) throw new InvalidOperationException("نوع الحساب غير صالح.");
        if (!Enum.TryParse<NormalBalance>(x.Normal, true, out var normal)) throw new InvalidOperationException("طبيعة الحساب غير صالحة.");
        if (!x.Id.HasValue)
        {
            var id = await _facade.CreateChildAccountAsync(x.ParentId, x.Code, x.Name, type, normal, x.Postable, ct).ConfigureAwait(false);
            return new { id };
        }
        var idValue = x.Id.Value;
        var moved = await _db.JournalLines.AsNoTracking().AnyAsync(l => l.AccountId == idValue, ct).ConfigureAwait(false);
        if (moved)
            await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Accounts SET Name={x.Name.Trim()} WHERE Id={idValue.ToString()}", ct).ConfigureAwait(false);
        else
            await _db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Accounts SET Code={x.Code.Trim()},Name={x.Name.Trim()},AccountType={(int)type},NormalBalance={(int)normal},ParentAccountId={x.ParentId?.ToString()},IsPostable={(x.Postable ? 1 : 0)} WHERE Id={idValue.ToString()}", ct).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
        return new { id = idValue };
    }

    private async Task<object?> AccountLedgerAsync(Guid accountId, string from, string to, CancellationToken ct)
    {
        var account = await _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == _user.CompanyId && a.BranchId == _user.BranchId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("الحساب غير موجود.");
        var rows = FilterLedger(await _facade.GetLedgerAsync(accountId, ct).ConfigureAwait(false), from, to);
        return new
        {
            account = new { id = account.Id, code = account.Code, name = account.Name, type = account.AccountType.ToString(), normal = account.NormalBalance.ToString() },
            rows = rows.Select(r => new { date = r.DateUtc, reference = r.Reference, description = r.Description, debit = r.Debit, credit = r.Credit, running_balance = r.RunningBalance }).ToArray(),
            balance = rows.LastOrDefault()?.RunningBalance ?? 0m
        };
    }

    private async Task<string> NextMasterCodeAsync(string prefix, string table, CancellationToken ct)
    {
        await using var con = await OpenAsync(ct).ConfigureAwait(false);
        for (var n = 1; n < 100000; n++)
        {
            var code = prefix + n.ToString("D4", CultureInfo.InvariantCulture);
            if (await ScalarLongAsync(con, $"SELECT COUNT(*) FROM {table} WHERE CompanyId=$company AND BranchId=$branch AND Code=$code", ct,
                ("$company", _user.CompanyId.ToString()), ("$branch", _user.BranchId.ToString()), ("$code", code)).ConfigureAwait(false) == 0) return code;
        }
        throw new InvalidOperationException("تعذر إنشاء كود جديد.");
    }
}