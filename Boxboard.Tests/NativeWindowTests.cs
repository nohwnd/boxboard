using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class NativeWindowTests
{
    [TestMethod]
    [DoNotParallelize]
    public Task CompactWindow_TrayDirectMoveClearAndReload_UsesSavedIdentities()
    {
        return WpfTestHost.RunAsync(async () =>
        {
                var directory = Path.Combine(Path.GetTempPath(), "Boxboard-tests", Guid.NewGuid().ToString("N"));
                MainWindow? window = null;
                try
                {
                    using var store = new SettingsStore(Path.Combine(directory, "settings.json"));
                    await store.SaveAsync(DemoData.Settings());
                    var board = new SlotBoard(store);
                    await board.LoadAsync();
                    await board.EnsureFourCellsAsync();
                    window = new MainWindow(board, demo: true, store.SettingsPath) { ShowActivated = false };
                    window.Show();
                    await WaitForAsync(() => board.DiscoveryVerified && window.RefreshButton.IsEnabled);
                    await SettleAsync(window);
                    Assert.HasCount(4, window.Cells);
                    Assert.HasCount(2, window.MachineList.Items);
                    Assert.IsTrue(window.MachineList.Items.Cast<MachineOption>().Any(m => m.Label == "aitestagent"));
                    Assert.IsFalse(window.MachineList.Items.Cast<MachineOption>().Any(m => m.Label.StartsWith("azdo2")));
                    Capture(window, "01-compact-board");
                    Assert.IsTrue(window.IsTrayIconVisible);
                    window.WindowState = WindowState.Minimized;
                    await SettleAsync(window);
                    Assert.IsFalse(window.IsVisible);
                    Assert.IsFalse(window.ShowInTaskbar);
                    window.RestoreFromTray();
                    await SettleAsync(window);
                    Assert.IsTrue(window.IsVisible);
                    Assert.IsTrue(window.ShowInTaskbar);
                    Assert.AreEqual(WindowState.Normal, window.WindowState);
                    Capture(window, "02-restored-from-tray");

                    var original = board.Settings.Slots.ToArray();
                    var sourceMachineId = original[1].MachineId;
                    Assert.IsNotNull(sourceMachineId);
                    var data = new DataObject(MainWindow.MachineDragFormat, sourceMachineId);
                    await window.AssignDroppedMachineAsync(original[0].Id, data);
                    await SettleAsync(window);
                    Assert.AreEqual(original[1].MachineId, board.Settings.Slots[0].MachineId);
                    Assert.AreEqual(original[0].MachineId, board.Settings.Slots[1].MachineId);
                    Assert.IsFalse(window.MachineList.Items.Cast<MachineOption>().Any(m => m.Label.StartsWith("azdo2")));
                    Assert.IsFalse(window.MachineList.Items.Cast<MachineOption>().Any(m => m.Label == "azdo1"));
                    Capture(window, "03-direct-assignment-move");

                    await window.AssignDroppedMachineAsync(original[3].Id,
                        new DataObject(MainWindow.MachineDragFormat, DemoData.Machines()[3].UniqueId));
                    Assert.AreEqual(DemoData.Machines()[3].UniqueId, board.Settings.Slots[3].MachineId);
                    var beforeInvalid = board.Settings.Slots.ToArray();
                    await window.AssignDroppedMachineAsync(original[0].Id, new DataObject(DataFormats.Text, "not an inventory machine"));
                    CollectionAssert.AreEqual(beforeInvalid, board.Settings.Slots);
                    Assert.Contains("inventory", window.ErrorText.Text);
                    await SettleAsync(window);

                    var actions = MainWindow.Descendants<Button>(window.DesktopCards)
                        .Single(button => button.Name == "ClearAssignmentButton" &&
                            ReferenceEquals(button.DataContext, window.Cells[0]));
                    actions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitForAsync(() => window.RefreshButton.IsEnabled && board.Settings.Slots[0].MachineId is null);
                    await window.AssignDroppedMachineAsync(original[0].Id,
                        new DataObject(MainWindow.MachineDragFormat, DemoData.Machines()[2].UniqueId));
                    var saved = board.Settings.Slots.ToArray();
                    window.Close();
                    Assert.IsFalse(window.IsTrayIconVisible);
                    var reopened = new SlotBoard(store);
                    await reopened.LoadAsync();
                    await reopened.EnsureFourCellsAsync();
                    window = new MainWindow(reopened, demo: true, store.SettingsPath) { ShowActivated = false };
                    window.Show();
                    await WaitForAsync(() => reopened.DiscoveryVerified && window.RefreshButton.IsEnabled);
                    await SettleAsync(window);
                    CollectionAssert.AreEqual(saved, reopened.Settings.Slots);
                    Capture(window, "05-reopened-compact-board");
                    VerifyEqualViewports(window);
                    window.SizeToContent = SizeToContent.Manual;
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                    await SettleAsync(window);
                    VerifyEqualViewports(window);
                    Capture(window, "06-minimum-board-size");
                    window.Width = 1400;
                    window.Height = 1000;
                    await SettleAsync(window);
                    Capture(window, "07-final-compact-board");
                    var port = MainWindow.Descendants<Border>(window.DesktopCards)
                        .Single(b => b.Name == "SlotTile" && ReferenceEquals(b.DataContext, window.Cells[0]));
                    var height = port.ActualHeight;
                    window.Cells[0].Status = new string('X', 400);
                    window.ErrorText.Text = new string('X', 400);
                    window.ErrorText.Visibility = Visibility.Visible;
                    await SettleAsync(window);
                    Assert.IsLessThanOrEqualTo(1.0, Math.Abs(port.ActualHeight - height));
                    window.ErrorText.Visibility = Visibility.Hidden;
                    window.LogButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await SettleAsync(window);
                    var logWindow = Application.Current.Windows.OfType<LogWindow>().Single();
                    var activity = Assert.IsInstanceOfType<ActivityLog>(logWindow.DataContext);
                    Assert.IsNotEmpty(activity.Entries);
                    Assert.IsTrue(activity.Entries.Any(entry => entry.Context == "Discovery"));
                    Capture(logWindow, "08-separate-live-log");
                    logWindow.Close();
                    window.Close();
                    var realWindow = new MainWindow(reopened, demo: false, store.SettingsPath);
                    Assert.AreEqual(WindowState.Normal, realWindow.WindowState);
                    Assert.AreEqual(680, realWindow.Width);
                    realWindow.Close();
                }
                finally
                {
                    window?.Close();
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                }
        }, TimeSpan.FromSeconds(45));
    }

    private static void VerifyEqualViewports(MainWindow window)
    {
        var ports = MainWindow.Descendants<Border>(window.DesktopCards).Where(b => b.Name == "SlotTile").ToList();
        Assert.HasCount(4, ports);
        Assert.IsLessThanOrEqualTo(1.0, ports.Max(p => p.ActualWidth) - ports.Min(p => p.ActualWidth));
        Assert.IsLessThanOrEqualTo(1.0, ports.Max(p => p.ActualHeight) - ports.Min(p => p.ActualHeight));
        Assert.IsTrue(ports.All(p => p.ActualWidth >= 100 && p.ActualHeight >= 50));
    }
    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(10))
                Assert.Fail("Timed out waiting for the native UI operation.");
            await Task.Delay(15);
        }
    }
    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
        await Task.Delay(70);
    }
    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("BOXBOARD_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right),
            (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }
}
