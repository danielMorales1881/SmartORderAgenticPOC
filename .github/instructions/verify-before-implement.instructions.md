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

### Microsoft.Agents.AI (v1.6.1) — Microsoft Agent Framework

This project uses **Microsoft Agent Framework**, NOT Semantic Kernel. Key APIs (verified May 2026):

- `IChatClient.AsBuilder()` → `IChatClientBuilder` — configure per-agent options and middleware.
- `.ConfigureOptions(opts => { opts.ResponseFormat = ...; opts.MaxOutputTokens = N; })` → sets `ChatOptions` on every call.
- `.UseFunctionInvocation()` → enables the automatic tool-call loop middleware.
- `.Build()` → returns `IChatClient` with middleware applied.
- `client.AsAIAgent(name, instructions, tools?)` → creates `AIAgent`.
- `await agent.RunAsync(inputText, cancellationToken: ct)` → runs agent, returns `AgentResponse` with `.Text`.
- `AIFunctionFactory.Create((Func<...>)plugin.MethodAsync)` → wraps a method as `AITool`.
- Cast to exact delegate `(Func<string, double?, int, CancellationToken, Task<string>>)method` to disambiguate overloads.

Do NOT use `ChatCompletionAgent`, `KernelArguments`, `FunctionChoiceBehavior.Auto()`, or `kernel.Plugins.AddFromObject` — those are Semantic Kernel APIs not present in this project.

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
