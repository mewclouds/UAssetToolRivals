using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UAssetTool.IoStore;

/// <summary>
/// Removes AES encryption without changing compressed bytes, chunk IDs, or cooked assets.
/// Supports unsigned TOC versions 1 through 5 with one UCAS partition.
/// </summary>
public static class IoStoreDecryptor
{
    private const int HeaderSize = 144;
    private const int VersionOffset = 16;
    private const int HeaderSizeOffset = 20;
    private const int ChunkCountOffset = 24;
    private const int BlockCountOffset = 28;
    private const int BlockEntrySizeOffset = 32;
    private const int MethodCountOffset = 36;
    private const int MethodLengthOffset = 40;
    private const int DirectorySizeOffset = 48;
    private const int PartitionCountOffset = 52;
    private const int FlagsOffset = 80;
    private const int SeedCountOffset = 84;
    private const int OverflowCountOffset = 96;
    private const int ChunkIdSize = 12;
    private const int ChunkOffsetLengthSize = 10;
    private const int BlockEntrySize = 12;
    private const int BlockPositionSize = 5;
    private const int CompressedSizeOffset = 5;
    private const int UncompressedSizeOffset = 8;
    private const int MethodOffset = 11;
    private const int IntegerSize = sizeof(uint);
    private const int DirectoryEntrySize = 16;
    private const int FileEntrySize = 12;
    private const int AesKeySize = 32;
    private const int AesBlockSize = 16;
    private const int BitsPerByte = 8;

