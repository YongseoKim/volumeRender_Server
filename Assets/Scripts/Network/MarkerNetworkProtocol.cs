using System;
using UnityEngine;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// Named-message serialization for paneLine_Volume marker sync.
/// Follows the same FastBufferReader/Writer style as VolumeNetworkProtocol.
/// </summary>
public static class MarkerNetworkProtocol
{
    public const string DragBeginMessageName = "MarkerDragBegin";
    public const string DragPointMessageName = "MarkerDragPoint";
    public const string DragEndMessageName = "MarkerDragEnd";
    public const string MarkerCreatedMessageName = "MarkerCreated";
    public const string MarkerErasedMessageName = "MarkerErased";
    public const string MarkerSnapshotMessageName = "MarkerSnapshot";
    public const string BroadcastPermissionSyncMessageName = "MarkerBroadcastPermissionSync";

    public const NetworkDelivery PermissionDelivery = NetworkDelivery.ReliableSequenced;
    public const NetworkDelivery DragPreviewDelivery = NetworkDelivery.UnreliableSequenced;
    public const NetworkDelivery MarkerCommitDelivery = NetworkDelivery.ReliableFragmentedSequenced;

    private const int MetadataHeaderBytes = 64;
    private const int DragSessionIdBytes = 12;
    private const int Vector3Bytes = sizeof(float) * 3;
    private const int DefaultSmallMessageBytes = 64;
    public const int MaxMarkerMessageBytes = 256 * 1024;

    public static int EstimateMetadataSize(in PaneLineVolumeMetadata metadata)
    {
        var pointCount = metadata.VolumePoints01?.Length ?? 0;
        return MetadataHeaderBytes + sizeof(int) + pointCount * Vector3Bytes;
    }

    public static int EstimateDragBeginSize(in PaneLineDragBeginData data)
    {
        return DragSessionIdBytes + DefaultSmallMessageBytes + Vector3Bytes;
    }

    public static int EstimateDragPointSize(in PaneLineDragPointData data)
    {
        return DragSessionIdBytes + DefaultSmallMessageBytes + Vector3Bytes;
    }

    public static int EstimateDragEndSize(in PaneLineDragEndData data)
    {
        return DragSessionIdBytes + EstimateMetadataSize(data.DraftMetadata);
    }

    public static int EstimateMetadataListSize(PaneLineVolumeMetadata[] markers)
    {
        var size = sizeof(int);
        if (markers == null)
        {
            return size;
        }

        for (var i = 0; i < markers.Length; ++i)
        {
            size += EstimateMetadataSize(markers[i]);
        }

        return size;
    }

    public static int ClampMessageCapacity(int estimatedBytes)
    {
        var padded = estimatedBytes + 128;
        return Mathf.Clamp(padded, DefaultSmallMessageBytes, MaxMarkerMessageBytes);
    }

    public static int ResolveWriterMaxSize(int initialCapacity)
    {
        return MaxMarkerMessageBytes;
    }

    public static void WriteDragSessionId(FastBufferWriter writer, in PaneLineDragSessionId sessionId)
    {
        writer.WriteValueSafe(sessionId.OwnerClientId);
        writer.WriteValueSafe(sessionId.LocalDragId);
    }

    public static void ReadDragSessionId(FastBufferReader reader, out PaneLineDragSessionId sessionId)
    {
        reader.ReadValueSafe(out ulong ownerClientId);
        reader.ReadValueSafe(out uint localDragId);
        sessionId = new PaneLineDragSessionId
        {
            OwnerClientId = ownerClientId,
            LocalDragId = localDragId,
        };
    }

    public static void WriteDragBegin(FastBufferWriter writer, in PaneLineDragBeginData data)
    {
        WriteDragSessionId(writer, data.SessionId);
        writer.WriteValueSafe((byte)data.Pane);
        writer.WriteValueSafe(data.DrawnSliceCoordinate);
        writer.WriteValueSafe(data.FileIndex);
        WriteColor(writer, data.Color);
        writer.WriteValueSafe(data.VolumeLineWidth);
        WriteVector3(writer, data.FirstVolumePoint01);
    }

    public static void ReadDragBegin(FastBufferReader reader, out PaneLineDragBeginData data)
    {
        ReadDragSessionId(reader, out var sessionId);
        reader.ReadValueSafe(out byte paneRaw);
        reader.ReadValueSafe(out float drawnSliceCoordinate);
        reader.ReadValueSafe(out int fileIndex);
        ReadColor(reader, out var color);
        reader.ReadValueSafe(out float volumeLineWidth);
        ReadVector3(reader, out var firstPoint);

        data = new PaneLineDragBeginData
        {
            SessionId = sessionId,
            Pane = (MarkerPane)paneRaw,
            DrawnSliceCoordinate = drawnSliceCoordinate,
            FileIndex = fileIndex,
            Color = color,
            VolumeLineWidth = volumeLineWidth,
            FirstVolumePoint01 = firstPoint,
        };
    }

    public static void WriteDragPoint(FastBufferWriter writer, in PaneLineDragPointData data)
    {
        WriteDragSessionId(writer, data.SessionId);
        writer.WriteValueSafe((byte)data.Pane);
        writer.WriteValueSafe(data.FileIndex);
        WriteVector3(writer, data.VolumePoint01);
    }

