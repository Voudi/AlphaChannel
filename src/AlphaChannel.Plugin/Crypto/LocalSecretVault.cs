using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin.Crypto;

// Keeps account sessions and publishing keys out of Dalamud's ordinary plugin
// configuration. The vault is encrypted with AES-GCM using a random local key.
// DPAPI protects that key where available; other platforms retain the random key
// in a separate, user-restricted file so routine config sharing exposes no secrets.
internal sealed class LocalSecretVault
{
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("AlphaChannel.LocalSecretVault.v1");
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("AlphaChannel.LocalSecretKey.v1");
    private readonly string vaultPath;
    private readonly string keyPath;
    private bool available = true;

    internal LocalSecretVault(string configDirectory)
    {
        var directory = Path.Combine(configDirectory, "Secrets");
        vaultPath = Path.Combine(directory, "vault.dat");
        keyPath = Path.Combine(directory, "key.dat");
    }

    internal bool Restore(Configuration configuration)
    {
        if (!File.Exists(vaultPath))
            return true;

        try
        {
            var key = ReadKey();
            var envelope = JsonSerializer.Deserialize<VaultEnvelope>(File.ReadAllText(vaultPath))
                ?? throw new InvalidDataException("Secret vault is empty.");
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            var tag = Convert.FromBase64String(envelope.Tag);
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, tag.Length))
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);

            var secrets = JsonSerializer.Deserialize<VaultContents>(plaintext)
                ?? throw new InvalidDataException("Secret vault contents are invalid.");

            // Values still present in an old plaintext configuration take precedence
            // during the one-time migration; otherwise restore the encrypted values.
            foreach (var (contentId, session) in secrets.CharacterSessions)
                configuration.CharacterSessions.TryAdd(contentId, session);
            foreach (var (accountId, streamKey) in secrets.StreamKeys)
                configuration.StreamKeys.TryAdd(accountId, streamKey);

            CryptographicOperations.ZeroMemory(plaintext);
            return true;
        }
        catch (Exception exception)
        {
            available = false;
            AepLog.Error($"[Secrets] Could not open the local secret vault: {exception.Message}");
            return false;
        }
    }

    internal void Save(Configuration configuration)
    {
        if (!available)
            return;

        try
        {
            var directory = Path.GetDirectoryName(vaultPath)!;
            Directory.CreateDirectory(directory);
            RestrictDirectory(directory);

            var key = GetOrCreateKey();
            var contents = new VaultContents(configuration.CharacterSessions, configuration.StreamKeys);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(contents);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, tag.Length))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);

            var envelope = new VaultEnvelope(
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag));
            WriteAtomically(vaultPath, JsonSerializer.Serialize(envelope));
            RestrictFile(vaultPath);
            CryptographicOperations.ZeroMemory(plaintext);
        }
        catch (Exception exception)
        {
            AepLog.Error($"[Secrets] Could not save the local secret vault: {exception.Message}");
        }
    }

    private byte[] GetOrCreateKey()
    {
        if (File.Exists(keyPath))
            return ReadKey();

        var rawKey = RandomNumberGenerator.GetBytes(32);
        byte[] storedKey;
        var mode = "local";
        try
        {
            storedKey = ProtectedData.Protect(rawKey, DpapiEntropy, DataProtectionScope.CurrentUser);
            var check = ProtectedData.Unprotect(storedKey, DpapiEntropy, DataProtectionScope.CurrentUser);
            if (!CryptographicOperations.FixedTimeEquals(rawKey, check))
                throw new CryptographicException("DPAPI verification failed.");
            CryptographicOperations.ZeroMemory(check);
            mode = "dpapi";
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or CryptographicException)
        {
            storedKey = rawKey.ToArray();
            AepLog.Warning("[Secrets] DPAPI is unavailable; using the restricted local-key fallback.");
        }

        WriteAtomically(keyPath, JsonSerializer.Serialize(new KeyEnvelope(mode, Convert.ToBase64String(storedKey))));
        RestrictFile(keyPath);
        CryptographicOperations.ZeroMemory(storedKey);
        return rawKey;
    }

    private byte[] ReadKey()
    {
        var envelope = JsonSerializer.Deserialize<KeyEnvelope>(File.ReadAllText(keyPath))
            ?? throw new InvalidDataException("Secret key file is invalid.");
        var stored = Convert.FromBase64String(envelope.Data);
        if (string.Equals(envelope.Mode, "dpapi", StringComparison.Ordinal))
            return ProtectedData.Unprotect(stored, DpapiEntropy, DataProtectionScope.CurrentUser);
        if (!string.Equals(envelope.Mode, "local", StringComparison.Ordinal) || stored.Length != 32)
            throw new InvalidDataException("Secret key file uses an unsupported format.");
        return stored;
    }

    private static void WriteAtomically(string path, string contents)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents, Encoding.UTF8);
        File.Move(temporary, path, true);
    }

    private static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record KeyEnvelope(string Mode, string Data);
    private sealed record VaultEnvelope(string Nonce, string Ciphertext, string Tag);
    private sealed record VaultContents(
        Dictionary<ulong, CharacterSession> CharacterSessions,
        Dictionary<string, string> StreamKeys);
}