    /// <summary>
    /// Returns false for an already clear container. Failed validation leaves both source files intact.
    /// The PAK companion does not change. Replacement failures restore the UCAS backup.
    /// </summary>
    public static bool Decrypt(string utocPath, string? aesKeyHex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(utocPath);
        utocPath = Path.GetFullPath(utocPath);
        byte[] toc = File.ReadAllBytes(utocPath);
        ValidateHeader(toc);
        if (((EIoContainerFlags)toc[FlagsOffset] & EIoContainerFlags.Encrypted) == 0)
            return false;

        string keyHex = string.IsNullOrEmpty(aesKeyHex) ? IoStoreReader.DEFAULT_AES_KEY_HEX : aesKeyHex;
        if (keyHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            keyHex = keyHex[2..];
        byte[] key = Convert.FromHexString(keyHex);
        if (key.Length != AesKeySize)
            throw new ArgumentException("IoStore decryption requires a 32-byte AES key.");

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();

        string ucasPath = Path.ChangeExtension(utocPath, ".ucas");
        string tempRoot = Path.Combine(Path.GetDirectoryName(utocPath)!, ".uat-decrypt-" + Guid.NewGuid().ToString("N"));
        string stagedUcas = Path.Combine(tempRoot, "decrypted.ucas");
        string stagedUtoc = Path.Combine(tempRoot, "decrypted.utoc");
        bool preserveBackup = false;
        try
        {
            using (var source = new FileStream(ucasPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var layout = ReadLayout(toc, source.Length);
                if (layout.DirectorySize != 0 && !ValidDirectory(toc.AsSpan(layout.DirectoryOffset, layout.DirectorySize)))
                {
                    if (layout.DirectorySize % AesBlockSize != 0)
                        throw new InvalidDataException("The encrypted directory index is not AES-aligned.");
                    decryptor.TransformBlock(toc, layout.DirectoryOffset, layout.DirectorySize, toc, layout.DirectoryOffset);
                    if (!ValidDirectory(toc.AsSpan(layout.DirectoryOffset, layout.DirectorySize)))
                        throw new InvalidDataException("The directory index cannot be decrypted with this key.");
                }

                Directory.CreateDirectory(tempRoot);
                File.Copy(ucasPath, stagedUcas);
                using var staged = new FileStream(stagedUcas, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                for (int offset = layout.BlockOffset; offset < layout.BlockOffset + layout.BlockLength; offset += BlockEntrySize)
                {
                    var entry = toc.AsSpan(offset, BlockEntrySize);
                    long position = (long)ReadLittleEndian(entry[..BlockPositionSize]);
                    byte[] data = new byte[AlignedBlockSize(entry)];
                    staged.Position = position;
                    staged.ReadExactly(data);
                    decryptor.TransformBlock(data, 0, data.Length, data, 0);
                    staged.Position = position;
                    staged.Write(data);
                }
                staged.Flush(true);
            }

            toc[FlagsOffset] &= unchecked((byte)~(byte)EIoContainerFlags.Encrypted);
            File.WriteAllBytes(stagedUtoc, toc);
            string backupUcas = Path.Combine(tempRoot, "original.ucas");
            File.Move(ucasPath, backupUcas);
            try
            {
                File.Move(stagedUcas, ucasPath);
                File.Move(stagedUtoc, utocPath, overwrite: true);
            }
            catch (Exception replacementError)
            {
                try
                {
                    File.Move(backupUcas, ucasPath, overwrite: true);
                }
                catch (Exception rollbackError)
                {
                    // Preserve the backup when the filesystem also rejects rollback.
                    preserveBackup = true;
                    throw new IOException($"IoStore replacement and UCAS rollback failed. Backup: {backupUcas}",
                        new AggregateException(replacementError, rollbackError));
                }
                throw;
            }
            return true;
        }
        finally
        {
            if (!preserveBackup && Directory.Exists(tempRoot))
                RemoveTemporaryDirectory(tempRoot);
        }
    }

    private static void RemoveTemporaryDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove the temporary decrypt directory: {error.Message}");
        }
    }

    private static void ValidateHeader(ReadOnlySpan<byte> toc)
    {
        if (toc.Length < HeaderSize || !toc[..FIoStoreTocHeader.TOC_MAGIC.Length].SequenceEqual(FIoStoreTocHeader.TOC_MAGIC))
            throw new InvalidDataException("Invalid IoStore TOC header.");
        var version = (EIoStoreTocVersion)toc[VersionOffset];
        if (version < EIoStoreTocVersion.Initial || version > EIoStoreTocVersion.PerfectHashWithOverflow ||
            ReadUInt32(toc, HeaderSizeOffset) != HeaderSize)
            throw new NotSupportedException("Unsupported IoStore TOC version or header size.");
        if (((EIoContainerFlags)toc[FlagsOffset] & EIoContainerFlags.Signed) != 0 ||
            ReadUInt32(toc, PartitionCountOffset) != 1)
            throw new NotSupportedException("Direct decryption does not support signed or multi-partition containers.");
    }

    private static (int BlockOffset, int BlockLength, int DirectoryOffset, int DirectorySize) ReadLayout(byte[] toc, long ucasSize)
    {
        if (ReadUInt32(toc, BlockEntrySizeOffset) != BlockEntrySize)
            throw new NotSupportedException("Unsupported compression block entry size.");
        ulong chunkCount = ReadUInt32(toc, ChunkCountOffset);
        ulong blockCount = ReadUInt32(toc, BlockCountOffset);
        ulong methodCount = ReadUInt32(toc, MethodCountOffset);
        ulong methodLength = ReadUInt32(toc, MethodLengthOffset);
        ulong directorySize = ReadUInt32(toc, DirectorySizeOffset);
        ulong blockOffset = HeaderSize + chunkCount * (ChunkIdSize + ChunkOffsetLengthSize);
        var version = (EIoStoreTocVersion)toc[VersionOffset];
        if (version >= EIoStoreTocVersion.PerfectHash)
            blockOffset += (ulong)ReadUInt32(toc, SeedCountOffset) * IntegerSize;
        if (version >= EIoStoreTocVersion.PerfectHashWithOverflow)
            blockOffset += (ulong)ReadUInt32(toc, OverflowCountOffset) * IntegerSize;
        ulong blockEnd = blockOffset + blockCount * BlockEntrySize;
        if ((blockCount == 0 && chunkCount != 0) || blockEnd > (ulong)toc.Length ||
            methodCount * methodLength > (ulong)toc.Length - blockEnd)
            throw new InvalidDataException("Invalid IoStore compression table.");
        ulong directoryOffset = blockEnd + methodCount * methodLength;
        if (directorySize > (ulong)toc.Length - directoryOffset)
            throw new InvalidDataException("Truncated IoStore directory index.");

        long previousEnd = 0;
        for (int offset = (int)blockOffset; offset < (int)blockEnd; offset += BlockEntrySize)
        {
            var entry = toc.AsSpan(offset, BlockEntrySize);
            long position = (long)ReadLittleEndian(entry[..BlockPositionSize]);
            int size = AlignedBlockSize(entry);
            if (size == 0 || position < previousEnd || position + size > ucasSize || entry[MethodOffset] > methodCount)
                throw new InvalidDataException("Invalid or truncated IoStore compression block.");
            previousEnd = position + size;
        }
        return ((int)blockOffset, (int)(blockEnd - blockOffset), (int)directoryOffset, (int)directorySize);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static ulong ReadLittleEndian(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (int index = 0; index < data.Length; index++)
            value |= (ulong)data[index] << (BitsPerByte * index);
        return value;
    }

    private static int AlignedBlockSize(ReadOnlySpan<byte> entry)
    {
        int sizeOffset = entry[MethodOffset] == 0 ? UncompressedSizeOffset : CompressedSizeOffset;
        int size = (int)ReadLittleEndian(entry.Slice(sizeOffset, UncompressedSizeOffset - CompressedSizeOffset));
        return (size + AesBlockSize - 1) & ~(AesBlockSize - 1);
    }

    private static bool ValidDirectory(ReadOnlySpan<byte> data)
    {
        if (!ReadString(ref data, out string mount) || (!mount.StartsWith("../") && !mount.StartsWith('/')))
            return false;
        foreach (int entrySize in new[] { DirectoryEntrySize, FileEntrySize })
        {
            if (data.Length < IntegerSize)
                return false;
            ulong size = (ulong)ReadUInt32(data, 0) * (uint)entrySize;
            if (size > (ulong)(data.Length - IntegerSize))
                return false;
            data = data[(IntegerSize + (int)size)..];
        }
        if (data.Length < IntegerSize)
            return false;
        uint count = ReadUInt32(data, 0);
        data = data[IntegerSize..];
        if ((ulong)count * IntegerSize > (ulong)data.Length)
            return false;
        for (uint index = 0; index < count; index++)
            if (!ReadString(ref data, out _))
                return false;
        return data.IndexOfAnyExcept((byte)0) < 0;
    }

    private static bool ReadString(ref ReadOnlySpan<byte> data, out string value)
    {
        value = "";
        if (data.Length < IntegerSize)
            return false;
        long length = BinaryPrimitives.ReadInt32LittleEndian(data);
        data = data[IntegerSize..];
        if (length == 0)
            return true;
        long byteCount = length > 0 ? length : -length * sizeof(char);
        int terminatorSize = length > 0 ? sizeof(byte) : sizeof(char);
        if (byteCount > data.Length || data.Slice((int)byteCount - terminatorSize, terminatorSize).IndexOfAnyExcept((byte)0) >= 0)
            return false;
        try
        {
            Encoding encoding = length > 0 ? new UTF8Encoding(false, true) : new UnicodeEncoding(false, false, true);
            value = encoding.GetString(data[..((int)byteCount - terminatorSize)]);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        data = data[(int)byteCount..];
        return true;
    }
}
