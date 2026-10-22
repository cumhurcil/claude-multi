using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace ClaudeMulti.Core.Transfer;

/// <summary>Protects a small secret for the signed-in user. Abstracted so the key hand-over can be tested.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);

    /// <summary>
    /// True when Claude Desktop's own encryption key can be read and written through this protector. Only Windows
    /// keeps it in "Local State"; macOS keeps it in the Keychain and Linux in the desktop's secret service.
    /// </summary>
    bool SupportsChromiumKey => true;
}

/// <summary>Windows DPAPI, current-user scope: the form Chromium uses for its "Local State" key.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public byte[] Protect(byte[] data) => Run(data, protect: true);

    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = pin.AddrOfPinnedObject() };
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok)
                throw new TransferException("Windows data protection (DPAPI) failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            pin.Free();
        }
    }
}

/// <summary>
/// The key Claude Desktop (Electron/Chromium) encrypts its sign-in tokens and cookies with. It sits in
/// "Local State" as <c>os_crypt.encrypted_key</c>: base64 of "DPAPI" + the key protected for the Windows user.
/// A protected copy only opens for the same user on the same PC, which is why moving a sign-in to another PC
/// means handing over the raw key and protecting it again there.
/// </summary>
public static class ChromiumKey
{
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();

    public static string LocalStatePath(InstanceInfo instance) => Path.Combine(instance.DataDir, "Local State");

    /// <summary>The raw key, or null when the file or the entry does not exist.</summary>
    public static byte[]? Read(string localStatePath, IKeyProtector protector)
    {
        var (state, root) = JsonFile.Read(localStatePath);
        if (state == DocState.Missing) return null;
        if (state == DocState.Invalid) throw new TransferException("Could not read the 'Local State' file (broken).");

        var encoded = (root!["os_crypt"] as JsonObject)?["encrypted_key"]?.ToString();
        if (string.IsNullOrEmpty(encoded)) return null;

        byte[] blob;
        try { blob = Convert.FromBase64String(encoded); }
        catch (FormatException e) { throw new TransferException("Could not read the key in 'Local State'.", e); }
        if (blob.Length <= DpapiPrefix.Length || !blob.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
            throw new TransferException("The key format in 'Local State' is not supported.");
        return protector.Unprotect(blob[DpapiPrefix.Length..]);
    }

    /// <summary>Stores <paramref name="key"/> protected for the current user; every other entry of the file stays as it was.</summary>
    public static void Write(string localStatePath, byte[] key, IKeyProtector protector)
    {
        var (state, existing) = JsonFile.Read(localStatePath);
        if (state == DocState.Invalid) throw new TransferException("Could not read the 'Local State' file (broken); not changed.");

        var root = existing ?? new JsonObject();
        if (root["os_crypt"] is not JsonObject crypt)
            root["os_crypt"] = crypt = new JsonObject();
        crypt["encrypted_key"] = Convert.ToBase64String([.. DpapiPrefix, .. protector.Protect(key)]);
        JsonFile.WriteCompact(localStatePath, root);
    }
}
