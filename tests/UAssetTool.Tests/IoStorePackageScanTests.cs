using System.Diagnostics;
using System.Text.Json;
using UAssetTool;
using UAssetTool.IoStore;

internal static class IoStorePackageScanTests
{
    internal static void ScanBatch()
    {
        using var fixture = new Fixture(false, false);
        string plain = Path.Combine(fixture.Root, "plain.utoc");
        string encrypted = Path.Combine(fixture.Root, "encrypted.utoc");
        WriteBundle(plain, encrypt: false);
        WriteBundle(encrypted, encrypt: true, key: fixture.KeyHex);
        var inputs = new[] { encrypted, plain, plain, encrypted, plain };
        var sourceFiles = new[] { plain, encrypted, Path.ChangeExtension(plain, ".ucas"), Path.ChangeExtension(encrypted, ".ucas") };
        var originalBytes = sourceFiles.Select(File.ReadAllBytes).ToArray();

        var responses = ScanTwiceInWorker(inputs, fixture.KeyHex);
        var results = responses[0].GetProperty("data").GetProperty("bundles");
        var repeated = responses[1].GetProperty("data").GetProperty("bundles");

        Require(results.GetRawText() == repeated.GetRawText(), "The repeated batch returned different results.");
        Require(results.GetArrayLength() == inputs.Length, "The scan lost an input.");
        for (int i = 0; i < inputs.Length; i++)
        {
            var bundle = results[i];
            Require(bundle.GetProperty("file_path").GetString() == inputs[i], "The batch changed input order.");
            Require(bundle.GetProperty("error").ValueKind == JsonValueKind.Null, "A readable bundle failed.");
            var packages = bundle.GetProperty("packages");
            Require(packages.GetArrayLength() == 2, "The scan missed a package or included a bulk chunk.");
            Require(!packages[0].GetProperty("has_unversioned_properties").GetBoolean(), "A versioned package was flagged.");
            Require(packages[1].GetProperty("has_unversioned_properties").GetBoolean(), "The unversioned flag was missed.");
            Require(packages[1].GetProperty("path").GetString()!.EndsWith("Unversioned.uasset", StringComparison.Ordinal),
                "The scan returned the wrong package path.");
        }
        for (int i = 0; i < sourceFiles.Length; i++)
            Require(File.ReadAllBytes(sourceFiles[i]).AsSpan().SequenceEqual(originalBytes[i]), "The scan changed an input file.");

        var wrongKey = Scan([encrypted], new string('0', 64))[0].GetProperty("packages");
        Require(wrongKey.GetArrayLength() == 2, "The wrong key hid a package.");
        foreach (var package in wrongKey.EnumerateArray())
            RequireUnknown(package);
    }

    internal static void ScanCompressedSummary()
    {
        using var fixture = new Fixture(true, false, cookedPackage: Fixture.ZenPackage(true));

        var packages = Scan([fixture.Utoc], fixture.KeyHex)[0].GetProperty("packages");

        Require(packages.GetArrayLength() == 1, "The scan missed the compressed package.");
        Require(packages[0].GetProperty("has_unversioned_properties").GetBoolean(), "The compressed summary was not decoded.");
    }

    internal static void ReportUnreadableInputs()
    {
        using var fixture = new Fixture(false, false);
        string brokenPackage = Path.Combine(fixture.Root, "broken-package.utoc");
        string badMagic = Path.Combine(fixture.Root, "bad-magic.utoc");
        string unsupported = Path.Combine(fixture.Root, "unsupported.utoc");
        WriteBundle(brokenPackage, encrypt: false, includeBadPackages: true);
        WriteBundle(badMagic, encrypt: false);
        WriteBundle(unsupported, encrypt: false);
        byte[] toc = File.ReadAllBytes(badMagic);
        toc[0] ^= 0xFF;
        File.WriteAllBytes(badMagic, toc);
        toc = File.ReadAllBytes(unsupported);
        toc[16] = (byte)EIoStoreTocVersion.OnDemandMetaData;
        File.WriteAllBytes(unsupported, toc);

        var results = Scan([badMagic, brokenPackage, unsupported, Path.Combine(fixture.Root, "missing.utoc")]);

        foreach (int index in new[] { 0, 2, 3 })
        {
            Require(results[index].GetProperty("error").ValueKind == JsonValueKind.String, "A failed bundle was accepted.");
            Require(results[index].GetProperty("packages").GetArrayLength() == 0, "A failed bundle returned packages.");
        }
        var packages = results[1].GetProperty("packages");
        Require(packages.GetArrayLength() == 4, "The scan lost packages next to a bad header.");
        Require(packages[1].GetProperty("has_unversioned_properties").GetBoolean(), "A bad package hid a valid result.");
        RequireUnknown(packages[2]);
        RequireUnknown(packages[3]);
    }

