using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace WinIMsg.Core.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore
{
    private readonly string _root;

    public DpapiSecretStore(string root)
    {
        _root = root;
    }

    public void Save(string name, string value)
    {
        Directory.CreateDirectory(_root);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(name), protectedBytes);
    }

    public string? Read(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = ProtectedData.Unprotect(
            File.ReadAllBytes(path),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string name)
    {
        var safeName = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return Path.Combine(_root, $"{safeName}.bin");
    }
}
