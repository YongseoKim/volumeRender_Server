using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Server-authoritative marker persistence. Only MarkerNetService should call this on the server.
/// </summary>
public sealed class MarkerServerStore
{
    private uint _nextMarkerId = 1;
    private readonly List<PaneLineVolumeMetadata> _markers = new List<PaneLineVolumeMetadata>();

    public uint AllocateMarkerId() => _nextMarkerId++;

    public void Save(in PaneLineVolumeMetadata metadata)
    {
        if (!metadata.IsValid)
        {
            Debug.LogWarning("[MarkerServerStore] Ignored invalid marker save request.");
            return;
        }

        _markers.Add(metadata);
        PersistToDisk(metadata);
    }

    public bool TryErase(uint markerId, out PaneLineVolumeMetadata erased)
    {
        for (var i = _markers.Count - 1; i >= 0; --i)
        {
            if (_markers[i].MarkerId != markerId)
            {
                continue;
            }

            erased = _markers[i];
            _markers.RemoveAt(i);
            PersistEraseToDisk(markerId);
            return true;
        }

        erased = default;
        return false;
    }

    public IReadOnlyList<PaneLineVolumeMetadata> GetAll()
    {
        return _markers;
    }

    public PaneLineVolumeMetadata[] GetSnapshotForFileIndex(int fileIndex)
    {
        var results = new List<PaneLineVolumeMetadata>();
        for (var i = 0; i < _markers.Count; ++i)
        {
            if (_markers[i].FileIndex == fileIndex)
            {
                results.Add(_markers[i]);
            }
        }

        return results.ToArray();
    }

    private void PersistToDisk(in PaneLineVolumeMetadata metadata)
    {
        // TODO: JSON / DB / file per sample index.
    }

    private void PersistEraseToDisk(uint markerId)
    {
        // TODO: remove marker from persistent storage.
    }
}
