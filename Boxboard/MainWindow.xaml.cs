using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bevdox.Auth;
using Bevdox.Services;
using Boxboard.Models;
using Boxboard.Services;
using static Boxboard.Models.BoardSettings;
using Forms = System.Windows.Forms;

namespace Boxboard;

public partial class MainWindow : Window
{
    internal const string MachineDragFormat = "Boxboard.DevBoxIdentity";
    private readonly SlotBoard _board;
    private readonly string _settingsPath;
    private readonly bool _demo;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DevBoxDiscovery? _discovery;
    private readonly DevBoxLauncher? _launcher;
    private NativeSessionWindows? _windows;
    private readonly Dictionary<LayoutKey, LayoutRuntime> _layouts = [];
    private readonly Dictionary<Guid, PixelRect> _fallbackBounds = [];
    private HashSet<LayoutKey> _fallbackKeys = [];
    private HashSet<LayoutKey> _fallbackHosts = [];
    private bool _displayChangePending;
    private IReadOnlyList<VirtualDesktopInfo> _desktopChoices = [];
    private IReadOnlyList<MonitorInfo> _monitorChoices = [];
    private readonly IMonitors _monitors;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private bool _busy, _arranging, _closeRequested, _loadedOnce;
    private string? _operationError, _startupError;
    private Point _dragStart;
    private CellViewModel? _pressedCell;
    private UIElement? _pressedTile;
    private MachineOption? _pressedMachine;
    private Border? _pressedInventoryTile;
    private HwndSource? _source;
    private bool _sessionNotifications;
    private readonly ActivityLog _log;
    private LogWindow? _logWindow;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _trayIcon;
    private string? _trayError;

    private sealed record LayoutRuntime(LayoutKey Key, TargetSessionWindows Windows,
        SessionCoordinator Sessions, KeepConnectedController KeepConnected)
    {
        public Guid DesktopId => Key.DesktopId;
        public bool PendingApply { get; set; }
    }

    /// <summary>One card on a desktop: the monitor it pins Dev Boxes to.</summary>
    private sealed record CardTarget(LayoutKey Key, int MonitorNumber, string MonitorName,
        string MonitorDetails, bool Available);
    private static readonly Brush OpenBrush = new SolidColorBrush(Color.FromRgb(57, 164, 107));
    private static readonly Brush AttentionBrush = new SolidColorBrush(Color.FromRgb(232, 170, 60));
    private static readonly Brush MissingBrush = new SolidColorBrush(Color.FromRgb(157, 168, 174));
    private static readonly Brush UnappliedBrush = new SolidColorBrush(Color.FromRgb(118, 83, 163));

    public MainWindow(SlotBoard board, bool demo, string path, IMonitors? monitors = null)
    {
        InitializeComponent();
        _board = board;
        _settingsPath = path;
        _demo = demo;
        _monitors = monitors ?? MonitorShell.Instance;
        _log = new ActivityLog(Dispatcher);
        if (!demo)
        {
            var auth = new AuthService();
            _discovery = new DevBoxDiscovery(auth);
            _launcher = new DevBoxLauncher(auth);
        }
        // A supplied monitor source is already known; the real one is read when the window loads.
        if (monitors is not null)
            _monitorChoices = monitors.GetMonitors();
        VersionText.Text = FormatVersion(typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException("The Boxboard build version is unavailable."));
        ReapplyAllButton.IsEnabled = !demo;
        StartWithWindowsCheckBox.IsEnabled = !demo;
        StartWithWindowsCheckBox.IsChecked = !demo && board.Settings.StartWithWindows;
        Title = demo ? "Boxboard - OFFLINE DEMO" : "Boxboard";
        _log.Write("Boxboard", demo ? "Offline demo opened; no real clients will be controlled." :
            "Board opened. Keep connected may request missing assigned clients when enabled.");
        _log.Write("Settings", $"Layout file: {path}");
        Render();
    }

    internal IReadOnlyList<DesktopGroupViewModel> Groups =>
        (IReadOnlyList<DesktopGroupViewModel>)DesktopCards.ItemsSource;
    internal IReadOnlyList<DesktopCardViewModel> Cards => Groups.SelectMany(group => group.Cards).ToList();
    internal IReadOnlyList<CellViewModel> Cells => Cards.SelectMany(card => card.Cells).ToList();
    internal bool IsTrayIconVisible => _notifyIcon?.Visible == true;

    internal static string FormatVersion(string informationalVersion)
    {
        var version = informationalVersion.Split('+')[0];
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("The build version cannot be empty.", nameof(informationalVersion));
        return version == "0.0.0-dev" ? "dev" : $"v{version}";
    }

    internal void ReportStartupError(string message)
    {
        _startupError = message;
        _log.Write("Error", message);
        ShowError();
    }

