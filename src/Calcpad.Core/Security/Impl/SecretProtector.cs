using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Calcpad.Core.Security.Impl;

/// <summary>
/// Supplies the 32 byte device key that <see cref="SecretProtector"/> uses on platforms without
/// DPAPI. Replaceable so a platform head can bind the key to a hardware keystore.
/// </summary>
public interface ISecretKeyProvider
{
    /// <summary>Stable identifier recorded next to every protected value.</summary>
    string Name { get; }

    /// <summary>True when the key never leaves a hardware backed keystore.</summary>
    bool IsHardwareBacked { get; }

    /// <summary>Returns the existing key, creating it on first use. Never returns null.</summary>
    byte[] GetOrCreateKey();
}

/// <summary>A protected value together with the scheme needed to read it back.</summary>
/// <param name="CipherText">Base64 payload. Never contains plaintext.</param>
/// <param name="Scheme">Identifier of the protection scheme that produced <paramref name="CipherText"/>.</param>
public sealed record ProtectedSecret(string CipherText, string Scheme);

/// <summary>
/// Encrypts secrets (AI provider API keys) before they touch the database. On Windows the secret is
/// wrapped with DPAPI in the current user scope; everywhere else an AES-256-GCM key derived from a
/// per-install device key is used. Plaintext is never written to disk: when no scheme is available
/// <see cref="Protect"/> returns null and the caller must drop the secret instead of storing it.
/// </summary>
public static class SecretProtector
{
    /// <summary>Scheme identifier for Windows DPAPI (current user scope).</summary>
    public const string DpapiScheme = "dpapi-currentuser";

    /// <summary>Scheme identifier for AES-256-GCM with a device key.</summary>
    public const string AesGcmScheme = "aesgcm-hkdf-devicekey";

    /// <summary>Scheme identifier meaning "no protection was applied". Never used for stored data.</summary>
    public const string NoScheme = "none";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const uint CryptProtectUiForbidden = 0x1;
    private const string HkdfInfo = "calcpad.secret.v1";

    private static readonly object Gate = new();
    private static ISecretKeyProvider? _keyProvider;
    private static string? _keyDirectory;

    /// <summary>Scheme that will be used on the current platform.</summary>
    public static string ActiveScheme => OperatingSystem.IsWindows() ? DpapiScheme : AesGcmScheme;

    /// <summary>
    /// True when secrets can be protected on this platform. DPAPI is used on Windows and
    /// AES-256-GCM with a device key everywhere else, so protection is always available.
    /// </summary>
    public static bool IsPlatformProtectionAvailable => true;

    /// <summary>
    /// Sets the directory that holds the device key file used on non-Windows platforms.
    /// Called by the composition root once the data directory is known.
    /// </summary>
    /// <param name="keyDirectory">Directory for <c>device.key</c>; null restores the default location.</param>
    public static void Configure(string? keyDirectory)
    {
        lock (Gate)
        {
            _keyDirectory = keyDirectory;
        }
    }

    /// <summary>Installs a platform specific key provider, e.g. an Android keystore backed one.</summary>
    /// <param name="provider">Provider to use, or null to fall back to the built-in file key.</param>
    public static void ConfigureKeyProvider(ISecretKeyProvider? provider)
    {
        lock (Gate)
        {
            _keyProvider = provider;
        }
    }

    /// <summary>Name of the key provider in use, for settings screens and diagnostics.</summary>
    public static string KeyProviderName
    {
        get
        {
            lock (Gate)
            {
                if (_keyProvider is { } custom)
                {
                    return custom.Name;
                }
            }

            return OperatingSystem.IsWindows() ? DpapiScheme : "file-devicekey";
        }
    }

