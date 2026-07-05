using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace gclo.ViewModels;

/// <summary>
/// An <see cref="ITokenVault"/> backed by the Windows Credential Manager: each token
/// is a generic credential in the per-user store (persisted across reboots) whose
/// blob is the token's UTF-16 bytes, under the target name "gclo:account:&lt;id&gt;".
/// Talks to advapi32 directly, so it works packaged or unpackaged. Construction on a
/// non-Windows OS throws <see cref="PlatformNotSupportedException"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialManagerVault : ITokenVault
{
    private const uint CredTypeGeneric = 1;         // CRED_TYPE_GENERIC
    private const uint CredPersistLocalMachine = 2; // CRED_PERSIST_LOCAL_MACHINE: per-user, survives reboot
    private const int ErrorNotFound = 1168;         // ERROR_NOT_FOUND

    /// <summary>Fails fast on platforms without a Credential Manager.</summary>
    public CredentialManagerVault()
        : this(OperatingSystem.IsWindows())
    {
    }

    /// <summary>Test seam: the platform check is a parameter so both arms are coverable.</summary>
    internal CredentialManagerVault(bool isWindows)
    {
        if (!isWindows)
        {
            throw new PlatformNotSupportedException(
                "Account token storage requires the Windows Credential Manager.");
        }
    }

    /// <inheritdoc/>
    public void Store(Guid accountId, string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        byte[] blob = Encoding.Unicode.GetBytes(token);
        var pinned = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new CredentialW
            {
                Type = CredTypeGeneric,
                TargetName = TargetName(accountId),
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = pinned.AddrOfPinnedObject(),
                Persist = CredPersistLocalMachine,
                UserName = "gclo",
            };

            if (!CredWriteW(ref credential, 0))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error, $"CredWriteW failed (error {error}) storing the token for account {accountId:N}.");
            }
        }
        finally
        {
            // The array is pinned, so the GC cannot have copied it elsewhere: clearing
            // it removes the one deterministic plaintext copy of the token from the
            // heap (crash dumps, pagefile) before unpinning.
            Array.Clear(blob);
            pinned.Free();
        }
    }

    /// <inheritdoc/>
    public string? TryRetrieve(Guid accountId) => TryRetrieve(TargetName(accountId), accountId);

    /// <summary>Test seam: an invalid target name makes the non-not-found error arm coverable.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1822:Mark members as static",
        Justification = "Instance seam: tests call it on a constructed vault; static would be a CS0176.")]
    internal string? TryRetrieve(string targetName, Guid accountId)
    {
        if (!CredReadW(targetName, CredTypeGeneric, 0, out IntPtr credentialPtr))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }
            throw new Win32Exception(
                error, $"CredReadW failed (error {error}) reading the token for account {accountId:N}.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<CredentialW>(credentialPtr);
            return credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    /// <inheritdoc/>
    public void Delete(Guid accountId) => Delete(TargetName(accountId), accountId);

    /// <summary>Test seam: an invalid target name makes the non-not-found error arm coverable.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance", "CA1822:Mark members as static",
        Justification = "Instance seam: tests call it on a constructed vault; static would be a CS0176.")]
    internal void Delete(string targetName, Guid accountId)
    {
        if (!CredDeleteW(targetName, CredTypeGeneric, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(
                    error, $"CredDeleteW failed (error {error}) deleting the token for account {accountId:N}.");
            }
        }
    }

    private static string TargetName(Guid accountId) => $"gclo:account:{accountId:N}";

    /// <summary>
    /// Managed mirror of the native CREDENTIALW structure. The explicit StructLayout
    /// both fixes the field order for marshaling and tells the compiler the unread
    /// fields (filled by CredReadW) are intentional.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredentialW
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref CredentialW credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, uint type, uint flags);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr buffer);
}
