using System.Security.Cryptography;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AsmResolver.PE.DotNet.StrongName;
using Switchyard.Weaver;
using Xunit;

namespace Switchyard.Core.Tests;

/// <summary>
/// Level 1 unit tests for the opt-in strong-name re-signing path
/// (<c>SwitchyardStrongNameKeyFile</c>). Verifies that when a
/// <see cref="StrongNameKey"/> is supplied:
/// <list type="bullet">
/// <item><see cref="AssemblyWeaver.PrepareAndRename"/> replaces the original
/// strong-name identity with the user-provided public key, allocates the
/// strong-name data directory, and produces a fully-signed PE (AsmResolver's
/// in-process <c>StrongNameSigner</c>, no <c>sn.exe</c>).</item>
/// <item><see cref="ReferenceRedirector.RedirectReferences"/> stamps the
/// redirected <c>AssemblyReference.PublicKeyOrToken</c> with the new key's
/// token so the CLR binds the routed assembly by
/// <c>(Name, Version, PublicKeyToken)</c>.</item>
/// </list>
/// When no key is supplied, the original strong-name-stripping behaviour is
/// preserved exactly.
/// </summary>
public class StrongNameResignTests : IDisposable
{
    private readonly string _tempDir;

    public StrongNameResignTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "switchyard-sn-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    /// <summary>
    /// Generates a fresh RSA key pair and writes a valid CryptoAPI
    /// PRIVATEKEYBLOB <c>.snk</c> file (the layout <c>sn -k</c> produces and
    /// <see cref="StrongNamePrivateKey.FromFile"/> consumes): BLOBHEADER +
    /// RSAPUBKEY + little-endian Modulus/P/Q/DP/DQ/InverseQ/D. .NET's
    /// <see cref="RSAParameters"/> are big-endian, so every parameter is
    /// reversed before being written. Uses a 1024-bit key to keep the test
    /// fast; production keys are normally 2048+ bits.
    /// </summary>
    private string WriteTestSnk()
    {
        using var rsa = RSA.Create(1024);
        var parameters = rsa.ExportParameters(includePrivateParameters: true);

        string snkPath = Path.Combine(_tempDir, "test.snk");
        using var fs = new FileStream(snkPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        // BLOBHEADER (8 bytes)
        bw.Write((byte)0x07);          // bType = PRIVATEKEYBLOB
        bw.Write((byte)0x02);          // bVersion = 2
        bw.Write((ushort)0);           // reserved
        bw.Write((uint)0x00002400);    // aiKeyAlg = CALG_RSA_SIGN

        // RSAPUBKEY (12 bytes)
        int bitLen = parameters.Modulus!.Length * 8;
        bw.Write((uint)0x32415352);    // magic = RSA2 ("RSA2" little-endian)
        bw.Write((uint)bitLen);
        // Public exponent as a little-endian uint32 (RSAParameters.Exponent
        // is big-endian variable-length; pad/convert).
        uint pubExp = ToLittleEndianUint32(parameters.Exponent!);
        bw.Write(pubExp);

        // Private key parameters, little-endian (reverse the big-endian .NET arrays).
        int len8 = bitLen / 8;
        int len16 = bitLen / 16;
        bw.Write(Reverse(parameters.Modulus!, len8));        // Modulus
        bw.Write(Reverse(parameters.P!, len16));            // P
        bw.Write(Reverse(parameters.Q!, len16));            // Q
        bw.Write(Reverse(parameters.DP!, len16));           // DP
        bw.Write(Reverse(parameters.DQ!, len16));           // DQ
        bw.Write(Reverse(parameters.InverseQ!, len16));     // InverseQ
        bw.Write(Reverse(parameters.D!, len8));             // Private exponent

        return snkPath;
    }

    private static byte[] Reverse(byte[] data, int length)
    {
        var result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = data[data.Length - 1 - (i + (data.Length - length))];
        return result;
    }

    private static uint ToLittleEndianUint32(byte[] bigEndian)
    {
        uint value = 0;
        for (int i = 0; i < bigEndian.Length; i++)
            value |= (uint)bigEndian[bigEndian.Length - 1 - i] << (8 * i);
        return value;
    }

    /// <summary>
    /// Builds a <see cref="StrongNameKey"/> directly from a freshly generated
    /// RSA key pair (no file round-trip), for tests that only need the key
    /// material and not the <see cref="StrongNameKey.Load"/> path.
    /// </summary>
    private static StrongNameKey MakeKeyFromRsa()
    {
        using var rsa = RSA.Create(1024);
        var parameters = rsa.ExportParameters(includePrivateParameters: true);
        return StrongNameKey.FromPrivateKey(new StrongNamePrivateKey(parameters));
    }

    [Fact]
    public void Load_ReadsSnk_AndDerivesPublicKeyBlobAndToken()
    {
        string snkPath = WriteTestSnk();
        var key = StrongNameKey.Load(snkPath);

        Assert.NotNull(key.PrivateKey);
        Assert.NotEmpty(key.PublicKeyBlob);
        // A 1024-bit RSA public-key blob is 32 (header) + 128 (modulus) = 160 bytes.
        Assert.Equal(160, key.PublicKeyBlob.Length);
        // The token is always 8 bytes.
        Assert.Equal(8, key.PublicKeyToken.Length);

        // The loaded key must be usable for actual signing (verifies the
        // .snk byte order round-trips correctly into RSA.ImportParameters).
        string source = TestAssemblyFactory.CreateStrongNamedTargetLib(
            Path.Combine(_tempDir, "TargetLib.dll"));
        string outputPath = Path.Combine(_tempDir, "TargetLib.Switchyard.1.0.0.dll");
        AssemblyWeaver.PrepareAndRename(source, "TargetLib.Switchyard.1.0.0", outputPath, key);
        var module = ModuleDefinition.FromFile(outputPath);
        Assert.True(module.IsStrongNameSigned);
    }

    [Fact]
    public void Load_OnMissingFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() =>
            StrongNameKey.Load(Path.Combine(_tempDir, "does-not-exist.snk")));
    }

    [Fact]
    public void PrepareAndRename_WithSignKey_SetsPublicKeyAndSignsAssembly()
    {
        var key = MakeKeyFromRsa();

        string source = TestAssemblyFactory.CreateStrongNamedTargetLib(
            Path.Combine(_tempDir, "TargetLib.dll"));
        string outputPath = Path.Combine(_tempDir, "TargetLib.Switchyard.1.0.0.dll");

        AssemblyWeaver.PrepareAndRename(source, "TargetLib.Switchyard.1.0.0", outputPath, key);

        var module = ModuleDefinition.FromFile(outputPath);
        Assert.NotNull(module.Assembly);
        Assert.Equal("TargetLib.Switchyard.1.0.0", module.Assembly.Name);
        // The user-provided public key replaces the original dummy key.
        Assert.Equal(key.PublicKeyBlob, module.Assembly.PublicKey);
        Assert.True(module.Assembly.HasPublicKey);
        Assert.Equal(AssemblyHashAlgorithm.Sha1, module.Assembly.HashAlgorithm);
        // The module reports as strong-name signed (signature directory is
        // present and non-empty — filled by StrongNameSigner).
        Assert.True(module.IsStrongNameSigned);
    }

    [Fact]
    public void PrepareAndRename_WithSignKey_ProducesAssemblyWithValidStrongNameSignature()
    {
        // End-to-end signing: the rewritten DLL must carry a strong-name
        // signature that the CLR's StrongNameSignatureVerificationEx would
        // accept. We verify by reading the strong-name data directory and
        // confirming it is non-empty (a delay-signed-only assembly would have
        // an all-zero signature).
        var key = MakeKeyFromRsa();

        string source = TestAssemblyFactory.CreateStrongNamedTargetLib(
            Path.Combine(_tempDir, "TargetLib.dll"));
        string outputPath = Path.Combine(_tempDir, "TargetLib.Switchyard.1.0.0.dll");

        AssemblyWeaver.PrepareAndRename(source, "TargetLib.Switchyard.1.0.0", outputPath, key);

        var module = ModuleDefinition.FromFile(outputPath);
        var strongName = module.DotNetDirectory?.StrongName;
        Assert.NotNull(strongName);
        byte[] data = ((IReadableSegment)strongName).ToArray();
        // A 1024-bit RSA signature is 128 bytes; it must not be all zeros
        // (that would indicate a delay-signed slot that was never filled).
        Assert.Equal(128, data.Length);
        Assert.True(data.Any(b => b != 0), "Strong-name signature must be non-zero (actually signed, not delay-signed).");
    }

    [Fact]
    public void PrepareAndRename_WithoutSignKey_StripsStrongNameAsBefore()
    {
        // Regression guard: the opt-in path must not change the default
        // behaviour when no key is supplied.
        string source = TestAssemblyFactory.CreateStrongNamedTargetLib(
            Path.Combine(_tempDir, "TargetLib.dll"));
        string outputPath = Path.Combine(_tempDir, "TargetLib.Switchyard.1.0.0.dll");

        AssemblyWeaver.PrepareAndRename(source, "TargetLib.Switchyard.1.0.0", outputPath);

        var module = ModuleDefinition.FromFile(outputPath);
        Assert.NotNull(module.Assembly);
        Assert.Null(module.Assembly.PublicKey);
        Assert.False(module.Assembly.HasPublicKey);
        Assert.Equal(AssemblyHashAlgorithm.None, module.Assembly.HashAlgorithm);
    }

    [Fact]
    public void RedirectReferences_WithSignKeyToken_StampsTokenOnRedirectedReference()
    {
        var key = MakeKeyFromRsa();

        string caller = TestAssemblyFactory.CreateCallerReferencingTargetLib(
            Path.Combine(_tempDir, "MainApp.dll"));
        var redirections = new Dictionary<string, string>
        {
            ["TargetLib"] = "TargetLib.Switchyard.1.0.0"
        };
        string outputPath = Path.Combine(_tempDir, "MainApp.Redirected.dll");

        ReferenceRedirector.RedirectReferences(caller, redirections, outputPath, routedPublicKeyToken: key.PublicKeyToken);

        var module = ModuleDefinition.FromFile(outputPath);
        var targetRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "TargetLib.Switchyard.1.0.0");
        Assert.NotNull(targetRef);
        Assert.Equal(key.PublicKeyToken, targetRef.PublicKeyOrToken);
        Assert.False(targetRef.HasPublicKey);
        Assert.DoesNotContain(module.AssemblyReferences, r => r.Name == "TargetLib");
    }

    [Fact]
    public void RedirectReferences_WithoutSignKeyToken_ClearsTokenAsBefore()
    {
        // Regression guard: when no token is supplied, the redirected
        // reference's PublicKeyOrToken is cleared (default behaviour).
        string caller = TestAssemblyFactory.CreateCallerReferencingTargetLib(
            Path.Combine(_tempDir, "MainApp.dll"));
        var redirections = new Dictionary<string, string>
        {
            ["TargetLib"] = "TargetLib.Switchyard.1.0.0"
        };
        string outputPath = Path.Combine(_tempDir, "MainApp.Redirected.dll");

        ReferenceRedirector.RedirectReferences(caller, redirections, outputPath);

        var module = ModuleDefinition.FromFile(outputPath);
        var targetRef = module.AssemblyReferences.FirstOrDefault(r => r.Name == "TargetLib.Switchyard.1.0.0");
        Assert.NotNull(targetRef);
        Assert.Null(targetRef.PublicKeyOrToken);
    }

    [Fact]
    public void ComputeToken_MatchesClrAlgorithm()
    {
        // The token is the last 8 bytes of the SHA-1 hash of the public-key
        // blob, reversed. Re-derive it independently and confirm the helper
        // matches.
        var key = MakeKeyFromRsa();

        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(key.PublicKeyBlob);
        var expected = new byte[8];
        for (int i = 0; i < 8; i++)
            expected[i] = hash[hash.Length - 1 - i];

        Assert.Equal(expected, key.PublicKeyToken);
    }
}