using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// Chunked volume transfer messages between server and clients.
/// Payload voxels are normalized to [0, 1] on the server, quantized to R16, then Deflate-compressed.
/// Optional HD-BET (or other) label overlays follow as a second U8 Deflate stream.
/// </summary>
public static class VolumeNetworkProtocol
{
    public const string VolumeBeginMessageName = "VolumeBegin";
    public const string VolumeChunkMessageName = "VolumeChunk";
    public const string VolumeEndMessageName = "VolumeEnd";
    public const string VolumeAbortMessageName = "VolumeAbort";

    public const string SegmentationBeginMessageName = "VolumeSegBegin";
    public const string SegmentationChunkMessageName = "VolumeSegChunk";
    public const string SegmentationEndMessageName = "VolumeSegEnd";

    public const uint Magic = 0x31564F56; // "VOL1" little-endian
    public const uint SegmentationMagic = 0x31474553; // "SEG1" little-endian
    public const ushort Version = 1;
    public const ushort SegmentationVersion = 1;
    public const string DefaultHdBetColorMapFileName = "HdBetSegmentationColorMap.txt";

    /// <summary>
    /// Volume chunks exceed the default reliable sequenced payload limit (~1.2 KB).
    /// </summary>
    public const NetworkDelivery VolumeMessageDelivery = NetworkDelivery.ReliableFragmentedSequenced;

    public enum VoxelFormat : byte
    {
        R16 = 1,
    }

    public enum LabelFormat : byte
    {
        U8 = 1,
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

        public SimpleITKUnity.SimpleITKImageInfo ToImageInfo()
        {
            return new SimpleITKUnity.SimpleITKImageInfo
            {
                Dimension = 3,
                Size = new ulong[] { (ulong)Width, (ulong)Height, (ulong)Depth },
                Spacing = new[] { (double)SpacingX, (double)SpacingY, (double)SpacingZ },
                Origin = new[] { (double)OriginX, (double)OriginY, (double)OriginZ },
                Direction = new[]
                {
                    (double)Direction00, (double)Direction01, (double)Direction02,
                    (double)Direction10, (double)Direction11, (double)Direction12,
                    (double)Direction20, (double)Direction21, (double)Direction22,
                },
                PixelId = 0,
                ComponentsPerPixel = 1,
            };
        }
    }

    public struct SegmentationHeader
    {
        public uint TransferId;
        public int SampleIndex;
        public int Width;
        public int Height;
        public int Depth;
        public LabelFormat LabelFormat;
        public CompressionKind Compression;
        public byte LabelMaximum;
        public uint UncompressedPayloadBytes;
        public uint CompressedPayloadBytes;
        public ushort TotalChunks;
        public uint PayloadCrc32;
        public string ColorMapFileName;

        public static SegmentationHeader Create(
            uint transferId,
            int sampleIndex,
            int width,
            int height,
            int depth,
            byte labelMaximum,
            CompressionKind compression,
            uint uncompressedPayloadBytes,
            uint compressedPayloadBytes,
            ushort totalChunks,
            uint payloadCrc32,
            string colorMapFileName)
        {
            return new SegmentationHeader
            {
                TransferId = transferId,
                SampleIndex = sampleIndex,
                Width = width,
                Height = height,
                Depth = depth,
                LabelFormat = LabelFormat.U8,
                Compression = compression,
                LabelMaximum = labelMaximum,
                UncompressedPayloadBytes = uncompressedPayloadBytes,
                CompressedPayloadBytes = compressedPayloadBytes,
                TotalChunks = totalChunks,
                PayloadCrc32 = payloadCrc32,
                ColorMapFileName = string.IsNullOrWhiteSpace(colorMapFileName)
                    ? DefaultHdBetColorMapFileName
                    : colorMapFileName.Trim(),
            };
        }
    }

