using System.Collections;
using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetTool;

internal static class AssetLayoutProofTests
{
    internal static void VerifyRoundTripAndPortability()
    {
        using var fixture = new Fixture(false, false);
        var first = ("First", EPropertyType.IntProperty);
        var second = ("Second", EPropertyType.IntProperty);
        string source = WriteMappings(fixture.Root, "source", [first, second], hasInheritedField: true);
        string inserted = WriteMappings(fixture.Root, "inserted", [("NewField", EPropertyType.IntProperty), first, second]);
        string removed = WriteMappings(fixture.Root, "removed", [first]);
        string retyped = WriteMappings(fixture.Root, "retyped", [first, ("Second", EPropertyType.FloatProperty)]);
        string reordered = WriteMappings(fixture.Root, "reordered", [second, first]);
        string asset = WriteAsset(fixture.Root, source);
        string exports = Path.ChangeExtension(asset, ".uexp");
        byte[] originalAsset = File.ReadAllBytes(asset);
        byte[] originalExports = File.ReadAllBytes(exports);

        var response = UAssetTool.Program.ProcessRequest(new UAssetRequest
        {
            Action = "read_asset_layouts",
            LayoutReads = [
                new(asset, source, VerifyExports: true),
                new(asset, source, TargetUsmapPath: inserted),
                new(asset, source, TargetUsmapPath: removed),
                new(asset, source, TargetUsmapPath: retyped),
                new(asset, source, TargetUsmapPath: reordered)
            ]
        });

        Require(response.Success, response.Message);
        var reads = JsonSerializer.SerializeToElement(response.Data).GetProperty("reads");
        Require(reads.GetArrayLength() == 5, "A proof is missing.");
        foreach (var proof in reads.EnumerateArray())
        {
            var error = proof.GetProperty("error");
            Require(error.ValueKind == JsonValueKind.Null, error.GetString() ?? "The proof failed.");
            Require(proof.GetProperty("writes_original").GetBoolean(), "The proof failed to reproduce the export bytes.");
        }
        Require(reads[0].GetProperty("portable").ValueKind == JsonValueKind.Null, "A round trip alone returned a remap verdict.");
        Require(reads[1].GetProperty("portable").GetBoolean(), "An insertion blocked the stored derived fields.");
        foreach (int i in new[] { 2, 3, 4 })
        {
            Require(!reads[i].GetProperty("portable").GetBoolean(), "A removed, retyped, or reordered field was accepted.");
            Require(reads[i].GetProperty("portability_reason").ValueKind == JsonValueKind.String, "A blocked proof has no reason.");
        }
        Require(File.ReadAllBytes(asset).AsSpan().SequenceEqual(originalAsset), "The proof changed the source asset.");
        Require(File.ReadAllBytes(exports).AsSpan().SequenceEqual(originalExports), "The proof changed the source exports.");
    }

    private static string WriteAsset(string root, string mappings)
    {
        string path = Path.Combine(root, "Proof.uasset");
        var asset = new UAsset(EngineVersion.VER_UE5_3, new Usmap(mappings))
        {
            FilePath = path,
            FolderName = FString.FromString("None"),
            PackageFlags = EPackageFlags.PKG_Cooked | EPackageFlags.PKG_FilterEditorOnly | EPackageFlags.PKG_UnversionedProperties,
            UseSeparateBulkDataFiles = true,
            Imports = [],
            Exports = []
        };
        asset.ClearNameIndexList();
        asset.Imports.Add(new Import("/Script/CoreUObject", "Class", new FPackageIndex(), "Derived", false, asset));
        asset.Exports.Add(new NormalExport(asset, [])
        {
            ClassIndex = new FPackageIndex(-1),
            ObjectName = new FName(asset, "Proof"),
            Data = [
                new IntPropertyData(new FName(asset, "First")) { Value = 17 },
                new IntPropertyData(new FName(asset, "Second")) { Value = 32 }
            ],
            OriginalUnversionedHeader = new FUnversionedHeader
            {
                Fragments = new LinkedList<FFragment>([new FFragment(0, 2, true, false, 0)]),
                ZeroMask = new BitArray(0),
                bHasNonZeroValues = true
            }
        });
        asset.InitializeForWriting();
        asset.Write(path);
        return path;
    }

    private static string WriteMappings(string root, string label, (string Name, EPropertyType Type)[] fields, bool hasInheritedField = false)
    {
        string[] names = ["Base", "Derived", "Inherited", .. fields.Select(field => field.Name)];
        using var payload = new MemoryStream();
        using (var data = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            data.Write(names.Length);
            foreach (string name in names)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(name);
                data.Write((byte)bytes.Length);
                data.Write(bytes);
            }
            data.Write(0); // Enums
            data.Write(2); // Schemas
            data.Write(0); // Base
            data.Write(-1); // No superclass
            data.Write((ushort)(hasInheritedField ? 1 : 0));
            data.Write((ushort)(hasInheritedField ? 1 : 0));
            if (hasInheritedField)
                WriteField(data, 0, 2, EPropertyType.IntProperty);

            data.Write(1); // Derived
            data.Write(0); // Base
            data.Write((ushort)fields.Length);
            data.Write((ushort)fields.Length);
            for (int i = 0; i < fields.Length; i++)
                WriteField(data, i, i + 3, fields[i].Type);
        }

        string path = Path.Combine(root, $"{label}.usmap");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Usmap.USMAP_MAGIC);
        writer.Write((byte)UsmapVersion.Initial);
        writer.Write((byte)0); // Uncompressed
        writer.Write((uint)payload.Length);
        writer.Write((uint)payload.Length);
        writer.Write(payload.ToArray());
        return path;
    }

    private static void WriteField(BinaryWriter writer, int index, int name, EPropertyType type)
    {
        writer.Write((ushort)index);
        writer.Write((byte)1); // Array size
        writer.Write(name);
        writer.Write((byte)type);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
