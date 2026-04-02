using System.Net.Sockets;

namespace BuildVersionBot.Networking;

public class PortChecker
{
    public async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using TcpClient client = new();
            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(timeoutMs);

            if (await Task.WhenAny(connectTask, timeoutTask) == connectTask)
            {
                await connectTask;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
