using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Scalar.AspNetCore;
using SmartOrders.Api;
using SmartOrders.Core.Pipeline;
using System.Text.Json;
using System.Threading.Channels;
using SmartOrders.Core.Repositories;
using SmartOrders.Infrastructure.Agents;
using SmartOrders.Infrastructure.Configuration;
using SmartOrders.Infrastructure.NoteGateway;
using SmartOrders.Infrastructure.Pipeline;
using SmartOrders.Infrastructure.Plugins;
using SmartOrders.Infrastructure.Repositories;
using SmartOrders.Infrastructure.Services;
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
builder.Services.AddSingleton<IChatCompletionService, GeminiChatCompletionService>();

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

    var http = new HttpClient { BaseAddress = new Uri(settings.TwQueueBaseUrl) };
    http.DefaultRequestHeaders.Add("Authorization", $"Bearer {settings.TwQueueApiKey}");
    return new RealTwOrderQueueRepository(http, sp.GetRequiredService<ILogger<RealTwOrderQueueRepository>>());
});

// -----------------------------------------------------------------------
// Layer 2: Plugins + ValidationService
// Each agent gets its OWN kernel with only the plugins it is allowed to call.
// Sharing a kernel would expose Submission_SubmitOrderAsync to MappingAgent (BC-2 risk).
// -----------------------------------------------------------------------

// ValidationService — pure C#, no LLM (PureValidationAgent equivalent)
builder.Services.AddSingleton<ValidationService>();

// -----------------------------------------------------------------------
// Layer 3: Pipeline — per-agent kernel isolation
// -----------------------------------------------------------------------
builder.Services.AddSingleton<IPipelineOrchestrator>(sp =>
{
    var chatService = sp.GetRequiredService<IChatCompletionService>();

    // Helper: fresh kernel pre-wired with the shared chat service
    Kernel MakeKernel()
    {
        var kb = Kernel.CreateBuilder();
        kb.Services.AddSingleton(chatService);
        return kb.Build();
    }

    // IntentAgent — structured JSON output, no tools
    var intentKernel = MakeKernel();

    // MappingAgent — search_orders, map_diagnosis, apply_order_defaults only
    var mappingKernel = MakeKernel();
    mappingKernel.Plugins.AddFromObject(ActivatorUtilities.CreateInstance<SearchPlugin>(sp), "Search");
    mappingKernel.Plugins.AddFromObject(ActivatorUtilities.CreateInstance<MappingPlugin>(sp), "Mapping");

    // SubmissionAgent — submit_order only (BC-2: never expose submit to other agents)
    var submissionKernel = MakeKernel();
    submissionKernel.Plugins.AddFromObject(ActivatorUtilities.CreateInstance<SubmissionPlugin>(sp), "Submission");

    var settings = sp.GetRequiredService<IOptions<SmartOrdersSettings>>().Value;
    var validationService = sp.GetRequiredService<ValidationService>();

    string LoadPrompt(string name) =>
        File.ReadAllText(Path.Combine(settings.PromptsPath, $"{name}.txt"));

    return new SmartOrdersPipeline(
        AgentFactory.CreateIntentAgent(intentKernel, LoadPrompt("intent_agent")),
        AgentFactory.CreateMappingAgent(mappingKernel, LoadPrompt("mapping_agent")),
        validationService,
        AgentFactory.CreateSubmissionAgent(submissionKernel, LoadPrompt("submission_agent")),
        sp.GetRequiredService<ILogger<SmartOrdersPipeline>>());
});

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

// SSE endpoint — streams live pipeline progress events to the browser.
// Writes the text/event-stream format manually so it works on any .NET 10 build.
app.MapPost("/api/orders/stream", async (HttpContext context, ProcessOrdersRequest request, IPipelineOrchestrator pipeline, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request?.ClinicalText))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("ClinicalText is required.", ct);
        return;
    }

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    var channel = Channel.CreateBounded<PipelineProgressEvent>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });

    _ = Task.Run(async () =>
    {
        var progress = new Progress<PipelineProgressEvent>(evt => channel.Writer.TryWrite(evt));
        try   { await pipeline.RunAsync(request.ClinicalText, progress, ct); }
        catch (Exception ex) { channel.Writer.TryWrite(new PipelineProgressEvent("error", "Pipeline", ex.Message)); }
        finally { channel.Writer.TryComplete(); }
    }, ct);

    var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    await foreach (var evt in channel.Reader.ReadAllAsync(ct))
    {
        await context.Response.WriteAsync($"event: {evt.Type}\ndata: {JsonSerializer.Serialize(evt, opts)}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
});

app.Run();
