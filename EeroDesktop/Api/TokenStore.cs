using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EeroDesktop.Api;

/// <summary>Stores the eero session token encrypted with Windows DPAPI (current user only).</summary>
public static class TokenStore
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EeroDesktop");
    // EERO_DESKTOP_SESSION_FILE lets automated tests use a separate (or empty) session without touching the real one.
    private static readonly string FilePath = Environment.GetEnvironmentVariable("EERO_DESKTOP_SESSION_FILE") is { Length: > 0 } custom
        ? custom : Path.Combine(Dir, "session.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EeroDesktop.session.v1");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception) { return null; }
    }

    public static void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (IOException) { }
    }
}
