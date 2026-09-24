using System.ComponentModel;
using System.Diagnostics;
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

namespace Boxboard;

public partial class MainWindow : Window
{
    internal const string MachineDragFormat = "Boxboard.DevBoxIdentity";
    private readonly SlotBoard _board;
    private readonly bool _demo;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DevBoxDiscovery? _discovery;
    private readonly DevBoxLauncher? _launcher;
    private NativeSessionWindows? _windows;
    private readonly Dictionary<Guid, LayoutRuntime> _layouts = [];
    private IReadOnlyList<VirtualDesktopInfo> _desktopChoices = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private bool _busy, _arranging, _closeRequested, _allowMove;
    private (Guid? DesktopId, Guid Slot, string Machine)? _pendingMove;
    private string? _operationError;
    private Point _dragStart;
    private HwndSource? _source;
    private bool _sessionNotifications;
    private Window? _movePrompt;
    private readonly ActivityLog _log;
    private LogWindow? _logWindow;

    private sealed record LayoutRuntime(Guid DesktopId, TargetSessionWindows Windows,
        SessionCoordinator Sessions, KeepConnectedController KeepConnected)
    {
        public bool PendingApply { get; set; }
    }
    private static readonly Brush OpenBrush = new SolidColorBrush(Color.FromRgb(57, 164, 107));
    private static readonly Brush AttentionBrush = new SolidColorBrush(Color.FromRgb(232, 170, 60));
    private static readonly Brush MissingBrush = new SolidColorBrush(Color.FromRgb(157, 168, 174));
    private static readonly Brush UnappliedBrush = new SolidColorBrush(Color.FromRgb(118, 83, 163));

    public MainWindow(SlotBoard board, bool demo, string path)
    {
        InitializeComponent();
        _board = board;
        _demo = demo;
        _log = new ActivityLog(Dispatcher);
        if (!demo)
        {
            var auth = new AuthService();
            _discovery = new DevBoxDiscovery(auth);
            _launcher = new DevBoxLauncher(auth);
        }
        Title = demo ? "Boxboard - OFFLINE DEMO" : "Boxboard";
        ToolTip = $"Settings: {path}";
        _log.Write("Boxboard", demo ? "Offline demo opened; no real clients will be controlled." :
            "Board opened. Keep connected may request missing assigned clients when enabled.");
        Render();
    }

