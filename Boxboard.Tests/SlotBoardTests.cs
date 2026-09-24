using Bevdox.Models;
using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Boxboard.Tests;

[TestClass]
public sealed class SlotBoardTests
{
    private sealed class MemoryStore : ISettingsStore
    {
        public BoardSettings Saved { get; private set; } = DemoData.Settings();
        public bool FailSave { get; set; }
        public int SaveCount { get; private set; }
        public Task<BoardSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);
        public Task SaveAsync(BoardSettings settings, CancellationToken ct = default)
        {
            if (FailSave)
                throw new IOException("Disk is read-only.");
            Saved = settings;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task EnsureFourCells_MigratesWithoutDeletingExtraAssignmentsOrChangingSlotIds()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var old = board.Settings.Slots.ToArray();
        await board.EnsureFourCellsAsync();
        CollectionAssert.AreEqual(old, board.Settings.Slots.Take(3).ToArray());
        Assert.HasCount(4, board.Settings.Slots);
        await board.AddSlotAsync();
        await board.AssignAsync(board.Settings.Slots[4].Id, DemoData.Machines()[3].UniqueId, _ => true);
        var five = board.Settings.Slots.ToArray();
        await board.EnsureFourCellsAsync();
        CollectionAssert.AreEqual(five, board.Settings.Slots);
        Assert.AreEqual(DemoData.Machines()[3].UniqueId, board.Settings.Slots[4].MachineId);
    }

    [TestMethod]
    public async Task Refresh_ReorderedAndRenamedMachines_RetainsStableSlotIdsAndAssignments()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var slots = board.Settings.Slots.ToArray();
        var machines = DemoData.Machines();
        machines[0].OriginalName = "zzz-renamed";
        machines.Reverse();
        await board.RefreshAsync(_ => Task.FromResult(machines));
        var restarted = new SlotBoard(store);
        await restarted.LoadAsync();

