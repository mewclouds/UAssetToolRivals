using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace UAssetTool;

public sealed record AssetLayoutReadRequest(
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("usmap_path")] string? UsmapPath,
    [property: JsonPropertyName("verify_exports")] bool VerifyExports = false,
    [property: JsonPropertyName("target_usmap_path")] string? TargetUsmapPath = null);

public partial class Program
{
    // Layout comparisons concern the decoded exports. File offsets and
    // the unversioned headers can differ while the property values match.
    private static readonly string[] LayoutValueFields = ["$type", "ObjectName", "ClassIndex", "Data", "Extras"];

    private sealed record AssetLayoutReadResult(
        [property: JsonPropertyName("file_path")] string? FilePath,
        [property: JsonPropertyName("usmap_path")] string? UsmapPath,
        [property: JsonPropertyName("has_unversioned_properties")] bool? HasUnversionedProperties = null,
        [property: JsonPropertyName("clean")] bool? Clean = null,
        [property: JsonPropertyName("classes")] string[]? Classes = null,
        [property: JsonPropertyName("values_sha256")] string? ValuesSha256 = null,
        [property: JsonPropertyName("writes_original")] bool? WritesOriginal = null,
        [property: JsonPropertyName("portable")] bool? Portable = null,
        [property: JsonPropertyName("portability_reason")] string? PortabilityReason = null,
        [property: JsonPropertyName("error")] string? Error = null);

    private static UAssetResponse ReadAssetLayoutsJson(List<AssetLayoutReadRequest?>? requests)
    {
        if (requests == null || requests.Count == 0)
            return new UAssetResponse { Success = false, Message = "layout_reads must contain at least one asset and mappings pair." };

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var mappings = new Dictionary<string, Lazy<Usmap>>(pathComparer);
        var reads = new List<AssetLayoutReadResult>(requests.Count);
        foreach (var request in requests)
        {
            string? assetPath = request?.FilePath;
            string? mappingsPath = request?.UsmapPath;
            var read = new AssetLayoutReadResult(assetPath, mappingsPath);
            try
            {
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(mappingsPath))
                    throw new ArgumentException("file_path and usmap_path are required for each read.");

                var source = GetLayoutMappings(mappings, mappingsPath);
                var asset = LoadAssetWithMappings(assetPath, source.ForkForAssetRead());
                PreloadReferencedAssetsForSchemas(asset);
                bool clean = asset.Exports.All(export => export is not RawExport);
                read = read with
                {
                    HasUnversionedProperties = asset.HasUnversionedProperties,
                    Clean = clean,
                    Classes = GetLayoutExportClasses(asset),
                    ValuesSha256 = clean ? HashLayoutValues(asset) : null
                };
                if (clean && (request!.VerifyExports || request.TargetUsmapPath != null))
                {
                    string document = asset.SerializeJson();
                    bool exact = WritesOriginalExports(assetPath, document, source);
                    read = read with { WritesOriginal = exact };
                    if (exact && request.TargetUsmapPath != null)
                    {
                        var target = GetLayoutMappings(mappings, request.TargetUsmapPath);
                        string? reason = AssetLayoutPortability.GetBlockingReason(asset, document, target);
                        read = read with { Portable = reason == null, PortabilityReason = reason };
                    }
                }
            }
            catch (Exception ex)
            {
                read = read with { Error = ex.Message };
            }
            reads.Add(read);
        }

        return new UAssetResponse
        {
            Success = true,
            Message = $"Processed {reads.Count} asset layout read(s).",
            Data = new { reads, layouts_loaded = mappings.Values.Count(layout => layout.IsValueCreated) }
        };
    }

    private static Usmap GetLayoutMappings(Dictionary<string, Lazy<Usmap>> mappings, string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!mappings.TryGetValue(fullPath, out var parsed))
        {
            parsed = new Lazy<Usmap>(() => new Usmap(fullPath));
            mappings.Add(fullPath, parsed);
        }
        return parsed.Value;
    }

    private static bool WritesOriginalExports(string assetPath, string document, Usmap source)
    {
        // Use the same JSON round trip as from_json, with a fresh mappings
        // view. Its write-back is isolated from the source asset directory.
        var proof = UAsset.DeserializeJson(document);
        proof.Mappings = source.ForkForAssetRead();
        proof.FilePath = Path.Combine(Path.GetTempPath(), $"uatr-layout-proof-{Guid.NewGuid():N}", Path.GetFileName(assetPath));
        PreloadReferencedAssetsForSchemas(proof);
        proof.Write(out var package, out var exports);
        using (package)
        using (exports)
        {
            if (exports == null)
                throw new InvalidDataException("The write did not produce a separate export stream.");

            byte[] original = File.ReadAllBytes(Path.ChangeExtension(assetPath, ".uexp"));
            return original.AsSpan().SequenceEqual(exports.GetBuffer().AsSpan(0, checked((int)exports.Length)));
        }
    }

    private static string[] GetLayoutExportClasses(UAsset asset)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in asset.Exports)
        {
            int index = export.ClassIndex?.Index ?? 0;
            if (index >= 0 || ~index >= asset.Imports.Count)
                continue;

            string? name = asset.Imports[~index].ObjectName?.ToString();
            if (!string.IsNullOrEmpty(name))
                classes.Add(name);
        }
        return classes.Order(StringComparer.Ordinal).ToArray();
    }

    private static string HashLayoutValues(UAsset asset)
    {
        using var exports = JsonDocument.Parse(asset.SerializeJsonObject(asset.Exports), new JsonDocumentOptions { MaxDepth = 256 });
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartArray();
            foreach (var export in exports.RootElement.EnumerateArray())
            {
                writer.WriteStartArray();
                foreach (var field in LayoutValueFields)
                {
                    export.TryGetProperty(field, out var value);
                    WriteLayoutValue(writer, value);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    private static void WriteLayoutValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (property.Name is "OriginalUnversionedHeader" or "OriginalStructHeader")
                        continue;
                    writer.WritePropertyName(property.Name);
                    WriteLayoutValue(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteLayoutValue(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
