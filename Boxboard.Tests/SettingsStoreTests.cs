using System.Text.Json;
using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Boxboard-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task SaveLoad_ActualFileRoundtrip_PreservesAssignmentsAndOrderAcrossRestart()
    {
        var expected = DemoData.Settings();
        using (var store = new SettingsStore(SettingsPath))
        {
            await store.SaveAsync(expected);
            var board = new SlotBoard(store);
            await board.LoadAsync();
            await board.AssignAsync(expected.Slots[1].Id, expected.Slots[0].MachineId!, _ => true);
            expected = board.Settings;
        }
        using var reopened = new SettingsStore(SettingsPath);
        var actual = await reopened.LoadAsync();
        CollectionAssert.AreEqual(expected.Slots, actual.Slots);
        Assert.AreEqual(expected.NextSlotNumber, actual.NextSlotNumber);
        Assert.AreEqual(expected.Machines[0].UniqueId, actual.Machines[0].UniqueId);
        Assert.IsFalse(File.Exists(SettingsPath + ".tmp"));
    }

    [TestMethod]
    public async Task StartWithWindows_NewPreferencePersistsWithoutEnablingItForOldSettings()
    {
        using var store = new SettingsStore(SettingsPath);
        await store.SaveAsync(DemoData.Settings());
        var board = new SlotBoard(store);
        await board.LoadAsync();
        Assert.IsFalse(board.Settings.StartWithWindows);

        await board.SetStartWithWindowsAsync(true);
        Assert.IsTrue((await store.LoadAsync()).StartWithWindows);
        await board.SetStartWithWindowsAsync(false);
        Assert.IsFalse((await store.LoadAsync()).StartWithWindows);
    }

    [TestMethod]
    [DataRow("{broken")]
    [DataRow("null")]
    [DataRow("{\"version\":99}")]
    [DataRow("{\"slots\":null}")]
    [DataRow("{\"machines\":null}")]
    public async Task Load_InvalidSettings_ThrowsWithoutOverwriting(string json)
    {
        using var store = new SettingsStore(SettingsPath);
        await File.WriteAllTextAsync(SettingsPath, json);
        await Assert.ThrowsAsync<Exception>(() => store.LoadAsync());
        Assert.AreEqual(json, await File.ReadAllTextAsync(SettingsPath));
    }

    [TestMethod]
    public async Task Save_CancelledWrite_PreservesPreviousFile()
    {
        using var store = new SettingsStore(SettingsPath);
        var settings = DemoData.Settings();
        await store.SaveAsync(settings);
        var before = await File.ReadAllTextAsync(SettingsPath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(settings with { Slots = [] }, cts.Token));
        Assert.AreEqual(before, await File.ReadAllTextAsync(SettingsPath));
        Assert.IsFalse(File.Exists(SettingsPath + ".tmp"));
    }

    [TestMethod]
    public void Open_SecondWriter_RejectsConcurrentLayoutOverwrite()
    {
        using var store = new SettingsStore(SettingsPath);
        Assert.ThrowsExactly<IOException>(() => { using var second = new SettingsStore(SettingsPath); });
    }

    [TestMethod]
    public async Task Save_DuplicateAssignment_RejectsWithoutChangingFile()
    {
        using var store = new SettingsStore(SettingsPath);
        var settings = DemoData.Settings();
        await store.SaveAsync(settings);
        var before = await File.ReadAllTextAsync(SettingsPath);
        var invalid = settings with
        {
            Slots = [settings.Slots[0], settings.Slots[1] with { MachineId = settings.Slots[0].MachineId }]
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(invalid));
        Assert.AreEqual(before, await File.ReadAllTextAsync(SettingsPath));
    }

    [TestMethod]
    public async Task Save_DuplicateAssignmentAcrossDesktops_RejectsWithoutChangingFile()
    {
        using var store = new SettingsStore(SettingsPath);
        var settings = DemoData.Settings();
        await store.SaveAsync(settings);
        var before = await File.ReadAllTextAsync(SettingsPath);
        var invalid = settings with
        {
            Version = 2,
            PrimaryDesktopId = Guid.NewGuid(),
            DesktopLayouts =
            [
                new(Guid.NewGuid(), "Desktop 2", 2,
                    [new(Guid.NewGuid(), 1, settings.Slots[0].MachineId)])
            ]
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(invalid));
        Assert.AreEqual(before, await File.ReadAllTextAsync(SettingsPath));
    }

    [TestMethod]
    public async Task Save_LegacyVersionWithDesktopLayout_RejectsInsteadOfLosingLayouts()
    {
        using var store = new SettingsStore(SettingsPath);
        var legacy = DemoData.Settings();
        await store.SaveAsync(legacy);
        var before = await File.ReadAllTextAsync(SettingsPath);
        var invalid = legacy with { PrimaryDesktopId = Guid.NewGuid() };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(invalid));
        Assert.AreEqual(before, await File.ReadAllTextAsync(SettingsPath));
    }

    [TestMethod]
    public async Task Save_VersionTwoWithDisabledKeepConnected_RejectsInsteadOfLosingOptOut()
    {
        using var store = new SettingsStore(SettingsPath);
        var previous = DemoData.Settings() with { Version = 2, PrimaryDesktopId = Guid.NewGuid() };
        await store.SaveAsync(previous);
        var before = await File.ReadAllTextAsync(SettingsPath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.SaveAsync(previous with { PrimaryKeepConnected = false }));
        Assert.AreEqual(before, await File.ReadAllTextAsync(SettingsPath));
    }

    [TestMethod]
    public async Task SaveLoad_DesktopLayouts_RetainDistinctAssignmentsAcrossRestart()
    {
        var primary = Guid.NewGuid();
        var additional = Guid.NewGuid();
        var oldSlots = DemoData.Settings().Slots.ToArray();
        using (var store = new SettingsStore(SettingsPath))
        {
            await store.SaveAsync(DemoData.Settings());
            var board = new SlotBoard(store);
            await board.LoadAsync();
            await board.SelectDesktopAsync(primary, "Desktop 1");
            await board.SelectDesktopAsync(additional, "Desktop 2");
            await board.AssignAsync(board.CurrentSlots[0].Id, DemoData.Machines()[3].UniqueId, _ => true);
        }
        using var reopened = new SettingsStore(SettingsPath);
        var saved = await reopened.LoadAsync();
        Assert.AreEqual(3, saved.Version);
        Assert.AreEqual(primary, saved.PrimaryDesktopId);
        CollectionAssert.AreEqual(oldSlots, saved.Slots);
        Assert.AreEqual(additional, saved.DesktopLayouts.Single().DesktopId);
        Assert.AreEqual(DemoData.Machines()[3].UniqueId,
            saved.DesktopLayouts.Single().Slots[0].MachineId);
        Assert.IsFalse(File.Exists(SettingsPath + ".tmp"));
    }

    [TestMethod]
    public async Task Load_OlderDesktopLayoutsWithoutKeepConnectedFields_DefaultsToEnabled()
    {
        var primary = Guid.NewGuid();
        var settings = DemoData.Settings() with
        {
            Version = 2,
            PrimaryDesktopId = primary,
            DesktopLayouts = [new(Guid.NewGuid(), "Desktop 2", 2, [new(Guid.NewGuid(), 1)])]
        };
        var json = JsonSerializer.SerializeToNode(settings,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
        json.Remove("primaryKeepConnected");
        json["desktopLayouts"]!.AsArray()[0]!.AsObject().Remove("keepConnected");
        using var store = new SettingsStore(SettingsPath);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, json.ToJsonString());

        var loaded = await store.LoadAsync();
        Assert.AreEqual(2, loaded.Version);
        Assert.IsTrue(loaded.PrimaryKeepConnected);
        Assert.IsTrue(loaded.DesktopLayouts.Single().KeepConnected);
    }

    [TestMethod]
    public async Task SaveLoad_SideBySideLayoutPersistsAndOldVersionCannotDropIt()
    {
        var primary = Guid.NewGuid();
        var secondary = Guid.NewGuid();
        using (var store = new SettingsStore(SettingsPath))
        {
            await store.SaveAsync(DemoData.Settings());
            var board = new SlotBoard(store);
            await board.LoadAsync();
            await board.SelectDesktopAsync(primary, "Desktop 2");
            await board.SelectDesktopAsync(secondary, "Desktop 3");
            await board.SetLayoutModeAsync(WindowLayoutMode.SideBySide);
            Assert.AreEqual(4, board.Settings.Version);
        }
        using var reopened = new SettingsStore(SettingsPath);
        var settings = await reopened.LoadAsync();
        Assert.AreEqual(WindowLayoutMode.Quadrants, settings.PrimaryLayoutMode);
        Assert.AreEqual(WindowLayoutMode.SideBySide, settings.DesktopLayouts.Single().LayoutMode);
        Assert.HasCount(4, settings.DesktopLayouts.Single().Slots);
        var before = await File.ReadAllTextAsync(SettingsPath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            reopened.SaveAsync(settings with { Version = 3 }));
        Assert.AreEqual(before, await File.ReadAllTextAsync(SettingsPath));
    }
}
