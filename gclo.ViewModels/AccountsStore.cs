using System.Text.Json;
using System.Text.Json.Serialization;
using gclo.Engine;

namespace gclo.ViewModels;

/// <summary>
/// Persists account profiles as JSON at %LOCALAPPDATA%\gclo\accounts.json and their
/// tokens in an <see cref="ITokenVault"/>; the file holds metadata only and never a
/// token. Unlike <see cref="AppSettings"/>, accounts are primary user data, so write
/// failures in <see cref="Save"/>, <see cref="Delete"/>, and
/// <see cref="RecordSyncResult"/> propagate instead of being swallowed; only loading
/// is tolerant (a missing or corrupt file yields an empty list).
/// </summary>
public sealed class AccountsStore
{
    private readonly ITokenVault _vault;
    private readonly IActivityLog _log;
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<Account> _accounts;

    /// <summary>
    /// Loads existing accounts from <paramref name="directory"/> (default:
    /// %LOCALAPPDATA%\gclo). A missing file simply means no accounts yet; a corrupt
    /// or unreadable one is logged (pass the app's <paramref name="log"/> to surface
    /// that) and treated as empty rather than blocking startup.
    /// </summary>
    public AccountsStore(ITokenVault vault, string? directory = null, IActivityLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _vault = vault;
        _log = log ?? new NullActivityLog();
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "gclo");
        _filePath = Path.Combine(directory, "accounts.json");
        _accounts = Load();
    }

    /// <summary>All accounts, sorted by <see cref="Account.Name"/> (case-insensitive).</summary>
    public IReadOnlyList<Account> GetAll()
    {
        lock (_gate)
        {
            return _accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>The account whose name matches case-insensitively, or null.</summary>
    public Account? FindByName(string name)
    {
        lock (_gate)
        {
            return _accounts.FirstOrDefault(
                a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Inserts or updates (matching by <see cref="Account.Id"/>) and persists
    /// immediately. Throws <see cref="ArgumentException"/> when another account
    /// already uses the name (case-insensitive); nothing is persisted in that case.
    /// A non-null <paramref name="token"/> goes into the vault only after the
    /// metadata write succeeds, so a vault entry never points at an unsaved account;
    /// a null token leaves any existing vault entry untouched.
    /// </summary>
    public void Save(Account account, string? token)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_gate)
        {
            bool nameTaken = _accounts.Any(a =>
                a.Id != account.Id
                && string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase));
            if (nameTaken)
            {
                throw new ArgumentException(
                    $"An account named '{account.Name}' already exists.", nameof(account));
            }

            var updated = new List<Account>(_accounts);
            int index = updated.FindIndex(a => a.Id == account.Id);
            if (index >= 0)
            {
                updated[index] = account;
            }
            else
            {
                updated.Add(account);
            }

            Persist(updated); // IO failures propagate; _accounts stays unchanged then.
            _accounts = updated;

            if (token is not null)
            {
                _vault.Store(account.Id, token);
            }
        }
    }

    /// <summary>
    /// Removes the account's metadata and vault token; an unknown id is a no-op.
    /// The vault entry is deleted only after the metadata write succeeds.
    /// </summary>
    public void Delete(Guid id)
    {
        lock (_gate)
        {
            var remaining = _accounts.Where(a => a.Id != id).ToList();
            if (remaining.Count == _accounts.Count)
            {
                return;
            }

            Persist(remaining);
            _accounts = remaining;
            _vault.Delete(id);
        }
    }

    /// <summary>
    /// Stamps the outcome of a finished sync onto the account and persists it; every
    /// other field is left untouched. An unknown id (account deleted while its sync
    /// ran) is logged and ignored.
    /// </summary>
    public void RecordSyncResult(Guid id, DateTimeOffset lastSyncUtc, string summary)
    {
        lock (_gate)
        {
            int index = _accounts.FindIndex(a => a.Id == id);
            if (index < 0)
            {
                _log.Error($"Cannot record a sync result: no account with id {id:N}.");
                return;
            }

            var updated = new List<Account>(_accounts)
            {
                [index] = _accounts[index] with { LastSyncUtc = lastSyncUtc, LastSyncSummary = summary },
            };
            Persist(updated);
            _accounts = updated;
        }
    }

    private List<Account> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var loaded = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath), AccountsJsonContext.Default.ListAccount);
            if (loaded is not null)
            {
                return loaded;
            }
            _log.Error($"Accounts file '{_filePath}' deserialized to null; starting with no accounts.");
            PreserveCorruptFile();
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load accounts from '{_filePath}'; starting with no accounts.", ex);
            PreserveCorruptFile();
        }

        return [];
    }

    /// <summary>
    /// Moves an unreadable accounts file aside before the empty in-memory list can be
    /// persisted over it: the metadata is what associates vault entries with accounts,
    /// so clobbering a recoverable file would orphan every stored token.
    /// </summary>
    private void PreserveCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                string aside = $"{_filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                File.Move(_filePath, aside, overwrite: true);
                _log.Error($"Preserved unreadable accounts file as '{aside}'.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Could not preserve the unreadable accounts file.", ex);
        }
    }

    private void Persist(List<Account> accounts)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Atomic replace: a crash or full disk mid-write must never truncate the live
        // file — corrupted metadata orphans every vault token (accounts are found by
        // Guid, and recreated accounts mint new ones).
        string json = JsonSerializer.Serialize(accounts, AccountsJsonContext.Default.ListAccount);
        string tempPath = _filePath + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_filePath))
        {
            File.Replace(tempPath, _filePath, _filePath + ".bak");
        }
        else
        {
            File.Move(tempPath, _filePath);
        }
    }
}

/// <summary>
/// Source-generated serializer context so account (de)serialization keeps working in
/// trimmed Release publishes without reflection warnings.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<Account>))]
internal sealed partial class AccountsJsonContext : JsonSerializerContext
{
}
