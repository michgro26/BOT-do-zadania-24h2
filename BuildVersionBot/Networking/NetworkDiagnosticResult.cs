namespace BuildVersionBot.Networking;

public class NetworkDiagnosticResult
{
    public string? Hostname { get; set; }
    public bool IsActive { get; set; }
    public bool IsVpn { get; set; }
    public string? IpAddress { get; set; }
}
