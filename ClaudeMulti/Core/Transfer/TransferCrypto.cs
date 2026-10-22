using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeMulti.Core.Transfer;

/// <summary>A problem the user can act on (wrong passphrase, Claude still open, damaged bundle).</summary>
public sealed class TransferException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The container format of a transfer bundle: a passphrase-protected stream cut into 1 MiB AES-256-GCM chunks.
/// <code>
/// header  "CMXFER01" | iterations (int32 LE) | salt (16) | nonce prefix (4)
/// chunk   ciphertext (1 MiB, the last one 1 byte to 1 MiB) | tag (16)
/// </code>
/// The header and a "last chunk" flag are authenticated with every chunk, and the chunk number is part of its
/// nonce, so reordering, cutting or editing the file fails instead of yielding partial data. The decrypting side
/// is seekable, which lets a ZIP archive be read straight from the bundle without a plain-text temporary file.
/// </summary>
internal static class TransferCrypto
{
    public const int ChunkSize = 1 << 20;
    public const int TagSize = 16;
    public const int NonceSize = 12;
    public const int SaltSize = 16;
    public const int PrefixSize = 4;
    public const int KeySize = 32;
    public const int HeaderSize = 8 + 4 + SaltSize + PrefixSize;
    public const int DefaultIterations = 600_000;
    public const int MaxIterations = 20_000_000; // a damaged header must not make the reader spin for minutes

    public static readonly byte[] Magic = "CMXFER01"u8.ToArray();

    public static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    public static void Nonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, long counter)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteInt64BigEndian(nonce[PrefixSize..], counter);
    }

    public static byte[] Aad(byte[] header, bool final)
    {
        var aad = new byte[header.Length + 1];
        header.CopyTo(aad, 0);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }
}

/// <summary>Write-only side. Call <see cref="Complete"/> once everything is written; disposing alone leaves the bundle unfinished on purpose.</summary>
public sealed class EncryptingStream : Stream
{
    private readonly Stream output;
    private readonly bool leaveOpen;
    private readonly AesGcm aes;
    private readonly byte[] header = new byte[TransferCrypto.HeaderSize];
    private readonly byte[] prefix;
    private readonly byte[] buffer = new byte[TransferCrypto.ChunkSize];
    private readonly byte[] cipher = new byte[TransferCrypto.ChunkSize];
    private readonly byte[] tag = new byte[TransferCrypto.TagSize];
    private int filled;
    private long counter;
    private bool completed;

    public EncryptingStream(Stream output, string passphrase, int iterations = TransferCrypto.DefaultIterations, bool leaveOpen = false)
    {
        this.output = output;
        this.leaveOpen = leaveOpen;
        TransferCrypto.Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), iterations);
        RandomNumberGenerator.Fill(header.AsSpan(12, TransferCrypto.SaltSize + TransferCrypto.PrefixSize));
        prefix = header.AsSpan(12 + TransferCrypto.SaltSize, TransferCrypto.PrefixSize).ToArray();
        aes = new AesGcm(TransferCrypto.DeriveKey(passphrase, header.AsSpan(12, TransferCrypto.SaltSize), iterations), TransferCrypto.TagSize);
        output.Write(header);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        while (!data.IsEmpty)
        {
            // A full buffer is only written out once more data follows, so the last chunk is never empty.
            if (filled == TransferCrypto.ChunkSize) Emit(final: false);
            var n = Math.Min(data.Length, TransferCrypto.ChunkSize - filled);
            data[..n].CopyTo(buffer.AsSpan(filled));
            filled += n;
            data = data[n..];
        }
    }

    public override void Write(byte[] array, int offset, int count) => Write(array.AsSpan(offset, count));

    /// <summary>Writes the last chunk. Without it the bundle is rejected as cut short.</summary>
    public void Complete()
    {
        if (completed) return;
        Emit(final: true);
        completed = true;
        output.Flush();
    }

    private void Emit(bool final)
    {
        Span<byte> nonce = stackalloc byte[TransferCrypto.NonceSize];
        TransferCrypto.Nonce(nonce, prefix, counter++);
        aes.Encrypt(nonce, buffer.AsSpan(0, filled), cipher.AsSpan(0, filled), tag, TransferCrypto.Aad(header, final));
        output.Write(cipher, 0, filled);
        output.Write(tag);
        filled = 0;
    }

    public override void Flush() => output.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            aes.Dispose();
            if (!leaveOpen) output.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Read-only, seekable side. The constructor already checks the passphrase against the first chunk.</summary>
