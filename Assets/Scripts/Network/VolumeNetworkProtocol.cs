using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// Chunked volume transfer messages between server and clients.
/// Payload voxels are normalized to [0, 1] on the server, quantized to R16, then Deflate-compressed.
/// </summary>
public static class VolumeNetworkProtocol
{
    public const string VolumeBeginMessageName = "VolumeBegin";
    public const string VolumeChunkMessageName = "VolumeChunk";
    public const string VolumeEndMessageName = "VolumeEnd";
    public const string VolumeAbortMessageName = "VolumeAbort";

    public const uint Magic = 0x31564F56; // "VOL1" little-endian
    public const ushort Version = 1;

    /// <summary>
    /// Volume chunks exceed the default reliable sequenced payload limit (~1.2 KB).
    /// </summary>
    public const NetworkDelivery VolumeMessageDelivery = NetworkDelivery.ReliableFragmentedSequenced;

    public enum VoxelFormat : byte
    {
        R16 = 1,
    }

    public enum CompressionKind : byte
    {
        None = 0,
        Deflate = 1,
    }

    public struct VolumeHeader
    {
        public uint TransferId;
        public int SampleIndex;
        public int Width;
        public int Height;
        public int Depth;
        public VoxelFormat VoxelFormat;
        public CompressionKind Compression;
        public float SourceMinimum;
        public float SourceMaximum;
        public float SpacingX;
        public float SpacingY;
        public float SpacingZ;
        public float OriginX;
        public float OriginY;
        public float OriginZ;
        public float Direction00;
        public float Direction01;
        public float Direction02;
        public float Direction10;
        public float Direction11;
        public float Direction12;
        public float Direction20;
        public float Direction21;
        public float Direction22;
        public uint UncompressedPayloadBytes;
        public uint CompressedPayloadBytes;
        public ushort TotalChunks;
        public uint PayloadCrc32;

        public static VolumeHeader FromImageInfo(
            uint transferId,
            int sampleIndex,
            int width,
            int height,
            int depth,
            SimpleITKUnity.SimpleITKImageInfo imageInfo,
            float sourceMinimum,
            float sourceMaximum,
            VoxelFormat voxelFormat,
            CompressionKind compression,
            uint uncompressedPayloadBytes,
            uint compressedPayloadBytes,
            ushort totalChunks,
            uint payloadCrc32)
        {
            var spacing = imageInfo.Spacing ?? Array.Empty<double>();
            var origin = imageInfo.Origin ?? Array.Empty<double>();
            var direction = imageInfo.Direction ?? Array.Empty<double>();

            return new VolumeHeader
            {
                TransferId = transferId,
                SampleIndex = sampleIndex,
                Width = width,
                Height = height,
                Depth = depth,
                VoxelFormat = voxelFormat,
                Compression = compression,
                SourceMinimum = sourceMinimum,
                SourceMaximum = sourceMaximum,
                SpacingX = spacing.Length > 0 ? (float)spacing[0] : 1f,
                SpacingY = spacing.Length > 1 ? (float)spacing[1] : 1f,
                SpacingZ = spacing.Length > 2 ? (float)spacing[2] : 1f,
                OriginX = origin.Length > 0 ? (float)origin[0] : 0f,
                OriginY = origin.Length > 1 ? (float)origin[1] : 0f,
                OriginZ = origin.Length > 2 ? (float)origin[2] : 0f,
                Direction00 = direction.Length > 0 ? (float)direction[0] : 1f,
                Direction01 = direction.Length > 1 ? (float)direction[1] : 0f,
                Direction02 = direction.Length > 2 ? (float)direction[2] : 0f,
                Direction10 = direction.Length > 3 ? (float)direction[3] : 0f,
                Direction11 = direction.Length > 4 ? (float)direction[4] : 1f,
                Direction12 = direction.Length > 5 ? (float)direction[5] : 0f,
                Direction20 = direction.Length > 6 ? (float)direction[6] : 0f,
                Direction21 = direction.Length > 7 ? (float)direction[7] : 0f,
                Direction22 = direction.Length > 8 ? (float)direction[8] : 1f,
                UncompressedPayloadBytes = uncompressedPayloadBytes,
                CompressedPayloadBytes = compressedPayloadBytes,
                TotalChunks = totalChunks,
                PayloadCrc32 = payloadCrc32,
            };
        }
    }

    public static void WriteHeader(FastBufferWriter writer, in VolumeHeader header)
    {
        writer.WriteValueSafe(Magic);
        writer.WriteValueSafe(Version);
        writer.WriteValueSafe(header.TransferId);
        writer.WriteValueSafe(header.SampleIndex);
        writer.WriteValueSafe(header.Width);
        writer.WriteValueSafe(header.Height);
        writer.WriteValueSafe(header.Depth);
        writer.WriteValueSafe((byte)header.VoxelFormat);
        writer.WriteValueSafe((byte)header.Compression);
        writer.WriteValueSafe(header.SourceMinimum);
        writer.WriteValueSafe(header.SourceMaximum);
        writer.WriteValueSafe(header.SpacingX);
        writer.WriteValueSafe(header.SpacingY);
        writer.WriteValueSafe(header.SpacingZ);
        writer.WriteValueSafe(header.OriginX);
        writer.WriteValueSafe(header.OriginY);
        writer.WriteValueSafe(header.OriginZ);
        writer.WriteValueSafe(header.Direction00);
        writer.WriteValueSafe(header.Direction01);
        writer.WriteValueSafe(header.Direction02);
        writer.WriteValueSafe(header.Direction10);
        writer.WriteValueSafe(header.Direction11);
        writer.WriteValueSafe(header.Direction12);
        writer.WriteValueSafe(header.Direction20);
        writer.WriteValueSafe(header.Direction21);
        writer.WriteValueSafe(header.Direction22);
        writer.WriteValueSafe(header.UncompressedPayloadBytes);
        writer.WriteValueSafe(header.CompressedPayloadBytes);
        writer.WriteValueSafe(header.TotalChunks);
        writer.WriteValueSafe(header.PayloadCrc32);
    }

    public static void WriteChunk(
        FastBufferWriter writer,
        uint transferId,
        ushort chunkIndex,
        ushort totalChunks,
        byte[] chunkBytes)
    {
        writer.WriteValueSafe(transferId);
        writer.WriteValueSafe(chunkIndex);
        writer.WriteValueSafe(totalChunks);
        WriteByteArray(writer, chunkBytes);
    }

    public static void WriteTransferId(FastBufferWriter writer, uint transferId)
    {
        writer.WriteValueSafe(transferId);
    }

    public static void WriteByteArray(FastBufferWriter writer, byte[] bytes)
    {
        var length = bytes?.Length ?? 0;
        writer.WriteValueSafe(length);
        if (length > 0)
        {
            writer.WriteBytesSafe(bytes, length);
        }
    }

    public static int EstimateBeginMessageSize()
    {
        return sizeof(uint) * 6 + sizeof(int) * 5 + sizeof(ushort) * 2 + sizeof(float) * 17 + sizeof(byte) * 2;
    }

    public static int EstimateChunkMessageSize(int chunkPayloadBytes)
    {
        return sizeof(uint) + sizeof(ushort) * 2 + sizeof(int) + chunkPayloadBytes;
    }
}
