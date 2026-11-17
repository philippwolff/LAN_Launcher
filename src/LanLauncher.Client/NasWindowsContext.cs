using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LanLauncher.Client;

internal static class NasWindowsContext
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { internal uint Low; internal int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        internal Luid TokenId;
        internal Luid AuthenticationId;
        internal long ExpirationTime;
        internal int TokenType;
        internal int ImpersonationLevel;
        internal uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
        internal Luid ModifiedId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out TokenStatistics information, int length, out int returnedLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out int information, int length, out int returnedLength);

    // SID and authentication LUID are token-local: no account-name/domain lookup or SMB call.
    internal static string Capture()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            var auth = GetTokenInformation(identity.AccessToken, 10, out TokenStatistics statistics,
                Marshal.SizeOf<TokenStatistics>(), out _)
                ? $"{unchecked((uint)statistics.AuthenticationId.High):X8}:{statistics.AuthenticationId.Low:X8}"
                : "unbekannt/Windows " + Marshal.GetLastWin32Error();
            var elevation = GetTokenInformation(identity.AccessToken, 20, out int elevated, sizeof(int), out _)
                ? (elevated != 0).ToString() : "unbekannt/Windows " + Marshal.GetLastWin32Error();
            return $"PID={Environment.ProcessId}; Session={process.SessionId}; SID={identity.User?.Value}; " +
                $"LogonId={auth}; Elevated={elevation}; Impersonation={identity.ImpersonationLevel}";
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or Win32Exception)
        {
            return $"PID={Environment.ProcessId}; Windows-Kontext nicht lesbar: {error.GetType().Name}, HRESULT=0x{error.HResult:X8}";
        }
    }
}
