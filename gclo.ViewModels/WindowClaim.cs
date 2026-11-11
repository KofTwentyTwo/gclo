/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;


namespace gclo.ViewModels;


/// <summary>
/// One gclo window per data folder (#216). Two windows on one folder each kept
/// their own copy of the settings and of the accounts, and the one that saved
/// last undid what the other had saved. A window therefore claims its data
/// folder while it is open; a second start finds the claim, asks the open window
/// to come to the front, and ends.
/// <para>
/// The claim is a file in the data folder (<c>window.lock</c>) held open and
/// shared with nobody: Windows gives it up when the process ends, however it
/// ends, and the file is removed with it. The request to come to the front is a
/// named event, named after the folder. Another data folder (the dev channel
/// beside the stable one, <c>GCLO_DATA_DIR</c>) is another claim.
/// </para>
/// <para>
/// The command line makes no claim: it opens no window and may run beside the app.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowClaim : IDisposable
{
   /// <summary>The file is open in another process (ERROR_SHARING_VIOLATION as an HRESULT).</summary>
   private const int SharingViolation = unchecked((int)0x80070020);

   private readonly FileStream? _held;

   private readonly EventWaitHandle _show;

   private RegisteredWaitHandle? _waiting;



   private WindowClaim(FileStream? held, bool isTheOnlyOne, EventWaitHandle show)
   {
      _held = held;
      IsTheOnlyOne = isTheOnlyOne;
      _show = show;
   }



   /// <summary>
   /// Claims <paramref name="dataRoot"/> for a window of this process. When
   /// another window holds it, <see cref="IsTheOnlyOne"/> is false. A folder that
   /// cannot be written to holds no claim and stops no window from opening: what
   /// cannot be saved there is reported where it is saved.
   /// </summary>
   public static WindowClaim For(string dataRoot)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
      FileStream? held = null;
      bool anotherHoldsIt = false;
      try
      {
         Directory.CreateDirectory(dataRoot);
         held = new FileStream(
             Path.Combine(dataRoot, "window.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
             bufferSize: 1, FileOptions.DeleteOnClose);
      }
      catch(IOException ex) when(ex.HResult == SharingViolation)
      {
         anotherHoldsIt = true;
      }
      catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
      {
         // No claim can be made here (a read-only profile, a path that is no folder): the window opens all the same.
      }

      var show = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, $@"Local\gclo-show-{KeyFor(dataRoot)}");
      return new WindowClaim(held, !anotherHoldsIt, show);
   }



   /// <summary>
   /// What the request to come to the front is named after: the folder as Windows
   /// knows it, whatever its spelling (case, a separator at the end, a relative path).
   /// </summary>
   public static string KeyFor(string dataRoot)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
      string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant();
      return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)), 0, 16);
   }



   /// <summary>True when no other window holds the data folder: this process may open one.</summary>
   public bool IsTheOnlyOne { get; }



   /// <summary>Asks the window that holds the data folder to come to the front.</summary>
   public void AskTheOtherToShowItself() => _show.Set();



   /// <summary>
   /// What the window that holds the claim does when another start asks it to come
   /// to the front. <paramref name="show"/> runs on a thread of the pool, once for
   /// each request; setting it again replaces the one before.
   /// </summary>
   public void OnAskedToShow(Action show)
   {
      ArgumentNullException.ThrowIfNull(show);
      _waiting?.Unregister(null);
      _waiting = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), state: null, Timeout.Infinite, executeOnlyOnce: false);
   }



   /// <summary>Gives the claim up: the next start opens a window of its own.</summary>
   public void Dispose()
   {
      _waiting?.Unregister(null);
      _waiting = null;
      _show.Dispose();
      _held?.Dispose();
   }
}
