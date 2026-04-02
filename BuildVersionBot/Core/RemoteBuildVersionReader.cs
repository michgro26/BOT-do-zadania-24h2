using Microsoft.Win32;

namespace BuildVersionBot.Core;

public static class RemoteBuildVersionReader
{
    public static string ReadBuildVersion(string computerName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, computerName);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            if (key is null)
                return "BŁĄD";

            string? productName = key.GetValue("ProductName")?.ToString();
            string? displayVersion = key.GetValue("DisplayVersion")?.ToString();
            if (string.IsNullOrWhiteSpace(displayVersion))
                displayVersion = key.GetValue("ReleaseId")?.ToString();

            string? currentBuildNumber = key.GetValue("CurrentBuildNumber")?.ToString();
            string? ubr = key.GetValue("UBR")?.ToString();

            return OsVersionResolver.Resolve(productName, displayVersion, currentBuildNumber, ubr);
        }
        catch
        {
            return "BŁĄD";
        }
    }
}
