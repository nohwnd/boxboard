using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class DesktopWindowLayoutTests
{
    private static readonly Guid Desktop = Guid.Parse("00000000-0000-0000-0000-000000000099");
    private static SessionWindow Client(nint handle, string title, Guid? desktop = null) =>
        new(new(handle, checked((int)handle), 100), title, desktop ?? Desktop,
            new(10, 10, 800, 600), false, false, false);

    private sealed class Windows : ISessionWindows
    {
        public BoardEnvironment Environment { get; set; } = new(Desktop, false, true, new(0, 0, 3840, 2088));
        public List<SessionWindow> Items { get; set; } = [];
        public List<(WindowIdentity Identity, PixelRect Bounds)> Moves { get; } = [];
        public BoardEnvironment GetEnvironment() => Environment;
        public IReadOnlyList<SessionWindow> Enumerate() => Items.ToArray();
        public int CountVisibleTopLevelWindows(WindowIdentity identity) => 1;
        public Task CloseReconnectPromptAsync(SessionWindow window, CancellationToken ct) =>
            throw new AssertFailedException("Unexpected reconnect-window close.");
        public PixelRect GetVisibleBounds(SessionWindow window) => window.Bounds;
        public Task PlaceAsync(SessionWindow window, PixelRect bounds, CancellationToken ct)
        {
            Moves.Add((window.Identity, bounds));
            Items = Items.Select(w => w.Identity == window.Identity ? w with { Bounds = bounds } : w).ToList();
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task Focus_ResizesOnlyBoundClientsOnInactiveDesktopAndCanSwitch()
    {
        var one = Client(1, "007playground");
        var four = Client(4, "azdo1");
        var unrelated = Client(9, "unrelated");
        var windows = new Windows { Items = [one, four, unrelated] };
        var layout = new DesktopWindowLayout(windows);
        SessionWindow?[] slots = [one, null, null, four];

        var first = await layout.ArrangeFocusAsync(slots, 0);
        Assert.HasCount(2, windows.Moves);
        Assert.AreEqual(first[0], windows.GetVisibleBounds(windows.Items.Single(w => w.Identity == one.Identity)));
        Assert.AreEqual(first[3], windows.GetVisibleBounds(windows.Items.Single(w => w.Identity == four.Identity)));
        Assert.IsFalse(windows.Moves.Any(m => m.Identity == unrelated.Identity));

        var second = await layout.ArrangeFocusAsync(slots, 3, 75);
        Assert.HasCount(4, windows.Moves);
        Assert.AreEqual(second[3], windows.Moves[^1].Bounds);
        Assert.AreEqual(2880, second[3].Width);
        Assert.AreEqual(new PixelRect(10, 10, 800, 600),
            windows.Items.Single(w => w.Identity == unrelated.Identity).Bounds);
    }

    [TestMethod]
    public async Task Focus_RejectsWrongDesktopFullscreenDuplicateAndLockBeforeMovingAnything()
    {
        var one = Client(1, "007playground");
        var four = Client(4, "azdo1");
        var windows = new Windows { Items = [one, four] };
        var layout = new DesktopWindowLayout(windows);
        SessionWindow?[] slots = [one, null, null, four];

        windows.Items[1] = four with { DesktopId = Guid.NewGuid() };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => layout.ArrangeFocusAsync(slots, 0));
        windows.Items[1] = four with { Fullscreen = true };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => layout.ArrangeFocusAsync(slots, 0));
        windows.Items[1] = four;
        slots[3] = one;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => layout.ArrangeFocusAsync(slots, 0));
        slots[3] = four;
        windows.Environment = windows.Environment with { CanInteract = false };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => layout.ArrangeFocusAsync(slots, 0));
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task Overlay_OnlyResizesSelectedWindowAndRestoresPreviousQuadrant()
    {
        var original = new[]
        {
            new PixelRect(12, 24, 1800, 920),
            new PixelRect(1940, 30, 1800, 920),
            new PixelRect(18, 1100, 1800, 920),
            new PixelRect(1932, 1100, 1800, 920)
        };
        SessionWindow?[] slots =
        [
            Client(1, "azdo1") with { Bounds = original[0] },
            Client(2, "azdo2") with { Bounds = original[1] },
            Client(3, "azdo3") with { Bounds = original[2] },
            Client(4, "007playground") with { Bounds = original[3] }
        ];
        var unrelated = Client(9, "unrelated");
        var windows = new Windows { Items = [.. slots.OfType<SessionWindow>(), unrelated] };
        var layout = new DesktopWindowLayout(windows);

        var first = await layout.ArrangeOverlayAsync(slots, 0);
        Assert.HasCount(1, windows.Moves);
        Assert.AreEqual(first, windows.Items.Single(w => w.Identity == slots[0]!.Identity).Bounds);
        for (int index = 1; index < 4; index++)
            Assert.AreEqual(original[index], windows.Items.Single(w => w.Identity == slots[index]!.Identity).Bounds);

        var fourth = await layout.ArrangeOverlayAsync(slots, 3);
        Assert.HasCount(3, windows.Moves);
        Assert.AreEqual(slots[0]!.Identity, windows.Moves[1].Identity);
        Assert.AreEqual(original[0], windows.Moves[1].Bounds);
        Assert.AreEqual(slots[3]!.Identity, windows.Moves[2].Identity);
        Assert.AreEqual(fourth, windows.Moves[2].Bounds);
        Assert.AreEqual(original[1], windows.Items.Single(w => w.Identity == slots[1]!.Identity).Bounds);
        Assert.AreEqual(original[2], windows.Items.Single(w => w.Identity == slots[2]!.Identity).Bounds);
        Assert.AreEqual(new PixelRect(10, 10, 800, 600),
            windows.Items.Single(w => w.Identity == unrelated.Identity).Bounds);

        await layout.ArrangeOverlayAsync(slots, 3);
        Assert.HasCount(3, windows.Moves);
        await layout.RestoreGridAsync(slots);
        Assert.HasCount(4, windows.Moves);
        for (int index = 0; index < 4; index++)
            Assert.AreEqual(original[index], windows.Items.Single(w => w.Identity == slots[index]!.Identity).Bounds);
        await layout.ArrangeOverlayAsync(slots, 3);
        Assert.HasCount(5, windows.Moves);
    }

    [TestMethod]
    [DataRow(0, 3840, 2088)]
    [DataRow(1, 3840, 2088)]
    [DataRow(2, 1919, 1079)]
    [DataRow(3, 1919, 1079)]
    public void Overlay_StaysWithinWorkAreaAndKeepsOtherQuadrantsAvailable(int index, int width, int height)
    {
        var area = new PixelRect(100, 200, width, height);
        var baseBounds = FourCellGeometry.Divide(area, gap: 0);
        var focused = FourCellGeometry.Overlay(area, index);
        Assert.IsTrue(area.Contains(focused));
        Assert.IsTrue(focused.Contains(baseBounds[index]));
        Assert.IsGreaterThan(baseBounds[index].Width, focused.Width);
        Assert.IsGreaterThan(baseBounds[index].Height, focused.Height);
        Assert.IsTrue(Enumerable.Range(0, 4).Where(i => i != index).All(i =>
            focused.Right < baseBounds[i].Right || focused.X > baseBounds[i].X ||
            focused.Bottom < baseBounds[i].Bottom || focused.Y > baseBounds[i].Y));
    }

    [TestMethod]
    public async Task Overlay_RejectsInvalidFocusWithoutMovingClients()
    {
        var one = Client(1, "azdo1");
        var windows = new Windows { Items = [one] };
        var layout = new DesktopWindowLayout(windows);
        SessionWindow?[] slots = [one, null, null, null];
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => layout.ArrangeOverlayAsync(slots, 1));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => layout.ArrangeOverlayAsync(slots, 0, 81));
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task Overlay_RejectsFocusThatWouldCompletelyHideAnotherClient()
    {
        var first = Client(1, "azdo1");
        var fourth = Client(4, "007playground") with { Bounds = new(100, 100, 500, 400) };
        var windows = new Windows { Items = [first, fourth] };
        var layout = new DesktopWindowLayout(windows);
        SessionWindow?[] slots = [first, null, null, fourth];
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            layout.ArrangeOverlayAsync(slots, 0));
        Assert.Contains("would hide 007playground", error.Message);
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task ForegroundController_MapsOnlyBoundHandlesAndRestoresOriginalGrid()
    {
        var first = Client(1, "azdo1") with { Bounds = new(12, 24, 1800, 920) };
        var fourth = Client(4, "007playground") with { Bounds = new(1932, 1100, 1800, 920) };
        var windows = new Windows { Items = [first, fourth, Client(9, "unrelated")] };
        var controller = new DesktopFocusController(new DesktopWindowLayout(windows));
        SessionWindow?[] slots = [first, null, null, fourth];

        Assert.IsNull(await controller.HandleForegroundAsync(9, slots));
        Assert.IsEmpty(windows.Moves);
        var one = await controller.HandleForegroundAsync(1, slots);
        Assert.AreEqual(0, one?.FocusedIndex);
        Assert.HasCount(1, windows.Moves);
        var four = await controller.HandleForegroundAsync(4, slots);
        Assert.AreEqual(0, four?.PreviousIndex);
        Assert.AreEqual(3, four?.FocusedIndex);
        Assert.HasCount(3, windows.Moves);
        Assert.AreEqual(first.Bounds, windows.Items.Single(w => w.Identity == first.Identity).Bounds);
        var restored = await controller.HandleForegroundAsync(9, slots);
        Assert.AreEqual(3, restored?.PreviousIndex);
        Assert.IsNull(restored?.FocusedIndex);
        Assert.AreEqual(fourth.Bounds, windows.Items.Single(w => w.Identity == fourth.Identity).Bounds);
        Assert.IsNull(await controller.HandleForegroundAsync(9, slots));
        Assert.HasCount(4, windows.Moves);
    }

    [TestMethod]
    public async Task ForegroundRouter_SwitchesDesktopLayoutsWithoutResizingOtherDesktopClients()
    {
        var otherDesktop = Guid.NewGuid();
        var first = Client(1, "azdo1") with { Bounds = new(0, 0, 1800, 900) };
        var second = Client(4, "azdo2") with { Bounds = new(1930, 1100, 1800, 900) };
        var third = Client(11, "azdo3", otherDesktop) with { Bounds = new(0, 0, 1800, 900) };
        var fourth = Client(14, "azdo4", otherDesktop) with { Bounds = new(1930, 1100, 1800, 900) };
        var windowsA = new Windows { Items = [first, second] };
        var windowsB = new Windows
        {
            Environment = new(otherDesktop, true, true, new(0, 0, 3840, 2088)),
            Items = [third, fourth]
        };
        var layouts = new DesktopFocusLayout[]
        {
            new(Desktop, new(new DesktopWindowLayout(windowsA)), [first, null, null, second]),
            new(otherDesktop, new(new DesktopWindowLayout(windowsB)), [third, null, null, fourth])
        };

        var firstClick = await DesktopFocusRouter.RouteAsync(first.Identity.Handle, layouts);
        Assert.HasCount(1, firstClick);
        Assert.AreEqual(Desktop, firstClick[0].DesktopId);
        Assert.HasCount(1, windowsA.Moves);
        Assert.IsEmpty(windowsB.Moves);

        var secondClick = await DesktopFocusRouter.RouteAsync(fourth.Identity.Handle, layouts);
        Assert.HasCount(2, secondClick);
        Assert.AreEqual(first.Bounds, windowsA.Items.Single(w => w.Identity == first.Identity).Bounds);
        Assert.AreEqual(third.Bounds, windowsB.Items.Single(w => w.Identity == third.Identity).Bounds);
        Assert.HasCount(2, windowsA.Moves);
        Assert.HasCount(1, windowsB.Moves);

        var outside = await DesktopFocusRouter.RouteAsync(99, layouts);
        Assert.HasCount(1, outside);
        Assert.AreEqual(fourth.Bounds, windowsB.Items.Single(w => w.Identity == fourth.Identity).Bounds);
        Assert.HasCount(2, windowsB.Moves);
    }

    [TestMethod]
    public async Task ForegroundRouter_RejectsAmbiguousWindowIdentityBeforeMoving()
    {
        var client = Client(1, "azdo1");
        var first = new Windows { Items = [client] };
        var second = new Windows { Environment = new(Guid.NewGuid(), true, true, new(0, 0, 3840, 2088)) };
        var layouts = new DesktopFocusLayout[]
        {
            new(Desktop, new(new DesktopWindowLayout(first)), [client, null, null, null]),
            new(second.Environment.DesktopId, new(new DesktopWindowLayout(second)), [client, null, null, null])
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            DesktopFocusRouter.RouteAsync(client.Identity.Handle, layouts));
        Assert.IsEmpty(first.Moves);
        Assert.IsEmpty(second.Moves);
    }
}
