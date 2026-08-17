using System.Security.Cryptography;
using System.Text;

namespace RimContext.Core.Model;

public static class StableEntityId
{
    public static string Create(string kind, string scope, string semanticIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticIdentity);

        var canonical = string.Join('\0',
            NormalizePart(kind),
            NormalizePart(scope),
            NormalizePart(semanticIdentity));

        return $"{NormalizePart(kind)}:{Base32Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).Substring(0, 26)}";
    }

    public static string DigestBase32(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Base32Url(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/');
        var segments = new List<string>();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new ArgumentException("Path traversal escapes its root.", nameof(path));
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private static string NormalizePart(string value) => value.Trim().ToLowerInvariant().Replace('\\', '/');

    private static string Base32Url(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var result = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
        {
            result.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return result.ToString();
    }
}
