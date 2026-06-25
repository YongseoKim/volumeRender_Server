using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;

public class MarkerManager : MonoBehaviour
{
    [Header("Marker Settings")]
    [SerializeField] private Button penButton;
    [SerializeField] private Button redButton;
    [SerializeField] private Button purpleButton;
    [SerializeField] private Button eraseButton;

    private Color _redColor = new Color(1f, 0f, 0f, 0.95f);
    private Color _purpleColor = new Color(0.863f, 0.353f, 1.0f, 0.95f);
    
    [Header("Pane Line Visuals")]
    [SerializeField] private bool showPaneLineVisuals = true;
    [SerializeField, Range(2f, 32f)] private float lineDragMinPointSpacingPixels = 6f;
    [SerializeField, Range(1f, 12f)] private float paneLineWidthPixels = 4f;
    [SerializeField, Range(0.0005f, 0.02f)] private float volumeLineWidth = 0.003f;
    [SerializeField] private float paneLineLocalDepth = -1f;
    [SerializeField] private int volumeLineLayer = 29;

    [SerializeField] private bool logPlacementFailures;
    [SerializeField] private bool deferCompletionToNetwork = true;
    [SerializeField] private MonoBehaviour paneContextResolverBehaviour;

    public event Action<PaneLineDragBeginData> LocalDragBegun;
    public event Action<PaneLineDragPointData> LocalDragPointAppended;
    public event Action<PaneLineDragEndData> LocalDragEnded;

    public bool DeferCompletionToNetwork
    {
        get => deferCompletionToNetwork;
        set => deferCompletionToNetwork = value;
    }

    public void SetNetworkCommitEnabled(bool enabled)
    {
        _networkCommitEnabled = enabled;
    }

    private Color _activePaneLineColor;
    private bool _isPenModeActive;
    private bool _hasSelectedColor;

    private struct PaneLineStyle
    {
        public Color Color;
        public float UiWidthPixels;
        public float UiLocalDepth;
        public float VolumeWidth;
        public int VolumeLineLayer;
    }

    private sealed class ActiveDragTrail
    {
        public MarkerPane Pane;
        public float DrawnSliceCoordinate;
        public int FileIndex = -1;
        public UILineGraphic UiLine;
        public LineRenderer VolumeLine;
        public readonly List<Vector2> UiPoints = new List<Vector2>();
        public readonly List<Vector3> VolumePoints01 = new List<Vector3>();
        public readonly List<Vector3> VolumePoints = new List<Vector3>();
        public Vector2 LastScreenPosition;
        public bool HasLastScreenPosition;
    }

    private sealed class CompletedPaneMarker
    {
        public uint MarkerId;
        public MarkerPane Pane;
        public float DrawnSliceCoordinate;
        public int FileIndex = -1;
        public RectTransform RawImage;
        public UILineGraphic UiLine;
        public LineRenderer VolumeLine;
        public Color LineColor;
        public float VolumeLineWidth;
        public readonly List<Vector3> VolumePoints01 = new List<Vector3>();
    }

    private sealed class RemoteDragTrailBinding
    {
        public RectTransform RawImage;
        public Transform VolumeLineAnchor;
        public ActiveDragTrail Trail;
    }

    private readonly List<GameObject> _spawnedLineObjects = new List<GameObject>();
    private readonly Dictionary<RectTransform, ActiveDragTrail> _activeDragTrails =
        new Dictionary<RectTransform, ActiveDragTrail>();
    private readonly List<CompletedPaneMarker> _completedMarkers = new List<CompletedPaneMarker>();
    private readonly Dictionary<PaneLineDragSessionId, RemoteDragTrailBinding> _remoteDragTrails =
        new Dictionary<PaneLineDragSessionId, RemoteDragTrailBinding>();

    private Material _volumeLineMaterial;
    private Material _uiLineMaterial;
    private IVolumeMarkerProjector _projector;
    private IMarkerPaneContextResolver _paneContextResolver;
    private int _activeFileIndex = -1;
    private bool _networkCommitEnabled;

    private void Awake()
    {
        ResolvePaneContextResolver();
        ClearMarkers();
        BindMarkerButtons();
        ResetMarkerToolState();
    }

    private void OnDestroy()
    {
        UnbindMarkerButtons();
        ClearMarkers();
        DestroyLineMaterials();
    }

    private void OnApplicationQuit()
    {
        ClearMarkers();
    }

    public void SetProjector(IVolumeMarkerProjector projector)
    {
        _projector = projector;
        RefreshMarkerUi();
    }

    public void SetActiveFileIndex(int fileIndex)
    {
        if (_activeFileIndex == fileIndex)
        {
            return;
        }

        _activeFileIndex = fileIndex;
        RefreshMarkerUi();
    }

