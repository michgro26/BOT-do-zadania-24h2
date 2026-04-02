using System.Net.NetworkInformation;

namespace BuildVersionBot.Networking;

public class NetworkDiagnosticService : INetworkDiagnosticService
{
    private readonly PortChecker _portChecker = new();

    public async Task<NetworkDiagnosticResult> IsHostActiveAsync(string host)
    {
        var result = new NetworkDiagnosticResult { Hostname = host };

        if (string.IsNullOrWhiteSpace(host))
            return result;

        using (Ping ping = new())
        {
            try
            {
                var reply = await ping.SendPingAsync(host, 800);
                if (reply.Status == IPStatus.Success)
                {
                    result.IsActive = true;
                    result.IsVpn = false;
                    result.IpAddress = reply.Address?.ToString();
                    return result;
                }
            }
            catch
            {
            }
        }

        bool smbOpen = await _portChecker.IsPortOpenAsync(host, 445, 500);
        if (smbOpen)
        {
            result.IsActive = true;
            result.IsVpn = true;
            return result;
        }

        result.IsActive = false;
        return result;
    }
}
