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
- Structured JSON output (`JsonMode=true`) or tool-using (`FunctionChoiceBehavior.Auto()`)?

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
public static ChatCompletionAgent Create<Name>Agent(Kernel kernel, string instruction) =>
    new()
    {
        Name = "<Name>Agent",
        Description = "<One-line description>",
        Instructions = instruction,
        Kernel = kernel,
        Arguments = new KernelArguments(new GeminiExecutionSettings
        {
            JsonMode = true,            // if structured output
            // FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(), // if tools
            MaxOutputTokens = 512,      // adjust as needed
        }),
    };
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

1. Add constructor parameter: `ChatCompletionAgent <name>Agent`
2. Add pipeline stage in `RunAsync`:

```csharp
// Stage N: <Name>Agent
logger.LogInformation("pipeline_stage stage=<Name>Agent");
var <name>Input = $"<upstream_state_key>:\n{state.<UpstreamField>}";
var <name>Raw = await RunAgentAsync(<name>Agent, <name>Input, ct);
state.<Name>ResultsJson = ExtractJsonArray(<name>Raw);
logger.LogInformation("<name>_complete");
```

### 6. Wire up in Program.cs

Open `src/SmartOrders.Api/Program.cs`.

1. Load the prompt: `LoadPrompt("<name>_agent")`
2. Pass the new agent to `SmartOrdersPipeline` constructor
3. If new plugins are needed, register them on the Kernel first

### 7. Update state ownership table

Update the state key table in `.github/instructions/sk-agents.instructions.md` and `.github/copilot-instructions.md`.

### 8. Write unit tests

Create `tests/SmartOrders.Unit/Test<Name>Agent.cs` with:
- Happy path: agent produces expected JSON output
- Error path: malformed upstream JSON is handled gracefully