    public void BeginDragMarkers(
        RectTransform rawImage,
        Transform volumeLineAnchor,
        MarkerPane pane,
        float drawnSliceCoordinate,
        int fileIndex,
        Camera eventCamera,
        Vector2 screenPosition,
        Vector3 volume01,
        Vector3 volumeWorldPosition)
    {
        if (!showPaneLineVisuals || !CanDrawLines(rawImage))
        {
            return;
        }

        ClearPaneTrail(rawImage);
        var trail = GetOrCreateActiveDragTrail(rawImage, volumeLineAnchor, pane, drawnSliceCoordinate, fileIndex);
        var pointAdded = AppendTrailPoint(
            trail,
            rawImage,
            eventCamera,
            screenPosition,
            volume01,
            volumeWorldPosition);
        trail.LastScreenPosition = screenPosition;
        trail.HasLastScreenPosition = true;

        if (pointAdded)
        {
            RaiseLocalDragBegun(trail, volume01);
        }
    }

    public void UpdateDragMarkers(
        RectTransform rawImage,
        Transform volumeLineAnchor,
        MarkerPane pane,
        float drawnSliceCoordinate,
        int fileIndex,
        Camera eventCamera,
        Vector2 screenPosition,
        Vector3 volume01,
        Vector3 volumeWorldPosition)
    {
        if (!showPaneLineVisuals || !CanDrawLines(rawImage))
        {
            return;
        }

        if (!_activeDragTrails.TryGetValue(rawImage, out var trail))
        {
            BeginDragMarkers(
                rawImage,
                volumeLineAnchor,
                pane,
                drawnSliceCoordinate,
                fileIndex,
                eventCamera,
                screenPosition,
                volume01,
                volumeWorldPosition);
            return;
        }

        trail.FileIndex = fileIndex;

        if (trail.HasLastScreenPosition &&
            Vector2.Distance(screenPosition, trail.LastScreenPosition) < lineDragMinPointSpacingPixels)
        {
            return;
        }

        EnsureTrailRenderers(trail, rawImage, volumeLineAnchor);
        var pointAdded = AppendTrailPoint(
            trail,
            rawImage,
            eventCamera,
            screenPosition,
            volume01,
            volumeWorldPosition);
        trail.LastScreenPosition = screenPosition;
        trail.HasLastScreenPosition = true;

        if (pointAdded)
        {
            RaiseLocalDragPointAppended(trail, volume01);
        }
    }

    public void EndDragMarkers(RectTransform rawImage)
    {
        if (rawImage == null)
        {
            return;
        }

        if (!_activeDragTrails.TryGetValue(rawImage, out var trail))
        {
            return;
        }

        if (ShouldDeferCompletionToNetwork())
        {
            var draft = BuildMetadataFromTrail(trail);
            LocalDragEnded?.Invoke(new PaneLineDragEndData
            {
                DraftMetadata = draft,
            });
            DestroyTrailRenderers(trail);
        }
        else
        {
            FinalizeTrail(trail, rawImage);
        }

        _activeDragTrails.Remove(rawImage);
    }

    public void BeginRemoteDragPreview(in PaneLineDragBeginData data)
    {
        if (!showPaneLineVisuals || !TryResolveRemoteDragContext(data, out var rawImage, out var volumeLineAnchor))
        {
            return;
        }

        CancelRemoteDragPreview(data.SessionId);

        var style = CreatePaneLineStyleFromRemote(data.Color, data.VolumeLineWidth);
        var trail = new ActiveDragTrail
        {
            Pane = data.Pane,
            DrawnSliceCoordinate = data.DrawnSliceCoordinate,
            FileIndex = data.FileIndex,
        };
        EnsureTrailRenderers(trail, rawImage, volumeLineAnchor, style);
        AppendRemoteTrailPoint(trail, rawImage, volumeLineAnchor, data.FirstVolumePoint01, style);

        _remoteDragTrails[data.SessionId] = new RemoteDragTrailBinding
        {
            RawImage = rawImage,
            VolumeLineAnchor = volumeLineAnchor,
            Trail = trail,
        };
    }

    public void UpdateRemoteDragPreview(in PaneLineDragPointData data)
    {
        if (!showPaneLineVisuals ||
            !_remoteDragTrails.TryGetValue(data.SessionId, out var binding) ||
            binding?.Trail == null ||
            binding.RawImage == null)
        {
            return;
        }

        var style = CreatePaneLineStyleFromRemote(
            binding.Trail.UiLine != null ? binding.Trail.UiLine.color : _activePaneLineColor,
            volumeLineWidth);
        EnsureTrailRenderers(binding.Trail, binding.RawImage, binding.VolumeLineAnchor, style);
        AppendRemoteTrailPoint(
            binding.Trail,
            binding.RawImage,
            binding.VolumeLineAnchor,
            data.VolumePoint01,
            style);
    }

