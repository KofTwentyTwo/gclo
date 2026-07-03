using System.Globalization;
using System.Text.RegularExpressions;
using gclo.Engine;

namespace gclo.Engine.Tests;

/// <summary>
/// Tests for <see cref="FileActivityLog"/> (line format, daily file naming, lazy
/// directory creation, swallowing IO failures, thread safety) and
/// <see cref="NullActivityLog"/> (pure no-op).
/// </summary>
public sealed class ActivityLogTests : IDisposable
{
    /// <summary>"yyyy-MM-dd HH:mm:ss.fff [LEVEL] " prefix every entry line must carry.</summary>
    private static readonly Regex LinePattern = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(INFO|ERROR)\] ", RegexOptions.Compiled);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort; stray empty temp dirs are harmless.
        }
    }

    // ---------------------------------------------------------------- line format

    [Fact]
    public void Info_WritesOneTimestampedLine_WithInfoTag()
    {
        var log = new FileActivityLog(_root);

        log.Info("hello world");

        string line = Assert.Single(File.ReadAllLines(log.CurrentLogFilePath));
        Assert.Matches(LinePattern, line);
        Assert.Contains("[INFO] ", line);
        Assert.EndsWith("hello world", line);
    }

    [Fact]
    public void Error_WithoutException_WritesOneLineWithErrorTag()
    {
        var log = new FileActivityLog(_root);

        log.Error("something failed");

        string line = Assert.Single(File.ReadAllLines(log.CurrentLogFilePath));
        Assert.Matches(LinePattern, line);
        Assert.Contains("[ERROR] ", line);
        Assert.EndsWith("something failed", line);
    }

    [Fact]
    public void Error_WithException_AppendsExceptionTextOnFollowingLines()
    {
        var log = new FileActivityLog(_root);

        log.Error("sync blew up", new InvalidOperationException("kaboom"));

        string[] lines = File.ReadAllLines(log.CurrentLogFilePath);
        Assert.True(lines.Length >= 2, "exception text belongs on lines after the message");
        Assert.Matches(LinePattern, lines[0]);
        Assert.EndsWith("sync blew up", lines[0]);
        Assert.Contains("InvalidOperationException", lines[1]);
        Assert.Contains("kaboom", lines[1]);
    }

    [Fact]
    public void Writes_Accumulate_InTheSameFile()
    {
        var log = new FileActivityLog(_root);

        log.Info("first");
        log.Error("second");
        log.Info("third");

        string[] lines = File.ReadAllLines(log.CurrentLogFilePath);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Matches(LinePattern, line));
        Assert.EndsWith("first", lines[0]);
        Assert.EndsWith("second", lines[1]);
        Assert.EndsWith("third", lines[2]);
    }

    // ---------------------------------------------------------------- paths

    [Fact]
    public void CurrentLogFilePath_IsTodaysDailyFile_UnderLogDirectory()
    {
        var log = new FileActivityLog(_root);

        string expected = Path.Combine(
            _root,
            "gclo-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
        Assert.Equal(_root, log.LogDirectory);
        Assert.Equal(expected, log.CurrentLogFilePath);
    }

    [Fact]
    public void DefaultDirectory_IsLocalAppDataGcloLogs()
    {
        var log = new FileActivityLog();

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gclo", "logs");
        Assert.Equal(expected, log.LogDirectory);
    }

    [Fact]
    public void Directory_IsCreatedLazily_OnFirstWriteNotInConstructor()
    {
        string directory = Path.Combine(_root, "lazy");

        var log = new FileActivityLog(directory);
        Assert.False(Directory.Exists(directory), "constructor must not create the directory");

        log.Info("first write");

        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(log.CurrentLogFilePath));
    }

    // ---------------------------------------------------------------- never throws

    [Fact]
    public void Write_WhenDirectoryIsUnwritable_NeverThrows()
    {
        // A file sitting where the log directory should go makes CreateDirectory
        // throw; the log must swallow that on every call.
        Directory.CreateDirectory(_root);
        string fileAsDirectory = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(fileAsDirectory, "blocks directory creation");

        var log = new FileActivityLog(fileAsDirectory);

        log.Info("goes nowhere");
        log.Error("still nowhere", new InvalidOperationException("boom"));

        // The properties still answer; only the writes are lost.
        Assert.Equal(fileAsDirectory, log.LogDirectory);
        Assert.StartsWith(fileAsDirectory, log.CurrentLogFilePath);
    }

    // ---------------------------------------------------------------- thread safety

    [Fact]
    public void ParallelWrites_ProduceOnlyWholeWellFormedLines()
    {
        var log = new FileActivityLog(_root);
        const int writers = 4;
        const int perWriter = 25;

        Parallel.For(0, writers, writer =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                log.Info($"writer {writer} message {i}");
            }
        });

        string[] lines = File.ReadAllLines(log.CurrentLogFilePath);
        Assert.Equal(writers * perWriter, lines.Length);
        Assert.All(lines, line => Assert.Matches(LinePattern, line));
    }

    // ---------------------------------------------------------------- NullActivityLog

    [Fact]
    public void NullActivityLog_NoOps_AndReturnsEmptyStrings()
    {
        var log = new NullActivityLog();

        log.Info("ignored");
        log.Error("ignored too", new InvalidOperationException("ignored as well"));

        Assert.Equal("", log.LogDirectory);
        Assert.Equal("", log.CurrentLogFilePath);
    }
}
