using System.Diagnostics;

namespace GlassRea.Core.Protocol;

/// <summary>
/// How much processor time Glass-Rea asks Windows for. Stored as a setting and applied by the service and the app
/// to themselves. Realtime is never offered: it can starve Windows itself.
/// </summary>
public static class Priority
{
    public const string Normal = "normal", AboveNormal = "above_normal", High = "high";

    public static bool IsValid(string? value) => value is Normal or AboveNormal or High;

    public static ProcessPriorityClass Parse(string? value) => value switch
    {
        AboveNormal => ProcessPriorityClass.AboveNormal,
        High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal,
    };

    /// <summary>Applies the setting to this process. Returns false when Windows refused.</summary>
    public static bool ApplyToSelf(string? value)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var wanted = Parse(value);
            if (self.PriorityClass != wanted) self.PriorityClass = wanted;
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