    public void EndRemoteDragPreview(PaneLineDragSessionId sessionId)
    {
        CancelRemoteDragPreview(sessionId);
    }

    public void CancelRemoteDragPreview(PaneLineDragSessionId sessionId)
    {
        if (!_remoteDragTrails.TryGetValue(sessionId, out var binding))
        {
            return;
        }

        DestroyTrailRenderers(binding.Trail);
        _remoteDragTrails.Remove(sessionId);
    }

    public void ApplyRemoteMarker(in PaneLineVolumeMetadata metadata)
    {
        if (!showPaneLineVisuals || !metadata.IsValid)
        {
            return;
        }

        if (!TryResolveRemoteMarkerContext(metadata, out var rawImage, out var volumeLineAnchor))
        {
            Debug.LogWarning("[MarkerManager] Could not resolve pane context for remote marker.", this);
            return;
        }

        var style = CreatePaneLineStyleFromRemote(metadata.Color, metadata.VolumeLineWidth);
        var uiLine = CreateUiLine(rawImage, style);
        LineRenderer volumeLine = null;
        if (volumeLineAnchor != null)
        {
            volumeLine = CreateVolumeLine(volumeLineAnchor, style, metadata.FileIndex);
        }

        var marker = new CompletedPaneMarker
        {
            MarkerId = metadata.MarkerId,
            Pane = metadata.Pane,
            DrawnSliceCoordinate = metadata.DrawnSliceCoordinate,
            FileIndex = metadata.FileIndex,
            RawImage = rawImage,
            UiLine = uiLine,
            VolumeLine = volumeLine,
            LineColor = metadata.Color,
            VolumeLineWidth = metadata.VolumeLineWidth,
        };
        marker.VolumePoints01.AddRange(metadata.VolumePoints01);

        if (volumeLine != null)
        {
            ApplyVolumeLinePositionsFromMetadata(volumeLine, metadata);
        }

        _completedMarkers.Add(marker);
        RefreshCompletedMarker(marker);
    }

    public void RemoveRemoteMarker(uint markerId)
    {
        for (var i = _completedMarkers.Count - 1; i >= 0; --i)
        {
            if (_completedMarkers[i].MarkerId != markerId)
            {
                continue;
            }

            var marker = _completedMarkers[i];
            _completedMarkers.RemoveAt(i);
            DestroyLineObject(marker.UiLine != null ? marker.UiLine.gameObject : null);
            DestroyLineObject(marker.VolumeLine != null ? marker.VolumeLine.gameObject : null);
            return;
        }
    }

    public void ClearCommittedMarkers()
    {
        for (var i = _completedMarkers.Count - 1; i >= 0; --i)
        {
            var marker = _completedMarkers[i];
            DestroyLineObject(marker.UiLine != null ? marker.UiLine.gameObject : null);
            DestroyLineObject(marker.VolumeLine != null ? marker.VolumeLine.gameObject : null);
        }

        _completedMarkers.Clear();
    }

    public void RefreshMarkerUi()
    {
        if (!showPaneLineVisuals)
        {
            return;
        }

        for (var i = 0; i < _completedMarkers.Count; ++i)
        {
            RefreshCompletedMarker(_completedMarkers[i]);
        }

        foreach (var pair in _activeDragTrails)
        {
            RefreshActiveTrail(pair.Value, pair.Key);
        }

        foreach (var pair in _remoteDragTrails)
        {
            RefreshRemoteDragTrail(pair.Value, pair.Key);
        }
    }

    public void ClearMarkers()
    {
        _activeDragTrails.Clear();
        _remoteDragTrails.Clear();
        _completedMarkers.Clear();

        for (var i = _spawnedLineObjects.Count - 1; i >= 0; --i)
        {
            var lineObject = _spawnedLineObjects[i];
            if (lineObject == null)
            {
                continue;
            }

            if (Application.isPlaying)
            {
                Destroy(lineObject);
            }
            else
            {
                DestroyImmediate(lineObject);
            }
        }

        _spawnedLineObjects.Clear();
    }

    private void FinalizeTrail(ActiveDragTrail trail, RectTransform rawImage)
    {
        if (trail == null || rawImage == null || trail.VolumePoints01.Count == 0)
        {
            return;
        }

        var marker = new CompletedPaneMarker
        {
            Pane = trail.Pane,
            DrawnSliceCoordinate = trail.DrawnSliceCoordinate,
            FileIndex = trail.FileIndex,
            RawImage = rawImage,
            UiLine = trail.UiLine,
            VolumeLine = trail.VolumeLine,
            LineColor = _activePaneLineColor,
            VolumeLineWidth = volumeLineWidth,
        };
        marker.VolumePoints01.AddRange(trail.VolumePoints01);

        _completedMarkers.Add(marker);
        RefreshCompletedMarker(marker);
    }

