using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class MonitorShellTests
{
    [TestMethod]
    public void MigrationWithoutAnOpenClient_UsesOnlyExternalOrPrimaryMonitor()
    {
        var builtIn = new MonitorInfo("internal-path", 1, new(0, 0, 1920, 1080),
            new(0, 0, 1920, 1040), true) { IsBuiltIn = true };
        var external = new MonitorInfo("external-path", 2, new(1920, 0, 2560, 1440),
            new(1920, 0, 2560, 1400), false) { IsBuiltIn = false };
        Assert.AreEqual(external, MonitorShell.ChooseMigrationMonitor([builtIn, external], builtIn));

        var secondExternal = external with { Id = "second-external-path", Number = 3 };
        Assert.AreEqual(builtIn,
            MonitorShell.ChooseMigrationMonitor([builtIn, external, secondExternal], external));
        Assert.AreEqual(builtIn, MonitorShell.ChooseMigrationMonitor([builtIn], builtIn));
        Assert.AreEqual(builtIn,
            MonitorShell.ChooseMigrationMonitor([builtIn, external with { IsBuiltIn = null }], external));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MonitorShell.ChooseMigrationMonitor([external with { Available = false }], builtIn));
    }

    [TestMethod]
    public void DisplayConfigInterop_MatchesNativeStructLayouts()
    {
        Assert.AreEqual((72, 64, 84, 420), MonitorShell.NativeStructSizes);
    }

    [TestMethod]
    public void PersistentId_UsesTheTargetPathOrMarksGdiFallback()
    {
        Assert.AreEqual(@"\\?\DISPLAY#MONITOR#123", MonitorShell.PersistentId(
            @"\\.\DISPLAY3", @"\\?\DISPLAY#MONITOR#123"));
        Assert.AreEqual(@"gdi:\\.\DISPLAY3", MonitorShell.PersistentId(@"\\.\DISPLAY3", null));
        Assert.AreEqual(@"gdi:\\.\DISPLAY3", MonitorShell.PersistentId(@"\\.\DISPLAY3", ""));
    }

    [TestMethod]
    public void Number_OrdersMonitorsLeftToRightRegardlessOfTheAdapterDeviceName()
    {
        var numbered = MonitorShell.Number(
        [
            (@"\\.\DISPLAY577", new PixelRect(2560, 0, 1920, 1080), new PixelRect(2560, 0, 1920, 1040), false),
            (@"\\.\DISPLAY3", new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1040), false),
            (@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), true)
        ]);
        CollectionAssert.AreEqual(new[] { @"\\.\DISPLAY3", @"\\.\DISPLAY1", @"\\.\DISPLAY577" },
            numbered.Select(monitor => monitor.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, numbered.Select(monitor => monitor.Number).ToArray());
        Assert.AreEqual("Monitor 2", numbered[1].Name);
    }

    [TestMethod]
    public void Number_BreaksTiesOnTheSameColumnByTheTopEdge()
    {
        var numbered = MonitorShell.Number(
        [
            ("lower", new PixelRect(0, 1080, 1920, 1080), new PixelRect(0, 1080, 1920, 1040), false),
            ("upper", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true)
        ]);
        CollectionAssert.AreEqual(new[] { "upper", "lower" }, numbered.Select(monitor => monitor.Id).ToArray());
    }

    [TestMethod]
    public void GetMonitors_ReportsDistinctNumberedMonitorsWithExactlyOnePrimary()
    {
        var monitors = MonitorShell.Instance.GetMonitors();
        Assert.IsNotEmpty(monitors);
        Assert.AreEqual(1, monitors.Count(monitor => monitor.Primary));
        Assert.HasCount(monitors.Count, monitors.Select(monitor => monitor.Id).Distinct().ToList());
        CollectionAssert.AreEqual(Enumerable.Range(1, monitors.Count).ToArray(),
            monitors.Select(monitor => monitor.Number).ToArray());
        foreach (var monitor in monitors)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(monitor.GdiDeviceName));
            if (monitor.StableIdentity)
                StringAssert.StartsWith(monitor.Id, "edid:");
            if (!monitor.StableIdentity)
                StringAssert.Contains(monitor.Description, "pin may change");
            Assert.IsGreaterThan(0, monitor.Bounds.Width);
            Assert.IsGreaterThan(0, monitor.WorkArea.Height);
            Assert.IsTrue(monitor.Bounds.Contains(monitor.WorkArea),
                $"{monitor.Name} work area {monitor.WorkArea} is outside its bounds {monitor.Bounds}.");
            Assert.AreEqual($"Monitor {monitor.Number}", monitor.Name);
        }
    }

    [TestMethod]
    public void PhysicalMonitorIdentity_DoesNotDependOnWindowsInstanceOrExtensionBytes()
    {
        var edid = new byte[256];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(edid, 0);
        new byte[] { 0x09, 0xd1, 0x20, 0x80, 0x45, 0x12, 0x34, 0x56 }.CopyTo(edid, 8);
        var identity = MonitorIdentity.FromEdid(edid);
        Assert.IsNotNull(identity);
        StringAssert.StartsWith(identity, "edid:");
        edid[200] = 42;
        Assert.AreEqual(identity, MonitorIdentity.FromEdid(edid));
        edid[12] = 3;
        Assert.AreNotEqual(identity, MonitorIdentity.FromEdid(edid));
        Array.Fill(edid, (byte)0, 12, 4);
        Assert.IsNull(MonitorIdentity.FromEdid(edid));
        Assert.IsNull(MonitorIdentity.FromDevicePath("not-a-monitor-path"));
        Assert.IsNull(MonitorIdentity.FromDevicePath(@"\\?\DISPLAY#MODEL#..\bad#{GUID}"));
    }

    [TestMethod]
    public void Description_ShowsResolutionAndMarksThePrimaryAndDisconnectedMonitors()
    {
        var primary = new MonitorInfo(@"\\.\DISPLAY1", 1, new(0, 0, 2560, 1440), new(0, 0, 2560, 1400), true);
        var second = primary with { Id = @"\\.\DISPLAY2", Number = 2, Primary = false };
        Assert.AreEqual("2560 × 1440 · primary", primary.Description);
        Assert.AreEqual("2560 × 1440", second.Description);
        Assert.AreEqual("Disconnected", (second with { Available = false }).Description);
    }
}
