using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class MonitorPinningTests
{
    private const string First = @"\\.\DISPLAY1";
    private const string Second = @"\\.\DISPLAY2";

    private sealed class MemoryStore(BoardSettings settings) : ISettingsStore
    {
        private BoardSettings _settings = settings;
        public Task<BoardSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(_settings);
        public Task SaveAsync(BoardSettings settings, CancellationToken ct = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private static async Task<SlotBoard> LoadAsync()
    {
        var board = new SlotBoard(new MemoryStore(DemoData.Settings()));
        await board.LoadAsync();
        await board.EnsureFourCellsAsync();
        return board;
    }

    [TestMethod]
    public async Task OneDesktop_KeepsSeparateSlotsPerMonitor()
    {
        var board = await LoadAsync();
        var desktop = Guid.NewGuid();
        await board.SelectDesktopAsync(new LayoutKey(desktop, First), "Desktop 1", 1);
        await board.EnsureFourCellsAsync();
        await board.SelectDesktopAsync(new LayoutKey(desktop, Second), "Desktop 1", 2);
        await board.EnsureFourCellsAsync();

        var onFirst = board.GetVisibleSlots(new LayoutKey(desktop, First));
        var onSecond = board.GetVisibleSlots(new LayoutKey(desktop, Second));
        Assert.HasCount(4, onFirst);
        Assert.HasCount(4, onSecond);
        Assert.IsEmpty(onFirst.Select(slot => slot.Id).Intersect(onSecond.Select(slot => slot.Id)).ToList());

        var machine = DemoData.Machines()[3].UniqueId;
        var beforeOnFirst = board.GetSlots(new LayoutKey(desktop, First)).Select(slot => slot.MachineId).ToList();
        Assert.IsTrue(await board.AssignAsync(new LayoutKey(desktop, Second), onSecond[0].Id, machine, _ => true));
        Assert.AreEqual(machine, board.GetSlots(new LayoutKey(desktop, Second))[0].MachineId);
        CollectionAssert.AreEqual(beforeOnFirst,
            board.GetSlots(new LayoutKey(desktop, First)).Select(slot => slot.MachineId).ToList());
        board.Settings.Validate();
    }

    [TestMethod]
    public async Task LayoutModeAndKeepConnected_ApplyToOneMonitorOnly()
    {
        var board = await LoadAsync();
        var desktop = Guid.NewGuid();
        var first = new LayoutKey(desktop, First);
        var second = new LayoutKey(desktop, Second);
        await board.SelectDesktopAsync(first, "Desktop 1", 1);
        await board.EnsureFourCellsAsync();
        await board.SelectDesktopAsync(second, "Desktop 1", 2);
        await board.EnsureFourCellsAsync();

        await board.SetLayoutModeAsync(second, WindowLayoutMode.SideBySide);
        await board.SetKeepConnectedAsync(second, false);

        Assert.AreEqual(WindowLayoutMode.SideBySide, board.GetLayoutMode(second));
        Assert.AreEqual(WindowLayoutMode.Quadrants, board.GetLayoutMode(first));
        Assert.IsFalse(board.IsKeepConnected(second));
        Assert.IsTrue(board.IsKeepConnected(first));
    }

    [TestMethod]
    public async Task PinLayout_KeepsAssignmentsOfLayoutsSavedBeforeMonitorPinning()
    {
        var board = await LoadAsync();
        var desktop = Guid.NewGuid();
        await board.SelectDesktopAsync(desktop, "Desktop 1");
        var assigned = board.GetSlots(desktop).Select(slot => slot.MachineId).ToList();
        Assert.IsLessThan(5, board.Settings.Version);

        await board.PinLayoutAsync(desktop, Second, 2);

        var pinned = new LayoutKey(desktop, Second);
        Assert.IsTrue(board.HasLayout(pinned));
        Assert.IsFalse(board.HasLayout(desktop));
        Assert.AreEqual(pinned, board.SelectedKey);
        Assert.AreEqual(5, board.Settings.Version);
        CollectionAssert.AreEqual(assigned, board.GetSlots(pinned).Select(slot => slot.MachineId).ToList());
        Assert.AreEqual(2, board.Layouts.Single(layout => layout.Key == pinned).MonitorNumber);
    }

    [TestMethod]
    public async Task PhysicalMonitorMigration_KeepsAssignedSlotsWhenTheInstancePathChanged()
    {
        var board = await LoadAsync();
        var desktop = Guid.NewGuid();
        var previous = new LayoutKey(desktop, "previous-instance");
        var currentInstance = new LayoutKey(desktop, "current-instance");
        var stable = new LayoutKey(desktop, "edid:hardware-serial");
        await board.SelectDesktopAsync(previous, "Desktop 1", 1);
        await board.EnsureFourCellsAsync();
        var assigned = board.GetSlots(previous).ToArray();
        await board.SelectDesktopAsync(currentInstance, "Desktop 1", 1);
        await board.EnsureFourCellsAsync();
        await board.SelectDesktopAsync(previous, "Desktop 1", 1);
        var monitor = new MonitorInfo(stable.MonitorId!, 1, new(0, 0, 1920, 1080),
            new(0, 0, 1920, 1040), true);

        var result = await MonitorPinMigration.MigrateAsync(board, [monitor],
            path => path == previous.MonitorId ? stable.MonitorId : null);

        Assert.AreEqual(1, result.Migrated);
        Assert.IsEmpty(result.Conflicts);
        Assert.IsTrue(board.HasLayout(stable));
        Assert.IsFalse(board.HasLayout(previous));
        Assert.IsTrue(board.HasLayout(currentInstance));
        Assert.AreEqual(stable, board.SelectedKey);
        CollectionAssert.AreEqual(assigned, board.GetSlots(stable).ToArray());
        board.Settings.Validate();
    }

    [TestMethod]
    public async Task PhysicalMonitorMigration_ReportsConflictingLayoutWithoutLosingAssignments()
    {
        var board = await LoadAsync();
        var desktop = Guid.NewGuid();
        var previous = new LayoutKey(desktop, "previous-instance");
        var stable = new LayoutKey(desktop, "edid:hardware-serial");
        await board.SelectDesktopAsync(previous, "Desktop 1", 1);
        var assigned = board.GetSlots(previous).ToArray();
        await board.SelectDesktopAsync(stable, "Desktop 1", 1);
        var monitor = new MonitorInfo(stable.MonitorId!, 1, new(0, 0, 1920, 1080),
            new(0, 0, 1920, 1040), true);

        var result = await MonitorPinMigration.MigrateAsync(board, [monitor],
            path => path == previous.MonitorId ? stable.MonitorId : null);

        Assert.AreEqual(0, result.Migrated);
        CollectionAssert.AreEqual(new[] { previous }, result.Conflicts.ToArray());
        CollectionAssert.AreEqual(assigned, board.GetSlots(previous).ToArray());
        Assert.IsTrue(board.HasLayout(stable));
        board.Settings.Validate();
    }

    [TestMethod]
    public async Task PinLayout_DoesNotChangeAnotherDesktopsAssignments()
    {
        var board = await LoadAsync();
        var firstDesktop = Guid.NewGuid();
        var secondDesktop = Guid.NewGuid();
        await board.SelectDesktopAsync(firstDesktop, "Desktop 1");
        var firstSlots = board.GetSlots(firstDesktop).ToArray();
        await board.SelectDesktopAsync(secondDesktop, "Desktop 2");
        await board.EnsureFourCellsAsync();
        var secondSlots = board.GetSlots(secondDesktop).ToArray();

        await board.PinLayoutAsync(firstDesktop, First, 1);
        await board.PinLayoutAsync(secondDesktop, Second, 2);

        CollectionAssert.AreEqual(firstSlots, board.GetSlots(new LayoutKey(firstDesktop, First)).ToArray());
        CollectionAssert.AreEqual(secondSlots, board.GetSlots(new LayoutKey(secondDesktop, Second)).ToArray());
        Assert.IsFalse(board.HasLayout(firstDesktop));
        Assert.IsFalse(board.HasLayout(secondDesktop));
        board.Settings.Validate();
    }

    [TestMethod]
    public async Task PinLayout_RejectsAnAlreadyPinnedLayout()
    {
        var board = await LoadAsync();
        var desktop = Guid.NewGuid();
        await board.SelectDesktopAsync(new LayoutKey(desktop, First), "Desktop 1", 1);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            board.PinLayoutAsync(new LayoutKey(desktop, First), Second, 2));
    }

    [TestMethod]
    public async Task SelectDesktop_RequiresTheMonitorNumberAndIdentityTogether()
    {
        var board = await LoadAsync();
        var desktop = Guid.NewGuid();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            board.SelectDesktopAsync(new LayoutKey(desktop, First), "Desktop 1"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            board.SelectDesktopAsync(new LayoutKey(desktop), "Desktop 1", 1));
    }

    [TestMethod]
    public void Validate_RejectsTwoLayoutsForTheSameDesktopAndMonitor()
    {
        var desktop = Guid.NewGuid();
        var settings = Pinned(desktop, First, 1) with { Version = 5 };
        settings.DesktopLayouts.Add(Layout(desktop, Second, 2, 10));
        settings.Validate();

        settings.DesktopLayouts.Add(Layout(desktop, Second, 2, 20));
        Assert.ThrowsExactly<InvalidDataException>(settings.Validate);
    }

    [TestMethod]
    public void Validate_RejectsAMonitorPinStoredUnderAnOlderSettingsVersion()
    {
        var settings = Pinned(Guid.NewGuid(), First, 1) with { Version = 4 };
        Assert.ThrowsExactly<InvalidDataException>(settings.Validate);
    }

    [TestMethod]
    public void Validate_RejectsAHalfWrittenMonitorPin()
    {
        var desktop = Guid.NewGuid();
        Assert.ThrowsExactly<InvalidDataException>(
            (Pinned(desktop, First, 1) with { Version = 5, PrimaryMonitorNumber = 0 }).Validate);
        Assert.ThrowsExactly<InvalidDataException>(
            (Pinned(desktop, First, 1) with { Version = 5, PrimaryMonitorId = null }).Validate);
    }

    [TestMethod]
    public void PrimaryKey_StaysDistinctFromTheSameDesktopOnAnotherMonitor()
    {
        var desktop = Guid.NewGuid();
        var settings = Pinned(desktop, First, 1) with { Version = 5 };
        Assert.AreEqual(new LayoutKey(desktop, First), settings.PrimaryKey);
        Assert.AreNotEqual(new LayoutKey(desktop, Second), settings.PrimaryKey);
    }

    private static BoardSettings Pinned(Guid desktop, string monitorId, int monitorNumber) =>
        DemoData.Settings() with
        {
            Version = 5,
            PrimaryDesktopId = desktop,
            PrimaryDesktopName = "Desktop 1",
            PrimaryMonitorId = monitorId,
            PrimaryMonitorNumber = monitorNumber
        };

    private static DesktopLayout Layout(Guid desktop, string monitorId, int monitorNumber, int firstSlotNumber) =>
        new(desktop, "Desktop 1", firstSlotNumber + 4,
            [.. Enumerable.Range(firstSlotNumber, 4).Select(number => new SlotAssignment(Guid.NewGuid(), number))])
        {
            MonitorId = monitorId,
            MonitorNumber = monitorNumber
        };
}
