using System.Text.Json.Serialization;

namespace Accounting.UI.Windows;

public sealed record WebItemUnitRequest(
    [property: JsonPropertyName("unitId")] Guid UnitId,
    [property: JsonPropertyName("factor")] decimal Factor,
    [property: JsonPropertyName("barcode")] string Barcode,
    [property: JsonPropertyName("salesPrice")] decimal SalesPrice,
    [property: JsonPropertyName("openingCost")] decimal OpeningCost,
    [property: JsonPropertyName("isBase")] bool IsBase);

public sealed record WebItemRequest(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("baseUnitId")] Guid BaseUnitId,
    [property: JsonPropertyName("inventoryAccountId")] Guid InventoryAccountId,
    [property: JsonPropertyName("salesAccountId")] Guid SalesAccountId,
    [property: JsonPropertyName("cogsAccountId")] Guid CogsAccountId,
    [property: JsonPropertyName("units")] IReadOnlyList<WebItemUnitRequest> Units);

public sealed record WebPartnerRequest(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("phone")] string Phone,
    [property: JsonPropertyName("whatsapp")] string WhatsApp,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("taxNumber")] string TaxNumber,
    [property: JsonPropertyName("creditLimit")] decimal CreditLimit,
    [property: JsonPropertyName("openingBalance")] decimal OpeningBalance);

public sealed record WebInvoiceLineRequest(
    [property: JsonPropertyName("itemId")] Guid ItemId,
    [property: JsonPropertyName("itemUnitId")] Guid ItemUnitId,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unitPrice")] decimal UnitPrice,
    [property: JsonPropertyName("discount")] decimal Discount);

public sealed record WebInvoiceRequest(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("warehouseId")] Guid WarehouseId,
    [property: JsonPropertyName("settlementAccountId")] Guid SettlementAccountId,
    [property: JsonPropertyName("partnerId")] Guid? PartnerId,
    [property: JsonPropertyName("paymentMode")] string PaymentMode,
    [property: JsonPropertyName("discount")] decimal Discount,
    [property: JsonPropertyName("taxRate")] decimal TaxRate,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("lines")] IReadOnlyList<WebInvoiceLineRequest> Lines);

public sealed record WebVoucherRequest(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("cashAccountId")] Guid CashAccountId,
    [property: JsonPropertyName("counterAccountId")] Guid CounterAccountId,
    [property: JsonPropertyName("partnerId")] Guid? PartnerId,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("draft")] bool Draft);

public sealed record WebJournalLineRequest(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("debit")] decimal Debit,
    [property: JsonPropertyName("credit")] decimal Credit,
    [property: JsonPropertyName("memo")] string Memo);

public sealed record WebJournalRequest(
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("lines")] IReadOnlyList<WebJournalLineRequest> Lines);

public sealed record WebAccountRequest(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("normal")] string Normal,
    [property: JsonPropertyName("parentId")] Guid? ParentId,
    [property: JsonPropertyName("postable")] bool Postable);

public sealed record WebSettingsRequest(
    [property: JsonPropertyName("arabicName")] string ArabicName,
    [property: JsonPropertyName("englishName")] string EnglishName,
    [property: JsonPropertyName("business")] string Business,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("phones")] string Phones,
    [property: JsonPropertyName("whatsapp")] string WhatsApp,
    [property: JsonPropertyName("taxNumber")] string TaxNumber,
    [property: JsonPropertyName("commercialRegistration")] string CommercialRegistration,
    [property: JsonPropertyName("footer")] string Footer,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("currencySymbol")] string CurrencySymbol,
    [property: JsonPropertyName("taxEnabled")] bool TaxEnabled,
    [property: JsonPropertyName("taxRate")] decimal TaxRate,
    [property: JsonPropertyName("logoData")] string LogoData,
    [property: JsonPropertyName("allowNegativeStock")] bool AllowNegativeStock = false);

public sealed record WebRestoreRequest([property: JsonPropertyName("path")] string Path);
public sealed record WebSyncSettingsRequest([property: JsonPropertyName("serverUrl")] string ServerUrl);