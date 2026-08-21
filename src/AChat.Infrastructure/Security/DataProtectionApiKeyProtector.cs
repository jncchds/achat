using System.Security.Cryptography;
using System.Text;
using AChat.Core.Interfaces;
using Microsoft.AspNetCore.DataProtection;

namespace AChat.Infrastructure.Security;

/// <summary>
/// API key encryption via ASP.NET Core Data Protection (§7.3). The key ring is
/// persisted to the database (configured in the Api project) so it survives container
/// rebuilds and travels with a database backup.
/// </summary>
public sealed class DataProtectionApiKeyProtector : IApiKeyProtector
{
    private const string Purpose = "achat.api-key.v1";

    private readonly IDataProtector _protector;

    public DataProtectionApiKeyProtector(IDataProtectionProvider dataProtection)
    {
        _protector = dataProtection.CreateProtector(Purpose);
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;
        return Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(plaintext)));
    }

    /// <summary>
    /// Throws <see cref="CryptographicException"/> when the key ring is unavailable
    /// (e.g. database restored without the keys table) — callers must surface that as
    /// "re-enter your API key" rather than crash (§7.3).
    /// </summary>
    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(protectedValue)));
        }
        catch (FormatException)
        {
            throw new CryptographicException("Stored API key is corrupt or was encrypted with a different key ring.");
        }
    }
}
