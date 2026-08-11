using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using SimpleITKUnity;
using UnityEngine;

/// <summary>
/// Encodes normalized voxel arrays into a compact network payload.
/// </summary>
public static class VolumePayloadBuilder
{
    public sealed class BuiltPayload
    {
        public byte[] CompressedBytes;
        public uint UncompressedBytes;
        public uint CompressedBytesLength;
        public uint PayloadCrc32;
        public ushort TotalChunks;
    }

    public static Task<BuiltPayload> BuildAsync(
        SimpleITKVolumeData volumeData,
        int chunkPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Build(volumeData, chunkPayloadBytes, cancellationToken), cancellationToken);
    }

    public static Task<BuiltPayload> BuildLabelAsync(
        SimpleITKLabelVolumeData labelData,
        int chunkPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => BuildLabel(labelData, chunkPayloadBytes, cancellationToken), cancellationToken);
    }

    public static BuiltPayload Build(
        SimpleITKVolumeData volumeData,
        int chunkPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = volumeData.VoxelsNormalized;
        if (normalized == null || normalized.Length == 0)
        {
            throw new InvalidOperationException("Volume voxel buffer is empty.");
        }

        var uncompressed = EncodeR16LittleEndian(normalized);
        cancellationToken.ThrowIfCancellationRequested();

        return BuildCompressedPayload(uncompressed, chunkPayloadBytes, cancellationToken);
    }

    public static BuiltPayload BuildLabel(
        SimpleITKLabelVolumeData labelData,
        int chunkPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var labels = labelData.Voxels;
        if (labels == null || labels.Length == 0)
        {
            throw new InvalidOperationException("Label voxel buffer is empty.");
        }

        var expectedVoxels = labelData.Width * labelData.Height * labelData.Depth;
        if (expectedVoxels <= 0 || labels.Length != expectedVoxels)
        {
            throw new InvalidOperationException(
                $"Label voxel count mismatch. Expected {expectedVoxels}, received {labels.Length}.");
        }

        return BuildCompressedPayload(labels, chunkPayloadBytes, cancellationToken);
    }

    private static BuiltPayload BuildCompressedPayload(
        byte[] uncompressed,
        int chunkPayloadBytes,
        CancellationToken cancellationToken)
    {
        var compressed = DeflateCompress(uncompressed);
        cancellationToken.ThrowIfCancellationRequested();

        var chunkSize = Mathf.Max(1024, chunkPayloadBytes);
        var totalChunks = (ushort)Mathf.Clamp(
            Mathf.CeilToInt(compressed.Length / (float)chunkSize),
            1,
            ushort.MaxValue);

        return new BuiltPayload
        {
            CompressedBytes = compressed,
            UncompressedBytes = (uint)uncompressed.Length,
            CompressedBytesLength = (uint)compressed.Length,
            PayloadCrc32 = Crc32.Compute(compressed),
            TotalChunks = totalChunks,
        };
    }

    public static byte[] EncodeR16LittleEndian(float[] normalizedVoxels)
    {
        var bytes = new byte[normalizedVoxels.Length * sizeof(ushort)];
        for (var i = 0; i < normalizedVoxels.Length; ++i)
        {
            var quantized = (ushort)Mathf.Clamp(Mathf.RoundToInt(normalizedVoxels[i] * 65535f), 0, 65535);
            var offset = i * sizeof(ushort);
            bytes[offset] = (byte)(quantized & 0xFF);
            bytes[offset + 1] = (byte)(quantized >> 8);
        }

        return bytes;
    }

    public static float[] DecodeR16LittleEndian(byte[] r16Bytes, int expectedVoxelCount)
    {
        if (r16Bytes == null)
        {
            throw new ArgumentNullException(nameof(r16Bytes));
        }

        var maxVoxels = r16Bytes.Length / sizeof(ushort);
        if (expectedVoxelCount > maxVoxels)
        {
            throw new InvalidOperationException(
                $"R16 payload is too small. Expected {expectedVoxelCount} voxels, buffer supports {maxVoxels}.");
        }

        var normalized = new float[expectedVoxelCount];
        for (var i = 0; i < expectedVoxelCount; ++i)
        {
            var offset = i * sizeof(ushort);
            var quantized = (ushort)(r16Bytes[offset] | (r16Bytes[offset + 1] << 8));
            normalized[i] = quantized / 65535f;
        }

        return normalized;
    }

    public static byte[] DeflateCompress(byte[] input)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    public static byte[] DeflateDecompress(byte[] input, int uncompressedLength)
    {
        using var inputStream = new MemoryStream(input, writable: false);
        using var deflate = new DeflateStream(inputStream, CompressionMode.Decompress);
        using var output = new MemoryStream(uncompressedLength);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    public static void CopyChunk(byte[] source, int sourceOffset, int length, byte[] destination, int destinationOffset)
    {
        Buffer.BlockCopy(source, sourceOffset, destination, destinationOffset, length);
    }
}

/// <summary>
/// Lightweight CRC32 used to validate the compressed payload on the client.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return 0u;
        }

        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < bytes.Length; ++i)
        {
            crc = (crc >> 8) ^ Table[(crc ^ bytes[i]) & 0xFF];
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        const uint polynomial = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < table.Length; ++i)
        {
            var value = i;
            for (var j = 0; j < 8; ++j)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ polynomial : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
