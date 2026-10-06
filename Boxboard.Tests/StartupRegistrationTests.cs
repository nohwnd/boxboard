using Boxboard.Models;
using Boxboard.Services;
using Boxboard;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class StartupRegistrationTests
{
    [TestMethod]
    public void Command_QuotesBothPathsWithoutChangingSettings()
    {
        var command = StartupRegistration.Command(@"C:\Program Files\Boxboard\Boxboard.exe",
            @"C:\Users\Sample User\AppData\Local\Boxboard\settings.json");
        Assert.AreEqual("\"C:\\Program Files\\Boxboard\\Boxboard.exe\" --settings " +
            "\"C:\\Users\\Sample User\\AppData\\Local\\Boxboard\\settings.json\"", command);
        Assert.ThrowsExactly<ArgumentException>(() =>
            StartupRegistration.Command("Boxboard.exe", @"C:\data\settings.json"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            StartupRegistration.Command(@"C:\Boxboard.exe", "\"bad\""));
    }

    [TestMethod]
    public void ExistingSettings_WithoutStartupPreferenceRemainOptIn()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<BoardSettings>(
            "{\"Version\":5,\"Slots\":[],\"Machines\":[],\"DesktopLayouts\":[]}");
        Assert.IsNotNull(old);
        Assert.IsFalse(old.StartWithWindows);
        Assert.IsTrue(new BoardSettings { StartWithWindows = true }.StartWithWindows);
        Assert.IsTrue(App.InitialSettings(demo: false).StartWithWindows);
        Assert.IsFalse(App.InitialSettings(demo: true).StartWithWindows);
    }
}
