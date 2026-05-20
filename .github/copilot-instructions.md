# Smart Orders .NET — GitHub Copilot Instructions

## Project Overview

**Smart Orders** is an AI-assisted clinical order entry system for TouchWorks EHR (release 26.3),
owned by the V11 Ambient team at Altera Digital Health. This repository (`smart-orders-dotnet`) is the
**C# .NET 10 orchestration layer** — it runs as an ASP.NET Core Web API that hosts the full
Smart Orders multi-agent pipeline using **Microsoft Agent Framework** (`Microsoft.Agents.AI`).

This is a direct functional port of the Python ADK sidecar (`smart-orders-adk`). Every agent, tool,
and pipeline stage maps 1-to-1 to a Python equivalent.

Epic: [#9224227 — Smart Orders (TouchWorks EHR)](https://almdivapp1.rd.allscripts.com/tfs/projects/TWEHR/_workitems/edit/9224227)

---

## Solution Structure

```
smart-orders-dotnet/
├── src/
│   ├── SmartOrders.Api/           ← ASP.NET Core Minimal controllers, DI root, Program.cs
│   ├── SmartOrders.Core/          ← Domain models, interfaces (no framework dependencies)
│   └── SmartOrders.Infrastructure/
│       ├── Agents/                ← AgentFactory — ChatCompletionAgent factory methods
│       ├── Configuration/         ← Settings POCOs, GeminiExecutionSettings
│       ├── NoteGateway/           ← FrontDoorTokenService, GeminiChatCompletionService
│       ├── Pipeline/              ← SmartOrdersPipeline (SequentialAgent equivalent)
│       ├── Plugins/               ← SK plugins (equivalent to ADK tools)
│       │   ├── SearchPlugin       ← search_orders tool
│       │   ├── MappingPlugin      ← map_diagnosis + apply_order_defaults tools
│       │   ├── ValidationPlugin   ← validate_order_fields tool
│       │   └── SubmissionPlugin   ← submit_order tool (HITL enforced)
│       ├── Repositories/
│       │   ├── QdrantOrderCatalogRepository  ← Semantic vector search (Qdrant + ONNX)
│       │   ├── SqliteOrderCatalogRepository  ← SQLite fallback
│       │   ├── CatalogIndexer     ← One-time SQLite → Qdrant indexer
│       │   ├── SentenceEmbedder   ← all-MiniLM-L6-v2 via ONNX Runtime
│       │   ├── MockTwOrderQueueRepository
│       │   └── RealTwOrderQueueRepository
│       └── Services/
│           └── ValidationService  ← Pure C# equivalent of PureValidationAgent
```

---

## Architecture: Python ADK ↔ .NET Semantic Kernel Mapping

| Python (smart-orders-adk) | .NET (smart-orders-dotnet) | Notes |
|---|---|---|
| `SequentialAgent` | `SmartOrdersPipeline` | Sequential execution, explicit state passing |
| `LlmAgent(output_schema=...)` | `ChatCompletionAgent` + `GeminiExecutionSettings { JsonMode = true }` | Structured JSON output |
| `LlmAgent(tools=[...])` | `ChatCompletionAgent` + `FunctionChoiceBehavior.Auto()` | Auto tool call |
| `tool_context.request_confirmation()` | Prompt instruction — agent presents orders, waits for explicit `CONFIRM` | No SK equivalent; enforced via prompt |
| `session.state["order_intents"]` | `PipelineState.OrderIntentsJson` + inline context in next message | Explicit state passing |
| `search_orders()` | `SearchPlugin.SearchOrdersAsync()` | Same signature |
| `map_diagnosis()` | `MappingPlugin.MapDiagnosisAsync()` | Same behavior |
| `apply_defaults()` | `MappingPlugin.ApplyOrderDefaultsAsync()` | Same behavior |
| `validate_order_fields()` | `ValidationService.ValidateBatchAsync()` (pure C#) | No LLM needed |
| `submit_orders_tool()` | `SubmissionPlugin.SubmitOrderAsync()` | HITL enforced by prompt |
| `QdrantClient(path=...)` embedded | `QdrantClient(host, port)` gRPC | .NET requires running Qdrant server |
| `sentence_transformers` | `SentenceEmbedder` (ONNX Runtime + Microsoft.ML.Tokenizers) | Identical 384-dim output |
| `scripts/index_catalog.py` | `CatalogIndexer` + `POST /api/catalog/index` | Same SQLite → Qdrant flow |

### Pipeline stages

```
POST /api/orders/process
        │
        ▼
Stage 1: IntentAgent (ChatCompletionAgent, JsonMode=true, MaxOutputTokens=512)
   → Extracts structured order intents from clinical text
   → Writes: PipelineState.OrderIntents + OrderIntentsJson
        │
        ▼
Stage 2: MappingAgent (ChatCompletionAgent, FunctionChoiceBehavior.Auto, MaxOutputTokens=2048)
   → Tools: SearchPlugin.SearchOrdersAsync, MappingPlugin.MapDiagnosisAsync, MappingPlugin.ApplyOrderDefaultsAsync
   → Writes: PipelineState.MappedOrdersJson
        │
        ▼
Stage 3: ValidationService (pure C#, no LLM)
   → Validates required fields per order category
   → Writes: PipelineState.ValidatedOrdersJson
        │
        ▼
Stage 4: SubmissionAgent (ChatCompletionAgent, FunctionChoiceBehavior.Auto)
   → Tool: SubmissionPlugin.SubmitOrderAsync (HITL: only after provider confirmation)
   → Writes: PipelineState.SubmissionResultsJson
```

---

## LLM Connection

All agents use `GeminiChatCompletionService` — a custom `IChatCompletionService` that routes via
**Note+ Front Door → Azure APIM → Vertex AI Gemini**.

```csharp
// Configuration (appsettings.json / Key Vault)
"GeminiGateway": {
    "ApiBase": "https://<apim-host>/gemini/v1",     // APIM gateway URL
    "SubscriptionKey": "<from Key Vault>",           // Ocp-Apim-Subscription-Key
    "Model": "gemini-2.5-flash-001",                 // Never hardcode — read from config
    "MaxTokens": 2048
}

// Never instantiate a raw HttpClient manually for Gemini calls.
// Always use GeminiChatCompletionService via DI.
```

---

## Qdrant Setup (Local Dev)

The order catalog uses semantic vector search. Run Qdrant locally before starting:

```bash
docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant
```

Then seed the index (run once):

```
POST /api/catalog/index
```

This reads `OIDExtract.db` (SQLite, 9,158 rows), embeds each row with `all-MiniLM-L6-v2`,
and upserts into Qdrant collection `tw_orders` (384-dim cosine).

### ONNX model setup (run once — download from HuggingFace)

The same `all-MiniLM-L6-v2` model used by the Python project has a pre-built ONNX version
on HuggingFace. Download directly — no Python toolchain needed:

```powershell
New-Item -ItemType Directory -Force "data\all-MiniLM-L6-v2-onnx"
Invoke-WebRequest -Uri "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt" -OutFile "data\all-MiniLM-L6-v2-onnx\vocab.txt"
Invoke-WebRequest -Uri "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx" -OutFile "data\all-MiniLM-L6-v2-onnx\model.onnx"
```

Already committed to `data/` in this repo — no action needed for local dev.

Set in `appsettings.json`:

```json
"SmartOrders": {
    "OnnxVocabPath": "data/all-MiniLM-L6-v2-onnx/vocab.txt",
    "OnnxModelPath": "data/all-MiniLM-L6-v2-onnx/model.onnx",
    "QdrantHost": "localhost",
    "QdrantPort": 6334
}
```

---

## Business Constraints (hard rules — never violate)

- **BC-2**: No order is submitted without explicit provider confirmation. `SubmissionPlugin.SubmitOrderAsync` must ONLY be called after the provider has explicitly confirmed. The submission agent prompt enforces this.
- **BC-3**: TW Order Engine validation (medical necessity, duplicates, formulary) is never bypassed.
- **BC-4**: No predictive/unsolicited recommendations. Only interpret what the provider explicitly says/accepts.
- **BC-5**: Phase 1 does NOT handle medication DUR/interaction checking.
- **BC-9**: LLM model name is never hardcoded — always read from `GeminiGatewaySettings.Model` (config/Key Vault).

---

## Coding Conventions

### C# / .NET

- **Target framework**: `net10.0` (LTS). `pyproject.toml` equivalent = `.csproj`.
- **SOLID**: Single Responsibility on every class. Interfaces in `SmartOrders.Core`; implementations in `SmartOrders.Infrastructure`.
- **DI**: All dependencies injected via constructor. No service locator.
- **Async**: All I/O is `async`/`await`. Never `.Result` or `.Wait()` unless inside a `Dispose()` override.
- **Settings**: All configuration via `IOptions<T>` bound to `appsettings.json` sections. No `Environment.GetEnvironmentVariable()` directly in business code.
- **Credentials**: Never hardcode. Always Key Vault → `appsettings.json` → `IOptions<T>`.
- **Logging**: Structured logging via `ILogger<T>`. Use `LogDebug` for per-request telemetry, `LogInformation` for pipeline milestones, `LogError` for exceptions.

### Microsoft Agent Framework patterns (Microsoft.Agents.AI v1.6.1)

This project uses **Microsoft Agent Framework** — NOT Semantic Kernel. Do NOT import `Microsoft.SemanticKernel`.

- **Agents**: Always created via `AgentFactory` static methods — never inline. Factory returns `AIAgent`.
- **Plugins**: Plain `async Task<string>` methods with `[Description]` attributes — no `[KernelFunction]`.
- **Tools**: Registered at agent creation via `AIFunctionFactory.Create((Func<...>)plugin.Method)`. No Kernel needed.
- **JSON output**: `client.AsBuilder().ConfigureOptions(opts => { opts.ResponseFormat = ChatResponseFormat.Json; opts.AdditionalProperties["response_schema"] = schema; })` (equivalent to Python `output_schema`).
- **Tool calls**: `.UseFunctionInvocation()` middleware on `IChatClientBuilder` (equivalent to Python `tools=[...]`).
- **Run agent**: `await agent.RunAsync(inputText, cancellationToken: ct)` → returns `AgentResponse` with `.Text`.
- **State passing**: Pass upstream output as plain text in the next agent's input: `$"order_intents:\n{state.OrderIntentsJson}"`.

### Qdrant .NET client

- Use `Qdrant.Client` NuGet package. Connect via gRPC: `new QdrantClient(host, port)`.
- **Search**: `await client.SearchAsync(collectionName, vector, limit, payloadSelector, ct)` — returns `IReadOnlyList<ScoredPoint>`.
- **Upsert**: `await client.UpsertAsync(collectionName, points)` — `points` is `IReadOnlyList<PointStruct>`.
- **Payload access**: `pt.Payload.TryGetValue("key", out var v)` → `v.StringValue`.
- Do NOT use `QdrantClient(path=...)` — that is Python-only embedded mode. .NET requires a running Qdrant server.

### Order categories (canonical — never deviate)

```csharp
public static readonly string[] OrderCategories = ["Lab", "Imaging", "Diagnostic Orders", "Referrals", "FollowUp Orders", "Medications"];
```

These align exactly with the `OIDExtract.db` schema's `Order Category` column.

---

## NuGet Package Reference

| Layer | Package | Version |
|---|---|---|
| Infrastructure | `Microsoft.Agents.AI` | 1.6.1 |
| Infrastructure | `Qdrant.Client` | 1.13.0 |
| Infrastructure | `Microsoft.ML.OnnxRuntime` | 1.21.0 |
| Infrastructure | `Microsoft.ML.Tokenizers` | 2.0.0 |
| Infrastructure | `Microsoft.Data.Sqlite` | 10.0.8 |
| Infrastructure | `Microsoft.Identity.Client` (MSAL) | 4.84.1 |

---

## Environment / Configuration

| Key | Section | Required | Description |
|---|---|---|---|
| `GeminiGateway:ApiBase` | GeminiGateway | Yes | APIM gateway base URL |
| `GeminiGateway:SubscriptionKey` | GeminiGateway | Yes | APIM subscription key |
| `GeminiGateway:Model` | GeminiGateway | No | Gemini model ID (default: `gemini-2.5-flash-001`) |
| `SmartOrders:QdrantHost` | SmartOrders | No | Qdrant host (default: `localhost`) |
| `SmartOrders:QdrantPort` | SmartOrders | No | Qdrant gRPC port (default: `6334`) |
| `SmartOrders:OnnxVocabPath` | SmartOrders | Yes | Path to `vocab.txt` |
| `SmartOrders:OnnxModelPath` | SmartOrders | Yes | Path to `model.onnx` |
| `SmartOrders:OrdersDbPath` | SmartOrders | No | Path to `OIDExtract.db` (default: `data/OIDExtract.db`) |
| `SmartOrders:UseMockQueue` | SmartOrders | No | `true` = mock TW queue (default: `true`) |
| `SmartOrders:AmbientConfidenceThreshold` | SmartOrders | No | Float 0–1 (default: `0.8`) |

---

## Testing

- Unit tests: `tests/SmartOrders.Unit/` — mock all external I/O. Use `NSubstitute`.
- Integration tests: `tests/SmartOrders.Integration/` — real pipeline via `WebApplicationFactory<Program>`. Requires `.env`-equivalent local `appsettings.Development.json`.
- Test runner: `dotnet test`
- Coverage threshold: 80% (enforced in CI).
- Framework: `xUnit` + `NSubstitute` + `FluentAssertions`.

---

## What NOT to do

- Do NOT modify `.github/workflows/ci.yml` or `deploy.yml` without consulting Derek (DevOps/SM).
- Do NOT commit directly to `main` — all work is on `tw-smart-orders-axiom` feature branch.
- Do NOT hardcode model names, API keys, or connection strings.
- Do NOT bypass TW Order Engine validation in `SubmissionPlugin`.
- Do NOT call `SubmitOrderAsync` without prior provider confirmation — BC-2 violation.
- Do NOT use `.Result` or `.Wait()` on async methods.
- Do NOT add framework imports to `SmartOrders.Core` — it must remain framework-free.
- Do NOT use `QdrantClient(path=...)` — not supported in .NET client.
- Do NOT use `[KernelFunction]`, `ChatCompletionAgent`, `Kernel`, or `GeminiExecutionSettings` — those are Semantic Kernel APIs; this project uses Microsoft Agent Framework (`Microsoft.Agents.AI`).
