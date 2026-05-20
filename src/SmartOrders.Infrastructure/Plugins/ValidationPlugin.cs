using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SmartOrders.Infrastructure.Plugins;

/// <summary>
/// Order field validation against required-fields table.
///
/// NOTE — NOT USED IN THE PIPELINE.
/// Stage 3 validation is handled by <see cref="SmartOrders.Infrastructure.Services.ValidationService"/>,
/// a pure-C# service (no LLM, no SK tool call) that runs the same logic directly.
/// This plugin exists as the SK-tool equivalent, kept for two reasons:
///   1. Future: if validation is ever moved back to an LLM agent, this is the tool to register.
///   2. Unit testing: tests can invoke ValidateOrderFieldsAsync directly without DI.
/// To activate: register on a kernel via k.Plugins.AddFromObject(new ValidationPlugin(...), "Validation")
/// and wire a new ValidationAgent in AgentFactory + SmartOrdersPipeline.
///
/// Phase 2 upgrade: replace static RequiredByCategory table with full RPL-driven field rules from TW config.
/// </summary>
public sealed class ValidationPlugin(ILogger<ValidationPlugin> logger)
{
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

    private static readonly Dictionary<string, string> CategoryNormalize = new(StringComparer.OrdinalIgnoreCase)
    {
        ["referral"] = "Referrals", ["referrals"] = "Referrals",
        ["medication"] = "Medications", ["medications"] = "Medications",
        ["lab"] = "Lab", ["labs"] = "Lab",
        ["imaging"] = "Imaging",
        ["diagnostic"] = "Diagnostic Orders", ["diagnostic orders"] = "Diagnostic Orders",
        ["followup"] = "FollowUp Orders", ["follow up"] = "FollowUp Orders", ["follow-up"] = "FollowUp Orders",
    };

    [Description("Validate that all required fields are present for an order.")]
    public Task<string> ValidateOrderFieldsAsync(
        [Description("The catalog item ID — confirms the order was mapped.")] string itemId,
        [Description("Broad category e.g. 'Lab', 'Imaging'.")] string orderCategory,
        [Description("Comma-separated list of field names that currently have values.")] string providedFields)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(orderCategory))
        {
            return Task.FromResult(JsonSerializer.Serialize(new { missing_fields = new[] { "item_id" }, is_complete = false }));
        }

        var canonical = CategoryNormalize.TryGetValue(orderCategory.Trim(), out var norm) ? norm : orderCategory;
        var required = RequiredByCategory.TryGetValue(canonical, out var req) ? req : RequiredByCategory["default"];
        var present = new HashSet<string>(providedFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase) { "item_id" };
        var missing = required.Where(f => !present.Contains(f)).ToArray();

        logger.LogDebug("validate_order_fields category={Category} missing={Missing}", orderCategory, string.Join(",", missing));
        return Task.FromResult(JsonSerializer.Serialize(new { missing_fields = missing, is_complete = missing.Length == 0 }));
    }
}
