using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace CloudFolderBrowser.Networking;

internal sealed record RemoteChecksum(string Algorithm, byte[] Expected);

internal sealed class RemoteChecksumMismatchException : IOException
{
    public RemoteChecksumMismatchException(string message) : base(message)
    {
    }
}

internal static class RemoteChecksumValidator
{
    public static RemoteChecksum? Read(
        HttpResponseMessage response,
        bool includeContentMd5 = true)
    {
        TryReadDigest(response.Headers, out RemoteChecksum? digest);
        TryReadDigest(response.Content.Headers, out RemoteChecksum? contentDigest);
        if (digest?.Algorithm == "SHA256")
            return digest;
        if (contentDigest?.Algorithm == "SHA256")
            return contentDigest;
        if (digest != null || contentDigest != null)
            return digest ?? contentDigest;
        if (TryReadGoogleHash(response.Headers, out RemoteChecksum? google)
            || TryReadGoogleHash(response.Content.Headers, out google))
            return google;
        if (!includeContentMd5)
            return null;
        if (response.Content.Headers.ContentMD5 is { Length: 16 } md5)
            return new RemoteChecksum("MD5", md5);
        if (TryReadBase64Header(response.Content.Headers, "Content-MD5", 16, out byte[]? contentMd5)
            || TryReadBase64Header(response.Headers, "Content-MD5", 16, out contentMd5))
        {
            return new RemoteChecksum("MD5", contentMd5);
        }
        return null;
    }

    public static async Task ValidateFileAsync(
        string path,
        RemoteChecksum? checksum,
        CancellationToken cancellationToken)
    {
        if (checksum == null)
            return;
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] actual = checksum.Algorithm switch
        {
            "SHA256" => await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            "MD5" => await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unsupported remote checksum {checksum.Algorithm}.")
        };
        if (!CryptographicOperations.FixedTimeEquals(actual, checksum.Expected))
            throw new RemoteChecksumMismatchException(
                $"Remote {checksum.Algorithm} checksum mismatch.");
    }

    public static async Task<string> CalculateSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false));
    }

    private static bool TryReadDigest(HttpHeaders headers, out RemoteChecksum? checksum)
    {
        checksum = null;
        if (!headers.TryGetValues("Digest", out IEnumerable<string>? values))
            return false;
        foreach (string part in string.Join(",", values).Split(','))
        {
            string[] pair = part.Trim().Split('=', 2);
            if (pair.Length != 2)
                continue;
            string algorithm = pair[0].Trim().ToLowerInvariant();
            if (algorithm is not ("sha-256" or "sha256" or "md5"))
                continue;
            bool isSha256 = algorithm.StartsWith("sha", StringComparison.Ordinal);
            if (TryDecode(pair[1].Trim().Trim(':'), out byte[]? expected)
                && expected.Length == (isSha256 ? 32 : 16))
            {
                if (isSha256)
                {
                    checksum = new RemoteChecksum("SHA256", expected);
                    return true;
                }
                checksum ??= new RemoteChecksum("MD5", expected);
            }
        }
        return checksum != null;
    }

    private static bool TryReadGoogleHash(HttpHeaders headers, out RemoteChecksum? checksum)
    {
        checksum = null;
        if (!headers.TryGetValues("x-goog-hash", out IEnumerable<string>? values))
            return false;
        foreach (string part in string.Join(",", values).Split(','))
        {
            string[] pair = part.Trim().Split('=', 2);
            if (pair.Length == 2
                && pair[0].Equals("md5", StringComparison.OrdinalIgnoreCase)
                && TryDecode(pair[1], out byte[]? expected)
                && expected.Length == 16)
            {
                checksum = new RemoteChecksum("MD5", expected);
                return true;
            }
        }
        return false;
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value.Trim().Trim('"'));
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static bool TryReadBase64Header(
        HttpHeaders headers,
        string name,
        int expectedLength,
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        return headers.TryGetValues(name, out IEnumerable<string>? values)
            && TryDecode(values.FirstOrDefault() ?? string.Empty, out bytes)
            && bytes.Length == expectedLength;
    }
}
