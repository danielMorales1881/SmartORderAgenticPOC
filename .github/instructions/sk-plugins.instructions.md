---
applyTo: "src/SmartOrders.Infrastructure/Plugins/**/*.cs"
description: "Semantic Kernel plugin (tool) coding conventions for Smart Orders"
---

# Semantic Kernel Plugin Conventions

## Plugin = ADK tool

Every plugin method is the .NET equivalent of a Python ADK `@tool` function.

| Python ADK tool | .NET SK Plugin method |
|---|---|
| `@tool async def search_orders(...)` | `[KernelFunction] async Task<string> SearchOrdersAsync(...)` |
| `@tool async def map_diagnosis(...)` | `[KernelFunction] async Task<string> MapDiagnosisAsync(...)` |
| `@tool async def apply_defaults(...)` | `[KernelFunction] async Task<string> ApplyOrderDefaultsAsync(...)` |
| `@tool async def submit_orders_tool(...)` | `[KernelFunction] async Task<string> SubmitOrderAsync(...)` |

## Required decorators

Every plugin method **must** have both decorators:

```csharp
[KernelFunction, Description("Clear single-sentence description of what the tool does.")]
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

## Plugin registration

All plugins are registered in `Program.cs` on the shared `Kernel` singleton:

```csharp
k.Plugins.AddFromObject(ActivatorUtilities.CreateInstance<SearchPlugin>(sp), "Search");
k.Plugins.AddFromObject(ActivatorUtilities.CreateInstance<MappingPlugin>(sp), "Mapping");
k.Plugins.AddFromObject(ActivatorUtilities.CreateInstance<SubmissionPlugin>(sp), "Submission");
```

`ValidationPlugin` is available to the `Kernel` for optional validation tool calls but
`ValidationService` is the primary (pure C#, no LLM) validation path.
