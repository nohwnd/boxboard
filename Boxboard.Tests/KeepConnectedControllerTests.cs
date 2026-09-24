using Bevdox.Models;
using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class KeepConnectedControllerTests
{
    private static readonly Guid Desktop = Guid.Parse("00000000-0000-0000-0000-000000000099");
    private static DevBoxInstance Machine => DemoData.Machines()[0];
    private static SlotAssignment Slot => new(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, Machine.UniqueId);
    private static SessionWindow Client() =>
        new(new(10, 42, 100), Machine.OriginalName, Desktop, new(0, 0, 800, 600), false, false, false);
    private static (SlotAssignment Slot, DevBoxInstance Machine)[] Assignments => [(Slot, Machine)];

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Windows : ISessionWindows
    {
        public BoardEnvironment Environment { get; set; } = new(Desktop, true, true, new(0, 0, 1920, 1080));
        public List<SessionWindow> Items { get; set; } = [];
        public int VisibleWindowCount { get; set; } = 1;
        public int CloseAttempts { get; private set; }
        public bool RejectClose { get; set; }
        public BoardEnvironment GetEnvironment() => Environment;
        public IReadOnlyList<SessionWindow> Enumerate() => Items.ToArray();
        public int CountVisibleTopLevelWindows(WindowIdentity identity) => VisibleWindowCount;
        public Task CloseReconnectPromptAsync(SessionWindow window, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.AreEqual(2, VisibleWindowCount);
            Assert.IsTrue(Items.Any(item => item.Identity == window.Identity));
            CloseAttempts++;
            if (RejectClose)
                throw new InvalidOperationException("Windows App refused to close the prompt.");
            Items.RemoveAll(item => item.Identity == window.Identity);
            VisibleWindowCount = 1;
            return Task.CompletedTask;
        }
        public PixelRect GetVisibleBounds(SessionWindow window) => window.Bounds;
        public Task PlaceAsync(SessionWindow window, PixelRect bounds, CancellationToken ct)
        {
            Items = Items.Select(item => item.Identity == window.Identity ? item with { Bounds = bounds } : item).ToList();
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task MissingClient_ReopensAfterDebounceButNotWhileLaunchPending()
    {
        var windows = new Windows();
        var clock = new Clock();
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        using var keep = new KeepConnectedController(windows, sessions, clock);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: false);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        clock.Now += TimeSpan.FromSeconds(2);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(0, launches);
        clock.Now += TimeSpan.FromSeconds(1);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, launches);
        clock.Now += TimeSpan.FromSeconds(20);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, launches);

        windows.Items = [Client()];
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        sessions.Observe();
        await sessions.ArrangeAsync(Slot, Machine, true, new(20, 20, 500, 400));
        Assert.IsFalse(sessions.For(Slot).Connecting);
        windows.Items = [];
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        clock.Now += TimeSpan.FromSeconds(3);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(2, launches);
    }

    [TestMethod]
    public async Task LockedSession_WaitsUntilUnlockBeforeStartingDebounce()
    {
        var windows = new Windows { Environment = new(Desktop, true, false, new(0, 0, 1920, 1080)) };
        var clock = new Clock();
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        using var keep = new KeepConnectedController(windows, sessions, clock);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        clock.Now += TimeSpan.FromMinutes(10);
        windows.Environment = windows.Environment with { CanInteract = true };
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(0, launches);
        clock.Now += TimeSpan.FromSeconds(3);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task ThreeFailures_PauseUntilExplicitReset()
    {
        var windows = new Windows();
        var clock = new Clock();
        int requests = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => { requests++; throw new HttpRequestException("DevCenter unavailable."); },
            _ => Assert.Fail("Failed request must not launch."), clock);
        using var keep = new KeepConnectedController(windows, sessions, clock);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        foreach (var delay in new[] { 3, 5, 30 })
        {
            clock.Now += TimeSpan.FromSeconds(delay);
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
                keep.TickAsync(Assignments, DemoData.Machines(), enabled: true));
        }
        clock.Now += TimeSpan.FromHours(1);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(3, requests);
        Assert.Contains("paused after three attempts", sessions.For(Slot).Status);
        keep.Reset();
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        clock.Now += TimeSpan.FromSeconds(3);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            keep.TickAsync(Assignments, DemoData.Machines(), enabled: true));
        Assert.AreEqual(4, requests);
    }

    [TestMethod]
    public async Task ShortLivedWindows_DoNotResetThreeAttemptBudget()
    {
        var windows = new Windows();
        var clock = new Clock();
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        using var keep = new KeepConnectedController(windows, sessions, clock);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            clock.Now += TimeSpan.FromSeconds(attempt switch { 1 => 3, 2 => 5, _ => 30 });
            await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
            Assert.AreEqual(attempt, launches);
            windows.Items =
            [
                Client() with { Identity = new(10 + attempt, 42 + attempt, 100 + attempt) }
            ];
            sessions.Observe();
            await sessions.ArrangeAsync(Slot, Machine, true, new(20, 20, 500, 400));
            await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
            windows.Items = [];
            await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        }
        clock.Now += TimeSpan.FromHours(1);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(3, launches);
        Assert.Contains("paused after three attempts", sessions.For(Slot).Status);
    }

    [TestMethod]
    public async Task ChangingAssignedMachine_ResetsTheOldMachinesExhaustedBudget()
    {
        var windows = new Windows();
        var clock = new Clock();
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (machine, _) => BoardSettings.SameId(machine.UniqueId, Machine.UniqueId)
                ? throw new HttpRequestException("First Dev Box is unavailable.")
                : Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        using var keep = new KeepConnectedController(windows, sessions, clock);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        foreach (var seconds in new[] { 3, 5, 30 })
        {
            clock.Now += TimeSpan.FromSeconds(seconds);
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
                keep.TickAsync(Assignments, DemoData.Machines(), enabled: true));
        }
        var replacement = DemoData.Machines()[1];
        var newSlot = Slot with { MachineId = replacement.UniqueId };
        await keep.TickAsync([(newSlot, replacement)], DemoData.Machines(), enabled: true);
        clock.Now += TimeSpan.FromSeconds(3);
        await keep.TickAsync([(newSlot, replacement)], DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task DisablingKeepConnected_CancelsPendingConnectionBeforeLaunch()
    {
        var windows = new Windows();
        var clock = new Clock();
        var response = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        int launches = 0;
        using var sessions = new SessionCoordinator(windows, (_, _) => response.Task, _ => launches++, clock);
        using var keep = new KeepConnectedController(windows, sessions, clock);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        clock.Now += TimeSpan.FromSeconds(3);
        var pending = keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.IsTrue(sessions.For(Slot).Connecting);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: false);
        response.SetResult(new Uri("ms-cloudpc:connect?cpcid=synthetic"));
        await pending;
        Assert.AreEqual(0, launches);
    }

    [TestMethod]
    public async Task ReconnectPrompt_ClosesOldClientBeforeRequestingOneReplacement()
    {
        var windows = new Windows { Items = [Client()], VisibleWindowCount = 2 };
        var clock = new Clock();
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        using var keep = new KeepConnectedController(windows, sessions, clock);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, windows.CloseAttempts);
        Assert.IsEmpty(windows.Items);
        Assert.AreEqual(0, launches);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, launches);
        Assert.IsTrue(sessions.For(Slot).Connecting);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, launches);
        Assert.AreEqual(1, windows.CloseAttempts);
    }

    [TestMethod]
    public async Task ReconnectPrompt_KeepConnectedOffLeavesBothWindowsAlone()
    {
        var windows = new Windows { Items = [Client()], VisibleWindowCount = 2 };
        using var sessions = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("Disabled Keep connected must not request a connection."),
            _ => Assert.Fail("Disabled Keep connected must not launch."));
        using var keep = new KeepConnectedController(windows, sessions);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: false);
        Assert.AreEqual(0, windows.CloseAttempts);
        Assert.HasCount(1, windows.Items);
    }

    [TestMethod]
    public async Task ReconnectPrompt_ReplacesAlreadyRequestedClientWithoutOverlappingLaunches()
    {
        var windows = new Windows();
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++);
        using var keep = new KeepConnectedController(windows, sessions);
        await sessions.ConnectAsync(Slot, Machine, nameIsUnique: true);
        Assert.AreEqual(1, launches);
        windows.Items = [Client()];
        windows.VisibleWindowCount = 2;
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, windows.CloseAttempts);
        Assert.AreEqual(1, launches);
        Assert.IsFalse(sessions.For(Slot).Connecting);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(2, launches);
        Assert.IsTrue(sessions.For(Slot).Connecting);
    }

    [TestMethod]
    public async Task ReconnectPrompt_AmbiguousAndOtherDesktopClientsAreNeverClosed()
    {
        var windows = new Windows { Items = [Client()], VisibleWindowCount = 2 };
        using var sessions = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("Ambiguous client must not connect."),
            _ => Assert.Fail("Ambiguous client must not launch."));
        using var keep = new KeepConnectedController(windows, sessions);
        var duplicate = DemoData.Machines()[1];
        duplicate.OriginalName = Machine.OriginalName;
        await keep.TickAsync(Assignments, [Machine, duplicate], enabled: true);
        Assert.AreEqual(0, windows.CloseAttempts);

        windows.Items = [Client() with { DesktopId = Guid.NewGuid() }];
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(0, windows.CloseAttempts);
    }

    [TestMethod]
    public async Task ReconnectPrompt_ThreeWindowsOrLockedDesktopDoNotTriggerClose()
    {
        var windows = new Windows { Items = [Client()], VisibleWindowCount = 3 };
        using var sessions = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("Unconfirmed prompt must not connect."),
            _ => Assert.Fail("Unconfirmed prompt must not launch."));
        using var keep = new KeepConnectedController(windows, sessions);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(0, windows.CloseAttempts);
        windows.VisibleWindowCount = 2;
        windows.Environment = windows.Environment with { CanInteract = false };
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(0, windows.CloseAttempts);
    }

    [TestMethod]
    public async Task ReconnectPrompt_FailedCloseIsNotRepeatedUntilManualReset()
    {
        var windows = new Windows { Items = [Client()], VisibleWindowCount = 2, RejectClose = true };
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++);
        using var keep = new KeepConnectedController(windows, sessions);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            keep.TickAsync(Assignments, DemoData.Machines(), enabled: true));
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, windows.CloseAttempts);
        Assert.AreEqual(0, launches);
        keep.Reset();
        windows.RejectClose = false;
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(2, windows.CloseAttempts);
        Assert.AreEqual(1, launches);
    }

    [TestMethod]
    public async Task ReconnectPrompt_RepeatedShortLivedClientsRespectThreeRequestBudget()
    {
        var windows = new Windows { Items = [Client()], VisibleWindowCount = 2 };
        var clock = new Clock();
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        using var keep = new KeepConnectedController(windows, sessions, clock);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(1, launches);
        for (int attempt = 2; attempt <= 3; attempt++)
        {
            clock.Now += attempt == 2 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);
            windows.Items = [Client() with { Identity = new(attempt * 10, attempt * 40, attempt * 100) }];
            windows.VisibleWindowCount = 2;
            await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
            await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
            Assert.AreEqual(attempt, launches);
        }
        clock.Now += TimeSpan.FromMinutes(5);
        windows.Items = [Client() with { Identity = new(40, 160, 400) }];
        windows.VisibleWindowCount = 2;
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(3, launches);
        Assert.AreEqual(3, windows.CloseAttempts);
        Assert.HasCount(1, windows.Items);
    }

    [TestMethod]
    public async Task ExistingAssignedClients_SnapOnceThenRespectManualPosition()
    {
        var client = Client();
        var windows = new Windows { Items = [client] };
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("Existing client must not fetch connection info."),
            _ => launches++);
        using var keep = new KeepConnectedController(windows, sessions);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: true);
        Assert.AreEqual(client.Identity, sessions.For(Slot).BoundWindow);
        Assert.AreEqual(0, launches);
        Assert.AreEqual(client.Bounds, windows.Items.Single().Bounds);
        sessions.Observe();
        var assigned = new PixelRect(20, 20, 500, 400);
        await sessions.ArrangeAsync(Slot, Machine, true, assigned);
        Assert.AreEqual(assigned, windows.Items.Single().Bounds);
        var manual = new PixelRect(40, 40, 900, 700);
        windows.Items = [windows.Items[0] with { Bounds = manual }];
        sessions.Observe();
        await sessions.ArrangeAsync(Slot, Machine, true, assigned);
        Assert.AreEqual(manual, windows.Items.Single().Bounds);
        await sessions.ArrangeAsync(Slot, Machine, true, new(100, 100, 500, 400));
        Assert.AreEqual(manual, windows.Items.Single().Bounds);
        sessions.InvalidatePlacements();
        await sessions.ArrangeAsync(Slot, Machine, true, assigned);
        Assert.AreEqual(assigned, windows.Items.Single().Bounds);
    }

    [TestMethod]
    public async Task OptedOutLayout_DoesNotBindExistingClientAutomatically()
    {
        var windows = new Windows { Items = [Client()] };
        using var sessions = new SessionCoordinator(windows,
            (_, _) => throw new AssertFailedException("No request was expected."),
            _ => throw new AssertFailedException("No launch was expected."));
        using var keep = new KeepConnectedController(windows, sessions);
        await keep.TickAsync(Assignments, DemoData.Machines(), enabled: false);
        Assert.IsNull(sessions.For(Slot).BoundWindow);
    }

    [TestMethod]
    public async Task AmbiguousCatalogTitle_PausesWithoutAttributingAnotherClient()
    {
        var windows = new Windows { Items = [Client()] };
        var clock = new Clock();
        int launches = 0;
        using var sessions = new SessionCoordinator(windows,
            (_, _) => Task.FromResult(new Uri("ms-cloudpc:connect?cpcid=synthetic")),
            _ => launches++, clock);
        using var keep = new KeepConnectedController(windows, sessions, clock);
        var duplicate = DemoData.Machines()[1];
        duplicate.OriginalName = Machine.OriginalName;
        await keep.TickAsync(Assignments, [Machine, duplicate], enabled: true);
        Assert.Contains("ambiguous", sessions.For(Slot).Status);
        Assert.AreEqual(0, launches);
    }
}
