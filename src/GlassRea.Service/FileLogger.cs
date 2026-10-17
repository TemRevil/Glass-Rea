using System.Security.AccessControl;
using System.Security.Principal;
using GlassRea.Core.Logs;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace GlassRea.Service;

/// <summary>
/// The service's log file, C:\ProgramData\Glass-Rea\Logs\service.log, beside the Windows event log: what it did, what
/// went wrong and what was slow, for the app's Settings > Logs to pack up and send.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public static readonly LogFile File = new(Path.Combine(ServiceState.DataDirectory, "Logs"), "service");

    /// <summary>Log with this event to mark something that worked but took too long.</summary>
    public static readonly EventId Slow = new(1, "Slow");

    /// <summary>
    /// Makes the logs folder this service's own before anything is written. Everyone may add files to ProgramData's
    /// folders, so a folder already there could be someone's link to a system folder, which this service, running as
    /// SYSTEM, would then write into; it's replaced with one only SYSTEM and administrators can change.
    /// </summary>
    public static void PrepareFolder()
    {
        var dir = new DirectoryInfo(File.Directory);
        try
        {
            if (dir.Exists && (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsTrusted(dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)))))
                dir.Delete(recursive: true);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var (sid, rights) in new[]
                     {
                         (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
                         (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
                         (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute),
                     })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            dir.Refresh();
            if (dir.Exists) dir.SetAccessControl(security);
            else dir.Create(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logging stays off rather than unsafe */ }
    }

    private static bool IsTrusted(IdentityReference? owner) => owner is SecurityIdentifier sid &&
        (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

    public ILogger CreateLogger(string category) => new FileLogger(category[(category.LastIndexOf('.') + 1)..]);

    public void Dispose() { }

    private sealed class FileLogger(string source) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level)) return;
            var kind = level switch
            {
                LogLevel.Critical => Core.Logs.LogLevel.Crash,
                LogLevel.Error => Core.Logs.LogLevel.Error,
                LogLevel.Warning => Core.Logs.LogLevel.Warning,
                _ => id.Name == Slow.Name ? Core.Logs.LogLevel.Slow : Core.Logs.LogLevel.Info,
            };
            File.Write(kind, source, format(state, error), error);
        }
    }
}
