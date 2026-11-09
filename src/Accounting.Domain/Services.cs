namespace Accounting.Domain;

public interface ICurrencyPolicyService
{
    decimal RoundMoney(decimal value, CompanyPolicy policy);
    decimal RoundQuantity(decimal value, CompanyPolicy policy);
    decimal Add(decimal left, decimal right, CompanyPolicy policy);
    decimal Subtract(decimal left, decimal right, CompanyPolicy policy);
    decimal Multiply(decimal left, decimal right, CompanyPolicy policy);
    decimal AddQuantity(decimal left, decimal right, CompanyPolicy policy);
    decimal SubtractQuantity(decimal left, decimal right, CompanyPolicy policy);
    decimal MultiplyQuantity(decimal left, decimal right, CompanyPolicy policy);
    decimal Divide(decimal numerator, decimal denominator, CompanyPolicy policy, int? scaleOverride = null);
    Money Money(decimal amount, CompanyPolicy policy);
}

public sealed class CurrencyPolicyService : ICurrencyPolicyService
{
    public decimal RoundMoney(decimal value, CompanyPolicy policy) =>
        decimal.Round(value, policy.CurrencyScale, policy.RoundingMode);

    public decimal RoundQuantity(decimal value, CompanyPolicy policy) =>
        decimal.Round(value, policy.QuantityScale, policy.RoundingMode);

    public decimal Add(decimal left, decimal right, CompanyPolicy policy) =>
        RoundMoney(left + right, policy);

    public decimal Subtract(decimal left, decimal right, CompanyPolicy policy) =>
        RoundMoney(left - right, policy);

    public decimal Multiply(decimal left, decimal right, CompanyPolicy policy) =>
        RoundMoney(left * right, policy);

    public decimal AddQuantity(decimal left, decimal right, CompanyPolicy policy) =>
        RoundQuantity(left + right, policy);

    public decimal SubtractQuantity(decimal left, decimal right, CompanyPolicy policy) =>
        RoundQuantity(left - right, policy);

    public decimal MultiplyQuantity(decimal left, decimal right, CompanyPolicy policy) =>
        RoundQuantity(left * right, policy);

    public decimal Divide(decimal numerator, decimal denominator, CompanyPolicy policy, int? scaleOverride = null)
    {
        if (denominator == 0m) throw new AccountingDomainException("Division by zero is not allowed.");
        return decimal.Round(numerator / denominator, scaleOverride ?? policy.CurrencyScale, policy.RoundingMode);
    }

    public Money Money(decimal amount, CompanyPolicy policy) =>
        new(RoundMoney(amount, policy), policy.BaseCurrencyCode);
}

public sealed record InventoryState(
    decimal QuantityOnHand,
    decimal InventoryValue,
    decimal AverageUnitCost,
    decimal LastKnownUnitCost);

public sealed record InventoryIssueValuation(
    decimal Quantity,
    decimal UnitCost,
    decimal TotalCost);

public sealed record StockLedgerValuationRow(
    Guid Id,
    Guid ItemId,
    Guid WarehouseId,
    decimal QuantityDelta,
    decimal InventoryValueDelta,
    decimal UnitCost,
    DateTimeOffset PostedAtUtc);

public sealed class InventoryValuationEngine
{
    private readonly ICurrencyPolicyService _currency;

    public InventoryValuationEngine(ICurrencyPolicyService currency) => _currency = currency;

    public InventoryState CalculateState(IEnumerable<StockLedgerEntry> entries, CompanyPolicy policy) =>
        CalculateState(entries
            .Where(x => x.Status == PostingStatus.Posted)
            .Select(ToValuationRow), policy);

    public InventoryState CalculateState(IEnumerable<StockLedgerValuationRow> entries, CompanyPolicy policy)
    {
        decimal quantity = 0m;
        decimal value = 0m;
        decimal lastCost = 0m;

        foreach (var entry in entries.OrderBy(x => x.PostedAtUtc).ThenBy(x => x.Id))
        {
            quantity = _currency.AddQuantity(quantity, entry.QuantityDelta, policy);
            value = _currency.Add(value, entry.InventoryValueDelta, policy);
            if (entry.UnitCost > 0m) lastCost = entry.UnitCost;
        }

        var average = quantity == 0m
            ? lastCost
            : _currency.Divide(value, quantity, policy, Math.Max(policy.CurrencyScale + 6, 6));

        return new InventoryState(quantity, value, average, lastCost);
    }

