using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class TargetSessionWindowsTests
{
    private static readonly Guid ManagerDesktop = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid TargetDesktop = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly PixelRect Area = new(0, 0, 3840, 2088);
    private static SessionWindow Client(Guid desktop) =>
        new(new(1, 42, 100), "azdo1", desktop, new(50, 50, 800, 600), false, false, false);

    private sealed class Windows : ISessionWindows
    {
        public BoardEnvironment Environment { get; set; } = new(ManagerDesktop, true, true, Area);
        public List<SessionWindow> Items { get; set; } = [];
        public WindowIdentity? ClosedIdentity { get; private set; }
        public BoardEnvironment GetEnvironment() => Environment;
        public IReadOnlyList<SessionWindow> Enumerate() => Items.ToArray();
        public int CountVisibleTopLevelWindows(WindowIdentity identity) => 1;
        public Task CloseReconnectPromptAsync(SessionWindow window, CancellationToken ct)
        {
            ClosedIdentity = window.Identity;
            return Task.CompletedTask;
        }
        public PixelRect GetVisibleBounds(SessionWindow window) => window.Bounds;
        public Task PlaceAsync(SessionWindow window, PixelRect bounds, CancellationToken ct) =>
            throw new AssertFailedException("The manager desktop must not position target-desktop clients.");
    }

    [TestMethod]
    public async Task TargetWindows_PlaceClientOnInactiveDesktopWithoutUsingManagerPlacement()
    {
        var manager = new Windows { Items = [Client(TargetDesktop)] };
        var moves = new List<PixelRect>();
        var target = new TargetSessionWindows(manager, TargetDesktop, Area, () => ManagerDesktop,
            (window, bounds, _) =>
            {
                moves.Add(bounds);
                manager.Items = [window with { Bounds = bounds }];
                return Task.CompletedTask;
            });
        Assert.AreEqual(TargetDesktop, target.GetEnvironment().DesktopId);
        Assert.IsFalse(target.GetEnvironment().IsCurrentDesktop);
        await target.PlaceAsync(manager.Items[0], new(1920, 1044, 1920, 1044), CancellationToken.None);
        Assert.AreEqual(new PixelRect(1920, 1044, 1920, 1044), moves.Single());
    }

    [TestMethod]
    public async Task TargetWindows_RejectWrongDesktopBoundsAndLockedSession()
    {
        var manager = new Windows { Items = [Client(ManagerDesktop)] };
        int moves = 0;
        var target = new TargetSessionWindows(manager, TargetDesktop, Area, () => ManagerDesktop,
            (_, _, _) => { moves++; return Task.CompletedTask; });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            target.PlaceAsync(manager.Items[0], new(0, 0, 1920, 1044), CancellationToken.None));
        manager.Items = [Client(TargetDesktop)];
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            target.PlaceAsync(manager.Items[0], new(3000, 0, 1920, 1044), CancellationToken.None));
        manager.Environment = manager.Environment with { CanInteract = false };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            target.PlaceAsync(manager.Items[0], new(0, 0, 1920, 1044), CancellationToken.None));
        Assert.AreEqual(0, moves);
    }

    [TestMethod]
    public async Task TargetWindows_OnlyCloseReconnectClientOnAssignedUnlockedDesktop()
    {
        var manager = new Windows { Items = [Client(TargetDesktop)] };
        var target = new TargetSessionWindows(manager, TargetDesktop, Area, () => ManagerDesktop,
            (_, _, _) => Task.CompletedTask);
        await target.CloseReconnectPromptAsync(manager.Items[0], CancellationToken.None);
        Assert.AreEqual(manager.Items[0].Identity, manager.ClosedIdentity);

        manager.Items = [Client(ManagerDesktop)];
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            target.CloseReconnectPromptAsync(manager.Items[0], CancellationToken.None));
        manager.Items = [Client(TargetDesktop)];
        manager.Environment = manager.Environment with { CanInteract = false };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            target.CloseReconnectPromptAsync(manager.Items[0], CancellationToken.None));
    }

    [TestMethod]
    public void TargetWindows_SameDesktopUsesManagerCurrentStateWithoutPrivateLookup()
    {
        var manager = new Windows();
        var target = new TargetSessionWindows(manager, ManagerDesktop, Area,
            () => throw new AssertFailedException("Same-desktop layout should use the public manager state."),
            (_, _, _) => Task.CompletedTask);
        Assert.IsTrue(target.GetEnvironment().IsCurrentDesktop);
    }
}
