using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Boxboard.Models;
using Microsoft.Win32.SafeHandles;

namespace Boxboard.Services;

public sealed class WindowPlacementRejectedException(string message) : InvalidOperationException(message);

public sealed partial class NativeSessionWindows(nint boardHandle) : ISessionWindows, IDisposable
{
    private readonly IVirtualDesktopManager _desktops = (IVirtualDesktopManager)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"), throwOnError: true)!)!;
    public static bool CanInteract => CanInteractWithInputDesktop();

    public BoardEnvironment GetEnvironment()
    {
        Marshal.ThrowExceptionForHR(_desktops.GetWindowDesktopId(boardHandle, out var desktop));
        Marshal.ThrowExceptionForHR(_desktops.IsWindowOnCurrentVirtualDesktop(boardHandle, out var current));
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfoW(MonitorFromWindow(boardHandle, 2), ref info) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new(desktop, current != 0, CanInteractWithInputDesktop(), info.Work.ToPixels());
    }

    public IReadOnlyList<SessionWindow> Enumerate()
    {
        var result = new List<SessionWindow>();
        foreach (var process in Process.GetProcessesByName("msrdc"))
        {
            using (process)
            {
                try
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd == 0 || ReadClass(hwnd) != "TscShellContainerClass")
                        continue;
                    if (GetWindowRect(hwnd, out var rect) == 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    var hr = _desktops.GetWindowDesktopId(hwnd, out var desktop);
                    if (hr != 0)
                        Marshal.ThrowExceptionForHR(hr);
                    var style = (long)GetWindowLongPtrW(hwnd, -16);
                    result.Add(new(new(hwnd, process.Id, process.StartTime.ToUniversalTime().Ticks),
                        process.MainWindowTitle, desktop, rect.ToPixels(),
                        (style & 0x00C00000) != 0x00C00000 || (style & 0x00040000) == 0,
                        IsIconic(hwnd) != 0, IsZoomed(hwnd) != 0));
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) when (process.HasExited) { }
            }
        }
        return result;
    }

    public int CountVisibleTopLevelWindows(WindowIdentity identity) => VisibleTopLevelWindows(identity).Count;

    public async Task CloseReconnectPromptAsync(SessionWindow window, CancellationToken ct)
    {
        if (!CanInteractWithInputDesktop())
            throw new InvalidOperationException("Windows is locked; the reconnect prompt was not closed.");
        var identity = window.Identity;
        if (!Enumerate().Any(candidate => candidate.Identity == identity &&
            candidate.DesktopId == window.DesktopId && candidate.Title == window.Title))
            throw new InvalidOperationException("The assigned client changed before its reconnect prompt could be closed.");
        var handles = VisibleTopLevelWindows(identity);
        if (handles.Count != 2 || !handles.Contains(identity.Handle))
            throw new InvalidOperationException("The client no longer has exactly one additional visible window.");
        var prompt = handles.Single(handle => handle != identity.Handle);
        if (ReadClass(prompt) == "TscShellContainerClass")
            throw new InvalidOperationException("The additional window is another client, not a reconnect prompt.");
        ct.ThrowIfCancellationRequested();
        if (PostMessageW(prompt, 0x0010, 0, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        for (int attempt = 0; attempt < 50 && IsWindow(prompt) != 0 && IsWindowVisible(prompt) != 0; attempt++)
            await Task.Delay(100, ct);
        if (IsWindow(prompt) != 0 && IsWindowVisible(prompt) != 0)
            throw new TimeoutException("Windows App did not close the reconnect prompt; no replacement was launched.");
        if (IsWindow(identity.Handle) != 0)
        {
            ct.ThrowIfCancellationRequested();
            _ = VisibleTopLevelWindows(identity);
            GetWindowThreadProcessId(identity.Handle, out var processId);
            if (processId != (uint)identity.ProcessId ||
                ReadClass(identity.Handle) != "TscShellContainerClass")
                throw new InvalidOperationException("The original client identity changed; no replacement was launched.");
            if (PostMessageW(identity.Handle, 0x0010, 0, 0) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            for (int attempt = 0; attempt < 50 && IsWindow(identity.Handle) != 0; attempt++)
                await Task.Delay(100, ct);
            if (IsWindow(identity.Handle) != 0)
                throw new TimeoutException("Windows App did not close the old client; no replacement was launched.");
        }
    }

    private static IReadOnlyList<nint> VisibleTopLevelWindows(WindowIdentity identity)
    {
        using var process = Process.GetProcessById(identity.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.ProcessStartUtcTicks)
            throw new InvalidOperationException("The client process changed before its windows could be counted.");
        var handles = new List<nint>();
        EnumWindowsCallback callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == (uint)identity.ProcessId && IsWindowVisible(hwnd) != 0)
                handles.Add(hwnd);
            return 1;
        };
        if (EnumWindows(callback, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        GC.KeepAlive(callback);
        return handles;
    }

    public async Task PlaceAsync(SessionWindow window, PixelRect bounds, CancellationToken ct)
    {
        var identity = window.Identity;
        ValidateTarget(window, bounds);
        if (IsIconic(identity.Handle) != 0 || IsZoomed(identity.Handle) != 0)
        {
            if (ShowWindowAsync(identity.Handle, 4) == 0)
                throw new InvalidOperationException("Windows App did not accept the windowed restore request.");
            await Task.Delay(120, ct);
        }
        ct.ThrowIfCancellationRequested();
        ValidateTarget(window, bounds);
        if (GetWindowRect(identity.Handle, out var before) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var outer = before.ToPixels();
        var visible = GetVisibleBounds(window);
        if (!outer.Contains(visible))
            throw new WindowPlacementRejectedException(
                $"The visible frame {visible} is outside its native window bounds {outer}.");
        var requested = FourCellGeometry.OuterForVisible(bounds, outer, visible);
        // Normal top-level windows above the board, without activation or global topmost state.
        if (SetWindowPos(identity.Handle, 0, requested.X, requested.Y, requested.Width, requested.Height, 0x0010 | 0x0200) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        PixelRect lastBounds = default;
        PixelRect lastVisible = default;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(30, ct);
            ValidateTarget(window, bounds);
            if (GetWindowRect(identity.Handle, out var actual) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            lastBounds = actual.ToPixels();
            lastVisible = GetVisibleBounds(window);
            if (lastVisible == bounds)
                return;
        }
        throw new WindowPlacementRejectedException($"The client did not accept the requested visible bounds {bounds}; " +
            $"last visible frame was {lastVisible} and native window bounds were {lastBounds}.");
    }

    public PixelRect GetVisibleBounds(SessionWindow window)
    {
        if (!Enumerate().Any(candidate => candidate.Identity == window.Identity &&
            string.Equals(candidate.Title, window.Title, StringComparison.Ordinal)))
            throw new InvalidOperationException("The selected client changed before its visible frame could be read.");
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window.Identity.Handle, 9, out var visible,
            (uint)Marshal.SizeOf<NativeRect>()));
        return visible.ToPixels();
    }

    private void ValidateTarget(SessionWindow expected, PixelRect bounds)
    {
        var environment = GetEnvironment();
        if (!environment.CanInteract)
            throw new InvalidOperationException("Layout paused because Windows is locked or showing a secure desktop.");
        if (!environment.WorkArea.Contains(bounds))
            throw new InvalidOperationException("Cell bounds extend outside the board's single-monitor work area.");
        var actual = Enumerate().SingleOrDefault(w => w.Identity == expected.Identity &&
            string.Equals(w.Title, expected.Title, StringComparison.Ordinal));
        if (actual is null || actual.DesktopId != environment.DesktopId)
            throw new InvalidOperationException("The bound window changed or moved to another desktop; refusing to move it.");
        if (actual.Fullscreen)
            throw new InvalidOperationException("Fullscreen client detected. Use per-device windowed display settings in Windows App.");
    }

    private static unsafe string ReadClass(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetClassNameW(hwnd, buffer, 256);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new string(buffer, 0, length);
    }

    private static unsafe bool CanInteractWithInputDesktop()
    {
        using var desktop = OpenInputDesktop(0, 0, 0x0001);
        if (desktop.IsInvalid)
            return false;
        var buffer = stackalloc char[256];
        if (GetUserObjectInformationW(desktop, 2, buffer, 512, out _) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return string.Equals(new string(buffer), "Default", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Marshal.ReleaseComObject(_desktops);

    private sealed partial class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DesktopHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseDesktop(handle) != 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial DesktopHandle OpenInputDesktop(uint flags, int inherit, uint access);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int CloseDesktop(nint desktop);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial int GetUserObjectInformationW(DesktopHandle desktop, int index, void* buffer, uint length, out uint needed);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetWindowRect(nint hwnd, out NativeRect rect);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumWindowsCallback(nint hwnd, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int EnumWindows(EnumWindowsCallback callback, nint lParam);
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [LibraryImport("user32.dll")]
    private static partial int IsWindowVisible(nint hwnd);
    [LibraryImport("user32.dll")]
    private static partial int IsWindow(nint hwnd);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, out NativeRect value, uint size);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial int GetClassNameW(nint hwnd, char* value, int capacity);
    [LibraryImport("user32.dll")]
    private static partial int IsIconic(nint hwnd);
    [LibraryImport("user32.dll")]
    private static partial int IsZoomed(nint hwnd);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")]
    private static partial int ShowWindowAsync(nint hwnd, int show);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelRect ToPixels() => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint hwnd, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(nint hwnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(nint hwnd, in Guid desktopId);
    }
}
