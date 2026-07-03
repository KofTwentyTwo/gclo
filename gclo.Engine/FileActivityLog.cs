using System.Globalization;

namespace gclo.Engine;

/// <summary>
/// An <see cref="IActivityLog"/> that appends timestamped lines to a daily file,
/// gclo-yyyy-MM-dd.log. Writes are serialized by a lock and each one opens,
/// appends, and closes the file, so every entry reaches disk even if the process
/// dies right after. Logging must never take the application down: all IO
/// failures (unwritable directory, locked file, full disk) are swallowed. The
/// directory is created lazily on the first write, never in the constructor.
/// </summary>
public sealed class FileActivityLog : IActivityLog
{
    private readonly object _gate = new();

    /// <summary>Creates a log that writes under <paramref name="directory"/>.</summary>
    /// <param name="directory">
    /// Folder for the log files; defaults to %LOCALAPPDATA%\gclo\logs when null or blank.
    /// </param>
    public FileActivityLog(string? directory = null)
    {
        LogDirectory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "gclo", "logs")
            : directory;
    }

    /// <inheritdoc/>
    public string LogDirectory { get; }

    /// <summary>Today's log file; entries roll over to a new file at midnight.</summary>
    public string CurrentLogFilePath
        => Path.Combine(
            LogDirectory,
            "gclo-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    /// <inheritdoc/>
    public void Info(string message) => Write("INFO", message, exception: null);

    /// <inheritdoc/>
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(LogDirectory);

                string timestamp = DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string entry = $"{timestamp} [{level}] {message}";
                if (exception is not null)
                {
                    entry += Environment.NewLine + exception;
                }

                File.AppendAllText(CurrentLogFilePath, entry + Environment.NewLine);
            }
        }
        catch
        {
            // A logging failure must never take the operation it describes down.
        }
    }
}

/// <summary>An <see cref="IActivityLog"/> that discards everything.</summary>
public sealed class NullActivityLog : IActivityLog
{
    /// <inheritdoc/>
    public string LogDirectory => "";

    /// <inheritdoc/>
    public string CurrentLogFilePath => "";

    /// <inheritdoc/>
    public void Info(string message)
    {
    }

    /// <inheritdoc/>
    public void Error(string message, Exception? exception = null)
    {
    }
}
