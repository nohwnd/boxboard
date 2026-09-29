using System.ComponentModel;
using System.Runtime.InteropServices;
using Boxboard.Models;

namespace Boxboard.Services;

/// <summary>
/// Numbers the active displays from left to right. DisplayConfig maps the temporary
/// GDI source name to a monitor device path that survives DISPLAY number changes
/// while Windows continues to report that same device path.
/// </summary>
public sealed partial class MonitorShell : IMonitors
{
    public static MonitorShell Instance { get; } = new();

    internal static MonitorInfo ChooseMigrationMonitor(IReadOnlyList<MonitorInfo> monitors, MonitorInfo boardMonitor)
    {
        var connected = monitors.Where(monitor => monitor.Available).ToList();
        if (connected.Count == 0)
            throw new InvalidOperationException("No connected monitor is available for the saved layout.");
        var external = connected.Where(monitor => monitor.IsBuiltIn == false).ToList();
        return external.Count == 1 ? external[0] :
            connected.FirstOrDefault(monitor => monitor.Primary) ??
            connected.FirstOrDefault(monitor => monitor.Id == boardMonitor.Id) ??
            connected[0];
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var identities = ReadMonitorIdentities();
        var handles = new List<nint>();
        MonitorEnumCallback callback = (monitor, _, _, _) =>
        {
            handles.Add(monitor);
            return 1;
        };
        if (EnumDisplayMonitors(0, 0, callback, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        GC.KeepAlive(callback);

        var found = new List<(string Id, PixelRect Bounds, PixelRect WorkArea, bool Primary)>();
        var metadata = new Dictionary<string, (string GdiName, bool? IsBuiltIn, bool Stable)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var handle in handles)
        {
            var info = NewMonitorInfo();
            if (GetMonitorInfoW(handle, ref info) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var gdiName = ReadDevice(info);
            var identity = identities.TryGetValue(gdiName, out var mapped)
                ? mapped : new DisplayIdentity(PersistentId(gdiName, null), null, false);
            found.Add((identity.Id, info.Monitor.ToPixels(), info.Work.ToPixels(), (info.Flags & 1) != 0));
            if (!metadata.TryAdd(identity.Id, (gdiName, identity.IsBuiltIn, identity.Stable)))
                throw new InvalidOperationException("Windows reported duplicate monitor device names.");
        }
        if (found.Count == 0)
            throw new InvalidOperationException("Windows reported no monitors.");
        return [.. Number(found).Select(monitor =>
        {
            var details = metadata[monitor.Id];
            return monitor with
            {
                GdiDeviceName = details.GdiName,
                IsBuiltIn = details.IsBuiltIn,
                StableIdentity = details.Stable
            };
        })];
    }

    internal static string PersistentId(string gdiName, string? monitorPath) =>
        string.IsNullOrWhiteSpace(monitorPath) ? $"gdi:{gdiName}" : monitorPath;

    internal static (int Path, int Mode, int SourceName, int TargetName) NativeStructSizes =>
        (Marshal.SizeOf<DisplayConfigPath>(), Marshal.SizeOf<DisplayConfigMode>(),
            Marshal.SizeOf<DisplayConfigSourceName>(), Marshal.SizeOf<DisplayConfigTargetName>());

    private readonly record struct DisplayIdentity(string Id, bool? IsBuiltIn, bool Stable);

    private static Dictionary<string, DisplayIdentity> ReadMonitorIdentities()
    {
        const uint activePaths = 2;
        var result = new Dictionary<string, DisplayIdentity>(StringComparer.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var error = GetDisplayConfigBufferSizes(activePaths, out var pathCount, out var modeCount);
            if (error is 5 or 50) return result;
            if (error != 0) throw new Win32Exception(error);
            if (pathCount is 0 or > 128 || modeCount > 512)
                throw new InvalidOperationException("Windows returned an invalid display configuration size.");

            var paths = new DisplayConfigPath[pathCount];
            var modes = new DisplayConfigMode[Math.Max(modeCount, 1)];
            error = QueryDisplayConfig(activePaths, ref pathCount, paths, ref modeCount, modes, 0);
            if (error == 122) continue;
            if (error is 5 or 50) return result;
            if (error != 0) throw new Win32Exception(error);
            foreach (var path in paths.Take(checked((int)pathCount)))
            {
                var source = new DisplayConfigSourceName
                {
                    Header = new(1, (uint)Marshal.SizeOf<DisplayConfigSourceName>(),
                        path.Source.AdapterId, path.Source.Id)
                };
                var target = new DisplayConfigTargetName
                {
                    Header = new(2, (uint)Marshal.SizeOf<DisplayConfigTargetName>(),
                        path.Target.AdapterId, path.Target.Id)
                };
                var sourceError = GetSourceDeviceName(ref source);
                var targetError = GetTargetDeviceName(ref target);
                if (sourceError != 0) throw new Win32Exception(sourceError);
                if (targetError is not (0 or 5 or 50 or 1168))
                    throw new Win32Exception(targetError);
                var gdiName = ReadSourceName(source);
                if (string.IsNullOrWhiteSpace(gdiName))
                    throw new InvalidOperationException("Windows returned a display path without a GDI source name.");
                var pathName = targetError == 0 ? ReadTargetPath(target) : null;
                var technology = path.Target.OutputTechnology;
                bool? isBuiltIn = technology == 0x80000000 ? true :
                    technology is 0xFFFFFFFF or 17 ? null : false;
                var identity = new DisplayIdentity(PersistentId(gdiName, pathName),
                    isBuiltIn, !string.IsNullOrWhiteSpace(pathName));
                if (result.TryGetValue(gdiName, out var existing) && existing.Id != identity.Id)
                    result[gdiName] = new(PersistentId(gdiName, null), null, false);
                else
                    result.TryAdd(gdiName, identity);
            }
            return result;
        }
        throw new Win32Exception(122, "The display configuration kept changing during enumeration.");
    }

    private static unsafe string ReadSourceName(DisplayConfigSourceName source) =>
        ReadNullTerminated(new ReadOnlySpan<char>(source.ViewGdiDeviceName, 32));

    private static unsafe string ReadTargetPath(DisplayConfigTargetName target) =>
        ReadNullTerminated(new ReadOnlySpan<char>(target.MonitorDevicePath, 128));

    private static string ReadNullTerminated(ReadOnlySpan<char> value)
    {
        var length = value.IndexOf('\0');
        return (length < 0 ? value : value[..length]).ToString();
    }

    /// <summary>Numbers monitors from 1, left to right and then top to bottom.</summary>
    internal static IReadOnlyList<MonitorInfo> Number(
        IEnumerable<(string Id, PixelRect Bounds, PixelRect WorkArea, bool Primary)> monitors) =>
        [.. monitors
            .OrderBy(monitor => monitor.Bounds.X)
            .ThenBy(monitor => monitor.Bounds.Y)
            .Select((monitor, index) =>
                new MonitorInfo(monitor.Id, index + 1, monitor.Bounds, monitor.WorkArea, monitor.Primary))];

    private static MonitorInfoEx NewMonitorInfo()
    {
        var info = default(MonitorInfoEx);
        info.Size = Marshal.SizeOf<MonitorInfoEx>();
        return info;
    }

    private static unsafe string ReadDevice(MonitorInfoEx info)
    {
        var span = new ReadOnlySpan<char>(info.Device, 32);
        var end = span.IndexOf('\0');
        var device = (end < 0 ? span : span[..end]).ToString();
        return device.Length == 0
            ? throw new InvalidOperationException("Windows reported a monitor without a device name.")
            : device;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int MonitorEnumCallback(nint monitor, nint deviceContext, nint rect, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int EnumDisplayMonitors(nint deviceContext, nint clip,
        MonitorEnumCallback callback, nint data);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static partial int GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);
    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint pathCount,
        [Out] DisplayConfigPath[] paths, ref uint modeCount, [Out] DisplayConfigMode[] modes, nint topologyId);
    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static partial int GetSourceDeviceName(ref DisplayConfigSourceName source);
    [LibraryImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static partial int GetTargetDeviceName(ref DisplayConfigTargetName target);

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigLuid
    {
        public uint Low;
        public int High;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigHeader(uint type, uint size, DisplayConfigLuid adapterId, uint id)
    {
        public uint Type = type, Size = size;
        public DisplayConfigLuid AdapterId = adapterId;
        public uint Id = id;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSource
    {
        public DisplayConfigLuid AdapterId;
        public uint Id, ModeIndex, StatusFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigTarget
    {
        public DisplayConfigLuid AdapterId;
        public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
        public uint RefreshNumerator, RefreshDenominator, ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPath
    {
        public DisplayConfigSource Source;
        public DisplayConfigTarget Target;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DisplayConfigMode
    {
        public uint Type, Id;
        public DisplayConfigLuid AdapterId;
        public fixed byte ModeData[48];
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DisplayConfigSourceName
    {
        public DisplayConfigHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DisplayConfigTargetName
    {
        public DisplayConfigHeader Header;
        public uint Flags, OutputTechnology;
        public ushort EdidManufactureId, EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char MonitorFriendlyDeviceName[64];
        public fixed char MonitorDevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelRect ToPixels() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
        public fixed char Device[32];
    }
}
