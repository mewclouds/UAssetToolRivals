using System.Globalization;
using System.Text.Json.Serialization;
using UAssetTool.ZenPackage;

namespace UAssetTool;

public partial class Program
{
    private const int MaxConcurrentLegacyExtractions = 2;

    private sealed record LegacyExtractionData(
        [property: JsonPropertyName("extracted_count")] int ExtractedCount,
        [property: JsonPropertyName("failed_count")] int FailedCount,
        [property: JsonPropertyName("output_dir")] string OutputDir,
        [property: JsonPropertyName("files")] IReadOnlyList<string> Files);

    private static FZenPackageContext LoadLegacyGameContext(string gamePaks, string? aesKeyHex)
    {
        var context = new FZenPackageContext();
        try
        {
            context.SetAesKey(string.IsNullOrEmpty(aesKeyHex) ? IoStore.IoStoreReader.DEFAULT_AES_KEY_HEX : aesKeyHex);
            Console.Error.WriteLine($"[ExtractIoStoreLegacy] Loading game containers from: {gamePaks}");

            // Imports can reference script objects from the global container.
            string globalPath = Path.Combine(gamePaks, "global.utoc");
            if (File.Exists(globalPath))
            {
                Console.Error.WriteLine("[ExtractIoStoreLegacy] Loading global.utoc...");
                context.LoadContainer(globalPath);
                context.LoadScriptObjectsFromContainer(0);
            }

            var utocFiles = Directory.GetFiles(gamePaks, "*.utoc", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith("global.utoc", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path)
                .ToList();
            Console.Error.WriteLine($"[ExtractIoStoreLegacy] Loading {utocFiles.Count} game containers...");
            foreach (var utocPath in utocFiles)
            {
                try { context.LoadContainer(utocPath); }
                catch (Exception ex) { Console.Error.WriteLine($"  Warning: Failed to load {Path.GetFileName(utocPath)}: {ex.Message}"); }
            }
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static UAssetResponse ExtractIoStoreLegacyBatchJson(
        string? gamePaks, List<string>? filePaths, string? outputPath, List<string>? filterPatterns, string? aesKeyHex, bool withDeps,
        bool parallel)
    {
        if (string.IsNullOrEmpty(gamePaks) || !Directory.Exists(gamePaks))
            return LegacyExtractionError("game_paks must be an existing directory.");
        if (filePaths == null || filePaths.Count == 0)
            return LegacyExtractionError("file_paths must contain at least one mod UTOC path.");
        if (string.IsNullOrEmpty(outputPath))
            return LegacyExtractionError("output_path is required.");

        Directory.CreateDirectory(outputPath);
        if (Directory.EnumerateFileSystemEntries(outputPath).Any())
            return LegacyExtractionError("output_path must be empty.");

        using var game = LoadLegacyGameContext(gamePaks, aesKeyHex);
        var bundles = new object[filePaths.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallel ? Math.Min(MaxConcurrentLegacyExtractions, Environment.ProcessorCount) : 1
        };
        Parallel.For(0, filePaths.Count, options, i =>
        {
            string modPath = filePaths[i];
            string bundleOutput = Path.Combine(outputPath, i.ToString(CultureInfo.InvariantCulture));
            UAssetResponse result;
            try
            {
                if (!File.Exists(modPath) || !string.Equals(Path.GetExtension(modPath), ".utoc", StringComparison.OrdinalIgnoreCase))
                    throw new FileNotFoundException("A mod UTOC file is required.");

                using var mod = game.CreateModContext();
                mod.LoadContainerWithPriority(modPath);
                Directory.CreateDirectory(bundleOutput);
                result = ExtractLegacyPackages(mod, mod.GetPackageIdsFromContainer(mod.LastContainerIndex), bundleOutput, filterPatterns, withDeps);
            }
            catch (Exception ex)
            {
                result = LegacyExtractionError(ex.Message);
            }
            bundles[i] = new { file_path = modPath, result };
        });

        return new UAssetResponse
        {
            Success = true,
            Message = $"Processed {bundles.Length} IoStore container(s).",
            Data = new { bundles }
        };
    }

    private static UAssetResponse LegacyExtractionError(string message) =>
        new() { Success = false, Message = $"Failed to extract IoStore (legacy): {message}" };
}
