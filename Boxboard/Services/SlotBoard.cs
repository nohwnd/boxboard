using Bevdox.Models;
using Boxboard.Models;
using static Boxboard.Models.BoardSettings;

namespace Boxboard.Services;

public sealed class SlotBoard(ISettingsStore store)
{
    private readonly SemaphoreSlim _changes = new(1, 1);
    private HashSet<string> _available = new(StringComparer.OrdinalIgnoreCase);
    private LayoutKey? _selectedKey;
    public BoardSettings Settings { get; private set; } = new();
    public bool DiscoveryVerified { get; private set; }
    public string? RefreshError { get; private set; }
    public LayoutKey? SelectedKey => _selectedKey;
    public Guid? SelectedDesktopId => _selectedKey?.DesktopId;
    public IReadOnlyList<SlotAssignment> CurrentSlots => SlotsFor(Settings);
    public bool KeepConnected => _selectedKey is { } key && IsKeepConnected(key);
    public WindowLayoutMode LayoutMode => CurrentLayout(Settings)?.LayoutMode ?? Settings.PrimaryLayoutMode;
    public IReadOnlyList<SlotAssignment> VisibleSlots => CurrentSlots.Take(VisibleCount(LayoutMode)).ToList();
    public IReadOnlyList<SlotAssignment> GetVisibleSlots(LayoutKey key) =>
        GetSlots(key).Take(VisibleCount(GetLayoutMode(key))).ToList();
    public WindowLayoutMode GetLayoutMode(LayoutKey key) =>
        Settings.PrimaryKey == key ? Settings.PrimaryLayoutMode : Layout(key).LayoutMode;
    public bool IsKeepConnected(LayoutKey key) =>
        Settings.PrimaryKey == key ? Settings.PrimaryKeepConnected : Layout(key).KeepConnected;
    public IReadOnlyList<SlotAssignment> GetSlots(LayoutKey key) =>
        Settings.PrimaryKey == key ? Settings.Slots : Layout(key).Slots;
    public bool HasLayout(LayoutKey key) =>
        Settings.PrimaryKey == key || Settings.DesktopLayouts.Any(layout => layout.Key == key);
    public IReadOnlyList<DesktopLayoutOption> Layouts => Settings.PrimaryKey is { } primary
        ? [new(primary, Settings.PrimaryDesktopName, Settings.PrimaryMonitorNumber),
            .. Settings.DesktopLayouts.Select(layout =>
                new DesktopLayoutOption(layout.Key, layout.Name, layout.MonitorNumber))]
        : [];

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var settings = await store.LoadAsync(ct);
        settings.Validate();
        Settings = settings;
        _selectedKey = settings.PrimaryKey;
    }

    public async Task SelectDesktopAsync(LayoutKey key, string name, int monitorNumber = 0,
        CancellationToken ct = default)
    {
        if (key.DesktopId == Guid.Empty || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A desktop layout needs a nonempty identity and name.");
        if (key.MonitorId is null != (monitorNumber == 0))
            throw new ArgumentException("A pinned layout needs both a monitor identity and its number.");
        await _changes.WaitAsync(ct);
        try
        {
            var version = key.MonitorId is null ? 3 : 5;
            if (Settings.PrimaryDesktopId is null)
                await CommitAsync(Settings with
                {
                    Version = Math.Max(Settings.Version, version),
                    PrimaryDesktopId = key.DesktopId,
                    PrimaryDesktopName = name,
                    PrimaryMonitorId = key.MonitorId,
                    PrimaryMonitorNumber = monitorNumber
                }, ct);
            else if (!HasLayout(key))
            {
                var layout = new DesktopLayout(key.DesktopId, name, 5,
                    [.. Enumerable.Range(1, 4).Select(number => new SlotAssignment(Guid.NewGuid(), number))])
                {
                    MonitorId = key.MonitorId,
                    MonitorNumber = monitorNumber
                };
                await CommitAsync(Settings with
                {
                    Version = Math.Max(Settings.Version, version),
                    DesktopLayouts = [.. Settings.DesktopLayouts, layout]
                }, ct);
            }
            else if (Settings.Version < version)
                await CommitAsync(Settings with { Version = version }, ct);
            _selectedKey = key;
        }
        finally { _changes.Release(); }
    }

    /// <summary>
    /// Pins a layout saved before monitor pinning existed to a monitor, keeping its slots and assignments.
    /// </summary>
    public async Task PinLayoutAsync(LayoutKey key, string monitorId, int monitorNumber,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(monitorId) || monitorNumber < 1)
            throw new ArgumentException("A monitor pin needs a nonempty identity and a positive number.");
        if (key.MonitorId is not null)
            throw new InvalidOperationException("That layout is already pinned to a monitor.");
        if (!HasLayout(key))
            throw new InvalidOperationException("The layout to pin no longer exists.");
        await ChangeAsync(settings =>
        {
            var pinned = settings.PrimaryKey == key
                ? settings with { PrimaryMonitorId = monitorId, PrimaryMonitorNumber = monitorNumber }
                : settings with
                {
                    DesktopLayouts = settings.DesktopLayouts.Select(layout => layout.Key == key
                        ? layout with { MonitorId = monitorId, MonitorNumber = monitorNumber }
                        : layout).ToList()
                };
            return pinned with { Version = Math.Max(pinned.Version, 5) };
        }, ct);
        if (_selectedKey == key)
            _selectedKey = new LayoutKey(key.DesktopId, monitorId);
    }

    public Task SetKeepConnectedAsync(bool enabled, CancellationToken ct = default)
    {
        if (_selectedKey is not { } selected)
            throw new InvalidOperationException("Select a desktop layout before changing Keep connected.");
        return SetKeepConnectedAsync(selected, enabled, ct);
    }

    public Task SetKeepConnectedAsync(LayoutKey key, bool enabled, CancellationToken ct = default)
    {
        if (IsKeepConnected(key) == enabled && Settings.Version >= 3)
            return Task.CompletedTask;
        return ChangeAsync(settings => settings.PrimaryKey == key
            ? settings with
            {
                Version = Math.Max(settings.Version, 3),
                PrimaryKeepConnected = enabled
            }
            : settings with
            {
                Version = Math.Max(settings.Version, 3),
                DesktopLayouts = settings.DesktopLayouts.Select(layout => layout.Key == key
                    ? layout with { KeepConnected = enabled } : layout).ToList()
            }, ct);
    }

    public Task SetLayoutModeAsync(WindowLayoutMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (_selectedKey is not { } selected)
            throw new InvalidOperationException("Select a desktop layout before changing its window arrangement.");
        return SetLayoutModeAsync(selected, mode, ct);
    }

    public Task SetLayoutModeAsync(LayoutKey key, WindowLayoutMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (GetLayoutMode(key) == mode)
            return Task.CompletedTask;
        return ChangeAsync(settings =>
        {
            var previousMode = settings.PrimaryKey == key ? settings.PrimaryLayoutMode :
                settings.DesktopLayouts.Single(layout => layout.Key == key).LayoutMode;
            var oldCount = VisibleCount(previousMode);
            var newCount = VisibleCount(mode);
            var slots = SlotsFor(settings, key).Select((slot, index) =>
                index >= newCount && index < oldCount ? slot with { MachineId = null } : slot).ToList();
            return settings.PrimaryKey == key
                ? settings with { Version = Math.Max(settings.Version, 4), PrimaryLayoutMode = mode, Slots = slots }
                : settings with
                {
                    Version = Math.Max(settings.Version, 4),
                    DesktopLayouts = settings.DesktopLayouts.Select(layout => layout.Key == key
                        ? layout with { LayoutMode = mode, Slots = slots } : layout).ToList()
                };
        }, ct);
    }

    private static int VisibleCount(WindowLayoutMode mode) => mode switch
    {
        WindowLayoutMode.Quadrants => 4,
        WindowLayoutMode.SideBySide => 2,
        WindowLayoutMode.LargeLeftTwoStackedRight => 3,
        WindowLayoutMode.SingleWindow => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public IReadOnlyList<MachineOption> Options => Settings.Machines
        .OrderBy(m => m.EffectiveName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(m => m.ProjectName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(m => m.UniqueId, StringComparer.OrdinalIgnoreCase)
        .Select(m =>
        {
            var assigned = AssignmentFor(m.UniqueId);
            var name = m.EffectiveName;
            if (Settings.Machines.Count(other => string.Equals(other.EffectiveName, name, StringComparison.OrdinalIgnoreCase)) > 1)
                name += $" [{m.ProjectName} | {m.DevCenterUri}]";
            var label = assigned is null ? name : $"{name} (Assigned to {assigned.Value.Label})";
            if (DiscoveryVerified && !_available.Contains(m.UniqueId))
                label += " - Unavailable";
            var state = !DiscoveryVerified ? "Not verified this session" :
                RefreshError is not null ? "Not verified: refresh failed" :
                !_available.Contains(m.UniqueId) ? "Unavailable in latest discovery" :
                $"Power: {m.State} (not connection state)";
            return new MachineOption(m.UniqueId, label, $"{m.ProjectName} | {m.Location} | {state}",
                DiscoveryVerified && RefreshError is null && _available.Contains(m.UniqueId));
        }).ToList();

    public DevBoxInstance GetMachine(string id) => Settings.Machines.Single(m => SameId(m.UniqueId, id));

    public Task EnsureFourCellsAsync(CancellationToken ct = default) => CurrentSlots.Count >= 4
        ? Task.CompletedTask
        : ChangeAsync(settings =>
        {
            var slots = SlotsFor(settings).ToList();
            var next = CurrentLayout(settings)?.NextSlotNumber ?? settings.NextSlotNumber;
            while (slots.Count < 4)
                slots.Add(new(Guid.NewGuid(), next++));
            return ReplaceSelected(settings, slots, next);
        }, ct);

    public Task AddSlotAsync(CancellationToken ct = default) => ChangeAsync(settings =>
    {
        var next = CurrentLayout(settings)?.NextSlotNumber ?? settings.NextSlotNumber;
        return ReplaceSelected(settings, [.. SlotsFor(settings), new(Guid.NewGuid(), next)], checked(next + 1));
    }, ct);

    public Task RemoveSlotAsync(Guid slotId, CancellationToken ct = default) => ChangeAsync(settings =>
    {
        var slots = SlotsFor(settings);
        _ = slots.Single(s => s.Id == slotId);
        return ReplaceSelected(settings, slots.Where(s => s.Id != slotId).ToList());
    }, ct);

    public Task ClearSlotAsync(Guid slotId, CancellationToken ct = default) => ChangeAsync(settings =>
    {
        var slots = SlotsFor(settings);
        _ = slots.Single(s => s.Id == slotId);
        return ReplaceSelected(settings, slots.Select(s => s.Id == slotId ? s with { MachineId = null } : s).ToList());
    }, ct);

    public Task ClearSlotAsync(LayoutKey key, Guid slotId, CancellationToken ct = default) => ChangeAsync(settings =>
    {
        var slots = SlotsFor(settings, key);
        _ = slots.Single(s => s.Id == slotId);
        return ReplaceSelected(settings, slots.Select(s => s.Id == slotId ? s with { MachineId = null } : s).ToList(),
            key: key);
    }, ct);

    public Task<bool> AssignAsync(Guid slotId, string machineId, Func<MoveRequest, bool> confirmMove,
        CancellationToken ct = default) =>
        AssignCoreAsync(_selectedKey, slotId, machineId, confirmMove, ct);

    public Task<bool> AssignAsync(LayoutKey key, Guid slotId, string machineId, Func<MoveRequest, bool> confirmMove,
        CancellationToken ct = default) =>
        AssignCoreAsync(key, slotId, machineId, confirmMove, ct);

    private async Task<bool> AssignCoreAsync(LayoutKey? key, Guid slotId, string machineId,
        Func<MoveRequest, bool> confirmMove, CancellationToken ct)
    {
        await _changes.WaitAsync(ct);
        try
        {
            var target = (key is { } layout ? SlotsFor(Settings, layout) : CurrentSlots)
                .Single(s => s.Id == slotId);
            var machine = GetMachine(machineId);
            if (SameId(target.MachineId, machine.UniqueId))
                return false;
            var source = AllSlots(Settings).SingleOrDefault(s => SameId(s.MachineId, machine.UniqueId));
            if (source is not null && !confirmMove(new(machine.EffectiveName,
                DescribeSlot(Settings, source.Id), DescribeSlot(Settings, target.Id),
                target.MachineId is null ? null : GetMachine(target.MachineId).EffectiveName)))
                return false;

            var displacedMachineId = source is null ? null : target.MachineId;
            SlotAssignment SetAssignment(SlotAssignment slot) => slot.Id == target.Id
                ? slot with { MachineId = machine.UniqueId }
                : slot.Id == source?.Id ? slot with { MachineId = displacedMachineId } : slot;
            await CommitAsync(Settings with
            {
                Slots = Settings.Slots.Select(SetAssignment).ToList(),
                DesktopLayouts = Settings.DesktopLayouts.Select(layout =>
                    layout with { Slots = layout.Slots.Select(SetAssignment).ToList() }).ToList()
            }, ct);
            return true;
        }
        finally { _changes.Release(); }
    }

    public async Task RefreshAsync(Func<CancellationToken, Task<List<DevBoxInstance>>> discover,
        CancellationToken ct = default)
    {
        try
        {
            var machines = await discover(ct);
            await _changes.WaitAsync(ct);
            try
            {
                var ids = machines.Select(m => m.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var retained = Settings.Machines.Where(m => !ids.Contains(m.UniqueId) &&
                    AllSlots(Settings).Any(s => SameId(s.MachineId, m.UniqueId)));
                await CommitAsync(Settings with
                {
                    Machines = [.. machines, .. retained],
                    LastRefreshUtc = DateTimeOffset.UtcNow
                }, ct);
                _available = ids;
                DiscoveryVerified = true;
                RefreshError = null;
            }
            finally { _changes.Release(); }
        }
        catch (Exception ex)
        {
            RefreshError = ex.Message;
            throw;
        }
    }

    private async Task ChangeAsync(Func<BoardSettings, BoardSettings> change, CancellationToken ct)
    {
        await _changes.WaitAsync(ct);
        try { await CommitAsync(change(Settings), ct); }
        finally { _changes.Release(); }
    }

    private async Task CommitAsync(BoardSettings next, CancellationToken ct)
    {
        next.Validate();
        await store.SaveAsync(next, ct);
        Settings = next;
    }

    private DesktopLayout Layout(LayoutKey key) => Settings.DesktopLayouts.Single(layout => layout.Key == key);

    private DesktopLayout? CurrentLayout(BoardSettings settings) =>
        _selectedKey is { } key && settings.PrimaryKey != key
            ? settings.DesktopLayouts.Single(layout => layout.Key == key)
            : null;

    private IReadOnlyList<SlotAssignment> SlotsFor(BoardSettings settings) =>
        CurrentLayout(settings)?.Slots ?? settings.Slots;

    private static IReadOnlyList<SlotAssignment> SlotsFor(BoardSettings settings, LayoutKey key) =>
        settings.PrimaryKey == key ? settings.Slots :
            settings.DesktopLayouts.Single(layout => layout.Key == key).Slots;

    private BoardSettings ReplaceSelected(BoardSettings settings, List<SlotAssignment> slots, int? next = null,
        LayoutKey? key = null)
    {
        var selected = key is { } layoutKey
            ? settings.DesktopLayouts.SingleOrDefault(layout => layout.Key == layoutKey) : CurrentLayout(settings);
        if (selected is null)
            return settings with { Slots = slots, NextSlotNumber = next ?? settings.NextSlotNumber };
        return settings with
        {
            DesktopLayouts = settings.DesktopLayouts.Select(layout => layout.Key == selected.Key
                ? layout with { Slots = slots, NextSlotNumber = next ?? layout.NextSlotNumber }
                : layout).ToList()
        };
    }

    private static IEnumerable<SlotAssignment> AllSlots(BoardSettings settings) =>
        settings.Slots.Concat(settings.DesktopLayouts.SelectMany(layout => layout.Slots));

    internal static string LayoutLabel(string name, int monitorNumber) =>
        monitorNumber == 0 ? name : $"{name} · Monitor {monitorNumber}";

    private (SlotAssignment Slot, string Label)? AssignmentFor(string machineId)
    {
        if (Settings.Slots.FirstOrDefault(slot => SameId(slot.MachineId, machineId)) is { } primary)
            return (primary, Settings.PrimaryDesktopId is null
                ? primary.Name
                : $"{LayoutLabel(Settings.PrimaryDesktopName, Settings.PrimaryMonitorNumber)} / {primary.Name}");
        foreach (var layout in Settings.DesktopLayouts)
            if (layout.Slots.FirstOrDefault(slot => SameId(slot.MachineId, machineId)) is { } slot)
                return (slot, $"{LayoutLabel(layout.Name, layout.MonitorNumber)} / {slot.Name}");
        return null;
    }

    private static string DescribeSlot(BoardSettings settings, Guid slotId)
    {
        if (settings.Slots.FirstOrDefault(slot => slot.Id == slotId) is { } primary)
            return settings.PrimaryDesktopId is null
                ? primary.Name
                : $"{LayoutLabel(settings.PrimaryDesktopName, settings.PrimaryMonitorNumber)} / {primary.Name}";
        var layout = settings.DesktopLayouts.Single(item => item.Slots.Any(slot => slot.Id == slotId));
        return $"{LayoutLabel(layout.Name, layout.MonitorNumber)} / " +
            $"{layout.Slots.Single(slot => slot.Id == slotId).Name}";
    }
}
