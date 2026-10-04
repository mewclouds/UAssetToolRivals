using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UAssetTool;
using UAssetTool.IoStore;

var tests = new (string Name, Action Run, bool WindowsOnly)[]
{
    ("clear index", () => PreserveBytes(false, false), false),
    ("encrypted index", () => PreserveBytes(true, false), false),
    ("clear UTF-16 index", () => PreserveBytes(false, true), false),
    ("encrypted UTF-16 index", () => PreserveBytes(true, true), false),
    ("unsafe layouts", RejectUnsafeLayouts, false),
    ("truncated UCAS", RejectTruncatedUcas, false),
    ("wrong index key", RejectWrongKey, false),
    ("invalid key length", RejectInvalidKey, false),
    ("empty containers in supported versions", DecryptEmptyContainers, false),
    ("JSON success and repeat", JsonSuccess, false),
    ("JSON failure", JsonFailure, false),
    ("CLI success and invalid arguments", CliSuccess, false),
    ("TOC replacement rollback", RestoreAfterReplacementFailure, true),
    ("locked UCAS", RejectLockedUcas, true),
};
int passed = 0, failed = 0, skipped = 0;
foreach (var test in tests)
{
    if (test.WindowsOnly && !OperatingSystem.IsWindows())
    {
        skipped++;
        Console.WriteLine($"SKIP {test.Name}: requires Windows file sharing.");
        continue;
    }
    try
    {
        test.Run();
        passed++;
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {error}");
    }
}
Console.WriteLine($"Passed: {passed}; Failed: {failed}; Skipped: {skipped}");
return failed == 0 ? 0 : 1;

static void PreserveBytes(bool encryptedDirectory, bool wideNames)
{
    // Arrange
    using var fixture = new Fixture(encryptedDirectory, wideNames);

    // Act
    bool changed = IoStoreDecryptor.Decrypt(fixture.Utoc, fixture.KeyHex);

    // Assert
    Require(changed, "The encrypted input was not decrypted.");
    Equal(File.ReadAllBytes(fixture.Utoc), fixture.PlainToc, "TOC metadata or IDs changed.");
    Equal(File.ReadAllBytes(fixture.Ucas), fixture.PlainUcas, "Compression bytes or padding changed.");
    Require(!Directory.EnumerateDirectories(fixture.Root).Any(), "A temporary directory remained.");
}

static void RejectUnsafeLayouts()
{
    var changes = new Action<byte[]>[]
    {
        toc => toc[0] = 0,
        toc => toc[16] = 6,
        toc => BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(20), 1),
        toc => toc[80] |= 4,
        toc => BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(52), 2),
        toc => BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(28), uint.MaxValue),
        toc => BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(48), uint.MaxValue),
        toc => toc[Fixture.BlockOffset + 12] = 0,
        toc => toc[Fixture.BlockOffset + 11] = 2,
        toc => toc[Fixture.DirectoryOffset] = 0xFF,
    };
    foreach (var change in changes)
    {
        // Arrange
        using var fixture = new Fixture(false, false);
        byte[] toc = File.ReadAllBytes(fixture.Utoc);
        change(toc);
        File.WriteAllBytes(fixture.Utoc, toc);

        // Act and Assert
        RejectWithoutChanges(fixture, () => IoStoreDecryptor.Decrypt(fixture.Utoc, fixture.KeyHex));
    }
}

static void RejectTruncatedUcas()
{
    // Arrange
    using var fixture = new Fixture(false, false);
    File.WriteAllBytes(fixture.Ucas, new byte[1]);

    // Act and Assert
    RejectWithoutChanges(fixture, () => IoStoreDecryptor.Decrypt(fixture.Utoc, fixture.KeyHex));
}

static void RejectWrongKey()
{
    // Arrange
    using var fixture = new Fixture(true, false);

    // Act and Assert
    RejectWithoutChanges(fixture, () => IoStoreDecryptor.Decrypt(fixture.Utoc, Convert.ToHexString(new byte[32])));
}

static void RejectInvalidKey()
{
    // Arrange
    using var fixture = new Fixture(false, false);

    // Act and Assert
    RejectWithoutChanges(fixture, () => IoStoreDecryptor.Decrypt(fixture.Utoc, "AB"));
}

