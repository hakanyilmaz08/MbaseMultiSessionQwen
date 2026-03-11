using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SocialDilemmaLLMSimulation;

public static class SecureCredentialStore
{
    private const string MacServiceName = "SocialDilemmaLLMSimulation";
    private const string LegacyMacServiceName = "MbaseMultiSessionQwen";
    private const string WindowsTargetPrefix = "SocialDilemmaLLMSimulation:";
    private const string LegacyWindowsTargetPrefix = "MbaseMultiSessionQwen:";
    private const int GenericCredentialType = 1;
    private const int PersistLocalMachine = 2;
    private const int MaxCredentialBlobSize = 5 * 512;

    public static bool IsSupported
        => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public static string StoreDisplayName
        => OperatingSystem.IsMacOS() ? "macOS Keychain"
        : OperatingSystem.IsWindows() ? "Windows Credential Manager"
        : "secure OS credential storage";

    public static string NormalizeCredentialKey(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        return string.IsNullOrWhiteSpace(normalized) ? "provider" : normalized;
    }

    public static bool TryRead(string credentialKey, out string secret, out string? error)
    {
        if (OperatingSystem.IsMacOS())
            return TryReadFromMacKeychain(credentialKey, out secret, out error);

        if (OperatingSystem.IsWindows())
            return TryReadFromWindowsCredentialManager(credentialKey, out secret, out error);

        secret = string.Empty;
        error = "Secure credential storage is supported only on macOS and Windows.";
        return false;
    }

    public static bool TryWrite(string credentialKey, string secret, out string? error)
    {
        if (OperatingSystem.IsMacOS())
            return TryWriteToMacKeychain(credentialKey, secret, out error);

        if (OperatingSystem.IsWindows())
            return TryWriteToWindowsCredentialManager(credentialKey, secret, out error);

        error = "Secure credential storage is supported only on macOS and Windows.";
        return false;
    }

    private static bool TryReadFromMacKeychain(string credentialKey, out string secret, out string? error)
    {
        if (TryReadFromMacKeychainService(MacServiceName, credentialKey, out secret, out error))
            return true;

        return TryReadFromMacKeychainService(LegacyMacServiceName, credentialKey, out secret, out error);
    }

    private static bool TryReadFromMacKeychainService(string serviceName, string credentialKey, out string secret, out string? error)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.StartInfo.ArgumentList.Add("find-generic-password");
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.ArgumentList.Add(serviceName);
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add(NormalizeCredentialKey(credentialKey));
        process.StartInfo.ArgumentList.Add("-w");

        process.Start();
        secret = process.StandardOutput.ReadToEnd().Trim();
        var stderr = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(secret))
        {
            error = null;
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr) ? null : stderr;
        secret = string.Empty;
        return false;
    }

    private static bool TryWriteToMacKeychain(string credentialKey, string secret, out string? error)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/security",
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.StartInfo.ArgumentList.Add("add-generic-password");
        process.StartInfo.ArgumentList.Add("-U");
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.ArgumentList.Add(MacServiceName);
        process.StartInfo.ArgumentList.Add("-a");
        process.StartInfo.ArgumentList.Add(NormalizeCredentialKey(credentialKey));
        process.StartInfo.ArgumentList.Add("-w");
        process.StartInfo.ArgumentList.Add(secret);

        process.Start();
        var stderr = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode == 0)
        {
            error = null;
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr) ? "macOS Keychain write failed." : stderr;
        return false;
    }

    private static bool TryReadFromWindowsCredentialManager(string credentialKey, out string secret, out string? error)
    {
        if (TryReadFromWindowsCredentialManagerTarget(WindowsTargetPrefix, credentialKey, out secret, out error))
            return true;

        return TryReadFromWindowsCredentialManagerTarget(LegacyWindowsTargetPrefix, credentialKey, out secret, out error);
    }

    private static bool TryReadFromWindowsCredentialManagerTarget(string targetPrefix, string credentialKey, out string secret, out string? error)
    {
        secret = string.Empty;
        var target = targetPrefix + NormalizeCredentialKey(credentialKey);

        if (!CredReadW(target, GenericCredentialType, 0, out var credentialPtr))
        {
            error = Marshal.GetLastWin32Error() == 1168 ? null : $"CredRead failed: {Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                error = null;
                return false;
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            secret = Encoding.Unicode.GetString(blob).TrimEnd('\0');
            error = null;
            return !string.IsNullOrWhiteSpace(secret);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    private static bool TryWriteToWindowsCredentialManager(string credentialKey, string secret, out string? error)
    {
        error = null;
        var target = WindowsTargetPrefix + NormalizeCredentialKey(credentialKey);
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        if (secretBytes.Length > MaxCredentialBlobSize)
        {
            error = "Credential is too large for Windows Credential Manager.";
            return false;
        }

        var blob = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);
            var credential = new CREDENTIAL
            {
                Type = GenericCredentialType,
                TargetName = target,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = NormalizeCredentialKey(credentialKey)
            };

            if (CredWriteW(ref credential, 0))
                return true;

            error = $"CredWrite failed: {Marshal.GetLastWin32Error()}";
            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW([In] ref CREDENTIAL userCredential, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPtr);
}
