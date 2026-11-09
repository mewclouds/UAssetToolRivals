using System.Text.Json.Serialization;
using UAssetTool.IoStore;
using UAssetTool.ZenPackage;

namespace UAssetTool;

public sealed record IoStorePatchBundle(
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("pak_path")] string? PakPath);

public sealed record IoStorePatchLayout(
    [property: JsonPropertyName("build")] int Build,
    [property: JsonPropertyName("usmap_path")] string? UsmapPath);

public partial class Program
{
    private sealed record PatchBundleVerdict(
        [property: JsonPropertyName("file_path")] string? FilePath,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("assets")] IReadOnlyList<PatchAssetVerdict> Assets,
        [property: JsonPropertyName("detail")] string? Detail = null);

    private sealed record PatchAssetVerdict(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("from_build")] int FromBuild = 0,
        [property: JsonPropertyName("reason")] string? Reason = null);

    private static UAssetResponse CheckIoStorePatchJson(
        string? gamePaks, List<IoStorePatchBundle?>? bundles, List<IoStorePatchLayout?>? layouts, string? aesKeyHex)
    {
        if (string.IsNullOrWhiteSpace(gamePaks) || !Directory.Exists(gamePaks))
            throw new ArgumentException("game_paks must be an existing directory.");
        if (bundles == null || bundles.Count == 0)
            throw new ArgumentException("patch_bundles must contain at least one bundle.");
        if (layouts == null || layouts.Count == 0)
            throw new ArgumentException("patch_layouts must contain the current game's mappings.");

        var orderedLayouts = new List<IoStorePatchLayout>(layouts.Count);
        foreach (var layout in layouts)
        {
            if (layout == null || layout.Build <= 0 || string.IsNullOrWhiteSpace(layout.UsmapPath))
                throw new ArgumentException("Each patch layout needs a positive build number and a mappings path.");
            if (orderedLayouts.Count > 0 && layout.Build >= orderedLayouts[^1].Build)
                throw new ArgumentException("Provide the current patch layout first, followed by older builds in descending order.");
            orderedLayouts.Add(layout);
        }

        string key = string.IsNullOrEmpty(aesKeyHex) ? IoStoreReader.DEFAULT_AES_KEY_HEX : aesKeyHex;
        byte[] aesKey = Convert.FromHexString(key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key);
        if (aesKey.Length != 32)
            throw new ArgumentException("An AES-256 key must contain 64 hex digits.");

        var scans = IoStorePackageScanner.Scan(bundles.Select(bundle => bundle?.FilePath ?? "").ToArray(), aesKey);
        var verdicts = new PatchBundleVerdict[bundles.Count];
        var candidates = new List<int>();
        for (int i = 0; i < bundles.Count; i++)
        {
            var bundle = bundles[i];
            try
            {
                if (bundle == null || string.IsNullOrWhiteSpace(bundle.FilePath) || string.IsNullOrWhiteSpace(bundle.PakPath) ||
                    !string.Equals(Path.GetExtension(bundle.FilePath), ".utoc", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Each bundle needs a UTOC file_path and a companion pak_path.");
                if (!File.Exists(bundle.FilePath) || !File.Exists(Path.ChangeExtension(bundle.FilePath, ".ucas")))
                    throw new FileNotFoundException("The bundle needs both its UTOC and UCAS files.");

                using var pak = new IoStore.PakReader(bundle.PakPath, key);
                if (pak.Files.Any(IsLooseCompanionFile))
                    verdicts[i] = new PatchBundleVerdict(bundle.FilePath, "blocked", [],
                        "keeps loose files in its .pak, which the updater does not rebuild yet");
                else if (scans[i].Error != null || scans[i].Packages.Any(package => package.HasUnversionedProperties != false))
                    candidates.Add(i);
                else
                    verdicts[i] = new PatchBundleVerdict(bundle.FilePath, "current", []);
            }
            catch (Exception ex)
            {
                verdicts[i] = new PatchBundleVerdict(bundle?.FilePath, "failed", [], ex.Message);
            }
        }

        if (candidates.Count > 0)
        {
            var checker = new PatchLayoutChecker(orderedLayouts);
            using var game = LoadLegacyGameContext(gamePaks, key);
            foreach (int i in candidates)
            {
                string path = bundles[i]!.FilePath!;
                try
                {
                    verdicts[i] = CheckPatchBundle(path, scans[i], game, checker);
                }
                catch (Exception ex)
                {
                    verdicts[i] = new PatchBundleVerdict(path, "failed", [], ex.Message);
                }
            }
        }

        return new UAssetResponse
        {
            Success = true,
            Message = $"Checked {verdicts.Length} IoStore bundle(s).",
            Data = new { game_build = orderedLayouts[0].Build, bundles = verdicts }
        };
    }

    private static bool IsLooseCompanionFile(string path) =>
        !path.Contains(".cratebug-companion-stub", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(path.Replace('\\', '/')).ToLowerInvariant() is not ("chunknames" or "patched_files");

    private static PatchBundleVerdict CheckPatchBundle(
        string path, IoStoreBundleScan scan, FZenPackageContext game, PatchLayoutChecker checker)
    {
        // A missing prefix verdict falls back to reading each extracted asset.
        HashSet<ulong>? flagged = scan.Error != null || scan.Packages.Any(package => package.HasUnversionedProperties == null)
            ? null
            : scan.Packages.Where(package => package.HasUnversionedProperties == true).Select(package => package.PackageId).ToHashSet();
        var staging = Directory.CreateTempSubdirectory("uatr-patch-check-");
        try
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            using (var mod = game.CreateModContext())
            {
                mod.LoadContainerWithPriority(path);
                var extracted = ExtractLegacyPackages(mod, mod.GetPackageIdsFromContainer(mod.LastContainerIndex),
                    staging.FullName, filterPatterns: null, withDeps: false,
                    onExtracted: (id, relative) =>
                    {
                        if ((flagged == null || flagged.Contains(id)) &&
                            string.Equals(Path.GetExtension(relative), ".uasset", StringComparison.OrdinalIgnoreCase))
                            paths.Add(relative.Replace('\\', '/'));
                    });
                if (!extracted.Success)
                    throw new InvalidDataException(extracted.Message);
                if (extracted.Data is not LegacyExtractionData data || data.FailedCount != 0)
                    throw new InvalidDataException(extracted.Message);
            }

            var assets = new List<PatchAssetVerdict>();
            foreach (string relative in paths.Order(StringComparer.Ordinal))
            {
                try
                {
                    var verdict = checker.CheckAsset(Path.Combine(staging.FullName, relative), relative);
                    if (verdict != null)
                        assets.Add(verdict);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"{relative}: {ex.Message}", ex);
                }
            }

            bool updated = assets.Any(asset => asset.Outcome == "updated");
            bool blocked = assets.Any(asset => asset.Outcome == "blocked");
            string status = (updated, blocked) switch
            {
                (true, true) => "partial",
                (true, false) => "updated",
                (false, true) => "blocked",
                _ => assets.Any(asset => asset.Outcome == "ambiguous") ? "ambiguous" : "current"
            };
            return new PatchBundleVerdict(path, status, assets);
        }
        finally
        {
            // Only remove the directory this check created.
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(staging.Parent?.FullName, temp, comparison))
                throw new IOException("Unexpected patch staging directory.");
            staging.Delete(recursive: true);
        }
    }
}
