using Bevdox.Models;
using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class SessionCoordinatorTests
{
    private static readonly Guid Desktop = Guid.Parse("00000000-0000-0000-0000-000000000099");
    private static DevBoxInstance Machine => DemoData.Machines()[0];
    private static SlotAssignment Slot => new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, Machine.UniqueId);
    private static readonly PixelRect Cell = new(20, 20, 500, 400);
    private static SessionWindow Window(nint handle = 1, int pid = 10, long started = 100, Guid? desktop = null) =>
        new(new(handle, pid, started), "azdo1", desktop ?? Desktop, new(0, 0, 640, 480), false, false, false);

    private sealed class Windows : ISessionWindows
    {
        public BoardEnvironment Environment { get; set; } = new(Desktop, true, true, new(0, 0, 1920, 1080));
        public List<SessionWindow> Items { get; set; } = [];
        public List<(WindowIdentity Identity, PixelRect Bounds)> Moves { get; } = [];
        public (int Left, int Top, int Right, int Bottom) Insets { get; set; }
        public int VisibleWindowCount { get; set; } = 1;
        public int RejectedPlacements { get; set; }
        public int PlacementAttempts { get; private set; }
        public BoardEnvironment GetEnvironment() => Environment;
        public IReadOnlyList<SessionWindow> Enumerate() => Items.ToArray();
        public int CountVisibleTopLevelWindows(WindowIdentity identity) => VisibleWindowCount;
        public Task CloseReconnectPromptAsync(SessionWindow window, CancellationToken ct) =>
            throw new AssertFailedException("Unexpected reconnect-window close.");
        public PixelRect GetVisibleBounds(SessionWindow window) => new(
            window.Bounds.X + Insets.Left, window.Bounds.Y + Insets.Top,
            window.Bounds.Width - Insets.Left - Insets.Right,
            window.Bounds.Height - Insets.Top - Insets.Bottom);
        public Task PlaceAsync(SessionWindow window, PixelRect bounds, CancellationToken ct)
        {
            PlacementAttempts++;
            if (PlacementAttempts <= RejectedPlacements)
                throw new WindowPlacementRejectedException("Windows App is still sizing its client.");
            Moves.Add((window.Identity, bounds));
            var outer = new PixelRect(bounds.X - Insets.Left, bounds.Y - Insets.Top,
                bounds.Width + Insets.Left + Insets.Right, bounds.Height + Insets.Top + Insets.Bottom);
            Items = Items.Select(w => w.Identity == window.Identity ? w with { Bounds = outer } : w).ToList();
            return Task.CompletedTask;
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static SessionCoordinator Create(Windows windows, Action<Uri>? launch = null, Clock? clock = null) =>
        new(windows, (_, _) => Task.FromResult(new Uri("ms-avd:connect?resourceid=synthetic")),
            launch ?? (_ => Assert.Fail("This test must not launch a session.")), clock);

    [TestMethod]
    public async Task Arrange_ExistingWindow_PreservesPositionUntilExplicitArrange()
    {
        var windows = new Windows { Items = [Window()] };
        using var coordinator = Create(windows);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.IsEmpty(windows.Moves);
        coordinator.Bind(Slot, Machine, Window().Identity);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.IsEmpty(windows.Moves);
        Assert.Contains("preserved", coordinator.For(Slot).Status);
        coordinator.InvalidatePlacements();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual((Window().Identity, Cell), windows.Moves.Single());
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.HasCount(1, windows.Moves);
    }

    [TestMethod]
    public async Task BindExisting_AdoptsUniqueClientsWithoutLaunchingOrMovingThem()
    {
        var secondMachine = DemoData.Machines()[1];
        var secondSlot = new SlotAssignment(Guid.NewGuid(), 2, secondMachine.UniqueId);
        var first = Window();
        var second = Window(2, 20) with { Title = secondMachine.OriginalName };
        var foreign = Window(3, 30, desktop: Guid.NewGuid());
        var windows = new Windows { Items = [first, second, foreign] };
        using var coordinator = Create(windows);

        var result = coordinator.BindExisting([(Slot, Machine), (secondSlot, secondMachine)], DemoData.Machines());
        Assert.AreEqual(2, result.Bound);
        Assert.IsTrue(result.Complete);
        CollectionAssert.AreEqual(new[] { first.Identity, second.Identity },
            coordinator.BoundWindows([Slot, secondSlot]).Select(w => w?.Identity).ToArray());
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task BindExisting_OnInactiveDesktop_PreservesClientUntilExplicitArrange()
    {
        var windows = new Windows
        {
            Items = [Window()],
            Environment = new(Desktop, false, true, new(0, 0, 1920, 1080))
        };
        using var coordinator = Create(windows);
        var result = coordinator.BindExisting([(Slot, Machine)], DemoData.Machines());
        Assert.AreEqual(1, result.Bound);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.IsEmpty(windows.Moves);
        coordinator.InvalidatePlacements();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(Cell, windows.Moves.Single().Bounds);
    }

    [TestMethod]
    public void BindExisting_ReportsMissingOtherDesktopAndAmbiguousWindows()
    {
        var windows = new Windows();
        using var coordinator = Create(windows);
        var missing = coordinator.BindExisting([(Slot, Machine)], DemoData.Machines());
        CollectionAssert.AreEqual(new[] { Machine.EffectiveName }, missing.Missing.ToArray());
        Assert.AreEqual(0, missing.Bound);

        windows.Items = [Window(desktop: Guid.NewGuid())];
        var foreign = coordinator.BindExisting([(Slot, Machine)], DemoData.Machines());
        CollectionAssert.AreEqual(new[] { Machine.EffectiveName }, foreign.OtherDesktop.ToArray());
        Assert.AreEqual(0, foreign.Bound);

        windows.Items = [Window(), Window(2)];
        var ambiguous = coordinator.BindExisting([(Slot, Machine)], DemoData.Machines());
        CollectionAssert.AreEqual(new[] { Machine.EffectiveName }, ambiguous.Ambiguous.ToArray());
        Assert.AreEqual(0, ambiguous.Bound);
        Assert.IsNull(coordinator.For(Slot).BoundWindow);
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task BindExisting_ExplicitMoveAdoptsRemoteWindowWithoutResizingIt()
    {
        var source = Window(desktop: Guid.NewGuid());
        var windows = new Windows
        {
            Items = [source],
            Environment = new(Desktop, false, true, new(0, 0, 1920, 1080))
        };
        int moves = 0;
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("Binding must not fetch a connection URL."),
            _ => throw new AssertFailedException("Binding must not launch a connection."),
            moveToDesktop: (window, machine, target) =>
            {
                Assert.AreEqual(source.Identity, window.Identity);
                Assert.AreEqual(Machine.UniqueId, machine.UniqueId);
                Assert.AreEqual(Desktop, target);
                moves++;
                windows.Items = [window with { DesktopId = target }];
            });
        var result = coordinator.BindExisting([(Slot, Machine)], DemoData.Machines(),
            moveFromOtherDesktop: true);
        Assert.AreEqual(1, result.Bound);
        Assert.AreEqual(1, result.Moved);
        Assert.IsTrue(result.Complete);
        Assert.AreEqual(source.Identity, coordinator.For(Slot).BoundWindow);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.IsEmpty(windows.Moves);
        Assert.AreEqual(1, moves);
    }

    [TestMethod]
    public void BindExisting_ExplicitMoveRejectsAmbiguousOrUnavailableMover()
    {
        var windows = new Windows { Items = [Window(desktop: Guid.NewGuid())] };
        using var withoutMover = Create(windows);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            withoutMover.BindExisting([(Slot, Machine)], DemoData.Machines(), moveFromOtherDesktop: true));
        int moves = 0;
        using var withMover = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("No connection was requested."),
            _ => throw new AssertFailedException("No connection was requested."),
            moveToDesktop: (_, _, _) => moves++);
        windows.Items.Add(Window(2, 20, desktop: Guid.NewGuid()));
        var result = withMover.BindExisting([(Slot, Machine)], DemoData.Machines(),
            moveFromOtherDesktop: true);
        Assert.HasCount(1, result.Ambiguous);
        Assert.AreEqual(0, result.Moved);
        Assert.AreEqual(0, moves);
    }

    [TestMethod]
    public async Task ApplyLayout_BindsAndArrangesAlreadyConnectedClientsWithOneRequest()
    {
        var secondMachine = DemoData.Machines()[1];
        var secondSlot = new SlotAssignment(Guid.NewGuid(), 2, secondMachine.UniqueId);
        var windows = new Windows
        {
            Items = [Window(), Window(2, 20) with { Title = secondMachine.OriginalName }]
        };
        int launches = 0;
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("Existing clients must not request a connection URL."),
            _ => launches++, moveToDesktop: (_, _, _) =>
                throw new AssertFailedException("Both clients are already on this desktop."));
        var result = await coordinator.ApplyLayoutAsync(
            [(Slot, Machine), (secondSlot, secondMachine)], DemoData.Machines());
        Assert.AreEqual(new LayoutApplyResult(2, 0, 0), result);
        Assert.AreEqual(0, launches);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        await coordinator.ArrangeAsync(secondSlot, secondMachine, true, new(600, 20, 500, 400));
        Assert.HasCount(2, windows.Moves);
    }

    [TestMethod]
    public async Task ApplyLayout_RequestsMissingClientOnceWithoutUsingDiscoveryState()
    {
        var secondMachine = DemoData.Machines()[1];
        var secondSlot = new SlotAssignment(Guid.NewGuid(), 2, secondMachine.UniqueId);
        var windows = new Windows
        {
            Items = [Window(2, 20) with { Title = secondMachine.OriginalName }]
        };
        int launches = 0;
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, moveToDesktop: (_, _, _) =>
                throw new AssertFailedException("No desktop movement was required."));
        var result = await coordinator.ApplyLayoutAsync(
            [(Slot, Machine), (secondSlot, secondMachine)], DemoData.Machines());
        Assert.AreEqual(new LayoutApplyResult(1, 0, 1), result);
        Assert.AreEqual(1, launches);
        Assert.IsTrue(coordinator.For(Slot).Connecting);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.ApplyLayoutAsync([(Slot, Machine), (secondSlot, secondMachine)], DemoData.Machines()));
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task AutomaticLayoutChange_DoesNotDuplicateAnAlreadyPendingConnection()
    {
        var windows = new Windows();
        int launches = 0;
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, moveToDesktop: (_, _, _) => Assert.Fail("No client window exists."));

        await coordinator.ApplyLayoutAsync([(Slot, Machine)], DemoData.Machines());
        var again = await coordinator.ApplyLayoutAsync([(Slot, Machine)], DemoData.Machines(),
            skipPendingConnections: true);
        Assert.AreEqual(new LayoutApplyResult(0, 0, 0), again);
        Assert.AreEqual(1, launches);
        Assert.IsTrue(coordinator.For(Slot).Connecting);
    }

    [TestMethod]
    public async Task AutomaticAssignment_PlacesOnlyNewSlotAndKeepsManualPositionOfOtherClient()
    {
        var secondMachine = DemoData.Machines()[1];
        var secondSlot = new SlotAssignment(Guid.NewGuid(), 2, secondMachine.UniqueId);
        var first = Window();
        var second = Window(2, 20) with { Title = secondMachine.OriginalName };
        var windows = new Windows { Items = [first, second] };
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("Existing clients must not request a connection URL."),
            _ => Assert.Fail("Existing clients must not launch another window."),
            moveToDesktop: (_, _, _) => Assert.Fail("Both clients are already on this desktop."));
        coordinator.Bind(Slot, Machine, first.Identity);
        coordinator.InvalidatePlacements();
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        var manuallyMoved = new PixelRect(10, 600, 800, 400);
        windows.Items = windows.Items.Select(window => window.Identity == first.Identity
            ? window with { Bounds = manuallyMoved } : window).ToList();
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);

        var result = await coordinator.ApplyAssignedSlotAsync(secondSlot, secondMachine, DemoData.Machines());
        Assert.AreEqual(new LayoutApplyResult(1, 0, 0), result);
        coordinator.Observe();
        var secondBounds = new PixelRect(960, 0, 960, 1080);
        await coordinator.ArrangeAsync(secondSlot, secondMachine, true, secondBounds);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);

        Assert.HasCount(2, windows.Moves);
        Assert.AreEqual((first.Identity, Cell), windows.Moves[0]);
        Assert.AreEqual((second.Identity, secondBounds), windows.Moves[1]);
        Assert.AreEqual(manuallyMoved, windows.Items.Single(w => w.Identity == first.Identity).Bounds);
    }

    [TestMethod]
    public async Task SwappedAssignedSlots_RebindAndArrangeBothExistingClientsWithoutLaunching()
    {
        var secondMachine = DemoData.Machines()[1];
        var secondSlot = new SlotAssignment(Guid.NewGuid(), 2, secondMachine.UniqueId);
        var first = Window();
        var second = Window(2, 20) with { Title = secondMachine.OriginalName };
        var windows = new Windows { Items = [first, second] };
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("Swapping existing clients must not fetch a connection URL."),
            _ => Assert.Fail("Swapping existing clients must not start a new session."),
            moveToDesktop: (_, _, _) => Assert.Fail("Both clients are on the same desktop."));
        coordinator.Bind(Slot, Machine, first.Identity);
        coordinator.Bind(secondSlot, secondMachine, second.Identity);
        var newSource = Slot with { MachineId = secondMachine.UniqueId };
        var newTarget = secondSlot with { MachineId = Machine.UniqueId };
        coordinator.For(newSource);
        coordinator.For(newTarget);

        await coordinator.ApplyAssignedSlotAsync(newTarget, Machine, DemoData.Machines());
        coordinator.Observe();
        var targetBounds = new PixelRect(600, 20, 500, 400);
        await coordinator.ArrangeAsync(newTarget, Machine, true, targetBounds);
        await coordinator.ApplyAssignedSlotAsync(newSource, secondMachine, DemoData.Machines());
        coordinator.Observe();
        await coordinator.ArrangeAsync(newSource, secondMachine, true, Cell);

        Assert.HasCount(2, windows.Moves);
        Assert.AreEqual((first.Identity, targetBounds), windows.Moves[0]);
        Assert.AreEqual((second.Identity, Cell), windows.Moves[1]);
    }

    [TestMethod]
    public async Task ApplyLayout_AmbiguousClientAbortsBeforeBindingOtherClients()
    {
        var secondMachine = DemoData.Machines()[1];
        var secondSlot = new SlotAssignment(Guid.NewGuid(), 2, secondMachine.UniqueId);
        var windows = new Windows
        {
            Items =
            [
                Window(), Window(2, 20),
                Window(3, 30) with { Title = secondMachine.OriginalName }
            ]
        };
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("Ambiguous client must not fetch connection info."),
            _ => throw new AssertFailedException("Ambiguous client must not launch."),
            moveToDesktop: (_, _, _) => throw new AssertFailedException("No client may move."));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.ApplyLayoutAsync([(Slot, Machine), (secondSlot, secondMachine)], DemoData.Machines()));
        Assert.IsNull(coordinator.For(secondSlot).BoundWindow);
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public void BindExisting_DuplicateCatalogTitlesRequireManualSelection()
    {
        var windows = new Windows { Items = [Window()] };
        using var coordinator = Create(windows);
        var duplicate = DemoData.Machines()[1];
        duplicate.OriginalName = Machine.OriginalName;
        var result = coordinator.BindExisting([(Slot, Machine)], [Machine, duplicate]);
        Assert.HasCount(1, result.Ambiguous);
        Assert.IsNull(coordinator.For(Slot).BoundWindow);
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task ExtraWindow_ReportsPossibleReconnectWithoutMovingOrLaunching()
    {
        var windows = new Windows { Items = [Window()], VisibleWindowCount = 2 };
        using var coordinator = Create(windows);
        coordinator.Observe();
        Assert.HasCount(1, coordinator.ReconnectPromptCandidates);
        Assert.AreEqual("azdo1", coordinator.ReconnectPromptCandidates[0].ClientName);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.Contains("Possible reconnect prompt", coordinator.For(Slot).Status);
        Assert.IsTrue(coordinator.For(Slot).ReconnectPromptSuspected);
        Assert.IsEmpty(windows.Moves);

        windows.VisibleWindowCount = 1;
        coordinator.Observe();
        Assert.IsEmpty(coordinator.ReconnectPromptCandidates);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.IsFalse(coordinator.For(Slot).ReconnectPromptSuspected);
        Assert.AreEqual("Client window found. Bind it to this cell.", coordinator.For(Slot).Status);
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task BoundWindow_ExtraWindowPausesPlacementEvenOnInactiveDesktop()
    {
        var windows = new Windows { Items = [Window()] };
        using var coordinator = Create(windows);
        coordinator.Bind(Slot, Machine, Window().Identity);
        windows.VisibleWindowCount = 2;
        windows.Environment = windows.Environment with { IsCurrentDesktop = false };
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.Contains("Possible reconnect prompt", coordinator.For(Slot).Status);
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public void TwoMainWindowsInOneProcess_AreNotAssumedToBeReconnectPrompt()
    {
        var windows = new Windows { Items = [Window(), Window(2)], VisibleWindowCount = 2 };
        using var coordinator = Create(windows);
        coordinator.Observe();
        Assert.IsEmpty(coordinator.ReconnectPromptCandidates);
    }

    [TestMethod]
    public async Task Arrange_ManualClientResize_OnlyExplicitApplyResetsIt()
    {
        var windows = new Windows { Items = [Window()] };
        using var coordinator = Create(windows);
        coordinator.Bind(Slot, Machine, Window().Identity);
        coordinator.InvalidatePlacements();
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        windows.Items = [windows.Items[0] with { Bounds = new(40, 40, 700, 500) }];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.HasCount(1, windows.Moves);
        Assert.Contains("moved manually", coordinator.For(Slot).Status);

        var largerCell = new PixelRect(20, 20, 800, 600);
        await coordinator.ArrangeAsync(Slot, Machine, true, largerCell);
        Assert.HasCount(1, windows.Moves);
        Assert.AreEqual(new PixelRect(40, 40, 700, 500), windows.Items.Single().Bounds);
        coordinator.InvalidatePlacements();
        await coordinator.ArrangeAsync(Slot, Machine, true, largerCell);
        Assert.HasCount(2, windows.Moves);
        Assert.AreEqual(largerCell, windows.Moves[^1].Bounds);
    }

    [TestMethod]
    public async Task Arrange_TransientClientSizing_RetriesAfterBackoffWithoutAnotherLaunch()
    {
        var windows = new Windows { Items = [Window()], RejectedPlacements = 2 };
        var clock = new Clock();
        using var coordinator = Create(windows, clock: clock);
        coordinator.Bind(Slot, Machine, Window().Identity, preservePosition: false);
        coordinator.Observe();

        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(1, windows.PlacementAttempts);
        Assert.Contains("attempt 1/3", coordinator.For(Slot).Status);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(1, windows.PlacementAttempts);
        clock.Now += TimeSpan.FromSeconds(2);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(2, windows.PlacementAttempts);
        Assert.Contains("attempt 2/3", coordinator.For(Slot).Status);
        clock.Now += TimeSpan.FromSeconds(3);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(2, windows.PlacementAttempts);
        clock.Now += TimeSpan.FromSeconds(1);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(3, windows.PlacementAttempts);
        Assert.AreEqual(Cell, windows.Moves.Single().Bounds);
        Assert.Contains("Placed HWND", coordinator.For(Slot).Status);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(3, windows.PlacementAttempts);
    }

    [TestMethod]
    public async Task NewClient_LateStartupResizeIsCorrectedButLaterManualResizeIsPreserved()
    {
        var windows = new Windows();
        var clock = new Clock();
        int launches = 0;
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        await coordinator.ConnectAsync(Slot, Machine, nameIsUnique: true);
        windows.Items = [Window(55, 99, 500)];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.HasCount(1, windows.Moves);

        clock.Now += TimeSpan.FromMilliseconds(700);
        windows.Items = [windows.Items[0] with { Bounds = new(0, 0, 1920, 1080) }];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.HasCount(2, windows.Moves);
        Assert.AreEqual(Cell, windows.Items[0].Bounds);
        Assert.AreEqual(1, launches);

        clock.Now += TimeSpan.FromSeconds(16);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        windows.Items = [windows.Items[0] with { Bounds = new(40, 40, 700, 500) }];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.HasCount(2, windows.Moves);
        Assert.AreEqual(new PixelRect(40, 40, 700, 500), windows.Items[0].Bounds);
        Assert.Contains("moved manually", coordinator.For(Slot).Status);
    }

    [TestMethod]
    public async Task NewClient_InitialSizeRejectionCanSettleOnFourthBoundedAttempt()
    {
        var windows = new Windows { RejectedPlacements = 3 };
        var clock = new Clock();
        int launches = 0;
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        await coordinator.ConnectAsync(Slot, Machine, nameIsUnique: true);
        windows.Items = [Window(55, 99, 500)];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        clock.Now += TimeSpan.FromSeconds(2);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        clock.Now += TimeSpan.FromSeconds(4);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.Contains("attempt 3/5", coordinator.For(Slot).Status);
        clock.Now += TimeSpan.FromSeconds(4);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(4, windows.PlacementAttempts);
        Assert.AreEqual(Cell, windows.Moves.Single().Bounds);
        Assert.AreEqual(1, launches);
        Assert.IsFalse(coordinator.For(Slot).Failed);
    }

    [TestMethod]
    public async Task NewClient_FiveRejectedSizesStopWithoutAnotherLaunch()
    {
        var windows = new Windows { RejectedPlacements = 5 };
        var clock = new Clock();
        int launches = 0;
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        await coordinator.ConnectAsync(Slot, Machine, nameIsUnique: true);
        windows.Items = [Window(55, 99, 500)];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        foreach (var delay in new[] { 2, 4, 4 })
        {
            clock.Now += TimeSpan.FromSeconds(delay);
            await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        }
        clock.Now += TimeSpan.FromSeconds(4);
        await Assert.ThrowsExactlyAsync<WindowPlacementRejectedException>(() =>
            coordinator.ArrangeAsync(Slot, Machine, true, Cell));
        Assert.AreEqual(5, windows.PlacementAttempts);
        Assert.Contains("failed after five attempts", coordinator.For(Slot).Status);
        Assert.IsTrue(coordinator.For(Slot).Failed);
        Assert.AreEqual(1, launches);
        clock.Now += TimeSpan.FromMinutes(1);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(5, windows.PlacementAttempts);
    }

    [TestMethod]
    public async Task Arrange_ThreeRejectedSizes_PausesUntilExplicitApply()
    {
        var windows = new Windows { Items = [Window()], RejectedPlacements = 3 };
        var clock = new Clock();
        using var coordinator = Create(windows, clock: clock);
        coordinator.Bind(Slot, Machine, Window().Identity, preservePosition: false);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        clock.Now += TimeSpan.FromSeconds(2);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        clock.Now += TimeSpan.FromSeconds(4);
        await Assert.ThrowsExactlyAsync<WindowPlacementRejectedException>(() =>
            coordinator.ArrangeAsync(Slot, Machine, true, Cell));
        Assert.Contains("failed after three attempts", coordinator.For(Slot).Status);
        Assert.IsFalse(coordinator.For(Slot).Connecting);
        Assert.IsTrue(coordinator.For(Slot).Failed);
        clock.Now += TimeSpan.FromMinutes(10);
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(3, windows.PlacementAttempts);

        coordinator.InvalidatePlacements();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(4, windows.PlacementAttempts);
        Assert.AreEqual(Cell, windows.Moves.Single().Bounds);
    }

    [TestMethod]
    public async Task Arrange_InvisibleWindowInset_DoesNotReapplyOrReportManualResize()
    {
        var windows = new Windows { Items = [Window()], Insets = (9, 0, 9, 9) };
        using var coordinator = Create(windows);
        coordinator.Bind(Slot, Machine, Window().Identity);
        coordinator.InvalidatePlacements();
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.HasCount(1, windows.Moves);
        Assert.AreEqual(Cell, windows.GetVisibleBounds(windows.Items.Single()));
        Assert.DoesNotContain("resized manually", coordinator.For(Slot).Status);
    }

    [TestMethod]
    public void Bind_OneWindowCannotBackTwoMachineIdentities()
    {
        var windows = new Windows { Items = [Window()] };
        using var coordinator = Create(windows);
        coordinator.Bind(Slot, Machine, Window().Identity);
        var otherMachine = Machine;
        otherMachine.UniqueId += "-different-resource";
        var otherSlot = new SlotAssignment(Guid.NewGuid(), 2, otherMachine.UniqueId);
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.Bind(otherSlot, otherMachine, Window().Identity));
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task Bind_OtherDesktop_RefusesToMoveOrLaunch()
    {
        var other = Window(desktop: Guid.NewGuid());
        var windows = new Windows { Items = [other] };
        using var coordinator = Create(windows);
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.Bind(Slot, Machine, other.Identity));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ConnectAsync(Slot, Machine, true));
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task ReplacedFullscreenWindow_ReportsReplacementInsteadOfStaleFullscreenError()
    {
        var windows = new Windows { Items = [Window() with { Fullscreen = true }] };
        using var coordinator = Create(windows);
        coordinator.Bind(Slot, Machine, Window().Identity);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.Contains("fullscreen", coordinator.For(Slot).Status);
        windows.Items = [Window(2, 20, 200)];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual("Client window found. Bind it to this cell.", coordinator.For(Slot).Status);
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task Reconnect_NewUniqueWindow_RebindsSameCellWithoutPersistingHandle()
    {
        int launches = 0;
        var windows = new Windows();
        using var coordinator = Create(windows, _ => launches++);
        await coordinator.ConnectAsync(Slot, Machine, true);
        Assert.IsTrue(coordinator.For(Slot).Connecting);
        windows.Items = [Window(55, 99, 500)];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(Window(55, 99, 500).Identity, coordinator.For(Slot).BoundWindow);
        Assert.AreEqual(Cell, windows.Moves.Single().Bounds);
        Assert.AreEqual(1, launches);
        Assert.IsFalse(coordinator.For(Slot).Connecting);
    }

    [TestMethod]
    public async Task Connect_NewClientFromActiveDesktop_MovesToInactiveTargetAndPlacesIt()
    {
        var windows = new Windows
        {
            Environment = new(Desktop, false, true, new(0, 0, 1920, 1080))
        };
        int launches = 0, moves = 0;
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, moveToDesktop: (client, machine, desktop) =>
            {
                Assert.AreEqual(Machine.UniqueId, machine.UniqueId);
                Assert.AreEqual(Desktop, desktop);
                moves++;
                windows.Items = [client with { DesktopId = desktop }];
            });
        await coordinator.ConnectAsync(Slot, Machine, true);
        var newWindow = Window(55, 99, 500, Guid.NewGuid());
        windows.Items = [newWindow];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(1, launches);
        Assert.AreEqual(1, moves);
        Assert.AreEqual(newWindow.Identity, coordinator.For(Slot).BoundWindow);
        Assert.AreEqual(Cell, windows.Moves.Single().Bounds);
        Assert.IsFalse(coordinator.For(Slot).Connecting);
    }

    [TestMethod]
    public async Task Connect_MoveFailureStopsPendingRequestWithoutRetry()
    {
        var windows = new Windows
        {
            Environment = new(Desktop, false, true, new(0, 0, 1920, 1080))
        };
        int attempts = 0;
        using var coordinator = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => { }, moveToDesktop: (_, _, _) =>
            {
                attempts++;
                throw new InvalidOperationException("Shell denied movement.");
            });
        await coordinator.ConnectAsync(Slot, Machine, true);
        windows.Items = [Window(55, 99, 500, Guid.NewGuid())];
        coordinator.Observe();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.ArrangeAsync(Slot, Machine, true, Cell));
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(1, attempts);
        Assert.IsTrue(coordinator.For(Slot).Failed);
        Assert.Contains("Shell denied movement", coordinator.For(Slot).Status);
    }

    [TestMethod]
    public async Task RequestedWindow_OnWrongDesktop_ReportsItWithoutMovingOrRetrying()
    {
        var windows = new Windows();
        int launches = 0;
        using var coordinator = Create(windows, _ => launches++);
        await coordinator.ConnectAsync(Slot, Machine, true);
        windows.Items = [Window(9, 90, 900, Guid.NewGuid())];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.Contains("opened the requested client on another virtual desktop", coordinator.For(Slot).Status);
        Assert.IsFalse(coordinator.For(Slot).Connecting);
        Assert.IsEmpty(windows.Moves);
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task Reconnect_DuplicateClickWhileAwaitingWindow_DoesNotLaunchTwice()
    {
        int launches = 0;
        using var coordinator = Create(new Windows(), _ => launches++);
        await coordinator.ConnectAsync(Slot, Machine, true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ConnectAsync(Slot, Machine, true));
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task Reconnect_LockDuringApiCall_PreventsLaunch()
    {
        var windows = new Windows();
        var request = new TaskCompletionSource<Uri>();
        int launches = 0;
        using var coordinator = new SessionCoordinator(windows, (_, _) => request.Task, _ => launches++);
        var connect = coordinator.ConnectAsync(Slot, Machine, true);
        windows.Environment = windows.Environment with { CanInteract = false };
        coordinator.Observe();
        request.SetResult(new Uri("ms-avd:connect?resourceid=synthetic"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => connect);
        Assert.AreEqual(0, launches);
        windows.Environment = windows.Environment with { CanInteract = true };
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(0, launches);
    }

    [TestMethod]
    public async Task Reconnect_WindowAppearsDuringApiCall_RefusesDuplicateLaunch()
    {
        var windows = new Windows();
        var request = new TaskCompletionSource<Uri>();
        using var coordinator = new SessionCoordinator(windows, (_, _) => request.Task, _ => Assert.Fail("Duplicate launch."));
        var connect = coordinator.ConnectAsync(Slot, Machine, true);
        windows.Items = [Window()];
        request.SetResult(new Uri("ms-avd:connect?resourceid=synthetic"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => connect);
    }

    [TestMethod]
    public async Task Replacement_SameProcessRecreatedHandle_RebindsButDoesNotTrustReusedPid()
    {
        var windows = new Windows { Items = [Window()] };
        using var coordinator = Create(windows);
        coordinator.Bind(Slot, Machine, Window().Identity);
        windows.Items = [Window(2)];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(Window(2).Identity, coordinator.For(Slot).BoundWindow);
        windows.Items = [Window(3, started: 999)];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.AreEqual(Window(2).Identity, coordinator.For(Slot).BoundWindow);
        Assert.HasCount(1, windows.Moves);
    }

    [TestMethod]
    public async Task Reconnect_AmbiguousNamesOrWindows_NeverChoosesFirstMatch()
    {
        var windows = new Windows { Items = [Window(), Window(2)] };
        using var coordinator = Create(windows);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ConnectAsync(Slot, Machine, true));
        windows.Items = [];
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ConnectAsync(Slot, Machine, false));
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task Arrange_LockedFullscreenOrOffMonitor_DoesNotMove()
    {
        var windows = new Windows { Items = [Window()] };
        using var coordinator = Create(windows);
        coordinator.Bind(Slot, Machine, Window().Identity);
        windows.Environment = windows.Environment with { CanInteract = false };
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        windows.Environment = windows.Environment with { CanInteract = true };
        windows.Items = [Window() with { Fullscreen = true }];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        windows.Items = [Window()];
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, new(1800, 0, 500, 400));
        Assert.IsEmpty(windows.Moves);
    }

    [TestMethod]
    public async Task Reconnect_Timeout_StopsWaitingWithoutAutomaticRetries()
    {
        var clock = new Clock();
        var windows = new Windows();
        int launches = 0;
        using var coordinator = Create(windows, _ => launches++, clock);
        await coordinator.ConnectAsync(Slot, Machine, true);
        clock.Now += TimeSpan.FromMinutes(3);
        coordinator.Observe();
        await coordinator.ArrangeAsync(Slot, Machine, true, Cell);
        Assert.IsFalse(coordinator.For(Slot).Connecting);
        Assert.Contains("2 minutes", coordinator.For(Slot).Status);
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    [DataRow(1920, 1080)]
    [DataRow(1919, 1079)]
    [DataRow(3840, 2088)]
    public void FourCellGeometry_CoversOneWorkAreaWithEqualNonOverlappingCells(int width, int height)
    {
        var area = new PixelRect(100, 200, width, height);
        var cells = FourCellGeometry.Divide(area);
        Assert.HasCount(4, cells);
        Assert.IsTrue(cells.All(area.Contains));
        Assert.IsLessThanOrEqualTo(1, cells.Max(c => c.Width) - cells.Min(c => c.Width));
        Assert.IsLessThanOrEqualTo(1, cells.Max(c => c.Height) - cells.Min(c => c.Height));
        Assert.AreEqual(cells[0].Right + 8, cells[1].X);
        Assert.AreEqual(cells[0].Bottom + 8, cells[2].Y);
        Assert.AreEqual(area.Right, cells[3].Right);
        Assert.AreEqual(area.Bottom, cells[3].Bottom);
    }

    [TestMethod]
    [DataRow(3840, 2088)]
    [DataRow(1919, 1079)]
    public void FourCellGeometry_ZeroGap_CoversEntireMonitorWorkArea(int width, int height)
    {
        var area = new PixelRect(100, 200, width, height);
        var cells = FourCellGeometry.Divide(area, gap: 0);
        Assert.HasCount(4, cells);
        Assert.IsTrue(cells.All(area.Contains));
        Assert.AreEqual(cells[0].Right, cells[1].X);
        Assert.AreEqual(cells[0].Bottom, cells[2].Y);
        Assert.AreEqual(area.Right, cells[3].Right);
        Assert.AreEqual(area.Bottom, cells[3].Bottom);
        Assert.AreEqual((long)width * height, cells.Sum(c => (long)c.Width * c.Height));
    }

    [TestMethod]
    public void OuterForVisible_CompensatesNinePixelInvisibleInsets()
    {
        var outer = new PixelRect(1920, 1044, 1920, 1044);
        var visible = new PixelRect(1929, 1044, 1902, 1035);
        Assert.AreEqual(new PixelRect(-9, 0, 1938, 1053),
            FourCellGeometry.OuterForVisible(new(0, 0, 1920, 1044), outer, visible));
        Assert.AreEqual(new PixelRect(1911, 1044, 1938, 1053),
            FourCellGeometry.OuterForVisible(new(1920, 1044, 1920, 1044), outer, visible));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            FourCellGeometry.OuterForVisible(outer, outer, new PixelRect(0, 0, 0, 0)));
    }

    [TestMethod]
    [DataRow(0, 50, 3840, 2088)]
    [DataRow(1, 70, 3840, 2088)]
    [DataRow(2, 80, 1919, 1079)]
    [DataRow(3, 65, 3840, 2088)]
    public void Focus_CoversEntireWorkAreaWithoutOverlapping(int focusedIndex, int percentage, int width, int height)
    {
        var area = new PixelRect(100, 200, width, height);
        var cells = FourCellGeometry.Focus(area, focusedIndex, percentage);
        Assert.HasCount(4, cells);
        Assert.IsTrue(cells.All(c => c.Width > 0 && c.Height > 0 && area.Contains(c)));
        Assert.AreEqual(area.Height, cells[focusedIndex].Height);
        Assert.AreEqual((int)((long)width * percentage / 100), cells[focusedIndex].Width);
        Assert.AreEqual((long)width * height, cells.Sum(c => (long)c.Width * c.Height));
        for (int i = 0; i < cells.Count; i++)
            for (int j = i + 1; j < cells.Count; j++)
                Assert.IsTrue(cells[i].Right <= cells[j].X || cells[j].Right <= cells[i].X ||
                    cells[i].Bottom <= cells[j].Y || cells[j].Bottom <= cells[i].Y);
    }

    [TestMethod]
    public void Focus_RejectsInvalidSlotRatioAndWorkArea()
    {
        var area = new PixelRect(0, 0, 1920, 1080);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FourCellGeometry.Focus(area, 4));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FourCellGeometry.Focus(area, 0, 49));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FourCellGeometry.Focus(area, 0, 81));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FourCellGeometry.Focus(new(0, 0, 199, 1080), 0));
    }
}
