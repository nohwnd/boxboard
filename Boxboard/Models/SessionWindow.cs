namespace Boxboard.Models;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(PixelRect other) => other.X >= X && other.Y >= Y &&
        other.Right <= Right && other.Bottom <= Bottom;
}

public static class FourCellGeometry
{
    public static IReadOnlyList<PixelRect> Divide(PixelRect area, int gap = 8)
    {
        if (gap < 0 || area.Width < gap + 2 || area.Height < gap + 2)
            throw new ArgumentOutOfRangeException(nameof(area), "The board is too small for four cells.");
        int leftWidth = (area.Width - gap) / 2, topHeight = (area.Height - gap) / 2;
        return
        [
            new(area.X, area.Y, leftWidth, topHeight),
            new(area.X + leftWidth + gap, area.Y, area.Width - leftWidth - gap, topHeight),
            new(area.X, area.Y + topHeight + gap, leftWidth, area.Height - topHeight - gap),
            new(area.X + leftWidth + gap, area.Y + topHeight + gap,
                area.Width - leftWidth - gap, area.Height - topHeight - gap)
        ];
    }

    public static PixelRect OuterForVisible(PixelRect desired, PixelRect outer, PixelRect visible)
    {
        if (desired.Width <= 0 || desired.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(desired));
        if (visible.Width <= 0 || visible.Height <= 0 || !outer.Contains(visible))
            throw new InvalidOperationException($"The visible frame {visible} is outside its native window bounds {outer}.");
        var left = visible.X - outer.X;
        var top = visible.Y - outer.Y;
        var right = outer.Right - visible.Right;
        var bottom = outer.Bottom - visible.Bottom;
        return new(checked(desired.X - left), checked(desired.Y - top),
            checked(desired.Width + left + right), checked(desired.Height + top + bottom));
    }

    public static IReadOnlyList<PixelRect> Focus(PixelRect area, int focusedIndex, int focusedPercent = 70)
    {
        if (focusedIndex is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(focusedIndex));
        if (focusedPercent is < 50 or > 80)
            throw new ArgumentOutOfRangeException(nameof(focusedPercent));
        if (area.Width < 200 || area.Height < 300)
            throw new ArgumentOutOfRangeException(nameof(area), "The work area is too small for four windows.");

        int focusedWidth = (int)((long)area.Width * focusedPercent / 100);
        int secondaryWidth = area.Width - focusedWidth;
        int focusedX = focusedIndex % 2 == 0 ? area.X : area.X + secondaryWidth;
        int secondaryX = focusedIndex % 2 == 0 ? area.X + focusedWidth : area.X;
        var bounds = new PixelRect[4];
        bounds[focusedIndex] = new(focusedX, area.Y, focusedWidth, area.Height);
        int secondaryIndex = 0;
        for (int i = 0; i < bounds.Length; i++)
        {
            if (i == focusedIndex) continue;
            int top = area.Y + (int)((long)area.Height * secondaryIndex / 3);
            int bottom = area.Y + (int)((long)area.Height * (secondaryIndex + 1) / 3);
            bounds[i] = new(secondaryX, top, secondaryWidth, bottom - top);
            secondaryIndex++;
        }
        return bounds;
    }

    public static PixelRect Overlay(PixelRect area, int focusedIndex, int focusedPercent = 70)
    {
        if (focusedIndex is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(focusedIndex));
        if (focusedPercent is < 60 or > 80)
            throw new ArgumentOutOfRangeException(nameof(focusedPercent));
        if (area.Width < 200 || area.Height < 200)
            throw new ArgumentOutOfRangeException(nameof(area), "The work area is too small for a focused window.");

        int width = (int)((long)area.Width * focusedPercent / 100);
        int height = (int)((long)area.Height * focusedPercent / 100);
        int x = focusedIndex % 2 == 0 ? area.X : area.Right - width;
        int y = focusedIndex < 2 ? area.Y : area.Bottom - height;
        return new(x, y, width, height);
    }
}

// Borrowed window identities, never owning handles. Process creation time prevents PID reuse.
public readonly record struct WindowIdentity(nint Handle, int ProcessId, long ProcessStartUtcTicks);
public sealed record SessionWindow(WindowIdentity Identity, string Title, Guid DesktopId,
    PixelRect Bounds, bool Fullscreen, bool Minimized, bool Maximized)
{
    public string Label => $"{Title} | PID {Identity.ProcessId} | HWND {Identity.Handle}";
}
public sealed record BoardEnvironment(Guid DesktopId, bool IsCurrentDesktop, bool CanInteract, PixelRect WorkArea);

public interface ISessionWindows
{
    BoardEnvironment GetEnvironment();
    IReadOnlyList<SessionWindow> Enumerate();
    int CountVisibleTopLevelWindows(WindowIdentity identity);
    Task CloseReconnectPromptAsync(SessionWindow window, CancellationToken ct);
    PixelRect GetVisibleBounds(SessionWindow window);
    Task PlaceAsync(SessionWindow window, PixelRect bounds, CancellationToken ct);
}
