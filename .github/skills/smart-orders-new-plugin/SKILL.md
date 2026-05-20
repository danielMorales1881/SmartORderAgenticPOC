---
name: smart-orders-new-plugin
description: >
  Use when adding a new tool/plugin to the Smart Orders .NET pipeline. Examples:
  "Add a new tool", "Create a search plugin for ICD-10 codes", "I need a plugin that calls the TW API".
---

# Smart Orders .NET — Add a New Plugin

## Steps

### 1. Gather requirements

Ask the user:
- What is the plugin's single responsibility?
- What parameters does it need? (map these to `[Description]` attributes)
- What does it return? (always a JSON string)
- Which agent(s) will use it?
- Does it require any I/O (DB, HTTP)? If so, what interface does it depend on?

### 2. Create the plugin class

Create `src/SmartOrders.Infrastructure/Plugins/<Name>Plugin.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SmartOrders.Infrastructure.Plugins;

/// <summary>
/// <One-line description>.
/// Equivalent to <tool_name>() in smart-orders-adk/smart_orders/tools/<file>.py.
/// </summary>
public sealed class <Name>Plugin(/* dependencies */, ILogger<<Name>Plugin> logger)
{
    [Description("<Clear description of what this tool does.>")]
    public async Task<string> <ToolMethod>Async(
        [Description("<What this parameter means>.")] string param,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("<tool_name> param={Param}", param);
        // implementation
        return JsonSerializer.Serialize(result);
    }
}
```

Note: Do NOT add `[KernelFunction]` — this project uses Microsoft Agent Framework, not Semantic Kernel.

### 3. Add interface to Core (if new I/O dependency)

If the plugin depends on a new external resource, add the interface in `src/SmartOrders.Core/Repositories/`:

```csharp
public interface I<Name>Repository
{
    Task<SomeResult> DoSomethingAsync(string input, CancellationToken ct = default);
}
```

Implement in `src/SmartOrders.Infrastructure/Repositories/<Name>Repository.cs`.

### 4. Register the plugin and wire to the agent

Open `src/SmartOrders.Infrastructure/SmartOrdersServiceCollectionExtensions.cs`.

1. Register any new repository in `Program.cs`:
```csharp
builder.Services.AddSingleton<I<Name>Repository, <Name>Repository>();
```

2. Instantiate the plugin in `SmartOrdersServiceCollectionExtensions`:
```csharp
var myPlugin = ActivatorUtilities.CreateInstance<<Name>Plugin>(sp);
```

3. Pass it to the target agent factory method in `AgentFactory`:
```csharp
AgentFactory.CreateMappingAgent(baseClient, LoadPrompt("mapping_agent"), searchPlugin, mappingPlugin, myPlugin)
```

4. In `AgentFactory`, add the new tool to the agent's tool list:
```csharp
List<AITool> tools =
[
    // existing tools ...
    AIFunctionFactory.Create(
        (Func<string, CancellationToken, Task<string>>)myPlugin.<ToolMethod>Async),
];
```

The target agent must use `.UseFunctionInvocation()` middleware (MappingAgent and SubmissionAgent both do).

### 5. Write unit tests

Create `tests/SmartOrders.Unit/Test<Name>Plugin.cs`:

```csharp
public sealed class <Name>PluginTests
{
    [Fact]
    public async Task <ToolMethod>Async_ValidInput_ReturnsJson()
    {
        var sut = new <Name>Plugin(/* mock deps */, NullLogger<<Name>Plugin>.Instance);
        var result = await sut.<ToolMethod>Async("test");
        result.Should().NotBeNullOrEmpty();
        // assert JSON structure
    }
}
```