    public InventoryIssueValuation CalculateIssue(
        IReadOnlyCollection<StockLedgerEntry> existingEntries,
        decimal requestedQuantity,
        CompanyPolicy policy,
        bool allowNegativeStock) =>
        CalculateIssue(
            existingEntries
                .Where(x => x.Status == PostingStatus.Posted)
                .Select(ToValuationRow)
                .ToArray(),
            requestedQuantity,
            policy,
            allowNegativeStock);

    public InventoryIssueValuation CalculateIssue(
        IReadOnlyCollection<StockLedgerValuationRow> existingEntries,
        decimal requestedQuantity,
        CompanyPolicy policy,
        bool allowNegativeStock)
    {
        if (requestedQuantity <= 0m)
            throw new AccountingDomainException("Issue quantity must be positive.");

        var state = CalculateState(existingEntries, policy);
        if (!allowNegativeStock && state.QuantityOnHand < requestedQuantity)
        {
            var sample = existingEntries.FirstOrDefault();
            throw new NegativeStockNotAllowedException(
                sample?.ItemId ?? Guid.Empty,
                sample?.WarehouseId ?? Guid.Empty,
                state.QuantityOnHand,
                requestedQuantity);
        }

        return policy.InventoryValuationMethod switch
        {
            InventoryValuationMethod.MovingWeightedAverage =>
                Weighted(state, requestedQuantity, policy),
            InventoryValuationMethod.Fifo =>
                Fifo(existingEntries, state, requestedQuantity, policy, allowNegativeStock),
            _ => throw new AccountingDomainException("Unsupported inventory valuation method.")
        };
    }

    private InventoryIssueValuation Weighted(
        InventoryState state,
        decimal quantity,
        CompanyPolicy policy)
    {
        var unitCost = state.QuantityOnHand == 0m
            ? state.LastKnownUnitCost
            : state.AverageUnitCost;

        var total = _currency.Multiply(quantity, unitCost, policy);
        var normalized = _currency.Divide(
            total,
            quantity,
            policy,
            Math.Max(policy.CurrencyScale + 6, 6));

        return new InventoryIssueValuation(quantity, normalized, total);
    }

    private InventoryIssueValuation Fifo(
        IReadOnlyCollection<StockLedgerValuationRow> entries,
        InventoryState state,
        decimal quantity,
        CompanyPolicy policy,
        bool allowNegativeStock)
    {
        var layers = new List<(decimal Quantity, decimal Cost)>();

        foreach (var entry in entries.OrderBy(x => x.PostedAtUtc).ThenBy(x => x.Id))
        {
            if (entry.QuantityDelta > 0m)
            {
                layers.Add((entry.QuantityDelta, entry.UnitCost));
                continue;
            }

            var consume = -entry.QuantityDelta;
            for (var i = 0; i < layers.Count && consume > 0m; i++)
            {
                var take = Math.Min(consume, layers[i].Quantity);
                layers[i] = (
                    _currency.SubtractQuantity(layers[i].Quantity, take, policy),
                    layers[i].Cost);
                consume = _currency.SubtractQuantity(consume, take, policy);
            }

            layers.RemoveAll(x => x.Quantity == 0m);
        }

        var remaining = quantity;
        decimal cost = 0m;
        foreach (var layer in layers)
        {
            if (remaining <= 0m) break;
            var take = Math.Min(remaining, layer.Quantity);
            cost = _currency.Add(
                cost,
                _currency.Multiply(take, layer.Cost, policy),
                policy);
            remaining = _currency.SubtractQuantity(remaining, take, policy);
        }

        if (remaining > 0m)
        {
            if (!allowNegativeStock)
            {
                var sample = entries.FirstOrDefault();
                throw new NegativeStockNotAllowedException(
                    sample?.ItemId ?? Guid.Empty,
                    sample?.WarehouseId ?? Guid.Empty,
                    state.QuantityOnHand,
                    quantity);
            }

            cost = _currency.Add(
                cost,
                _currency.Multiply(remaining, state.LastKnownUnitCost, policy),
                policy);
        }

        return new InventoryIssueValuation(
            quantity,
            _currency.Divide(cost, quantity, policy, Math.Max(policy.CurrencyScale + 6, 6)),
            cost);
    }

    private static StockLedgerValuationRow ToValuationRow(StockLedgerEntry entry) =>
        new(
            entry.Id,
            entry.ItemId,
            entry.WarehouseId,
            entry.QuantityDelta,
            entry.InventoryValueDelta,
            entry.UnitCost,
            entry.PostedAtUtc);
}
