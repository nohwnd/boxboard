using Bevdox.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class ManagementLayoutTests
{
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

    [TestMethod]
    [DoNotParallelize]
    public Task DesktopCards_RenderAllThreeLayoutsAndUnassignedInventoryOffscreen()
    {
        return WpfTestHost.RunAsync(() =>
        {
                var settings = DemoData.Settings();
                var extras = Enumerable.Range(1, 5).Select(index => new DevBoxInstance
                {
                    UniqueId = $"https://demo.invalid/projects/demo/users/demo/devboxes/devbox{index}",
                    OriginalName = $"devbox{index}", ProjectName = "Demo project",
                    DevCenterUri = "https://demo.invalid", State = "Running", Location = "Demo region"
                }).ToList();
                var board = new SlotBoard(new MemoryStore(settings with
                {
                    Machines = [.. settings.Machines, .. extras]
                }));
                board.LoadAsync().GetAwaiter().GetResult();
                board.EnsureFourCellsAsync().GetAwaiter().GetResult();
                var first = Guid.NewGuid();
                var second = Guid.NewGuid();
                var third = Guid.NewGuid();
                board.SelectDesktopAsync(first, "Desktop 1").GetAwaiter().GetResult();
                board.SelectDesktopAsync(second, "Desktop 2").GetAwaiter().GetResult();
                board.SetLayoutModeAsync(second, WindowLayoutMode.SideBySide).GetAwaiter().GetResult();
                board.AssignAsync(second, board.GetVisibleSlots(second)[0].Id,
                    DemoData.Machines()[2].UniqueId, _ => true).GetAwaiter().GetResult();
                board.SelectDesktopAsync(third, "Desktop 3").GetAwaiter().GetResult();
                board.SetLayoutModeAsync(third, WindowLayoutMode.LargeLeftTwoStackedRight).GetAwaiter().GetResult();
                board.AssignAsync(third, board.GetVisibleSlots(third)[0].Id,
                    DemoData.Machines()[3].UniqueId, _ => true).GetAwaiter().GetResult();
                board.SelectDesktopAsync(first, "Desktop 1").GetAwaiter().GetResult();
                var window = new MainWindow(board, demo: true, "offline-layout");
                var root = Measure(window);
                Assert.HasCount(3, window.Cards);
                Assert.HasCount(4, window.Cards[0].Cells);
                Assert.HasCount(2, window.Cards[1].Cells);
                Assert.HasCount(3, window.Cards[2].Cells);
                Assert.AreEqual("Desktop 1", window.Cards[0].Name);
                Assert.AreEqual(WindowLayoutMode.SideBySide, window.Cards[1].SelectedMode.Mode);
                Assert.AreEqual(WindowLayoutMode.LargeLeftTwoStackedRight, window.Cards[2].SelectedMode.Mode);
                Assert.IsTrue(window.Cards.All(card => card.KeepConnected && !card.CanEdit));
                Assert.HasCount(5, window.MachineList.Items);
                Assert.IsTrue(window.MachineList.Items.Cast<MachineOption>().All(machine =>
                    machine.Label is not ("azdo1" or "azdo2" or "azdo3" or "aitestagent" or "offline-box")));
                Assert.AreEqual(680, window.Width);
                var tiles = MainWindow.Descendants<Border>(window.DesktopCards)
                    .Where(border => border.Name == "SlotTile").ToList();
                Assert.HasCount(9, tiles);
                var thirdTiles = tiles.Where(tile => tile.DataContext is CellViewModel model &&
                    model.DesktopId == third).ToList();
                Assert.HasCount(3, thirdTiles);
                var positions = thirdTiles.Select(tile => tile.TranslatePoint(new Point(), root)).ToList();
                Assert.IsLessThan(positions[1].X, positions[0].X);
                Assert.IsLessThanOrEqualTo(1.0, Math.Abs(positions[1].X - positions[2].X));
                Assert.IsLessThan(positions[2].Y, positions[1].Y);
                Assert.IsGreaterThan(thirdTiles[1].ActualHeight, thirdTiles[0].ActualHeight);
                Capture(root, "compact-desktop-cards");
                window.Close();
                return Task.CompletedTask;
        }, TimeSpan.FromSeconds(20));
    }

    [TestMethod]
    [DoNotParallelize]
    public Task OneWindowCard_UsesFullCardWidthAndReturnsRemovedAssignmentsToTray()
    {
        return WpfTestHost.RunAsync(() =>
        {
            var board = new SlotBoard(new MemoryStore(DemoData.Settings()));
            board.LoadAsync().GetAwaiter().GetResult();
            board.EnsureFourCellsAsync().GetAwaiter().GetResult();
            board.SelectDesktopAsync(Guid.NewGuid(), "Desktop 2").GetAwaiter().GetResult();
            board.SetLayoutModeAsync(WindowLayoutMode.SingleWindow).GetAwaiter().GetResult();
            var window = new MainWindow(board, demo: true, "offline-layout");
            try
            {
                var root = Measure(window);
                Assert.HasCount(1, window.Cards);
                Assert.HasCount(1, window.Cards[0].Cells);
                Assert.AreEqual(WindowLayoutMode.SingleWindow, window.Cards[0].SelectedMode.Mode);
                Assert.IsFalse(window.Cards[0].HasHiddenAssignments);
                Assert.IsTrue(window.MachineList.Items.Cast<MachineOption>()
                    .Any(machine => machine.Label == "azdo2"));
                var tile = MainWindow.Descendants<Border>(window.DesktopCards)
                    .Single(border => border.Name == "SlotTile");
                Assert.IsGreaterThan(500.0, tile.ActualWidth);
                Capture(root, "compact-single-window");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(20));
    }

    private static FrameworkElement Measure(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(680, 850));
        root.Arrange(new Rect(0, 0, 680, 850));
        root.UpdateLayout();
        return root;
    }

    private static void Capture(FrameworkElement root, string name)
    {
        var directory = Environment.GetEnvironmentVariable("BOXBOARD_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap(680, 850, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }
}