    internal IReadOnlyList<DesktopCardViewModel> Cards =>
        (IReadOnlyList<DesktopCardViewModel>)DesktopCards.ItemsSource;
    internal IReadOnlyList<CellViewModel> Cells => Cards.SelectMany(card => card.Cells).ToList();

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
                ? _board.Layouts.Select((layout, index) =>
                    new VirtualDesktopInfo(index + 1, layout.DesktopId, layout.Name)).ToList()
                : [new VirtualDesktopInfo(1, Guid.Empty, "Desktop 1")];
        var scroll = DesktopScroll.VerticalOffset;
        var cards = desktops.OrderBy(desktop => desktop.Id == _board.Settings.PrimaryDesktopId ? 0 : 1)
            .ThenBy(desktop => desktop.Number).Select(desktop =>
        {
            var primaryDemo = desktop.Id == Guid.Empty && _board.Settings.PrimaryDesktopId is null;
            var slots = primaryDemo ? _board.CurrentSlots : _board.GetSlots(desktop.Id);
            var visible = primaryDemo ? _board.VisibleSlots : _board.GetVisibleSlots(desktop.Id);
            var mode = primaryDemo ? _board.LayoutMode : _board.GetLayoutMode(desktop.Id);
            _layouts.TryGetValue(desktop.Id, out var runtime);
            var cells = visible.Select(slot =>
            {
                var machine = options.SingleOrDefault(option => SameId(option.UniqueId, slot.MachineId));
                var cell = new CellViewModel
                {
                    DesktopId = desktop.Id, Slot = slot,
                    MachineName = slot.MachineId is null ? "Unassigned" :
                        _board.GetMachine(slot.MachineId).EffectiveName,
                    MachineDetails = machine?.Details ?? "Drop a Dev Box here.",
                    CanBind = !_demo && desktop.Available && runtime is not null && machine is not null,
                    CanConnect = !_demo && desktop.Available && runtime?.PendingApply != true &&
                        machine?.Available == true && runtime?.Sessions.For(slot).Connecting != true,
                    Status = _demo ? "Demo only: no remote desktop is connected." :
                        runtime?.Sessions.For(slot).Status ?? "No client window is bound."
                };
                UpdateCellState(cell, runtime, desktop.Available);
                return cell;
            }).ToList();
            var hidden = slots.Skip(visible.Count).Where(slot => slot.MachineId is not null)
                .Select(slot => _board.GetMachine(slot.MachineId!).EffectiveName).ToList();
            return new DesktopCardViewModel
            {
                DesktopId = desktop.Id, Name = desktop.Name, Mode = mode,
                KeepConnected = primaryDemo ? false : _board.IsKeepConnected(desktop.Id),
                CanEdit = !_demo && desktop.Available && runtime is not null,
                PendingApply = runtime?.PendingApply == true,
                Cells = cells,
                HiddenAssignments = hidden.Count == 0 ? "" :
                    $"Saved outside this layout: {string.Join(", ", hidden)}."
            };
        }).ToList();
        DesktopCards.ItemsSource = cards;
        Dispatcher.BeginInvoke(() => DesktopScroll.ScrollToVerticalOffset(scroll), DispatcherPriority.Loaded);
        UpdateBoardStatus();
        ShowError();
    }

    private void UpdateCellState(CellViewModel cell, LayoutRuntime? runtime, bool available)
    {
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
        else
        {
            var refresh = _board.Settings.LastRefreshUtc is { } time ? $" Last discovery: {time.ToLocalTime():g}." : "";
            StatusText.Text = $"{_board.Layouts.Count} desktops · {_board.Options.Count} known Dev Boxes.{refresh}";
        }
    }

    private void ShowError()
    {
        var error = _operationError ?? _board.RefreshError;
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        DesktopCards.IsEnabled = MachineList.IsEnabled = RefreshButton.IsEnabled = false;
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
            DesktopCards.IsEnabled = MachineList.IsEnabled = RefreshButton.IsEnabled = _pendingMove is null;
            if (_closeRequested)
                Close();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_demo)
        {
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
            await _board.SelectDesktopAsync(initialChoice.Id, initialChoice.Name, _lifetime.Token);
            if (!hadPrimaryDesktop)
                _log.Write("Desktop", $"Linked the legacy slot assignments to {initialChoice.Name} ({initialChoice.Id}).");
            await _board.EnsureFourCellsAsync(_lifetime.Token);
            foreach (var choice in _desktopChoices.Where(choice => choice.Available && choice.Id != initialChoice.Id))
            {
                await _board.SelectDesktopAsync(choice.Id, choice.Name, _lifetime.Token);
                await _board.EnsureFourCellsAsync(_lifetime.Token);
            }
            await _board.SelectDesktopAsync(initialChoice.Id, initialChoice.Name, _lifetime.Token);
            RefreshDesktopChoices(_desktopChoices);
            foreach (var saved in _board.Layouts)
                if (_desktopChoices.Any(choice => choice.Id == saved.DesktopId && choice.Available))
                    GetOrCreateLayout(saved.DesktopId);
            Render();
            _source = HwndSource.FromHwnd(handle);
            _source.AddHook(SessionMessage);
            if (SessionNotifications.Register(handle) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            _sessionNotifications = true;
            _log.Write("Window integration", "Monitoring msrdc window metadata. Only explicitly bound or requested replacement windows can be arranged.");
            _timer.Tick += async (_, _) => await UpdateSessionsAsync();
            _timer.Start();
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

    private LayoutRuntime GetOrCreateLayout(Guid desktopId)
    {
        if (_layouts.TryGetValue(desktopId, out var existing))
            return existing;
        var manager = _windows ?? throw new InvalidOperationException("Window integration is unavailable.");
        var assignedNames = _board.GetSlots(desktopId).Where(slot => slot.MachineId is not null)
            .Select(slot => _board.GetMachine(slot.MachineId!).OriginalName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchor = manager.Enumerate().FirstOrDefault(window =>
            window.DesktopId == desktopId && assignedNames.Contains(window.Title));
        PixelRect area;
        if (anchor is null)
            area = manager.GetEnvironment().WorkArea;
        else
        {
            using var target = new NativeSessionWindows(anchor.Identity.Handle);
            area = target.GetEnvironment().WorkArea;
        }
        var targetWindows = new TargetSessionWindows(manager, desktopId, area,
            VirtualDesktopShell.GetCurrentDesktopId,
            async (window, bounds, ct) =>
            {
                using var client = new NativeSessionWindows(window.Identity.Handle);
                var environment = client.GetEnvironment();
                if (environment.DesktopId != desktopId || environment.WorkArea != area)
                    throw new InvalidOperationException("The selected client moved to another desktop or monitor.");
                await client.PlaceAsync(window, bounds, ct);
            });
        var sessions = new SessionCoordinator(targetWindows,
            (machine, ct) => _launcher!.GetWindowsAppConnectionUriAsync(machine, ct),
            uri => Process.Start(new ProcessStartInfo(uri.OriginalString) { UseShellExecute = true }),
            moveToDesktop: VirtualDesktopShell.MoveAssignedWindow);
        sessions.Activity += (slot, message) => _log.Write(
            $"{slot.Name} / {_board.Settings.Machines.SingleOrDefault(m => SameId(m.UniqueId, slot.MachineId))?.EffectiveName ?? "unassigned"}",
            message);
        var runtime = new LayoutRuntime(desktopId, targetWindows, sessions,
            new KeepConnectedController(targetWindows, sessions));
        _layouts.Add(desktopId, runtime);
        return runtime;
    }

    private void RefreshDesktopChoices(IReadOnlyList<VirtualDesktopInfo> available)
    {
        _desktopChoices = [.. available, .. _board.Layouts
            .Where(layout => available.All(desktop => desktop.Id != layout.DesktopId))
            .Select(layout => new VirtualDesktopInfo(0, layout.DesktopId, $"{layout.Name} (missing)", false))];
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
        return 0;
    }

    private Task RefreshAsync() => RunAsync(async () =>
    {
        _log.Write("Discovery", _demo ? "Synthetic demo discovery started." : "Read-only discovery started.");
        if (!_demo && _windows is not null && Environment.OSVersion.Version.Build >= 26100)
        {
            var available = VirtualDesktopShell.GetDesktops();
            var selected = _board.SelectedDesktopId;
            foreach (var desktop in available.Where(desktop =>
                         _board.Layouts.All(layout => layout.DesktopId != desktop.Id)))
            {
                await _board.SelectDesktopAsync(desktop.Id, desktop.Name, _lifetime.Token);
                await _board.EnsureFourCellsAsync(_lifetime.Token);
                GetOrCreateLayout(desktop.Id);
            }
            if (selected is { } id)
                await _board.SelectDesktopAsync(id, _board.Layouts.Single(layout => layout.DesktopId == id).Name,
                    _lifetime.Token);
            RefreshDesktopChoices(available);
        }
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

    private async void CardLayout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_demo || sender is not ComboBox { DataContext: DesktopCardViewModel card } ||
            e.AddedItems.OfType<WindowLayoutChoice>().SingleOrDefault() is not { } selected ||
            selected.Mode == card.Mode || !card.CanEdit)
            return;
        await RunAsync(async () =>
        {
            var previous = _board.GetVisibleSlots(card.DesktopId);
            await _board.SetLayoutModeAsync(card.DesktopId, selected.Mode, _lifetime.Token);
            var runtime = _layouts[card.DesktopId];
            var removed = previous.Skip(_board.GetVisibleSlots(card.DesktopId).Count)
                .Where(slot => slot.MachineId is not null).ToList();
            foreach (var slot in removed)
                runtime.Sessions.For(_board.GetSlots(card.DesktopId).Single(item => item.Id == slot.Id));
            runtime.PendingApply = true;
            runtime.KeepConnected.Reset();
            await ApplyVisibleLayoutAsync(card.DesktopId, runtime);
            _log.Write("Layout", $"{card.Name}: {selected.Name} applied automatically." +
                (removed.Count == 0 ? "" : $" {removed.Count} Dev Box assignment(s) returned to the tray."));
        });
    }

    private async Task<LayoutApplyResult?> ApplyVisibleLayoutAsync(Guid desktopId, LayoutRuntime runtime)
    {
        var visible = _board.GetVisibleSlots(desktopId);
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
            _board.GetLayoutMode(desktopId));
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
            !card.CanEdit || !_layouts.TryGetValue(card.DesktopId, out var runtime))
            return;
        var enabled = ((CheckBox)sender).IsChecked == true;
        if (card.KeepConnected == enabled)
            return;
        if (!enabled)
            runtime.KeepConnected.Reset();
        await RunAsync(async () =>
        {
            await _board.SetKeepConnectedAsync(card.DesktopId, enabled, _lifetime.Token);
            if (enabled)
                runtime.KeepConnected.Reset();
            _log.Write("Keep connected", $"{card.Name}: {(enabled ? "enabled" : "disabled")}.");
        });
    }

    private async void CardApply_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_demo)
            throw new InvalidOperationException("Offline demo does not connect or arrange real client windows.");
        if (sender is not Button { DataContext: DesktopCardViewModel card } || !card.CanEdit ||
            !_layouts.TryGetValue(card.DesktopId, out var runtime))
            throw new InvalidOperationException("The selected desktop layout is unavailable.");
        runtime.KeepConnected.Reset();
        var result = await ApplyVisibleLayoutAsync(card.DesktopId, runtime);
        _log.Write("Window integration", result is null ? $"{card.Name} has no assigned clients to re-apply." :
            $"{card.Name} re-applied: {result.Bound} existing client(s) bound, " +
            $"{result.Moved} moved to this desktop, {result.ConnectionRequests} connection(s) requested.");
    });
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

    private async Task UpdateSessionsAsync()
    {
        if (_windows is null || _busy || _arranging ||
            _pendingMove is not null)
            return;
        _arranging = true;
        try
        {
            foreach (var runtime in _layouts.Values.ToList())
                runtime.Sessions.Observe();
            UpdateBoardStatus();
            foreach (var runtime in _layouts.Values.ToList())
            {
                var slots = _board.GetVisibleSlots(runtime.DesktopId).ToList();
                var card = Cards.SingleOrDefault(item => item.DesktopId == runtime.DesktopId);
                if (card is null || !card.CanEdit)
                    continue;
                var bounds = WindowLayoutGeometry.Divide(runtime.Windows.GetEnvironment().WorkArea,
                    _board.GetLayoutMode(runtime.DesktopId));
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
                        bounds[index], _lifetime.Token);
                    var cell = card.Cells[index];
                    cell.Windows = runtime.Sessions.Candidates(machine);
                    cell.Status = runtime.Sessions.For(slot).Status;
                    cell.CanConnect = !runtime.Sessions.For(slot).Connecting &&
                        _board.Options.Single(option => SameId(option.UniqueId, id)).Available;
                    UpdateCellState(cell, runtime, available: true);
                }
                await runtime.KeepConnected.TickAsync(assignments, _board.Settings.Machines,
                    _board.IsKeepConnected(runtime.DesktopId), _lifetime.Token);
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
        => AssignDroppedMachineAsync(_board.SelectedDesktopId, slotId, data);

    internal Task AssignDroppedMachineAsync(Guid? desktopId, Guid slotId, IDataObject data)
    {
        if (data.GetData(MachineDragFormat) is not string id || !_board.Settings.Machines.Any(m => SameId(m.UniqueId, id)))
        {
            _operationError = "Drag a Dev Box from the inventory or another assigned tile.";
            ShowError();
            return Task.CompletedTask;
        }
        return SelectAsync(desktopId, slotId, id);
    }

    private Task SelectAsync(Guid? desktopId, Guid slot, string machine) => RunAsync(async () =>
    {
        var source = _board.Layouts.SelectMany(layout => _board.GetSlots(layout.DesktopId)
            .Select(assignment => (layout.DesktopId, Slot: assignment)))
            .FirstOrDefault(entry => SameId(entry.Slot.MachineId, machine));
        bool Confirm(MoveRequest request)
        {
            if (_allowMove) return true;
            _pendingMove = (desktopId, slot, machine);
            MoveText.Text = $"Move {request.MachineName} from {request.SourceSlot} to {request.TargetSlot}?\n\n" +
                $"{request.SourceSlot} will become unassigned." +
                (request.ReplacedMachine is null ? "" : $"\n{request.ReplacedMachine} will be unassigned from {request.TargetSlot}.");
            ShowMovePrompt();
            return false;
        }
        var changed = desktopId is { } id
            ? await _board.AssignAsync(id, slot, machine, Confirm, _lifetime.Token)
            : await _board.AssignAsync(slot, machine, Confirm, _lifetime.Token);
        if (changed)
        {
            _log.Write(desktopId is { } selected
                    ? $"{_board.Layouts.Single(layout => layout.DesktopId == selected).Name} / " +
                      _board.GetSlots(selected).Single(s => s.Id == slot).Name
                    : _board.CurrentSlots.Single(s => s.Id == slot).Name,
                $"Assigned {_board.GetMachine(machine).EffectiveName}; stable identity saved.");
            if (_demo)
                return;
            if (desktopId is not { } target || !_layouts.TryGetValue(target, out var runtime))
                throw new InvalidOperationException("Assignment saved, but its desktop is unavailable for automatic placement.");
            if (source.Slot is not null && _layouts.TryGetValue(source.DesktopId, out var oldRuntime))
                oldRuntime.Sessions.For(_board.GetSlots(source.DesktopId).Single(s => s.Id == source.Slot.Id));
            if (runtime.PendingApply)
            {
                await ApplyVisibleLayoutAsync(target, runtime);
                return;
            }
            var assigned = _board.GetVisibleSlots(target).Single(s => s.Id == slot);
            var instance = _board.GetMachine(machine);
            var result = await runtime.Sessions.ApplyAssignedSlotAsync(assigned, instance,
                _board.Settings.Machines, _lifetime.Token);
            runtime.Sessions.Observe();
            var index = _board.GetVisibleSlots(target).ToList().FindIndex(s => s.Id == slot);
            var bounds = WindowLayoutGeometry.Divide(runtime.Windows.GetEnvironment().WorkArea,
                _board.GetLayoutMode(target))[index];
            await runtime.Sessions.ArrangeAsync(assigned, instance, NameIsUnique(instance.OriginalName),
                bounds, _lifetime.Token);
            _log.Write("Window integration", $"Automatically placed {instance.EffectiveName}: " +
                $"{result.Moved} window(s) moved, {result.ConnectionRequests} connection(s) requested.");
        }
    });

    private void MachineList_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);
    private void MachineList_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (e.LeftButton == MouseButtonState.Pressed && MachineList.SelectedItem is MachineOption machine &&
            (Math.Abs(point.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(point.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance))
            DragDrop.DoDragDrop(MachineList, new DataObject(MachineDragFormat, machine.UniqueId), DragDropEffects.Move);
    }
    private void Cell_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(MachineDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Cell_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: CellViewModel cell })
            await AssignDroppedMachineAsync(cell.DesktopId == Guid.Empty ? null : cell.DesktopId,
                cell.Slot.Id, e.Data);
    }
    private void Cell_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);
    private void Cell_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CellViewModel cell } ||
            cell.Slot.MachineId is not { } machineId || e.LeftButton != MouseButtonState.Pressed)
            return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(point.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(MachineDragFormat, machineId),
                DragDropEffects.Move);
    }
    private async void AssignSelected_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CellViewModel cell } && MachineList.SelectedItem is MachineOption machine)
            await SelectAsync(cell.DesktopId == Guid.Empty ? null : cell.DesktopId,
                cell.Slot.Id, machine.UniqueId);
        else { _operationError = "Select an unassigned Dev Box above, or drag one into this slot."; ShowError(); }
    }
    private void CancelMove_Click(object sender, RoutedEventArgs e)
    {
        _pendingMove = null;
        CloseMovePrompt();
        DesktopCards.IsEnabled = MachineList.IsEnabled = RefreshButton.IsEnabled = true;
        Render();
    }
    private void MoveOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelMove_Click(sender, e); e.Handled = true; }
    }
    private async void ConfirmMove_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingMove is not { } pending) return;
        _pendingMove = null;
        CloseMovePrompt();
        _allowMove = true;
        try { await SelectAsync(pending.DesktopId, pending.Slot, pending.Machine); }
        finally { _allowMove = false; }
    }

    private void ShowMovePrompt()
    {
        ((Grid)Content).Children.Remove(MoveOverlay);
        MoveOverlay.Visibility = Visibility.Visible;
        _movePrompt = new Window
        {
            Owner = this, Title = "Move Boxboard assignment?", Content = MoveOverlay,
            Width = 620, Height = 350, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = !_demo, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        _movePrompt.Closed += (_, _) =>
        {
            if (_pendingMove is not null)
                CancelMove_Click(this, new RoutedEventArgs());
        };
        _movePrompt.Show();
        CancelMoveButton.Focus();
    }

    private void CloseMovePrompt()
    {
        var prompt = _movePrompt;
        _movePrompt = null;
        if (prompt is not null)
        {
            prompt.Content = null;
            prompt.Close();
        }
        MoveOverlay.Visibility = Visibility.Collapsed;
        if (MoveOverlay.Parent is null)
            ((Grid)Content).Children.Add(MoveOverlay);
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CellViewModel cell })
            await RunAsync(() => cell.DesktopId == Guid.Empty
                ? _board.ClearSlotAsync(cell.Slot.Id, _lifetime.Token)
                : _board.ClearSlotAsync(cell.DesktopId, cell.Slot.Id, _lifetime.Token));
    }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
    private async void BindPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_demo || sender is not FrameworkElement { DataContext: CellViewModel cell } ||
            cell.Slot.MachineId is not { } id || !_layouts.TryGetValue(cell.DesktopId, out var runtime))
        {
            _operationError = "Choose an assigned client on an available desktop before binding.";
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
                await (_layouts.TryGetValue(cell.DesktopId, out var runtime)
                    ? runtime.Sessions : throw new InvalidOperationException("The desktop layout is unavailable."))
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
