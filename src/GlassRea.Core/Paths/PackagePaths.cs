namespace GlassRea.Core.Paths;

public static class PackagePaths
{
    /// <summary>
    /// For a Store app under ...\WindowsApps\Name_Version_Arch_ResourceId_PublisherId\..., returns the package
    /// family name "Name_PublisherId", which Windows Firewall needs to target packaged apps. Otherwise null.
    /// </summary>
    public static string? FamilyName(string path)
    {
        const string marker = @"\WindowsApps\";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = path[(at + marker.Length)..];
        var folder = rest.Split(System.IO.Path.DirectorySeparatorChar)[0];
        var parts = folder.Split('_');
        if (parts.Length < 5) return null;
        return $"{parts[0]}_{parts[^1]}";
    }
}
