# Smart Orders — System Architecture

## Solution Layers

```mermaid
flowchart TB
    classDef client    fill:#4A90D9,stroke:#2c6fad,color:#fff,font-weight:bold
    classDef api       fill:#5BA85B,stroke:#3d7a3d,color:#fff,font-weight:bold
    classDef infra     fill:#8B5CF6,stroke:#6d3fc8,color:#fff,font-weight:bold
    classDef core      fill:#F59E0B,stroke:#c47d09,color:#fff,font-weight:bold
    classDef ext       fill:#6B7280,stroke:#4b5563,color:#fff,font-weight:bold
    classDef extai     fill:#DB4437,stroke:#a83228,color:#fff,font-weight:bold

    PROVIDER(["👤 Clinical Provider\n(TouchWorks EHR)"]):::client

    subgraph API["  SmartOrders.Api  ·  ASP.NET Core  ·  net10.0  "]
        OC["OrdersController\nPOST /api/orders/process\nPOST /api/orders/submit"]:::api
        CC["CatalogController\nPOST /api/catalog/index"]:::api
        UI["Static UI\nwwwroot/"]:::api
    end

    subgraph INFRA["  SmartOrders.Infrastructure  ·  Class Library  "]
        direction LR

        subgraph PIPE["Pipeline & Agents"]
            P["SmartOrdersPipeline\nIPipelineOrchestrator"]:::infra
            AF["AgentFactory\n4 × AIAgent"]:::infra
        end

        subgraph TOOLS["Plugins  (Tools)"]
            SP["SearchPlugin\nsearch_orders()"]:::infra
            MP["MappingPlugin\nmap_diagnosis()\napply_order_defaults()"]:::infra
            SUB["SubmissionPlugin\nsubmit_order() ⚠ HITL"]:::infra
            VS["ValidationService\npure C# — no LLM"]:::infra
        end

        subgraph GW["NoteGateway"]
            GCS["GeminiChatCompletionService\nIChatClient"]:::infra
            FDT["FrontDoorTokenService\nMSAL · Azure AD"]:::infra
        end

        subgraph REPO["Repositories"]
            QR["QdrantOrderCatalogRepository\nIOrderCatalogRepository"]:::infra
            SE["SentenceEmbedder\nall-MiniLM-L6-v2 · ONNX"]:::infra
            CI["CatalogIndexer\nSQLite → Qdrant"]:::infra
            TQ["TwOrderQueueRepository\nITwOrderQueueRepository\nMock / Real"]:::infra
        end
    end

    subgraph CORE["  SmartOrders.Core  ·  Class Library  ·  no framework deps  "]
        direction LR
        DM["Domain Models\nOrderIntent · MappedOrder\nValidatedOrder · Errors"]:::core
        IF["Interfaces\nIPipelineOrchestrator\nIOrderCatalogRepository\nITwOrderQueueRepository"]:::core
        PS["PipelineState\n+ LlmUsageScope"]:::core
    end

    subgraph EXTERNAL["  External Services  "]
        direction LR
        GEMINI["☁ Vertex AI Gemini\nvia Note+ Front Door\n→ Azure APIM"]:::extai
        QDRANT[("🗄 Qdrant\nVector DB\ngRPC :6334")]:::ext
        SQLITE[("📄 OIDExtract.db\nSQLite · 9,158 rows")]:::ext
        TWOE["🏥 TW Order Engine\nOrder Queue"]:::ext
        AAD["🔐 Azure AD\nOAuth 2.0 / MSAL"]:::ext
    end

    PROVIDER -->|HTTPS| OC
    OC --> P
    CC --> CI

    P --> AF
    AF --> SP & MP & SUB
    P --> VS

    SP & MP --> QR
    QR --> SE
    SUB --> TQ

    AF --> GCS
    GCS --> FDT

    GCS -->|"HTTP POST :generateContent"| GEMINI
    FDT -->|"client credentials"| AAD
    SE -->|"gRPC"| QDRANT
    CI --> SQLITE
    CI -->|"upsert vectors"| QDRANT
    TQ -->|"HTTPS"| TWOE

    INFRA -.->|implements| CORE
    API -.->|references| CORE
    API -.->|references| INFRA
```

---

## Deployment View

```mermaid
flowchart LR
    classDef box fill:#1e293b,stroke:#475569,color:#e2e8f0,font-weight:bold
    classDef cloud fill:#0f4c81,stroke:#1d6fa5,color:#fff
    classDef local fill:#14532d,stroke:#166534,color:#fff

    subgraph DEV["Developer Machine  /  Container"]
        API2["SmartOrders.Api\n:5101"]:::box
        QDRANT2[("Qdrant\nDocker\n:6333 HTTP\n:6334 gRPC")]:::local
        DB2[("OIDExtract.db\ndata/")]:::local
        ONNX["model.onnx\nvocab.txt\ndata/all-MiniLM-L6-v2-onnx/"]:::local
    end

    subgraph AZURE["Azure Cloud"]
        FD["Note+ Front Door\n(Azure Front Door)"]:::cloud
        APIM2["Azure APIM\nGemini gateway"]:::cloud
        VERTEX["Vertex AI\nGemini 2.5 Flash"]:::cloud
        AAD2["Azure AD\nApp Registration"]:::cloud
    end

    subgraph TW["TouchWorks EHR"]
        TWE2["TW Order Engine\nOrder Queue"]:::box
    end

    API2 -->|"HTTP POST"| FD
    FD --> APIM2 --> VERTEX
    API2 -->|"OAuth 2.0"| AAD2
    API2 -->|"gRPC"| QDRANT2
    API2 -->|"file I/O"| DB2
    API2 -->|"ONNX Runtime"| ONNX
    API2 -->|"HTTPS"| TWE2
```

---

## NuGet Dependencies

| Project | Key Packages |
|---------|-------------|
| `SmartOrders.Api` | `Microsoft.AspNetCore` · `Scalar.AspNetCore` |
| `SmartOrders.Infrastructure` | `Microsoft.Agents.AI 1.6.1` · `Qdrant.Client 1.13.0` · `Microsoft.ML.OnnxRuntime 1.21.0` · `Microsoft.ML.Tokenizers 2.0.0` · `Microsoft.Data.Sqlite 10.0.8` · `Microsoft.Identity.Client 4.84.1` |
| `SmartOrders.Core` | _(none — framework-free)_ |
