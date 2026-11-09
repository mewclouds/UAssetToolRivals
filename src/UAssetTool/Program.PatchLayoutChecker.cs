using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Unversioned;

namespace UAssetTool;

public partial class Program
{
    private sealed class PatchLayoutChecker
    {
        private readonly IReadOnlyList<IoStorePatchLayout> _layouts;
        private readonly Dictionary<string, Lazy<Usmap>> _mappings =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly Dictionary<string, ParsedLayout[]> _candidates = new(StringComparer.Ordinal);
        private readonly Usmap _current;

        private sealed record ParsedLayout(int Build, Usmap Mappings);
        private sealed record LayoutRead(UAsset Asset, bool Clean, string? Values);

        internal PatchLayoutChecker(IReadOnlyList<IoStorePatchLayout> layouts)
        {
            _layouts = layouts;
            _current = GetLayoutMappings(_mappings, layouts[0].UsmapPath!);
        }

        internal PatchAssetVerdict? CheckAsset(string path, string relative)
        {
            var current = Read(path, _current);
            if (!current.Asset.HasUnversionedProperties)
                return null;

            var candidates = OlderCandidates(GetLayoutExportClasses(current.Asset));
            if (candidates.Length == 0)
                return null;

            foreach (var source in candidates)
            {
                var older = Read(path, source.Mappings);
                if (!older.Clean || (current.Clean && current.Values == older.Values))
                    continue;

                if (current.Clean)
                {
                    bool currentExact = Prove(path, current.Asset, _current, out _);
                    bool olderExact = Prove(path, older.Asset, source.Mappings, out string document);
                    if (currentExact && !olderExact)
                        return null;
                    if (!currentExact && olderExact)
                        return CheckPortability(relative, older.Asset, document, source.Build);
                    return new PatchAssetVerdict(relative, "ambiguous", source.Build,
                        $"reads cleanly in both build {source.Build} and the current layout");
                }

                if (!Prove(path, older.Asset, source.Mappings, out string olderDocument))
                    return new PatchAssetVerdict(relative, "blocked", source.Build,
                        $"build {source.Build} reads it, but cannot write it back unchanged");
                return CheckPortability(relative, older.Asset, olderDocument, source.Build);
            }

            return current.Clean
                ? null
                : new PatchAssetVerdict(relative, "blocked", Reason: "no known game layout reads this asset");
        }

        private PatchAssetVerdict CheckPortability(string path, UAsset asset, string document, int sourceBuild)
        {
            string? reason = AssetLayoutPortability.GetBlockingReason(asset, document, _current);
            return reason == null
                ? new PatchAssetVerdict(path, "updated", sourceBuild)
                : new PatchAssetVerdict(path, "blocked", sourceBuild, reason);
        }

        private static LayoutRead Read(string path, Usmap source)
        {
            var asset = LoadAssetWithMappings(path, source.ForkForAssetRead());
            PreloadReferencedAssetsForSchemas(asset);
            bool clean = asset.Exports.All(export => export is not RawExport);
            return new LayoutRead(asset, clean, clean && asset.HasUnversionedProperties ? HashLayoutValues(asset) : null);
        }

        private static bool Prove(string path, UAsset asset, Usmap source, out string document)
        {
            document = asset.SerializeJson();
            return WritesOriginalExports(path, document, source);
        }

        private ParsedLayout[] OlderCandidates(string[] classes)
        {
            if (classes.Length == 0)
                return [];

            string key = string.Join('\0', classes);
            if (_candidates.TryGetValue(key, out var cached))
                return cached;

            var relevant = CollectSchemas(_current, classes);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new List<ParsedLayout>();
            foreach (var layout in _layouts.Skip(1))
            {
                var older = GetLayoutMappings(_mappings, layout.UsmapPath!);
                var names = new HashSet<string>(relevant, StringComparer.Ordinal);
                names.UnionWith(CollectSchemas(older, classes));
                string fingerprint = DescribeSchemas(older, names);
                if (fingerprint == DescribeSchemas(_current, names) || !seen.Add(fingerprint))
                    continue;
                candidates.Add(new ParsedLayout(layout.Build, older));
            }
            var result = candidates.ToArray();
            _candidates.Add(key, result);
            return result;
        }

        // Match the patch checker's schema comparison. Usmap also indexes
        // aliases and permits case-insensitive lookups for asset decoding.
        private static UsmapSchema? ExactSchema(Usmap mapping, string name) =>
            mapping.Schemas.TryGetValue(name, out var schema) && schema.Name == name ? schema : null;

        private static HashSet<string> CollectSchemas(Usmap mapping, string[] classes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(classes);
            while (pending.TryPop(out var name))
            {
                if (string.IsNullOrEmpty(name) || !seen.Add(name) || ExactSchema(mapping, name) is not { } schema)
                    continue;
                if (!string.IsNullOrEmpty(schema.SuperType))
                    pending.Push(schema.SuperType);
                foreach (var property in schema.Properties.Values)
                    CollectStructTypes(property.PropertyData, pending);
            }
            return seen;
        }

        private static void CollectStructTypes(UsmapPropertyData type, Stack<string> pending)
        {
            switch (type)
            {
                case UsmapStructData structure:
                    pending.Push(structure.StructType);
                    break;
                case UsmapMapData map:
                    CollectStructTypes(map.InnerType, pending);
                    CollectStructTypes(map.ValueType, pending);
                    break;
                case UsmapArrayData array:
                    CollectStructTypes(array.InnerType, pending);
                    break;
                case UsmapEnumData enumeration:
                    CollectStructTypes(enumeration.InnerType, pending);
                    break;
            }
        }

        private static string DescribeSchemas(Usmap mapping, HashSet<string> names)
        {
            var description = new StringBuilder();
            foreach (string name in names.Order(StringComparer.Ordinal))
            {
                description.Append(name).Append(':');
                if (ExactSchema(mapping, name) is not { } schema)
                {
                    description.Append("missing;");
                    continue;
                }

                description.Append(schema.SuperType).Append(':').Append(schema.PropCount);
                // UAssetAPI expands static arrays into one slot per element.
                // The first element records the starting slot and array size.
                foreach (var property in schema.Properties.Values.Where(property => property.ArrayIndex == 0).OrderBy(property => property.SchemaIndex))
                {
                    description.Append(',').Append(property.SchemaIndex).Append('/').Append(property.ArraySize)
                        .Append('/').Append(property.Name).Append('/');
                    DescribeType(description, property.PropertyData);
                }
                description.Append(';');
            }
            return description.ToString();
        }

        private static void DescribeType(StringBuilder description, UsmapPropertyData type)
        {
            description.Append((byte)type.Type);
            switch (type)
            {
                case UsmapStructData structure:
                    description.Append('<').Append(structure.StructType).Append('>');
                    break;
                case UsmapEnumData enumeration:
                    description.Append('{').Append(enumeration.Name).Append("}(");
                    DescribeType(description, enumeration.InnerType);
                    description.Append(')');
                    break;
                case UsmapArrayData array:
                    description.Append('(');
                    DescribeType(description, array.InnerType);
                    description.Append(')');
                    break;
                case UsmapMapData map:
                    description.Append('(');
                    DescribeType(description, map.InnerType);
                    description.Append(',');
                    DescribeType(description, map.ValueType);
                    description.Append(')');
                    break;
            }
        }
    }
}
