using System.Text.Json;

namespace Accounting.Modules;

public sealed record WholesalePriceTierDefinition(
    Guid ItemId,
    decimal MinBaseQuantity,
    decimal UnitPrice);

public sealed record WholesaleModuleSettings(
    IReadOnlyList<WholesalePriceTierDefinition> PriceTiers);

public sealed class WholesalePricingService
{
    public decimal GetUnitPrice(string configurationJson, Guid itemId, decimal baseQuantity, decimal fallbackUnitPrice)
    {
        if (baseQuantity <= 0m) throw new ArgumentOutOfRangeException(nameof(baseQuantity));
        if (fallbackUnitPrice < 0m) throw new ArgumentOutOfRangeException(nameof(fallbackUnitPrice));
        if (string.IsNullOrWhiteSpace(configurationJson)) return fallbackUnitPrice;

        WholesaleModuleSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<WholesaleModuleSettings>(
                configurationJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Wholesale module configuration is invalid: {ex.Message}", ex);
        }

        var tier = (settings?.PriceTiers ?? Array.Empty<WholesalePriceTierDefinition>())
            .Where(x => x.ItemId == itemId && x.MinBaseQuantity > 0m && x.UnitPrice >= 0m && x.MinBaseQuantity <= baseQuantity)
            .OrderByDescending(x => x.MinBaseQuantity)
            .FirstOrDefault();
        return tier?.UnitPrice ?? fallbackUnitPrice;
    }
}
