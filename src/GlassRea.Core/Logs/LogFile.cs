using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace GlassRea.Core.Logs;

/// <summary>How serious a log line is. Slow and Freeze are for things that worked but kept you waiting.</summary>
public enum LogLevel { Info, Slow, Freeze, Warning, Error, Crash }

/// <summary>
/// A plain-text log that stays small: one line per event (more when there's an exception), and when the file passes
/// <paramref name="maxBytes"/> it becomes "name.1.log", the older ones move up, and the oldest past
/// <paramref name="keep"/> is deleted. Safe to write from any thread; other programs can read it while it's open.
/// </summary>
public sealed class LogFile(string directory, string name, long maxBytes = 1_000_000, int keep = 4)
{
    private readonly Lock _gate = new();

    public string Directory => directory;
    public string Path => System.IO.Path.Combine(directory, name + ".log");

    public void Write(LogLevel level, string source, string message, Exception? error = null)
    {
        var line = Format(DateTimeOffset.Now, level, source, message, error);
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                if (File.Exists(Path) && new FileInfo(Path).Length + line.Length > maxBytes) Rotate();
                using var file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                file.Write(Encoding.UTF8.GetBytes(line));
            }
            catch (IOException) { /* a full or locked disk mustn't take the program down with it */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>"2026-10-17 19:42:01.123 +03:00 SLOW    Apps page: opened in 1.4 s", then the exception, indented.</summary>
    public static string Format(DateTimeOffset time, LogLevel level, string source, string message, Exception? error)
    {
        var b = new StringBuilder();
        b.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)).Append(' ')
            .Append(level.ToString().ToUpperInvariant().PadRight(8)).Append(source).Append(": ")
            .Append(message.ReplaceLineEndings(" | "));
        b.Append('\n');
        if (error is not null)
            foreach (var l in error.ToString().ReplaceLineEndings("\n").Split('\n'))
                if (l.Length > 0) b.Append("    ").Append(l).Append('\n');
        return b.ToString();
    }

    /// <summary>This log's files, newest first.</summary>
    public IEnumerable<string> Files() =>
        Enumerable.Range(0, keep + 1).Select(i => i == 0 ? Path : System.IO.Path.Combine(directory, $"{name}.{i}.log")).Where(File.Exists);

    private void Rotate()
    {
        var oldest = System.IO.Path.Combine(directory, $"{name}.{keep}.log");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = keep - 1; i >= 1; i--)
        {
            var from = System.IO.Path.Combine(directory, $"{name}.{i}.log");
            if (File.Exists(from)) File.Move(from, System.IO.Path.Combine(directory, $"{name}.{i + 1}.log"));
        }
        File.Move(Path, System.IO.Path.Combine(directory, $"{name}.1.log"));
    }
}

/// <summary>The logs packed into one .zip to send: every .log file of each folder given, and a note about the PC.</summary>
public static class LogBundle
{
    /// <param name="folders">Each folder of logs, and the folder name it gets inside the zip ("app", "service").</param>
    /// <param name="about">What goes in about.txt: versions, Windows, when it was made.</param>
    public static void Write(Stream output, IEnumerable<(string Name, string Directory)> folders, string about)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        using (var w = new StreamWriter(zip.CreateEntry("about.txt").Open(), new UTF8Encoding(false))) w.Write(about);
        foreach (var (folder, dir) in folders)
        {
            if (!System.IO.Directory.Exists(dir)) continue;
            string[] files;
            try { files = System.IO.Directory.GetFiles(dir, "*.log"); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                try
                {
                    // Shared, since the program writing it may still have it open.
                    using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = zip.CreateEntry($"{folder}/{System.IO.Path.GetFileName(file)}").Open();
                    source.CopyTo(target);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
