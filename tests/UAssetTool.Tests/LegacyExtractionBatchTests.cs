using System.Text.Json;
using UAssetTool;
using UAssetTool.IoStore;

internal static class LegacyExtractionBatchTests
{
    private const string AssetPath = "Marvel/Content/Fixture.uasset";

    internal static void ExtractInBothOrders()
    {
        using var fixture = new Fixture(false, false);
        string gamePaks = Directory.CreateDirectory(Path.Combine(fixture.Root, "game")).FullName;
        string a = Path.Combine(fixture.Root, "a.utoc");
        string b = Path.Combine(fixture.Root, "b.utoc");
        string missing = Path.Combine(fixture.Root, "missing.utoc");
        WriteBundle(Path.Combine(gamePaks, "base.utoc"), unversioned: false, [3], [30]);
        WriteBundle(a, unversioned: true, [1], []);
        WriteBundle(b, unversioned: false, [2], [20]);

        string referenceA = ExtractOne(gamePaks, a, Path.Combine(fixture.Root, "reference-a"));
        string referenceB = ExtractOne(gamePaks, b, Path.Combine(fixture.Root, "reference-b"));
        string forwardRoot = Path.Combine(fixture.Root, "forward");
        var forward = ExtractBatch(gamePaks, [a, missing, b], forwardRoot);
        var reverse = ExtractBatch(gamePaks, [b, a], Path.Combine(fixture.Root, "reverse"), parallel: true);

        Require(forward.GetArrayLength() == 3 && reverse.GetArrayLength() == 2, "The batch lost a bundle.");
        Require(!forward[1].GetProperty("result").GetProperty("success").GetBoolean(), "The missing mod was accepted.");
        RequireSameFiles(referenceA, BundleOutput(forward[0], a));
        RequireSameFiles(referenceB, BundleOutput(forward[2], b));
        RequireSameFiles(referenceB, BundleOutput(reverse[0], b));
        RequireSameFiles(referenceA, BundleOutput(reverse[1], a));
        string bulkPath = Path.ChangeExtension(Path.Combine(referenceA, AssetPath), ".ubulk");
        Require(File.ReadAllBytes(bulkPath).AsSpan().SequenceEqual(new byte[] { 1 }), "The game bulk replaced mod A's bulk.");
        Require(!File.Exists(Path.ChangeExtension(bulkPath, ".uptnl")), "Mod A inherited optional bulk from the game.");

        var nonempty = UAssetTool.Program.ProcessRequest(new UAssetRequest
        {
            Action = "extract_iostore_legacy_batch", GamePaks = gamePaks, FilePaths = [b], OutputPath = forwardRoot
        });
        Require(!nonempty.Success, "The batch accepted a nonempty output directory.");
        RequireSameFiles(referenceA, BundleOutput(forward[0], a));
    }

    private static void WriteBundle(string path, bool unversioned, byte[] bulk, byte[] optional)
    {
        using var writer = new IoStoreWriter(path, enableCompression: false);
        writer.WriteChunk(FIoChunkId.Create(1, 0, EIoChunkType.ExportBundleData), AssetPath, Fixture.ZenPackage(unversioned));
        writer.WriteChunk(FIoChunkId.Create(1, 0, EIoChunkType.BulkData), null, bulk);
        if (optional.Length > 0)
            writer.WriteChunk(FIoChunkId.Create(1, 0, EIoChunkType.OptionalBulkData), null, optional);
        writer.Complete();
    }

    private static string ExtractOne(string gamePaks, string path, string output)
    {
        var response = UAssetTool.Program.ProcessRequest(new UAssetRequest
        {
            Action = "extract_iostore_legacy", GamePaks = gamePaks, ModPath = path, OutputPath = output
        });
        Require(response.Success, response.Message);
        return ExtractionOutput(JsonSerializer.SerializeToElement(response.Data));
    }

    private static JsonElement ExtractBatch(string gamePaks, List<string> paths, string output, bool parallel = false)
    {
        var response = UAssetTool.Program.ProcessRequest(new UAssetRequest
        {
            Action = "extract_iostore_legacy_batch", GamePaks = gamePaks, FilePaths = paths, OutputPath = output, Parallel = parallel
        });
        Require(response.Success, response.Message);
        return JsonSerializer.SerializeToElement(response.Data).GetProperty("bundles");
    }

    private static string BundleOutput(JsonElement bundle, string path)
    {
        Require(bundle.GetProperty("file_path").GetString() == path, "The batch changed the request order.");
        var result = bundle.GetProperty("result");
        Require(result.GetProperty("success").GetBoolean(), result.GetProperty("message").GetString()!);
        return ExtractionOutput(result.GetProperty("data"));
    }

    private static string ExtractionOutput(JsonElement data)
    {
        Require(data.GetProperty("extracted_count").GetInt32() == 1, "The extractor did not return the requested package.");
        Require(data.GetProperty("failed_count").GetInt32() == 0, "The package failed to extract.");
        return data.GetProperty("output_dir").GetString()!;
    }

    private static void RequireSameFiles(string expected, string actual)
    {
        static string[] FileNames(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file)).Order(StringComparer.Ordinal).ToArray();

        string[] names = FileNames(expected);
        Require(names.SequenceEqual(FileNames(actual)), "The batch changed the extracted file set.");
        foreach (string name in names)
        {
            byte[] expectedBytes = File.ReadAllBytes(Path.Combine(expected, name));
            Require(expectedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(actual, name))),
                $"The batch changed {name}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
