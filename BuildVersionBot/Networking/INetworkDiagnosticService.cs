namespace BuildVersionBot.Networking;

public interface INetworkDiagnosticService
{
    Task<NetworkDiagnosticResult> IsHostActiveAsync(string host);
}
