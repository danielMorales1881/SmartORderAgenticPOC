---
applyTo: "src/SmartOrders.Infrastructure/Agents/**/*.cs"
description: "Semantic Kernel agent coding conventions for Smart Orders agents"
---

# Semantic Kernel Agent Conventions

## AgentFactory pattern

Every agent **must** be created via a static factory method in `AgentFactory` — never inline:

```csharp
public static ChatCompletionAgent Create<Name>Agent(Kernel kernel, string instruction) =>
    new()
    {
        Name = "<Name>Agent",
        Description = "<One-line description>",
        Instructions = instruction,
        Kernel = kernel,
        Arguments = new KernelArguments(new GeminiExecutionSettings
        {
            // JsonMode = true for structured output agents (equivalent to output_schema in Python)
            // FunctionChoiceBehavior.Auto() for tool-using agents
        }),
    };
```

## Python ADK → Semantic Kernel equivalence

| Python ADK | Semantic Kernel .NET |
|---|---|
| `LlmAgent(output_schema=...)` | `ChatCompletionAgent` + `GeminiExecutionSettings { JsonMode = true }` |
| `LlmAgent(tools=[...])` | `ChatCompletionAgent` + `FunctionChoiceBehavior.Auto()` |
| `output_key="state_key"` | Caller reads `msg.Message.Content` and puts it in `PipelineState` |
| `agent.run_async(session)` | `await foreach (var msg in agent.InvokeAsync(history, ct))` |

## State key ownership (mirrors Python ADK convention)

| Agent | Python output_key | .NET PipelineState field |
|---|---|---|
| `IntentAgent` | `state["order_intents"]` | `PipelineState.OrderIntentsJson` |
| `MappingAgent` | `state["mapped_orders"]` | `PipelineState.MappedOrdersJson` |
| `ValidationService` | `state["validated_orders"]` | `PipelineState.ValidatedOrdersJson` |
| `SubmissionAgent` | `state["submission_results"]` | `PipelineState.SubmissionResultsJson` |

## State passing between agents

Pass upstream output as context in the user message for the next agent (mirrors `session.state` reads):

```csharp
var mappingInput = $"order_intents:\n{state.OrderIntentsJson}";
var mappingRaw = await RunAgentAsync(mappingAgent, mappingInput, ct);
```

## HITL in SubmissionAgent

The `SubmissionAgent` **must** present orders to the provider and wait for explicit confirmation
before calling `SubmitOrderAsync`. This is enforced via the agent instruction.

Auto-submission is a **BC-2 violation**. The agent prompt must include:
- Human-readable summary of all orders in the batch
- Target patient + encounter context
- Explicit "Review & Sign" wording
- Instruction not to call `submit_order` until the provider responds with `CONFIRM`

## Instruction string style (matches Python convention)

- Use all-caps section headers: `TASK`, `RULES`, `OUTPUT FORMAT`, `EXAMPLES`
- Include at least 2 few-shot examples in the `EXAMPLES` section
- End safety-critical rules with "Do NOT..."
- Order categories must always reference the canonical list: `Lab`, `Imaging`, `Diagnostic Orders`, `Referrals`, `FollowUp Orders`, `Medications`
- Instructions are loaded from `Prompts/<agent_name>.txt` — never hardcoded in C#

## MaxOutputTokens

Match the Python ADK `generate_content_config` values:

| Agent | MaxOutputTokens |
|---|---|
| IntentAgent | 512 |
| MappingAgent | 2048 |
| SubmissionAgent | default (no override) |
