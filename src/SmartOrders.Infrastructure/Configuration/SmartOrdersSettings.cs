namespace SmartOrders.Infrastructure.Configuration;

/// <summary>
/// Application settings — validated at startup via IOptions.
/// Mirrors Python's pydantic-settings Settings class.
/// </summary>
public sealed class SmartOrdersSettings
{
    public const string SectionName = "SmartOrders";

    // Data
    public string OrdersDbPath { get; set; } = "data/OIDExtract.db";
    public string PromptsPath { get; set; } = "Prompts";

    // Qdrant semantic search (mirrors Python QdrantOrderCatalogRepository + _QDRANT_STORAGE path)
    // Python used embedded mode (path=); .NET connects to a running Qdrant server via gRPC.
    // Start: docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant
    public string QdrantHost { get; set; } = "localhost";
    public int QdrantPort { get; set; } = 6334;

    // ONNX model for all-MiniLM-L6-v2 (equivalent to sentence_transformers in Python).
    // Export once from the Python project:
    //   pip install optimum
    //   optimum-cli export onnx --model sentence-transformers/all-MiniLM-L6-v2 data/all-MiniLM-L6-v2-onnx/
    public string OnnxVocabPath { get; set; } = "data/all-MiniLM-L6-v2-onnx/vocab.txt";
    public string OnnxModelPath { get; set; } = "data/all-MiniLM-L6-v2-onnx/model.onnx";

    // Pipeline behaviour
    public bool UseMockQueue { get; set; } = true;
    public string TwQueueBaseUrl { get; set; } = string.Empty;
    public string TwQueueApiKey { get; set; } = string.Empty;

    public double AmbientConfidenceThreshold { get; set; } = 0.8;
}
