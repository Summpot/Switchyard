using AsmResolver.PE.DotNet.Metadata.Tables;
using AsmResolver.PE.DotNet.StrongName;
using System.Security.Cryptography;

namespace Switchyard.Weaver;

/// <summary>
/// Loads a strong-name key pair (<c>.snk</c>) once and exposes the derived
/// material the Switchyard weaver needs to opt-in re-sign routed assemblies
/// with a user-provided key: the <see cref="StrongNamePrivateKey"/> used to
/// compute the RSA signature, the public-key blob embedded into
/// <see cref="AsmResolver.DotNet.AssemblyDefinition.PublicKey"/>, and the
/// 8-byte public key token written into redirected
/// <see cref="AsmResolver.DotNet.AssemblyReference.PublicKeyOrToken"/> entries.
/// </summary>
/// <remarks>
/// <para>
/// Signing is performed entirely in-process via AsmResolver's
/// <see cref="AsmResolver.PE.DotNet.StrongName.StrongNameSigner"/> (pure
/// managed RSA + PE hashing), so Switchyard does not shell out to
/// <c>sn.exe</c> and has no SDK/PATH dependency. This is the cross-platform
/// opt-in alternative to the default behaviour of stripping the strong name
/// from routed assemblies.
/// </para>
/// <para>
/// The hash algorithm is SHA-1 (the traditional strong-name algorithm, which
/// <c>sn.exe</c> also defaults to). Enhanced strong names (SHA-256) are not
/// supported by this opt-in path; assemblies that require SHA-256 should
/// re-sign out-of-band.
/// </para>
/// </remarks>
public sealed class StrongNameKey
{
    private StrongNameKey(StrongNamePrivateKey privateKey, byte[] publicKeyBlob, byte[] publicKeyToken)
    {
        PrivateKey = privateKey;
        PublicKeyBlob = publicKeyBlob;
        PublicKeyToken = publicKeyToken;
    }

    /// <summary>
    /// Builds a <see cref="StrongNameKey"/> from an already-loaded RSA
    /// key pair. Derives the public-key blob (for
    /// <c>AssemblyDefinition.PublicKey</c>) and the 8-byte public key token
    /// (for redirected <c>AssemblyReference.PublicKeyOrToken</c>).
    /// </summary>
    public static StrongNameKey FromPrivateKey(StrongNamePrivateKey privateKey)
    {
        if (privateKey is null)
            throw new ArgumentNullException(nameof(privateKey));
        var publicKeyBlob = privateKey.CreatePublicKeyBlob(AssemblyHashAlgorithm.Sha1);
        var token = ComputeToken(publicKeyBlob);
        return new StrongNameKey(privateKey, publicKeyBlob, token);
    }

    /// <summary>
    /// The RSA private/public key pair read from the <c>.snk</c> file. Used by
    /// <see cref="AsmResolver.PE.DotNet.StrongName.StrongNameSigner.SignImage"/>
    /// to compute and write the strong-name signature into the PE.
    /// </summary>
    public StrongNamePrivateKey PrivateKey { get; }

    /// <summary>
    /// The full public-key blob (in the
    /// <c>SignatureAlgorithm | HashAlgorithm | TotalSize | BLOBHEADER | RSAPUBKEY | Modulus</c>
    /// layout the CLR stores in <c>AssemblyName.PublicKey</c>) that is written
    /// into each routed assembly's <c>AssemblyDefinition.PublicKey</c> so the
    /// CLR treats the renamed assembly as a strongly-named one and the
    /// strong-name data directory is allocated at the right size
    /// (<c>Modulus.Length</c>) on write.
    /// </summary>
    public byte[] PublicKeyBlob { get; }

    /// <summary>
    /// The 8-byte public key token (SHA-1 of <see cref="PublicKeyBlob"/>, last
    /// 8 bytes reversed — the same algorithm the CLR uses). Written into each
    /// redirected caller's <c>AssemblyReference.PublicKeyOrToken</c> so the CLR
    /// binds the reference against the routed assembly's new strong-name
    /// identity by <c>(Name, Version, PublicKeyToken)</c>.
    /// </summary>
    public byte[] PublicKeyToken { get; }

    /// <summary>
    /// Loads a strong-name key pair from a <c>.snk</c> file and derives the
    /// public-key blob and token. Throws when the file is missing or is not a
    /// valid RSA key-pair blob (a public-key-only <c>.snk</c> cannot sign and
    /// is therefore rejected — signing requires the private key).
    /// </summary>
    public static StrongNameKey Load(string keyFilePath)
    {
        if (!File.Exists(keyFilePath))
            throw new FileNotFoundException("Switchyard strong-name key file not found: " + keyFilePath, keyFilePath);

        StrongNamePrivateKey privateKey;
        try
        {
            privateKey = StrongNamePrivateKey.FromFile(keyFilePath);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Switchyard strong-name key file '{keyFilePath}' is not a valid RSA key-pair .snk. " +
                "A full key pair (private + public) is required for signing; a public-key-only .snk cannot sign.",
                ex);
        }

        return FromPrivateKey(privateKey);
    }

    /// <summary>
    /// Computes the 8-byte public key token from a full public-key blob using
    /// SHA-1 (the CLR strong-name token algorithm: the last 8 bytes of the
    /// SHA-1 hash, reversed).
    /// </summary>
    public static byte[] ComputeToken(byte[] publicKey)
    {
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(publicKey);
        var token = new byte[8];
        for (int i = 0; i < 8; i++)
            token[i] = hash[hash.Length - 1 - i];
        return token;
    }

    /// <summary>
    /// Signs an already-written, delay-signed assembly in place by computing
    /// the strong-name hash (excluding the strong-name data directory) and
    /// writing the RSA signature into the directory. The assembly must already
    /// carry <see cref="PublicKeyBlob"/> in its
    /// <c>AssemblyDefinition.PublicKey</c> so the strong-name data directory
    /// was allocated at <c>Modulus.Length</c> size during the preceding write.
    /// </summary>
    public void Sign(string assemblyPath)
    {
        using var fs = new FileStream(assemblyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var signer = new AsmResolver.PE.DotNet.StrongName.StrongNameSigner(PrivateKey);
        signer.SignImage(fs, AssemblyHashAlgorithm.Sha1);
    }
}