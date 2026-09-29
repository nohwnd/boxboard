using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Boxboard.Models;

namespace Boxboard.Services;

/// <summary>
/// Shows the Windows display number on every monitor, the way the display settings
/// page does, so a monitor can be matched to the card that pins Dev Boxes to it.
/// </summary>
public static partial class MonitorIdentityOverlay
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);

    public static IReadOnlyList<Window> Show(IReadOnlyList<MonitorInfo> monitors, TimeSpan? duration = null)
    {
        if (monitors.Count == 0)
            throw new ArgumentException("No monitors to identify.", nameof(monitors));
        var badges = new List<Window>();
        try
        {
            foreach (var monitor in monitors.Where(monitor => monitor.Available))
                badges.Add(Create(monitor, duration ?? DefaultDuration));
        }
        catch
        {
            // Never leave half of the screens covered by a badge that no timer will close.
            foreach (var badge in badges)
                badge.Close();
            throw;
        }
        if (badges.Count == 0)
            throw new InvalidOperationException("None of the saved monitors is connected.");
        return badges;
    }

    private static Window Create(MonitorInfo monitor, TimeSpan duration)
    {
        var number = new TextBlock
        {
            Text = monitor.Number.ToString(),
            FontSize = 120,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var caption = new TextBlock
        {
            Text = $"{monitor.Name} · {monitor.Description}",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(214, 226, 236)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(number);
        content.Children.Add(caption);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            IsHitTestVisible = false,
            Title = $"Boxboard {monitor.Name}",
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(23, 41, 56)) { Opacity = 0.88 },
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(40, 26, 40, 30),
                Child = content
            }
        };
        // WPF positions in device-independent units, so place the badge in physical
        // pixels instead; per-monitor DPI would otherwise land it on the wrong screen.
        window.SourceInitialized += (_, _) => PlaceCentered(window, monitor.Bounds);
        var timer = new DispatcherTimer(duration, DispatcherPriority.Normal, (_, _) => window.Close(),
            window.Dispatcher);
        window.Closed += (_, _) => timer.Stop();
        window.Show();
        return window;
    }

    private static void PlaceCentered(Window window, PixelRect bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
            return;
        // WS_EX_TRANSPARENT keeps clicks going to whatever is underneath, WS_EX_NOACTIVATE
        // stops the badge stealing focus, WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
        var style = GetWindowLongPtrW(handle, -20);
        SetWindowLongPtrW(handle, -20, style | 0x20 | 0x08000000 | 0x80);
        var width = Math.Clamp(bounds.Width / 3, 260, 520);
        var height = Math.Clamp(bounds.Height / 4, 180, 300);
        // SWP_NOACTIVATE | SWP_NOZORDER
        SetWindowPos(handle, 0, bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2, width, height, 0x0010 | 0x0004);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowPos(nint hwnd, nint insertAfter, int x, int y,
        int width, int height, uint flags);
}
