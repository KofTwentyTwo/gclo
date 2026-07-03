namespace gclo.Engine;

/// <summary>
/// Application activity log for human-readable operational events. Implementations
/// must be safe to call from any thread and must never throw from a logging call;
/// secrets (tokens) must never be written to it.
/// </summary>
public interface IActivityLog
{
    /// <summary>Records an informational event.</summary>
    void Info(string message);

    /// <summary>Records a failure, with the exception's full text when one is given.</summary>
    void Error(string message, Exception? exception = null);

    /// <summary>Folder that holds the log files.</summary>
    string LogDirectory { get; }

    /// <summary>Full path of the file new entries are currently appended to.</summary>
    string CurrentLogFilePath { get; }
}
