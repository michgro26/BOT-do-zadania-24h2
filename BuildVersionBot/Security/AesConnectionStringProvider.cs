using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BuildVersionBot.Security;

public class AesConnectionStringProvider : IConnectionStringProvider
{
    private readonly string _secureFilePath;
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("9E1C7A4B6F2D9G8H");
    private static readonly byte[] Iv = Encoding.UTF8.GetBytes("A7C5E9B1D3F2H4J6");

    public AesConnectionStringProvider(string secureFilePath)
    {
        _secureFilePath = secureFilePath;
    }

    public bool IsConfigured() => File.Exists(_secureFilePath);

    public string GetConnectionString()
    {
        try
        {
            if (!IsConfigured())
                throw new FileNotFoundException("Plik secureconn.dat nie istnieje. Najpierw skonfiguruj połączenie.");

            string cipherText = File.ReadAllText(_secureFilePath).Trim();
            return Decrypt(cipherText);
        }
        catch (Exception ex)
        {
            throw new Exception($"Błąd odczytu connection stringa: {ex.Message}", ex);
        }
    }

    public void SaveConnectionString(string connectionString)
    {
        try
        {
            string cipherText = Encrypt(connectionString);
            File.WriteAllText(_secureFilePath, cipherText);
        }
        catch (Exception ex)
        {
            throw new Exception($"Błąd zapisu connection stringa: {ex.Message}", ex);
        }
    }

    private static string Encrypt(string plainText)
    {
        using Aes aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;

        using MemoryStream memoryStream = new();
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        using CryptoStream cryptoStream = new(memoryStream, encryptor, CryptoStreamMode.Write);
        using StreamWriter streamWriter = new(cryptoStream);
        streamWriter.Write(plainText);
        streamWriter.Flush();
        cryptoStream.FlushFinalBlock();

        return Convert.ToBase64String(memoryStream.ToArray());
    }

    private static string Decrypt(string cipherText)
    {
        byte[] buffer = Convert.FromBase64String(cipherText);

        using Aes aes = Aes.Create();
        aes.Key = Key;
        aes.IV = Iv;

        using MemoryStream memoryStream = new(buffer);
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        using CryptoStream cryptoStream = new(memoryStream, decryptor, CryptoStreamMode.Read);
        using StreamReader streamReader = new(cryptoStream);

        return streamReader.ReadToEnd();
    }
}