    internal static void RejectInvalidRequests()
    {
        var empty = UAssetTool.Program.ProcessRequest(new UAssetRequest { Action = "scan_iostore_packages", FilePaths = [] });
        var badKey = UAssetTool.Program.ProcessRequest(new UAssetRequest
        {
            Action = "scan_iostore_packages", FilePaths = ["fixture.utoc"], AesKey = "AA"
        });
        Require(!empty.Success && !badKey.Success, "The action accepted an invalid request.");
    }

    private static JsonElement[] ScanTwiceInWorker(string[] paths, string key)
    {
        string executable = OperatingSystem.IsWindows() ? "UAssetTool.exe" : "UAssetTool";
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, executable))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("The worker did not start.");
        try
        {
            string request = JsonSerializer.Serialize(new { action = "scan_iostore_packages", file_paths = paths, aes_key = key });
            var output = worker.StandardOutput.ReadToEndAsync();
            worker.StandardInput.WriteLine(request);
            worker.StandardInput.WriteLine(request);
            worker.StandardInput.Close();
            Require(worker.WaitForExit(15_000), "The worker did not finish two batches.");
            Require(worker.ExitCode == 0, "The worker process failed.");

            var responses = output.GetAwaiter().GetResult()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .ToArray();
            Require(responses.Length == 2 && responses.All(response => response.GetProperty("success").GetBoolean()),
                "The worker did not return two successful NDJSON responses.");
            return responses;
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill();
                worker.WaitForExit();
            }
        }
    }

    private static JsonElement Scan(string[] paths, string? key = null)
    {
        var response = UAssetTool.Program.ProcessRequest(new UAssetRequest
        {
            Action = "scan_iostore_packages", FilePaths = paths.ToList(), AesKey = key
        });
        Require(response.Success, response.Message);
        return JsonSerializer.SerializeToElement(response.Data).GetProperty("bundles");
    }

    private static void WriteBundle(string path, bool encrypt, string? key = null, bool includeBadPackages = false)
    {
        using var writer = new IoStoreWriter(path, enableCompression: false, enableEncryption: encrypt, aesKeyHex: key);
        writer.WriteChunk(FIoChunkId.Create(1, 0, EIoChunkType.ExportBundleData), "Marvel/Content/Versioned.uasset", Fixture.ZenPackage(false));
        writer.WriteChunk(FIoChunkId.Create(2, 0, EIoChunkType.ExportBundleData), "Marvel/Content/Unversioned.uasset", Fixture.ZenPackage(true));
        writer.WriteChunk(FIoChunkId.Create(2, 0, EIoChunkType.BulkData), "Marvel/Content/Unversioned.ubulk", new byte[128]);
        if (includeBadPackages)
        {
            writer.WriteChunk(FIoChunkId.Create(3, 0, EIoChunkType.ExportBundleData), null, new byte[12]);
            writer.WriteChunk(FIoChunkId.Create(4, 0, EIoChunkType.ExportBundleData), null, new byte[64]);
        }
        writer.Complete();
    }

    private static void RequireUnknown(JsonElement package)
    {
        Require(package.GetProperty("has_unversioned_properties").ValueKind == JsonValueKind.Null, "An unreadable package was classified.");
        Require(package.GetProperty("error").ValueKind == JsonValueKind.String, "An unreadable package has no error.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
