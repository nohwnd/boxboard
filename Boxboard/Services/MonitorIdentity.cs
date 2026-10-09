using System.Security.Cryptography;
using Microsoft.Win32;

namespace Boxboard.Services;

internal static class MonitorIdentity
{
    internal static string? FromDevicePath(string? devicePath)
    {
        var parts = devicePath?.Split('#');
        if (parts is not { Length: >= 4 } ||
            !parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) ||
            !ValidSegment(parts[1]) || !ValidSegment(parts[2]))
            return null;

        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
        return key?.GetValue("EDID") is byte[] edid ? FromEdid(edid) : null;
    }

    internal static string? FromEdid(ReadOnlySpan<byte> edid)
    {
        ReadOnlySpan<byte> header = [0, 255, 255, 255, 255, 255, 255, 0];
        ReadOnlySpan<byte> invalidSerial = [255, 255, 255, 255];
        if (edid.Length < 128 || !edid[..8].SequenceEqual(header) ||
            edid[12..16].SequenceEqual(stackalloc byte[4]) ||
            edid[12..16].SequenceEqual(invalidSerial))
            return null;
        return "edid:" + Convert.ToHexString(SHA256.HashData(edid[8..16]));
    }

    private static bool ValidSegment(string segment) => segment.Length is > 0 and < 128 &&
        segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '&' or '_' or '-');
}
