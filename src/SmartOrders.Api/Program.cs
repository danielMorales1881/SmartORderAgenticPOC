using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using SmartOrders.Api;
using SmartOrders.Core.Pipeline;
using SmartOrders.Core.Repositories;
using SmartOrders.Infrastructure;
using SmartOrders.Infrastructure.Configuration;
using SmartOrders.Infrastructure.NoteGateway;
using SmartOrders.Infrastructure.Repositories;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

// -----------------------------------------------------------------------
// Settings
// -----------------------------------------------------------------------
builder.Services.Configure<SmartOrdersSettings>(
    builder.Configuration.GetSection(SmartOrdersSettings.SectionName));
builder.Services.Configure<GeminiGatewaySettings>(
    builder.Configuration.GetSection("GeminiGateway"));
builder.Services.Configure<FrontDoorSettings>(
    builder.Configuration.GetSection("FrontDoor"));

// -----------------------------------------------------------------------
// Note+ Gateway — same pattern as FileComparer-Semantic
// FrontDoorTokenService: MSAL client credentials → Azure AD Bearer token
// GeminiChatCompletionService: HTTP → Note+ gateway → Vertex AI / Gemini
// -----------------------------------------------------------------------
builder.Services.AddSingleton<FrontDoorTokenService>();
builder.Services.AddSingleton<IChatClient, GeminiChatCompletionService>();

// -----------------------------------------------------------------------
// Layer 1: Repositories
// -----------------------------------------------------------------------
// QdrantOrderCatalogRepository — semantic search using all-MiniLM-L6-v2 embeddings.
// Mirrors Python's QdrantOrderCatalogRepository (order_catalog_qdrant.py).
// Requires Qdrant running: docker run -p 6333:6333 -p 6334:6334 qdrant/qdrant
// Build index first: POST /api/catalog/index  (equivalent to scripts/index_catalog.py)
builder.Services.AddSingleton<IOrderCatalogRepository>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<SmartOrdersSettings>>().Value;
    return new QdrantOrderCatalogRepository(
        settings.QdrantHost,
        settings.QdrantPort,
        settings.OnnxVocabPath,
        settings.OnnxModelPath,
        sp.GetRequiredService<ILogger<QdrantOrderCatalogRepository>>());
});

// CatalogIndexer — used by POST /api/catalog/index
builder.Services.AddSingleton<CatalogIndexer>();

builder.Services.AddSingleton<ITwOrderQueueRepository>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<SmartOrdersSettings>>().Value;
    if (settings.UseMockQueue)
        return new MockTwOrderQueueRepository(sp.GetRequiredService<ILogger<MockTwOrderQueueRepository>>());

    // Use IHttpClientFactory for proper socket management and DNS refresh.
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient("TwQueue");
    return new RealTwOrderQueueRepository(http, sp.GetRequiredService<ILogger<RealTwOrderQueueRepository>>());
});

// Named HttpClient for TW Order Engine queue — used by RealTwOrderQueueRepository.
// IHttpClientFactory ensures proper socket pooling and DNS refresh.
builder.Services.AddHttpClient("TwQueue", (sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<SmartOrdersSettings>>().Value;
    if (!string.IsNullOrWhiteSpace(settings.TwQueueBaseUrl))
        client.BaseAddress = new Uri(settings.TwQueueBaseUrl);
    if (!string.IsNullOrWhiteSpace(settings.TwQueueApiKey))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {settings.TwQueueApiKey}");
});

// -----------------------------------------------------------------------
// Layer 2 + 3: Pipeline (ValidationService, plugins, agents, SmartOrdersPipeline)
// All per-agent IChatClient wiring and schema enforcement is encapsulated in
// SmartOrdersServiceCollectionExtensions — see SmartOrders.Infrastructure.
// -----------------------------------------------------------------------
builder.Services.AddSmartOrdersPipeline();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(); // UI at /scalar/v1
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();
