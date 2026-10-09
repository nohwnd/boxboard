using Boxboard.Models;

namespace Boxboard.Services;

public static class DesktopLayoutSelection
{
    internal static bool ShowDesktop(VirtualDesktopInfo desktop,
        IReadOnlySet<Guid> desktopsWithAssignments) =>
        desktop.Available || desktopsWithAssignments.Contains(desktop.Id);

    public static Guid ChooseInitialDesktop(BoardSettings settings, Guid managerDesktop,
        IReadOnlyList<SessionWindow> observed)
    {
        if (settings.PrimaryDesktopId is { } saved)
            return saved;
        var matched = new HashSet<Guid>();
        foreach (var slot in settings.Slots.Where(slot => slot.MachineId is not null))
        {
            var machine = settings.Machines.Single(item => BoardSettings.SameId(item.UniqueId, slot.MachineId));
            if (settings.Machines.Count(item => string.Equals(item.OriginalName, machine.OriginalName,
                StringComparison.OrdinalIgnoreCase)) != 1)
                return managerDesktop;
            var windows = observed.Where(window => string.Equals(window.Title, machine.OriginalName,
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (windows.Count > 1)
                return managerDesktop;
            if (windows.Count == 1)
                matched.Add(windows[0].DesktopId);
        }
        return matched.Count == 1 ? matched.Single() : managerDesktop;
    }
}
