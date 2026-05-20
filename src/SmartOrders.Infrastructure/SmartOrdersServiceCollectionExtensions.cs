using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartOrders.Core.Pipeline;
using SmartOrders.Infrastructure.Agents;
using SmartOrders.Infrastructure.Configuration;
using SmartOrders.Infrastructure.Pipeline;
using SmartOrders.Infrastructure.Plugins;
using SmartOrders.Infrastructure.Services;

namespace SmartOrders.Infrastructure;

/// <summary>
/// DI extension that registers the full Smart Orders pipeline.
/// Call from Program.cs: <c>builder.Services.AddSmartOrdersPipeline()</c>.
///
/// Encapsulates all per-agent IChatClient configuration (response schemas,
/// tool registration, UseFunctionInvocation middleware) so that Program.cs
/// remains a thin composition root.
/// </summary>
public static class SmartOrdersServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ValidationService"/> and <see cref="IPipelineOrchestrator"/>
    /// (backed by <see cref="SmartOrdersPipeline"/>) as singletons.
    ///
    /// Prerequisites that must already be registered before calling this:
    /// <list type="bullet">
    ///   <item><see cref="IChatClient"/> — base Gemini client (GeminiChatCompletionService)</item>
    ///   <item><see cref="IOrderCatalogRepository"/> — Qdrant or SQLite catalog</item>
    ///   <item><see cref="ITwOrderQueueRepository"/> — mock or real TW queue</item>
    ///   <item><see cref="SmartOrdersSettings"/> bound via IOptions</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddSmartOrdersPipeline(this IServiceCollection services)
    {
        services.AddSingleton<ValidationService>();

        services.AddSingleton<IPipelineOrchestrator>(sp =>
        {
            var baseClient  = sp.GetRequiredService<IChatClient>();
            var settings    = sp.GetRequiredService<IOptions<SmartOrdersSettings>>().Value;
            var validation  = sp.GetRequiredService<ValidationService>();
            var logger      = sp.GetRequiredService<ILogger<SmartOrdersPipeline>>();

            string LoadPrompt(string name) =>
                File.ReadAllText(Path.Combine(settings.PromptsPath, $"{name}.txt"));

            // Plugins are created via ActivatorUtilities so their own constructor
            // dependencies (IOrderCatalogRepository, ITwOrderQueueRepository, etc.)
            // are resolved from the DI container automatically.
            var searchPlugin     = ActivatorUtilities.CreateInstance<SearchPlugin>(sp);
            var mappingPlugin    = ActivatorUtilities.CreateInstance<MappingPlugin>(sp);
            var submissionPlugin = ActivatorUtilities.CreateInstance<SubmissionPlugin>(sp);

            return new SmartOrdersPipeline(
                AgentFactory.CreateIntentAgent(
                    baseClient, LoadPrompt("intent_agent")),
                AgentFactory.CreateMappingAgent(
                    baseClient, LoadPrompt("mapping_agent"), searchPlugin, mappingPlugin),
                validation,
                AgentFactory.CreateSubmissionPresenterAgent(
                    baseClient, LoadPrompt("submission_agent")),
                AgentFactory.CreateSubmissionAgent(
                    baseClient, LoadPrompt("submission_agent"), submissionPlugin),
                logger);
        });

        return services;
    }
}
