---
applyTo: "src/SmartOrders.Infrastructure/Plugins/**/*.cs"
description: "Microsoft Agent Framework plugin (tool) coding conventions for Smart Orders"
---

# Agent Framework Plugin Conventions

## Library: Microsoft.Agents.AI v1.6.1

This project uses **Microsoft Agent Framework** — NOT Semantic Kernel. Do NOT add `[KernelFunction]`.
Correct import for plugin descriptions: `using System.ComponentModel;`

## Plugin = ADK tool

Every plugin method is the .NET equivalent of a Python ADK `@tool` function.

| Python ADK tool | .NET Plugin method |
|---|---|
| `@tool async def search_orders(...)` | `async Task<string> SearchOrdersAsync(...)` |
| `@tool async def map_diagnosis(...)` | `async Task<string> MapDiagnosisAsync(...)` |
| `@tool async def apply_defaults(...)` | `async Task<string> ApplyOrderDefaultsAsync(...)` |
| `@tool async def submit_orders_tool(...)` | `async Task<string> SubmitOrderAsync(...)` |

## Required decorator

Every plugin method **must** have `[Description]` — this is what the agent model sees as the tool description.
Do NOT add `[KernelFunction]` — it is a Semantic Kernel attribute and is not used in this project.

```csharp
[Description("Clear single-sentence description of what the tool does.")]
public async Task<string> MyToolAsync(
    [Description("What this parameter means in clinical context.")] string param,
    CancellationToken cancellationToken = default)
{
    // ...
    return JsonSerializer.Serialize(result);
}
```

## Return type

All plugin methods return `Task<string>` (or `string` for sync) — **always JSON-serialized**.
Never return domain objects directly. This mirrors ADK tool return pattern.

## CancellationToken

Always accept `CancellationToken cancellationToken = default` as the last parameter.

## Logging

Log at `Debug` level using structured logging. Include the tool name and key parameters:

```csharp
logger.LogDebug("search_orders text={Text} limit={Limit}", text, limit);
```

## SAFETY CONTRACT for SubmissionPlugin

`SubmitOrderAsync` must only be called after the provider has explicitly confirmed the order.
This contract is documented in the XML summary and enforced via the SubmissionAgent prompt.
Never relax this constraint.

## Plugin registration (Agent Framework pattern)

Plugins are instantiated in `SmartOrdersServiceCollectionExtensions` via `ActivatorUtilities`
and then passed to `AgentFactory` factory methods. Tools are registered via `AIFunctionFactory.Create`:

```csharp
// In SmartOrdersServiceCollectionExtensions:
var searchPlugin = ActivatorUtilities.CreateInstance<SearchPlugin>(sp);

// In AgentFactory.CreateMappingAgent:
List<AITool> tools =
[
    AIFunctionFactory.Create(
        (Func<string, double?, int, CancellationToken, Task<string>>)searchPlugin.SearchOrdersAsync),
];
```

Do NOT use `kernel.Plugins.AddFromObject(...)` — there is no Kernel in this project.

## ValidationPlugin

`ValidationPlugin` is NOT used in the pipeline. Stage 3 uses `ValidationService` (pure C#, no LLM).
`ValidationPlugin` exists only for unit testing and as a future extension point.