    private bool ShouldDeferCompletionToNetwork()
    {
        return deferCompletionToNetwork && _networkCommitEnabled;
    }

    private void RaiseLocalDragBegun(ActiveDragTrail trail, Vector3 firstVolumePoint01)
    {
        if (LocalDragBegun == null)
        {
            return;
        }

        LocalDragBegun.Invoke(new PaneLineDragBeginData
        {
            Pane = trail.Pane,
            DrawnSliceCoordinate = trail.DrawnSliceCoordinate,
            FileIndex = trail.FileIndex,
            Color = _activePaneLineColor,
            VolumeLineWidth = volumeLineWidth,
            FirstVolumePoint01 = firstVolumePoint01,
        });
    }

    private void RaiseLocalDragPointAppended(ActiveDragTrail trail, Vector3 volumePoint01)
    {
        LocalDragPointAppended?.Invoke(new PaneLineDragPointData
        {
            Pane = trail.Pane,
            FileIndex = trail.FileIndex,
            VolumePoint01 = volumePoint01,
        });
    }

    private PaneLineVolumeMetadata BuildMetadataFromTrail(ActiveDragTrail trail)
    {
        return new PaneLineVolumeMetadata
        {
            Pane = trail.Pane,
            DrawnSliceCoordinate = trail.DrawnSliceCoordinate,
            FileIndex = trail.FileIndex,
            Color = _activePaneLineColor,
            VolumeLineWidth = volumeLineWidth,
            VolumePoints01 = trail.VolumePoints01.ToArray(),
        };
    }

    private void ResolvePaneContextResolver()
    {
        if (_paneContextResolver != null)
        {
            return;
        }

        if (paneContextResolverBehaviour is IMarkerPaneContextResolver resolver)
        {
            _paneContextResolver = resolver;
            return;
        }

        foreach (var behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (behaviour is IMarkerPaneContextResolver candidate)
            {
                _paneContextResolver = candidate;
                paneContextResolverBehaviour = behaviour;
                return;
            }
        }
    }

    private bool TryResolveRemoteDragContext(
        in PaneLineDragBeginData data,
        out RectTransform rawImage,
        out Transform volumeLineAnchor)
    {
        ResolvePaneContextResolver();
        if (_paneContextResolver != null &&
            _paneContextResolver.TryResolvePaneContext(data.Pane, data.FileIndex, out rawImage, out volumeLineAnchor))
        {
            return rawImage != null;
        }

        rawImage = null;
        volumeLineAnchor = null;
        return false;
    }

    private bool TryResolveRemoteMarkerContext(
        in PaneLineVolumeMetadata metadata,
        out RectTransform rawImage,
        out Transform volumeLineAnchor)
    {
        ResolvePaneContextResolver();
        if (_paneContextResolver != null &&
            _paneContextResolver.TryResolvePaneContext(metadata.Pane, metadata.FileIndex, out rawImage, out volumeLineAnchor))
        {
            return rawImage != null;
        }

        rawImage = null;
        volumeLineAnchor = null;
        return false;
    }

    private PaneLineStyle CreatePaneLineStyleFromRemote(Color color, float remoteVolumeLineWidth)
    {
        return new PaneLineStyle
        {
            Color = color,
            UiWidthPixels = paneLineWidthPixels,
            UiLocalDepth = paneLineLocalDepth,
            VolumeWidth = remoteVolumeLineWidth > 0f ? remoteVolumeLineWidth : volumeLineWidth,
            VolumeLineLayer = volumeLineLayer,
        };
    }

    private void AppendRemoteTrailPoint(
        ActiveDragTrail trail,
        RectTransform rawImage,
        Transform volumeLineAnchor,
        Vector3 volume01,
        PaneLineStyle style)
    {
        if (trail.VolumePoints01.Count == 0 ||
            Vector3.Distance(trail.VolumePoints01[trail.VolumePoints01.Count - 1], volume01) > 1e-5f)
        {
            trail.VolumePoints01.Add(volume01);
        }

        if (_projector != null &&
            _projector.TryProjectVolume01ToRawImageLocal(trail.Pane, volume01, out var localPoint) &&
            (trail.UiPoints.Count == 0 ||
             Vector2.Distance(trail.UiPoints[trail.UiPoints.Count - 1], localPoint) > 0.01f))
        {
            trail.UiPoints.Add(localPoint);
            if (trail.UiLine != null)
            {
                trail.UiLine.gameObject.SetActive(true);
                trail.UiLine.AddPoint(localPoint);
            }
        }

        if (trail.VolumeLine != null)
        {
            ResolvePaneContextResolver();
            if (_paneContextResolver != null &&
                _paneContextResolver.TryVolume01ToWorld(trail.Pane, volume01, out var worldPosition) &&
                (trail.VolumePoints.Count == 0 ||
                 Vector3.Distance(trail.VolumePoints[trail.VolumePoints.Count - 1], worldPosition) > 1e-5f))
            {
                trail.VolumePoints.Add(worldPosition);
                trail.VolumeLine.positionCount = trail.VolumePoints.Count;
                trail.VolumeLine.SetPosition(trail.VolumePoints.Count - 1, worldPosition);
            }
        }
    }

