using System.Text.Json;
using UAssetAPI;
using UAssetAPI.Unversioned;

namespace UAssetTool;

internal sealed class AssetLayoutPortability(Usmap source, Usmap target)
{
    private const int MaxFragmentCount = 0x7f; // The skip and value counts are seven bits each.
    private readonly Dictionary<(string Schema, string? Module), int[]> _slotMaps = new();

    internal static string? GetBlockingReason(UAsset asset, string document, Usmap target)
    {
        using var json = JsonDocument.Parse(document, new JsonDocumentOptions { MaxDepth = 256 });
        var checker = new AssetLayoutPortability(asset.Mappings, target);
        try
        {
            int index = 0;
            foreach (var export in json.RootElement.GetProperty("Exports").EnumerateArray())
            {
                if (HasHeader(export, "OriginalUnversionedHeader", out var header))
                {
                    var parsed = asset.Exports[index];
                    if (!parsed.ClassIndex.IsImport())
                        throw new InvalidDataException("An export class is not an import.");
                    string? className = parsed.GetClassTypeForAncestry(asset, out var module)?.Value?.Value;
                    if (string.IsNullOrEmpty(className))
                        throw new InvalidDataException("An export class import has no name.");
                    checker.CheckHeader(header, className, module?.Value?.Value);
                }
                checker.CheckNestedHeaders(export);
                index++;
            }
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    private static bool HasHeader(JsonElement value, string name, out JsonElement header) =>
        value.TryGetProperty(name, out header) && header.ValueKind == JsonValueKind.Object;

    private void CheckNestedHeaders(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
                CheckNestedHeaders(child);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (HasHeader(value, "OriginalStructHeader", out var header))
            {
                if (!value.TryGetProperty("StructType", out var type) || type.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(type.GetString()))
                    throw new InvalidDataException("A stored struct header has no struct type.");
                CheckHeader(header, type.GetString()!);
            }

            foreach (var child in value.EnumerateObject())
                if (child.Name is not ("OriginalUnversionedHeader" or "OriginalStructHeader"))
                    CheckNestedHeaders(child.Value);
        }
    }

    private void CheckHeader(JsonElement header, string schema, string? module = null)
    {
        if (!header.TryGetProperty("Fragments", out var fragments) || fragments.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{schema} has no stored header fragments.");

        int maskLength = header.TryGetProperty("ZeroMask", out var mask) && mask.ValueKind == JsonValueKind.Array
            ? mask.GetArrayLength()
            : 0;
        int next = 0;
        int usedMaskBits = 0;
        int previous = -1;
        int[]? map = null;
        foreach (var fragment in fragments.EnumerateArray())
        {
            int skip = fragment.GetProperty("SkipNum").GetInt32();
            int values = fragment.GetProperty("ValueNum").GetInt32();
            if (skip < 0 || skip > MaxFragmentCount || values < 0 || values > MaxFragmentCount)
                throw new InvalidDataException($"{schema} has an invalid stored header fragment.");
            next += skip;
            if (fragment.GetProperty("bHasAnyZeroes").GetBoolean())
            {
                usedMaskBits += values;
                if (usedMaskBits > maskLength)
                    throw new InvalidDataException($"{schema} has a short stored zero mask.");
            }

            for (int i = 0; i < values; i++, next++)
            {
                map ??= GetSlotMap(schema, module);
                if (next >= map.Length || map[next] < 0)
                    throw new InvalidDataException($"Property {next} of {schema} has no compatible target slot.");
                if (map[next] <= previous)
                    throw new InvalidDataException($"The stored properties of {schema} changed order.");
                previous = map[next];
            }
        }
    }

    private int[] GetSlotMap(string schema, string? module)
    {
        if (_slotMaps.TryGetValue((schema, module), out var cached))
            return cached;

        var from = GetSlots(source, schema, module);
        var to = GetSlots(target, schema, module);
        var positions = new Dictionary<(string, ushort), int>();
        for (int i = 0; i < to.Count; i++)
        {
            if (to[i] is not { } property)
                continue;
            var key = (property.Name, property.ArrayIndex);
            if (!positions.TryAdd(key, i))
                positions[key] = -1; // More than one field can have the same name in a hierarchy.
        }

        var map = new int[from.Count];
        Array.Fill(map, -1);
        for (int i = 0; i < from.Count; i++)
        {
            if (from[i] is { } property && positions.TryGetValue((property.Name, property.ArrayIndex), out int index) &&
                index >= 0 && SameType(property.PropertyData, to[index]!.PropertyData))
                map[i] = index;
        }
        _slotMaps.Add((schema, module), map);
        return map;
    }

    private static List<UsmapProperty?> GetSlots(Usmap mappings, string name, string? module)
    {
        UsmapSchema schema;
        if (module != null && mappings.Schemas.TryGetValue($"{module}.{name}", out var match))
        {
            schema = match;
        }
        else
        {
            if (!mappings.Schemas.TryGetValue(name, out var fallback))
                throw new InvalidDataException($"The mappings lack schema {name}.");
            schema = fallback;
            if (module == null && mappings.Schemas.Values.Any(other =>
                !ReferenceEquals(other, fallback) && string.Equals(other.Name, fallback.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"The mappings contain more than one definition of {name}.");
        }

        var slots = new List<UsmapProperty?>();
        var seen = new HashSet<UsmapSchema>();
        while (true)
        {
            if (!seen.Add(schema))
                throw new InvalidDataException($"The inheritance chain of {name} does not end.");

            // Follow the order used by MainSerializer.Read: the derived
            // schema's slots precede the slots of its superclass.
            int offset = slots.Count;
            slots.AddRange(new UsmapProperty?[schema.PropCount]);
            foreach (var (index, property) in schema.Properties)
            {
                if ((uint)index >= schema.PropCount)
                    throw new InvalidDataException($"Schema {schema.Name} has a property outside its declared slots.");
                slots[offset + index] = property;
            }

            string? super = schema.SuperType;
            if (string.IsNullOrEmpty(super))
                return slots;
            if (schema.SuperTypeModulePath != null &&
                mappings.Schemas.TryGetValue($"{schema.SuperTypeModulePath}.{super}", out var qualified))
                schema = qualified;
            else if (schema.Name != super && mappings.Schemas.TryGetValue(super, out var parent))
                schema = parent;
            else
                throw new InvalidDataException($"The mappings lack superclass {super} of {schema.Name}.");
        }
    }

    private static bool SameType(UsmapPropertyData? from, UsmapPropertyData? to)
    {
        if (from == null || to == null || from.Type != to.Type || from.Type == EPropertyType.Unknown)
            return false;

        return (from, to) switch
        {
            (UsmapStructData a, UsmapStructData b) => a.StructType == b.StructType,
            (UsmapEnumData a, UsmapEnumData b) => a.Name == b.Name && SameType(a.InnerType, b.InnerType),
            (UsmapMapData a, UsmapMapData b) => SameType(a.InnerType, b.InnerType) && SameType(a.ValueType, b.ValueType),
            (UsmapArrayData a, UsmapArrayData b) => SameType(a.InnerType, b.InnerType),
            _ => from.GetType() == typeof(UsmapPropertyData) && to.GetType() == typeof(UsmapPropertyData)
        };
    }
}
