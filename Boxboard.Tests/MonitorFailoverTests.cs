using Boxboard.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class MonitorFailoverTests
{
    private static readonly PixelRect PrimaryArea = new(0, 0, 2560, 1400);
    private static readonly MonitorInfo Primary = new("internal", 1, PrimaryArea,
        PrimaryArea, true) { IsBuiltIn = true };
    private static readonly MonitorInfo External = new("external", 2, new(2560, 0, 1920, 1080),
        new(2560, 0, 1920, 1040), false) { IsBuiltIn = false };

    [TestMethod]
    public void Destination_UsesCurrentPrimaryOnlyWhilePinnedMonitorIsDisconnected()
    {
        var key = new LayoutKey(Guid.NewGuid(), External.Id);
        Assert.IsNull(MonitorFailover.Destination([Primary, External], key));
        Assert.AreEqual(Primary, MonitorFailover.Destination([Primary], key));
        Assert.AreEqual(Primary, MonitorFailover.Destination(
            [Primary, External with { Available = false }], key));
        Assert.IsNull(MonitorFailover.Destination([Primary], new(key.DesktopId, Primary.Id)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MonitorFailover.Destination([], key));
    }

    [TestMethod]
    public void Tile_ShowsAllClientsWithoutOverlapsOnThePrimaryMonitor()
    {
        for (int count = 1; count <= 12; count++)
        {
            var bounds = MonitorFailover.Tile(PrimaryArea, count);
            Assert.HasCount(count, bounds);
            Assert.IsTrue(bounds.All(rect => PrimaryArea.Contains(rect) &&
                rect.Width >= 100 && rect.Height >= 100));
            for (int left = 0; left < count; left++)
                for (int right = left + 1; right < count; right++)
                {
                    var a = bounds[left];
                    var b = bounds[right];
                    Assert.IsTrue(a.X + a.Width <= b.X || b.X + b.Width <= a.X ||
                        a.Y + a.Height <= b.Y || b.Y + b.Height <= a.Y);
                }

        }
        Assert.AreEqual(PrimaryArea, MonitorFailover.Tile(PrimaryArea, 1)[0]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MonitorFailover.Tile(PrimaryArea, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            MonitorFailover.Tile(new(0, 0, 90, 90), 2));
    }

    [TestMethod]
    public void Combine_TemporarilyTilesTwoLayoutsWithoutChangingSavedPinsOrAssignments()
    {
        var desktop = Guid.NewGuid();
        var laptop = new LayoutKey(desktop, Primary.Id);
        var dock = new LayoutKey(desktop, External.Id);
        var laptopSlots = Enumerable.Range(1, 4)
            .Select(index => new SlotAssignment(Guid.NewGuid(), index, $"laptop-{index}")).ToArray();
        var dockSlots = Enumerable.Range(1, 2)
            .Select(index => new SlotAssignment(Guid.NewGuid(), index, $"external-{index}")).ToArray();
        var six = MonitorFailover.Combine(PrimaryArea,
            [(laptop, laptopSlots), (dock, dockSlots)]);
        Assert.HasCount(6, six);
        Assert.IsTrue(six.Take(4).All(item => item.Key == laptop));
        Assert.IsTrue(six.Skip(4).All(item => item.Key == dock));
        Assert.IsTrue(six.All(item => PrimaryArea.Contains(item.Bounds)));
        Assert.AreEqual(External.Id, dock.MonitorId);
        CollectionAssert.AreEqual(dockSlots,
            six.Where(item => item.Key == dock).Select(item => item.Slot).ToArray());

        Assert.IsNull(MonitorFailover.Destination([Primary, External], dock));
        var restoredLaptop = WindowLayoutGeometry.Divide(PrimaryArea, WindowLayoutMode.Quadrants);
        var restoredDock = WindowLayoutGeometry.Divide(External.WorkArea, WindowLayoutMode.SideBySide);
        Assert.HasCount(4, restoredLaptop);
        Assert.HasCount(2, restoredDock);
        Assert.AreNotEqual(six[4].Bounds, restoredDock[0]);
    }
}