    private void ApplyVolumeLinePositionsFromMetadata(LineRenderer volumeLine, in PaneLineVolumeMetadata metadata)
    {
        if (volumeLine == null || metadata.VolumePoints01 == null)
        {
            return;
        }

        ResolvePaneContextResolver();
        if (_paneContextResolver == null)
        {
            return;
        }

        var worldPoints = new List<Vector3>(metadata.VolumePoints01.Length);
        for (var i = 0; i < metadata.VolumePoints01.Length; ++i)
        {
            if (_paneContextResolver.TryVolume01ToWorld(metadata.Pane, metadata.VolumePoints01[i], out var worldPoint))
            {
                worldPoints.Add(worldPoint);
            }
        }

        if (worldPoints.Count < 2)
        {
            return;
        }

        volumeLine.positionCount = worldPoints.Count;
        for (var i = 0; i < worldPoints.Count; ++i)
        {
            volumeLine.SetPosition(i, worldPoints[i]);
        }
    }

    private void RefreshCompletedMarker(CompletedPaneMarker marker)
    {
        if (marker?.UiLine == null)
        {
            return;
        }

        if (!IsMarkerVisibleForActiveFile(marker.FileIndex))
        {
            SetUiLineActive(marker.UiLine, false);
            SetVolumeLineActive(marker.VolumeLine, false);
            marker.UiLine.ClearPoints();
            return;
        }

        ApplyProjectedUiPoints(marker.UiLine, marker.Pane, marker.DrawnSliceCoordinate, marker.VolumePoints01);
        SetVolumeLineActive(marker.VolumeLine, HasRenderableVolumeLine(marker.VolumeLine));
    }

    private void RefreshActiveTrail(ActiveDragTrail trail, RectTransform rawImage)
    {
        if (trail?.UiLine == null)
        {
            return;
        }

        if (!IsMarkerVisibleForActiveFile(trail.FileIndex))
        {
            SetUiLineActive(trail.UiLine, false);
            SetVolumeLineActive(trail.VolumeLine, false);
            trail.UiLine.ClearPoints();
            return;
        }

        ApplyProjectedUiPoints(trail.UiLine, trail.Pane, trail.DrawnSliceCoordinate, trail.VolumePoints01);
        SetVolumeLineActive(trail.VolumeLine, HasRenderableVolumeLine(trail.VolumeLine));
    }

    private void RefreshRemoteDragTrail(RemoteDragTrailBinding binding, PaneLineDragSessionId sessionId)
    {
        if (binding?.Trail?.UiLine == null)
        {
            return;
        }

        RefreshActiveTrail(binding.Trail, binding.RawImage);
    }

    private bool IsMarkerVisibleForActiveFile(int fileIndex)
    {
        return _activeFileIndex >= 0 && fileIndex == _activeFileIndex;
    }

    private static bool HasRenderableVolumeLine(LineRenderer volumeLine)
    {
        return volumeLine != null && volumeLine.positionCount >= 2;
    }

    private static void SetUiLineActive(UILineGraphic uiLine, bool visible)
    {
        if (uiLine != null)
        {
            uiLine.gameObject.SetActive(visible);
        }
    }

    private static void SetVolumeLineActive(LineRenderer volumeLine, bool visible)
    {
        if (volumeLine != null)
        {
            volumeLine.gameObject.SetActive(visible);
        }
    }

    private void ApplyProjectedUiPoints(
        UILineGraphic uiLine,
        MarkerPane pane,
        float drawnSliceCoordinate,
        IReadOnlyList<Vector3> volumePoints01)
    {
        if (uiLine == null)
        {
            return;
        }

        if (_projector == null ||
            !_projector.IsMarkerVisibleOnCurrentSlice(pane, drawnSliceCoordinate))
        {
            uiLine.gameObject.SetActive(false);
            uiLine.ClearPoints();
            return;
        }

        uiLine.gameObject.SetActive(true);

        var projectedPoints = new List<Vector2>(volumePoints01.Count);
        for (var i = 0; i < volumePoints01.Count; ++i)
        {
            if (_projector.TryProjectVolume01ToRawImageLocal(pane, volumePoints01[i], out var localPoint))
            {
                projectedPoints.Add(localPoint);
            }
        }

        if (projectedPoints.Count >= 2)
        {
            uiLine.SetPoints(projectedPoints);
        }
        else
        {
            uiLine.ClearPoints();
        }
    }

