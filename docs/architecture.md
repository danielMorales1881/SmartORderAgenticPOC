# Smart Orders — Pipeline Flow

## Pipeline Overview

```mermaid
flowchart TD
    subgraph CLIENT["Client"]
        EHR["EHR Frontend / Provider UI"]
    end

    subgraph API["SmartOrders.Api"]
        OC["OrdersController\nPOST /api/orders/process\nPOST /api/orders/submit"]
        CC["CatalogController\nPOST /api/catalog/index"]
    end

    subgraph PIPELINE["SmartOrdersPipeline  (IPipelineOrchestrator)"]
        direction TB
        S1["Stage 1 · IntentAgent\nChatCompletionAgent · JSON mode\nClinicalText → OrderIntents[]"]
        S2["Stage 2 · MappingAgent\nChatCompletionAgent · tool calls\nOrderIntents → MappedOrders[]"]
        S3["Stage 3 · ValidationService\nPure C# — no LLM\nMappedOrders → ValidatedOrders[]"]
        S4A["Stage 4a · SubmissionPresenterAgent\nNo tools · HITL turn 1\nPresents orders for provider review"]
        S4B["Stage 4b · SubmissionConfirmerAgent\nSubmissionPlugin · HITL turn 2\nSubmits after explicit provider CONFIRM"]

        S1 --> S2 --> S3 --> S4A
        S4A -. "AwaitingConfirmation = true\n(POST /api/orders/submit)" .-> S4B
    end

    subgraph PLUGINS["Plugins  (Tools)"]
        SP["SearchPlugin\nsearch_orders()"]
        MP["MappingPlugin\nmap_diagnosis()\napply_order_defaults()"]
        SUB["SubmissionPlugin\nsubmit_order()\n⚠ HITL — BC-2"]
    end

    subgraph GATEWAY["NoteGateway"]
        GCS["GeminiChatCompletionService\nIChatClient\nHTTP → Note+ Front Door"]
        FDT["FrontDoorTokenService\nMSAL · Azure AD Bearer token"]
    end

    subgraph REPOS["Repositories"]
        QR["QdrantOrderCatalogRepository\nIOrderCatalogRepository\nSemantic vector search"]
        SE["SentenceEmbedder\nall-MiniLM-L6-v2\nONNX Runtime · 384-dim"]
        CI["CatalogIndexer\nSQLite → Qdrant\none-time seed"]
        MQ["MockTwOrderQueueRepository\nITwOrderQueueRepository"]
        RQ["RealTwOrderQueueRepository\nITwOrderQueueRepository"]
    end

    subgraph EXTERNAL["External Services"]
        QDRANT[("Qdrant\nvector DB\ngRPC :6334")]
        SQLITE[("OIDExtract.db\nSQLite · 9,158 rows")]
        APIM["Azure APIM\n→ Vertex AI Gemini"]
        TWOE["TW Order Engine\nOrder Queue"]
    end

    EHR -->|"clinical text / confirmation"| OC
    OC --> PIPELINE
    CC --> CI

    S2 --> SP & MP
    S4B --> SUB

    SP & MP --> QR
    SUB --> MQ
    SUB -.->|"prod"| RQ

    QR --> SE --> QDRANT
    CI --> SQLITE
    CI --> QDRANT
    RQ --> TWOE

    S1 & S2 & S4A & S4B --> GCS
    GCS --> FDT
    GCS -->|"HTTP POST\n:generateContent"| APIM
```

---

## Layer Responsibilities

| Layer | Project | Responsibility |
|-------|---------|---------------|
| **API** | `SmartOrders.Api` | HTTP endpoints, DI root, `Program.cs`, static files |
| **Core** | `SmartOrders.Core` | Domain models (`OrderIntent`, `MappedOrder`, `ValidatedOrder`), interfaces — no framework deps |
| **Pipeline** | `SmartOrders.Infrastructure/Pipeline` | `SmartOrdersPipeline` — sequential orchestration of 4 stages |
| **Agents** | `SmartOrders.Infrastructure/Agents` | `AgentFactory` — creates all `AIAgent` instances |
| **Plugins** | `SmartOrders.Infrastructure/Plugins` | Tool implementations registered via `AIFunctionFactory` |
| **NoteGateway** | `SmartOrders.Infrastructure/NoteGateway` | `GeminiChatCompletionService` + `FrontDoorTokenService` |
| **Repositories** | `SmartOrders.Infrastructure/Repositories` | Qdrant, SQLite, TW Order Queue, ONNX embedder, catalog indexer |

---

## LLM Call Map

| Agent | Mode | Tools | Output |
|-------|------|-------|--------|
| `IntentAgent` | JSON mode (`responseMimeType`) | None | `OrderIntentsJson` |
| `MappingAgent` | Auto tool call | `SearchPlugin`, `MappingPlugin` | `MappedOrdersJson` |
| `SubmissionPresenterAgent` | Text | None | Order summary for provider |
| `SubmissionConfirmerAgent` | Auto tool call | `SubmissionPlugin` | `SubmissionResultsJson` |

---

## Data Flow

```mermaid
sequenceDiagram
    participant UI as Provider UI
    participant API as OrdersController
    participant P as SmartOrdersPipeline
    participant G as Gemini (via APIM)
    participant Q as Qdrant

    UI->>API: POST /api/orders/process { clinicalText }
    API->>P: RunAsync(clinicalText)

    P->>G: IntentAgent — extract intents
    G-->>P: OrderIntents[]

    loop for each intent
        P->>Q: SearchPlugin.SearchOrdersAsync()
        Q-->>P: catalog matches
        P->>G: MappingAgent — map + defaults
    end
    G-->>P: MappedOrders[]

    P->>P: ValidationService (pure C#)
    Note over P: No LLM call

    P->>G: SubmissionPresenterAgent — summarise
    G-->>P: Order summary text

    API-->>UI: PipelineState { AwaitingConfirmation: true, ... }

    UI->>API: POST /api/orders/submit { providerConfirmation }
    API->>P: ConfirmAndSubmitAsync()

    P->>G: SubmissionConfirmerAgent — confirm + submit
    G->>P: calls submit_order() per order
    P-->>API: PipelineState { SubmissionResults }
    API-->>UI: 200 OK
```
