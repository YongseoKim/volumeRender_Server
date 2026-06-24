using UnityEngine;

/// <summary>
/// Resolves scene objects needed to render remote markers on this peer.
/// Implement on SimpleITKOrthogonalViewController (or a thin bridge component).
/// </summary>
public interface IMarkerPaneContextResolver
{
    bool TryResolvePaneContext(
        MarkerPane pane,
        int fileIndex,
        out RectTransform rawImage,
        out Transform volumeLineAnchor);

    bool TryVolume01ToWorld(MarkerPane pane, Vector3 volume01, out Vector3 worldPosition);
}
