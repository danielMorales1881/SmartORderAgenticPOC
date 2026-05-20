---
name: smart-orders-debug-pipeline
description: >
  Use when the pipeline fails, hangs, or returns wrong results. Examples:
  "Why is IntentAgent returning empty orders?", "MappingAgent is stuck in a loop",
  "Gemini keeps ignoring my response schema", "Qdrant returns no results",
  "SubmissionAgent submitted without confirmation", "The SSE stream disconnects".
---

# Smart Orders .NET — Debug the Pipeline

## How to read pipeline logs

Every stage emits structured log events. Run with `dotnet run` and filter by stage:

```powershell
dotnet run --project src/SmartOrders.Api | Select-String "pipeline_stage|error|intents_extracted|mapping_complete|validation_complete|order_submitted"
```

Key log messages:

| Message | Meaning |
|---|---|
| `pipeline_stage stage=IntentAgent` | Stage 1 starting |
| `intents_extracted count=0` | → short-circuits pipeline, no orders found |
| `pipeline_stage stage=MappingAgent` | Stage 2 starting |
| `mapping_complete raw_length=N` | Stage 2 done; `N=2` = likely empty JSON `[]` |
| `pipeline_stage stage=ValidationService` | Stage 3 starting |
| `validation_complete` | Stage 3 done |
| `pipeline_stage stage=SubmissionAgent` | Stage 4 presenter starting |
| `pipeline_stage stage=SubmissionAgent confirmation` | Stage 4 confirmer starting |
| `order_submitted order_name=... order_id=...` | `SubmitOrderAsync` was called — BC-2 satisfied |

---

## Symptom: IntentAgent returns `count=0` / empty array

### Cause A — Gemini returned text, not JSON

IntentAgent uses `ResponseFormat = ChatResponseFormat.Json` + `response_schema`. If the model
returns a text explanation instead of JSON (e.g. "I cannot determine any orders"), `ParseIntents`
will return `[]`.

**Check**: add a temporary log before `ParseIntents`:
```csharp
logger.LogDebug("intent_raw response={Response}", intentResponse.Text);
```

**Fix**: strengthen the prompt in `Prompts/intent_agent.txt`:
- Add explicit instruction: "Always return a JSON array, even if empty (`[]`)."
- Add a few-shot example with clinical text that maps to zero orders → output `[]`

### Cause B — responseSchema mismatch

The schema in `AgentFactory.s_orderIntentSchema` defines `required = ["orderable_hint", "priority", "confidence"]`.
If the model returns objects missing those fields, `ParseIntents` discards them.

**Check**: print the raw `intentResponse.Text` and compare against the schema.

**Fix**: verify `orderable_hint` (not `orderableHint` or `name`) in the raw JSON.

### Cause C — Wrapped vs. bare array

`ParseIntents` handles both `{"orders": [...]}` and `[...]`. If the model wraps in an unexpected
key (e.g. `{"intents": [...]}`), it falls through to `[]`.

**Fix**: update `ParseIntents` in `SmartOrdersPipeline.cs` to handle the new wrapper key, or
add an explicit instruction in the prompt to not wrap.

---

## Symptom: MappingAgent hangs / tool-call loop never terminates

### Cause — UseFunctionInvocation loop keeps calling tools

`UseFunctionInvocation()` middleware loops until the model returns a non-tool-call response.
If the model keeps generating tool calls (e.g. repeatedly calling `search_orders`), it loops
forever until the `HttpClient` timeout fires (default: `GeminiGatewaySettings.TimeoutMinutes`).

**Check**: log inside `SearchPlugin.SearchOrdersAsync`:
```csharp
logger.LogDebug("search_orders CALL #{Count} text={Text}", Interlocked.Increment(ref _callCount), text);
```

**Fix A**: lower `opts.MaxOutputTokens = 2048` to cap runaway turns.

**Fix B**: add a loop-limit instruction to `Prompts/mapping_agent.txt`:
```
RULES
...
- Call search_orders at most ONCE per order intent. Do NOT re-search if results are returned.
- Do NOT call search_orders if orderable_hint is already in the catalog results.
```

**Fix C**: add a timeout guard in `SmartOrdersPipeline.RunAsync`:
```csharp
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
var mappingResponse = await mappingAgent.RunAsync(..., cancellationToken: timeoutCts.Token);
```

---

## Symptom: MappingAgent returns wrong field names (e.g. `order_id` instead of `item_id`)

### Cause — responseSchema ignored on synthesis turn

Gemini ignores `responseSchema` when `functionDeclarations` are present on the same turn.
`GeminiChatCompletionService` nullifies `functionDeclarations` on the synthesis turn (when the
last message contains tool results) — but only if `endsWithToolResult` is detected correctly.

**Check**: log the raw `mappingResponse.Text`:
```csharp
logger.LogDebug("mapping_raw text={Text}", mappingResponse.Text?.Substring(0, Math.Min(500, mappingResponse.Text.Length)));
```

**Fix**: verify that `GeminiChatCompletionService.GetResponseAsync` correctly detects
`endsWithToolResult` — the last `ChatMessage` must contain `FunctionResultContent` items.
If the middleware is not writing tool results as `FunctionResultContent`, add a breakpoint
in `GeminiChatCompletionService.BuildContents`.

---

## Symptom: ValidationService rejects all orders with "missing item_id"

### Cause — MappingAgent returned `null` or omitted `item_id`

