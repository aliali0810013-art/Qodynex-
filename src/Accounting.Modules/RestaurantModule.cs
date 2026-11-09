using System.Text.Json;
using Accounting.Application;
using Accounting.Domain;
using Accounting.SharedKernel;

namespace Accounting.Modules;

public enum RestaurantServiceType
{
    DineIn = 1,
    Takeaway = 2,
    Delivery = 3
}

public sealed record RestaurantOrderMetadata(RestaurantServiceType ServiceType, string? TableOrDeliveryReference);

public sealed class RestaurantModule : IActivityModule
{
    public const string ModuleKey = "RESTAURANT";
    private readonly IReadOnlyList<IDocumentPostingRule> _rules = new IDocumentPostingRule[] { new RestaurantPostingRule() };

    public string Key => ModuleKey;
    public string DisplayName => "مطاعم";
    public IReadOnlySet<string> Features { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "restaurant.order", "restaurant.dine-in", "restaurant.takeaway", "restaurant.delivery"
    };
    public IReadOnlyList<ModuleScreenDefinition> Screens { get; } =
    [
        new("restaurant.orders","الطلبات",ActivityPlatform.Windows,"restaurant.order"),
        new("restaurant.order.mobile","طلب سريع",ActivityPlatform.Android,"restaurant.order")
    ];
    public IReadOnlyList<IDocumentPostingRule> PostingRules => _rules;
}

public sealed class RestaurantPostingRule : IDocumentPostingRule
{
    public string ModuleKey => RestaurantModule.ModuleKey;

    public Task ValidateAsync(CommercialDocument document, UserContext userContext, CancellationToken cancellationToken)
    {
        if (document is not SalesInvoice)
            throw new AccountingDomainException("Restaurant module currently posts through SalesInvoice only.");

        RestaurantOrderMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<RestaurantOrderMetadata>(document.ModuleMetadataJson);
        }
        catch (JsonException ex)
        {
            throw new AccountingDomainException($"Restaurant metadata is invalid: {ex.Message}");
        }

        if (metadata is null || !Enum.IsDefined(metadata.ServiceType))
            throw new AccountingDomainException("Restaurant service type (DineIn/Takeaway/Delivery) is required.");
        return Task.CompletedTask;
    }
}