    /// <summary>
    /// Protects <paramref name="plaintext"/>.
    /// </summary>
    /// <returns>
    /// The protected value, or null when nothing could be protected (caller must not persist the
    /// plaintext in that case).
    /// </returns>
    public static ProtectedSecret? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var wrapped = DpapiProtect(bytes);
                if (wrapped is not null)
                {
                    return new ProtectedSecret(Convert.ToBase64String(wrapped), DpapiScheme);
                }
            }

            var (key, scheme) = ResolveDeviceKey();
            return new ProtectedSecret(Convert.ToBase64String(AesGcmProtect(key, bytes)), scheme);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Reads a value produced by <see cref="Protect"/>.</summary>
    /// <param name="cipherText">Base64 payload as stored.</param>
    /// <param name="scheme">Scheme recorded next to the payload.</param>
    /// <param name="plaintext">Decrypted value on success.</param>
    /// <param name="error">Human readable reason on failure, meant for the settings UI.</param>
    /// <returns>True when the value could be read back.</returns>
    public static bool TryUnprotect(string? cipherText, string? scheme, out string? plaintext, out string? error)
    {
        plaintext = null;
        error = null;

        if (string.IsNullOrEmpty(cipherText))
        {
            return true;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(cipherText);
        }
        catch (FormatException)
        {
            error = "Stored secret is not valid base64.";
            return false;
        }

        try
        {
            switch (scheme)
            {
                case DpapiScheme:
                    if (!OperatingSystem.IsWindows())
                    {
                        error = "This secret was protected with Windows DPAPI and cannot be read on this platform.";
                        return false;
                    }

                    var unwrapped = DpapiUnprotect(payload);
                    if (unwrapped is null)
                    {
                        error = "Windows DPAPI refused to decrypt the stored secret.";
                        return false;
                    }

                    plaintext = Encoding.UTF8.GetString(unwrapped);
                    CryptographicOperations.ZeroMemory(unwrapped);
                    return true;

                case AesGcmScheme:
                case null:
                case "":
                case NoScheme:
                    var (key, _) = ResolveDeviceKey();
                    var clear = AesGcmUnprotect(key, payload);
                    plaintext = Encoding.UTF8.GetString(clear);
                    CryptographicOperations.ZeroMemory(clear);
                    return true;

                default:
                    error = $"Unknown secret protection scheme '{scheme}'.";
                    return false;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or FormatException)
        {
            error = $"Stored secret could not be decrypted ({ex.GetType().Name}).";
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static (byte[] Key, string Scheme) ResolveDeviceKey()
    {
        ISecretKeyProvider provider;
        string keyDirectory;
        lock (Gate)
        {
            provider = _keyProvider ?? new FileDeviceKeyProvider(_keyDirectory ?? DefaultKeyDirectory());
            keyDirectory = _keyDirectory ?? DefaultKeyDirectory();
        }

        var providerKey = provider.GetOrCreateKey();
        if (providerKey.Length != KeyBytes)
        {
            throw new CryptographicException("Secret key provider returned an unexpected key length.");
        }

        // Bind the random device key to this machine and user so that copying the database alone
        // is not enough to read the secrets.
        var identity = Encoding.UTF8.GetBytes(
            $"{Environment.MachineName}\n{Environment.UserName}\n{keyDirectory}");
        var derived = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            providerKey,
            KeyBytes,
            salt: Encoding.UTF8.GetBytes(HkdfInfo),
            info: identity);

        CryptographicOperations.ZeroMemory(identity);
        return (derived, AesGcmScheme);
    }

    private static string DefaultKeyDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("CALCPAD_KEY_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            appData = Path.GetTempPath();
        }

        return Path.Combine(appData, "Calcpad");
    }

    private static byte[] AesGcmProtect(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag);
        }

        var result = new byte[NonceBytes + TagBytes + cipher.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceBytes);
        Buffer.BlockCopy(tag, 0, result, NonceBytes, TagBytes);
        Buffer.BlockCopy(cipher, 0, result, NonceBytes + TagBytes, cipher.Length);
        return result;
    }

    private static byte[] AesGcmUnprotect(byte[] key, byte[] payload)
    {
        if (payload.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Protected payload is truncated.");
        }

        var nonce = payload.AsSpan(0, NonceBytes);
        var tag = payload.AsSpan(NonceBytes, TagBytes);
        var cipher = payload.AsSpan(NonceBytes + TagBytes);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        return plain;
    }

    private static byte[]? DpapiProtect(byte[] data)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = handle.AddrOfPinnedObject();
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output))
            {
                return null;
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }

            handle.Free();
        }
    }

    private static byte[]? DpapiUnprotect(byte[] data)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = handle.AddrOfPinnedObject();
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output))
            {
                return null;
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }

            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// Default provider: a random 32 byte key kept in an app private file. Used on Android, Linux
    /// and macOS; Android swaps in a keystore backed provider when one is available.
    /// </summary>
    private sealed class FileDeviceKeyProvider : ISecretKeyProvider
    {
        private readonly string _path;
        private byte[]? _cached;

        public FileDeviceKeyProvider(string directory)
        {
            _path = Path.Combine(directory, "keys", "device.key");
        }

        public string Name => "file-devicekey";

        public bool IsHardwareBacked => false;

        public byte[] GetOrCreateKey()
        {
            lock (this)
            {
                if (_cached is not null)
                {
                    return _cached;
                }

                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(_path))
                {
                    var existing = File.ReadAllBytes(_path);
                    if (existing.Length == KeyBytes)
                    {
                        return _cached = existing;
                    }
                }

                var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
                var temporary = _path + ".tmp";
                File.WriteAllBytes(temporary, fresh);
                TryRestrictPermissions(temporary);
                File.Move(temporary, _path, overwrite: true);
                TryRestrictPermissions(_path);
                return _cached = fresh;
            }
        }

        private static void TryRestrictPermissions(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Best effort: the file still lives inside the app private data directory.
            }
        }
    }
}
