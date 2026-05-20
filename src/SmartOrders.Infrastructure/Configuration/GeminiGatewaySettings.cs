namespace SmartOrders.Infrastructure.Configuration;

/// <summary>
/// Configuration for the Vertex AI Gemini service via Azure Front Door proxy.
/// Copied from FileComparer-Semantic/GeminiSettings.cs — same endpoint/auth pattern.
///
/// Endpoint: POST {BaseUrl}{Endpoint} (with {modelName} placeholder replaced)
/// Header:   API-KEY: {ApiKey}
/// Body:     Vertex/Gemini native format (contents/parts, generationConfig)
/// </summary>
public class GeminiGatewaySettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string Endpoint { get; set; } = "{modelName}:generateContent";
    public double Temperature { get; set; } = 1.0;
    public int MaxTokens { get; set; } = 8192;
    public string ThinkingLevel { get; set; } = "";
    public double TopP { get; set; } = 0.8;
    public int TimeoutMinutes { get; set; } = 5;
    public int? Seed { get; set; }
}
