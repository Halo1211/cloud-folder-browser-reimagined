using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

namespace CloudFolderBrowser.Networking;

internal static class ShareCredentialCacheCodec
{
    private const string Prefix = "cfb-share-credentials:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudFolderBrowser.ShareCredentials.v1");

    internal static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    internal static string Serialize(Dictionary<string, string> values)
    {
        byte[] clear = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(values));
        try { return Prefix + Convert.ToBase64String(ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal static Dictionary<string, string> Deserialize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new();
        string json = value;
        if (IsProtected(value))
        {
            byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(value[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            try { json = Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        return (JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new())
            .Where(pair => pair.Value != null).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    // This JSON belongs inside the password-encrypted backup, so another Windows
    // user can restore it and protect it under their own account.
    internal static string ToPortableJson(string? value) => JsonConvert.SerializeObject(Deserialize(value));

    internal static string GetScopeKey(Uri source, string shareKey) =>
        source.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + "/s/" + Uri.EscapeDataString(shareKey);
}
