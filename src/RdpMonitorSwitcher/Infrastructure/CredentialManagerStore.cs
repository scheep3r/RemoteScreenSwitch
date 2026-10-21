using System.ComponentModel;
using System.Runtime.InteropServices;
using RdpMonitorSwitcher.Core.Configuration;

namespace RdpMonitorSwitcher.Infrastructure;

/// <summary>
/// Stores passwords in the Windows Credential Manager (generic credentials, encrypted by
/// Windows with the user's logon credentials). Visible under "Windows-Anmeldeinformationen"
/// as "RdpMonitorSwitcher:&lt;host&gt;:&lt;port&gt;|&lt;user&gt;".
/// </summary>
internal sealed class CredentialManagerStore : ICredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public string? TryGetPassword(string key)
    {
        if (!CredRead(Target(key), CRED_TYPE_GENERIC, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND)
                return null;
            throw new Win32Exception(error);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlob == 0 || cred.CredentialBlobSize == 0)
                return null;
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void SavePassword(string key, string userName, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(key),
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(password.Length * 2),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userName,
            };
            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public void DeletePassword(string key)
    {
        if (!CredDelete(Target(key), CRED_TYPE_GENERIC, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_NOT_FOUND)
                throw new Win32Exception(error);
        }
    }

    private static string Target(string key) => "RdpMonitorSwitcher:" + key;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
