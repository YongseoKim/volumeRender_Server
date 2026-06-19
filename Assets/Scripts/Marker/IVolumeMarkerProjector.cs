using UnityEngine;

public enum MarkerPane
{
    A = 0,
    B = 1,
    C = 2
}

public interface IVolumeMarkerProjector
{
    bool IsMarkerVisibleOnCurrentSlice(MarkerPane pane, float drawnSliceCoordinate);

    bool TryProjectVolume01ToRawImageLocal(MarkerPane pane, Vector3 volume01, out Vector2 rawImageLocal);
}