static void JsonSuccess()
{
    // Arrange
    using var fixture = new Fixture(true, false);
    var request = new UAssetRequest { Action = "decrypt_iostore", FilePath = fixture.Utoc, AesKey = fixture.KeyHex };

    // Act
    var response = UAssetTool.Program.ProcessRequest(request);
    var repeat = UAssetTool.Program.ProcessRequest(request);

    // Assert
    Require(response.Success && repeat.Success, "The JSON action failed.");
    var result = JsonSerializer.SerializeToElement(response.Data);
    Require(!result.GetProperty("encrypted").GetBoolean() && result.GetProperty("changed").GetBoolean(), "The JSON state is incorrect.");
    Require(!JsonSerializer.SerializeToElement(repeat.Data).GetProperty("changed").GetBoolean(), "A clear input changed again.");
    Equal(File.ReadAllBytes(fixture.Utoc), fixture.PlainToc, "JSON changed the clear TOC.");
    Equal(File.ReadAllBytes(fixture.Ucas), fixture.PlainUcas, "JSON changed the clear UCAS.");
}

static void DecryptEmptyContainers()
{
    foreach (byte version in new byte[] { 1, 2, 3, 4, 5 })
    {
        // Arrange
        using var fixture = new Fixture(false, false);
        byte[] toc = new byte[144];
        FIoStoreTocHeader.TOC_MAGIC.CopyTo(toc, 0);
        toc[16] = version;
        BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(20), 144);
        BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(32), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(52), 1);
        toc[80] = 2;
        File.WriteAllBytes(fixture.Utoc, toc);
        File.WriteAllBytes(fixture.Ucas, Array.Empty<byte>());

        // Act
        bool changed = IoStoreDecryptor.Decrypt(fixture.Utoc, fixture.KeyHex);

        // Assert
        toc[80] = 0;
        Require(changed, "An empty encrypted container was not decrypted.");
        Equal(File.ReadAllBytes(fixture.Utoc), toc, "The empty TOC changed beyond its flag.");
        Require(new FileInfo(fixture.Ucas).Length == 0, "The empty UCAS changed.");
    }
}

static void JsonFailure()
{
    // Arrange
    using var fixture = new Fixture(false, false);
    byte[] toc = File.ReadAllBytes(fixture.Utoc);
    byte[] ucas = File.ReadAllBytes(fixture.Ucas);
    File.WriteAllBytes(fixture.Ucas, ucas[..1]);

    // Act
    var response = UAssetTool.Program.ProcessRequest(new UAssetRequest { Action = "decrypt_iostore", FilePath = fixture.Utoc, AesKey = fixture.KeyHex });
    var missing = UAssetTool.Program.ProcessRequest(new UAssetRequest { Action = "decrypt_iostore" });

    // Assert
    Require(!response.Success && !missing.Success, "The JSON action accepted an invalid input.");
    Equal(File.ReadAllBytes(fixture.Utoc), toc, "JSON failure changed the TOC.");
    Equal(File.ReadAllBytes(fixture.Ucas), ucas[..1], "JSON failure changed the UCAS.");
}

static void CliSuccess()
{
    // Arrange
    using var fixture = new Fixture(true, false);

    // Act
    int result = UAssetTool.Program.Main(new[] { "decrypt_iostore", fixture.Utoc, "--aes-key", fixture.KeyHex }).GetAwaiter().GetResult();
    int invalid = UAssetTool.Program.Main(new[] { "decrypt_iostore" }).GetAwaiter().GetResult();

    // Assert
    Require(result == 0 && invalid != 0, "The CLI exit code is incorrect.");
    Equal(File.ReadAllBytes(fixture.Utoc), fixture.PlainToc, "CLI changed the TOC.");
    Equal(File.ReadAllBytes(fixture.Ucas), fixture.PlainUcas, "CLI changed the UCAS.");
}

static void RestoreAfterReplacementFailure()
{
    // Arrange
    using var fixture = new Fixture(false, false);
    using var lockedToc = new FileStream(fixture.Utoc, FileMode.Open, FileAccess.Read, FileShare.Read);

    // Act and Assert
    RejectWithoutChanges(fixture, () => IoStoreDecryptor.Decrypt(fixture.Utoc, fixture.KeyHex));
    Require(!Directory.EnumerateDirectories(fixture.Root).Any(), "The completed rollback left a temporary directory.");
}

static void RejectLockedUcas()
{
    // Arrange
    using var fixture = new Fixture(false, false);
    using var lockedUcas = new FileStream(fixture.Ucas, FileMode.Open, FileAccess.Read, FileShare.Read);

    // Act and Assert
    RejectWithoutChanges(fixture, () => IoStoreDecryptor.Decrypt(fixture.Utoc, fixture.KeyHex));
    Require(!Directory.EnumerateDirectories(fixture.Root).Any(), "A locked UCAS left a temporary directory.");
}

static void RejectWithoutChanges(Fixture fixture, Action action)
{
    byte[] toc = File.ReadAllBytes(fixture.Utoc);
    byte[] ucas = File.ReadAllBytes(fixture.Ucas);
    bool rejected = false;
    try { action(); }
    catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or CryptographicException) { rejected = true; }
    Require(rejected, "An invalid operation was accepted.");
    Equal(File.ReadAllBytes(fixture.Utoc), toc, "A failed operation changed the TOC.");
    Equal(File.ReadAllBytes(fixture.Ucas), ucas, "A failed operation changed the UCAS.");
}

