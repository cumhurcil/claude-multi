using System.Security.Cryptography;

namespace ClaudeMulti.Core.Transfer;

/// <summary>Picks the way small secrets (API keys) are protected on this system.</summary>
public static class KeyProtector
{
    public static IKeyProtector Create(ClaudeEnvironment env) =>
        OperatingSystem.IsWindows()
            ? new DpapiKeyProtector()
            : new FileKeyProtector(Path.Combine(env.SwitcherDir, FileKeyProtector.KeyFileName));
}

/// <summary>
/// macOS and Linux have no per-user data protection API that works without a login keyring, so a random key
/// lives in a file only the current user can read (mode 0600, in a 0700 folder) and seals secrets with AES-GCM.
/// That keeps an API key out of plain text on disk, in backups and in copied instance folders. It does not
/// stop a program running as the same user from reading the key file; Windows DPAPI has the same limit.
/// </summary>
public sealed class FileKeyProtector(string keyPath) : IKeyProtector
{
    public const string KeyFileName = "protector.key";

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "CMK1"u8.ToArray();

    public bool SupportsChromiumKey => false;

    public byte[] Protect(byte[] data)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(LoadOrCreateKey(), TagSize))
            aes.Encrypt(nonce, data, cipher, tag, Magic);
        return [.. Magic, .. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] data)
    {
        var headerSize = Magic.Length + NonceSize + TagSize;
        if (data.Length < headerSize || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new TransferException("The protected value is not in this system's format.");

        var key = LoadKey() ?? throw new TransferException("The key file for protected values is missing.");
        var nonce = data.AsSpan(Magic.Length, NonceSize);
        var tag = data.AsSpan(Magic.Length + NonceSize, TagSize);
        var cipher = data.AsSpan(headerSize);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
        }
        catch (CryptographicException e)
        {
            throw new TransferException("The protected value was sealed by another user or computer.", e);
        }
        return plain;
    }

    private byte[]? LoadKey()
    {
        try
        {
            var key = File.ReadAllBytes(keyPath);
            return key.Length == KeySize ? key : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private byte[] LoadOrCreateKey()
    {
        if (LoadKey() is { } existing) return existing;

        var folder = Path.GetDirectoryName(keyPath)!;
        var key = RandomNumberGenerator.GetBytes(KeySize);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        try
        {
            using var file = new FileStream(keyPath, options);
            file.Write(key);
            return key;
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            // Another process created it first; read what it wrote (after it finished).
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (LoadKey() is { } other) return other;
                Thread.Sleep(25);
            }
            throw new TransferException("Could not read the key file for protected values.");
        }
    }
}
