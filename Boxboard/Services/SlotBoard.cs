using Bevdox.Models;
using Boxboard.Models;
using static Boxboard.Models.BoardSettings;

namespace Boxboard.Services;

public sealed class SlotBoard(ISettingsStore store)
{
    private readonly SemaphoreSlim _changes = new(1, 1);
    private HashSet<string> _available = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _selectedDesktopId;
    public BoardSettings Settings { get; private set; } = new();
    public bool DiscoveryVerified { get; private set; }
    public string? RefreshError { get; private set; }
    public Guid? SelectedDesktopId => _selectedDesktopId;
    public IReadOnlyList<SlotAssignment> CurrentSlots => SlotsFor(Settings);
    public bool KeepConnected => _selectedDesktopId is { } id && IsKeepConnected(id);
    public WindowLayoutMode LayoutMode => CurrentLayout(Settings)?.LayoutMode ?? Settings.PrimaryLayoutMode;
    public IReadOnlyList<SlotAssignment> VisibleSlots => CurrentSlots.Take(VisibleCount(LayoutMode)).ToList();
    public IReadOnlyList<SlotAssignment> GetVisibleSlots(Guid desktopId) =>
        GetSlots(desktopId).Take(VisibleCount(GetLayoutMode(desktopId))).ToList();
    public WindowLayoutMode GetLayoutMode(Guid desktopId) =>
        Settings.PrimaryDesktopId == desktopId ? Settings.PrimaryLayoutMode :
        Settings.DesktopLayouts.Single(layout => layout.DesktopId == desktopId).LayoutMode;
    public bool IsKeepConnected(Guid desktopId) =>
        Settings.PrimaryDesktopId == desktopId ? Settings.PrimaryKeepConnected :
        Settings.DesktopLayouts.Single(layout => layout.DesktopId == desktopId).KeepConnected;
    public IReadOnlyList<SlotAssignment> GetSlots(Guid desktopId) =>
        Settings.PrimaryDesktopId == desktopId ? Settings.Slots :
        Settings.DesktopLayouts.Single(layout => layout.DesktopId == desktopId).Slots;
    public IReadOnlyList<DesktopLayoutOption> Layouts => Settings.PrimaryDesktopId is { } primary
        ? [new(primary, Settings.PrimaryDesktopName),
            .. Settings.DesktopLayouts.Select(layout => new DesktopLayoutOption(layout.DesktopId, layout.Name))]
        : [];

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var settings = await store.LoadAsync(ct);
        settings.Validate();
        Settings = settings;
        _selectedDesktopId = settings.PrimaryDesktopId;
    }

    public async Task SelectDesktopAsync(Guid desktopId, string name, CancellationToken ct = default)
    {
        if (desktopId == Guid.Empty || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A desktop layout needs a nonempty identity and name.");
        await _changes.WaitAsync(ct);
        try
        {
            if (Settings.PrimaryDesktopId is null)
                await CommitAsync(Settings with
                {
                    Version = Math.Max(Settings.Version, 3),
                    PrimaryDesktopId = desktopId,
                    PrimaryDesktopName = name
                }, ct);
            else if (Settings.PrimaryDesktopId != desktopId &&
                !Settings.DesktopLayouts.Any(layout => layout.DesktopId == desktopId))
            {
                var layout = new DesktopLayout(desktopId, name, 5,
                    [.. Enumerable.Range(1, 4).Select(number => new SlotAssignment(Guid.NewGuid(), number))]);
                await CommitAsync(Settings with
                {
                    Version = Math.Max(Settings.Version, 3),
                    DesktopLayouts = [.. Settings.DesktopLayouts, layout]
                }, ct);
            }
            else if (Settings.Version < 3)
                await CommitAsync(Settings with { Version = 3 }, ct);
            _selectedDesktopId = desktopId;
        }
        finally { _changes.Release(); }
    }

    public Task SetKeepConnectedAsync(bool enabled, CancellationToken ct = default)
    {
        if (_selectedDesktopId is not { } selected)
            throw new InvalidOperationException("Select a desktop layout before changing Keep connected.");
        return SetKeepConnectedAsync(selected, enabled, ct);
    }

    public Task SetKeepConnectedAsync(Guid desktopId, bool enabled, CancellationToken ct = default)
    {
        if (IsKeepConnected(desktopId) == enabled && Settings.Version >= 3)
            return Task.CompletedTask;
        return ChangeAsync(settings => settings.PrimaryDesktopId == desktopId
            ? settings with
            {
                Version = Math.Max(settings.Version, 3),
                PrimaryKeepConnected = enabled
            }
            : settings with
            {
                Version = Math.Max(settings.Version, 3),
                DesktopLayouts = settings.DesktopLayouts.Select(layout => layout.DesktopId == desktopId
                    ? layout with { KeepConnected = enabled } : layout).ToList()
            }, ct);
    }

    public Task SetLayoutModeAsync(WindowLayoutMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (_selectedDesktopId is not { } selected)
            throw new InvalidOperationException("Select a desktop layout before changing its window arrangement.");
        return SetLayoutModeAsync(selected, mode, ct);
    }

    public Task SetLayoutModeAsync(Guid desktopId, WindowLayoutMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (GetLayoutMode(desktopId) == mode)
            return Task.CompletedTask;
        return ChangeAsync(settings =>
        {
            var previousMode = settings.PrimaryDesktopId == desktopId ? settings.PrimaryLayoutMode :
                settings.DesktopLayouts.Single(layout => layout.DesktopId == desktopId).LayoutMode;
            var oldCount = VisibleCount(previousMode);
            var newCount = VisibleCount(mode);
            var slots = SlotsFor(settings, desktopId).Select((slot, index) =>
                index >= newCount && index < oldCount ? slot with { MachineId = null } : slot).ToList();
            return settings.PrimaryDesktopId == desktopId
                ? settings with { Version = Math.Max(settings.Version, 4), PrimaryLayoutMode = mode, Slots = slots }
                : settings with
                {
                    Version = Math.Max(settings.Version, 4),
                    DesktopLayouts = settings.DesktopLayouts.Select(layout => layout.DesktopId == desktopId
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

    public Task ClearSlotAsync(Guid desktopId, Guid slotId, CancellationToken ct = default) => ChangeAsync(settings =>
    {
        var slots = SlotsFor(settings, desktopId);
        _ = slots.Single(s => s.Id == slotId);
        return ReplaceSelected(settings, slots.Select(s => s.Id == slotId ? s with { MachineId = null } : s).ToList(),
            desktopId: desktopId);
    }, ct);

    public Task<bool> AssignAsync(Guid slotId, string machineId, Func<MoveRequest, bool> confirmMove,
        CancellationToken ct = default) =>
        AssignCoreAsync(_selectedDesktopId, slotId, machineId, confirmMove, ct);

    public Task<bool> AssignAsync(Guid desktopId, Guid slotId, string machineId, Func<MoveRequest, bool> confirmMove,
        CancellationToken ct = default) =>
        AssignCoreAsync(desktopId, slotId, machineId, confirmMove, ct);

    private async Task<bool> AssignCoreAsync(Guid? desktopId, Guid slotId, string machineId,
        Func<MoveRequest, bool> confirmMove, CancellationToken ct)
    {
        await _changes.WaitAsync(ct);
        try
        {
            var target = (desktopId is { } id ? SlotsFor(Settings, id) : CurrentSlots)
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

    private DesktopLayout? CurrentLayout(BoardSettings settings) =>
        _selectedDesktopId is { } id && settings.PrimaryDesktopId != id
            ? settings.DesktopLayouts.Single(layout => layout.DesktopId == id)
            : null;

    private IReadOnlyList<SlotAssignment> SlotsFor(BoardSettings settings) =>
        CurrentLayout(settings)?.Slots ?? settings.Slots;

    private static IReadOnlyList<SlotAssignment> SlotsFor(BoardSettings settings, Guid desktopId) =>
        settings.PrimaryDesktopId == desktopId ? settings.Slots :
            settings.DesktopLayouts.Single(layout => layout.DesktopId == desktopId).Slots;

    private BoardSettings ReplaceSelected(BoardSettings settings, List<SlotAssignment> slots, int? next = null,
        Guid? desktopId = null)
    {
        var selected = desktopId is { } id
            ? settings.DesktopLayouts.SingleOrDefault(layout => layout.DesktopId == id) : CurrentLayout(settings);
        if (selected is null)
            return settings with { Slots = slots, NextSlotNumber = next ?? settings.NextSlotNumber };
        return settings with
        {
            DesktopLayouts = settings.DesktopLayouts.Select(layout => layout.DesktopId == selected.DesktopId
                ? layout with { Slots = slots, NextSlotNumber = next ?? layout.NextSlotNumber }
                : layout).ToList()
        };
    }

    private static IEnumerable<SlotAssignment> AllSlots(BoardSettings settings) =>
        settings.Slots.Concat(settings.DesktopLayouts.SelectMany(layout => layout.Slots));

    private (SlotAssignment Slot, string Label)? AssignmentFor(string machineId)
    {
        if (Settings.Slots.FirstOrDefault(slot => SameId(slot.MachineId, machineId)) is { } primary)
            return (primary, Settings.PrimaryDesktopId is null
                ? primary.Name : $"{Settings.PrimaryDesktopName} / {primary.Name}");
        foreach (var layout in Settings.DesktopLayouts)
            if (layout.Slots.FirstOrDefault(slot => SameId(slot.MachineId, machineId)) is { } slot)
                return (slot, $"{layout.Name} / {slot.Name}");
        return null;
    }

    private static string DescribeSlot(BoardSettings settings, Guid slotId)
    {
        if (settings.Slots.FirstOrDefault(slot => slot.Id == slotId) is { } primary)
            return settings.PrimaryDesktopId is null
                ? primary.Name : $"{settings.PrimaryDesktopName} / {primary.Name}";
        var layout = settings.DesktopLayouts.Single(item => item.Slots.Any(slot => slot.Id == slotId));
        return $"{layout.Name} / {layout.Slots.Single(slot => slot.Id == slotId).Name}";
    }
}
