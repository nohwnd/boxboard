namespace Boxboard.Models;

public sealed record WindowLayoutChoice(WindowLayoutMode Mode, string Name);

public sealed class DesktopCardViewModel
{
    public static IReadOnlyList<WindowLayoutChoice> LayoutChoices { get; } =
    [
        new(WindowLayoutMode.Quadrants, "2 × 2"),
        new(WindowLayoutMode.SideBySide, "Side by side"),
        new(WindowLayoutMode.LargeLeftTwoStackedRight, "Large left + 2"),
        new(WindowLayoutMode.SingleWindow, "One window")
    ];

    public required Guid DesktopId { get; init; }
    public required string Name { get; init; }
    public string? MonitorId { get; init; }
    public int MonitorNumber { get; init; }
    public string MonitorName { get; init; } = "Any monitor";
    public string MonitorDetails { get; init; } = "";
    public bool IsDisconnected { get; init; }
    public string DisconnectedWarning { get; init; } = "";
    public LayoutKey Key => new(DesktopId, MonitorId);
    public required WindowLayoutMode Mode { get; init; }
    public required bool KeepConnected { get; init; }
    public required bool CanEdit { get; init; }
    /// <summary>The window arrangement is a saved preference, so the offline demo can change it too.</summary>
    public required bool CanChooseLayout { get; init; }
    public required bool PendingApply { get; init; }
    public required IReadOnlyList<CellViewModel> Cells { get; init; }
    public required string HiddenAssignments { get; init; }
    public bool HasHiddenAssignments => HiddenAssignments.Length > 0;
    public IReadOnlyList<WindowLayoutChoice> Modes => LayoutChoices;
    public WindowLayoutChoice SelectedMode => LayoutChoices.Single(choice => choice.Mode == Mode);
}

/// <summary>One virtual desktop and the per-monitor layout cards that belong to it.</summary>
public sealed class DesktopGroupViewModel
{
    public required Guid DesktopId { get; init; }
    public required string Name { get; init; }
    public required bool Available { get; init; }
    public required IReadOnlyList<DesktopCardViewModel> Cards { get; init; }
    public int DisconnectedCount => Cards.Count(card => card.IsDisconnected);
    public string Summary => Available
        ? $"{Cards.Count - DisconnectedCount} monitor{(Cards.Count - DisconnectedCount == 1 ? "" : "s")}" +
          (DisconnectedCount == 0 ? "" : $" · {DisconnectedCount} disconnected with assignments")
        : "Desktop unavailable";
}
