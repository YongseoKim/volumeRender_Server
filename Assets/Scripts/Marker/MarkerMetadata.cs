using System;
using UnityEngine;

/// <summary>
/// Network/storage DTO for a completed paneLine_Volume marker.
/// World positions are omitted; each peer reconstructs visuals from VolumePoints01.
/// </summary>
[Serializable]
public struct PaneLineVolumeMetadata
{
    public uint MarkerId;
    public ulong OwnerClientId;
    public MarkerPane Pane;
    public float DrawnSliceCoordinate;
    public int FileIndex;
    public Color Color;
    public float VolumeLineWidth;
    public Vector3[] VolumePoints01;

    public bool IsValid =>
        VolumePoints01 != null &&
        VolumePoints01.Length >= 2 &&
        FileIndex >= 0;
}

/// <summary>
/// Ephemeral drag session identity. Assigned by the drawing client at drag begin.
/// </summary>
[Serializable]
public struct PaneLineDragSessionId : IEquatable<PaneLineDragSessionId>
{
    public ulong OwnerClientId;
    public uint LocalDragId;

    public bool Equals(PaneLineDragSessionId other) =>
        OwnerClientId == other.OwnerClientId && LocalDragId == other.LocalDragId;

    public override bool Equals(object obj) => obj is PaneLineDragSessionId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(OwnerClientId, LocalDragId);

    public static bool operator ==(PaneLineDragSessionId left, PaneLineDragSessionId right) => left.Equals(right);
    public static bool operator !=(PaneLineDragSessionId left, PaneLineDragSessionId right) => !left.Equals(right);
}

/// <summary>
/// Sent when a client begins drawing. Starts a remote preview on other peers.
/// </summary>
[Serializable]
public struct PaneLineDragBeginData
{
    public PaneLineDragSessionId SessionId;
    public MarkerPane Pane;
    public float DrawnSliceCoordinate;
    public int FileIndex;
    public Color Color;
    public float VolumeLineWidth;
    public Vector3 FirstVolumePoint01;
}

/// <summary>
/// Throttled incremental update while dragging.
/// </summary>
[Serializable]
public struct PaneLineDragPointData
{
    public PaneLineDragSessionId SessionId;
    public MarkerPane Pane;
    public int FileIndex;
    public Vector3 VolumePoint01;
}

/// <summary>
/// Sent when drag ends. Server persists and rebroadcasts as PaneLineVolumeMetadata.
/// </summary>
[Serializable]
public struct PaneLineDragEndData
{
    public PaneLineDragSessionId SessionId;
    public PaneLineVolumeMetadata DraftMetadata;
}

/// <summary>
/// Internal handle used while a drag is active on this peer.
/// </summary>
public struct PaneLineDragContext
{
    public RectTransform RawImage;
    public Transform VolumeLineAnchor;
}