static void Equal(byte[] actual, byte[] expected, string message) => Require(actual.AsSpan().SequenceEqual(expected), message);
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class Fixture : IDisposable
{
    // The block table follows two chunk IDs, two offset entries, one seed, and one overflow index.
    public const int BlockOffset = 144 + 2 * (12 + 10) + 4 + 4;
    public const int DirectoryOffset = BlockOffset + 2 * 12 + 32;
    public string Root { get; } = Directory.CreateTempSubdirectory("uat-decrypt-test-").FullName;
    public string Utoc => Path.Combine(Root, "fixture.utoc");
    public string Ucas => Path.Combine(Root, "fixture.ucas");
    public string KeyHex { get; }
    public byte[] PlainToc { get; }
    public byte[] PlainUcas { get; }

    public Fixture(bool encryptedDirectory, bool wideNames)
    {
        byte[] key = Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();
        KeyHex = Convert.ToHexString(key);
        using var compressed = new MemoryStream();
        byte[] cooked = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("cooked texture bytes", 10)));
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) { zlib.Write(cooked); }
        byte[] packed = compressed.ToArray();
        using var directory = new MemoryStream();
        using (var writer = new BinaryWriter(directory, Encoding.UTF8, leaveOpen: true))
        {
            WriteString(writer, "../../../", wideNames);
            writer.Write(1u);
            foreach (uint value in new[] { uint.MaxValue, uint.MaxValue, uint.MaxValue, 0u }) writer.Write(value);
            writer.Write(1u);
            foreach (uint value in new[] { 0u, uint.MaxValue, 0u }) writer.Write(value);
            writer.Write(1u);
            WriteString(writer, wideNames ? "猫.uasset" : "asset.uasset", wideNames);
        }
        byte[] indexData = directory.ToArray();
        if (encryptedDirectory) Array.Resize(ref indexData, (indexData.Length + 15) & ~15);
        PlainToc = new byte[DirectoryOffset + indexData.Length + 64];
        FIoStoreTocHeader.TOC_MAGIC.CopyTo(PlainToc, 0);
        PlainToc[16] = 5;
        foreach (var field in new Dictionary<int, uint> { [20] = 144, [24] = 2, [28] = 2, [32] = 12, [36] = 1, [40] = 32, [44] = 256, [48] = (uint)indexData.Length, [52] = 1, [84] = 1, [96] = 1 })
            BinaryPrimitives.WriteUInt32LittleEndian(PlainToc.AsSpan(field.Key), field.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(PlainToc.AsSpan(56), 987654321);
        PlainToc.AsSpan(144, 24).Fill(0xAB);
        PlainToc[156] = 0xBC;
        PlainToc[80] = 9;
        PlainToc[BlockOffset + 5] = PlainToc[BlockOffset + 8] = 19;
        PlainToc[BlockOffset + 12] = 64;
        PlainToc[BlockOffset + 17] = (byte)packed.Length;
        PlainToc[BlockOffset + 20] = (byte)cooked.Length;
        PlainToc[BlockOffset + 23] = 1;
        Encoding.ASCII.GetBytes("Zlib").CopyTo(PlainToc, BlockOffset + 24);
        indexData.CopyTo(PlainToc, DirectoryOffset);
        PlainUcas = new byte[64 + ((packed.Length + 15) & ~15)];
        Encoding.ASCII.GetBytes("uncompressed cooked").CopyTo(PlainUcas, 0);
        packed.CopyTo(PlainUcas, 64);
        byte[] encryptedToc = (byte[])PlainToc.Clone();
        byte[] encryptedUcas = (byte[])PlainUcas.Clone();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        encryptor.TransformBlock(encryptedUcas, 0, 32, encryptedUcas, 0);
        encryptor.TransformBlock(encryptedUcas, 64, encryptedUcas.Length - 64, encryptedUcas, 64);
        if (encryptedDirectory) encryptor.TransformBlock(encryptedToc, DirectoryOffset, indexData.Length, encryptedToc, DirectoryOffset);
        encryptedToc[80] |= 2;
        File.WriteAllBytes(Utoc, encryptedToc);
        File.WriteAllBytes(Ucas, encryptedUcas);
    }

    private static void WriteString(BinaryWriter writer, string text, bool wide)
    {
        byte[] data = (wide ? Encoding.Unicode : Encoding.UTF8).GetBytes(text + '\0');
        writer.Write(wide ? -(text.Length + 1) : data.Length);
        writer.Write(data);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
