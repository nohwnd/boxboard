using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class LayoutBatchApplierTests
{
    [TestMethod]
    public async Task Apply_ContinuesAfterOneLayoutFailsAndReportsEveryResult()
    {
        var layouts = new[]
        {
            new LayoutKey(Guid.NewGuid(), "monitor-1"),
            new LayoutKey(Guid.NewGuid(), "monitor-2"),
            new LayoutKey(Guid.NewGuid(), "monitor-3")
        };
        var attempted = new List<LayoutKey>();
        var result = await LayoutBatchApplier.ApplyAsync(layouts, key =>
        {
            attempted.Add(key);
            if (key == layouts[1])
                throw new InvalidOperationException("Client title is ambiguous.");
            return Task.FromResult<LayoutApplyResult?>(key == layouts[0]
                ? new(2, 1, 0) : new(0, 0, 2));
        });

        CollectionAssert.AreEqual(layouts, attempted);
        Assert.AreEqual(2, result.Applied);
        Assert.AreEqual(2, result.Bound);
        Assert.AreEqual(1, result.Moved);
        Assert.AreEqual(2, result.ConnectionRequests);
        Assert.HasCount(1, result.Failures);
        Assert.AreEqual(layouts[1], result.Failures[0].Key);
        StringAssert.Contains(result.Failures[0].Error.Message, "ambiguous");
    }

    [TestMethod]
    public async Task Apply_StopsOnCancellationRatherThanReportingItAsALayoutFailure()
    {
        var layouts = new[] { new LayoutKey(Guid.NewGuid()), new LayoutKey(Guid.NewGuid()) };
        using var cancellation = new CancellationTokenSource();
        var attempted = 0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            LayoutBatchApplier.ApplyAsync(layouts, _ =>
            {
                attempted++;
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }, cancellation.Token));
        Assert.AreEqual(1, attempted);
    }
}
