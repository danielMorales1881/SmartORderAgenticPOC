---
name: smart-orders-new-agent
description: >
  Use when adding a new agent to the Smart Orders .NET pipeline. Examples:
  "Add a new agent", "Create a CarePlanAgent", "I need a new step in the pipeline".
---

# Smart Orders .NET — Add a New Agent

## Steps

### 1. Gather requirements

Ask the user:
- What is the agent's single responsibility?
- What `PipelineState` field does it read from? (upstream agent output)
- What `PipelineState` field does it write to? (its own output)
- Does it need plugins/tools? Which ones?
- Does it require HITL (human-in-the-loop)?
- Structured JSON output (JSON mode + responseSchema) or tool-using (UseFunctionInvocation)?

### 2. Add the PipelineState field

Open `src/SmartOrders.Core/Pipeline/PipelineState.cs` and add the new output field:

```csharp
public string <Name>ResultsJson { get; set; } = "[]";
```

### 3. Create the agent in AgentFactory

Open `src/SmartOrders.Infrastructure/Agents/AgentFactory.cs` and add:

```csharp
/// <summary>
/// <One-line description>.
/// Equivalent to <name>_agent.py in smart-orders-adk.
/// </summary>

// JSON-output agent (no tools):
public static AIAgent Create<Name>Agent(IChatClient baseClient, string instruction)
{
    var client = baseClient.AsBuilder()
        .ConfigureOptions(opts =>
        {
            opts.ResponseFormat = ChatResponseFormat.Json;
            opts.MaxOutputTokens = 512;   // adjust as needed
            opts.AdditionalProperties ??= [];
            opts.AdditionalProperties["response_schema"] = s_mySchema;
        })
        .Build();
    return client.AsAIAgent(name: "<Name>Agent", instructions: instruction);
}

// Tool-using agent:
public static AIAgent Create<Name>Agent(
    IChatClient baseClient,
    string instruction,
    MyPlugin myPlugin)
{
    List<AITool> tools =
    [
        AIFunctionFactory.Create(
            (Func<string, CancellationToken, Task<string>>)myPlugin.MyMethodAsync),
    ];
    var client = baseClient.AsBuilder()
        .ConfigureOptions(opts => opts.MaxOutputTokens = 2048)
        .UseFunctionInvocation()
        .Build();
    return client.AsAIAgent(name: "<Name>Agent", instructions: instruction, tools: tools);
}
```

### 4. Create the prompt file

Create `src/SmartOrders.Api/Prompts/<name>_agent.txt` with:
```
TASK
<What the agent must do>

RULES
1. <Rule 1>
2. Do NOT <safety-critical constraint>

ORDER CATEGORIES
Lab, Imaging, Diagnostic Orders, Referrals, FollowUp Orders, Medications

OUTPUT FORMAT
<JSON schema or structured description>

EXAMPLES
<Example 1>
<Example 2>
```

### 5. Register in SmartOrdersPipeline

Open `src/SmartOrders.Infrastructure/Pipeline/SmartOrdersPipeline.cs`.

1. Add constructor parameter: `AIAgent <name>Agent`
2. Add pipeline stage in `RunAsync`:

```csharp
// Stage N: <Name>Agent
logger.LogInformation("pipeline_stage stage=<Name>Agent");
var <name>Response = await <name>Agent.RunAsync(
    $"<upstream_state_key>:\n{state.<UpstreamField>}", cancellationToken: ct);
state.<Name>ResultsJson = <name>Response.Text ?? "[]";
logger.LogInformation("<name>_complete");
```

### 6. Wire up in SmartOrdersServiceCollectionExtensions

Open `src/SmartOrders.Infrastructure/SmartOrdersServiceCollectionExtensions.cs`.

1. Instantiate any new plugins: `ActivatorUtilities.CreateInstance<MyPlugin>(sp)`
2. Call the new factory method: `AgentFactory.Create<Name>Agent(baseClient, LoadPrompt("<name>_agent"), ...)`
3. Pass the new agent to the `SmartOrdersPipeline` constructor

### 7. Update state ownership table

Update the state key table in `.github/instructions/sk-agents.instructions.md` and `.github/copilot-instructions.md`.

### 8. Write unit tests

Create `tests/SmartOrders.Unit/Test<Name>Agent.cs` with:
- Happy path: agent produces expected JSON output
- Error path: malformed upstream JSON is handled gracefully
