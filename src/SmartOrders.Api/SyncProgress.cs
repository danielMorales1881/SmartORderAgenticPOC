namespace SmartOrders.Api;

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> implementation — calls the callback inline on the
/// reporting thread rather than posting to the thread pool (as the built-in <see cref="Progress{T}"/> does).
///
/// Required for the SSE streaming endpoint: <see cref="Progress{T}"/> posts to the sync context /
/// thread pool, so <c>TryComplete()</c> on the channel can fire before the last <c>TryWrite()</c>
/// finishes. Calling the callback synchronously eliminates this race condition.
/// </summary>
internal sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
