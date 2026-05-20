---
name: smart-orders-run-dev
description: >
  Use when you need to run, start, or debug the Smart Orders .NET server locally. Examples:
  "Run the server", "Start the API", "How do I test this locally?", "Launch the dev server".
---

# Smart Orders .NET — Run Locally

## Prerequisites checklist

Before starting, ensure all of these are in place:

### 1. Qdrant running (vector search)

```bash
docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant
```

Verify: open http://localhost:6333/dashboard in a browser.

### 2. ONNX model files (all-MiniLM-L6-v2)

**What this is**: `all-MiniLM-L6-v2` is the same embedding model used by the Python project
(`sentence-transformers`). ONNX is a universal model format — ONNX Runtime runs it in .NET
with no Python dependency. The HuggingFace Hub hosts a pre-built ONNX version (90 MB, full precision).

Download directly into the project (run once):

```powershell
New-Item -ItemType Directory -Force "data\all-MiniLM-L6-v2-onnx"

# Vocabulary file (~226 KB) — used by BertTokenizer in SentenceEmbedder.cs
Invoke-WebRequest `
    -Uri "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt" `
    -OutFile "data\all-MiniLM-L6-v2-onnx\vocab.txt"

# Model file (~90 MB) — neural network weights, run by ONNX Runtime
Invoke-WebRequest `
    -Uri "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx" `
    -OutFile "data\all-MiniLM-L6-v2-onnx\model.onnx"
```

The default paths in `appsettings.json` already point to these locations:
```json
"SmartOrders": {
    "OnnxVocabPath": "data/all-MiniLM-L6-v2-onnx/vocab.txt",
    "OnnxModelPath": "data/all-MiniLM-L6-v2-onnx/model.onnx"
}
```

> **Why not use Python to export?** The HuggingFace version is the official pre-exported model —
> identical output to `sentence-transformers`, no Python toolchain needed.

### 3. OIDExtract.db (order catalog)

Copy `data/OIDExtract.db` from the Python project to `smart-orders-dotnet/data/OIDExtract.db`,
or set `SmartOrders:OrdersDbPath` in `appsettings.Development.json`.

### 4. appsettings.Development.json (secrets)

Create `src/SmartOrders.Api/appsettings.Development.json` (gitignored):

```json
{
    "GeminiGateway": {
        "ApiBase": "https://<your-apim>.azure-api.net/gemini/v1",
        "SubscriptionKey": "<your-key>",
        "Model": "gemini-2.5-flash-001"
    },
    "FrontDoor": {
        "TenantId": "<tenant>",
        "ClientId": "<client-id>",
        "ClientSecret": "<secret>",
        "Scope": "https://<resource>/.default"
    }
}
```

## Start the API

```bash
cd smart-orders-dotnet/src/SmartOrders.Api
dotnet run
```

API will start at https://localhost:7xxx (see `launchSettings.json` for exact port).

## Seed the Qdrant index (first time only)

After the API is running:

```bash
curl -X POST https://localhost:7xxx/api/catalog/index
```

This reads `OIDExtract.db`, embeds all 9,158 orders with all-MiniLM-L6-v2, and upserts them into
the `tw_orders` Qdrant collection. Takes ~2-5 minutes on first run. Subsequent calls are no-ops.

## Test the pipeline

```bash
curl -X POST https://localhost:7xxx/api/orders/process \
  -H "Content-Type: application/json" \
  -d '{"clinicalText": "Order CBC with diff and a chest X-ray. Patient has hypertension."}'
```

## OpenAPI UI

Visit https://localhost:7xxx/openapi/v1.json or use the `.http` file at `src/SmartOrders.Api/SmartOrders.Api.http`.

## Troubleshooting

| Problem | Fix |
|---|---|
| `FileNotFoundException: ONNX model not found` | Export the ONNX model (Step 2 above) |
| `RpcException: Connection refused :6334` | Start Qdrant Docker container |
| `CatalogSearchError: Qdrant storage not found` | Run `POST /api/catalog/index` |
| `401 Unauthorized` from Gemini gateway | Check `GeminiGateway:SubscriptionKey` in appsettings |
| `JsonException` from IntentAgent | Check `GeminiGateway:Model` is a valid Gemini model ID |
