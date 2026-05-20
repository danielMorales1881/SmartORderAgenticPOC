using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SmartOrders.Infrastructure.Plugins;

/// <summary>
/// ICD-10 lookup and clinical defaults.
/// POC: in-memory table. Production upgrade: replace with terminology service or FHIR ValueSet.
/// </summary>
public sealed class MappingPlugin(ILogger<MappingPlugin> logger)
{
    private static readonly Dictionary<string, string> Icd10Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["diabetes"] = "E11.9", ["type 2 diabetes"] = "E11.9", ["type ii diabetes"] = "E11.9", ["dm2"] = "E11.9",
        ["hypertension"] = "I10", ["htn"] = "I10",
        ["anemia"] = "D64.9",
        ["hypothyroidism"] = "E03.9",
        ["copd"] = "J44.1",
        ["asthma"] = "J45.901",
        ["ckd"] = "N18.9", ["chronic kidney disease"] = "N18.9",
        ["heart failure"] = "I50.9", ["chf"] = "I50.9",
    };

    [Description("Map a free-text diagnosis hint to an ICD-10 code.")]
    public Task<string> MapDiagnosisAsync(
        [Description("Clinical text hint e.g. 'type 2 diabetes'.")] string diagnosisHint)
    {
        Icd10Map.TryGetValue(diagnosisHint.Trim(), out var code);
        logger.LogDebug("map_diagnosis hint={Hint} code={Code}", diagnosisHint, code);
        return Task.FromResult(JsonSerializer.Serialize(new { icd10_code = code }));
    }

    [Description("Apply standard clinical defaults for a given order type.")]
    public Task<string> ApplyOrderDefaultsAsync(
        [Description("Broad category: 'Lab', 'Imaging', 'Medication', 'Referral'.")] string orderType,
        [Description("Priority override. Null defaults to 'Routine'.")] string? priority = null)
    {
        var resolvedPriority = string.IsNullOrWhiteSpace(priority) ? "Routine" : priority;
        var defaults = new { priority = resolvedPriority, to_be_done_date = "today" };
        logger.LogDebug("apply_order_defaults order_type={OrderType} priority={Priority}", orderType, resolvedPriority);
        return Task.FromResult(JsonSerializer.Serialize(defaults));
    }
}
