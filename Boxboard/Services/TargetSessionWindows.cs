using Boxboard.Models;

namespace Boxboard.Services;

public sealed class TargetSessionWindows(
    ISessionWindows managerWindows,
    Guid desktopId,
    PixelRect workArea,
    Func<Guid> currentDesktopId,
    Func<SessionWindow, PixelRect, CancellationToken, Task> placeOnTarget) : ISessionWindows
{
    public BoardEnvironment GetEnvironment()
    {
        var manager = managerWindows.GetEnvironment();
        if (!manager.CanInteract)
            return new(desktopId, false, false, workArea);
        var current = manager.DesktopId == desktopId
            ? manager.IsCurrentDesktop : currentDesktopId() == desktopId;
        return new(desktopId, current, manager.CanInteract, workArea);
    }

    public IReadOnlyList<SessionWindow> Enumerate() => managerWindows.Enumerate();
    public int CountVisibleTopLevelWindows(WindowIdentity identity) =>
        managerWindows.CountVisibleTopLevelWindows(identity);
    public Task CloseReconnectPromptAsync(SessionWindow window, CancellationToken ct)
    {
        var environment = GetEnvironment();
        if (!environment.CanInteract || window.DesktopId != desktopId)
            throw new InvalidOperationException("The assigned desktop is unavailable; no reconnect window was closed.");
        return managerWindows.CloseReconnectPromptAsync(window, ct);
    }
    public PixelRect GetVisibleBounds(SessionWindow window) => managerWindows.GetVisibleBounds(window);

    public Task PlaceAsync(SessionWindow window, PixelRect bounds, CancellationToken ct)
    {
        var environment = GetEnvironment();
        if (!environment.CanInteract)
            throw new InvalidOperationException("Windows is locked or showing a secure desktop; no client was moved.");
        if (window.DesktopId != desktopId || !workArea.Contains(bounds))
            throw new InvalidOperationException("The client or requested bounds are outside the selected desktop layout.");
        return placeOnTarget(window, bounds, ct);
    }
}
