---
applyTo: "src/**/*.cs"
description: "Verify library APIs and types before implementing — consult docs before assuming"
---

# Verify Before Implementing

When unsure about the correct way to do something (API usage, type signatures, patterns, model availability), ALWAYS:

1. **Context7 MCP first** — `mcp_context7_resolve-library-id` + `mcp_context7_query-docs` for library questions (Semantic Kernel, Qdrant.Client, ONNX Runtime, ML.Tokenizers).
2. **Fetch the source** — GitHub source for the installed package version (check `.csproj` for version numbers).
3. **Check IntelliSense errors** — use `get_errors` tool after editing to catch type mismatches immediately.

## Known API facts (verified May 2026)

### Qdrant.Client for .NET (v1.13.0 — v1.18.0)

- `SearchAsync(collectionName, vector, limit, payloadSelector, ct)` → **still valid** in .NET client.
- `QueryAsync(...)` → also valid (newer universal API), use when you need hybrid/multi-stage queries.
- `UpsertAsync(collectionName, IReadOnlyList<PointStruct> points)` → valid.
- `GetCollectionInfoAsync(collectionName, ct)` → returns `CollectionInfo` with `PointsCount` (`ulong`).
- `CollectionExistsAsync(collectionName, ct)` → returns `bool` directly.
- **NOTE**: `QdrantClient(path=...)` is Python-only embedded mode. .NET ALWAYS uses `new QdrantClient(host, port)`.

### Microsoft.SemanticKernel (v1.76.0)

- `ChatCompletionAgent.InvokeAsync(history, ct)` → `IAsyncEnumerable<ChatMessageContent>` (iterate with `await foreach`).
- `FunctionChoiceBehavior.Auto()` → enables automatic tool selection.
- `k.Plugins.AddFromObject(instance, "PluginName")` → registers plugin instance.
- Never call `kernel.InvokeAsync` for agent execution — use `agent.InvokeAsync(history, ct)`.

### Microsoft.ML.Tokenizers (v1.0.3)

- `BertTokenizer.Create(vocabPath, options)` → creates tokenizer.
- `_tokenizer.EncodeToIds(text, addSpecialTokens: true, considerNormalization: true)` → `IReadOnlyList<int>`.

### Microsoft.ML.OnnxRuntime (v1.21.0)

- `new InferenceSession(modelPath)` → loads model.
- `session.Run(inputs)` → `IDisposableReadOnlyCollection<DisposableNamedOnnxValue>`.
- Output name for optimum-exported all-MiniLM-L6-v2: `"last_hidden_state"` (not `"output"` or other).

## Anti-patterns to avoid

- Never `.Result` or `.Wait()` on async methods — deadlock risk.
- Never `Environment.GetEnvironmentVariable()` in business code — use `IOptions<T>`.
- Never instantiate `QdrantClient` inside a request handler — use the registered singleton.
- Never hardcode model names — read from `GeminiGatewaySettings.Model`.