    public static void ReadDragPoint(FastBufferReader reader, out PaneLineDragPointData data)
    {
        ReadDragSessionId(reader, out var sessionId);
        reader.ReadValueSafe(out byte paneRaw);
        reader.ReadValueSafe(out int fileIndex);
        ReadVector3(reader, out var volumePoint01);

        data = new PaneLineDragPointData
        {
            SessionId = sessionId,
            Pane = (MarkerPane)paneRaw,
            FileIndex = fileIndex,
            VolumePoint01 = volumePoint01,
        };
    }

    public static void WriteDragEnd(FastBufferWriter writer, in PaneLineDragEndData data)
    {
        WriteDragSessionId(writer, data.SessionId);
        WriteMetadata(writer, data.DraftMetadata);
    }

    public static void ReadDragEnd(FastBufferReader reader, out PaneLineDragEndData data)
    {
        ReadDragSessionId(reader, out var sessionId);
        ReadMetadata(reader, out var metadata);
        data = new PaneLineDragEndData
        {
            SessionId = sessionId,
            DraftMetadata = metadata,
        };
    }

    public static void WriteMetadata(FastBufferWriter writer, in PaneLineVolumeMetadata metadata)
    {
        writer.WriteValueSafe(metadata.MarkerId);
        writer.WriteValueSafe(metadata.OwnerClientId);
        writer.WriteValueSafe((byte)metadata.Pane);
        writer.WriteValueSafe(metadata.DrawnSliceCoordinate);
        writer.WriteValueSafe(metadata.FileIndex);
        WriteColor(writer, metadata.Color);
        writer.WriteValueSafe(metadata.VolumeLineWidth);
        WriteVector3Array(writer, metadata.VolumePoints01);
    }

    public static void ReadMetadata(FastBufferReader reader, out PaneLineVolumeMetadata metadata)
    {
        reader.ReadValueSafe(out uint markerId);
        reader.ReadValueSafe(out ulong ownerClientId);
        reader.ReadValueSafe(out byte paneRaw);
        reader.ReadValueSafe(out float drawnSliceCoordinate);
        reader.ReadValueSafe(out int fileIndex);
        ReadColor(reader, out var color);
        reader.ReadValueSafe(out float volumeLineWidth);
        ReadVector3Array(reader, out var volumePoints01);

        metadata = new PaneLineVolumeMetadata
        {
            MarkerId = markerId,
            OwnerClientId = ownerClientId,
            Pane = (MarkerPane)paneRaw,
            DrawnSliceCoordinate = drawnSliceCoordinate,
            FileIndex = fileIndex,
            Color = color,
            VolumeLineWidth = volumeLineWidth,
            VolumePoints01 = volumePoints01,
        };
    }

    public static void WriteMetadataList(FastBufferWriter writer, PaneLineVolumeMetadata[] markers)
    {
        var count = markers?.Length ?? 0;
        writer.WriteValueSafe(count);
        for (var i = 0; i < count; ++i)
        {
            WriteMetadata(writer, markers[i]);
        }
    }

    public static void ReadMetadataList(FastBufferReader reader, out PaneLineVolumeMetadata[] markers)
    {
        reader.ReadValueSafe(out int count);
        if (count <= 0)
        {
            markers = Array.Empty<PaneLineVolumeMetadata>();
            return;
        }

        markers = new PaneLineVolumeMetadata[count];
        for (var i = 0; i < count; ++i)
        {
            ReadMetadata(reader, out markers[i]);
        }
    }

    public static void WriteMarkerId(FastBufferWriter writer, uint markerId)
    {
        writer.WriteValueSafe(markerId);
    }

    public static void ReadMarkerId(FastBufferReader reader, out uint markerId)
    {
        reader.ReadValueSafe(out markerId);
    }

    public static void WriteBool(FastBufferWriter writer, bool value)
    {
        writer.WriteValueSafe(value);
    }

    public static void ReadBool(FastBufferReader reader, out bool value)
    {
        reader.ReadValueSafe(out value);
    }

    private static void WriteColor(FastBufferWriter writer, Color color)
    {
        writer.WriteValueSafe(color.r);
        writer.WriteValueSafe(color.g);
        writer.WriteValueSafe(color.b);
        writer.WriteValueSafe(color.a);
    }

    private static void ReadColor(FastBufferReader reader, out Color color)
    {
        reader.ReadValueSafe(out float r);
        reader.ReadValueSafe(out float g);
        reader.ReadValueSafe(out float b);
        reader.ReadValueSafe(out float a);
        color = new Color(r, g, b, a);
    }

    private static void WriteVector3(FastBufferWriter writer, Vector3 value)
    {
        writer.WriteValueSafe(value.x);
        writer.WriteValueSafe(value.y);
        writer.WriteValueSafe(value.z);
    }

    private static void ReadVector3(FastBufferReader reader, out Vector3 value)
    {
        reader.ReadValueSafe(out float x);
        reader.ReadValueSafe(out float y);
        reader.ReadValueSafe(out float z);
        value = new Vector3(x, y, z);
    }

    private static void WriteVector3Array(FastBufferWriter writer, Vector3[] points)
    {
        var count = points?.Length ?? 0;
        writer.WriteValueSafe(count);
        for (var i = 0; i < count; ++i)
        {
            WriteVector3(writer, points[i]);
        }
    }

    private static void ReadVector3Array(FastBufferReader reader, out Vector3[] points)
    {
        reader.ReadValueSafe(out int count);
        if (count <= 0)
        {
            points = Array.Empty<Vector3>();
            return;
        }

        points = new Vector3[count];
        for (var i = 0; i < count; ++i)
        {
            ReadVector3(reader, out points[i]);
        }
    }
}