    private async void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadedOnce || _demo || sender is not CheckBox box ||
            _board.Settings.StartWithWindows == (box.IsChecked == true))
            return;
        var enabled = box.IsChecked == true;
        await RunAsync(async () =>
        {
            var previous = _board.Settings.StartWithWindows;
            StartupRegistration.Apply(enabled, _settingsPath);
            try { await _board.SetStartWithWindowsAsync(enabled, _lifetime.Token); }
            catch
            {
                StartupRegistration.Apply(previous, _settingsPath);
                throw;
            }
            _startupError = null;
            _log.Write("Startup", $"Start with Windows {(enabled ? "enabled" : "disabled")}.");
        });
        StartWithWindowsCheckBox.IsChecked = _board.Settings.StartWithWindows;
    }

    private void Render()
    {
        var selectedId = (MachineList.SelectedItem as MachineOption)?.UniqueId;
        var options = _board.Options;
        var assigned = _board.Settings.Slots.Concat(_board.Settings.DesktopLayouts.SelectMany(layout => layout.Slots))
            .Where(slot => slot.MachineId is not null).Select(slot => slot.MachineId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unassigned = options.Where(machine => !assigned.Contains(machine.UniqueId)).ToList();
        MachineList.ItemsSource = unassigned;
        MachineList.SelectedItem = unassigned.SingleOrDefault(machine => SameId(machine.UniqueId, selectedId));
        var desktops = _desktopChoices.Count > 0 ? _desktopChoices :
            _board.Layouts.Count > 0
                ? _board.Layouts.Select(layout => layout.DesktopId).Distinct()
                    .Select((id, index) => new VirtualDesktopInfo(
                        SavedDesktopNumber(_board.Layouts.First(layout => layout.DesktopId == id).Name, index + 1),
                        id, _board.Layouts.First(layout => layout.DesktopId == id).Name)).ToList()
                : [new VirtualDesktopInfo(1, Guid.Empty, "Desktop 1")];
        var scroll = DesktopScroll.VerticalOffset;
        var desktopsWithAssignments = _board.Layouts
            .Where(layout => _board.GetSlots(layout.Key).Any(slot => slot.MachineId is not null))
            .Select(layout => layout.DesktopId).ToHashSet();
        var groups = desktops.Where(desktop =>
                DesktopLayoutSelection.ShowDesktop(desktop, desktopsWithAssignments))
            .OrderByDescending(desktop => desktop.Available)
            .ThenBy(desktop => desktop.Number).Select(desktop => new DesktopGroupViewModel
            {
                DesktopId = desktop.Id,
                Name = desktop.Name,
                Available = desktop.Available,
                Cards = CardTargets(desktop.Id)
                    .Select(target => BuildCard(desktop, target, options)).ToList()
            }).ToList();
        DesktopCards.ItemsSource = groups;
        Dispatcher.BeginInvoke(() => DesktopScroll.ScrollToVerticalOffset(scroll), DispatcherPriority.Loaded);
        UpdateBoardStatus();
        ShowError();
    }

    /// <summary>The monitor cards to show under one virtual desktop.</summary>
    private LayoutKey KeyForMonitor(Guid desktopId, MonitorInfo monitor) =>
        _board.Layouts.FirstOrDefault(layout => layout.DesktopId == desktopId &&
            string.Equals(layout.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase))?.Key ??
        new LayoutKey(desktopId, monitor.Id);

    private IReadOnlyList<CardTarget> CardTargets(Guid desktopId)
    {
        var saved = _board.Layouts.Where(layout => layout.DesktopId == desktopId).ToList();
        var connected = _monitorChoices.Where(monitor => monitor.Available)
            .Select(monitor => new CardTarget(KeyForMonitor(desktopId, monitor), monitor.Number,
                monitor.Name, monitor.Description, Available: true))
            .ToList();
        var unpinned = saved.Where(layout => layout.MonitorId is null)
            .Select(layout => new CardTarget(layout.Key, 0, "Any monitor",
                "Follows the monitor Boxboard is on", Available: true));
        var disconnected = saved
            .Where(layout => layout.MonitorId is not null &&
                connected.All(card => card.Key != layout.Key) &&
                _board.GetSlots(layout.Key).Any(slot => slot.MachineId is not null))
            .Select(layout =>
            {
                var gone = new MonitorInfo(layout.MonitorId!, layout.MonitorNumber,
                    default, default, Primary: false, Available: false);
                return new CardTarget(layout.Key, gone.Number, gone.Name, gone.Description, Available: false);
            });
        List<CardTarget> targets = [.. connected, .. unpinned, .. disconnected];
        return targets.Count > 0 ? targets
            : [new CardTarget(new(desktopId), 0, "Any monitor", "Follows the monitor Boxboard is on", true)];
    }

    private DesktopCardViewModel BuildCard(VirtualDesktopInfo desktop, CardTarget target,
        IReadOnlyList<MachineOption> options)
    {
        var key = target.Key;
        var primaryDemo = desktop.Id == Guid.Empty && _board.Settings.PrimaryDesktopId is null;
        var known = primaryDemo || _board.HasLayout(key);
        IReadOnlyList<SlotAssignment> slots = !known ? [] : primaryDemo ? _board.CurrentSlots : _board.GetSlots(key);
        IReadOnlyList<SlotAssignment> visible = !known ? [] :
            primaryDemo ? _board.VisibleSlots : _board.GetVisibleSlots(key);
        var mode = !known ? WindowLayoutMode.Quadrants :
            primaryDemo ? _board.LayoutMode : _board.GetLayoutMode(key);
        var available = desktop.Available && target.Available;
        _layouts.TryGetValue(key, out var runtime);
        var cells = visible.Select(slot =>
        {
            var machine = options.SingleOrDefault(option => SameId(option.UniqueId, slot.MachineId));
            var cell = new CellViewModel
            {
                DesktopId = desktop.Id, MonitorId = key.MonitorId, Slot = slot,
                MachineName = slot.MachineId is null ? "Unassigned" :
                    _board.GetMachine(slot.MachineId).EffectiveName,
                MachineDetails = machine?.Details ?? "Drop a Dev Box here.",
                CanBind = !_demo && available && runtime is not null && machine is not null,
                CanConnect = !_demo && available && runtime?.PendingApply != true &&
                    machine?.Available == true && runtime?.Sessions.For(slot).Connecting != true,
                Status = _demo ? "Demo only: no remote desktop is connected." :
                    runtime?.Sessions.For(slot).Status ?? "No client window is bound."
            };
            UpdateCellState(cell, runtime, available);
            return cell;
        }).ToList();
        var hidden = slots.Skip(visible.Count).Where(slot => slot.MachineId is not null)
            .Select(slot => _board.GetMachine(slot.MachineId!).EffectiveName).ToList();
        return new DesktopCardViewModel
        {
            DesktopId = desktop.Id, Name = desktop.Name, Mode = mode,
            MonitorId = key.MonitorId, MonitorNumber = target.MonitorNumber,
            MonitorName = target.MonitorName, MonitorDetails = target.MonitorDetails,
            IsDisconnected = !target.Available,
            DisconnectedWarning = !target.Available
                ? $"{slots.Count(slot => slot.MachineId is not null)} saved Dev Box(es). " +
                  (desktop.Available
                      ? "Open clients use the primary monitor; use Re-apply all to start missing clients." :
                      "The desktop is unavailable; assignments are preserved.")
                : "",
            KeepConnected = primaryDemo || !known ? false : _board.IsKeepConnected(key),
            CanEdit = !_demo && available && runtime is not null,
            CanChooseLayout = known && available && (_demo || runtime is not null),
            PendingApply = runtime?.PendingApply == true,
            Cells = cells,
            HiddenAssignments = hidden.Count == 0 ? "" :
                $"Saved outside this layout: {string.Join(", ", hidden)}."
        };
    }

    private static int SavedDesktopNumber(string name, int fallback)
    {
        const string prefix = "Desktop ";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
            number > 0 ? number : fallback;
    }

    private void UpdateCellState(CellViewModel cell, LayoutRuntime? runtime, bool available)
    {
        cell.FullscreenBlocked = false;
        if (!cell.IsAssigned)
        {
            cell.StateText = "Empty";
            cell.StateBrush = MissingBrush;
            return;
        }
        if (!available || runtime is null)
        {
            cell.StateText = "Desktop unavailable";
            cell.StateBrush = MissingBrush;
            return;
        }
        var session = runtime.Sessions.For(cell.Slot);
        if (session.ReconnectPromptSuspected)
        {
            cell.StateText = "Check reconnect";
            cell.StateBrush = AttentionBrush;
        }
        else if (session.Connecting)
        {
            cell.StateText = "Connecting";
            cell.StateBrush = AttentionBrush;
        }
        else if (session.Fullscreen)
        {
            cell.FullscreenBlocked = true;
            cell.StateText = "Fullscreen: not placed";
            cell.StateBrush = AttentionBrush;
        }
        else if (session.BoundWindow is { } identity &&
                 runtime.Sessions.Candidates(_board.GetMachine(cell.Slot.MachineId!))
                     .Any(window => window.Identity == identity && window.DesktopId == cell.DesktopId))
        {
            cell.StateText = "Window open";
            cell.StateBrush = OpenBrush;
        }
        else
        {
            cell.StateText = session.Failed ? "Needs attention" : "No window";
            cell.StateBrush = session.Failed ? AttentionBrush : MissingBrush;
        }
    }

    private void UpdateBoardStatus()
    {
        var names = _layouts.Values.SelectMany(runtime => runtime.Sessions.ReconnectPromptCandidates)
            .Select(candidate => candidate.ClientName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        if (names is { Count: > 0 })
            StatusText.Text = $"Possible reconnect prompt: {string.Join(", ", names)}. " +
                "Keep on may restart the client; verify in Windows App.";
        else if (FullscreenClients() is { Count: > 0 } fullscreen)
            StatusText.Text = $"Not placed because the client is fullscreen: {string.Join(", ", fullscreen)}. " +
                "Set these Dev Boxes to windowed in the Windows App display settings, then use Re-apply.";
        else
        {
            var refresh = _board.Settings.LastRefreshUtc is { } time ? $" Last discovery: {time.ToLocalTime():g}." : "";
            var desktops = Groups.Count;
            var monitors = _monitorChoices.Count;
            StatusText.Text = $"{desktops} desktop{(desktops == 1 ? "" : "s")} · " +
                $"{monitors} monitor{(monitors == 1 ? "" : "s")} · " +
                $"{_board.Options.Count} known Dev Boxes.{refresh}";
        }
    }

    /// <summary>Dev Boxes whose client cannot be placed because it has no title bar or resize frame.</summary>
    internal IReadOnlyList<string> FullscreenClients() => DesktopCards.ItemsSource is null ? [] :
        [.. Cells.Where(cell => cell.FullscreenBlocked).Select(cell => cell.MachineName)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];

    private void ShowError()
    {
        var error = _operationError ?? _startupError ?? _board.RefreshError ?? _trayError;
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        DesktopCards.IsEnabled = MachineList.IsEnabled = RefreshButton.IsEnabled =
            ReapplyAllButton.IsEnabled = false;
        StartWithWindowsCheckBox.IsEnabled = false;
        _operationError = null;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            _lifetime.Token.ThrowIfCancellationRequested();
            while (_arranging)
                await Task.Delay(25, _lifetime.Token);
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _operationError = ex.Message; _log.Write("Error", ex.Message); }
        finally
        {
            Render();
            _busy = false;
            DesktopCards.IsEnabled = MachineList.IsEnabled = RefreshButton.IsEnabled = true;
            ReapplyAllButton.IsEnabled = !_demo;
            StartWithWindowsCheckBox.IsEnabled = !_demo;
            if (_displayChangePending && !_closeRequested)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = ApplyDisplayChange()));
            if (_closeRequested)
                Close();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
            return;
        _loadedOnce = true;
        InitializeTrayIcon();
        if (_demo)
        {
            await SeedDemoLayoutsAsync();
            await RefreshAsync();
            return;
        }

        await Dispatcher.Yield(DispatcherPriority.Background);
        if (_closeRequested)
            return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            _windows = new NativeSessionWindows(handle);
            var managerDesktop = _windows.GetEnvironment().DesktopId;
            _monitorChoices = _monitors.GetMonitors();
            var migrated = await MonitorPinMigration.MigrateAsync(_board, _monitorChoices,
                MonitorIdentity.FromDevicePath, _lifetime.Token);
            if (migrated.Migrated > 0)
                _log.Write("Monitors", $"Matched {migrated.Migrated} saved layout(s) to their " +
                    "physical displays. All assigned slots and layout preferences were retained.");
            if (migrated.Conflicts.Count > 0)
                ReportStartupError($"{migrated.Conflicts.Count} saved monitor layout(s) have " +
                    "conflicting assignments on the same physical display. Their pins were retained.");
            var boardMonitor = MonitorFor(_windows.MonitorWorkArea());
            _log.Write("Monitors", $"{_monitorChoices.Count} monitor(s): " +
                string.Join(", ", _monitorChoices.Select(monitor => $"{monitor.Name} ({monitor.Description})")) +
                $". Boxboard is on {boardMonitor.Name}.");
            _desktopChoices = Environment.OSVersion.Version.Build >= 26100
                ? VirtualDesktopShell.GetDesktops()
                : [new(1, managerDesktop, "Current desktop")];
            if (Environment.OSVersion.Version.Build < 26100)
                _log.Write("Desktop", "Multiple desktop layouts require Windows 11 build 26100 or later; current desktop remains available.");
            var known = _desktopChoices.SingleOrDefault(desktop => desktop.Id == managerDesktop)
                ?? throw new InvalidOperationException("The management window's desktop is missing from the Shell desktop list.");
            var hadPrimaryDesktop = _board.Settings.PrimaryDesktopId is not null;
            var initialDesktop = DesktopLayoutSelection.ChooseInitialDesktop(
                _board.Settings, managerDesktop, _windows.Enumerate());
            var initialChoice = _desktopChoices.SingleOrDefault(desktop => desktop.Id == initialDesktop);
            if (initialChoice is null)
            {
                _log.Write("Desktop", "The saved desktop no longer exists. Its assignments were retained; " +
                    "a new layout will be selected for the management window's desktop.");
                initialChoice = known;
            }
            await PinSavedLayoutsAsync(boardMonitor);
            var initialKey = KeyForMonitor(initialChoice.Id, boardMonitor);
            await _board.SelectDesktopAsync(initialKey, initialChoice.Name, boardMonitor.Number, _lifetime.Token);
            if (!hadPrimaryDesktop)
                _log.Write("Desktop", $"Linked the legacy slot assignments to {initialChoice.Name} ({initialChoice.Id}).");
            await _board.EnsureFourCellsAsync(_lifetime.Token);
            RefreshDesktopChoices(_desktopChoices);
            await EnsureMonitorLayoutsAsync();
            await _board.SelectDesktopAsync(initialKey, initialChoice.Name, boardMonitor.Number, _lifetime.Token);
            Render();
            _source = HwndSource.FromHwnd(handle);
            _source.AddHook(SessionMessage);
            if (SessionNotifications.Register(handle) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            _sessionNotifications = true;
            _log.Write("Window integration", "Monitoring msrdc window metadata. Only explicitly bound or requested replacement windows can be arranged.");
            _timer.Tick += async (_, _) => await UpdateSessionsAsync();
            _timer.Start();
            await RunAsync(ReconcileDisplayFailoverAsync);
            await UpdateSessionsAsync();
        }
        catch (Exception ex)
        {
            foreach (var runtime in _layouts.Values.ToList())
            {
                runtime.KeepConnected.Dispose();
                runtime.Sessions.Dispose();
            }
            _layouts.Clear();
            _windows?.Dispose();
            _windows = null;
            _operationError = $"Window integration unavailable: {ex.Message}";
            _log.Write("Error", _operationError);
            ShowError();
        }
    }

    private void InitializeTrayIcon()
    {
        if (_notifyIcon is not null || _trayError is not null)
            return;
        try
        {
            _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(
                Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable."))
                ?? throw new InvalidOperationException("The Boxboard executable has no icon.");
            _trayMenu = new Forms.ContextMenuStrip();
            _trayMenu.Items.Add("Open Boxboard", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
            _trayMenu.Items.Add("Exit Boxboard", null, (_, _) => Dispatcher.BeginInvoke(Close));
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Text = _demo ? "Boxboard (offline demo)" : "Boxboard",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _notifyIcon.MouseDoubleClick += (_, args) =>
            {
                if (args.Button == Forms.MouseButtons.Left)
                    Dispatcher.BeginInvoke(RestoreFromTray);
            };
            if (WindowState == WindowState.Minimized)
                HideToTray();
        }
        catch (Exception ex)
        {
            _notifyIcon?.Dispose();
            _notifyIcon = null;
            _trayMenu?.Dispose();
            _trayMenu = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
            _trayError = $"Tray icon unavailable: {ex.Message} Minimize will still use the taskbar.";
            _log.Write("Error", _trayError);
            ShowError();
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && IsTrayIconVisible)
            HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    internal void RestoreFromTray()
    {
        if (_closeRequested)
            return;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// The offline demo reads the real monitors so its cards show the same desktop and
    /// monitor grouping as a live board. No client window is ever touched in demo mode.
    /// </summary>
    private async Task SeedDemoLayoutsAsync()
    {
        if (_board.Settings.PrimaryDesktopId is not null)
            return;
        try { _monitorChoices = _monitors.GetMonitors(); }
        catch (Exception ex)
        {
            _log.Write("Monitors", $"Offline demo could not read the monitors: {ex.Message}");
            return;
        }
        foreach (var (desktop, name) in new[] { (Guid.NewGuid(), "Desktop 1"), (Guid.NewGuid(), "Desktop 2") })
            foreach (var monitor in _monitorChoices)
            {
                await _board.SelectDesktopAsync(new LayoutKey(desktop, monitor.Id), name,
                    monitor.Number, _lifetime.Token);
                await _board.EnsureFourCellsAsync(_lifetime.Token);
            }
        // Leave the first card selected so the demo's drag and drop targets it by default.
        var primary = _board.Layouts[0];
        await _board.SelectDesktopAsync(primary.Key, primary.Name, primary.MonitorNumber, _lifetime.Token);
        _log.Write("Monitors", $"Offline demo laid out 2 desktops across {_monitorChoices.Count} monitor(s).");
    }

    /// <summary>The monitor whose work area matches, falling back to the primary monitor.</summary>
    private MonitorInfo MonitorFor(PixelRect workArea)
    {
        var matches = _monitorChoices.Where(monitor => monitor.WorkArea == workArea).ToList();
        if (matches.Count == 1)
            return matches[0];
        var chosen = _monitorChoices.FirstOrDefault(monitor => monitor.Primary) ??
            _monitorChoices.FirstOrDefault() ??
            throw new InvalidOperationException("Windows reported no monitors.");
        _log.Write("Monitors", matches.Count > 1
            ? $"Several monitors report the work area {workArea}; using {chosen.Name}."
            : $"No monitor reports the work area {workArea}; using {chosen.Name}.");
        return chosen;
    }

    /// <summary>Every desktop and monitor combination that should have a layout card.</summary>
    private IEnumerable<LayoutKey> LayoutKeys() => _desktopChoices
        .Where(desktop => desktop.Available)
        .SelectMany(desktop => _monitorChoices.Where(monitor => monitor.Available)
            .Select(monitor => KeyForMonitor(desktop.Id, monitor)));

    /// <summary>
    /// A running assigned client identifies its monitor. Without one, prefer a single
    /// external monitor; otherwise use the active primary rather than guessing from Boxboard's position.
    /// </summary>
    private async Task PinSavedLayoutsAsync(MonitorInfo boardMonitor)
    {
        var manager = _windows ?? throw new InvalidOperationException("Window integration is unavailable.");
        var fallbackCount = 0;
        MonitorInfo? fallbackMonitor = null;
        var soleExternal = _monitorChoices.Count(choice => choice.Available && choice.IsBuiltIn == false) == 1;
        string FallbackReason(MonitorInfo monitor) => soleExternal
            ? "the only external monitor" : monitor.Primary ? "the primary monitor" : "the available monitor";
        foreach (var layout in _board.Layouts.Where(layout => layout.MonitorId is null).ToList())
        {
            var anchorArea = LegacyWorkArea(manager, layout.Key);
            var monitor = anchorArea is { } area
                ? MonitorFor(area)
                : MonitorShell.ChooseMigrationMonitor(_monitorChoices, boardMonitor);
            await _board.PinLayoutAsync(layout.Key, monitor.Id, monitor.Number, _lifetime.Token);
            if (anchorArea is null)
            {
                fallbackCount++;
                fallbackMonitor = monitor;
            }
            _log.Write("Monitors", anchorArea is null
                ? $"No assigned client was open for {layout.Name}; pinned its saved layout to {monitor.Name} " +
                  $"({FallbackReason(monitor)})."
                : $"Pinned the saved {layout.Name} layout to {monitor.Name} based on an assigned client.");
        }
        if (fallbackMonitor is not null)
            _notifyIcon?.ShowBalloonTip(6000, "Saved layouts pinned",
                $"No assigned client was open for {fallbackCount} saved layout(s). Pinned to " +
                $"{fallbackMonitor.Name} ({FallbackReason(fallbackMonitor)}).",
                Forms.ToolTipIcon.Info);
    }

    /// <summary>Where an unpinned layout used to land, if its assigned client is open.</summary>
    private PixelRect? LegacyWorkArea(NativeSessionWindows manager, LayoutKey key)
    {
        var assignedNames = _board.GetSlots(key).Where(slot => slot.MachineId is not null)
            .Select(slot => _board.GetMachine(slot.MachineId!).OriginalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchor = manager.Enumerate().FirstOrDefault(window =>
            window.DesktopId == key.DesktopId && assignedNames.Contains(window.Title));
        if (anchor is null)
            return null;
        using var target = new NativeSessionWindows(anchor.Identity.Handle);
        return target.MonitorWorkArea();
    }

    /// <summary>
    /// The pinned monitor's work area, read when it is needed rather than captured once,
    /// so a resolution or scaling change does not leave the layout tiling a stale rectangle.
    /// </summary>
    private PixelRect PinnedWorkArea(LayoutKey key, PixelRect lastKnown) =>
        key.MonitorId is { } id
            ? _monitorChoices.FirstOrDefault(monitor => monitor.Available &&
                string.Equals(monitor.Id, id, StringComparison.OrdinalIgnoreCase))?.WorkArea ??
              MonitorFailover.Destination(_monitorChoices, key)!.WorkArea
            : lastKnown;

    private LayoutRuntime GetOrCreateLayout(LayoutKey key)
    {
        if (_layouts.TryGetValue(key, out var existing))
            return existing;
        var manager = _windows ?? throw new InvalidOperationException("Window integration is unavailable.");
        var initialArea = key.MonitorId is { } monitorId
            ? (_monitorChoices.FirstOrDefault(monitor => monitor.Available &&
                string.Equals(monitor.Id, monitorId, StringComparison.OrdinalIgnoreCase))
                ?? MonitorFailover.Destination(_monitorChoices, key) ??
                throw new InvalidOperationException("The pinned monitor is unavailable.")).WorkArea
            : LegacyWorkArea(manager, key) ?? manager.MonitorWorkArea();
        var desktopId = key.DesktopId;
        PixelRect Area() => PinnedWorkArea(key, initialArea);
        var targetWindows = new TargetSessionWindows(manager, desktopId, Area,
            VirtualDesktopShell.GetCurrentDesktopId,
            async (window, bounds, ct) =>
            {
                // The pinned work area is the placement target even when the client currently
                // sits on another monitor, so moving it across monitors is allowed.
                using var client = new NativeSessionWindows(window.Identity.Handle, Area());
                if (client.GetEnvironment().DesktopId != desktopId)
                    throw new InvalidOperationException("The selected client moved to another desktop.");
                await client.PlaceAsync(window, bounds, ct);
            });
        var sessions = new SessionCoordinator(targetWindows,
            (machine, ct) => _launcher!.GetWindowsAppConnectionUriAsync(machine, ct),
            uri => Process.Start(new ProcessStartInfo(uri.OriginalString) { UseShellExecute = true }),
            moveToDesktop: VirtualDesktopShell.MoveAssignedWindow);
        sessions.Activity += (slot, message) => _log.Write(
            $"{slot.Name} / {_board.Settings.Machines.SingleOrDefault(m => SameId(m.UniqueId, slot.MachineId))?.EffectiveName ?? "unassigned"}",
            message);
        var runtime = new LayoutRuntime(key, targetWindows, sessions,
            new KeepConnectedController(targetWindows, sessions));
        _layouts.Add(key, runtime);
        return runtime;
    }

    private void RefreshDesktopChoices(IReadOnlyList<VirtualDesktopInfo> available)
    {
        _desktopChoices = [.. available, .. _board.Layouts
            .Where(layout => available.All(desktop => desktop.Id != layout.DesktopId))
            .GroupBy(layout => layout.DesktopId)
            .Select(group => new VirtualDesktopInfo(0, group.Key, $"{group.First().Name} (missing)", false))];
    }

    private nint SessionMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x02B1 && (int)wParam == 7)
        {
            foreach (var runtime in _layouts.Values)
                runtime.Sessions.CancelPending("Windows locked; no connection will be requested during the lock.");
            _log.Write("Windows session",
                "Locked; pending requests cancelled. Keep connected may request missing clients after unlock.");
        }
        else if (message == 0x02B1 && (int)wParam == 8)
        {
            _displayChangePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = ApplyDisplayChange()));
        }
        // WM_DISPLAYCHANGE: monitor topology or work areas changed.
        else if (message == 0x007E)
        {
            _displayChangePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = ApplyDisplayChange()));
        }
        return 0;
    }

    private async Task ApplyDisplayChange()
    {
        if (_demo || _windows is null || _closeRequested || !_displayChangePending || _busy)
            return;
        await RunAsync(async () =>
        {
            _displayChangePending = false;
            await Task.Delay(300, _lifetime.Token);
            var previous = _monitorChoices;
            _monitorChoices = _monitors.GetMonitors();
            if (!previous.SequenceEqual(_monitorChoices))
                _log.Write("Monitors", "Display layout changed: " +
                    string.Join(", ", _monitorChoices.Select(monitor => $"{monitor.Name} ({monitor.Description})")) +
                    ". Rechecking saved layouts and temporary placement.");
            await EnsureMonitorLayoutsAsync();
            await ReconcileDisplayFailoverAsync();
        });
    }

    /// <summary>Gives every available desktop and connected monitor a saved layout and a runtime.</summary>
    private async Task EnsureMonitorLayoutsAsync()
    {
        var selected = _board.SelectedKey;
        foreach (var key in LayoutKeys().ToList())
        {
            var desktop = _desktopChoices.Single(choice => choice.Id == key.DesktopId);
            var monitor = _monitorChoices.Single(item =>
                string.Equals(item.Id, key.MonitorId, StringComparison.OrdinalIgnoreCase));
            if (!_board.HasLayout(key))
            {
                await _board.SelectDesktopAsync(key, desktop.Name, monitor.Number, _lifetime.Token);
                await _board.EnsureFourCellsAsync(_lifetime.Token);
            }
            GetOrCreateLayout(key);
        }
        foreach (var layout in _board.Layouts.Where(layout =>
            _desktopChoices.Any(desktop => desktop.Id == layout.DesktopId && desktop.Available) &&
            layout.MonitorId is not null && _monitorChoices.All(monitor =>
                !monitor.Available ||
                !string.Equals(monitor.Id, layout.MonitorId, StringComparison.OrdinalIgnoreCase)) &&
            _board.GetVisibleSlots(layout.Key).Any(slot => slot.MachineId is not null)))
            GetOrCreateLayout(layout.Key);
        if (selected is { } previous && _board.HasLayout(previous))
        {
            var layout = _board.Layouts.Single(item => item.Key == previous);
            await _board.SelectDesktopAsync(previous, layout.Name, layout.MonitorNumber, _lifetime.Token);
        }
    }

    private async Task ReconcileDisplayFailoverAsync()
    {
        var previousKeys = _fallbackKeys;
        var previousHosts = _fallbackHosts;
        var fallback = _board.Layouts.Where(layout =>
            layout.MonitorId is not null &&
            _desktopChoices.Any(desktop => desktop.Id == layout.DesktopId && desktop.Available) &&
            _board.GetVisibleSlots(layout.Key).Any(slot => slot.MachineId is not null) &&
            MonitorFailover.Destination(_monitorChoices, layout.Key) is not null)
            .Select(layout => layout.Key).ToHashSet();
        if (previousKeys.Count == 0 && fallback.Count == 0)
            return;
        if (_windows is null || !_windows.GetEnvironment().CanInteract)
            throw new InvalidOperationException("Windows is locked; display recovery will retry after unlock.");

        var primary = _monitorChoices.Single(monitor => monitor.Available && monitor.Primary);
        var hosts = fallback.Select(key => KeyForMonitor(key.DesktopId, primary)).ToHashSet();
        var boundsBySlot = new Dictionary<Guid, PixelRect>();
        var failures = new List<string>();
        var affectedDesktops = fallback.Concat(previousKeys).Select(key => key.DesktopId).Distinct().ToList();
        foreach (var desktopId in affectedDesktops)
        {
            var missing = fallback.Where(key => key.DesktopId == desktopId).ToList();
            var host = KeyForMonitor(desktopId, primary);
            if (missing.Count > 0)
            {
                var keys = new[] { host }.Concat(missing).Distinct()
                    .Where(_board.HasLayout).ToList();
                var placements = MonitorFailover.Combine(primary.WorkArea, [.. keys.Select(key =>
                    (key, _board.GetVisibleSlots(key)))]);
                foreach (var placement in placements)
                {
                    boundsBySlot.Add(placement.Slot.Id, placement.Bounds);
                    await ArrangeExistingAsync(placement.Key, placement.Slot, placement.Bounds, failures);
                }
                _log.Write("Monitors", $"{placements.Count} assigned slots on desktop {desktopId} " +
                    $"temporarily share {primary.Name}; saved monitor pins were not changed.");
            }
            var restore = previousKeys.Concat(previousHosts)
                .Where(key => key.DesktopId == desktopId && !missing.Contains(key) &&
                    (missing.Count == 0 || key != host) && _board.HasLayout(key) &&
                    key.MonitorId is not null &&
                    _monitorChoices.Any(monitor => monitor.Available &&
                        string.Equals(monitor.Id, key.MonitorId, StringComparison.OrdinalIgnoreCase)))
                .Distinct();
            foreach (var key in restore)
            {
                var monitor = _monitorChoices.Single(item =>
                    string.Equals(item.Id, key.MonitorId, StringComparison.OrdinalIgnoreCase));
                var slots = _board.GetVisibleSlots(key);
                var bounds = WindowLayoutGeometry.Divide(monitor.WorkArea, _board.GetLayoutMode(key));
                for (int index = 0; index < slots.Count; index++)
                    if (slots[index].MachineId is not null)
                        await ArrangeExistingAsync(key, slots[index], bounds[index], failures);
                _log.Write("Monitors", $"{LayoutName(key)} returned to its saved {monitor.Name}.");
            }
        }
        _fallbackKeys = fallback;
        _fallbackHosts = hosts;
        _fallbackBounds.Clear();
        foreach (var (slotId, bounds) in boundsBySlot)
            _fallbackBounds.Add(slotId, bounds);
        if (failures.Count > 0)
            throw new InvalidOperationException("Display recovery could not place every assigned client: " +
                string.Join("; ", failures));
    }

    private async Task ArrangeExistingAsync(LayoutKey key, SlotAssignment slot, PixelRect bounds,
        List<string> failures)
    {
        try
        {
            var runtime = _layouts[key];
            var machine = _board.GetMachine(slot.MachineId!);
            var binding = runtime.Sessions.BindExisting([(slot, machine)], _board.Settings.Machines);
            if (binding.Ambiguous.Count > 0 || binding.OtherDesktop.Count > 0)
                throw new InvalidOperationException("The client is ambiguous or on another virtual desktop.");
            if (binding.Missing.Count > 0)
                return;
            runtime.Sessions.InvalidatePlacements();
            runtime.Sessions.Observe();
            await runtime.Sessions.ArrangeAsync(slot, machine, NameIsUnique(machine.OriginalName),
                bounds, _lifetime.Token);
            if (runtime.Sessions.For(slot).Fullscreen)
                throw new InvalidOperationException("The client is fullscreen; switch it to windowed mode.");
            if (runtime.Sessions.For(slot).ReconnectPromptSuspected)
                throw new InvalidOperationException("A reconnect prompt needs manual attention; the client was not moved.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failures.Add($"{LayoutName(key)} / {slot.Name}: {ex.Message}");
        }
    }

    private async Task RefreshAvailableLayoutsAsync()
    {
        _monitorChoices = _monitors.GetMonitors();
        var available = Environment.OSVersion.Version.Build >= 26100
            ? VirtualDesktopShell.GetDesktops()
            : _desktopChoices.Where(desktop => desktop.Available).ToList();
        RefreshDesktopChoices(available);
        await EnsureMonitorLayoutsAsync();
    }

    private Task RefreshAsync() => RunAsync(async () =>
    {
        _log.Write("Discovery", _demo ? "Synthetic demo discovery started." : "Read-only discovery started.");
        if (!_demo && _windows is not null)
            await RefreshAvailableLayoutsAsync();
        var progress = new Progress<string>(message =>
        {
            if (_busy) StatusText.Text = message;
            _log.Write("Discovery", message);
        });
        await _board.RefreshAsync(ct => _demo ? Task.FromResult(DemoData.Machines()) :
            _discovery!.DiscoverAsync(progress, ct), _lifetime.Token);
        _log.Write("Discovery", $"Complete: {_board.Options.Count} known machines; saved assignments retained.");
    });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CardLayoutPicker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private async void CardLayoutChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: DesktopCardViewModel card, Tag: WindowLayoutMode mode } ||
            mode == card.Mode || !card.CanChooseLayout)
            return;
        var selected = DesktopCardViewModel.LayoutChoices.Single(choice => choice.Mode == mode);
        await RunAsync(async () =>
        {
            var previous = _board.GetVisibleSlots(card.Key);
            await _board.SetLayoutModeAsync(card.Key, selected.Mode, _lifetime.Token);
            var removed = previous.Skip(_board.GetVisibleSlots(card.Key).Count)
                .Where(slot => slot.MachineId is not null).ToList();
            // The offline demo saves the choice but has no client windows to rearrange.
            if (_layouts.TryGetValue(card.Key, out var runtime))
            {
                foreach (var slot in removed)
                    runtime.Sessions.For(_board.GetSlots(card.Key).Single(item => item.Id == slot.Id));
                runtime.PendingApply = true;
                runtime.KeepConnected.Reset();
                await ApplyVisibleLayoutAsync(card.Key, runtime);
            }
            _log.Write("Layout", $"{card.Name} · {card.MonitorName}: {selected.Name} applied automatically." +
                (removed.Count == 0 ? "" : $" {removed.Count} Dev Box assignment(s) returned to the tray."));
        });
    }

    private async Task<LayoutApplyResult?> ApplyVisibleLayoutAsync(LayoutKey key, LayoutRuntime runtime)
    {
        var visible = _board.GetVisibleSlots(key);
        var assignments = visible.Where(slot => slot.MachineId is not null)
            .Select(slot => (slot, _board.GetMachine(slot.MachineId!))).ToList();
        if (assignments.Count == 0)
        {
            runtime.PendingApply = false;
            return null;
        }
        var result = await runtime.Sessions.ApplyLayoutAsync(assignments, _board.Settings.Machines,
            _lifetime.Token, skipPendingConnections: true);
        runtime.Sessions.Observe();
        var bounds = WindowLayoutGeometry.Divide(runtime.Windows.GetEnvironment().WorkArea,
            _board.GetLayoutMode(key));
        for (int index = 0; index < visible.Count; index++)
            if (visible[index].MachineId is { } id)
            {
                var machine = _board.GetMachine(id);
                await runtime.Sessions.ArrangeAsync(visible[index], machine,
                    NameIsUnique(machine.OriginalName), bounds[index], _lifetime.Token);
            }
        runtime.PendingApply = false;
        return result;
    }

    private async void CardKeepConnected_Changed(object sender, RoutedEventArgs e)
    {
        if (_demo || sender is not CheckBox { DataContext: DesktopCardViewModel card } ||
            !card.CanEdit || !_layouts.TryGetValue(card.Key, out var runtime))
            return;
        var enabled = ((CheckBox)sender).IsChecked == true;
        if (card.KeepConnected == enabled)
            return;
        if (!enabled)
            runtime.KeepConnected.Reset();
        await RunAsync(async () =>
        {
            await _board.SetKeepConnectedAsync(card.Key, enabled, _lifetime.Token);
            if (enabled)
                runtime.KeepConnected.Reset();
            _log.Write("Keep connected",
                $"{card.Name} · {card.MonitorName}: {(enabled ? "enabled" : "disabled")}.");
        });
    }

    private async Task<LayoutApplyResult?> ReapplyCardAsync(DesktopCardViewModel card)
    {
        if (!card.CanEdit || !_layouts.TryGetValue(card.Key, out var runtime))
            throw new InvalidOperationException("The selected desktop layout is unavailable.");
        runtime.KeepConnected.Reset();
        var result = await ApplyVisibleLayoutAsync(card.Key, runtime);
        var label = $"{card.Name} · {card.MonitorName}";
        _log.Write("Window integration", result is null ? $"{label} has no assigned clients to re-apply." :
            $"{label} re-applied: {result.Bound} existing client(s) bound, " +
            $"{result.Moved} moved to this desktop, {result.ConnectionRequests} connection(s) requested.");
        return result;
    }

    private async Task<LayoutApplyResult?> ReapplyDisconnectedLayoutAsync(
        DesktopCardViewModel card, LayoutRuntime runtime)
    {
        runtime.KeepConnected.Reset();
        var result = await LayoutBatchApplier.ApplyDisconnectedAsync(
            _board, card.Key, runtime.Sessions, _lifetime.Token);
        _log.Write("Window integration", $"{card.Name} · {card.MonitorName}: " +
            $"{result.Bound} open client(s) found, {result.ConnectionRequests} connection(s) requested " +
            "for temporary placement on the primary monitor. Saved monitor pins were retained.");
        return result;
    }

    private async void CardApply_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_demo)
            throw new InvalidOperationException("Offline demo does not connect or arrange real client windows.");
        if (sender is not Button { DataContext: DesktopCardViewModel card })
            throw new InvalidOperationException("The selected desktop layout is unavailable.");
        await ReapplyCardAsync(card);
    });

    private async void ReapplyAll_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_demo || _windows is null)
            throw new InvalidOperationException("Offline demo does not connect or arrange real client windows.");
        await RefreshAvailableLayoutsAsync();
        Render();
        var assigned = Cards.Where(card => _board.HasLayout(card.Key) &&
            _board.GetVisibleSlots(card.Key).Any(slot => slot.MachineId is not null)).ToList();
        var available = assigned.Where(card => card.CanEdit && _layouts.ContainsKey(card.Key))
            .ToDictionary(card => card.Key);
        var fallback = assigned.Where(card => card.IsDisconnected &&
            _desktopChoices.Any(desktop => desktop.Id == card.DesktopId && desktop.Available) &&
            _layouts.ContainsKey(card.Key) &&
            MonitorFailover.Destination(_monitorChoices, card.Key) is not null)
            .ToDictionary(card => card.Key);
        var skipped = assigned.Where(card =>
                !available.ContainsKey(card.Key) && !fallback.ContainsKey(card.Key))
            .Select(card => $"{card.Name} · {card.MonitorName}").ToList();
        var count = available.Count + fallback.Count;
        if (count == 0)
            throw new InvalidOperationException(assigned.Count == 0
                ? "No layouts have assigned Dev Boxes to re-apply."
                : $"No assigned layouts are available. Unavailable: {string.Join(", ", skipped)}.");

        var result = await LayoutBatchApplier.ApplyAsync([.. available.Keys, .. fallback.Keys],
            key => fallback.TryGetValue(key, out var card)
                ? ReapplyDisconnectedLayoutAsync(card, _layouts[key])
                : ReapplyCardAsync(available[key]), _lifetime.Token);
        var summary = $"Re-apply all: {result.Applied}/{count} layout(s), " +
            $"{result.Bound} existing client(s) bound, {result.Moved} moved, " +
            $"{result.ConnectionRequests} connection(s) requested." +
            (skipped.Count == 0 ? "" : $" Unavailable: {string.Join(", ", skipped)}.");
        _log.Write("Window integration", summary);
        var failures = result.Failures.Select(failure =>
            $"{LayoutName(failure.Key)}: {failure.Error.Message}").ToList();
        if (fallback.Count > 0 || _fallbackKeys.Count > 0)
        {
            try { await ReconcileDisplayFailoverAsync(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(ex.Message); }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(summary + " Failed: " + string.Join("; ", failures));
    });

    private void Identify_Click(object sender, RoutedEventArgs e) => IdentifyMonitors();

    internal void IdentifyMonitors()
    {
        _operationError = null;
        try
        {
            // Always re-enumerate: a monitor may have been plugged in since the last refresh.
            _monitorChoices = _monitors.GetMonitors();
            MonitorIdentityOverlay.Show(_monitorChoices);
            _log.Write("Monitors", $"Identified {_monitorChoices.Count} monitor(s) with an on-screen number.");
            Render();
            return;
        }
        catch (Exception ex)
        {
            _operationError = $"Could not identify the monitors: {ex.Message}";
            _log.Write("Error", _operationError);
        }
        ShowError();
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow(_log) { Owner = this, ShowActivated = !_demo };
            _logWindow.Closed += (_, _) => _logWindow = null;
        }
        if (_logWindow.IsVisible) _logWindow.Hide();
        else _logWindow.Show();
    }
    private async void Window_Activated(object? sender, EventArgs e)
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (!_closeRequested)
            await UpdateSessionsAsync();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        ReleasePressedCell();
        ReleasePressedMachine();
        ClearDragTargets();
    }

    private async Task UpdateSessionsAsync()
    {
        if (_windows is null || _busy || _arranging)
            return;
        _arranging = true;
        try
        {
            foreach (var runtime in _layouts.Values.ToList())
                runtime.Sessions.Observe();
            UpdateBoardStatus();
            foreach (var runtime in _layouts.Values.ToList())
            {
                var slots = _board.GetVisibleSlots(runtime.Key).ToList();
                var card = Cards.SingleOrDefault(item => item.Key == runtime.Key);
                var temporarilyPlaced = _fallbackKeys.Contains(runtime.Key);
                if (card is null || (!card.CanEdit && !temporarilyPlaced))
                    continue;
                var bounds = WindowLayoutGeometry.Divide(runtime.Windows.GetEnvironment().WorkArea,
                    _board.GetLayoutMode(runtime.Key));
                if (runtime.PendingApply)
                {
                    foreach (var cell in card.Cells)
                    {
                        cell.Status = "Layout not applied. Resolve the error and re-apply.";
                        cell.StateText = "Layout not applied";
                        cell.StateBrush = UnappliedBrush;
                    }
                    continue;
                }
                var assignments = new List<(SlotAssignment Slot, Bevdox.Models.DevBoxInstance Machine)>();
                for (int index = 0; index < slots.Count; index++)
                {
                    var slot = slots[index];
                    if (slot.MachineId is not { } id)
                        continue;
                    var machine = _board.GetMachine(id);
                    assignments.Add((slot, machine));
                    await runtime.Sessions.ArrangeAsync(slot, machine, NameIsUnique(machine.OriginalName),
                        _fallbackBounds.GetValueOrDefault(slot.Id, bounds[index]), _lifetime.Token);
                    var cell = card.Cells[index];
                    cell.Windows = runtime.Sessions.Candidates(machine);
                    cell.Status = runtime.Sessions.For(slot).Status;
                    cell.CanConnect = !runtime.Sessions.For(slot).Connecting &&
                        _board.Options.Single(option => SameId(option.UniqueId, id)).Available;
                    UpdateCellState(cell, runtime, available: true);
                }
                if (!temporarilyPlaced)
                    await runtime.KeepConnected.TickAsync(assignments, _board.Settings.Machines,
                        _board.IsKeepConnected(runtime.Key), _lifetime.Token);
                for (int index = 0; index < slots.Count; index++)
                {
                    var cell = card.Cells[index];
                    cell.Status = runtime.Sessions.For(slots[index]).Status;
                    UpdateCellState(cell, runtime, available: true);
                }
            }
            UpdateBoardStatus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_operationError != ex.Message) _log.Write("Window integration", ex.Message);
            _operationError = ex.Message;
            ShowError();
        }
        finally
        {
            _arranging = false;
            if (_closeRequested && !_busy)
                Close();
        }
    }

    private bool NameIsUnique(string name) => _board.Settings.Machines.Count(m =>
        string.Equals(m.OriginalName, name, StringComparison.OrdinalIgnoreCase)) == 1;

    internal Task AssignDroppedMachineAsync(Guid slotId, IDataObject data)
        => AssignDroppedMachineAsync(_board.SelectedKey, slotId, data);

    internal Task AssignDroppedMachineAsync(LayoutKey? key, Guid slotId, IDataObject data)
    {
        if (data.GetData(MachineDragFormat) is not string id || !_board.Settings.Machines.Any(m => SameId(m.UniqueId, id)))
        {
            _operationError = "Drag a Dev Box from the inventory or another assigned tile.";
            ShowError();
            return Task.CompletedTask;
        }
        return SelectAsync(key, slotId, id);
    }

    private Task SelectAsync(LayoutKey? targetKey, Guid slot, string machine) => RunAsync(async () =>
    {
        var source = _board.Layouts.SelectMany(layout => _board.GetSlots(layout.Key)
            .Select(assignment => (Key: layout.Key, Slot: assignment)))
            .FirstOrDefault(entry => SameId(entry.Slot.MachineId, machine));
        var previousTarget = targetKey is { } lookup
            ? _board.GetVisibleSlots(lookup).Single(item => item.Id == slot)
            : _board.VisibleSlots.Single(item => item.Id == slot);
        var displacedId = source.Slot is null ? null : previousTarget.MachineId;
        var changed = targetKey is { } key
            ? await _board.AssignAsync(key, slot, machine, _ => true, _lifetime.Token)
            : await _board.AssignAsync(slot, machine, _ => true, _lifetime.Token);
        if (changed)
        {
            _log.Write(targetKey is { } selected
                    ? $"{LayoutName(selected)} / " + _board.GetSlots(selected).Single(s => s.Id == slot).Name
                    : _board.CurrentSlots.Single(s => s.Id == slot).Name,
                displacedId is null ? $"Assigned {_board.GetMachine(machine).EffectiveName}; stable identity saved." :
                    $"Swapped {_board.GetMachine(machine).EffectiveName} and " +
                    $"{_board.GetMachine(displacedId).EffectiveName}; both assignments saved.");
            if (_demo)
                return;
            if (targetKey is not { } target || !_layouts.TryGetValue(target, out var runtime))
                throw new InvalidOperationException("Assignment saved, but its monitor is unavailable for automatic placement.");
            if (source.Slot is not null && _layouts.TryGetValue(source.Key, out var oldRuntime))
                oldRuntime.Sessions.For(_board.GetSlots(source.Key).Single(s => s.Id == source.Slot.Id));
            var assigned = _board.GetVisibleSlots(target).Single(s => s.Id == slot);
            runtime.Sessions.For(assigned);
            var appliedWholeTarget = runtime.PendingApply;
            if (appliedWholeTarget)
                await ApplyVisibleLayoutAsync(target, runtime);
            else
                await ApplyAssignedSlotAsync(target, runtime, assigned);
            if (displacedId is not null && source.Slot is not null)
            {
                if (!_layouts.TryGetValue(source.Key, out var sourceRuntime))
                    throw new InvalidOperationException("The swap was saved, but its source monitor is unavailable for placement.");
                if (source.Key == target && appliedWholeTarget)
                    return;
                var swappedSource = _board.GetVisibleSlots(source.Key)
                    .Single(item => item.Id == source.Slot.Id);
                if (sourceRuntime.PendingApply)
                    await ApplyVisibleLayoutAsync(source.Key, sourceRuntime);
                else
                    await ApplyAssignedSlotAsync(source.Key, sourceRuntime, swappedSource);
            }
        }
    });

    private string LayoutName(LayoutKey key)
    {
        var layout = _board.Layouts.Single(item => item.Key == key);
        return SlotBoard.LayoutLabel(layout.Name, layout.MonitorNumber);
    }

    private async Task ApplyAssignedSlotAsync(LayoutKey key, LayoutRuntime runtime, SlotAssignment slot)
    {
        var machine = _board.GetMachine(slot.MachineId ??
            throw new InvalidOperationException("The selected slot no longer has an assigned Dev Box."));
        var result = await runtime.Sessions.ApplyAssignedSlotAsync(slot, machine,
            _board.Settings.Machines, _lifetime.Token);
        runtime.Sessions.Observe();
        var index = _board.GetVisibleSlots(key).ToList().FindIndex(item => item.Id == slot.Id);
        if (index < 0)
            throw new InvalidOperationException("The assigned slot is outside the selected layout.");
        var bounds = WindowLayoutGeometry.Divide(runtime.Windows.GetEnvironment().WorkArea,
            _board.GetLayoutMode(key))[index];
        await runtime.Sessions.ArrangeAsync(slot, machine, NameIsUnique(machine.OriginalName),
            bounds, _lifetime.Token);
        _log.Write("Window integration", $"Automatically placed {machine.EffectiveName}: " +
            $"{result.Moved} window(s) moved, {result.ConnectionRequests} connection(s) requested.");
    }

    private void MachineTile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: MachineOption machine } tile)
            return;
        ReleasePressedMachine();
        _pressedMachine = machine;
        _pressedInventoryTile = tile;
        tile.Tag = true;
        MachineList.SelectedItem = machine;
        _dragStart = e.GetPosition(this);
    }
    private void ReleasePressedMachine()
    {
        if (_pressedInventoryTile is { } tile)
            tile.Tag = null;
        _pressedInventoryTile = null;
        _pressedMachine = null;
    }
    private void MachineList_MouseUp(object sender, MouseButtonEventArgs e) => ReleasePressedMachine();
    private void MachineList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ReleasePressedMachine();
            return;
        }
        var point = e.GetPosition(this);
        if (_pressedMachine is { } machine &&
            (Math.Abs(point.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(point.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            try { DragDrop.DoDragDrop(MachineList, new DataObject(MachineDragFormat, machine.UniqueId), DragDropEffects.Move); }
            finally { ReleasePressedMachine(); ClearDragTargets(); }
        }
    }
    private void Cell_DragOver(object sender, DragEventArgs e)
    {
        var valid = e.Data.GetDataPresent(MachineDragFormat) &&
            e.Data.GetData(MachineDragFormat) is string id &&
            _board.Settings.Machines.Any(machine => SameId(machine.UniqueId, id));
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (sender is FrameworkElement { DataContext: CellViewModel cell })
            cell.IsDragTarget = valid;
        e.Handled = true;
    }
    private void Cell_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CellViewModel cell })
            cell.IsDragTarget = false;
        e.Handled = true;
    }
    private async void Cell_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: CellViewModel cell })
        {
            cell.IsDragTarget = false;
            await AssignDroppedMachineAsync(cell.DesktopId == Guid.Empty ? null : cell.Key,
                cell.Slot.Id, e.Data);
        }
        else
        {
            _operationError = "The target slot is unavailable.";
            ShowError();
        }
    }
    private void ClearDragTargets()
    {
        foreach (var cell in Cards.SelectMany(card => card.Cells))
            cell.IsDragTarget = false;
    }
    private void Cell_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CellViewModel { IsAssigned: true } cell } tile ||
            e.OriginalSource is DependencyObject source && IsInsideButton(source, tile))
            return;
        ReleasePressedCell();
        _dragStart = e.GetPosition(this);
        _pressedCell = cell;
        _pressedTile = tile;
        cell.IsDragSource = true;
        if (PresentationSource.FromVisual(tile) is not null)
            tile.CaptureMouse();
    }

    private static bool IsInsideButton(DependencyObject source, DependencyObject tile)
    {
        for (DependencyObject? current = source; current is not null && current != tile;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : null)
            if (current is Button)
                return true;
        return false;
    }

    private void ReleasePressedCell()
    {
        if (_pressedCell is { } cell)
            cell.IsDragSource = false;
        if (_pressedTile is not null && Mouse.Captured == _pressedTile)
            Mouse.Capture(null);
        _pressedCell = null;
        _pressedTile = null;
    }

    private void Cell_MouseUp(object sender, MouseButtonEventArgs e) => ReleasePressedCell();
    private void Cell_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CellViewModel cell } ||
            _pressedCell != cell || cell.Slot.MachineId is not { } machineId)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ReleasePressedCell();
            return;
        }
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(point.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (Mouse.Captured == sender)
                Mouse.Capture(null);
            try
            {
                DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(MachineDragFormat, machineId),
                    DragDropEffects.Move);
            }
            finally { ReleasePressedCell(); ClearDragTargets(); }
        }
    }
    private async void AssignSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CellViewModel cell } && MachineList.SelectedItem is MachineOption machine)
            await SelectAsync(cell.DesktopId == Guid.Empty ? null : cell.Key,
                cell.Slot.Id, machine.UniqueId);
        else { _operationError = "Select an unassigned Dev Box above, or drag one into this slot."; ShowError(); }
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CellViewModel cell })
            await RunAsync(() => cell.DesktopId == Guid.Empty
                ? _board.ClearSlotAsync(cell.Slot.Id, _lifetime.Token)
                : _board.ClearSlotAsync(cell.Key, cell.Slot.Id, _lifetime.Token));
    }
    private async void BindPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_demo || sender is not FrameworkElement { DataContext: CellViewModel cell } ||
            cell.Slot.MachineId is not { } id || !_layouts.TryGetValue(cell.Key, out var runtime))
        {
            _operationError = "Choose an assigned client on an available monitor before binding.";
            ShowError();
            return;
        }
        runtime.Sessions.Observe();
        var machine = _board.GetMachine(id);
        var candidates = runtime.Sessions.Candidates(machine);
        if (candidates.Count == 0)
        {
            _operationError = "No matching client window was found. Use Apply to start or move it.";
            ShowError();
            return;
        }
        var picker = new ComboBox
        {
            ItemsSource = candidates,
            DisplayMemberPath = nameof(SessionWindow.Label),
            SelectedIndex = candidates.Count == 1 ? 0 : -1,
            MinWidth = 360
        };
        var bind = new Button { Content = "Bind selected window", IsDefault = true, Margin = new Thickness(0, 10, 0, 0) };
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock
        {
            Text = $"Select the Windows App window for {machine.EffectiveName}.",
            Margin = new Thickness(0, 0, 0, 9)
        });
        content.Children.Add(picker);
        content.Children.Add(bind);
        var dialog = new Window
        {
            Owner = this, Title = "Bind existing window", Content = content,
            Width = 440, Height = 165, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        bind.Click += (_, _) =>
        {
            if (picker.SelectedItem is SessionWindow)
                dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() == true && picker.SelectedItem is SessionWindow chosen)
            await RunAsync(() =>
            {
                runtime.Sessions.Bind(cell.Slot, machine, chosen.Identity);
                return Task.CompletedTask;
            });
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_demo) { _operationError = "Offline demo never connects real machines."; ShowError(); return; }
        if (sender is FrameworkElement { DataContext: CellViewModel cell } && cell.Slot.MachineId is { } id)
            await RunAsync(async () =>
            {
                if (!_board.Options.Single(m => SameId(m.UniqueId, id)).Available)
                    throw new InvalidOperationException("Refresh machines successfully before connecting this Dev Box.");
                var machine = _board.GetMachine(id);
                await (_layouts.TryGetValue(cell.Key, out var runtime)
                    ? runtime.Sessions : throw new InvalidOperationException("The monitor layout is unavailable."))
                    .ConnectAsync(cell.Slot, machine, NameIsUnique(machine.OriginalName), _lifetime.Token);
            });
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy || _arranging)
        {
            e.Cancel = true;
            _closeRequested = true;
            _lifetime.Cancel();
        }
        else if (_sessionNotifications)
        {
            if (SessionNotifications.Unregister(new WindowInteropHelper(this).Handle) == 0)
            {
                _operationError = $"Could not unregister Windows session notifications: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
                ShowError();
            }
            _sessionNotifications = false;
        }
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        _closeRequested = true;
        _timer.Stop();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _trayMenu?.Dispose();
        _trayIcon?.Dispose();
        foreach (var runtime in _layouts.Values)
        {
            runtime.KeepConnected.Dispose();
            runtime.Sessions.Dispose();
        }
        _layouts.Clear();
        _source?.RemoveHook(SessionMessage);
        _windows?.Dispose();
        _lifetime.Dispose();
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}

internal static partial class SessionNotifications
{
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSRegisterSessionNotification", SetLastError = true)]
    internal static partial int Register(nint hwnd, uint flags = 0);
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSUnRegisterSessionNotification", SetLastError = true)]
    internal static partial int Unregister(nint hwnd);
}
