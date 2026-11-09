using System.Text.Json;
using UAssetTool;
using UAssetTool.IoStore;

internal static class IoStorePatchCheckTests
{
    internal static void CheckMixedBundles()
    {
        using var fixture = new Fixture(false, false);
        string gamePaks = Directory.CreateDirectory(Path.Combine(fixture.Root, "game")).FullName;
        string mappings = AssetLayoutReadTests.WriteMappings(fixture.Root);
        string versioned = Path.Combine(fixture.Root, "versioned.utoc");
        string unreadable = Path.Combine(fixture.Root, "unreadable.utoc");
        WriteIoStore(versioned, Fixture.ZenPackage(false));
        WriteIoStore(unreadable, new byte[12]);

        string stub = Path.Combine(fixture.Root, "stub.pak");
        string metadata = Path.Combine(fixture.Root, "metadata.pak_crateoff");
        string hybrid = Path.Combine(fixture.Root, "hybrid.pak");
        ChunkNamesPakWriter.CreateStub(stub);
        WritePak(metadata, "meta/patched_files");
        WritePak(hybrid, "loose.bin");
        var before = Directory.EnumerateFiles(fixture.Root).ToDictionary(file => file, File.ReadAllBytes);

        var request = new UAssetRequest
        {
            Action = "check_iostore_patch",
            GamePaks = gamePaks,
            PatchLayouts = [new(200, mappings)],
            PatchBundles = [
                new(versioned, Path.Combine(fixture.Root, "missing.pak")),
                new(versioned, metadata),
                new(versioned, hybrid),
                new(unreadable, stub),
                new(versioned, stub)
            ]
        };
        var response = UAssetTool.Program.ProcessRequest(request);

        Require(response.Success, response.Message);
        var data = JsonSerializer.SerializeToElement(response.Data);
        Require(data.GetProperty("game_build").GetInt32() == 200, "The response returned the wrong build.");
        var bundles = data.GetProperty("bundles");
        string?[] statuses = bundles.EnumerateArray().Select(bundle => bundle.GetProperty("status").GetString()).ToArray();
        Require(statuses.SequenceEqual(new[] { "failed", "current", "blocked", "failed", "current" }),
            $"Unexpected verdicts: {string.Join(", ", statuses)}.");
        Require(bundles[3].GetProperty("file_path").GetString() == unreadable &&
                bundles[3].GetProperty("detail").GetString()?.Contains("1 failed", StringComparison.Ordinal) == true,
            "An unreadable prefix did not fall back to extraction and report the extraction failure.");
        Require(bundles.EnumerateArray().All(bundle => bundle.GetProperty("assets").GetArrayLength() == 0),
            "The check returned asset findings for bundles it did not analyze.");

        request.PatchLayouts = [new(200, mappings), new(201, mappings)];
        Require(!UAssetTool.Program.ProcessRequest(request).Success, "The check accepted a newer layout as an earlier build.");
        foreach (var (file, bytes) in before)
            Require(File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes), "The check changed an input file.");
    }

    private static void WriteIoStore(string path, byte[] package)
    {
        using var writer = new IoStoreWriter(path, enableCompression: false);
        writer.WriteChunk(FIoChunkId.Create(1, 0, EIoChunkType.ExportBundleData), "Marvel/Content/Fixture.uasset", package);
        writer.Complete();
    }

    private static void WritePak(string path, string entry)
    {
        using var writer = new PakWriter();
        writer.AddEntry(entry, [1]);
        writer.Write(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
