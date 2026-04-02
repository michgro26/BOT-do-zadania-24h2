namespace BuildVersionBot.Core;

public static class OsVersionResolver
{
    public static string Resolve(string? productName, string? displayVersion, string? currentBuildNumber, string? ubr)
    {
        string build = currentBuildNumber?.Trim() ?? "";

        return build switch
        {
            "18362" => "Windows 10, 1903",
            "18363" => "Windows 10, 1909",
            "19041" => "Windows 10, 2004",
            "19042" => "Windows 10, 20H2",
            "19043" => "Windows 10, 21H1",
            "19044" => "Windows 10, 21H2",
            "19045" => "Windows 10, 22H2",
            "22000" => "Windows 11, 21H2",
            "22621" => "Windows 11, 22H2",
            "22631" => "Windows 11, 23H2",
            "26100" => "Windows 11, 24h2",
            _ => BuildFallback(productName, displayVersion, build, ubr)
        };
    }

    private static string BuildFallback(string? productName, string? displayVersion, string build, string? ubr)
    {
        if (string.IsNullOrWhiteSpace(build))
            return "BŁĄD";

        string pn = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        string dv = string.IsNullOrWhiteSpace(displayVersion) ? "unknown" : displayVersion.Trim();
        string patch = string.IsNullOrWhiteSpace(ubr) ? "0" : ubr.Trim();

        return $"{pn}, {dv} (OS Build {build}.{patch})";
    }
}