    private PaneLineStyle CreatePaneLineStyle()
    {
        return new PaneLineStyle
        {
            Color = _activePaneLineColor,
            UiWidthPixels = paneLineWidthPixels,
            UiLocalDepth = paneLineLocalDepth,
            VolumeWidth = volumeLineWidth,
            VolumeLineLayer = volumeLineLayer
        };
    }

    private bool CanDrawLines(RectTransform rawImage)
    {
        return rawImage != null && _isPenModeActive && _hasSelectedColor;
    }

    private void BindMarkerButtons()
    {
        if (penButton != null)
        {
            penButton.onClick.AddListener(OnPenButtonToggleClicked);
        }

        if (redButton != null)
        {
            redButton.onClick.AddListener(OnRedButtonClicked);
        }

        if (purpleButton != null)
        {
            purpleButton.onClick.AddListener(OnPurpleButtonClicked);
        }

        if (eraseButton != null)
        {
            eraseButton.onClick.AddListener(OnEraseButtonClicked);
        }
    }

    private void UnbindMarkerButtons()
    {
        if (penButton != null)
        {
            penButton.onClick.RemoveListener(OnPenButtonToggleClicked);
        }

        if (redButton != null)
        {
            redButton.onClick.RemoveListener(OnRedButtonClicked);
        }

        if (purpleButton != null)
        {
            purpleButton.onClick.RemoveListener(OnPurpleButtonClicked);
        }

        if (eraseButton != null)
        {
            eraseButton.onClick.RemoveListener(OnEraseButtonClicked);
        }
    }

    private void ResetMarkerToolState()
    {
        _isPenModeActive = false;
        _hasSelectedColor = false;
        SetColorButtonsInteractable(false);
    }

    private void OnPenButtonToggleClicked()
    {
        if (_isPenModeActive)
        {
            OffPenButtonClicked();
        }
        else
        {
            OnPenButtonClicked();
        }
    }

    private void OnPenButtonClicked()
    {
        _isPenModeActive = true;
        SetColorButtonsInteractable(true);
    }

    private void OffPenButtonClicked()
    {
        _isPenModeActive = false;
        SetColorButtonsInteractable(false);
    }

    private void OnRedButtonClicked()
    {
        if (!_isPenModeActive)
        {
            return;
        }

        _activePaneLineColor = _redColor;
        _hasSelectedColor = true;
    }

    private void OnPurpleButtonClicked()
    {
        if (!_isPenModeActive)
        {
            return;
        }

        _activePaneLineColor = _purpleColor;
        _hasSelectedColor = true;
    }

    private void OnEraseButtonClicked()
    {
        EraseMostRecentMarker();
    }

    private void EraseMostRecentMarker()
    {
        if (_completedMarkers.Count == 0)
        {
            return;
        }

        var marker = _completedMarkers[_completedMarkers.Count - 1];
        _completedMarkers.RemoveAt(_completedMarkers.Count - 1);

        DestroyLineObject(marker.UiLine != null ? marker.UiLine.gameObject : null);
        DestroyLineObject(marker.VolumeLine != null ? marker.VolumeLine.gameObject : null);
    }

    private void SetColorButtonsInteractable(bool interactable)
    {
        if (redButton != null)
        {
            redButton.interactable = interactable;
        }

        if (purpleButton != null)
        {
            purpleButton.interactable = interactable;
        }
    }

    private ActiveDragTrail GetOrCreateActiveDragTrail(
        RectTransform rawImage,
        Transform volumeLineAnchor,
        MarkerPane pane,
        float drawnSliceCoordinate,
        int fileIndex)
    {
        if (!_activeDragTrails.TryGetValue(rawImage, out var trail))
        {
            trail = new ActiveDragTrail
            {
                Pane = pane,
                DrawnSliceCoordinate = drawnSliceCoordinate,
                FileIndex = fileIndex
            };
            _activeDragTrails[rawImage] = trail;
        }
        else
        {
            trail.Pane = pane;
            trail.DrawnSliceCoordinate = drawnSliceCoordinate;
            trail.FileIndex = fileIndex;
        }

        EnsureTrailRenderers(trail, rawImage, volumeLineAnchor);
        return trail;
    }

    private void EnsureTrailRenderers(
        ActiveDragTrail trail,
        RectTransform rawImage,
        Transform volumeLineAnchor)
    {
        EnsureTrailRenderers(trail, rawImage, volumeLineAnchor, CreatePaneLineStyle());
    }

