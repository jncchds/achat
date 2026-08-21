namespace AChat.Core.Interfaces;

/// <summary>
/// Encrypts / decrypts provider API keys at rest (§7.3). Backed by ASP.NET Core Data
/// Protection with the key ring persisted to the database, so the key ring survives
/// container rebuilds and travels with a database backup.
/// </summary>
public interface IApiKeyProtector
{
    /// <summary>Encrypt a plaintext key. Returns null for null/empty input (no key).</summary>
    string? Protect(string? plaintext);

    /// <summary>Decrypt a stored key. Returns null for null input. Throws <see cref="CryptographicException"/> if the key ring was lost.</summary>
    string? Unprotect(string? protectedValue);

    /// <summary>
    /// A masked representation safe to send to the browser: at most the last 4 chars,
    /// e.g. "sk-…4f2a" (§7.3).
    /// </summary>
    static string? Mask(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;
        var tail = plaintext.Length >= 4 ? plaintext[^4..] : plaintext;
        return $"…{tail}";
    }
}
