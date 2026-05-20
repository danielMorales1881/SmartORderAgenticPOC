using Microsoft.SemanticKernel;

namespace SmartOrders.Infrastructure.Configuration;

/// <summary>
/// Execution settings for the custom GeminiChatCompletionService.
/// JsonMode = true → responseMimeType = "application/json" (equivalent to ADK output_schema).
/// MaxOutputTokens overrides the global GeminiGatewaySettings.MaxTokens for this call.
/// ThinkingBudget = 0 disables thinking for simple extraction tasks (gemini-3-flash-preview is a thinking model).
/// </summary>
public sealed class GeminiExecutionSettings : PromptExecutionSettings
{
    public bool JsonMode { get; init; }
    public int? MaxOutputTokens { get; init; }
    /// <summary>
    /// Sets thinkingConfig.thinkingBudget on the Gemini request.
    /// Set to 0 to disable thinking (saves tokens for structured extraction tasks).
    /// Null = use the global ThinkingLevel from GeminiGatewaySettings.
    /// </summary>
    public int? ThinkingBudget { get; init; }
}
