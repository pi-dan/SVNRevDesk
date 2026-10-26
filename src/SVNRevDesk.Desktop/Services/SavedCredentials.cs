using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SVNRevDesk.Desktop.Services;

public sealed class SavedCredentials(string storageDirectory)
{
    public sealed record Login(string Username, string Password);
    private string FileFor(string target)
    {
        var key = Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile
            ? uri.AbsoluteUri.TrimEnd('/') : Path.GetFullPath(target).TrimEnd('\\').ToUpperInvariant();
        return Path.Combine(storageDirectory, "Credentials", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".bin");
    }
    public Login? Load(string target)
    {
        try
        {
            var file = FileFor(target);
            if (!File.Exists(file)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<Login>(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { return null; }
    }
    public void Save(string target, Login login)
    {
        var file = FileFor(target);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(login);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, file, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Forget(string target)
    {
        var file = FileFor(target);
        if (File.Exists(file)) File.Delete(file);
    }
}
