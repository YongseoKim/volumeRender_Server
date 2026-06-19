using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class MarkerManager : MonoBehaviour
{
    [Header("Pane Line Visuals")]
    [SerializeField] private bool showPaneLineVisuals = true;
    [SerializeField, Range(2f, 32f)] private float lineDragMinPointSpacingPixels = 6f;
    [SerializeField, Range(1f, 12f)] private float paneLineWidthPixels = 4f;
    [SerializeField, Range(0.0005f, 0.02f)] private float volumeLineWidth = 0.003f;
    [SerializeField] private Color paneLineColor = new Color(1f, 0.35f, 0.35f, 0.95f);
    [SerializeField] private float paneLineLocalDepth = -1f;
    [SerializeField] private int volumeLineLayer = 29;

    [SerializeField] private bool logPlacementFailures;

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
        public UILineGraphic UiLine;
        public LineRenderer VolumeLine;
        public readonly List<Vector2> UiPoints = new List<Vector2>();
        public readonly List<Vector3> VolumePoints = new List<Vector3>();
        public Vector2 LastScreenPosition;
        public bool HasLastScreenPosition;
    }

    private readonly List<GameObject> _spawnedLineObjects = new List<GameObject>();
    private readonly Dictionary<RectTransform, ActiveDragTrail> _activeDragTrails =
        new Dictionary<RectTransform, ActiveDragTrail>();

    private Material _volumeLineMaterial;
    private Material _uiLineMaterial;

    private void Awake()
    {
        ClearMarkers();
    }

    private void OnDestroy()
    {
        ClearMarkers();
        DestroyLineMaterials();
    }

    private void OnApplicationQuit()
    {
        ClearMarkers();
    }

    public void BeginDragMarkers(
        RectTransform rawImage,
        Transform volumeLineAnchor,
        Camera eventCamera,
        Vector2 screenPosition,
        Vector3 volumeWorldPosition)
    {
        if (!showPaneLineVisuals || !CanDrawLines(rawImage))
        {
            return;
        }

        ClearPaneTrail(rawImage);
        var trail = GetOrCreateActiveDragTrail(rawImage, volumeLineAnchor);
        AppendTrailPoint(trail, rawImage, eventCamera, screenPosition, volumeWorldPosition);
        trail.LastScreenPosition = screenPosition;
        trail.HasLastScreenPosition = true;
    }

    public void UpdateDragMarkers(
        RectTransform rawImage,
        Transform volumeLineAnchor,
        Camera eventCamera,
        Vector2 screenPosition,
        Vector3 volumeWorldPosition)
    {
        if (!showPaneLineVisuals || !CanDrawLines(rawImage))
        {
            return;
        }

        if (!_activeDragTrails.TryGetValue(rawImage, out var trail))
        {
            BeginDragMarkers(rawImage, volumeLineAnchor, eventCamera, screenPosition, volumeWorldPosition);
            return;
        }

        if (trail.HasLastScreenPosition &&
            Vector2.Distance(screenPosition, trail.LastScreenPosition) < lineDragMinPointSpacingPixels)
        {
            return;
        }

        EnsureTrailRenderers(trail, rawImage, volumeLineAnchor);
        AppendTrailPoint(trail, rawImage, eventCamera, screenPosition, volumeWorldPosition);
        trail.LastScreenPosition = screenPosition;
        trail.HasLastScreenPosition = true;
    }

    public void EndDragMarkers(RectTransform rawImage)
    {
        if (rawImage == null)
        {
            return;
        }

        _activeDragTrails.Remove(rawImage);
    }

    public void ClearMarkers()
    {
        _activeDragTrails.Clear();

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

    private PaneLineStyle CreatePaneLineStyle()
    {
        return new PaneLineStyle
        {
            Color = paneLineColor,
            UiWidthPixels = paneLineWidthPixels,
            UiLocalDepth = paneLineLocalDepth,
            VolumeWidth = volumeLineWidth,
            VolumeLineLayer = volumeLineLayer
        };
    }

    private static bool CanDrawLines(RectTransform rawImage)
    {
        return rawImage != null;
    }

    private ActiveDragTrail GetOrCreateActiveDragTrail(RectTransform rawImage, Transform volumeLineAnchor)
    {
        if (!_activeDragTrails.TryGetValue(rawImage, out var trail))
        {
            trail = new ActiveDragTrail();
            _activeDragTrails[rawImage] = trail;
        }

        EnsureTrailRenderers(trail, rawImage, volumeLineAnchor);
        return trail;
    }

    private void EnsureTrailRenderers(
        ActiveDragTrail trail,
        RectTransform rawImage,
        Transform volumeLineAnchor)
    {
        var style = CreatePaneLineStyle();

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
            trail.VolumeLine = CreateVolumeLine(volumeLineAnchor, style);
        }
        else
        {
            ApplyVolumeLineStyle(trail.VolumeLine, style);
        }
    }

    private void AppendTrailPoint(
        ActiveDragTrail trail,
        RectTransform rawImage,
        Camera eventCamera,
        Vector2 screenPosition,
        Vector3 volumeWorldPosition)
    {
        var camera = ResolveEventCamera(rawImage, eventCamera);
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rawImage,
                screenPosition,
                camera,
                out var localPoint))
        {
            localPoint = new Vector2(localPoint.x, localPoint.y);
            if (trail.UiPoints.Count == 0 || Vector2.Distance(trail.UiPoints[trail.UiPoints.Count - 1], localPoint) > 0.01f)
            {
                trail.UiPoints.Add(localPoint);
                if (trail.UiLine != null)
                {
                    trail.UiLine.AddPoint(localPoint);
                }
            }
        }
        else if (logPlacementFailures)
        {
            Debug.LogWarning(
                $"[MarkerManager] Failed to convert screen point {screenPosition} to RawImage local space. camera={(camera != null ? camera.name : "null")}",
                this);
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

    private LineRenderer CreateVolumeLine(Transform volumeLineAnchor, PaneLineStyle style)
    {
        var lineObject = new GameObject("PaneDragLine_Volume");
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
            ApplyVolumeLineMaterialColor(_volumeLineMaterial, style.Color);
            lineRenderer.sharedMaterial = _volumeLineMaterial;
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
            Object.Destroy(material);
        }
        else
        {
            Object.DestroyImmediate(material);
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
