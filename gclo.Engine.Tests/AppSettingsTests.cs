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
