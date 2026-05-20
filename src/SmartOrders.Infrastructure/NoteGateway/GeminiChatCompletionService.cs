using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using SmartOrders.Infrastructure.Configuration;

namespace SmartOrders.Infrastructure.NoteGateway;

/// <summary>
/// Custom SK IChatCompletionService that calls the Note+ gateway (Azure Front Door → Vertex AI).
///
/// Base pattern copied exactly from FileComparer-Semantic/GeminiLlmService.cs:
///   - Same endpoint URL pattern: {BaseUrl}{modelName}:generateContent
///   - Same auth: API-KEY header + Azure AD Bearer token via FrontDoorTokenService
///   - Same ExtractResponseText logic (handles thought parts, fallback to OpenAI format)
///
/// Extended for the SmartOrders multi-agent pipeline:
///   - Multi-turn ChatHistory → Gemini contents array
///   - Function/tool calling (Gemini functionDeclarations + functionCall/functionResponse)
///   - System instruction support
/// </summary>
public sealed class GeminiChatCompletionService : IChatCompletionService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiGatewaySettings _settings;
    private readonly FrontDoorTokenService _tokenService;
    private readonly ILogger<GeminiChatCompletionService> _logger;

    public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>();

    public GeminiChatCompletionService(
        IOptions<GeminiGatewaySettings> settings,
        FrontDoorTokenService tokenService,
        ILogger<GeminiChatCompletionService> logger)
    {
        _settings = settings.Value;
        _tokenService = tokenService;
        _logger = logger;

        // Same HttpClient setup as FileComparer GeminiLlmService
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("API-KEY", _settings.ApiKey);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.Timeout = TimeSpan.FromMinutes(_settings.TimeoutMinutes);
    }

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        // Build endpoint URL — same pattern as FileComparer
        var endpoint = _settings.Endpoint.Replace("{modelName}", _settings.ModelName);
        var url = $"{_settings.BaseUrl}{endpoint}";

        // Per-call MaxOutputTokens overrides global setting (matches Python generate_content_config per agent)
        var maxTokens = executionSettings is GeminiExecutionSettings { MaxOutputTokens: { } perCall }
            ? perCall
            : _settings.MaxTokens;

        // Build generationConfig — same structure as FileComparer
        var genConfig = new Dictionary<string, object>
        {
            ["temperature"] = _settings.Temperature,
            ["maxOutputTokens"] = maxTokens,
            ["topP"] = _settings.TopP,
        };

        if (_settings.Seed.HasValue)
            genConfig["seed"] = _settings.Seed.Value;

        // Per-call ThinkingBudget overrides global ThinkingLevel (0 = disable thinking)
        if (executionSettings is GeminiExecutionSettings { ThinkingBudget: { } budget })
            genConfig["thinkingConfig"] = new { thinkingBudget = budget };
        else if (!string.IsNullOrWhiteSpace(_settings.ThinkingLevel))
            genConfig["thinkingConfig"] = new { thinkingLevel = _settings.ThinkingLevel };

        // Structured output: enforce JSON mode when caller requests it via GeminiExecutionSettings
        if (executionSettings is GeminiExecutionSettings { JsonMode: true })
            genConfig["responseMimeType"] = "application/json";

        // Build contents array from ChatHistory (multi-turn)
        var (systemInstruction, contents) = BuildContents(chatHistory);

        // Build function declarations from kernel plugins when auto tool calling is enabled
        var hasFunctions = executionSettings?.FunctionChoiceBehavior != null && kernel?.Plugins.Count > 0;
        var functionDeclarations = hasFunctions ? BuildFunctionDeclarations(kernel!) : null;

        // Assemble request body
        object requestBody;
        if (systemInstruction != null && functionDeclarations != null)
        {
            requestBody = new { system_instruction = systemInstruction, contents, tools = new[] { new { functionDeclarations } }, generationConfig = genConfig };
        }
        else if (systemInstruction != null)
        {
            requestBody = new { system_instruction = systemInstruction, contents, generationConfig = genConfig };
        }
        else if (functionDeclarations != null)
        {
            requestBody = new { contents, tools = new[] { new { functionDeclarations } }, generationConfig = genConfig };
        }
        else
        {
            requestBody = new { contents, generationConfig = genConfig };
        }

        var jsonContent = JsonSerializer.Serialize(requestBody);
        using var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        // Acquire Bearer token — same as FileComparer
        var bearerToken = await _tokenService.GetTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = httpContent };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        _logger.LogDebug("Calling Note+ Gemini gateway: {Url}", url);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini gateway returned {Status}. Body: {Body}", (int)response.StatusCode, responseBody);
            throw new HttpRequestException($"Gemini gateway error {(int)response.StatusCode}: {responseBody}");
        }

        _logger.LogInformation("gemini_raw_response status={Status} body={Body}", (int)response.StatusCode, responseBody[..Math.Min(1000, responseBody.Length)]);

        return ParseResponse(responseBody);
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming is not supported by the Note+ Gemini gateway.");

    // -------------------------------------------------------------------------
    // Build Gemini contents from SK ChatHistory
    // Maps AuthorRole → Gemini role and handles FunctionCallContent / FunctionResultContent
    // -------------------------------------------------------------------------
    private static (object? SystemInstruction, List<object> Contents) BuildContents(ChatHistory history)
    {
        object? systemInstruction = null;
        var contents = new List<object>();

        foreach (var message in history)
        {
            if (message.Role == AuthorRole.System)
            {
                systemInstruction = new { parts = new[] { new { text = message.Content ?? "" } } };
                continue;
            }

            var geminiRole = message.Role == AuthorRole.Assistant ? "model" : "user";

            // If this assistant message carries raw Gemini content (with thoughtSignature),
            // re-use it verbatim — Gemini rejects requests where a function call in history
            // is missing its thoughtSignature.
            if (message.Role == AuthorRole.Assistant &&
                message.InnerContent is string rawGeminiContent &&
                !string.IsNullOrEmpty(rawGeminiContent))
            {
                var rawPartsNode = System.Text.Json.Nodes.JsonNode.Parse(rawGeminiContent)
                    ?["parts"];
                if (rawPartsNode != null)
                {
                    contents.Add(new { role = "model", parts = rawPartsNode });
                    continue;
                }
            }

            var parts = new List<object>();

            foreach (var item in message.Items)
            {
                switch (item)
                {
                    case TextContent tc:
                        if (!string.IsNullOrEmpty(tc.Text))
                            parts.Add(new { text = tc.Text });
                        break;

                    case FunctionCallContent fc:
                        // model → functionCall (fallback if no InnerContent)
                        var args = fc.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object?>();
                        parts.Add(new { functionCall = new { name = GeminiFunctionName(fc.PluginName, fc.FunctionName), args } });
                        break;

                    case FunctionResultContent fr:
                        // user → functionResponse
                        parts.Add(new
                        {
                            functionResponse = new
                            {
                                name = GeminiFunctionName(fr.PluginName, fr.FunctionName ?? ""),
                                response = new { result = fr.Result?.ToString() ?? "" }
                            }
                        });
                        break;
                }
            }

            if (parts.Count > 0)
                contents.Add(new { role = geminiRole, parts });
        }

        return (systemInstruction, contents);
    }

    // -------------------------------------------------------------------------
    // Build Gemini functionDeclarations from SK KernelPlugins
    // -------------------------------------------------------------------------
    private static List<object> BuildFunctionDeclarations(Kernel kernel)
    {
        var declarations = new List<object>();

        foreach (var plugin in kernel.Plugins)
        {
            foreach (var function in plugin)
            {
                var metadata = function.Metadata;
                var properties = new Dictionary<string, object>();
                var required = new List<string>();

                foreach (var param in metadata.Parameters)
                {
                    properties[param.Name] = new
                    {
                        type = ToGeminiType(param.ParameterType),
                        description = param.Description ?? ""
                    };

                    if (param.IsRequired)
                        required.Add(param.Name);
                }

                declarations.Add(new
                {
                    name = GeminiFunctionName(plugin.Name, function.Name),
                    description = metadata.Description ?? "",
                    parameters = new
                    {
                        type = "OBJECT",
                        properties,
                        required
                    }
                });
            }
        }

        return declarations;
    }

    // -------------------------------------------------------------------------
    // Parse Gemini response — same ExtractResponseText logic as FileComparer
    // Extended to handle functionCall parts → SK FunctionCallContent
    // -------------------------------------------------------------------------
    private static IReadOnlyList<ChatMessageContent> ParseResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return [new ChatMessageContent(AuthorRole.Assistant, "")];

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts))
            return [new ChatMessageContent(AuthorRole.Assistant, "")];

        var textParts = new StringBuilder();
        var items = new ChatMessageContentItemCollection();
        var hasFunctionCalls = false;

        foreach (var part in parts.EnumerateArray())
        {
            // Skip thought parts — same as FileComparer ExtractResponseText
            if (part.TryGetProperty("thought", out var thought) && thought.GetBoolean())
                continue;

            if (part.TryGetProperty("functionCall", out var fc))
            {
                hasFunctionCalls = true;
                var name = fc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var (pluginName, functionName) = ParseGeminiFunctionName(name);
                var callId = Guid.NewGuid().ToString("N")[..8];

                KernelArguments? args = null;
                if (fc.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                {
                    args = new KernelArguments();
                    foreach (var prop in argsEl.EnumerateObject())
                        args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString()
                            : prop.Value.GetRawText();
                }

                items.Add(new FunctionCallContent(functionName, pluginName, callId, args));
            }
            else if (part.TryGetProperty("text", out var text))
            {
                var txt = text.GetString() ?? "";
                if (!string.IsNullOrEmpty(txt))
                {
                    textParts.Append(txt);
                    items.Add(new TextContent(txt));
                }
            }
        }

        if (hasFunctionCalls)
        {
            var msg = new ChatMessageContent(AuthorRole.Assistant, items);
            // Preserve the raw Gemini content block so BuildContents can re-send it
            // verbatim (including thoughtSignature) when function results are returned.
            // Gemini 400s if thoughtSignature is missing from a prior function call turn.
            msg.InnerContent = content.GetRawText();
            return [msg];
        }

        return [new ChatMessageContent(AuthorRole.Assistant, textParts.ToString())];
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string GeminiFunctionName(string? pluginName, string functionName)
        => string.IsNullOrEmpty(pluginName) ? functionName : $"{pluginName}_{functionName}";

    private static (string PluginName, string FunctionName) ParseGeminiFunctionName(string name)
    {
        var idx = name.IndexOf('_');
        return idx > 0
            ? (name[..idx], name[(idx + 1)..])
            : ("", name);
    }

    private static string ToGeminiType(Type? type)
    {
        if (type == null) return "STRING";
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(int) || t == typeof(long)) return "INTEGER";
        if (t == typeof(double) || t == typeof(float)) return "NUMBER";
        if (t == typeof(bool)) return "BOOLEAN";
        return "STRING";
    }
}