    public static bool TryReadHeader(FastBufferReader reader, out VolumeHeader header, out string error)
    {
        header = default;
        error = string.Empty;

        reader.ReadValueSafe(out uint magic);
        if (magic != Magic)
        {
            error = $"Unexpected volume header magic: 0x{magic:X8}";
            return false;
        }

        reader.ReadValueSafe(out ushort version);
        if (version != Version)
        {
            error = $"Unsupported volume protocol version: {version}";
            return false;
        }

        reader.ReadValueSafe(out uint transferId);
        reader.ReadValueSafe(out int sampleIndex);
        reader.ReadValueSafe(out int width);
        reader.ReadValueSafe(out int height);
        reader.ReadValueSafe(out int depth);
        reader.ReadValueSafe(out byte voxelFormatRaw);
        reader.ReadValueSafe(out byte compressionRaw);
        reader.ReadValueSafe(out float sourceMinimum);
        reader.ReadValueSafe(out float sourceMaximum);
        reader.ReadValueSafe(out float spacingX);
        reader.ReadValueSafe(out float spacingY);
        reader.ReadValueSafe(out float spacingZ);
        reader.ReadValueSafe(out float originX);
        reader.ReadValueSafe(out float originY);
        reader.ReadValueSafe(out float originZ);
        reader.ReadValueSafe(out float direction00);
        reader.ReadValueSafe(out float direction01);
        reader.ReadValueSafe(out float direction02);
        reader.ReadValueSafe(out float direction10);
        reader.ReadValueSafe(out float direction11);
        reader.ReadValueSafe(out float direction12);
        reader.ReadValueSafe(out float direction20);
        reader.ReadValueSafe(out float direction21);
        reader.ReadValueSafe(out float direction22);
        reader.ReadValueSafe(out uint uncompressedPayloadBytes);
        reader.ReadValueSafe(out uint compressedPayloadBytes);
        reader.ReadValueSafe(out ushort totalChunks);
        reader.ReadValueSafe(out uint payloadCrc32);

        header = new VolumeHeader
        {
            TransferId = transferId,
            SampleIndex = sampleIndex,
            Width = width,
            Height = height,
            Depth = depth,
            VoxelFormat = (VoxelFormat)voxelFormatRaw,
            Compression = (CompressionKind)compressionRaw,
            SourceMinimum = sourceMinimum,
            SourceMaximum = sourceMaximum,
            SpacingX = spacingX,
            SpacingY = spacingY,
            SpacingZ = spacingZ,
            OriginX = originX,
            OriginY = originY,
            OriginZ = originZ,
            Direction00 = direction00,
            Direction01 = direction01,
            Direction02 = direction02,
            Direction10 = direction10,
            Direction11 = direction11,
            Direction12 = direction12,
            Direction20 = direction20,
            Direction21 = direction21,
            Direction22 = direction22,
            UncompressedPayloadBytes = uncompressedPayloadBytes,
            CompressedPayloadBytes = compressedPayloadBytes,
            TotalChunks = totalChunks,
            PayloadCrc32 = payloadCrc32,
        };

        return true;
    }

    public static bool TryReadSegmentationHeader(
        FastBufferReader reader,
        out SegmentationHeader header,
        out string error)
    {
        header = default;
        error = string.Empty;

        reader.ReadValueSafe(out uint magic);
        if (magic != SegmentationMagic)
        {
            error = $"Unexpected segmentation header magic: 0x{magic:X8}";
            return false;
        }

        reader.ReadValueSafe(out ushort version);
        if (version != SegmentationVersion)
        {
            error = $"Unsupported segmentation protocol version: {version}";
            return false;
        }

        reader.ReadValueSafe(out uint transferId);
        reader.ReadValueSafe(out int sampleIndex);
        reader.ReadValueSafe(out int width);
        reader.ReadValueSafe(out int height);
        reader.ReadValueSafe(out int depth);
        reader.ReadValueSafe(out byte labelFormatRaw);
        reader.ReadValueSafe(out byte compressionRaw);
        reader.ReadValueSafe(out byte labelMaximum);
        reader.ReadValueSafe(out uint uncompressedPayloadBytes);
        reader.ReadValueSafe(out uint compressedPayloadBytes);
        reader.ReadValueSafe(out ushort totalChunks);
        reader.ReadValueSafe(out uint payloadCrc32);
        reader.ReadValueSafe(out string colorMapFileName);

        header = new SegmentationHeader
        {
            TransferId = transferId,
            SampleIndex = sampleIndex,
            Width = width,
            Height = height,
            Depth = depth,
            LabelFormat = (LabelFormat)labelFormatRaw,
            Compression = (CompressionKind)compressionRaw,
            LabelMaximum = labelMaximum,
            UncompressedPayloadBytes = uncompressedPayloadBytes,
            CompressedPayloadBytes = compressedPayloadBytes,
            TotalChunks = totalChunks,
            PayloadCrc32 = payloadCrc32,
            ColorMapFileName = string.IsNullOrWhiteSpace(colorMapFileName)
                ? DefaultHdBetColorMapFileName
                : colorMapFileName,
        };

        return true;
    }

