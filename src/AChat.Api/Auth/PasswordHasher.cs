using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace AChat.Api.Auth;

/// <summary>
/// Password hashing — Argon2id (recommended in §7.2), stored as a self-describing
/// PHC string so verification never depends on process configuration. Parameters:
/// 64 MiB memory, 2 iterations, parallelism 1.
/// </summary>
public sealed class PasswordHasher
{
    private const int MemoryKiB = 65_536;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int HashLength = 32;
    private const int SaltLength = 16;

    public string Hash(string password)
    {
        if (password is null) throw new ArgumentNullException(nameof(password));
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            Iterations = Iterations,
            MemorySize = MemoryKiB,
            DegreeOfParallelism = Parallelism,
        };
        var hash = argon.GetBytes(HashLength);
        return $"$argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}" +
               $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        if (password is null) throw new ArgumentNullException(nameof(password));
        if (string.IsNullOrWhiteSpace(encodedHash)) return false;

        // $argon2id$v=19$m=<mem>,t=<iter>,p=<par>$<salt>$<hash>
        var parts = encodedHash.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id") return false;

        int memory = 0, iter = 0, par = 1;
        foreach (var kv in parts[3].Split(','))
        {
            var idx = kv.IndexOf('=');
            if (idx <= 0) return false;
            var key = kv[..idx];
            var val = kv[(idx + 1)..];
            if (!int.TryParse(val, out var num)) return false;
            if (key == "m") memory = num;
            else if (key == "t") iter = num;
            else if (key == "p") par = num;
        }
        if (memory <= 0 || iter <= 0 || par <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[4]);
            expected = Convert.FromBase64String(parts[5]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            Iterations = iter,
            MemorySize = memory,
            DegreeOfParallelism = par,
        };
        var actual = argon.GetBytes(expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
