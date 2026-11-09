using System.Text.Json.Serialization;
using UAssetTool.ZenPackage;
using EPackageFlags = UAssetAPI.UnrealTypes.EPackageFlags;
using ZenHeaderVersion = UAssetTool.ZenPackage.EIoContainerHeaderVersion;

namespace UAssetTool.IoStore;

internal sealed record IoStoreBundleScan(
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("packages")] IReadOnlyList<IoStorePackageScan> Packages,
    [property: JsonPropertyName("error")] string? Error = null);

internal sealed record IoStorePackageScan(
    [property: JsonPropertyName("chunk_id")] string ChunkId,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("has_unversioned_properties")] bool? HasUnversionedProperties,
    [property: JsonPropertyName("error")] string? Error = null);

internal static class IoStorePackageScanner
{
    // The TOC and decompression buffers belong to each scan. Limit open
    // containers even when the machine has many logical processors.
    private const int MaxConcurrentBundles = 4;
    private const int ZenSummarySize = 52; // NoExportInfo summary, before optional versioning info.

    internal static IoStoreBundleScan[] Scan(IReadOnlyList<string> paths, byte[] aesKey)
    {
        var results = new IoStoreBundleScan[paths.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, MaxConcurrentBundles)
        };
        Parallel.For(0, paths.Count, options, i => results[i] = ScanBundle(paths[i], aesKey));
        return results;
    }

    private static IoStoreBundleScan ScanBundle(string? path, byte[] aesKey)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new IoStoreBundleScan(path, [], "UTOC path is required.");

        try
        {
            using var reader = new IoStoreReader(path, aesKey);
            var toc = reader.Toc;
            if (toc.Version != EIoStoreTocVersion.PerfectHashWithOverflow || toc.PartitionCount != 1)
                throw new NotSupportedException("Package scanning requires a version 5 TOC with one UCAS partition.");

            var packages = new List<IoStorePackageScan>();
            for (int i = 0; i < toc.Chunks.Count; i++)
            {
                var chunk = toc.Chunks[i];
                if (chunk.ChunkType != EIoChunkType.ExportBundleData)
                    continue;

                string chunkId = Convert.ToHexString(chunk.ToBytes());
                string? packagePath = reader.GetChunkPath(chunk);
                try
                {
                    packages.Add(new IoStorePackageScan(chunkId, packagePath, ReadUnversionedFlag(reader, i)));
                }
                catch (Exception ex)
                {
                    packages.Add(new IoStorePackageScan(chunkId, packagePath, null, ex.Message));
                }
            }
            return new IoStoreBundleScan(path, packages);
        }
        catch (Exception ex)
        {
            return new IoStoreBundleScan(path, [], ex.Message);
        }
    }

    private static bool ReadUnversionedFlag(IoStoreReader reader, int chunkIndex)
    {
        var toc = reader.Toc;
        var location = toc.ChunkOffsetLengths[chunkIndex];
        if (location.Length < ZenSummarySize)
            throw new InvalidDataException("Package is shorter than its Zen summary.");

        uint blockSize = toc.CompressionBlockSize;
        if (toc.CompressionBlocks.Count > 0 &&
            (blockSize == 0 || location.Offset % blockSize != 0 ||
             (location.Offset + ZenSummarySize - 1) / blockSize >= (ulong)toc.CompressionBlocks.Count))
            throw new InvalidDataException("Package summary is outside the supported block layout.");

        using var stream = new MemoryStream(reader.ReadChunkByIndex((uint)chunkIndex, ZenSummarySize));
        using var binary = new BinaryReader(stream);
        var summary = new FZenPackageSummary();
        summary.Read(binary, ZenHeaderVersion.NoExportInfo);

        // A bad header or a wrong AES key must produce an unknown flag.
        if (summary.HasVersioningInfo > 1 || summary.Name.Type > EMappedNameType.Global ||
            summary.HeaderSize > location.Length ||
            summary.ImportedPublicExportHashesOffset < ZenSummarySize ||
            summary.ImportMapOffset < summary.ImportedPublicExportHashesOffset ||
            summary.ExportMapOffset < summary.ImportMapOffset ||
            summary.ExportBundleEntriesOffset < summary.ExportMapOffset ||
            summary.DependencyBundleHeadersOffset < summary.ExportBundleEntriesOffset ||
            summary.DependencyBundleEntriesOffset < summary.DependencyBundleHeadersOffset ||
            summary.ImportedPackageNamesOffset < summary.DependencyBundleEntriesOffset ||
            summary.ImportedPackageNamesOffset > summary.HeaderSize)
            throw new InvalidDataException("Invalid Zen package summary.");

        return (summary.PackageFlags & (uint)EPackageFlags.PKG_UnversionedProperties) != 0;
    }
}