        CollectionAssert.AreEqual(slots, restarted.Settings.Slots);
        Assert.AreEqual("zzz-renamed", restarted.GetMachine(slots[0].MachineId!).EffectiveName);
        Assert.HasCount(3, restarted.Settings.Slots);
        Assert.IsFalse(restarted.DiscoveryVerified);
        Assert.IsTrue(restarted.Options.All(m => !m.Available));
    }

    [TestMethod]
    public async Task Options_AllMachinesVisible_AssignedMarkedAndUnassignedUnmarked()
    {
        var board = new SlotBoard(new MemoryStore());
        await board.LoadAsync();
        await board.RefreshAsync(_ => Task.FromResult(DemoData.Machines()));

        Assert.HasCount(5, board.Options);
        Assert.AreEqual("azdo1 (Assigned to Slot 1)", board.Options.Single(m => m.UniqueId.EndsWith("/azdo1")).Label);
        Assert.AreEqual("azdo2 (Assigned to Slot 2)", board.Options.Single(m => m.UniqueId.EndsWith("/azdo2")).Label);
        Assert.AreEqual("aitestagent", board.Options.Single(m => m.UniqueId.EndsWith("/aitestagent")).Label);
        Assert.AreEqual("offline-box (Assigned to Slot 3) - Unavailable",
            board.Options.Single(m => m.UniqueId.EndsWith("/offline-box")).Label);
    }

    [TestMethod]
    public async Task Assign_ConfirmedMove_ClearsSourceAndReplacesTargetInOneSave()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var source = board.Settings.Slots[0];
        var target = board.Settings.Slots[1];
        MoveRequest? prompt = null;

        var changed = await board.AssignAsync(target.Id, source.MachineId!, request => { prompt = request; return true; });

        Assert.IsTrue(changed);
        Assert.AreEqual(new MoveRequest("azdo1", "Slot 1", "Slot 2", "azdo2"), prompt);
        Assert.IsNull(store.Saved.Slots[0].MachineId);
        Assert.AreEqual(source.MachineId, store.Saved.Slots[1].MachineId);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual("azdo1 (Assigned to Slot 2)", board.Options.Single(m => m.UniqueId == source.MachineId).Label);
        Assert.AreEqual("azdo2", board.Options.Single(m => m.UniqueId == target.MachineId).Label);
    }

    [TestMethod]
    public async Task Assign_CancelledMove_LeavesBothAssignmentsAndFileUnchanged()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var before = store.Saved;

        var changed = await board.AssignAsync(before.Slots[1].Id, before.Slots[0].MachineId!, _ => false);

        Assert.IsFalse(changed);
        Assert.AreSame(before, board.Settings);
        Assert.AreSame(before, store.Saved);
        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    public async Task Assign_CurrentMachine_DoesNotPromptOrSave()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var slot = board.Settings.Slots[0];
        var changed = await board.AssignAsync(slot.Id, slot.MachineId!.ToUpperInvariant(), _ =>
            throw new AssertFailedException("Reselecting the current machine must not ask to move."));

        Assert.IsFalse(changed);
        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    public async Task ClearRemoveAdd_PreservesOtherSlotNumbersAndHasNoFixedLimit()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var third = board.Settings.Slots[2];

        await board.ClearSlotAsync(board.Settings.Slots[0].Id);
        Assert.IsNull(board.Settings.Slots[0].MachineId);
        await board.RemoveSlotAsync(board.Settings.Slots[1].Id);
        for (int i = 0; i < 12; i++)
            await board.AddSlotAsync();

        Assert.AreEqual(third, board.Settings.Slots[1]);
        Assert.HasCount(14, board.Settings.Slots);
        Assert.AreEqual(15, board.Settings.Slots[^1].Number);
        Assert.AreEqual(14, board.Settings.Slots.Select(s => s.Id).Distinct().Count());
        Assert.AreEqual("azdo2", board.Options.Single(m => m.UniqueId.EndsWith("/azdo2")).Label);
    }

    [TestMethod]
    public async Task Refresh_MissingThenReturningMachine_PreservesAssignmentAndRestoresAvailability()
    {
        var board = new SlotBoard(new MemoryStore());
        await board.LoadAsync();
        var slot = board.Settings.Slots[0];
        await board.RefreshAsync(_ => Task.FromResult(new List<DevBoxInstance>()));
        Assert.AreEqual(slot, board.Settings.Slots[0]);
        Assert.AreEqual("azdo1 (Assigned to Slot 1) - Unavailable", board.Options.Single(m => m.UniqueId == slot.MachineId).Label);
        Assert.IsFalse(board.Options.Single(m => m.UniqueId == slot.MachineId).Available);

        await board.RefreshAsync(_ => Task.FromResult(DemoData.Machines()));
        Assert.IsTrue(board.Options.Single(m => m.UniqueId == slot.MachineId).Available);
        Assert.AreEqual(slot, board.Settings.Slots[0]);
    }

    [TestMethod]
    public async Task Refresh_Failure_DoesNotSaveOrEraseAssignmentsAndMakesStalenessExplicit()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        await board.RefreshAsync(_ => Task.FromResult(DemoData.Machines()));
        var before = store.Saved;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => board.RefreshAsync(_ =>
            throw new HttpRequestException("Project tenant access denied.")));

        Assert.AreSame(before, board.Settings);
        Assert.AreSame(before, store.Saved);
        Assert.AreEqual(1, store.SaveCount);
        Assert.AreEqual("Project tenant access denied.", board.RefreshError);
        Assert.IsTrue(board.Options.All(m => !m.Available));
        Assert.IsTrue(board.Options.All(m => m.Details.Contains("refresh failed")));
    }

    [TestMethod]
    public async Task Assign_SaveFailure_RollsBackBothAssignments()
    {
        var store = new MemoryStore { FailSave = true };
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var before = store.Saved;
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            board.AssignAsync(before.Slots[1].Id, before.Slots[0].MachineId!, _ => true));
        Assert.AreSame(before, board.Settings);
        Assert.AreSame(before, store.Saved);
    }

    [TestMethod]
    public async Task Refresh_DuplicateIdentity_RejectsAmbiguousDiscoveryWithoutSaving()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var before = store.Saved;
        var machine = DemoData.Machines()[0];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            board.RefreshAsync(_ => Task.FromResult(new List<DevBoxInstance> { machine, machine })));
        Assert.AreSame(before, store.Saved);
    }

    [TestMethod]
    public async Task RemoveAllSlots_RestartDoesNotInventNewSlots()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        foreach (var slot in board.Settings.Slots.ToArray())
            await board.RemoveSlotAsync(slot.Id);
        var restarted = new SlotBoard(store);
        await restarted.LoadAsync();
        Assert.IsEmpty(restarted.Settings.Slots);
        await restarted.AddSlotAsync();
        Assert.AreEqual(4, restarted.Settings.Slots.Single().Number);
    }

    [TestMethod]
    public async Task SelectDesktop_RetainsLegacySlotsAndPersistsIndependentSecondLayout()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var oldSlots = board.Settings.Slots.ToArray();
        var firstDesktop = Guid.NewGuid();
        var secondDesktop = Guid.NewGuid();

        await board.SelectDesktopAsync(firstDesktop, "Desktop 3");
        CollectionAssert.AreEqual(oldSlots, board.CurrentSlots.ToArray());
        Assert.AreEqual(3, board.Settings.Version);
        Assert.AreEqual(firstDesktop, board.Settings.PrimaryDesktopId);
        Assert.AreEqual("azdo1 (Assigned to Desktop 3 / Slot 1)",
            board.Options.Single(option => option.UniqueId.EndsWith("/azdo1")).Label);

        await board.SelectDesktopAsync(secondDesktop, "Desktop 2");
        Assert.HasCount(4, board.CurrentSlots);
        Assert.IsTrue(board.CurrentSlots.All(slot => slot.MachineId is null));
        Assert.HasCount(2, board.Layouts);
        await board.AssignAsync(board.CurrentSlots[0].Id, DemoData.Machines()[3].UniqueId, _ => true);
        Assert.AreEqual("aitestagent (Assigned to Desktop 2 / Slot 1)",
            board.Options.Single(option => option.UniqueId.EndsWith("/aitestagent")).Label);
        CollectionAssert.AreEqual(oldSlots, board.Settings.Slots);

        var reopened = new SlotBoard(store);
        await reopened.LoadAsync();
        Assert.AreEqual(firstDesktop, reopened.SelectedDesktopId);
        CollectionAssert.AreEqual(oldSlots, reopened.CurrentSlots.ToArray());
        await reopened.SelectDesktopAsync(secondDesktop, "ignored existing name");
        Assert.AreEqual("Desktop 2", reopened.Layouts[1].Name);
        Assert.AreEqual(DemoData.Machines()[3].UniqueId, reopened.CurrentSlots[0].MachineId);
        Assert.AreEqual(3, store.SaveCount);
    }

    [TestMethod]
    public async Task AssignAcrossDesktops_ConfirmedMoveClearsSourceInSameSave()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        await board.SelectDesktopAsync(Guid.NewGuid(), "Desktop 3");
        await board.SelectDesktopAsync(Guid.NewGuid(), "Desktop 2");
        var source = board.Settings.Slots[0];
        var target = board.CurrentSlots[0];
        var beforeSave = store.SaveCount;
        MoveRequest? prompt = null;

        var moved = await board.AssignAsync(target.Id, source.MachineId!,
            request => { prompt = request; return true; });
        Assert.IsTrue(moved);
        Assert.AreEqual("Desktop 3 / Slot 1", prompt?.SourceSlot);
        Assert.AreEqual("Desktop 2 / Slot 1", prompt?.TargetSlot);
        Assert.IsNull(board.Settings.Slots[0].MachineId);
        Assert.AreEqual(source.MachineId, board.CurrentSlots[0].MachineId);
        Assert.AreEqual(beforeSave + 1, store.SaveCount);
        await board.RefreshAsync(_ => Task.FromResult(new List<DevBoxInstance>()));
        Assert.Contains("Desktop 2 / Slot 1",
            board.Options.Single(option => option.UniqueId == source.MachineId).Label);
    }

    [TestMethod]
    public async Task SelectDesktop_SaveFailureLeavesSelectedLayoutAndSettingsUnchanged()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var firstDesktop = Guid.NewGuid();
        await board.SelectDesktopAsync(firstDesktop, "Desktop 3");
        var before = store.Saved;
        store.FailSave = true;

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            board.SelectDesktopAsync(Guid.NewGuid(), "Desktop 2"));
        Assert.AreSame(before, board.Settings);
        Assert.AreEqual(firstDesktop, board.SelectedDesktopId);
        Assert.IsEmpty(board.Settings.DesktopLayouts);
    }

    [TestMethod]
    public async Task KeepConnected_DefaultOnAndOptOutSavedPerDesktop()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await board.SelectDesktopAsync(first, "Desktop 1");
        Assert.IsTrue(board.KeepConnected);
        Assert.AreEqual(3, board.Settings.Version);
        await board.SelectDesktopAsync(second, "Desktop 2");
        Assert.IsTrue(board.KeepConnected);
        await board.SetKeepConnectedAsync(false);
        Assert.IsFalse(board.KeepConnected);
        await board.SelectDesktopAsync(first, "Desktop 1");
        Assert.IsTrue(board.KeepConnected);
        await board.SetKeepConnectedAsync(false);
        Assert.IsFalse(board.KeepConnected);
        Assert.IsFalse(board.IsKeepConnected(second));

        var restarted = new SlotBoard(store);
        await restarted.LoadAsync();
        Assert.AreEqual(first, restarted.SelectedDesktopId);
        Assert.IsFalse(restarted.KeepConnected);
        await restarted.SelectDesktopAsync(second, "Desktop 2");
        Assert.IsFalse(restarted.KeepConnected);
        await restarted.SetKeepConnectedAsync(true);
        Assert.IsTrue(restarted.KeepConnected);
        Assert.IsFalse(restarted.IsKeepConnected(first));
    }

    [TestMethod]
    public async Task KeepConnected_FailedSaveDoesNotDisableAutomaticLaunch()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        await board.SelectDesktopAsync(Guid.NewGuid(), "Desktop 1");
        var before = board.Settings;
        store.FailSave = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => board.SetKeepConnectedAsync(false));
        Assert.AreSame(before, board.Settings);
        Assert.IsTrue(board.KeepConnected);
    }

    [TestMethod]
    public async Task LayoutMode_UnassignsDisappearingSlotsWithoutDeletingTheirIdentities()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        await board.EnsureFourCellsAsync();
        var original = board.Settings.Slots.ToArray();
        await board.SelectDesktopAsync(Guid.NewGuid(), "Desktop 3");
        await board.SetLayoutModeAsync(WindowLayoutMode.SideBySide);
        Assert.AreEqual(4, board.Settings.Version);
        Assert.AreEqual(WindowLayoutMode.SideBySide, board.LayoutMode);
        Assert.HasCount(2, board.VisibleSlots);
        CollectionAssert.AreEqual(original.Take(2).ToArray(), board.Settings.Slots.Take(2).ToArray());
        Assert.AreEqual(original[2] with { MachineId = null }, board.Settings.Slots[2]);
        Assert.AreEqual("offline-box", board.Options.Single(option => option.UniqueId == original[2].MachineId).Label);
        Assert.IsTrue(board.KeepConnected);

        var reopened = new SlotBoard(store);
        await reopened.LoadAsync();
        Assert.AreEqual(WindowLayoutMode.SideBySide, reopened.LayoutMode);
        Assert.HasCount(2, reopened.VisibleSlots);
        await reopened.SetLayoutModeAsync(WindowLayoutMode.LargeLeftTwoStackedRight);
        Assert.HasCount(3, reopened.VisibleSlots);
        await reopened.SetLayoutModeAsync(WindowLayoutMode.Quadrants);
        Assert.HasCount(4, reopened.VisibleSlots);
        CollectionAssert.AreEqual(board.Settings.Slots.ToArray(), reopened.CurrentSlots.ToArray());
        Assert.IsNull(reopened.CurrentSlots[2].MachineId);
    }

    [TestMethod]
    public async Task CardActions_UpdateTargetDesktopWithoutChangingSelectedDesktopOrSettingsVersion()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        await board.EnsureFourCellsAsync();
        var primary = Guid.NewGuid();
        var secondary = Guid.NewGuid();
        await board.SelectDesktopAsync(primary, "Desktop 2");
        await board.SelectDesktopAsync(secondary, "Desktop 3");
        await board.SetLayoutModeAsync(secondary, WindowLayoutMode.SideBySide);
        await board.SelectDesktopAsync(primary, "Desktop 2");

        await board.SetKeepConnectedAsync(secondary, false);
        var unassigned = DemoData.Machines()[3];
        var slot = board.GetSlots(secondary)[0];
        Assert.IsTrue(await board.AssignAsync(secondary, slot.Id, unassigned.UniqueId, _ => true));
        Assert.AreEqual(primary, board.SelectedDesktopId);
        Assert.AreEqual(WindowLayoutMode.Quadrants, board.LayoutMode);
        Assert.AreEqual(4, board.Settings.Version);
        Assert.IsFalse(board.IsKeepConnected(secondary));
        Assert.AreEqual(unassigned.UniqueId, board.GetVisibleSlots(secondary)[0].MachineId);
        Assert.IsTrue(board.CurrentSlots.All(s => !BoardSettings.SameId(s.MachineId, unassigned.UniqueId)));

        await board.ClearSlotAsync(secondary, slot.Id);
        Assert.IsNull(board.GetVisibleSlots(secondary)[0].MachineId);
        Assert.AreEqual(primary, board.SelectedDesktopId);
        Assert.AreEqual(4, store.Saved.Version);
    }

    [TestMethod]
    public async Task CrossDesktopDrop_RequiresConfirmationAndPersistsTheMoveWithoutDowngradingSettings()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        await board.EnsureFourCellsAsync();
        var primary = Guid.NewGuid();
        var secondary = Guid.NewGuid();
        var later = Guid.NewGuid();
        await board.SelectDesktopAsync(primary, "Desktop 1");
        await board.SelectDesktopAsync(secondary, "Desktop 2");
        await board.SetLayoutModeAsync(secondary, WindowLayoutMode.SideBySide);
        await board.SelectDesktopAsync(primary, "Desktop 1");

        var source = board.GetSlots(primary)[0];
        var target = board.GetVisibleSlots(secondary)[0];
        var original = store.Saved;
        MoveRequest? request = null;
        Assert.IsFalse(await board.AssignAsync(secondary, target.Id, source.MachineId!, move =>
        {
            request = move;
            return false;
        }));
        Assert.IsNotNull(request);
        Assert.Contains("Desktop 1 / Slot 1", request.SourceSlot);
        Assert.Contains("Desktop 2 / Slot 1", request.TargetSlot);
        Assert.AreSame(original, store.Saved);

        Assert.IsTrue(await board.AssignAsync(secondary, target.Id, source.MachineId!, _ => true));
        Assert.IsNull(board.GetSlots(primary)[0].MachineId);
        Assert.AreEqual(source.MachineId, board.GetVisibleSlots(secondary)[0].MachineId);
        Assert.AreEqual(primary, board.SelectedDesktopId);
        await board.SelectDesktopAsync(later, "Desktop 3");
        Assert.AreEqual(4, store.Saved.Version);

        var restarted = new SlotBoard(store);
        await restarted.LoadAsync();
        Assert.AreEqual(WindowLayoutMode.SideBySide, restarted.GetLayoutMode(secondary));
        Assert.AreEqual(source.MachineId, restarted.GetVisibleSlots(secondary)[0].MachineId);
        Assert.IsNull(restarted.GetSlots(primary)[0].MachineId);
    }

    [TestMethod]
    public async Task SingleWindow_UnassignsOnlyDisappearingSlotsAndPreservesPreviouslyHiddenOnes()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        await board.EnsureFourCellsAsync();
        await board.AddSlotAsync();
        var desktop = Guid.NewGuid();
        await board.SelectDesktopAsync(desktop, "Desktop 2");
        await board.AssignAsync(board.GetSlots(desktop)[3].Id, DemoData.Machines()[2].UniqueId, _ => true);
        await board.AssignAsync(board.GetSlots(desktop)[4].Id, DemoData.Machines()[3].UniqueId, _ => true);
        var saved = board.GetSlots(desktop).ToArray();

        await board.SetLayoutModeAsync(desktop, WindowLayoutMode.SingleWindow);
        Assert.HasCount(1, board.GetVisibleSlots(desktop));
        Assert.AreEqual(saved[0], board.GetVisibleSlots(desktop)[0]);
        for (int index = 1; index < 4; index++)
        {
            Assert.AreEqual(saved[index].Id, board.GetSlots(desktop)[index].Id);
            Assert.IsNull(board.GetSlots(desktop)[index].MachineId);
        }
        Assert.AreEqual(saved[4], board.GetSlots(desktop)[4]);
        Assert.AreEqual("azdo2", board.Options.Single(option => option.UniqueId == saved[1].MachineId).Label);
        Assert.AreEqual(4, store.Saved.Version);

        var reopened = new SlotBoard(store);
        await reopened.LoadAsync();
        Assert.AreEqual(WindowLayoutMode.SingleWindow, reopened.GetLayoutMode(desktop));
        Assert.HasCount(1, reopened.GetVisibleSlots(desktop));
        await reopened.SetLayoutModeAsync(desktop, WindowLayoutMode.Quadrants);
        Assert.IsTrue(reopened.GetVisibleSlots(desktop).Skip(1).All(slot => slot.MachineId is null));
        Assert.AreEqual(saved[4], reopened.GetSlots(desktop)[4]);
    }

    [TestMethod]
    public async Task ShrinkingOtherDesktop_LeavesPrimaryAssignmentsAndFailedSavesUntouched()
    {
        var store = new MemoryStore();
        var board = new SlotBoard(store);
        await board.LoadAsync();
        await board.EnsureFourCellsAsync();
        var primary = Guid.NewGuid();
        var secondary = Guid.NewGuid();
        await board.SelectDesktopAsync(primary, "Desktop 1");
        await board.SelectDesktopAsync(secondary, "Desktop 2");
        await board.AssignAsync(secondary, board.GetSlots(secondary)[2].Id, DemoData.Machines()[2].UniqueId, _ => true);
        await board.AssignAsync(secondary, board.GetSlots(secondary)[3].Id, DemoData.Machines()[3].UniqueId, _ => true);
        await board.SelectDesktopAsync(primary, "Desktop 1");
        var primarySlots = board.GetSlots(primary).ToArray();
        var before = board.Settings;

        store.FailSave = true;
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            board.SetLayoutModeAsync(secondary, WindowLayoutMode.SideBySide));
        Assert.AreSame(before, board.Settings);
        store.FailSave = false;

        await board.SetLayoutModeAsync(secondary, WindowLayoutMode.SideBySide);
        Assert.AreEqual(primary, board.SelectedDesktopId);
        CollectionAssert.AreEqual(primarySlots, board.GetSlots(primary).ToArray());
        Assert.IsTrue(board.GetSlots(secondary).Skip(2).All(slot => slot.MachineId is null));
        var restarted = new SlotBoard(store);
        await restarted.LoadAsync();
        Assert.AreEqual(WindowLayoutMode.SideBySide, restarted.GetLayoutMode(secondary));
        Assert.IsTrue(restarted.GetSlots(secondary).Skip(2).All(slot => slot.MachineId is null));
    }
}