    private void EnsureTrailRenderers(
        ActiveDragTrail trail,
        RectTransform rawImage,
        Transform volumeLineAnchor,
        PaneLineStyle style)
    {
        if (trail.UiLine == null)
        {
            trail.UiLine = CreateUiLine(rawImage, style);
        }
        else
        {
            ApplyUiLineStyle(trail.UiLine, style);
        }

        if (volumeLineAnchor == null)
        {
            return;
        }

        if (trail.VolumeLine == null)
        {
            trail.VolumeLine = CreateVolumeLine(volumeLineAnchor, style, trail.FileIndex);
        }
        else
        {
            ApplyVolumeLineStyle(trail.VolumeLine, style);
        }
    }

    private bool AppendTrailPoint(
        ActiveDragTrail trail,
        RectTransform rawImage,
        Camera eventCamera,
        Vector2 screenPosition,
        Vector3 volume01,
        Vector3 volumeWorldPosition)
    {
        var addedVolumePoint = false;
        if (trail.VolumePoints01.Count == 0 ||
            Vector3.Distance(trail.VolumePoints01[trail.VolumePoints01.Count - 1], volume01) > 1e-5f)
        {
            trail.VolumePoints01.Add(volume01);
            addedVolumePoint = true;
        }

        Vector2 localPoint = default;
        var hasProjectedPoint = _projector != null &&
            _projector.TryProjectVolume01ToRawImageLocal(trail.Pane, volume01, out localPoint);

        if (!hasProjectedPoint)
        {
            var camera = ResolveEventCamera(rawImage, eventCamera);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    rawImage,
                    screenPosition,
                    camera,
                    out localPoint))
            {
                hasProjectedPoint = true;
            }
            else if (logPlacementFailures)
            {
                Debug.LogWarning(
                    $"[MarkerManager] Failed to convert screen point {screenPosition} to RawImage local space. camera={(camera != null ? camera.name : "null")}",
                    this);
            }
        }

        if (hasProjectedPoint &&
            (trail.UiPoints.Count == 0 ||
             Vector2.Distance(trail.UiPoints[trail.UiPoints.Count - 1], localPoint) > 0.01f))
        {
            trail.UiPoints.Add(localPoint);
            if (trail.UiLine != null)
            {
                trail.UiLine.gameObject.SetActive(true);
                trail.UiLine.AddPoint(localPoint);
            }
        }

        if (trail.VolumeLine != null)
        {
            if (trail.VolumePoints.Count == 0 ||
                Vector3.Distance(trail.VolumePoints[trail.VolumePoints.Count - 1], volumeWorldPosition) > 1e-5f)
            {
                trail.VolumePoints.Add(volumeWorldPosition);
                trail.VolumeLine.positionCount = trail.VolumePoints.Count;
                trail.VolumeLine.SetPosition(trail.VolumePoints.Count - 1, volumeWorldPosition);
            }
        }

        return addedVolumePoint;
    }

    private UILineGraphic CreateUiLine(RectTransform rawImage, PaneLineStyle style)
    {
        var lineObject = new GameObject("PaneDragLine_UI");
        lineObject.transform.SetParent(rawImage, false);

        var rectTransform = lineObject.AddComponent<RectTransform>();
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;
        rectTransform.localPosition = new Vector3(0f, 0f, style.UiLocalDepth);

        var graphic = lineObject.AddComponent<UILineGraphic>();
        ApplyUiLineStyle(graphic, style);
        SetLayerRecursively(lineObject, rawImage.gameObject.layer);
        TrackLineObject(lineObject);
        return graphic;
    }

    private LineRenderer CreateVolumeLine(Transform volumeLineAnchor, PaneLineStyle style, int fileIndex)
    {
        var lineObject = new GameObject($"PaneDragLine_Volume_{fileIndex}");
        lineObject.transform.SetParent(volumeLineAnchor, false);

        var lineRenderer = lineObject.AddComponent<LineRenderer>();
        ApplyVolumeLineStyle(lineRenderer, style);
        SetLayerRecursively(lineObject, style.VolumeLineLayer);
        TrackLineObject(lineObject);
        return lineRenderer;
    }

    private void ApplyUiLineStyle(UILineGraphic graphic, PaneLineStyle style)
    {
        if (graphic == null)
        {
            return;
        }

        EnsureUiLineMaterial();
        if (_uiLineMaterial != null)
        {
            graphic.material = _uiLineMaterial;
        }

        graphic.color = style.Color;
        graphic.Thickness = style.UiWidthPixels;
        graphic.raycastTarget = false;
    }

    private void ApplyVolumeLineStyle(LineRenderer lineRenderer, PaneLineStyle style)
    {
        if (lineRenderer == null)
        {
            return;
        }

        EnsureVolumeLineMaterial();
        if (_volumeLineMaterial != null)
        {
            var materialInstance = new Material(_volumeLineMaterial)
            {
                hideFlags = HideFlags.DontSave
            };
            ApplyVolumeLineMaterialColor(materialInstance, style.Color);
            lineRenderer.material = materialInstance;
        }

        lineRenderer.useWorldSpace = true;
        lineRenderer.alignment = LineAlignment.View;
        lineRenderer.loop = false;
        lineRenderer.textureMode = LineTextureMode.Stretch;
        lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lineRenderer.receiveShadows = false;
        lineRenderer.numCornerVertices = 4;
        lineRenderer.numCapVertices = 4;
        lineRenderer.widthMultiplier = style.VolumeWidth;
    }

    private void EnsureVolumeLineMaterial()
    {
        if (_volumeLineMaterial != null)
        {
            return;
        }

        var shader = FindVolumeLineShader();
        if (shader == null)
        {
            return;
        }

        _volumeLineMaterial = CreateDoubleSidedPaneLineMaterial(shader, "MarkerManager_VolumeLineMaterial");
    }

    private void EnsureUiLineMaterial()
    {
        if (_uiLineMaterial != null)
        {
            return;
        }

        var shader = FindUiLineShader();
        if (shader == null)
        {
            return;
        }

        _uiLineMaterial = CreateDoubleSidedPaneLineMaterial(shader, "MarkerManager_UiLineMaterial");
    }

    private static Shader FindVolumeLineShader()
    {
        return Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Unlit/Color");
    }

    private static Shader FindUiLineShader()
    {
        return Shader.Find("UI/Default")
            ?? Shader.Find("Universal Render Pipeline/UI/Default")
            ?? Shader.Find("Sprites/Default");
    }

    private static void ApplyVolumeLineMaterialColor(Material material, Color color)
    {
        if (material == null)
        {
            return;
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }
    }

    private static Material CreateDoubleSidedPaneLineMaterial(Shader shader, string materialName)
    {
        var material = new Material(shader)
        {
            name = materialName,
            hideFlags = HideFlags.DontSave
        };

        ApplyDoubleSidedMaterialSettings(material);
        return material;
    }

    private static void ApplyDoubleSidedMaterialSettings(Material material)
    {
        if (material == null)
        {
            return;
        }

        if (material.HasProperty("_Cull"))
        {
            material.SetInt("_Cull", (int)CullMode.Off);
        }

        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_Blend"))
        {
            material.SetFloat("_Blend", 0f);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 0f);
        }

        material.renderQueue = (int)RenderQueue.Transparent;
    }

    private void DestroyLineMaterials()
    {
        DestroyLineMaterial(ref _volumeLineMaterial);
        DestroyLineMaterial(ref _uiLineMaterial);
    }

    private static void DestroyLineMaterial(ref Material material)
    {
        if (material == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(material);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(material);
        }

        material = null;
    }

    private void ClearPaneTrail(RectTransform rawImage)
    {
        if (rawImage == null)
        {
            return;
        }

        if (!_activeDragTrails.TryGetValue(rawImage, out var trail))
        {
            return;
        }

        DestroyTrailRenderers(trail);
        _activeDragTrails.Remove(rawImage);
    }

    private void DestroyTrailRenderers(ActiveDragTrail trail)
    {
        if (trail == null)
        {
            return;
        }

        DestroyLineObject(trail.UiLine != null ? trail.UiLine.gameObject : null);
        DestroyLineObject(trail.VolumeLine != null ? trail.VolumeLine.gameObject : null);
        trail.UiLine = null;
        trail.VolumeLine = null;
        trail.UiPoints.Clear();
        trail.VolumePoints01.Clear();
        trail.VolumePoints.Clear();
        trail.HasLastScreenPosition = false;
    }

    private void DestroyLineObject(GameObject lineObject)
    {
        if (lineObject == null)
        {
            return;
        }

        _spawnedLineObjects.Remove(lineObject);

        if (Application.isPlaying)
        {
            Destroy(lineObject);
        }
        else
        {
            DestroyImmediate(lineObject);
        }
    }

    private static Camera ResolveEventCamera(RectTransform rawImage, Camera eventCamera)
    {
        if (eventCamera != null)
        {
            return eventCamera;
        }

        if (rawImage == null)
        {
            return Camera.main;
        }

        var canvas = rawImage.GetComponentInParent<Canvas>();
        if (canvas == null)
        {
            return Camera.main;
        }

        if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            return null;
        }

        return canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
    }

    private static void SetLayerRecursively(GameObject target, int layer)
    {
        if (target == null)
        {
            return;
        }

        target.layer = layer;
        for (var i = 0; i < target.transform.childCount; ++i)
        {
            SetLayerRecursively(target.transform.GetChild(i).gameObject, layer);
        }
    }

    private void TrackLineObject(GameObject lineObject)
    {
        if (lineObject != null)
        {
            _spawnedLineObjects.Add(lineObject);
        }
    }
}
