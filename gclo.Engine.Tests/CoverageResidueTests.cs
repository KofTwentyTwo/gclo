using gclo.ViewModels;

namespace gclo.Engine.Tests;

/// <summary>
/// Pins the defensive edges the mainline suites never reach: validator collision
/// branches, settings failure fallbacks, store corruption handling, and the
/// remaining status-text arms. Part of the 100%-coverage contract.
/// </summary>
public sealed class CoverageResidueTests
{
    // ------------------------------------------------- WindowsPathValidator

    [Fact]
    public void ValidatePaths_LeafCollidingWithDirectory_IsInvalid()
    {
        var invalid = WindowsPathValidator.ValidatePaths(["a/b", "a"]);

        Assert.Contains(invalid, i => i.RepoPath == "a" && i.Reason.Contains("file and a directory"));
    }

    [Fact]
    public void ValidatePaths_DirectoryCollidingWithFile_IsInvalid()
    {
        var invalid = WindowsPathValidator.ValidatePaths(["a", "a/b"]);

        Assert.Contains(invalid, i => i.RepoPath == "a" && i.Reason.Contains("file and a directory"));
    }

    [Fact]
    public void ValidatePaths_EmptySegment_IsInvalidWithSuggestion()
    {
        var invalid = WindowsPathValidator.ValidatePaths(["a//b"]);

        Assert.Contains(invalid, i => i.Reason == "empty path segment" && i.SuggestedName == "_");
    }

    // ------------------------------------------------- AppSettings failure paths

    private static (string Dir, IDisposable Scope) SettingsDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("GCLO_DATA_DIR", dir);
        return (dir, new EnvScope(dir));
    }

    private sealed class EnvScope(string dir) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GCLO_DATA_DIR", null);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Load_CorruptSettingsFile_FallsBackToDefaults()
    {
        (string dir, IDisposable scope) = SettingsDir();
        using (scope)
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");

            var settings = AppSettings.Load();

            Assert.Equal("System", settings.Theme);
            Assert.Equal(AppSettings.DefaultConcurrency, settings.DefaultMaxConcurrency);
        }
    }

    [Fact]
    public void Load_FileDeserializingToNull_FallsBackToDefaults()
    {
        (string dir, IDisposable scope) = SettingsDir();
        using (scope)
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "null");

            Assert.Equal("System", AppSettings.Load().Theme);
        }
    }

    [Fact]
    public void Load_InvalidTheme_SanitizesToSystem()
    {
        (string dir, IDisposable scope) = SettingsDir();
        using (scope)
        {
            File.WriteAllText(
                Path.Combine(dir, "settings.json"), """{ "Theme": "Hotdog" }""");

            Assert.Equal("System", AppSettings.Load().Theme);
        }
    }

    [Fact]
    public void Save_UnwritableFile_IsSwallowed()
    {
        (string dir, IDisposable scope) = SettingsDir();
        using (scope)
        {
            string path = Path.Combine(dir, "settings.json");
            using var exclusiveLock = new FileStream(
                path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            new AppSettings().Save(); // must not throw
        }
    }

    [Fact]
    public void DefaultTokenVaultId_IsStable()
        => Assert.Equal(new Guid("d0f0e0c0-9c40-4a5e-8f3a-5b1e6f7a2c11"), AppSettings.DefaultTokenVaultId);

    // ------------------------------------------------- AccountsStore edges

    private static Account MakeAccount(string name = "acc") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Organization = "org",
        TargetRoot = @"C:\x",
    };

    [Fact]
    public void RecordSyncResult_UnknownAccount_LogsAndLeavesTheListAlone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new RecordingLog();
            var store = new AccountsStore(new InMemoryVault(), dir, log);
            store.Save(MakeAccount(), "tok");

            store.RecordSyncResult(Guid.NewGuid(), DateTimeOffset.UtcNow, "summary");

            Assert.Contains(log.Errors, e => e.Contains("no account with id"));
            Assert.Null(store.GetAll()[0].LastSyncSummary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_FileDeserializingToNull_PreservesItAsCorrupt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "accounts.json"), "null");

            var store = new AccountsStore(new InMemoryVault(), dir, new RecordingLog());

            Assert.Empty(store.GetAll());
            Assert.Single(Directory.GetFiles(dir, "accounts.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_CorruptFileThatCannotBeMovedAside_LogsBothFailures()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "accounts.json");
            File.WriteAllText(path, "{ not json");
            var log = new RecordingLog();

            // No delete/move sharing: PreserveCorruptFile's File.Move must fail too.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var store = new AccountsStore(new InMemoryVault(), dir, log);
                Assert.Empty(store.GetAll());
            }

            Assert.Contains(log.Errors, e => e.Contains("Failed to load accounts"));
            Assert.Contains(log.Errors, e => e.Contains("Could not preserve"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------- RepoItemViewModel texts

    [Theory]
    [InlineData(SyncStatus.Cloning, null, "Cloning")]
    [InlineData(SyncStatus.Cloning, 0.5, "Cloning 50%")]
    [InlineData(SyncStatus.Pulling, null, "Pulling")]
    [InlineData(SyncStatus.Done, null, "Done")]
    [InlineData(SyncStatus.Failed, null, "Failed")]
    [InlineData(SyncStatus.Canceled, null, "Canceled")]
    public void StatusText_CoversEveryStatusArm(SyncStatus status, double? percent, string expected)
    {
        var item = new RepoItemViewModel(GitTestHelpers.Repo("r"))
        {
            Status = status,
            Percent = percent,
        };

        Assert.Equal(expected, item.StatusText);
    }

    [Fact]
    public void StatusText_UnknownStatusValue_FallsBackToToString()
    {
        // Defensive default arm: an out-of-range enum value renders via ToString.
        var item = new RepoItemViewModel(GitTestHelpers.Repo("r")) { Status = (SyncStatus)999 };

        Assert.Equal("999", item.StatusText);
    }

    private sealed class RecordingLog : IActivityLog
    {
        public List<string> Errors { get; } = [];
        public void Info(string message)
        {
        }
        public void Error(string message, Exception? exception = null) => Errors.Add(message);
        public string LogDirectory => "";
        public string CurrentLogFilePath => "";
    }
}
