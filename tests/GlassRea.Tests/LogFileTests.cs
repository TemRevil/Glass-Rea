using System.IO.Compression;
using GlassRea.Core.Logs;

namespace GlassRea.Tests;

public class LogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glassrea-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ALineSaysWhenWhatAndWhereWithTheExceptionUnderIt()
    {
        var time = new DateTimeOffset(2026, 10, 17, 19, 42, 1, 123, TimeSpan.FromHours(3));
        Exception error;
        try { throw new InvalidOperationException("pipe broke"); } catch (Exception e) { error = e; }
        var text = LogFile.Format(time, LogLevel.Slow, "Apps page", "opened in 1.4 s\r\nsecond line", error);
        var lines = text.Split('\n');
        Assert.Equal("2026-10-17 19:42:01.123 +03:00 SLOW    Apps page: opened in 1.4 s | second line", lines[0]);
        Assert.Equal("    System.InvalidOperationException: pipe broke", lines[1]);
        Assert.All(lines.Skip(1).Where(l => l.Length > 0), l => Assert.StartsWith("    ", l));
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void AFullLogMovesAsideAndOnlyTheNewestFewAreKept()
    {
        var log = new LogFile(_dir, "app", maxBytes: 300, keep: 2);
        for (var i = 0; i < 40; i++) log.Write(LogLevel.Info, "Test", $"event number {i:00} with some words to fill the line");
        var files = log.Files().ToList();
        Assert.Equal([Path.Combine(_dir, "app.log"), Path.Combine(_dir, "app.1.log"), Path.Combine(_dir, "app.2.log")], files);
        Assert.False(File.Exists(Path.Combine(_dir, "app.3.log")));
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 300));
        Assert.Contains("event number 39", File.ReadAllText(files[0]));
        Assert.DoesNotContain("event number 00", string.Concat(files.Select(File.ReadAllText)));
    }

    [Fact]
    public void TheBundleHoldsEveryLogEvenOneStillOpenForWriting()
    {
        var app = new LogFile(Path.Combine(_dir, "a"), "app");
        app.Write(LogLevel.Error, "Test", "it went wrong");
        Directory.CreateDirectory(Path.Combine(_dir, "s"));
        File.WriteAllText(Path.Combine(_dir, "s", "service.log"), "service line\n");
        File.WriteAllText(Path.Combine(_dir, "s", "notes.txt"), "not a log");
        using var open = new FileStream(app.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        using var zipped = new MemoryStream();
        LogBundle.Write(zipped, [("app", app.Directory), ("service", Path.Combine(_dir, "s")), ("missing", Path.Combine(_dir, "nope"))], "Glass-Rea 1.2.2");
        zipped.Position = 0;
        using var zip = new ZipArchive(zipped);
        Assert.Equal(["about.txt", "app/app.log", "service/service.log"], zip.Entries.Select(e => e.FullName).Order());
        using var reader = new StreamReader(zip.GetEntry("app/app.log")!.Open());
        Assert.Contains("it went wrong", reader.ReadToEnd());
    }
}
