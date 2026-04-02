using System.Globalization;
using System.Text;

namespace BuildVersionBot.Core;

public sealed class FileLogger
{
    private readonly string _logDirectory;
    private readonly object _lock = new();

    public FileLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            var filePath = GetCurrentLogFilePath();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";

            lock (_lock)
            {
                File.AppendAllText(filePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // logger nie może zatrzymać programu
        }
    }

    private string GetCurrentLogFilePath()
    {
        string fileName = $"log_{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.txt";
        return Path.Combine(_logDirectory, fileName);
    }
}
