using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartOrders.Infrastructure.Configuration;

namespace SmartOrders.Infrastructure.NoteGateway;

/// <summary>
/// Custom IChatClient that calls the Note+ gateway (Azure Front Door → Vertex AI Gemini).
/// Implements Microsoft.Extensions.AI.IChatClient for use with Microsoft Agent Framework.
///
/// Same HTTP pattern as FileComparer-Semantic/GeminiLlmService.cs:
///   - Endpoint: {BaseUrl}{modelName}:generateContent
///   - Auth:     API-KEY header + Azure AD Bearer token via FrontDoorTokenService
///
/// Settings via ChatOptions:
///   - ResponseFormat = ChatResponseFormat.Json  → responseMimeType=application/json
///   - MaxOutputTokens                           → generationConfig.maxOutputTokens
///   - AdditionalProperties["thinking_budget"]   → generationConfig.thinkingConfig.thinkingBudget
/// </summary>
public sealed class GeminiChatCompletionService : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly GeminiGatewaySettings _settings;
    private readonly FrontDoorTokenService _tokenService;
    private readonly ILogger<GeminiChatCompletionService> _logger;

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

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = _settings.Endpoint.Replace("{modelName}", _settings.ModelName);
        var url = $"{_settings.BaseUrl}{endpoint}";

        var maxTokens = options?.MaxOutputTokens ?? _settings.MaxTokens;
        var jsonMode = options?.ResponseFormat is ChatResponseFormatJson;

        int? thinkingBudget = null;
        if (options?.AdditionalProperties?.TryGetValue("thinking_budget", out var tb) == true && tb is int tbi)
            thinkingBudget = tbi;

        var genConfig = new Dictionary<string, object>
        {
            ["temperature"] = _settings.Temperature,
            ["maxOutputTokens"] = maxTokens,
            ["topP"] = _settings.TopP,
        };

        if (_settings.Seed.HasValue)
            genConfig["seed"] = _settings.Seed.Value;

        if (thinkingBudget.HasValue)
            genConfig["thinkingConfig"] = new { thinkingBudget = thinkingBudget.Value };
        else if (!string.IsNullOrWhiteSpace(_settings.ThinkingLevel))
            genConfig["thinkingConfig"] = new { thinkingLevel = _settings.ThinkingLevel };

        var messageList = messages.ToList();
        var (systemInstruction, contents) = BuildContents(messageList);

        var hasFunctions = options?.Tools?.Count > 0;
        var functionDeclarations = hasFunctions ? BuildFunctionDeclarations(options!.Tools!) : null;

        // Apply JSON mode only when:
        //   (a) there are no function declarations — safe to use responseMimeType alone, OR
        //   (b) the last message is a tool-result — we're in the final synthesis turn and want
        //       Gemini to produce structured JSON text (not another function call).
        // Applying responseMimeType on the very first turn (user message) WITH tools causes Gemini
        // to return a JSON text representation of function calls instead of native functionCall parts.
        var lastNonSystem = messageList.LastOrDefault(m => m.Role != ChatRole.System);
        var endsWithToolResult = lastNonSystem?.Contents.OfType<FunctionResultContent>().Any() == true;
        if (jsonMode && (functionDeclarations == null || endsWithToolResult))
        {
            genConfig["responseMimeType"] = "application/json";
            if (options?.AdditionalProperties?.TryGetValue("response_schema", out var rs) == true && rs is JsonElement responseSchema)
                genConfig["responseSchema"] = responseSchema;
        }

        // On the synthesis turn (after all tool calls), suppress function declarations.
        // Sending tools alongside responseSchema causes Gemini to ignore the schema —
        // it keeps trying to call functions instead of returning structured JSON.
        if (endsWithToolResult)
            functionDeclarations = null;

        object requestBody;
        if (systemInstruction != null && functionDeclarations != null)
            requestBody = new { system_instruction = systemInstruction, contents, tools = new[] { new { functionDeclarations } }, generationConfig = genConfig };
        else if (systemInstruction != null)
            requestBody = new { system_instruction = systemInstruction, contents, generationConfig = genConfig };
        else if (functionDeclarations != null)
            requestBody = new { contents, tools = new[] { new { functionDeclarations } }, generationConfig = genConfig };
        else
            requestBody = new { contents, generationConfig = genConfig };

        var jsonContent = JsonSerializer.Serialize(requestBody);
        using var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

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

        var chatResponse = ParseResponse(responseBody);
        RecordUsage(responseBody);
        return chatResponse;
    }

    /// <summary>
    /// Parses <c>usageMetadata</c> from the raw Gemini response and records it in the
    /// active <see cref="LlmUsageScope"/> (no-op when called outside a pipeline run).
    /// </summary>
    private static void RecordUsage(string responseBody)
    {
        if (LlmUsageScope.Current is not { } tracker) return;

        var inputTokens = 0;
        var outputTokens = 0;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("usageMetadata", out var usage))
            {
                if (usage.TryGetProperty("promptTokenCount", out var pIn))
                    inputTokens = pIn.GetInt32();
                if (usage.TryGetProperty("candidatesTokenCount", out var pOut))
                    outputTokens = pOut.GetInt32();
            }
        }
        catch { /* non-critical — still record the call */ }

        tracker.RecordCall(inputTokens, outputTokens);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Note+ gateway does not support SSE streaming — fall back to single-response.
        var chatResponse = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in chatResponse.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _httpClient.Dispose();

    // -------------------------------------------------------------------------
    // Build Gemini contents from ME.AI ChatMessages
    // -------------------------------------------------------------------------
    private static (object? SystemInstruction, List<object> Contents) BuildContents(List<ChatMessage> messages)
    {
        // First pass: build callId → functionName lookup (FunctionResultContent has no FunctionName)
        var functionNameByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var msg in messages)
            foreach (var item in msg.Contents)
                if (item is FunctionCallContent fc)
                    functionNameByCallId[fc.CallId] = fc.Name;

        object? systemInstruction = null;
        var contents = new List<object>();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                systemInstruction = new { parts = new[] { new { text = message.Text ?? "" } } };
                continue;
            }

            var geminiRole = message.Role == ChatRole.Assistant ? "model" : "user";

            // Re-use raw Gemini content when available — preserves thoughtSignature.
            // Gemini returns 400 if a prior function-call turn is missing its thoughtSignature.
            if (message.Role == ChatRole.Assistant && message.RawRepresentation is string rawContent)
            {
                var rawPartsNode = System.Text.Json.Nodes.JsonNode.Parse(rawContent)?["parts"];
                if (rawPartsNode != null)
                {
                    contents.Add(new { role = "model", parts = rawPartsNode });
                    continue;
                }
            }

            var parts = new List<object>();

            foreach (var item in message.Contents)
            {
                switch (item)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        parts.Add(new { text = tc.Text });
                        break;

                    case FunctionCallContent fc:
                        var args = fc.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
                        parts.Add(new { functionCall = new { name = fc.Name, args } });
                        break;

                    case FunctionResultContent fr:
                        var funcName = functionNameByCallId.GetValueOrDefault(fr.CallId, "unknown");
                        parts.Add(new
                        {
                            functionResponse = new
                            {
                                name = funcName,
                                response = new { result = fr.Result?.ToString() ?? "" }
                            }
                        });
                        break;
                }
            }

            if (parts.Count == 0 && !string.IsNullOrEmpty(message.Text))
                parts.Add(new { text = message.Text });

            if (parts.Count > 0)
                contents.Add(new { role = geminiRole, parts });
        }

        return (systemInstruction, contents);
    }

    // -------------------------------------------------------------------------
    // Build Gemini functionDeclarations from ME.AI AITool list.
    // Uses AIFunction.JsonSchema (a JsonElement property) — no reflection.
    // -------------------------------------------------------------------------
    private static List<object> BuildFunctionDeclarations(IList<AITool> tools)
    {
        var declarations = new List<object>();

        foreach (var tool in tools)
        {
            if (tool is not AIFunction func) continue;

            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            var schema = func.JsonSchema;
            if (schema.TryGetProperty("properties", out var props))
            {
                foreach (var prop in props.EnumerateObject())
                {
                    var geminiType = "STRING";
                    var description = "";

                    if (prop.Value.TryGetProperty("type", out var typeEl))
                    {
                        // Nullable types generate "type": ["number","null"] — pick the non-null type.
                        if (typeEl.ValueKind == JsonValueKind.Array)
                        {
                            var nonNull = typeEl.EnumerateArray()
                                .Select(t => t.GetString())
                                .FirstOrDefault(t => t != "null");
                            geminiType = ToGeminiType(nonNull);
                        }
                        else
                        {
                            geminiType = ToGeminiType(typeEl.GetString());
                        }
                    }
                    else if (prop.Value.TryGetProperty("anyOf", out var anyOf))
                    {
                        // Some schema generators use anyOf for nullable: [{"type":"number"},{"type":"null"}]
                        var nonNull = anyOf.EnumerateArray()
                            .Select(t => t.TryGetProperty("type", out var tt) ? tt.GetString() : null)
                            .FirstOrDefault(t => t != "null");
                        geminiType = ToGeminiType(nonNull);
                    }

                    if (prop.Value.TryGetProperty("description", out var descEl))
                        description = descEl.GetString() ?? "";

                    properties[prop.Name] = new { type = geminiType, description };
                }
            }

            if (schema.TryGetProperty("required", out var reqEl))
                foreach (var r in reqEl.EnumerateArray())
                    required.Add(r.GetString() ?? "");

            declarations.Add(new
            {
                name = func.Name,
                description = func.Description ?? "",
                parameters = new { type = "OBJECT", properties, required }
            });
        }

        return declarations;
    }

    // -------------------------------------------------------------------------
    // Parse Gemini response → ME.AI ChatResponse
    // -------------------------------------------------------------------------
    private static ChatResponse ParseResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, ""));

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts))
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, ""));

        var items = new List<AIContent>();
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
                var callId = Guid.NewGuid().ToString("N")[..8];

                Dictionary<string, object?>? args = null;
                if (fc.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                {
                    args = new Dictionary<string, object?>();
                    foreach (var prop in argsEl.EnumerateObject())
                        args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString()
                            : (object?)prop.Value.GetRawText();
                }

                items.Add(new FunctionCallContent(callId, name, args));
            }
            else if (part.TryGetProperty("text", out var text))
            {
                var txt = text.GetString() ?? "";
                if (!string.IsNullOrEmpty(txt))
                    items.Add(new TextContent(txt));
            }
        }

        if (hasFunctionCalls)
        {
            var msg = new ChatMessage(ChatRole.Assistant, items);
            // Preserve raw Gemini content block so BuildContents re-sends it verbatim
            // (including thoughtSignature) in subsequent function-result turns.
            msg.RawRepresentation = content.GetRawText();
            return new ChatResponse(msg) { FinishReason = ChatFinishReason.ToolCalls };
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, items));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static string ToGeminiType(string? jsonSchemaType) =>
        jsonSchemaType?.ToLowerInvariant() switch
        {
            "integer" => "INTEGER",
            "number"  => "NUMBER",
            "boolean" => "BOOLEAN",
            _         => "STRING",
        };
}
