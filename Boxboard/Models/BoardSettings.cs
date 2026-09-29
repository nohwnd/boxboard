using Bevdox.Models;

namespace Boxboard.Models;

public sealed record SlotAssignment(Guid Id, int Number, string? MachineId = null)
{
    public string Name => $"Slot {Number}";
}

public enum WindowLayoutMode { Quadrants, SideBySide, LargeLeftTwoStackedRight, SingleWindow }

public sealed record DesktopLayout(Guid DesktopId, string Name, int NextSlotNumber, List<SlotAssignment> Slots)
{
    public bool KeepConnected { get; init; } = true;
    public WindowLayoutMode LayoutMode { get; init; } = WindowLayoutMode.Quadrants;
    /// <summary>Adapter device name of the pinned monitor, or null for a layout saved before pinning existed.</summary>
    public string? MonitorId { get; init; }
    public int MonitorNumber { get; init; }
    public LayoutKey Key => new(DesktopId, MonitorId);
}
public sealed record DesktopLayoutOption(LayoutKey Key, string Name, int MonitorNumber)
{
    public Guid DesktopId => Key.DesktopId;
    public string? MonitorId => Key.MonitorId;
}

public sealed record BoardSettings
{
    public int Version { get; init; } = 1;
    public int NextSlotNumber { get; init; } = 2;
    public List<SlotAssignment> Slots { get; init; } = [new(Guid.NewGuid(), 1)];
    public Guid? PrimaryDesktopId { get; init; }
    public string PrimaryDesktopName { get; init; } = "Default";
    public string? PrimaryMonitorId { get; init; }
    public int PrimaryMonitorNumber { get; init; }
    public bool PrimaryKeepConnected { get; init; } = true;
    public WindowLayoutMode PrimaryLayoutMode { get; init; } = WindowLayoutMode.Quadrants;
    public List<DesktopLayout> DesktopLayouts { get; init; } = [];
    public List<DevBoxInstance> Machines { get; init; } = [];
    public DateTimeOffset? LastRefreshUtc { get; init; }

    public LayoutKey? PrimaryKey => PrimaryDesktopId is { } id ? new LayoutKey(id, PrimaryMonitorId) : null;

    public void Validate()
    {
        if (Version is not (1 or 2 or 3 or 4 or 5))
            throw new InvalidDataException($"Unsupported Boxboard settings version: {Version}.");
        if (Slots is null || Machines is null || DesktopLayouts is null ||
            PrimaryDesktopId == Guid.Empty || string.IsNullOrWhiteSpace(PrimaryDesktopName) ||
            (Version == 1 && (PrimaryDesktopId is not null || DesktopLayouts.Count > 0)) ||
            (Version < 3 && (!PrimaryKeepConnected || DesktopLayouts.Any(layout => layout is not null && !layout.KeepConnected))) ||
            !Enum.IsDefined(PrimaryLayoutMode) ||
            (Version < 4 && (PrimaryLayoutMode != WindowLayoutMode.Quadrants ||
                DesktopLayouts.Any(layout => layout is not null && layout.LayoutMode != WindowLayoutMode.Quadrants))) ||
            (Version < 5 && (PrimaryMonitorId is not null ||
                DesktopLayouts.Any(layout => layout is not null && layout.MonitorId is not null))) ||
            !IsMonitorPinValid(PrimaryMonitorId, PrimaryMonitorNumber) ||
            Slots.Any(s => s is null || s.Id == Guid.Empty || s.Number < 1) ||
            DesktopLayouts.Any(layout => layout is null || layout.DesktopId == Guid.Empty ||
                string.IsNullOrWhiteSpace(layout.Name) || layout.Slots is null ||
                !Enum.IsDefined(layout.LayoutMode) ||
                !IsMonitorPinValid(layout.MonitorId, layout.MonitorNumber) ||
                layout.Slots.Any(s => s is null || s.Id == Guid.Empty || s.Number < 1)) ||
            Machines.Any(m => m is null || string.IsNullOrWhiteSpace(m.UniqueId) ||
                string.IsNullOrWhiteSpace(m.OriginalName) || string.IsNullOrWhiteSpace(m.ProjectName) ||
                string.IsNullOrWhiteSpace(m.DevCenterUri)))
            throw new InvalidDataException("Boxboard settings contain invalid slots or machine identities.");
        var allSlots = Slots.Concat(DesktopLayouts.SelectMany(layout => layout.Slots)).ToList();
        if (NextSlotNumber < 1 || Slots.Any(s => s.Number >= NextSlotNumber) ||
            (DesktopLayouts.Count > 0 && PrimaryDesktopId is null) ||
            DesktopLayouts.Any(layout => layout.NextSlotNumber < 1 ||
                layout.Slots.Any(s => s.Number >= layout.NextSlotNumber) ||
                layout.Slots.Select(s => s.Number).Distinct().Count() != layout.Slots.Count) ||
            DesktopLayouts.Any(layout => layout.Key == PrimaryKey) ||
            DesktopLayouts.Select(layout => layout.Key).Distinct().Count() != DesktopLayouts.Count ||
            allSlots.Select(s => s.Id).Distinct().Count() != allSlots.Count ||
            Slots.Select(s => s.Number).Distinct().Count() != Slots.Count ||
            Machines.Select(m => m.UniqueId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Machines.Count)
            throw new InvalidDataException("Boxboard settings contain duplicate identities or invalid slot numbering.");
        var assignments = allSlots.Where(s => s.MachineId is not null).Select(s => s.MachineId!).ToList();
        if (assignments.Distinct(StringComparer.OrdinalIgnoreCase).Count() != assignments.Count ||
            assignments.Any(id => !Machines.Any(m => SameId(m.UniqueId, id))))
            throw new InvalidDataException("Boxboard settings contain duplicate or unknown machine assignments.");
    }

    private static bool IsMonitorPinValid(string? monitorId, int monitorNumber) => monitorId is null
        ? monitorNumber == 0
        : !string.IsNullOrWhiteSpace(monitorId) && monitorNumber >= 1;

    public static bool SameId(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public sealed record MachineOption(string UniqueId, string Label, string Details, bool Available);
public sealed record MoveRequest(string MachineName, string SourceSlot, string TargetSlot, string? ReplacedMachine);