`ValidationService` requires `item_id` for all order categories. If the catalog search returned
no match (`SearchPlugin` returned `[]`), MappingAgent may set `item_id: null` — which fails validation.

**Check**: inspect `state.MappedOrdersJson` in the `/process` response body.

**Fix A**: seed the Qdrant index if it's empty:
```
POST /api/catalog/index
```

**Fix B**: update the MappingAgent prompt to handle no-match gracefully:
```
RULES
- If search_orders returns no results, set item_id to null and add a "no_catalog_match": true flag.
- Do NOT invent item IDs.
```

**Fix C**: update `ValidationService` to emit a softer warning (not hard failure) for `item_id: null`
so the provider can still review and manually select an order.

---

## Symptom: Gemini API error (401, 403, 429, 500)

| Status | Meaning | Fix |
|---|---|---|
| 401 Unauthorized | `API-KEY` header missing or wrong | Check `GeminiGateway:ApiKey` in `appsettings.Development.json` |
| 401 + Bearer | Azure AD token expired | `FrontDoorTokenService` should auto-refresh — check MSAL cache logs |
| 403 Forbidden | APIM subscription key wrong or quota exceeded | Check `GeminiGateway:SubscriptionKey` |
| 429 Too Many Requests | Rate limit hit | Add retry with exponential backoff in `GeminiChatCompletionService` |
| 500 from Gemini | Model overloaded or bad request body | Log `jsonContent` in `GeminiChatCompletionService.GetResponseAsync` before `HttpClient.PostAsync` |

**Quick connection test** (bypasses all agents):
```http
POST /api/catalog/index
```
This triggers `SentenceEmbedder` (ONNX) and `QdrantClient` — not Gemini. If it succeeds, the
non-LLM path is healthy. If `POST /api/orders/process` fails immediately, it's likely Gemini.

---

## Symptom: Qdrant returns 0 results for every search

### Cause A — Collection not seeded

```http
POST /api/catalog/index
```
Wait for `200 OK { "indexed": 9158 }`. This is a one-time operation per Qdrant instance.

### Cause B — Qdrant not running

```powershell
docker ps | Select-String qdrant
```
If empty: `docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant`

### Cause C — Wrong gRPC port

`QdrantClient` uses gRPC port `6334`, not the REST port `6333`. Check `SmartOrders:QdrantPort` in `appsettings.json`.

### Cause D — Embedding dimension mismatch

`SentenceEmbedder` produces 384-dim vectors. If the Qdrant collection was created with a
different dimension (e.g. a leftover test collection), searches return nothing or error.

**Fix**: delete and recreate the collection:
```
DELETE /api/catalog/index  ← add this endpoint if not present
POST /api/catalog/index
```

---

## Symptom: SubmissionAgent called `submit_order` without provider confirmation (BC-2 violation)

This is a critical safety failure. Steps to diagnose:

1. Check `submissionPresenterAgent` in `AgentFactory.CreateSubmissionPresenterAgent` — it must have **NO tools**.
   Any tool present on this agent allows it to submit without confirmation.

2. Check `SubmissionPlugin` is only wired to `submissionConfirmerAgent` (created via `CreateSubmissionAgent`).

3. Check `Prompts/submission_agent.txt` — it must contain:
   ```
   RULES
   ...
   Do NOT call submit_order unless the Provider confirmation field is explicitly present in the input.
   ```

4. Check `SmartOrdersPipeline.ConfirmAndSubmitAsync` — `submissionConfirmerAgent.RunAsync` is only
   called from this method, which is only triggered by `POST /api/orders/submit`.

---

## Symptom: SSE stream (`/api/orders/stream`) disconnects before completion

### Cause A — Client timeout

Browser SSE connections have a 30-60s browser timeout. Long `MappingAgent` calls (with many tool
iterations) can exceed this. Fix: add `progress.Report(heartbeat)` calls between tool invocations,
or send a keep-alive comment every 15s.

### Cause B — Channel full (DropOldest)

The channel is bounded at 200 events with `DropOldest`. If the consumer (browser) is slow,
early progress events are silently dropped. Increase the bound or switch to `Wait`:
```csharp
new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest }
```

### Cause C — `CancellationToken` cancelled by client disconnect

`ct` in `OrdersController.StreamAsync` is the request cancellation token. If the client
disconnects, `ct` is cancelled, which propagates into `pipeline.RunAsync`. This is correct
behaviour — the pipeline is aborted cleanly.

---

## Useful HTTP test sequences

```http
### Step 1 — seed catalog (run once)
POST http://localhost:5000/api/catalog/index

### Step 2 — run full pipeline
POST http://localhost:5000/api/orders/process
Content-Type: application/json

{ "clinicalText": "Order CBC and chest X-ray for hypertension follow-up" }

### Step 3 — confirm and submit (copy ValidatedOrdersJson from step 2 response)
POST http://localhost:5000/api/orders/submit
Content-Type: application/json

{
  "validatedOrdersJson": "<paste from /process response>",
  "providerConfirmation": "submit all"
}

### Step 4 — streaming equivalent of step 2
POST http://localhost:5000/api/orders/stream
Content-Type: application/json

{ "clinicalText": "Order CBC and chest X-ray for hypertension follow-up" }
```

The `.http` file at `src/SmartOrders.Api/SmartOrders.Api.http` contains these requests pre-filled.
