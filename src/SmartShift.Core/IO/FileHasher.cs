using System.Security.Cryptography;

namespace SmartShift.Core.IO;

public static class FileHasher
{
    public static string Sha256Hex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
