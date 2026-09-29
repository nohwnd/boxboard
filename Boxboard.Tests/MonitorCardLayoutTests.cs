using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Boxboard.Models;
using Boxboard.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Boxboard.Tests;

[TestClass]
public sealed class MonitorCardLayoutTests
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);
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

    private sealed class FakeMonitors(params MonitorInfo[] monitors) : IMonitors
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() => monitors;
    }

    private static FakeMonitors TwoMonitors() => new(
        new(First, 1, new(0, 0, 2560, 1440), new(0, 0, 2560, 1400), true),
        new(Second, 2, new(2560, 0, 1920, 1080), new(2560, 0, 1920, 1040), false));

    private static async Task<(SlotBoard Board, Guid Left, Guid Right)> PinnedBoardAsync()
    {
        var board = new SlotBoard(new MemoryStore(DemoData.Settings()));
        await board.LoadAsync();
        await board.EnsureFourCellsAsync();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        foreach (var (desktop, name) in new[] { (first, "Desktop 1"), (second, "Desktop 2") })
            foreach (var monitor in TwoMonitors().GetMonitors())
            {
                await board.SelectDesktopAsync(new LayoutKey(desktop, monitor.Id), name, monitor.Number);
                await board.EnsureFourCellsAsync();
            }
        return (board, first, second);
    }

    [TestMethod]
    [DoNotParallelize]
    public Task DesktopGroups_ShowOneCardPerMonitorUnderEachDesktop()
    {
        return WpfTestHost.RunAsync(async () =>
        {
            var (board, first, second) = await PinnedBoardAsync();
            var window = new MainWindow(board, demo: true, "offline-layout", TwoMonitors());
            try
            {
                Assert.HasCount(2, window.Groups);
                CollectionAssert.AreEqual(new[] { "Desktop 1", "Desktop 2" },
                    window.Groups.Select(group => group.Name).ToArray());
                Assert.IsTrue(window.Groups.All(group => group.Cards.Count == 2));
                Assert.AreEqual("2 monitors", window.Groups[0].Summary);
                CollectionAssert.AreEqual(new[] { "Monitor 1", "Monitor 2" },
                    window.Groups[0].Cards.Select(card => card.MonitorName).ToArray());
                Assert.AreEqual("1920 × 1080", window.Groups[0].Cards[1].MonitorDetails);
                CollectionAssert.AreEqual(new[] { first, first, second, second },
                    window.Cards.Select(card => card.DesktopId).ToArray());
                CollectionAssert.AreEqual(new[] { First, Second, First, Second },
                    window.Cards.Select(card => card.MonitorId).ToArray());
                Assert.HasCount(4, window.Cards);
                Assert.HasCount(16, window.Cells);
            }
            finally { window.Close(); }
        }, TimeSpan.FromSeconds(20));
    }

    [TestMethod]
    [DoNotParallelize]
    public Task DroppingOnAMonitorCard_AssignsWithinThatMonitorLayoutOnly()
    {
        return WpfTestHost.RunAsync(async () =>
        {
            var (board, first, _) = await PinnedBoardAsync();
            var window = new MainWindow(board, demo: true, "offline-layout", TwoMonitors());
            try
            {
                var target = window.Cards.Single(card =>
                    card.DesktopId == first && card.MonitorId == Second).Cells[0];
                var machine = DemoData.Machines()[3].UniqueId;
                await window.AssignDroppedMachineAsync(target.Key, target.Slot.Id,
                    new DataObject(MainWindow.MachineDragFormat, machine));

                Assert.AreEqual(machine, board.GetSlots(new LayoutKey(first, Second))[0].MachineId);
                Assert.AreNotEqual(machine, board.GetSlots(new LayoutKey(first, First))[0].MachineId);
                Assert.AreEqual(target.Key, new LayoutKey(first, Second));
            }
            finally { window.Close(); }
        }, TimeSpan.FromSeconds(20));
    }

    [TestMethod]
    [DoNotParallelize]
    public Task DisconnectedMonitor_KeepsItsCardButBlocksEditing()
    {
        return WpfTestHost.RunAsync(async () =>
        {
            var (board, first, _) = await PinnedBoardAsync();
            var single = new FakeMonitors(TwoMonitors().GetMonitors()[0]);
            var window = new MainWindow(board, demo: true, "offline-layout", single);
            try
            {
                var cards = window.Cards.Where(card => card.DesktopId == first).ToList();
                Assert.HasCount(2, cards);
                Assert.AreEqual("Monitor 2", cards[1].MonitorName);
                Assert.AreEqual("Disconnected", cards[1].MonitorDetails);
                Assert.IsFalse(cards[1].CanEdit);
                Assert.IsTrue(cards[1].Cells.All(cell => cell.StateText is "Empty" or "Desktop unavailable"));
            }
            finally { window.Close(); }
        }, TimeSpan.FromSeconds(20));
    }

    [TestMethod]
    [DoNotParallelize]
    public Task IdentifyOverlay_ShowsTheWindowsDisplayNumberOnEveryConnectedMonitor()
    {
        return WpfTestHost.RunAsync(() =>
        {
            var monitors = TwoMonitors().GetMonitors();
            var badges = MonitorIdentityOverlay.Show(monitors, TimeSpan.FromMilliseconds(80));
            try
            {
                Assert.HasCount(2, badges);
                CollectionAssert.AreEqual(new[] { "Boxboard Monitor 1", "Boxboard Monitor 2" },
                    badges.Select(badge => badge.Title).ToArray());
                foreach (var badge in badges)
                {
                    Assert.IsTrue(badge.Topmost);
                    Assert.IsFalse(badge.ShowInTaskbar);
                    var handle = new System.Windows.Interop.WindowInteropHelper(badge).Handle;
                    var exStyle = (long)GetWindowLongPtrW(handle, -20);
                    // WS_EX_TRANSPARENT so the badge never swallows a click meant for the app under it.
                    Assert.AreEqual(0x20, exStyle & 0x20);
                    Assert.AreEqual(0x08000000, exStyle & 0x08000000);
                }
                var numbers = badges.SelectMany(badge => MainWindow.Descendants<TextBlock>(badge))
                    .Select(text => text.Text).ToList();
                CollectionAssert.Contains(numbers, "1");
                CollectionAssert.Contains(numbers, "2");
                CollectionAssert.Contains(numbers, "Monitor 2 · 1920 × 1080");
            }
            finally
            {
                foreach (var badge in badges)
                    badge.Close();
            }
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(20));
    }

    [TestMethod]
    [DoNotParallelize]
    public Task PinnedWorkArea_ReplacesTheClientMonitorSoWindowsCanCrossMonitors()
    {
        return WpfTestHost.RunAsync(() =>
        {
            var owner = new Window
            {
                Width = 200, Height = 150, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Opacity = 0
            };
            try
            {
                // A window only belongs to a virtual desktop once it has been shown.
                owner.Show();
                var handle = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
                using var natural = new NativeSessionWindows(handle);
                var ownMonitor = natural.MonitorWorkArea();
                Assert.AreEqual(ownMonitor, natural.GetEnvironment().WorkArea);

                var pinned = new PixelRect(ownMonitor.X + 4000, ownMonitor.Y, 1920, 1040);
                using var target = new NativeSessionWindows(handle, pinned);
                Assert.AreEqual(pinned, target.GetEnvironment().WorkArea);
                Assert.AreEqual(ownMonitor, target.MonitorWorkArea());
                Assert.AreEqual(natural.GetEnvironment().DesktopId, target.GetEnvironment().DesktopId);
            }
            finally { owner.Close(); }
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(20));
    }

    [TestMethod]
    [DoNotParallelize]
    public Task LayoutPicker_ChangesOnlyTheMonitorCardItWasOpenedFrom()
    {
        return WpfTestHost.RunAsync(async () =>
        {
            var (board, first, _) = await PinnedBoardAsync();
            var window = new MainWindow(board, demo: true, "offline-layout", TwoMonitors());
            try
            {
                var card = window.Cards.Single(item => item.DesktopId == first && item.MonitorId == Second);
                Assert.IsTrue(card.CanChooseLayout, "The layout picker should be usable on a known monitor card.");
                Realize(window);
                var picker = MainWindow.Descendants<Button>(window.DesktopCards)
                    .Single(button => ReferenceEquals(button.DataContext, card) &&
                        button.Content is WindowLayoutChoice);
                Assert.IsTrue(picker.IsEnabled);

                // Open it the way a click does, so the menu resolves its card through the nested templates.
                picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = picker.ContextMenu!;
                Assert.AreSame(picker, menu.PlacementTarget);
                Assert.AreSame(card, menu.DataContext);
                var choice = menu.Items.Cast<MenuItem>()
                    .Single(item => (WindowLayoutMode)item.Tag == WindowLayoutMode.SideBySide);
                Assert.AreSame(card, choice.DataContext);
                choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;
                await SettleAsync(window);

                Assert.AreEqual(WindowLayoutMode.SideBySide, board.GetLayoutMode(new LayoutKey(first, Second)));
                Assert.AreEqual(WindowLayoutMode.Quadrants, board.GetLayoutMode(new LayoutKey(first, First)));
                var refreshed = window.Cards.Single(item => item.DesktopId == first && item.MonitorId == Second);
                Assert.AreEqual(WindowLayoutMode.SideBySide, refreshed.SelectedMode.Mode);
                Assert.HasCount(2, refreshed.Cells);
            }
            finally { window.Close(); }
        }, TimeSpan.FromSeconds(20));
    }

    private static void Realize(MainWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(680, window.MaxHeight));
        root.Arrange(new Rect(0, 0, 680, Math.Clamp(root.DesiredSize.Height, window.MinHeight, window.MaxHeight)));
        root.UpdateLayout();
    }

    private static async Task SettleAsync(Window window)
    {
        for (int attempt = 0; attempt < 20; attempt++)
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    [TestMethod]
    [DoNotParallelize]
    public Task Identify_ReEnumeratesSoAMonitorPluggedInLaterGetsACard()
    {
        return WpfTestHost.RunAsync(async () =>
        {
            var (board, first, _) = await PinnedBoardAsync();
            var growing = new GrowingMonitors(TwoMonitors().GetMonitors());
            var window = new MainWindow(board, demo: true, "offline-layout", growing);
            try
            {
                Assert.HasCount(1, growing.Calls);
                Assert.HasCount(2, window.Cards.Where(card => card.DesktopId == first).ToList());

                growing.Plug(new(@"\\.\DISPLAY3", 3, new(4480, 0, 1280, 1024), new(4480, 0, 1280, 984), false));
                window.IdentifyMonitors();

                Assert.HasCount(2, growing.Calls);
                var cards = window.Cards.Where(card => card.DesktopId == first).ToList();
                Assert.HasCount(3, cards);
                Assert.AreEqual("Monitor 3", cards[2].MonitorName);
                Assert.AreEqual("1280 × 1024", cards[2].MonitorDetails);
            }
            finally { window.Close(); }
        }, TimeSpan.FromSeconds(20));
    }

    private sealed class GrowingMonitors(IReadOnlyList<MonitorInfo> initial) : IMonitors
    {
        private List<MonitorInfo> _monitors = [.. initial];
        public List<int> Calls { get; } = [];
        public void Plug(MonitorInfo monitor) => _monitors = [.. _monitors, monitor];
        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            Calls.Add(_monitors.Count);
            return _monitors;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public Task IdentifyOverlay_RejectsAnEmptyOrFullyDisconnectedMonitorList()
    {
        return WpfTestHost.RunAsync(() =>
        {
            Assert.ThrowsExactly<ArgumentException>(() => MonitorIdentityOverlay.Show([]));
            var offline = TwoMonitors().GetMonitors().Select(monitor => monitor with { Available = false }).ToList();
            Assert.ThrowsExactly<InvalidOperationException>(() => MonitorIdentityOverlay.Show(offline));
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(20));
    }
}
