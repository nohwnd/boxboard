using Boxboard.Models;

namespace Boxboard.Services;

internal sealed record MonitorPinMigrationResult(int Migrated, IReadOnlyList<LayoutKey> Conflicts);

internal static class MonitorPinMigration
{
    internal static async Task<MonitorPinMigrationResult> MigrateAsync(SlotBoard board,
        IReadOnlyList<MonitorInfo> monitors, Func<string, string?> physicalId,
        CancellationToken ct = default)
    {
        var conflicts = new List<LayoutKey>();
        int migrated = 0;
        foreach (var layout in board.Layouts.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (layout.MonitorId is not { } saved ||
                !board.GetSlots(layout.Key).Any(slot => slot.MachineId is not null))
                continue;
            var id = physicalId(saved);
            var monitor = monitors.SingleOrDefault(item => item.Available && item.StableIdentity &&
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (monitor is null || string.Equals(saved, monitor.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (board.HasLayout(new(layout.DesktopId, monitor.Id)))
            {
                conflicts.Add(layout.Key);
                continue;
            }
            await board.ReidentifyMonitorAsync(layout.Key, monitor.Id, monitor.Number, ct);
            migrated++;
        }
        return new(migrated, conflicts);
    }
}
