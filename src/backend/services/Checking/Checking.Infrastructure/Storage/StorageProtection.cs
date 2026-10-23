using System.Security.Cryptography;
using System.Text;

namespace Matigr.Checking.Infrastructure.Storage;

public sealed record IssuedCapability(string Value, string Hash, string Ciphertext)
{
    public override string ToString() => nameof(IssuedCapability);
}

// Persistent externally managed 256-bit key; never generate/rotate silently during host startup.
// AEAD binds ciphertext to its purpose and owner, preventing swaps between records.
public sealed class StorageProtection
{
    private readonly byte[] key;

    public StorageProtection(string keyPath)
    {
        var encoded = File.ReadAllText(keyPath).Trim();
        if (encoded.Length != 64 || !encoded.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Storage protection requires a 256-bit hexadecimal key.");
        key = Convert.FromHexString(encoded);
    }

    public IssuedCapability IssueCapability(Guid assignmentId, Guid reviewerId)
    {
        var value = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return new(value, Hash(value), Protect(value, CapabilityContext(assignmentId, reviewerId)));
    }

    public string RestoreCapability(Guid assignmentId, Guid reviewerId, string hash, string ciphertext)
    {
        var value = Unprotect(ciphertext, CapabilityContext(assignmentId, reviewerId));
        if (!MatchesHash(value, hash)) throw new CryptographicException("Capability integrity check failed.");
        return value;
    }

    public string ProtectHttpResponse(Guid subjectId, string operation, string idempotencyKey, string response) =>
        Protect(response, HttpContext(subjectId, operation, idempotencyKey));

    public string RestoreHttpResponse(Guid subjectId, string operation, string idempotencyKey, string ciphertext) =>
        Unprotect(ciphertext, HttpContext(subjectId, operation, idempotencyKey));

    public static bool MatchesHash(string value, string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(value)), Convert.FromHexString(hash));
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string CapabilityContext(Guid assignmentId, Guid reviewerId) =>
        $"matigr.checking.capability.v1/{assignmentId:D}/{reviewerId:D}";
    private static string HttpContext(Guid subjectId, string operation, string idempotencyKey) =>
        // Length prefixes make the operation/key boundary unambiguous.
        $"matigr.checking.http.v1/{subjectId:D}/{operation.Length}:{operation}/{idempotencyKey.Length}:{idempotencyKey}";

    private string Protect(string plaintext, string context)
    {
        var clear = Encoding.UTF8.GetBytes(plaintext);
        var bytes = new byte[1 + 12 + 16 + clear.Length];
        bytes[0] = 1;
        RandomNumberGenerator.Fill(bytes.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(bytes.AsSpan(1, 12), clear, bytes.AsSpan(29), bytes.AsSpan(13, 16), Encoding.UTF8.GetBytes(context));
        return Convert.ToBase64String(bytes);
    }

    private string Unprotect(string ciphertext, string context)
    {
        var bytes = Convert.FromBase64String(ciphertext);
        if (bytes.Length < 29 || bytes[0] != 1) throw new CryptographicException("Unsupported protected value.");
        var clear = new byte[bytes.Length - 29];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(bytes.AsSpan(1, 12), bytes.AsSpan(29), bytes.AsSpan(13, 16), clear, Encoding.UTF8.GetBytes(context));
        return Encoding.UTF8.GetString(clear);
    }
}
