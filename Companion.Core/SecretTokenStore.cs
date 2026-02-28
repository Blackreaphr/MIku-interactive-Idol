using System.Security.Cryptography;

namespace Companion.Core;

public sealed class SecretTokenStore
{
    private readonly string _path;

    public SecretTokenStore(string path)
    {
        _path = path;
    }

    public string LoadOrCreate()
    {
        try
        {
            if (File.Exists(_path))
            {
                var existing = File.ReadAllText(_path).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }
        }
        catch
        {
            // Ignore and regenerate.
        }

        var token = CreateToken();
        EnsureParentDirectory();
        File.WriteAllText(_path, token);
        return token;
    }

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private void EnsureParentDirectory()
    {
        var parent = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        Directory.CreateDirectory(parent);
    }
}
