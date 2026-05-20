using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SmartOrders.Infrastructure.Services;

/// <summary>
/// Pure C# equivalent of PureValidationAgent (validation_agent.py).
/// Validates mapped orders without any LLM call — 100% deterministic.
/// Equivalent of validate_mapped_orders_batch() in validation_tools.py.
/// </summary>
public sealed partial class ValidationService(ILogger<ValidationService> logger)
{
    // Required fields by order category — Phase 1 minimal set.
    // "item_id" is always implicitly required (verified separately).
    private static readonly Dictionary<string, string[]> RequiredByCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Lab"]               = ["item_id", "priority", "to_be_done_date"],
        ["Imaging"]           = ["item_id", "priority", "to_be_done_date"],
        ["Diagnostic Orders"] = ["item_id", "priority", "to_be_done_date"],
        ["Medications"]       = ["item_id", "priority", "dose", "route", "frequency"],
        ["Referrals"]         = ["item_id", "priority", "reason"],
        ["FollowUp Orders"]   = ["item_id", "priority"],
        ["default"]           = ["item_id", "priority"],
    };

    // Normalize LLM-generated category names to canonical DB values.
    private static readonly Dictionary<string, string> CategoryNormalize = new(StringComparer.OrdinalIgnoreCase)
    {
        ["referral"] = "Referrals", ["referrals"] = "Referrals",
        ["medication"] = "Medications", ["medications"] = "Medications",
        ["lab"] = "Lab", ["labs"] = "Lab",
        ["imaging"] = "Imaging",
        ["diagnostic"] = "Diagnostic Orders", ["diagnostic orders"] = "Diagnostic Orders",
        ["diagnostic order"] = "Diagnostic Orders",
        ["followup"] = "FollowUp Orders", ["followup orders"] = "FollowUp Orders",
        ["follow up"] = "FollowUp Orders", ["follow up orders"] = "FollowUp Orders",
        ["follow-up"] = "FollowUp Orders", ["follow-up orders"] = "FollowUp Orders",
    };

    /// <summary>
    /// Validates all mapped orders in one pass — no LLM required.
    /// Reads the mapped_orders JSON string (as produced by MappingAgent),
    /// adds missing_fields and is_complete to each order dict, returns validated JSON.
    /// Mirrors PureValidationAgent._run_async_impl() logic exactly.
    /// </summary>
    public async Task<string> ValidateBatchAsync(string mappedOrdersJson)
    {
        var orders = ParseOrdersJson(mappedOrdersJson);
        logger.LogDebug("ValidationService (pure C#) starting count={Count}", orders.Count);

        var result = new List<Dictionary<string, object?>>();
        foreach (var order in orders)
        {
            var itemId = GetString(order, "item_id") ?? string.Empty;
            var orderCategory = GetString(order, "order_category") ?? string.Empty;

            // Collect field names that have a non-null, non-empty value — same as Python
            var providedFields = order
                .Where(kv => kv.Value is not null && kv.Value.ToString() != string.Empty)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var (missingFields, isComplete) = ValidateOrderFields(itemId, orderCategory, providedFields);

            var validated = new Dictionary<string, object?>(order)
            {
                ["missing_fields"] = missingFields,
                ["is_complete"] = isComplete,
            };
            result.Add(validated);
        }

        logger.LogDebug("ValidationService (pure C#) completed count={Count}", result.Count);
        return await Task.FromResult(JsonSerializer.Serialize(result));
    }

    /// <summary>
    /// Validates that all required fields are present for an order.
    /// Direct port of validate_order_fields() in validation_tools.py.
    /// </summary>
    private static (string[] MissingFields, bool IsComplete) ValidateOrderFields(
        string itemId, string orderCategory, HashSet<string> providedFields)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(orderCategory))
            return (["item_id"], false);

        var canonical = CategoryNormalize.TryGetValue(orderCategory.Trim(), out var norm) ? norm : orderCategory;

        // Reject unrecognised order categories (e.g. "Patient Instructions" from catalog items
        // that are not true orderable entries). The provider will see ⚠ Missing: order_category.
        if (!RequiredByCategory.ContainsKey(canonical))
            return (["order_category"], false);

        var required = RequiredByCategory[canonical];

        // item_id always present if passed as argument
        providedFields.Add("item_id");
        var missing = required.Where(f => !providedFields.Contains(f)).ToArray();

        return (missing, missing.Length == 0);
    }

    /// <summary>
    /// Parses the mapped_orders JSON — try direct parse, fallback to regex extraction.
    /// Mirrors PureValidationAgent._run_async_impl() JSON parsing logic exactly.
    /// </summary>
    private List<Dictionary<string, object?>> ParseOrdersJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        // Try direct parse
        try
        {
            return JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(raw) ?? [];
        }
        catch (JsonException) { }

        // Fallback: extract first JSON array from surrounding text (LLM may wrap in prose/markdown)
        var match = JsonArrayRegex().Match(raw);
        if (match.Success)
        {
            try
            {
                return JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(match.Value) ?? [];
            }
            catch (JsonException) { }
        }

        logger.LogWarning("mapped_orders contains no parseable JSON array; treating as empty list");
        return [];
    }

    private static string? GetString(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var val) ? val?.ToString() : null;

    [GeneratedRegex(@"\[[\s\S]*\]")]
    private static partial Regex JsonArrayRegex();
}
