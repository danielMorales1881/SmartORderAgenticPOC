namespace SmartOrders.Infrastructure.NoteGateway;

/// <summary>
/// Thread-safe per-pipeline-run LLM usage accumulator.
/// Accessed via <see cref="LlmUsageScope"/> which uses AsyncLocal to flow
/// the tracker through async continuations and Task.Run chains.
/// </summary>
public sealed class LlmUsageTracker
{
    private int _calls;
    private int _inputTokens;
    private int _outputTokens;

    public int TotalCalls        => _calls;
    public int TotalInputTokens  => _inputTokens;
    public int TotalOutputTokens => _outputTokens;

    /// <summary>
    /// Estimated USD cost using Gemini 2.5 Flash list pricing:
    /// $0.15 / 1M input tokens · $0.60 / 1M output tokens.
    /// </summary>
    public decimal EstimatedCostUsd =>
        (_inputTokens / 1_000_000m) * 0.15m +
        (_outputTokens / 1_000_000m) * 0.60m;

    public void RecordCall(int inputTokens, int outputTokens)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _inputTokens, inputTokens);
        Interlocked.Add(ref _outputTokens, outputTokens);
    }
}

/// <summary>
/// AsyncLocal scope that makes the current <see cref="LlmUsageTracker"/> available
/// anywhere inside the same async call-graph without DI coupling.
///
/// AsyncLocal values flow into child tasks via ExecutionContext. Since we mutate
/// properties of the tracker object (not the reference stored in AsyncLocal),
/// writes from background threads are visible to the owner scope.
/// </summary>
public static class LlmUsageScope
{
    private static readonly AsyncLocal<LlmUsageTracker?> s_current = new();

    /// <summary>Currently active tracker, or <c>null</c> outside a pipeline run.</summary>
    public static LlmUsageTracker? Current => s_current.Value;

    /// <summary>
    /// Opens a new tracking scope for the calling async context.
    /// Call once at the start of each pipeline run.
    /// </summary>
    public static LlmUsageTracker Begin()
    {
        var tracker = new LlmUsageTracker();
        s_current.Value = tracker;
        return tracker;
    }
}
