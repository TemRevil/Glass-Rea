namespace GlassRea.Core.Paths;

/// <summary>Converts kernel paths like \Device\HarddiskVolume3\Windows\x.exe into C:\Windows\x.exe.</summary>
public sealed class DevicePaths(IReadOnlyDictionary<string, string> deviceToDrive)
{
    public string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) return path[4..];
        if (!path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return path;
        foreach (var (device, drive) in deviceToDrive)
        {
            if (path.StartsWith(device + @"\", StringComparison.OrdinalIgnoreCase))
                return drive + path[device.Length..];
        }
        return path;
    }
}
