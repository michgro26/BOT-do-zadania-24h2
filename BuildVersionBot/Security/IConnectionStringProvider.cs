namespace BuildVersionBot.Security;

public interface IConnectionStringProvider
{
    bool IsConfigured();
    string GetConnectionString();
    void SaveConnectionString(string connectionString);
}