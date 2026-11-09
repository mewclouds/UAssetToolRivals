using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.Unversioned;
using UAssetAPI.UnrealTypes;
using UAssetTool;

internal static class AssetLayoutReadTests
{
    internal static void ReadBatch()
    {
        using var fixture = new Fixture(false, false);
        string map = WriteMappings(fixture.Root);
        string asset = Path.Combine(fixture.Root, "Layout.uasset");
        var fixtureAsset = new UAsset(EngineVersion.VER_UE5_3)
        {
            FilePath = asset,
            FolderName = FString.FromString("None"),
            PackageFlags = EPackageFlags.PKG_Cooked | EPackageFlags.PKG_FilterEditorOnly | EPackageFlags.PKG_UnversionedProperties,
            Imports = [],
            Exports = []
        };
        fixtureAsset.ClearNameIndexList();
        fixtureAsset.InitializeForWriting();
        fixtureAsset.Write(asset);
        byte[] original = File.ReadAllBytes(asset);

        var response = UAssetTool.Program.ProcessRequest(new UAssetRequest
        {
            Action = "read_asset_layouts",
            LayoutReads = [
                new(asset, map),
                new(asset, Path.Combine(fixture.Root, "missing.usmap")),
                new(Path.Combine(fixture.Root, "missing.uasset"), map),
                new(asset, Path.Combine(fixture.Root, ".", "fixture.usmap"))
            ]
        });

        Require(response.Success, response.Message);
        var data = JsonSerializer.SerializeToElement(response.Data);
        Require(data.GetProperty("layouts_loaded").GetInt32() == 1, "Equivalent paths did not share the parsed mappings.");
        var reads = data.GetProperty("reads");
        Require(reads.GetArrayLength() == 4, "The batch lost a read.");
        Require(reads[0].GetProperty("error").ValueKind == JsonValueKind.Null,
            reads[0].GetProperty("error").GetString() ?? "The first read failed.");
        Require(reads[0].GetProperty("has_unversioned_properties").GetBoolean(), "The asset's package flag changed.");
        Require(reads[0].GetProperty("clean").GetBoolean(), "The first asset failed to decode.");
        Require(reads[3].GetProperty("clean").GetBoolean(), "A failed read affected the next one.");
        string? firstHash = reads[0].GetProperty("values_sha256").GetString();
        Require(firstHash?.Length == 64 && firstHash == reads[3].GetProperty("values_sha256").GetString(),
            "Repeated reads produced different value hashes.");
        foreach (int i in new[] { 1, 2 })
        {
            Require(reads[i].GetProperty("error").ValueKind == JsonValueKind.String, "A failed read had no error.");
            Require(reads[i].GetProperty("clean").ValueKind == JsonValueKind.Null, "A failed read returned a verdict.");
        }
        Require(File.ReadAllBytes(asset).AsSpan().SequenceEqual(original), "The read changed the asset.");
    }

    internal static void IsolateDiscoveredMappings()
    {
        using var fixture = new Fixture(false, false);
        var parsed = new Usmap(WriteMappings(fixture.Root));
        parsed.CustomVersionContainer = [new CustomVersion(Guid.Empty, 1)];
        var a = parsed.ForkForAssetRead();
        var b = parsed.ForkForAssetRead();
        var gameSchema = parsed.Schemas["Fixture"];
        Require(ReferenceEquals(b.Schemas["fixture"], gameSchema), "The parsed schema or its comparer was lost.");

        a.Schemas["fixture"] = new UsmapSchema();
        a.EnumMap["Discovered"] = new UsmapEnum();
        a.PathsAlreadyProcessedForSchemas["/Game/Dependency"] = 1;
        a.CustomVersionContainer[0].SetIsSerialized(false);

        Require(ReferenceEquals(parsed.Schemas["Fixture"], gameSchema) && ReferenceEquals(b.Schemas["Fixture"], gameSchema),
            "A discovered schema replaced the parsed definition in another read.");
        Require(!parsed.EnumMap.ContainsKey("Discovered") && !b.EnumMap.ContainsKey("Discovered"),
            "A discovered enum leaked into another read.");
        Require(parsed.PathsAlreadyProcessedForSchemas.IsEmpty && b.PathsAlreadyProcessedForSchemas.IsEmpty,
            "A dependency visit leaked into another read.");
        Require(parsed.CustomVersionContainer[0].IsSerialized && b.CustomVersionContainer[0].IsSerialized,
            "A custom version change leaked into another read.");
    }

    private static string WriteMappings(string root)
    {
        using var payload = new MemoryStream();
        using (var data = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            data.Write(1); // Names
            data.Write((byte)7);
            data.Write(Encoding.UTF8.GetBytes("Fixture"));
            data.Write(0); // Enums
            data.Write(1); // Schemas
            data.Write(0); // Name index
            data.Write(-1); // No superclass
            data.Write((ushort)0); // Properties
            data.Write((ushort)0); // Serialized properties
        }

        string path = Path.Combine(root, "fixture.usmap");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Usmap.USMAP_MAGIC);
        writer.Write((byte)UsmapVersion.Initial);
        writer.Write((byte)0); // Uncompressed
        writer.Write((uint)payload.Length);
        writer.Write((uint)payload.Length);
        writer.Write(payload.ToArray());
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
