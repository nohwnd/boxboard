namespace Boxboard.Models;

/// <summary>
/// A physical display. <see cref="Id"/> is its monitor device path when Windows
/// provides one, not the session-dependent GDI name (for example \\.\DISPLAY1).
/// </summary>
public sealed record MonitorInfo(string Id, int Number, PixelRect Bounds, PixelRect WorkArea,
    bool Primary, bool Available = true)
{
    public string? GdiDeviceName { get; init; }
    public bool? IsBuiltIn { get; init; }
    public bool StableIdentity { get; init; } = true;
    public string Name => $"Monitor {Number}";
    public string Description => Available
        ? $"{Bounds.Width} × {Bounds.Height}{(Primary ? " · primary" : "")}" +
            (StableIdentity ? "" : " · pin may change")
        : "Disconnected";
}

/// <summary>
/// Identifies one slot layout: a virtual desktop plus the monitor it is pinned to.
/// A null <see cref="MonitorId"/> is an unpinned layout saved before monitor pinning existed.
/// </summary>
public readonly record struct LayoutKey(Guid DesktopId, string? MonitorId = null)
{
    public static implicit operator LayoutKey(Guid desktopId) => new(desktopId);
}

public interface IMonitors
{
    IReadOnlyList<MonitorInfo> GetMonitors();
}
