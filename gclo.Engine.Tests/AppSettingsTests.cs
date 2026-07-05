using gclo.ViewModels;

namespace gclo.Engine.Tests;

/// <summary>Pins defaults and load-time sanitizing of <see cref="AppSettings"/>.</summary>
public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_SplashOnAtFiveSeconds()
    {
        var settings = new AppSettings();

        Assert.True(settings.ShowSplashScreen);
        Assert.Equal(5_000, settings.SplashMilliseconds);
    }

    [Fact]
    public void Load_SettingsFileFromOlderVersion_MigratesSplashToDefault()
    {
        // A pre-splash settings.json has no SplashMilliseconds field; it must load
        // as the 5s default, not as 0 clamped to the minimum.
        string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GCLO_DATA_DIR", dir);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "settings.json"),
                """{ "DefaultTargetFolder": "C:\\src", "DefaultMaxConcurrency": 8, "Theme": "Dark" }""");

            var loaded = AppSettings.Load();

            Assert.Equal(AppSettings.DefaultSplashMilliseconds, loaded.SplashMilliseconds);
            Assert.True(loaded.ShowSplashScreen);
            Assert.Equal("Dark", loaded.Theme); // existing values still honored
        }
        finally
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
    public void Save_ClampsSplashDuration()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GCLO_DATA_DIR", dir);
        try
        {
            var settings = new AppSettings { SplashMilliseconds = 999_999 };
            settings.Save();
            Assert.Equal(AppSettings.MaxSplashMilliseconds, settings.SplashMilliseconds);

            settings.SplashMilliseconds = 1;
            settings.Save();
            Assert.Equal(AppSettings.MinSplashMilliseconds, settings.SplashMilliseconds);

            // Round trip: the persisted values load back as saved.
            var loaded = AppSettings.Load();
            Assert.Equal(AppSettings.MinSplashMilliseconds, loaded.SplashMilliseconds);
            Assert.True(loaded.ShowSplashScreen);
        }
        finally
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
}
