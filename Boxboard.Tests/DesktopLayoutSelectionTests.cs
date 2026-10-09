using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class DesktopLayoutSelectionTests
{
    private static readonly Guid Manager = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Target = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static SessionWindow Client(int handle, string title, Guid desktop) =>
        new(new(handle, handle, 100), title, desktop, new(0, 0, 800, 600), false, false, false);

    [TestMethod]
    public void LegacyLayout_UsesUnambiguousExistingClientsDesktop()
    {
        var settings = DemoData.Settings();
        var selected = DesktopLayoutSelection.ChooseInitialDesktop(settings, Manager,
            [Client(1, "azdo1", Target), Client(2, "azdo2", Target)]);
        Assert.AreEqual(Target, selected);
    }

    [TestMethod]
    public void LegacyLayout_ConflictingOrDuplicateClientsFallBackToManagerDesktop()
    {
        var settings = DemoData.Settings();
        Assert.AreEqual(Manager, DesktopLayoutSelection.ChooseInitialDesktop(settings, Manager,
            [Client(1, "azdo1", Target), Client(2, "azdo2", Manager)]));
        Assert.AreEqual(Manager, DesktopLayoutSelection.ChooseInitialDesktop(settings, Manager,
            [Client(1, "azdo1", Target), Client(2, "azdo1", Target)]));
    }

    [TestMethod]
    public void SavedLayout_GuidWinsOverCurrentWindowPositions()
    {
        var settings = DemoData.Settings() with { Version = 2, PrimaryDesktopId = Target };
        Assert.AreEqual(Target, DesktopLayoutSelection.ChooseInitialDesktop(settings, Manager,
            [Client(1, "azdo1", Manager)]));
    }

    [TestMethod]
    public void MissingDesktop_IsHiddenOnlyWhenItHasNoAssignedDevBoxes()
    {
        var missing = new VirtualDesktopInfo(0, Target, "Desktop 2 (missing)", false);
        Assert.IsFalse(DesktopLayoutSelection.ShowDesktop(missing, new HashSet<Guid>()));
        Assert.IsTrue(DesktopLayoutSelection.ShowDesktop(missing, new HashSet<Guid> { Target }));
        Assert.IsTrue(DesktopLayoutSelection.ShowDesktop(
            missing with { Available = true }, new HashSet<Guid>()));
    }
}