    public static bool TryReadChunk(
        FastBufferReader reader,
        out uint transferId,
        out ushort chunkIndex,
        out ushort totalChunks,
        out byte[] chunkBytes,
        out string error)
    {
        transferId = 0;
        chunkIndex = 0;
        totalChunks = 0;
        chunkBytes = Array.Empty<byte>();
        error = string.Empty;

        reader.ReadValueSafe(out transferId);
        reader.ReadValueSafe(out chunkIndex);
        reader.ReadValueSafe(out totalChunks);
        return TryReadByteArray(reader, out chunkBytes, out error);
    }

    public static bool TryReadTransferIdAndCrc(
        FastBufferReader reader,
        out uint transferId,
        out uint payloadCrc32,
        out string error)
    {
        transferId = 0;
        payloadCrc32 = 0;
        error = string.Empty;

        reader.ReadValueSafe(out transferId);
        reader.ReadValueSafe(out payloadCrc32);
        return true;
    }

    public static bool TryReadTransferId(
        FastBufferReader reader,
        out uint transferId,
        out string error)
    {
        transferId = 0;
        error = string.Empty;
        reader.ReadValueSafe(out transferId);
        return true;
    }

    public static bool TryReadByteArray(FastBufferReader reader, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;

        reader.ReadValueSafe(out int length);
        if (length < 0)
        {
            error = $"Negative byte array length: {length}";
            return false;
        }

        if (length == 0)
        {
            return true;
        }

        bytes = new byte[length];
        for (var i = 0; i < length; ++i)
        {
            reader.ReadValueSafe(out byte value);
            bytes[i] = value;
        }

        return true;
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

    public static void WriteSegmentationHeader(FastBufferWriter writer, in SegmentationHeader header)
    {
        writer.WriteValueSafe(SegmentationMagic);
        writer.WriteValueSafe(SegmentationVersion);
        writer.WriteValueSafe(header.TransferId);
        writer.WriteValueSafe(header.SampleIndex);
        writer.WriteValueSafe(header.Width);
        writer.WriteValueSafe(header.Height);
        writer.WriteValueSafe(header.Depth);
        writer.WriteValueSafe((byte)header.LabelFormat);
        writer.WriteValueSafe((byte)header.Compression);
        writer.WriteValueSafe(header.LabelMaximum);
        writer.WriteValueSafe(header.UncompressedPayloadBytes);
        writer.WriteValueSafe(header.CompressedPayloadBytes);
        writer.WriteValueSafe(header.TotalChunks);
        writer.WriteValueSafe(header.PayloadCrc32);
        writer.WriteValueSafe(header.ColorMapFileName ?? DefaultHdBetColorMapFileName);
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

    public static int EstimateSegmentationBeginMessageSize(string colorMapFileName)
    {
        // FastBufferWriter.WriteValueSafe(string) encodes UTF-16 (2 bytes/char) + length prefix.
        var colorMap = string.IsNullOrWhiteSpace(colorMapFileName)
            ? DefaultHdBetColorMapFileName
            : colorMapFileName;
        var fixedBytes =
            sizeof(uint) * 5 +
            sizeof(int) * 4 +
            sizeof(ushort) * 2 +
            sizeof(byte) * 3;
        return fixedBytes + FastBufferWriter.GetWriteSize(colorMap) + 32;
    }

    public static int EstimateChunkMessageSize(int chunkPayloadBytes)
    {
        return sizeof(uint) + sizeof(ushort) * 2 + sizeof(int) + chunkPayloadBytes + 16;
    }
}
