---
applyTo: "src/SmartOrders.Infrastructure/Agents/**/*.cs"
description: "Microsoft Agent Framework agent coding conventions for Smart Orders agents"
---

# Agent Framework Agent Conventions

## Library: Microsoft.Agents.AI v1.6.1

This project uses **Microsoft Agent Framework** (`Microsoft.Agents.AI`) — NOT Semantic Kernel.
Correct imports:

```csharp
using Microsoft.Agents.AI;      // AIAgent, AIFunctionFactory
using Microsoft.Extensions.AI;  // IChatClient, ChatOptions, ChatResponseFormat, AITool
```

Do NOT import `Microsoft.SemanticKernel` — it is not a dependency of this project.

## AgentFactory pattern

Every agent **must** be created via a static factory method in `AgentFactory` — never inline.

Each factory method:
1. Takes `IChatClient baseClient` (raw base client from DI)
2. Configures per-agent options via `.AsBuilder().ConfigureOptions(opts => ...)`
3. Adds `.UseFunctionInvocation()` middleware for tool-using agents
4. Returns `AIAgent` via `.Build().AsAIAgent(name, instructions, tools?)`

### JSON-output agent (no tools)

```csharp
public static AIAgent Create<Name>Agent(IChatClient baseClient, string instruction)
{
    var client = baseClient.AsBuilder()
        .ConfigureOptions(opts =>
        {
            opts.ResponseFormat = ChatResponseFormat.Json;
            opts.MaxOutputTokens = 512;       // adjust per table below
            opts.AdditionalProperties ??= [];
            opts.AdditionalProperties["response_schema"] = s_mySchema;
        })
        .Build();
    return client.AsAIAgent(name: "<Name>Agent", instructions: instruction);
}
```

### Tool-using agent

```csharp
public static AIAgent Create<Name>Agent(
    IChatClient baseClient,
    string instruction,
    MyPlugin myPlugin)
{
    List<AITool> tools =
    [
        AIFunctionFactory.Create(
            (Func<string, int, CancellationToken, Task<string>>)myPlugin.MyMethodAsync),
    ];
    var client = baseClient.AsBuilder()
        .ConfigureOptions(opts => opts.MaxOutputTokens = 2048)
        .UseFunctionInvocation()
        .Build();
    return client.AsAIAgent(name: "<Name>Agent", instructions: instruction, tools: tools);
}
```

## Running an agent

```csharp
var response = await agent.RunAsync(inputText, cancellationToken: ct);
string json = response.Text ?? "";
```

`RunAsync` handles the tool-call loop automatically when `.UseFunctionInvocation()` is on the client.

Do NOT manually loop on `InvokeAsync` — that is the SK pattern and does not apply here.

## Tool registration via AIFunctionFactory

Tools are provided at agent creation time. Cast to exact delegate signature to disambiguate overloads:

```csharp
AIFunctionFactory.Create(
    (Func<string, double?, int, CancellationToken, Task<string>>)plugin.SearchOrdersAsync)
```

## Python ADK → Agent Framework equivalence

| Python ADK | .NET Microsoft.Agents.AI |
|---|---|
| `LlmAgent(output_schema=...)` | `ConfigureOptions({ ResponseFormat = Json, ["response_schema"] = schema })` |
| `LlmAgent(tools=[...])` | `.UseFunctionInvocation()` + `tools` on `AsAIAgent(...)` |
| `output_key="state_key"` | Caller reads `response.Text` → sets `PipelineState.<Field>` |
| `agent.run_async(session)` | `await agent.RunAsync(inputText, cancellationToken: ct)` |

## State key ownership (mirrors Python ADK convention)

| Agent | Python output_key | .NET PipelineState field |
|---|---|---|
| `IntentAgent` | `state["order_intents"]` | `PipelineState.OrderIntentsJson` |
| `MappingAgent` | `state["mapped_orders"]` | `PipelineState.MappedOrdersJson` |
| `ValidationService` | `state["validated_orders"]` | `PipelineState.ValidatedOrdersJson` |
| `SubmissionAgent` | `state["submission_results"]` | `PipelineState.SubmissionResultsJson` |

## State passing between agents

Pass upstream output as plain text in the next agent's input:

```csharp
var mappingResponse = await mappingAgent.RunAsync(
    $"order_intents:\n{state.OrderIntentsJson}", cancellationToken: ct);
state.MappedOrdersJson = mappingResponse.Text ?? "";
```

## HITL in SubmissionAgent

The pipeline uses **two** SubmissionAgent instances:
1. `SubmissionPresenterAgent` — NO tools, returns plain-text order summary for provider review.
2. `SubmissionAgent` — has `submit_order` tool, called only inside `ConfirmAndSubmitAsync`.

Auto-submission is a **BC-2 violation**. The presenter agent prompt must include:
- Human-readable summary of all orders in the batch
- Target patient + encounter context
- Explicit "Review & Sign" wording
- Instruction not to call `submit_order` until the provider sends `ProviderConfirmation` via `POST /api/orders/submit`

## Instruction string style (matches Python convention)

- Instructions are loaded from `Prompts/<agent_name>.txt` — never hardcoded in C#
- Use all-caps section headers: `TASK`, `RULES`, `OUTPUT FORMAT`, `EXAMPLES`
- Include at least 2 few-shot examples in the `EXAMPLES` section
- End safety-critical rules with "Do NOT..."
- Order categories must always reference the canonical list: `Lab`, `Imaging`, `Diagnostic Orders`, `Referrals`, `FollowUp Orders`, `Medications`

## MaxOutputTokens per agent

Set via `opts.MaxOutputTokens` in `ConfigureOptions`:

| Agent | MaxOutputTokens |
|---|---|
| IntentAgent | 512 |
| MappingAgent | 2048 |
| SubmissionAgent | default (no override) |
