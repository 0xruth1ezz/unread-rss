using System.Security.Cryptography;
using System.Text;

namespace Unread.App;

internal sealed class CredentialStore(string directory)
{
    private string FilePath => Path.Combine(directory, "feedly.credential");
    public string? Read()
    {
        if (!File.Exists(FilePath)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }
    public void Save(string token)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    }
    public void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}
