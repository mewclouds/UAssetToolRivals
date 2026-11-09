using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace UAssetTool;

public sealed record AssetLayoutReadRequest(
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("usmap_path")] string? UsmapPath);

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
            try
            {
                if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(mappingsPath))
                    throw new ArgumentException("file_path and usmap_path are required for each read.");

                string fullMappingsPath = Path.GetFullPath(mappingsPath);
                if (!mappings.TryGetValue(fullMappingsPath, out var parsed))
                {
                    parsed = new Lazy<Usmap>(() => new Usmap(fullMappingsPath));
                    mappings.Add(fullMappingsPath, parsed);
                }

                var asset = LoadAssetWithMappings(assetPath, parsed.Value.ForkForAssetRead());
                PreloadReferencedAssetsForSchemas(asset);
                bool clean = asset.Exports.All(export => export is not RawExport);
                reads.Add(new AssetLayoutReadResult(assetPath, mappingsPath, asset.HasUnversionedProperties,
                    clean, GetLayoutExportClasses(asset), clean ? HashLayoutValues(asset) : null));
            }
            catch (Exception ex)
            {
                reads.Add(new AssetLayoutReadResult(assetPath, mappingsPath, Error: ex.Message));
            }
        }

        return new UAssetResponse
        {
            Success = true,
            Message = $"Processed {reads.Count} asset layout read(s).",
            Data = new { reads, layouts_loaded = mappings.Values.Count(layout => layout.IsValueCreated) }
        };
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
