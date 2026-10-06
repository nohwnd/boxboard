namespace Boxboard.Models;

internal static class MonitorFailover
{
    internal sealed record Placement(LayoutKey Key, SlotAssignment Slot, PixelRect Bounds);

    internal static MonitorInfo? Destination(IReadOnlyList<MonitorInfo> monitors, LayoutKey key)
    {
        if (key.MonitorId is null || monitors.Any(monitor =>
            monitor.Available && string.Equals(monitor.Id, key.MonitorId, StringComparison.OrdinalIgnoreCase)))
            return null;
        return monitors.SingleOrDefault(monitor => monitor.Available && monitor.Primary) ??
            throw new InvalidOperationException("No active primary monitor is available for the disconnected layout.");
    }

    internal static IReadOnlyList<PixelRect> Tile(PixelRect area, int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (count + columns - 1) / columns;
        if (area.Width / columns < 100 || area.Height / rows < 100)
            throw new ArgumentOutOfRangeException(nameof(area),
                "The primary monitor is too small for all assigned Dev Box windows.");
        var result = new List<PixelRect>(count);
        for (int row = 0; row < rows; row++)
        {
            var rowCount = Math.Min(columns, count - row * columns);
            var top = area.Y + (int)((long)area.Height * row / rows);
            var bottom = area.Y + (int)((long)area.Height * (row + 1) / rows);
            for (int column = 0; column < rowCount; column++)
            {
                var left = area.X + (int)((long)area.Width * column / rowCount);
                var right = area.X + (int)((long)area.Width * (column + 1) / rowCount);
                result.Add(new(left, top, right - left, bottom - top));
            }
        }
        return result;
    }

    internal static IReadOnlyList<Placement> Combine(PixelRect area,
        IReadOnlyList<(LayoutKey Key, IReadOnlyList<SlotAssignment> Slots)> layouts)
    {
        var assigned = layouts.SelectMany(layout => layout.Slots
            .Where(slot => slot.MachineId is not null)
            .Select(slot => (layout.Key, Slot: slot))).ToList();
        if (assigned.Count == 0)
            return [];
        var bounds = Tile(area, assigned.Count);
        return [.. assigned.Select((item, index) =>
            new Placement(item.Key, item.Slot, bounds[index]))];
    }
}
