using Boxboard.Models;

namespace Boxboard.Services;

internal sealed record LayoutBatchResult(int Applied, int Bound, int Moved, int ConnectionRequests,
    IReadOnlyList<(LayoutKey Key, Exception Error)> Failures);

internal static class LayoutBatchApplier
{
    internal static async Task<LayoutBatchResult> ApplyAsync(IReadOnlyList<LayoutKey> layouts,
        Func<LayoutKey, Task<LayoutApplyResult?>> apply, CancellationToken ct = default)
    {
        var failures = new List<(LayoutKey Key, Exception Error)>();
        int applied = 0, bound = 0, moved = 0, connections = 0;
        foreach (var key in layouts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await apply(key) ??
                    throw new InvalidOperationException("The layout has no assigned clients to re-apply.");
                applied++;
                bound += result.Bound;
                moved += result.Moved;
                connections += result.ConnectionRequests;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures.Add((key, ex));
            }
        }
        return new(applied, bound, moved, connections, failures);
    }
}