public sealed class DecryptingStream : Stream
{
    private const int Stride = TransferCrypto.ChunkSize + TransferCrypto.TagSize;

    private readonly Stream input;
    private readonly bool leaveOpen;
    private readonly AesGcm aes;
    private readonly byte[] header = new byte[TransferCrypto.HeaderSize];
    private readonly byte[] prefix;
    private readonly long chunkCount;
    private readonly int lastLength;
    private readonly long length;
    private readonly byte[] plain = new byte[TransferCrypto.ChunkSize];
    private readonly byte[] sealedChunk = new byte[TransferCrypto.ChunkSize];
    private readonly byte[] tag = new byte[TransferCrypto.TagSize];
    private long loaded = -1;
    private int loadedLength;
    private long position;

    public DecryptingStream(Stream input, string passphrase, bool leaveOpen = false)
    {
        this.input = input;
        this.leaveOpen = leaveOpen;
        if (!input.CanSeek || input.Length < TransferCrypto.HeaderSize + TransferCrypto.TagSize)
            throw new TransferException("This file is not a transfer bundle.");

        input.Position = 0;
        input.ReadExactly(header);
        if (!header.AsSpan(0, TransferCrypto.Magic.Length).SequenceEqual(TransferCrypto.Magic))
            throw new TransferException("This file is not a transfer bundle or was created by a newer version.");
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (iterations < 1 || iterations > TransferCrypto.MaxIterations)
            throw new TransferException("The transfer bundle's header is corrupt.");

        prefix = header.AsSpan(12 + TransferCrypto.SaltSize, TransferCrypto.PrefixSize).ToArray();
        aes = new AesGcm(TransferCrypto.DeriveKey(passphrase, header.AsSpan(12, TransferCrypto.SaltSize), iterations), TransferCrypto.TagSize);

        var body = input.Length - TransferCrypto.HeaderSize;
        chunkCount = (body + Stride - 1) / Stride;
        var lastSealed = body - (chunkCount - 1) * Stride;
        if (lastSealed < TransferCrypto.TagSize) throw new TransferException("The transfer bundle is incomplete or corrupt.");
        lastLength = (int)(lastSealed - TransferCrypto.TagSize);
        length = (chunkCount - 1) * TransferCrypto.ChunkSize + lastLength;

        Load(0);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => position;
        set => position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(Span<byte> destination)
    {
        var total = 0;
        while (!destination.IsEmpty && position < length)
        {
            var index = position / TransferCrypto.ChunkSize;
            Load(index);
            var offset = (int)(position - index * TransferCrypto.ChunkSize);
            var n = Math.Min(destination.Length, loadedLength - offset);
            plain.AsSpan(offset, n).CopyTo(destination);
            destination = destination[n..];
            position += n;
            total += n;
        }
        return total;
    }

    public override int Read(byte[] array, int offset, int count) => Read(array.AsSpan(offset, count));

    private void Load(long index)
    {
        if (index == loaded) return;
        loaded = -1;
        var isLast = index == chunkCount - 1;
        var size = isLast ? lastLength : TransferCrypto.ChunkSize;
        input.Position = TransferCrypto.HeaderSize + index * Stride;
        input.ReadExactly(sealedChunk.AsSpan(0, size));
        input.ReadExactly(tag);

        Span<byte> nonce = stackalloc byte[TransferCrypto.NonceSize];
        TransferCrypto.Nonce(nonce, prefix, index);
        try
        {
            aes.Decrypt(nonce, sealedChunk.AsSpan(0, size), tag, plain.AsSpan(0, size), TransferCrypto.Aad(header, isLast));
        }
        catch (CryptographicException e)
        {
            throw new TransferException("Wrong password or the transfer bundle is corrupt.", e);
        }
        loaded = index;
        loadedLength = size;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            _ => length + offset,
        };
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            aes.Dispose();
            if (!leaveOpen) input.Dispose();
        }
        base.Dispose(disposing);
    }
}
