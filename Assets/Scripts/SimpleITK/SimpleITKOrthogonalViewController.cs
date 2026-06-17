using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace SimpleITKUnity
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-10)]
    public class SimpleITKOrthogonalViewController : MonoBehaviour
    {
        private const int SliceLayerA = 26;
        private const int SliceLayerB = 27;
        private const int SliceLayerC = 28;
        private const int IndicatorLayer = 29;
        private const float Initial3DViewYawDegrees = -90f;
        private const float Initial3DViewPitchDegrees = 60f;
        private const float Initial3DViewRollDegrees = 90f;
        private const float SlicePaneRotationA = -90f;
        private const float SlicePaneRotationB = 180f;
        private const float SlicePaneRotationC = 180f;
        private static readonly Color PlaneColorA = new Color(1f, 0.35f, 0.35f, 1f);
        private static readonly Color PlaneColorB = new Color(0.35f, 1f, 0.35f, 1f);
        private static readonly Color PlaneColorC = new Color(0.35f, 0.75f, 1f, 1f);
        private const int AutoFitFullScanVoxelBudget = 8 * 1024 * 1024;
        private const int AutoFitTargetSamplesPerAxis = 128;
        private static readonly Vector3[] IndicatorLocalPoints =
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(-0.5f, -0.5f, 0f)
        };

        [Header("Target")]
        [SerializeField] private SimpleITKVolumeRenderer targetRenderer;

        [Header("Slice Rendering")]
        [SerializeField] private Shader sliceShader;
        [SerializeField] private bool use2DTransferFunctionForSlices = false;

        [Header("ROI")]
        [SerializeField] private bool enableRoiBox = true;
        [SerializeField] private Vector3 roiStart01 = new Vector3(0.0f, 0.0f, 0.0f);
        [SerializeField] private Vector3 roiEnd01 = new Vector3(1.0f, 1.0f, 1.0f);
        private const bool keepInsideRoi = true;
        [SerializeField] private bool autoFitRoiOnLoad = false;
        [SerializeField, Range(0f, 0.1f)] private float autoFitThreshold = 0.01f;
        [SerializeField] private Color roiMarkerColor = new Color(1.0f, 0.92f, 0.18f, 1.0f);
        [SerializeField, Range(6f, 72f)] private float roiHandleSizePixels = 40f;
        [SerializeField, Range(1f, 12f)] private float roiLineThicknessPixels = 6f;
        [Tooltip("Play mode: draws grab zones, content rect, RawImage bounds, and mouse hit-test position.")]
        [SerializeField] private bool debugRoiHitAreas = false;
        [Header("ROI (UI Handles)")]
        [Tooltip("If enabled, ROI handle interaction is driven by UI prefabs (EventSystem) instead of OnGUI + Input.mousePosition.")]
        [SerializeField] private bool useUiRoiHandles = false;
        [SerializeField] private RectTransform roiOverlayA;
        [SerializeField] private RectTransform roiHandleStartA;
        [SerializeField] private RectTransform roiHandleEndA;
        [SerializeField] private RectTransform roiOverlayB;
        [SerializeField] private RectTransform roiHandleStartB;
        [SerializeField] private RectTransform roiHandleEndB;
        [SerializeField] private RectTransform roiOverlayC;
        [SerializeField] private RectTransform roiHandleStartC;
        [SerializeField] private RectTransform roiHandleEndC;


        [Header("Slice Position (0..1)")]
        [SerializeField, Range(0f, 1f)] private float sliceX = 0.5f;
        [SerializeField, Range(0f, 1f)] private float sliceY = 0.5f;
        [SerializeField, Range(0f, 1f)] private float sliceZ = 0.5f;

        [Header("Slice Scale")]
        [SerializeField, Range(0.2f, 2.5f)] private float scaleX = 1.0f;
        [SerializeField, Range(0.2f, 2.5f)] private float scaleY = 1.0f;
        [SerializeField, Range(0.2f, 2.5f)] private float scaleZ = 1.0f;

        [Header("3D Plane Overlay")]
        [SerializeField, Range(0.05f, 1f)] private float planeIndicatorOpacity = 0.85f;

        [Header("Input")]
        private const bool enableInput = true;
        private const bool requirePlayModeForInput = false;
        [SerializeField, Range(0.2f, 6f)] private float globalZoom = 1.0f;
        [SerializeField, Range(0.1f, 2.5f)] private float minGlobalZoom = 0.5f;
        [SerializeField, Range(0.5f, 8f)] private float maxGlobalZoom = 3.0f;
        [SerializeField, Range(0.1f, 8f)] private float globalZoomSpeed = 1.8f;
        [SerializeField, Range(0.05f, 2.5f)] private float mouseWheelZoomSensitivity = 0.35f;
        [SerializeField, Range(0.001f, 0.2f)] private float moveStep = 0.02f;
        [SerializeField, Range(0.01f, 0.5f)] private float scaleStep = 0.06f;
        [SerializeField, Range(0.2f, 3.0f)] private float minScale = 0.2f;
        [SerializeField, Range(0.5f, 4.0f)] private float maxScale = 2.5f;

        [Header("Cameras")]
        [SerializeField] private Camera cameraA;
        [SerializeField] private Camera cameraB;
        [SerializeField] private Camera cameraC;
        [SerializeField] private Camera camera3D;
        [SerializeField] private Camera3DProjection camera3DProjection = Camera3DProjection.Orthographic;
        [SerializeField, Range(20f, 90f)] private float camera3DFov = 50f;
        [SerializeField, Range(1.2f, 6f)] private float camera3DDistanceFactor = 2.8f;

        [Header("Slice UI Output")]
        [SerializeField] private RawImage rawImageA;
        [SerializeField] private RawImage rawImageB;
        [SerializeField] private RawImage rawImageC;
        [SerializeField] private RawImage rawImage3D;


        private Transform _mprRoot;
        private Transform _cameraRoot;

        private MprPlane _planeA;
        private MprPlane _planeB;
        private MprPlane _planeC;

        private Material _indicatorMaterial;
        private Pane _activeRoiPane = Pane.None;
        private RoiHandle _activeRoiHandle = RoiHandle.None;
        private bool _isRoiTouchDragging;
        private int _activeRoiTouchFingerId = -1;
        private bool _isTouchPinchZooming;
        private float _touchPinchStartDistance = 1f;
        private float _touchPinchStartZoom = 1f;
        private bool _runtimeVolumeLoadRequested;
        private Quaternion _initialVolumeLocalRotation;
        private Quaternion _initialVolumeWorldRotation;
        private Vector3    _initialVolumeLocalScale;
        private Vector3    _initialRoiStart;
        private Vector3    _initialRoiEnd;
        private float      _initialGlobalZoom;
        private float      _initialSliceX;
        private float      _initialSliceY;
        private float      _initialSliceZ;
        private bool       _initialVolumeTransformCaptured;
        private Texture3D _capturedInitialVolumeTexture;
        private RenderTexture _sliceRenderTextureA;
        private RenderTexture _sliceRenderTextureB;
        private RenderTexture _sliceRenderTextureC;
        private RenderTexture _renderTexture3D;

        [Flags]
        private enum MprApplyDirty
        {
            None = 0,
            VolumeMeshState = 1 << 0,
            Roi = 1 << 1,
            PlaneTransforms = 1 << 2,
            CameraLayout = 1 << 3,
            CameraPoses = 1 << 4,
            MprRootSync = 1 << 5,
            SliceRenderers = 1 << 6,
            All = ~0
        }

        private MprApplyDirty _applyDirty = MprApplyDirty.All;
        private bool _setupDirty = true;
        private int _cachedVolumeTextureId;
        private bool _cachedVolumeMeshEnabled;
        private Vector3 _cachedTargetLocalPosition;
        private Quaternion _cachedTargetLocalRotation;
        private Vector3 _cachedTargetLocalScale;
        private Vector2Int _cachedRawImageSizeA = new Vector2Int(int.MinValue, int.MinValue);
        private Vector2Int _cachedRawImageSizeB = new Vector2Int(int.MinValue, int.MinValue);
        private Vector2Int _cachedRawImageSizeC = new Vector2Int(int.MinValue, int.MinValue);
        private Vector2Int _cachedRawImageSize3D = new Vector2Int(int.MinValue, int.MinValue);
#if UNITY_EDITOR
        private bool _editorValidateRefreshQueued;
#endif

        private const string MprRootName = "MPRView";
        private const string LegacyMprRootName = "SimpleITK_MPR";
        private const string CameraRootName = "Cameras";
        private const string LegacyCameraRootName = "SimpleITK_MPR_Cameras";
        private const string LegacySlicePlaneName = "SimpleITK_SlicePlane";
        private const KeyCode ZoomInKey = KeyCode.E;
        private const KeyCode ZoomOutKey = KeyCode.Q;
        private static readonly Rect FullPaneViewportRect = new Rect(0f, 0f, 1f, 1f);

        private enum Pane
        {
            None,
            A,
            B,
            C,
            D3
        }

        public enum UiPane
        {
            A,
            B,
            C
        }

        public enum Camera3DProjection
        {
            Orthographic,
            Perspective
        }

        private enum RoiHandle
        {
            None,
            Start,
            End
        }

        private class MprPlane
        {
            public Transform Anchor;
            public Transform SliceObject;
            public SimpleITKSliceRenderer SliceRenderer;
            public LineRenderer Indicator;
            public int SliceLayer;
            public Color Color;
        }

        private void Reset()
        {
            targetRenderer = ResolveTargetInHierarchy();
        }

        private void OnEnable()
        {
            _runtimeVolumeLoadRequested = false;
            _setupDirty = true;
            _applyDirty = MprApplyDirty.All;
            ResolveTarget();
            EnsureSetup();
            _setupDirty = false;
            ApplyAll();
            CaptureInitialVolumeTransformIfNeeded();
        }

        private void Update()
        {
            ResolveTarget();
            if (_setupDirty)
            {
                EnsureSetup();
                _setupDirty = false;
                MarkApplyDirty(MprApplyDirty.All);
            }

            CaptureInitialVolumeTransformIfNeeded();
            PollDynamicApplyDirtyState();

            var zoomBefore = globalZoom;
            HandleInput();
            if (!Mathf.Approximately(zoomBefore, globalZoom))
            {
                MarkApplyDirty(MprApplyDirty.CameraPoses);
            }

            if (_applyDirty != MprApplyDirty.None)
            {
                ApplyAll();
            }

            SyncUiRoiHandles();
        }

        /// <summary>Called after volume data is applied so MPR views refresh once.</summary>
        public void NotifyVolumeDataApplied()
        {
            MarkApplyDirty(MprApplyDirty.All);
        }

        private void MarkApplyDirty(MprApplyDirty flags)
        {
            _applyDirty |= flags;
        }

        private void PollDynamicApplyDirtyState()
        {
            if (targetRenderer == null)
            {
                return;
            }

            var volumeTexture = targetRenderer.GetVolumeTexture();
            var textureId = volumeTexture != null ? volumeTexture.GetInstanceID() : 0;
            if (textureId != _cachedVolumeTextureId)
            {
                _cachedVolumeTextureId = textureId;
                MarkApplyDirty(
                    MprApplyDirty.VolumeMeshState |
                    MprApplyDirty.SliceRenderers |
                    MprApplyDirty.Roi |
                    MprApplyDirty.CameraPoses);
            }

            var volumeMeshEnabled = targetRenderer.IsVolumeMeshRenderingEnabled;
            if (volumeMeshEnabled != _cachedVolumeMeshEnabled)
            {
                _cachedVolumeMeshEnabled = volumeMeshEnabled;
                MarkApplyDirty(MprApplyDirty.VolumeMeshState);
            }

            var targetTransform = targetRenderer.transform;
            if (targetTransform.localPosition != _cachedTargetLocalPosition ||
                targetTransform.localRotation != _cachedTargetLocalRotation ||
                targetTransform.localScale != _cachedTargetLocalScale)
            {
                _cachedTargetLocalPosition = targetTransform.localPosition;
                _cachedTargetLocalRotation = targetTransform.localRotation;
                _cachedTargetLocalScale = targetTransform.localScale;
                MarkApplyDirty(
                    MprApplyDirty.MprRootSync |
                    MprApplyDirty.CameraPoses |
                    MprApplyDirty.SliceRenderers);
            }

            PollRawImageLayoutDirty(rawImageA, ref _cachedRawImageSizeA);
            PollRawImageLayoutDirty(rawImageB, ref _cachedRawImageSizeB);
            PollRawImageLayoutDirty(rawImageC, ref _cachedRawImageSizeC);
            PollRawImageLayoutDirty(rawImage3D, ref _cachedRawImageSize3D);
        }

        private void PollRawImageLayoutDirty(RawImage rawImage, ref Vector2Int cachedSize)
        {
            if (rawImage == null)
            {
                return;
            }

            var size = GetRawImagePixelSize(rawImage);
            if (size == cachedSize)
            {
                return;
            }

            cachedSize = size;
            MarkApplyDirty(MprApplyDirty.CameraLayout);
        }

        private void OnValidate()
        {
            roiStart01 = Clamp01Vector(roiStart01);
            roiEnd01 = Clamp01Vector(roiEnd01);

            sliceX = Mathf.Clamp01(sliceX);
            sliceY = Mathf.Clamp01(sliceY);
            sliceZ = Mathf.Clamp01(sliceZ);

            minGlobalZoom = Mathf.Max(0.1f, minGlobalZoom);
            maxGlobalZoom = Mathf.Max(minGlobalZoom + 0.01f, maxGlobalZoom);
            globalZoom = Mathf.Clamp(globalZoom, minGlobalZoom, maxGlobalZoom);
            mouseWheelZoomSensitivity = Mathf.Clamp(mouseWheelZoomSensitivity, 0.05f, 2.5f);

            scaleX = Mathf.Clamp(scaleX, minScale, maxScale);
            scaleY = Mathf.Clamp(scaleY, minScale, maxScale);
            scaleZ = Mathf.Clamp(scaleZ, minScale, maxScale);
            planeIndicatorOpacity = Mathf.Clamp(planeIndicatorOpacity, 0.05f, 1f);

            ResolveTarget();
            if (Application.isPlaying)
            {
                _setupDirty = true;
                MarkApplyDirty(MprApplyDirty.All);
            }
#if UNITY_EDITOR
            else
            {
                QueueEditorRefreshAfterValidate();
            }
#endif
        }

        private void OnDisable()
        {
            if (_indicatorMaterial != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_indicatorMaterial);
                }
                else
                {
                    DestroyImmediate(_indicatorMaterial);
                }

                _indicatorMaterial = null;
            }

            ReleaseSliceRenderTexture(ref _sliceRenderTextureA, cameraA, rawImageA);
            ReleaseSliceRenderTexture(ref _sliceRenderTextureB, cameraB, rawImageB);
            ReleaseSliceRenderTexture(ref _sliceRenderTextureC, cameraC, rawImageC);
            ReleaseSliceRenderTexture(ref _renderTexture3D, camera3D, rawImage3D);
        }

        private void ResolveTarget()
        {
            var resolved = ResolveTargetInHierarchy();
            if (resolved != null)
            {
                targetRenderer = resolved;
            }
        }

        private SimpleITKVolumeRenderer ResolveTargetInHierarchy()
        {
            return SimpleITKRendererLocator.FindInHierarchyOrScene(transform, gameObject.scene);
        }

        private void EnsureSetup()
        {
            if (targetRenderer == null)
            {
                return;
            }

            ResolveSliceRawImages();
            EnsureRoots();
            EnsureCameras();
            EnsurePlanes();
            DisableLegacySingleSlice();
        }

        private void ResolveSliceRawImages()
        {
            rawImageA = ResolveRawImage(rawImageA, "RawImage_A");
            rawImageB = ResolveRawImage(rawImageB, "RawImage_B");
            rawImageC = ResolveRawImage(rawImageC, "RawImage_C");
            rawImage3D = ResolveRawImage(rawImage3D, "RawImage_3D");
            ApplySliceViewRotations();
        }

        private void ApplySliceViewRotations()
        {
            ResolveUiRoiHandleRefsIfNeeded();
            ApplySlicePaneRotation(rawImageA, roiOverlayA, SlicePaneRotationA);
            ApplySlicePaneRotation(rawImageB, roiOverlayB, SlicePaneRotationB);
            ApplySlicePaneRotation(rawImageC, roiOverlayC, SlicePaneRotationC);
        }

        private static float GetSliceViewRotationZ(Pane pane)
        {
            switch (pane)
            {
                case Pane.A:
                    return SlicePaneRotationA;
                case Pane.B:
                    return SlicePaneRotationB;
                case Pane.C:
                    return SlicePaneRotationC;
                default:
                    return 0f;
            }
        }

        private static void ApplySlicePaneRotation(RawImage rawImage, RectTransform roiOverlay, float zDegrees)
        {
            var rotation = Quaternion.Euler(0f, 0f, zDegrees);

            if (rawImage != null)
            {
                rawImage.rectTransform.localRotation = rotation;
            }

            if (roiOverlay == null)
            {
                return;
            }

            if (rawImage != null)
            {
                roiOverlay.rotation = rawImage.rectTransform.rotation;
                return;
            }

            roiOverlay.localRotation = rotation;
        }

        private static RawImage ResolveRawImage(RawImage existing, string fallbackName)
        {
            if (existing != null)
            {
                return existing;
            }

            var obj = GameObject.Find(fallbackName);
            return obj != null ? obj.GetComponent<RawImage>() : null;
        }

        private void EnsureRoots()
        {
            var mprParent = GetMprParentTransform();
            if (mprParent == null)
            {
                return;
            }

            if (_mprRoot == null)
            {
                var existing = mprParent.Find(MprRootName);
                if (existing == null)
                {
                    existing = mprParent.Find(LegacyMprRootName);
                    if (existing != null)
                    {
                        existing.name = MprRootName;
                    }
                }

                if (existing == null)
                {
                    existing = targetRenderer.transform.Find(MprRootName);
                }

                if (existing == null)
                {
                    existing = targetRenderer.transform.Find(LegacyMprRootName);
                    if (existing != null)
                    {
                        existing.name = MprRootName;
                    }
                }

                if (existing != null)
                {
                    _mprRoot = existing;
                }
                else
                {
                    var global = GameObject.Find(MprRootName);
                    if (global == null)
                    {
                        global = GameObject.Find(LegacyMprRootName);
                        if (global != null)
                        {
                            global.name = MprRootName;
                        }
                    }

                    if (global != null && global.scene == gameObject.scene)
                    {
                        _mprRoot = global.transform;
                    }
                }

                if (_mprRoot == null)
                {
                    var rootObject = new GameObject(MprRootName);
                    _mprRoot = rootObject.transform;
                }
            }

            if (_mprRoot.parent != mprParent)
            {
                _mprRoot.SetParent(mprParent, false);
            }

            if (_mprRoot.name != MprRootName)
            {
                _mprRoot.name = MprRootName;
            }

            if (_cameraRoot == null)
            {
                var existingInMpr = _mprRoot.Find(CameraRootName);
                if (existingInMpr == null)
                {
                    existingInMpr = _mprRoot.Find(LegacyCameraRootName);
                    if (existingInMpr != null)
                    {
                        existingInMpr.name = CameraRootName;
                    }
                }

                if (existingInMpr != null)
                {
                    _cameraRoot = existingInMpr;
                }
                else
                {
                    var existing = GameObject.Find(CameraRootName);
                    if (existing == null)
                    {
                        existing = GameObject.Find(LegacyCameraRootName);
                        if (existing != null)
                        {
                            existing.name = CameraRootName;
                        }
                    }

                    if (existing != null)
                    {
                        _cameraRoot = existing.transform;
                    }
                    else
                    {
                        var rootObject = new GameObject(CameraRootName);
                        _cameraRoot = rootObject.transform;
                    }
                }
            }

            if (_cameraRoot != null)
            {
                if (_cameraRoot.name != CameraRootName)
                {
                    _cameraRoot.name = CameraRootName;
                }

                if (_cameraRoot.parent != _mprRoot)
                {
                    _cameraRoot.SetParent(_mprRoot, true);
                }

                _cameraRoot.localPosition = Vector3.zero;
                _cameraRoot.localRotation = Quaternion.identity;
                _cameraRoot.localScale = Vector3.one;
            }

            if (Application.isPlaying)
            {
                ConsolidateDuplicateCameraRoots();
                ConsolidateLegacyMprRoots(mprParent);
            }
        }

        private void ConsolidateDuplicateCameraRoots()
        {
            if (_mprRoot == null || _cameraRoot == null)
            {
                return;
            }

            for (var i = _mprRoot.childCount - 1; i >= 0; --i)
            {
                var child = _mprRoot.GetChild(i);
                if (child == null || child == _cameraRoot)
                {
                    continue;
                }

                if (child.name != CameraRootName && child.name != LegacyCameraRootName)
                {
                    continue;
                }

                while (child.childCount > 0)
                {
                    child.GetChild(0).SetParent(_cameraRoot, true);
                }

                DestroyObjectImmediateOrRuntime(child.gameObject);
            }
        }

        private void ConsolidateLegacyMprRoots(Transform mprParent)
        {
            if (_mprRoot == null)
            {
                return;
            }

            ConsolidateLegacyMprRootsUnderParent(mprParent);

            if (targetRenderer != null && targetRenderer.transform != null && targetRenderer.transform != mprParent)
            {
                ConsolidateLegacyMprRootsUnderParent(targetRenderer.transform);
            }
        }

        private void ConsolidateLegacyMprRootsUnderParent(Transform parent)
        {
            if (parent == null || _mprRoot == null)
            {
                return;
            }

            for (var i = parent.childCount - 1; i >= 0; --i)
            {
                var child = parent.GetChild(i);
                if (child == null || child == _mprRoot)
                {
                    continue;
                }

                if (child.name != MprRootName && child.name != LegacyMprRootName)
                {
                    continue;
                }

                while (child.childCount > 0)
                {
                    child.GetChild(0).SetParent(_mprRoot, true);
                }

                DestroyObjectImmediateOrRuntime(child.gameObject);
            }
        }

        private static void DestroyObjectImmediateOrRuntime(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(obj);
            }
            else
            {
                DestroyImmediate(obj);
            }
        }

        private Transform GetMprParentTransform()
        {
            if (targetRenderer == null)
            {
                return null;
            }

            return targetRenderer.transform.parent != null
                ? targetRenderer.transform.parent
                : targetRenderer.transform;
        }

        private void SyncMprRootTransform()
        {
            if (_mprRoot == null || targetRenderer == null)
            {
                return;
            }

            if (_mprRoot.parent == targetRenderer.transform)
            {
                _mprRoot.localPosition = Vector3.zero;
                _mprRoot.localRotation = Quaternion.identity;
                _mprRoot.localScale = Vector3.one;
                return;
            }

            _mprRoot.position = targetRenderer.transform.position;
            _mprRoot.rotation = targetRenderer.transform.rotation;

            var targetWorldScale = targetRenderer.transform.lossyScale;
            var parentWorldScale = _mprRoot.parent != null ? _mprRoot.parent.lossyScale : Vector3.one;
            _mprRoot.localScale = new Vector3(
                Mathf.Abs(parentWorldScale.x) > 1e-6f ? targetWorldScale.x / parentWorldScale.x : 1f,
                Mathf.Abs(parentWorldScale.y) > 1e-6f ? targetWorldScale.y / parentWorldScale.y : 1f,
                Mathf.Abs(parentWorldScale.z) > 1e-6f ? targetWorldScale.z / parentWorldScale.z : 1f);
        }

        private void CompensateCameraRootScale()
        {
            if (_mprRoot == null || _cameraRoot == null)
            {
                return;
            }

            var worldScale = _mprRoot.lossyScale;
            var inv = new Vector3(
                1f / Mathf.Max(Mathf.Abs(worldScale.x), 1e-4f),
                1f / Mathf.Max(Mathf.Abs(worldScale.y), 1e-4f),
                1f / Mathf.Max(Mathf.Abs(worldScale.z), 1e-4f));

            if ((_cameraRoot.localScale - inv).sqrMagnitude > 1e-8f)
            {
                _cameraRoot.localScale = inv;
            }
        }

        private void EnsureCameras()
        {
            cameraA = ResolveOrCreateNamedCamera(cameraA, "MPR_A");
            cameraB = ResolveOrCreateNamedCamera(cameraB, "MPR_B");
            cameraC = ResolveOrCreateNamedCamera(cameraC, "MPR_C");

            if (Application.isPlaying)
            {
                ConsolidateDuplicateNamedCameras("MPR_A", cameraA);
                ConsolidateDuplicateNamedCameras("MPR_B", cameraB);
                ConsolidateDuplicateNamedCameras("MPR_C", cameraC);
            }

            EnsureCameraParent(cameraA);
            EnsureCameraParent(cameraB);
            EnsureCameraParent(cameraC);

            EnsureCameraEnabled(cameraA);
            EnsureCameraEnabled(cameraB);
            EnsureCameraEnabled(cameraC);

            EnsureUprCameraData(cameraA);
            EnsureUprCameraData(cameraB);
            EnsureUprCameraData(cameraC);

            if (rawImage3D != null)
            {
                camera3D = ResolveSceneNamedCamera(camera3D, "MPR_3D");
                if (camera3D != null)
                {
                    EnsureUprCameraData(camera3D);
                }
            }
        }

        private static Camera ResolveSceneNamedCamera(Camera existing, string cameraName)
        {
            if (existing != null)
            {
                return existing;
            }

            if (string.IsNullOrEmpty(cameraName))
            {
                return null;
            }

            var obj = GameObject.Find(cameraName);
            return obj != null ? obj.GetComponent<Camera>() : null;
        }

        private Camera ResolveOrCreateNamedCamera(Camera existing, string cameraName)
        {
            if (existing == null)
            {
                existing = FindCameraUnderRoot(cameraName);
            }

            if (existing == null)
            {
                existing = CreateCamera(cameraName, _cameraRoot, addAudioListener: false);
            }

            if (existing != null && existing.gameObject.name != cameraName)
            {
                existing.gameObject.name = cameraName;
            }

            return existing;
        }

        private Camera FindCameraUnderRoot(string cameraName)
        {
            if (_cameraRoot == null || string.IsNullOrEmpty(cameraName))
            {
                return null;
            }

            Camera fallback = null;
            var cameras = _cameraRoot.GetComponentsInChildren<Camera>(true);
            for (var i = 0; i < cameras.Length; ++i)
            {
                var cam = cameras[i];
                if (cam == null)
                {
                    continue;
                }

                if (cam.gameObject.name == cameraName)
                {
                    return cam;
                }

                if (fallback == null && cam.gameObject.name.StartsWith(cameraName))
                {
                    fallback = cam;
                }
            }

            return fallback;
        }

        private void ConsolidateDuplicateNamedCameras(string cameraName, Camera keep)
        {
            if (_cameraRoot == null || keep == null || string.IsNullOrEmpty(cameraName))
            {
                return;
            }

            var duplicateSuffixPrefix = cameraName + " (";
            var cameras = _cameraRoot.GetComponentsInChildren<Camera>(true);
            for (var i = 0; i < cameras.Length; ++i)
            {
                var cam = cameras[i];
                if (cam == null || cam == keep)
                {
                    continue;
                }

                var currentName = cam.gameObject.name;
                if (currentName != cameraName && !currentName.StartsWith(duplicateSuffixPrefix))
                {
                    continue;
                }

                DestroyObjectImmediateOrRuntime(cam.gameObject);
            }
        }

        private void EnsurePlanes()
        {
            _planeA = EnsurePlane(_planeA, "Plane_A", SliceLayerA, PlaneColorA);
            _planeB = EnsurePlane(_planeB, "Plane_B", SliceLayerB, PlaneColorB);
            _planeC = EnsurePlane(_planeC, "Plane_C", SliceLayerC, PlaneColorC);

            ApplySliceRendererSettings(_planeA);
            ApplySliceRendererSettings(_planeB);
            ApplySliceRendererSettings(_planeC);
        }

        private MprPlane EnsurePlane(MprPlane plane, string name, int sliceLayer, Color color)
        {
            plane ??= new MprPlane();
            plane.SliceLayer = sliceLayer;
            plane.Color = color;

            if (plane.Anchor == null)
            {
                var existing = _mprRoot.Find(name);
                if (existing != null)
                {
                    plane.Anchor = existing;
                }
                else
                {
                    var anchorObject = new GameObject(name);
                    plane.Anchor = anchorObject.transform;
                    plane.Anchor.SetParent(_mprRoot, false);
                    plane.Anchor.localPosition = Vector3.zero;
                    plane.Anchor.localRotation = Quaternion.identity;
                    plane.Anchor.localScale = Vector3.one;
                }
            }

            if (plane.SliceObject == null)
            {
                var existing = plane.Anchor.Find("Slice");
                if (existing != null)
                {
                    plane.SliceObject = existing;
                }
                else
                {
                    var sliceObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    sliceObject.name = "Slice";
                    plane.SliceObject = sliceObject.transform;
                    plane.SliceObject.SetParent(plane.Anchor, false);

                    var collider = sliceObject.GetComponent<Collider>();
                    if (collider != null)
                    {
                        if (Application.isPlaying)
                        {
                            Destroy(collider);
                        }
                        else
                        {
                            DestroyImmediate(collider);
                        }
                    }
                }
            }

            SetLayerRecursively(plane.SliceObject.gameObject, sliceLayer);

            if (plane.SliceRenderer == null && plane.SliceObject != null)
            {
                plane.SliceRenderer = plane.SliceObject.GetComponent<SimpleITKSliceRenderer>();
                if (plane.SliceRenderer == null)
                {
                    plane.SliceRenderer = plane.SliceObject.gameObject.AddComponent<SimpleITKSliceRenderer>();
                }
            }

            if (plane.Indicator == null)
            {
                var existing = plane.Anchor.Find("Indicator");
                Transform indicatorTransform;
                if (existing != null)
                {
                    indicatorTransform = existing;
                }
                else
                {
                    var indicatorObject = new GameObject("Indicator");
                    indicatorTransform = indicatorObject.transform;
                    indicatorTransform.SetParent(plane.Anchor, false);
                }

                SetLayerRecursively(indicatorTransform.gameObject, IndicatorLayer);

                plane.Indicator = indicatorTransform.GetComponent<LineRenderer>();
                if (plane.Indicator == null)
                {
                    plane.Indicator = indicatorTransform.gameObject.AddComponent<LineRenderer>();
                }

                ConfigureIndicator(plane.Indicator, color);
            }

            if (plane.Indicator != null)
            {
                ConfigureIndicator(plane.Indicator, color);
            }

            return plane;
        }

        private void ApplySliceRendererSettings(MprPlane plane)
        {
            if (plane?.SliceRenderer == null)
            {
                return;
            }

            if (!plane.SliceRenderer.enabled)
            {
                plane.SliceRenderer.enabled = true;
            }

            plane.SliceRenderer.SetTarget(targetRenderer);
            plane.SliceRenderer.SetShaderOverride(sliceShader);
            plane.SliceRenderer.SetUseTransferFunction2D(use2DTransferFunctionForSlices);

            if (targetRenderer != null && targetRenderer.GetVolumeTexture() != null)
            {
                var meshRenderer = plane.SliceRenderer.GetComponent<MeshRenderer>();
                if (meshRenderer != null && !meshRenderer.enabled)
                {
                    meshRenderer.enabled = true;
                }
            }
        }

        private void ApplyAll()
        {
            if (targetRenderer == null)
            {
                _applyDirty = MprApplyDirty.None;
                return;
            }

            if (Application.isPlaying && targetRenderer.GetVolumeTexture() == null)
            {
                TryRecoverVolumeLoad();
            }
            else
            {
                _runtimeVolumeLoadRequested = false;
            }

            var dirty = _applyDirty;
            _applyDirty = MprApplyDirty.None;

            if ((dirty & MprApplyDirty.MprRootSync) != 0)
            {
                SyncMprRootTransform();
                CompensateCameraRootScale();
            }

            if ((dirty & MprApplyDirty.VolumeMeshState) != 0)
            {
                var volumeRenderer = targetRenderer.GetComponent<MeshRenderer>();
                if (volumeRenderer != null)
                {
                    volumeRenderer.enabled = targetRenderer.IsVolumeMeshRenderingEnabled;
                }
            }

            if ((dirty & MprApplyDirty.Roi) != 0)
            {
                ApplyRoiToRenderers();
            }

            if ((dirty & MprApplyDirty.PlaneTransforms) != 0)
            {
                ApplyPlaneTransforms();
            }

            if ((dirty & MprApplyDirty.CameraLayout) != 0)
            {
                ApplyCameraLayoutAndMasks();
            }

            if ((dirty & MprApplyDirty.CameraPoses) != 0)
            {
                ApplyCameraPoses();
            }

            if ((dirty & MprApplyDirty.SliceRenderers) != 0)
            {
                ApplySliceRenderers();
            }
        }

        private void ApplySliceRenderers()
        {
            ApplySliceRendererSettings(_planeA);
            ApplySliceRendererSettings(_planeB);
            ApplySliceRendererSettings(_planeC);
        }

        private void TryRecoverVolumeLoad()
        {
            if (targetRenderer == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                if (_runtimeVolumeLoadRequested)
                {
                    return;
                }

                if (!targetRenderer.HasVolumeSourceConfigured())
                {
                    return;
                }

                _runtimeVolumeLoadRequested = true;
                targetRenderer.LoadVolume();
                return;
            }
        }

        /// <summary>
        /// Automatically fits the ROI to the bounding box of non-zero voxels.
        /// Called by <see cref="SimpleITKVolumeLoader"/> after a volume finishes loading.
        /// </summary>
        public void AutoFitRoiFromData(SimpleITKVolumeData data)
        {
            if (!autoFitRoiOnLoad)
            {
                return;
            }

            var voxels = data.VoxelsNormalized;
            if (voxels == null || data.Width <= 0 || data.Height <= 0 || data.Depth <= 0)
            {
                return;
            }

            int w = data.Width, h = data.Height, d = data.Depth;
            int xMin = w, yMin = h, zMin = d;
            int xMax = -1, yMax = -1, zMax = -1;
            float thr = autoFitThreshold;
            GetAutoFitSamplingSteps(w, h, d, out var stepX, out var stepY, out var stepZ);

            for (int z = 0; z < d; z += stepZ)
            for (int y = 0; y < h; y += stepY)
            for (int x = 0; x < w; x += stepX)
            {
                if (voxels[x + y * w + z * w * h] > thr)
                {
                    if (x < xMin) xMin = x;
                    if (x > xMax) xMax = x;
                    if (y < yMin) yMin = y;
                    if (y > yMax) yMax = y;
                    if (z < zMin) zMin = z;
                    if (z > zMax) zMax = z;
                }
            }

            if (xMax < 0) // No valid voxels.
            {
                return;
            }

            if (stepX > 1 || stepY > 1 || stepZ > 1)
            {
                xMin = Mathf.Max(0, xMin - stepX);
                yMin = Mathf.Max(0, yMin - stepY);
                zMin = Mathf.Max(0, zMin - stepZ);
                xMax = Mathf.Min(w - 1, xMax + stepX);
                yMax = Mathf.Min(h - 1, yMax + stepY);
                zMax = Mathf.Min(d - 1, zMax + stepZ);
            }

            const float margin = 0.005f;
            roiStart01 = new Vector3(
                Mathf.Clamp01((float)xMin / w - margin),
                Mathf.Clamp01((float)yMin / h - margin),
                Mathf.Clamp01((float)zMin / d - margin));
            roiEnd01 = new Vector3(
                Mathf.Clamp01((float)(xMax + 1) / w + margin),
                Mathf.Clamp01((float)(yMax + 1) / h + margin),
                Mathf.Clamp01((float)(zMax + 1) / d + margin));

            MarkApplyDirty(MprApplyDirty.Roi);
        }

        private static void GetAutoFitSamplingSteps(
            int width,
            int height,
            int depth,
            out int stepX,
            out int stepY,
            out int stepZ)
        {
            var voxelCount = (long)width * height * depth;
            if (voxelCount <= AutoFitFullScanVoxelBudget)
            {
                stepX = 1;
                stepY = 1;
                stepZ = 1;
                return;
            }

            stepX = Mathf.Max(1, Mathf.CeilToInt(width / (float)AutoFitTargetSamplesPerAxis));
            stepY = Mathf.Max(1, Mathf.CeilToInt(height / (float)AutoFitTargetSamplesPerAxis));
            stepZ = Mathf.Max(1, Mathf.CeilToInt(depth / (float)AutoFitTargetSamplesPerAxis));
        }

        private void ApplyRoiToRenderers()
        {
            if (targetRenderer == null)
            {
                return;
            }

            if (!enableRoiBox)
            {
                targetRenderer.SetBoxCrossSectionEnabled(false);
                return;
            }

            if (!Application.isPlaying)
            {
                targetRenderer.SetBoxCrossSectionEnabled(false);
                return;
            }

            var roiMin = GetRoiMin01();
            var roiMax = GetRoiMax01();
            targetRenderer.SetBoxCrossSectionRegion(roiMin, roiMax, keepInsideRoi);
        }

        private void ApplyPlaneTransforms()
        {
            if (_planeA?.Anchor != null)
            {
                _planeA.Anchor.localPosition = new Vector3(sliceX - 0.5f, 0f, 0f);
                _planeA.Anchor.localRotation = Quaternion.Euler(0f, 90f, 0f);
                _planeA.Anchor.localScale = new Vector3(scaleX, scaleX, 1f);
            }

            if (_planeB?.Anchor != null)
            {
                _planeB.Anchor.localPosition = new Vector3(0f, sliceY - 0.5f, 0f);
                _planeB.Anchor.localRotation = Quaternion.Euler(-90f, 0f, 0f);
                _planeB.Anchor.localScale = new Vector3(scaleY, scaleY, 1f);
            }

            if (_planeC?.Anchor != null)
            {
                _planeC.Anchor.localPosition = new Vector3(0f, 0f, sliceZ - 0.5f);
                _planeC.Anchor.localRotation = Quaternion.Euler(0f, 0f, 0f);
                _planeC.Anchor.localScale = new Vector3(scaleZ, scaleZ, 1f);
            }
        }

        private void ApplyCameraLayoutAndMasks()
        {
            if (cameraA == null || cameraB == null || cameraC == null)
            {
                return;
            }

            ResolveSliceRawImages();
            var paneOutputsToRawImage = rawImageA != null || rawImageB != null || rawImageC != null || rawImage3D != null;
            AssignSliceCameraOutput(cameraA, rawImageA, ref _sliceRenderTextureA, "MPR_RT_A");
            AssignSliceCameraOutput(cameraB, rawImageB, ref _sliceRenderTextureB, "MPR_RT_B");
            AssignSliceCameraOutput(cameraC, rawImageC, ref _sliceRenderTextureC, "MPR_RT_C");
            AssignSliceCameraOutput(camera3D, rawImage3D, ref _renderTexture3D, "MPR_RT_3D");

            ApplyPaneViewport(cameraA, Pane.A, new Rect(0f, 0.5f, 0.5f, 0.5f), 0, paneOutputsToRawImage);
            ApplyPaneViewport(cameraB, Pane.B, new Rect(0.5f, 0.5f, 0.5f, 0.5f), 1, paneOutputsToRawImage);
            ApplyPaneViewport(cameraC, Pane.C, new Rect(0f, 0f, 0.5f, 0.5f), 2, paneOutputsToRawImage);
            ApplyPaneViewport(camera3D, Pane.D3, new Rect(0.5f, 0f, 0.5f, 0.5f), 3, paneOutputsToRawImage);

            if (paneOutputsToRawImage)
            {
                SyncSliceCameraEnabledForUiOutput(cameraA, rawImageA);
                SyncSliceCameraEnabledForUiOutput(cameraB, rawImageB);
                SyncSliceCameraEnabledForUiOutput(cameraC, rawImageC);
                SyncSliceCameraEnabledForUiOutput(camera3D, rawImage3D);
            }

            cameraA.orthographic = true;
            cameraB.orthographic = true;
            cameraC.orthographic = true;

            cameraA.clearFlags = CameraClearFlags.SolidColor;
            cameraB.clearFlags = CameraClearFlags.SolidColor;
            cameraC.clearFlags = CameraClearFlags.SolidColor;

            cameraA.backgroundColor = Color.black;
            cameraB.backgroundColor = Color.black;
            cameraC.backgroundColor = Color.black;

            cameraA.cullingMask = 1 << SliceLayerA;
            cameraB.cullingMask = 1 << SliceLayerB;
            cameraC.cullingMask = 1 << SliceLayerC;
        }

        private void ApplyPaneViewport(Camera cam, Pane pane, Rect defaultRect, int defaultDepth, bool renderSliceToRawImage)
        {
            if (cam == null)
            {
                return;
            }

            cam.enabled = true;
            cam.depth = defaultDepth;

            var paneRawImage = renderSliceToRawImage ? GetPaneRawImage(pane) : null;
            if (paneRawImage != null)
            {
                // RenderTexture is displayed on a RawImage: fill the full RT so content is centered in the UI rect.
                cam.rect = FullPaneViewportRect;
                if (!paneRawImage.gameObject.activeSelf)
                {
                    paneRawImage.gameObject.SetActive(true);
                }
                return;
            }

            cam.rect = defaultRect;
        }

        private static void SyncSliceCameraEnabledForUiOutput(Camera cam, RawImage rawImage)
        {
            if (cam == null)
            {
                return;
            }

            // Pane views are shown only via RawImage; do not paint over the screen background.
            cam.enabled = rawImage != null;
        }

        private RawImage GetPaneRawImage(Pane pane)
        {
            switch (pane)
            {
                case Pane.A:
                    return rawImageA;
                case Pane.B:
                    return rawImageB;
                case Pane.C:
                    return rawImageC;
                case Pane.D3:
                    return rawImage3D;
                default:
                    return null;
            }
        }

        private RawImage GetSliceRawImage(Pane pane)
        {
            switch (pane)
            {
                case Pane.A:
                    return rawImageA;
                case Pane.B:
                    return rawImageB;
                case Pane.C:
                    return rawImageC;
                default:
                    return null;
            }
        }

        private void AssignSliceCameraOutput(
            Camera cam,
            RawImage targetImage,
            ref RenderTexture rt,
            string rtName)
        {
            if (cam == null)
            {
                return;
            }

            if (targetImage == null)
            {
                ReleaseSliceRenderTexture(ref rt, cam, null);
                return;
            }

            var targetSize = GetRawImagePixelSize(targetImage);
            var width = Mathf.Max(64, targetSize.x);
            var height = Mathf.Max(64, targetSize.y);
            EnsureSliceRenderTexture(ref rt, width, height, rtName, cam, targetImage);

            cam.targetTexture = rt;
            if (targetImage.texture != rt)
            {
                targetImage.texture = rt;
            }

            targetImage.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        private static Vector2Int GetRawImagePixelSize(RawImage rawImage)
        {
            var rectTransform = rawImage != null ? rawImage.rectTransform : null;
            if (rectTransform == null)
            {
                return new Vector2Int(Screen.width, Screen.height);
            }

            var canvas = rawImage.canvas;
            var scaleFactor = canvas != null ? Mathf.Max(0.01f, canvas.scaleFactor) : 1f;
            var width = Mathf.RoundToInt(rectTransform.rect.width * scaleFactor);
            var height = Mathf.RoundToInt(rectTransform.rect.height * scaleFactor);
            return new Vector2Int(width, height);
        }

        private void EnsureSliceRenderTexture(
            ref RenderTexture rt,
            int width,
            int height,
            string name,
            Camera cam,
            RawImage targetImage)
        {
            var needsCreate = rt == null || !rt.IsCreated() || rt.width != width || rt.height != height;
            if (!needsCreate)
            {
                return;
            }

            ReleaseSliceRenderTexture(ref rt, cam, targetImage);
            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = name,
                useMipMap = false,
                autoGenerateMips = false
            };
            rt.Create();
        }

        private static void DetachSliceRenderTextureReferences(RenderTexture rt, Camera cam, RawImage rawImage)
        {
            if (rt == null)
            {
                return;
            }

            if (cam != null && cam.targetTexture == rt)
            {
                cam.targetTexture = null;
            }

            if (rawImage != null && rawImage.texture == rt)
            {
                rawImage.texture = null;
            }
        }

        private void ReleaseSliceRenderTexture(ref RenderTexture rt, Camera cam = null, RawImage rawImage = null)
        {
            if (rt == null)
            {
                return;
            }

            DetachSliceRenderTextureReferences(rt, cam, rawImage);

            if (rt.IsCreated())
            {
                rt.Release();
            }

            if (Application.isPlaying)
            {
                Destroy(rt);
            }
            else
            {
                DestroyImmediate(rt);
            }

            rt = null;
        }

        private void ApplyCameraPoses()
        {
            if (targetRenderer == null)
            {
                return;
            }

            var zoom = Mathf.Max(0.1f, globalZoom);

            // MPR_3D Transform is scene/Inspector-driven; do not overwrite position, rotation, or parent scale here.
            // if (camera3D != null)
            // {
            //     var referenceRotation = GetReferenceVolumeWorldRotation();
            //     var forwardLocal = GetInitial3DViewForwardLocal();
            //     var upLocal = GetInitial3DViewUpLocal(forwardLocal);
            //     var forward = (referenceRotation * forwardLocal).normalized;
            //     var up = (referenceRotation * upLocal).normalized;
            //     var direction = -forward;
            //
            //     camera3D.transform.position = center + direction * (dist / zoom);
            //     camera3D.transform.rotation = Quaternion.LookRotation(forward, up);
            //     camera3D.nearClipPlane = 0.01f;
            //     camera3D.farClipPlane = Mathf.Max(10f, dist * 8f);
            //
            //     if (camera3DProjection == Camera3DProjection.Orthographic)
            //     {
            //         camera3D.orthographicSize = Mathf.Max(0.01f, maxExtent / zoom);
            //     }
            //     else
            //     {
            //         camera3D.fieldOfView = camera3DFov;
            //     }
            // }

            var sliceZoom = Mathf.Min(zoom, 1.0f);
            PoseSliceCamera(cameraA, _planeA, sliceZoom);
            PoseSliceCamera(cameraB, _planeB, sliceZoom);
            PoseSliceCamera(cameraC, _planeC, sliceZoom);
        }

        private void PoseSliceCamera(Camera cam, MprPlane plane, float zoom)
        {
            if (cam == null || plane?.Anchor == null)
            {
                return;
            }

            var center = plane.Anchor.position;
            var forward = plane.Anchor.forward;
            var up = plane.Anchor.up;

            // Avoid lossyScale here: under non-uniform parent scale + rotation it can jitter frame-to-frame.
            // Use transformed basis vectors directly for stable world-space extents.
            var sliceTransform = plane.SliceObject != null ? plane.SliceObject : plane.Anchor;
            var halfWidth = sliceTransform.TransformVector(new Vector3(0.5f, 0f, 0f)).magnitude;
            var halfHeight = sliceTransform.TransformVector(new Vector3(0f, 0.5f, 0f)).magnitude;
            var aspect = Mathf.Max(1e-4f, cam.aspect);
            var orthoSize = Mathf.Max(halfHeight, halfWidth / aspect) * 1.02f;
            orthoSize /= Mathf.Max(0.1f, zoom);

            var dist = Mathf.Max(0.5f, orthoSize * 3.0f);
            cam.transform.position = center - forward * dist;
            cam.transform.rotation = Quaternion.LookRotation(forward, up);

            cam.orthographicSize = orthoSize;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = Mathf.Max(5f, dist * 8f);
        }

        private void HandleInput()
        {

            if (!useUiRoiHandles)
            {
                HandleRoiMouseInput();
            }

            var pane = GetMousePane();
            HandleGlobalZoomInput(pane);

            if (_activeRoiHandle != RoiHandle.None)
            {
                return;
            }

            if (pane == Pane.D3)
            {
                return;
            }

            if (pane != Pane.A && pane != Pane.B && pane != Pane.C)
            {
                return;
            }

            var scroll = GetMouseScrollDeltaY();
            if (Mathf.Abs(scroll) > 1e-4f)
            {
                var scaleModifier = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (scaleModifier)
                {
                    AdjustScale(pane, scroll * scaleStep);
                }
                else
                {
                    AdjustPosition(pane, scroll * moveStep);
                }
            }

            if (Input.GetMouseButton(1))
            {
                var drag = -Input.GetAxis("Mouse Y");
                if (Mathf.Abs(drag) > 1e-4f)
                {
                    AdjustScale(pane, drag * scaleStep * 0.6f);
                }
            }

            // Left-drag slice scrub disabled so ROI handles are easier to grab; use scroll wheel instead.
            // if (Input.GetMouseButton(0))
            // {
            //     var drag = -Input.GetAxis("Mouse Y");
            //     if (Mathf.Abs(drag) > 1e-4f)
            //     {
            //         AdjustPosition(pane, drag * moveStep * 0.5f);
            //     }
            // }
        }

        private bool HandleTouchInput()
        {
            if (!Input.touchSupported)
            {
                ClearTouchInputState();
                return false;
            }

            if (Input.touchCount <= 0)
            {
                ClearTouchInputState();
                return false;
            }

            if (TryHandleTouchRoiInput())
            {
                return true;
            }

            if (TryHandleTouchPinchZoomInput())
            {
                return true;
            }

            return true;
        }

        private void ClearTouchInputState()
        {
            if (_isRoiTouchDragging)
            {
                ClearTouchRoiDrag();
            }

            _isTouchPinchZooming = false;
        }

        private bool TryHandleTouchRoiInput()
        {
            if (!enableRoiBox || !Application.isPlaying)
            {
                if (_isRoiTouchDragging)
                {
                    ClearTouchRoiDrag();
                }

                return false;
            }

            if (_isRoiTouchDragging)
            {
                if (!TryGetTouchByFingerId(_activeRoiTouchFingerId, out var activeTouch))
                {
                    ClearTouchRoiDrag();
                    return true;
                }

                if (activeTouch.phase == UnityEngine.TouchPhase.Canceled || activeTouch.phase == UnityEngine.TouchPhase.Ended)
                {
                    ClearTouchRoiDrag();
                    return true;
                }

                var contentRect = GetRoiDrawingRect(_activeRoiPane);
                var uv = ScreenToPaneUv(contentRect, ToGuiPosition(activeTouch.position));
                SetRoiHandle(_activeRoiPane, _activeRoiHandle, uv.x, uv.y);
                MarkApplyDirty(MprApplyDirty.Roi);
                return true;
            }

            var rectA = GetRoiDrawingRect(Pane.A);
            var rectB = GetRoiDrawingRect(Pane.B);
            var rectC = GetRoiDrawingRect(Pane.C);

            for (var i = 0; i < Input.touchCount; i++)
            {
                var touch = Input.GetTouch(i);
                if (touch.phase != UnityEngine.TouchPhase.Began)
                {
                    continue;
                }

                var guiPos = ToGuiPosition(touch.position);
                if (TryBeginRoiDrag(Pane.A, rectA, guiPos) ||
                    TryBeginRoiDrag(Pane.B, rectB, guiPos) ||
                    TryBeginRoiDrag(Pane.C, rectC, guiPos))
                {
                    _isRoiTouchDragging = true;
                    _activeRoiTouchFingerId = touch.fingerId;
                    return true;
                }
            }

            return false;
        }

        private bool TryHandleTouchPinchZoomInput()
        {
            // Pinch zoom on the 3D background (Pane.D3) is disabled.
            _isTouchPinchZooming = false;
            return false;

            // if (!TryGetPinchTouches(out var touchA, out var touchB))
            // {
            //     _isTouchPinchZooming = false;
            //     return false;
            // }
            //
            // var midpoint = (touchA.position + touchB.position) * 0.5f;
            // if (GetPaneFromScreenPosition(midpoint) != Pane.D3)
            // {
            //     _isTouchPinchZooming = false;
            //     return false;
            // }
            //
            // var distance = Vector2.Distance(touchA.position, touchB.position);
            // if (distance <= 1f)
            // {
            //     return true;
            // }
            //
            // if (!_isTouchPinchZooming || touchA.phase == UnityEngine.TouchPhase.Began || touchB.phase == UnityEngine.TouchPhase.Began)
            // {
            //     _isTouchPinchZooming = true;
            //     _touchPinchStartDistance = Mathf.Max(1f, distance);
            //     _touchPinchStartZoom = globalZoom;
            //     return true;
            // }
            //
            // var ratio = distance / Mathf.Max(1f, _touchPinchStartDistance);
            // globalZoom = Mathf.Clamp(_touchPinchStartZoom * ratio, minGlobalZoom, maxGlobalZoom);
            // return true;
        }

        private bool TryGetPinchTouches(out Touch touchA, out Touch touchB)
        {
            touchA = default;
            touchB = default;

            var found = 0;
            for (var i = 0; i < Input.touchCount; i++)
            {
                var touch = Input.GetTouch(i);
                if (touch.phase == UnityEngine.TouchPhase.Canceled || touch.phase == UnityEngine.TouchPhase.Ended)
                {
                    continue;
                }

                if (_isRoiTouchDragging && touch.fingerId == _activeRoiTouchFingerId)
                {
                    continue;
                }

                if (found == 0)
                {
                    touchA = touch;
                    found = 1;
                    continue;
                }

                touchB = touch;
                return true;
            }

            return false;
        }

        private bool TryGetTouchByFingerId(int fingerId, out Touch touch)
        {
            for (var i = 0; i < Input.touchCount; i++)
            {
                touch = Input.GetTouch(i);
                if (touch.fingerId == fingerId)
                {
                    return true;
                }
            }

            touch = default;
            return false;
        }

        private void HandleGlobalZoomInput(Pane hoveredPane)
        {
            var zoomInput = 0.0f;
            if (Input.GetKey(ZoomInKey))
            {
                zoomInput += 1.0f;
            }
            if (Input.GetKey(ZoomOutKey))
            {
                zoomInput -= 1.0f;
            }

            // Scroll wheel over the 3D background must not change globalZoom (no zoom-in/out on camera3D region).
            // if (hoveredPane == Pane.D3)
            // {
            //     var wheel = GetMouseScrollDeltaY();
            //     if (Mathf.Abs(wheel) > 1e-4f)
            //     {
            //         zoomInput += wheel * mouseWheelZoomSensitivity;
            //     }
            // }

            if (Mathf.Abs(zoomInput) < 1e-4f)
            {
                return;
            }

            var dt = Application.isPlaying
                ? Time.deltaTime
                : Mathf.Max(Time.unscaledDeltaTime, 1f / 60f);
            var factor = Mathf.Exp(zoomInput * globalZoomSpeed * dt);
            globalZoom = Mathf.Clamp(globalZoom * factor, minGlobalZoom, maxGlobalZoom);
        }

        private static float GetMouseScrollDeltaY()
        {
            var scrollY = Input.mouseScrollDelta.y;
#if ENABLE_INPUT_SYSTEM
            if (Mathf.Abs(scrollY) <= 1e-4f)
            {
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    // Input System reports device-dependent scroll units.
                    // Normalize to roughly "notches" so sensitivity stays stable.
                    scrollY = mouse.scroll.ReadValue().y / 120f;
                }
            }
#endif
            return scrollY;
        }

        private void ClearTouchRoiDrag()
        {
            if (_isRoiTouchDragging)
            {
                _activeRoiHandle = RoiHandle.None;
                _activeRoiPane = Pane.None;
            }

            _isRoiTouchDragging = false;
            _activeRoiTouchFingerId = -1;
        }

        private void HandleRoiMouseInput()
        {
            if (!enableRoiBox || !Application.isPlaying)
            {
                return;
            }

            if (_isRoiTouchDragging || (Input.touchSupported && Input.touchCount > 0))
            {
                return;
            }

            var mousePos = new Vector2(
                Input.mousePosition.x,
                Screen.height - Input.mousePosition.y);

            if (Input.GetMouseButtonDown(0))
            {
                var rectA = GetRoiDrawingRect(Pane.A);
                var rectB = GetRoiDrawingRect(Pane.B);
                var rectC = GetRoiDrawingRect(Pane.C);

                TryBeginRoiDrag(Pane.A, rectA, mousePos);
                if (_activeRoiHandle == RoiHandle.None) TryBeginRoiDrag(Pane.B, rectB, mousePos);
                if (_activeRoiHandle == RoiHandle.None) TryBeginRoiDrag(Pane.C, rectC, mousePos);
            }

            if (!Input.GetMouseButton(0))
            {
                if (_activeRoiHandle != RoiHandle.None)
                {
                    _activeRoiHandle = RoiHandle.None;
                    _activeRoiPane = Pane.None;
                }
                return;
            }

            if (_activeRoiHandle == RoiHandle.None)
            {
                return;
            }

            var contentRect = GetRoiDrawingRect(_activeRoiPane);
            var uv = ScreenToPaneUv(contentRect, mousePos);
            SetRoiHandle(_activeRoiPane, _activeRoiHandle, uv.x, uv.y);
            MarkApplyDirty(MprApplyDirty.Roi);
        }

        private void CaptureInitialVolumeTransformIfNeeded()
        {
            var currentVolumeTexture = targetRenderer != null ? targetRenderer.GetVolumeTexture() : null;
            if (targetRenderer != null && currentVolumeTexture == null)
            {
                return;
            }

            if (_initialVolumeTransformCaptured && currentVolumeTexture == _capturedInitialVolumeTexture)
            {
                return;
            }

            // Capture ROI, zoom, and slice positions even when there is no target renderer.
            _initialRoiStart    = roiStart01;
            _initialRoiEnd      = roiEnd01;
            _initialGlobalZoom  = globalZoom;
            _initialSliceX      = sliceX;
            _initialSliceY      = sliceY;
            _initialSliceZ      = sliceZ;

            if (targetRenderer != null)
            {
                _initialVolumeLocalRotation = targetRenderer.transform.localRotation;
                _initialVolumeWorldRotation = targetRenderer.transform.rotation;
                _initialVolumeLocalScale    = targetRenderer.transform.localScale;
            }

            _capturedInitialVolumeTexture = currentVolumeTexture;
            _initialVolumeTransformCaptured = true;
        }

        private static float SliceToSliderValue(float slice) => 1f - Mathf.Clamp01(slice);

        private static float SliderToSliceValue(float slider) => 1f - Mathf.Clamp01(slider);

        private void BindSliceSlider(Slider slider, float value, UnityAction<float> onChanged)
        {
            if (slider == null)
            {
                return;
            }

            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.onValueChanged.RemoveListener(onChanged);
            slider.SetValueWithoutNotify(SliceToSliderValue(value));
            slider.onValueChanged.AddListener(onChanged);
        }

        private static void UnbindSliceSlider(Slider slider, UnityAction<float> onChanged)
        {
            if (slider == null)
            {
                return;
            }

            slider.onValueChanged.RemoveListener(onChanged);
        }

        private void OnSliceSliderXChanged(float value)
        {
            ApplySliceFromSlider(Pane.A, value);
        }

        private void OnSliceSliderYChanged(float value)
        {
            ApplySliceFromSlider(Pane.B, value);
        }

        private void OnSliceSliderZChanged(float value)
        {
            ApplySliceFromSlider(Pane.C, value);
        }

        private void ApplySliceFromSlider(Pane pane, float value)
        {
            var clamped = SliderToSliceValue(value);
            switch (pane)
            {
                case Pane.A:
                    if (Mathf.Approximately(sliceX, clamped))
                    {
                        return;
                    }

                    sliceX = clamped;
                    break;
                case Pane.B:
                    if (Mathf.Approximately(sliceY, clamped))
                    {
                        return;
                    }

                    sliceY = clamped;
                    break;
                case Pane.C:
                    if (Mathf.Approximately(sliceZ, clamped))
                    {
                        return;
                    }

                    sliceZ = clamped;
                    break;
                default:
                    return;
            }

            MarkApplyDirty(MprApplyDirty.PlaneTransforms | MprApplyDirty.CameraPoses);
        }

        private static Vector2 ToGuiPosition(Vector2 screenPosition)
        {
            return new Vector2(screenPosition.x, Screen.height - screenPosition.y);
        }

        private void OnGUI()
        {
            if (!enableRoiBox)
            {
                return;
            }

            if (useUiRoiHandles)
            {
                if (debugRoiHitAreas)
                {
                    var debugRectA = GetRoiDrawingRect(Pane.A);
                    var debugRectB = GetRoiDrawingRect(Pane.B);
                    var debugRectC = GetRoiDrawingRect(Pane.C);
                    DrawRoiHitDebugOverlay(Pane.A, debugRectA);
                    DrawRoiHitDebugOverlay(Pane.B, debugRectB);
                    DrawRoiHitDebugOverlay(Pane.C, debugRectC);
                }

                if (!Application.isPlaying)
                {
                    ClearTouchRoiDrag();
                    _activeRoiHandle = RoiHandle.None;
                    _activeRoiPane = Pane.None;
                }

                return;
            }

            var rectA = GetRoiDrawingRect(Pane.A);
            var rectB = GetRoiDrawingRect(Pane.B);
            var rectC = GetRoiDrawingRect(Pane.C);

            DrawRoiPaneOverlay(Pane.A, rectA, drawHandles: true);
            DrawRoiPaneOverlay(Pane.B, rectB, drawHandles: true);
            DrawRoiPaneOverlay(Pane.C, rectC, drawHandles: true);

            if (debugRoiHitAreas)
            {
                DrawRoiHitDebugOverlay(Pane.A, rectA);
                DrawRoiHitDebugOverlay(Pane.B, rectB);
                DrawRoiHitDebugOverlay(Pane.C, rectC);
            }

            if (!Application.isPlaying)
            {
                ClearTouchRoiDrag();
                _activeRoiHandle = RoiHandle.None;
                _activeRoiPane = Pane.None;
            }
        }

        private void OnDrawGizmos()
        {
            if (!enableRoiBox)
            {
                return;
            }

            ResolveTarget();
            if (targetRenderer == null)
            {
                return;
            }

            var roiMin = GetRoiMin01();
            var roiMax = GetRoiMax01();
            var localMin = roiMin - Vector3.one * 0.5f;
            var localMax = roiMax - Vector3.one * 0.5f;
            var localCenter = 0.5f * (localMin + localMax);
            var localSize = new Vector3(
                Mathf.Max(0.001f, localMax.x - localMin.x),
                Mathf.Max(0.001f, localMax.y - localMin.y),
                Mathf.Max(0.001f, localMax.z - localMin.z));

            var previousMatrix = Gizmos.matrix;
            var previousColor = Gizmos.color;

            Gizmos.matrix = targetRenderer.transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 1f, 1f, 0.45f);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);

            Gizmos.color = GetPaneColor(Pane.A);
            DrawYzFace(localMin.x, localMin.y, localMax.y, localMin.z, localMax.z);
            DrawYzFace(localMax.x, localMin.y, localMax.y, localMin.z, localMax.z);

            Gizmos.color = GetPaneColor(Pane.B);
            DrawXzFace(localMin.y, localMin.x, localMax.x, localMin.z, localMax.z);
            DrawXzFace(localMax.y, localMin.x, localMax.x, localMin.z, localMax.z);

            Gizmos.color = GetPaneColor(Pane.C);
            DrawXyFace(localMin.z, localMin.x, localMax.x, localMin.y, localMax.y);
            DrawXyFace(localMax.z, localMin.x, localMax.x, localMin.y, localMax.y);

            Gizmos.color = new Color(1f, 1f, 1f, 0.2f);
            Gizmos.DrawWireCube(localCenter, localSize);

            Gizmos.matrix = previousMatrix;
            Gizmos.color = previousColor;
        }

        private bool TryBeginRoiDrag(Pane pane, Rect paneRect, Vector2 mousePos)
        {
            if (!paneRect.Contains(mousePos))
            {
                return false;
            }

            var startPoint = GetRoiHandleScreenPoint(paneRect, pane, RoiHandle.Start);
            var endPoint   = GetRoiHandleScreenPoint(paneRect, pane, RoiHandle.End);
            var grabHalf = GetRoiGrabHalf(pane, paneRect);

            var distStart = ChebyshevDistance(mousePos, startPoint);
            var distEnd   = ChebyshevDistance(mousePos, endPoint);

            // Prefer whichever handle is closer.
            if (distStart <= grabHalf && (distStart <= distEnd || distEnd > grabHalf))
            {
                _activeRoiPane   = pane;
                _activeRoiHandle = RoiHandle.Start;
                return true;
            }

            if (distEnd <= grabHalf)
            {
                _activeRoiPane   = pane;
                _activeRoiHandle = RoiHandle.End;
                return true;
            }

            return false;
        }



        private void DrawRoiPaneOverlay(Pane pane, Rect contentRect, bool drawHandles)
        {
            if (contentRect.width <= 1f || contentRect.height <= 1f)
            {
                return;
            }

            var startUv = RoiToPaneUv(pane, roiStart01);
            var endUv = RoiToPaneUv(pane, roiEnd01);
            var uMin = Mathf.Min(startUv.x, endUv.x);
            var uMax = Mathf.Max(startUv.x, endUv.x);
            var vMin = Mathf.Min(startUv.y, endUv.y);
            var vMax = Mathf.Max(startUv.y, endUv.y);

            var lineThickness = GetScaledRoiLineThickness(pane, contentRect);
            var handleSize = GetScaledRoiHandleSize(pane, contentRect);

            var roiRect = PaneUvRectToScreenRect(contentRect, uMin, vMin, uMax, vMax);
            var startPoint = GetRoiHandleScreenPoint(contentRect, pane, RoiHandle.Start);
            var endPoint = GetRoiHandleScreenPoint(contentRect, pane, RoiHandle.End);
            var rotationZ = GetSliceViewRotationZ(pane);
            var previousMatrix = GUI.matrix;

            if (Mathf.Abs(rotationZ) > 0.01f)
            {
                GUIUtility.RotateAroundPivot(rotationZ, contentRect.center);
            }

            DrawRectOutline(roiRect, GetPaneColor(pane), lineThickness);

            if (drawHandles)
            {
                DrawHandle(startPoint, handleSize, new Color(0.2f, 1.0f, 0.35f, 1.0f));
                DrawHandle(endPoint, handleSize, new Color(1.0f, 0.35f, 0.35f, 1.0f));
            }

            GUI.matrix = previousMatrix;
        }

        public void SetRoiHandleFromUi(UiPane pane, bool isStartHandle, float overlayU, float overlayV)
        {
            if (!enableRoiBox)
            {
                return;
            }

            ResolveUiRoiHandleRefsIfNeeded();

            var p = pane == UiPane.A ? Pane.A : pane == UiPane.B ? Pane.B : Pane.C;
            var overlay = GetRoiOverlay(pane);
            if (!TryOverlayUvToContentUv(p, overlay, overlayU, overlayV, out var contentU, out var contentV))
            {
                contentU = overlayU;
                contentV = overlayV;
            }

            SetRoiHandle(p, isStartHandle ? RoiHandle.Start : RoiHandle.End, contentU, contentV);
            MarkApplyDirty(MprApplyDirty.Roi);
        }

        private RectTransform GetRoiOverlay(UiPane pane)
        {
            switch (pane)
            {
                case UiPane.A:
                    return roiOverlayA;
                case UiPane.B:
                    return roiOverlayB;
                default:
                    return roiOverlayC;
            }
        }

        private bool TryOverlayUvToContentUv(
            Pane pane,
            RectTransform overlay,
            float overlayU,
            float overlayV,
            out float contentU,
            out float contentV)
        {
            contentU = overlayU;
            contentV = overlayV;

            if (overlay == null)
            {
                return false;
            }

            var overlayRect = GetRectTransformGuiRect(overlay);
            if (overlayRect.width <= 1f || overlayRect.height <= 1f)
            {
                return false;
            }

            var guiPoint = PaneUvToScreenPoint(overlayRect, overlayU, overlayV);
            var contentRect = GetRoiDrawingRect(pane);
            if (contentRect.width <= 1f || contentRect.height <= 1f)
            {
                return false;
            }

            var contentUv = ScreenToPaneUv(contentRect, guiPoint);
            contentU = contentUv.x;
            contentV = contentUv.y;
            return true;
        }

        private void SyncUiRoiHandles()
        {
            if (!useUiRoiHandles)
            {
                return;
            }

            ResolveUiRoiHandleRefsIfNeeded();

            SyncUiRoiHandlesForPane(UiPane.A, roiOverlayA, roiHandleStartA, roiHandleEndA);
            SyncUiRoiHandlesForPane(UiPane.B, roiOverlayB, roiHandleStartB, roiHandleEndB);
            SyncUiRoiHandlesForPane(UiPane.C, roiOverlayC, roiHandleStartC, roiHandleEndC);
        }

        private void ResolveUiRoiHandleRefsIfNeeded()
        {
            // Allow the scene to work without manual wiring: find overlays/handles under each RawImage.
            if (roiOverlayA == null && rawImageA != null)
            {
                roiOverlayA = rawImageA.transform.Find("RoiOverlay") as RectTransform;
            }
            if (roiOverlayB == null && rawImageB != null)
            {
                roiOverlayB = rawImageB.transform.Find("RoiOverlay") as RectTransform;
            }
            if (roiOverlayC == null && rawImageC != null)
            {
                roiOverlayC = rawImageC.transform.Find("RoiOverlay") as RectTransform;
            }

            if (roiHandleStartA == null && roiOverlayA != null) roiHandleStartA = roiOverlayA.Find("RoiHandle_Start") as RectTransform;
            if (roiHandleEndA == null && roiOverlayA != null) roiHandleEndA = roiOverlayA.Find("RoiHandle_End") as RectTransform;
            if (roiHandleStartB == null && roiOverlayB != null) roiHandleStartB = roiOverlayB.Find("RoiHandle_Start") as RectTransform;
            if (roiHandleEndB == null && roiOverlayB != null) roiHandleEndB = roiOverlayB.Find("RoiHandle_End") as RectTransform;
            if (roiHandleStartC == null && roiOverlayC != null) roiHandleStartC = roiOverlayC.Find("RoiHandle_Start") as RectTransform;
            if (roiHandleEndC == null && roiOverlayC != null) roiHandleEndC = roiOverlayC.Find("RoiHandle_End") as RectTransform;
        }

        private void SyncUiRoiHandlesForPane(UiPane pane, RectTransform overlay, RectTransform startHandle, RectTransform endHandle)
        {
            if (overlay == null || startHandle == null || endHandle == null)
            {
                return;
            }

            // Match OnGUI handle positions: use the same content rect + screen-point path, then map into overlay UV space.
            var p = pane == UiPane.A ? Pane.A : pane == UiPane.B ? Pane.B : Pane.C;
            var contentRect = GetRoiDrawingRect(p);
            var overlayRect = GetRectTransformGuiRect(overlay);
            var startGui = GetRoiHandleScreenPoint(contentRect, p, RoiHandle.Start);
            var endGui = GetRoiHandleScreenPoint(contentRect, p, RoiHandle.End);
            var startUv = ScreenToPaneUv(overlayRect, startGui);
            var endUv = ScreenToPaneUv(overlayRect, endGui);

            PlaceUiHandle(startHandle, overlay, startUv);
            PlaceUiHandle(endHandle, overlay, endUv);

            UpdateUiRoiOutline(p, overlay, startUv, endUv);
        }

        private static void PlaceUiHandle(RectTransform handle, RectTransform overlay, Vector2 uv)
        {
            uv.x = Mathf.Clamp01(uv.x);
            uv.y = Mathf.Clamp01(uv.y);

            handle.SetParent(overlay, worldPositionStays: false);
            handle.anchorMin = uv;
            handle.anchorMax = uv;
            handle.anchoredPosition = Vector2.zero;
        }

        private void UpdateUiRoiOutline(Pane pane, RectTransform overlay, Vector2 startUv, Vector2 endUv)
        {
            if (overlay == null)
            {
                return;
            }

            var uMin = Mathf.Clamp01(Mathf.Min(startUv.x, endUv.x));
            var uMax = Mathf.Clamp01(Mathf.Max(startUv.x, endUv.x));
            var vMin = Mathf.Clamp01(Mathf.Min(startUv.y, endUv.y));
            var vMax = Mathf.Clamp01(Mathf.Max(startUv.y, endUv.y));

            // Avoid degenerate rects.
            if (Mathf.Abs(uMax - uMin) < 1e-6f) uMax = Mathf.Clamp01(uMin + 1e-4f);
            if (Mathf.Abs(vMax - vMin) < 1e-6f) vMax = Mathf.Clamp01(vMin + 1e-4f);

            var thickness = GetScaledUiRoiLineThickness(pane, overlay);
            var color = GetPaneColor(pane);
            color.a = 1f;

            var top = EnsureUiOutlineSegment(overlay, "__RoiOutline_Top", color);
            var bottom = EnsureUiOutlineSegment(overlay, "__RoiOutline_Bottom", color);
            var left = EnsureUiOutlineSegment(overlay, "__RoiOutline_Left", color);
            var right = EnsureUiOutlineSegment(overlay, "__RoiOutline_Right", color);

            // Horizontal segments: stretch in X between uMin..uMax, fixed height.
            PlaceUiSegment(top, new Vector2(uMin, vMax), new Vector2(uMax, vMax), new Vector2(0f, thickness));
            PlaceUiSegment(bottom, new Vector2(uMin, vMin), new Vector2(uMax, vMin), new Vector2(0f, thickness));

            // Vertical segments: stretch in Y between vMin..vMax, fixed width.
            PlaceUiSegment(left, new Vector2(uMin, vMin), new Vector2(uMin, vMax), new Vector2(thickness, 0f));
            PlaceUiSegment(right, new Vector2(uMax, vMin), new Vector2(uMax, vMax), new Vector2(thickness, 0f));
        }

        private static RectTransform EnsureUiOutlineSegment(RectTransform overlay, string name, Color color)
        {
            var existing = overlay.Find(name) as RectTransform;
            if (existing != null)
            {
                var img = existing.GetComponent<Image>();
                if (img != null)
                {
                    img.color = color;
                    img.raycastTarget = false;
                }
                return existing;
            }

            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.layer = overlay.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(overlay, worldPositionStays: false);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.localPosition = Vector3.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;

            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;

            return rt;
        }

        private static void PlaceUiSegment(RectTransform segment, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta)
        {
            segment.anchorMin = anchorMin;
            segment.anchorMax = anchorMax;
            segment.anchoredPosition = Vector2.zero;
            segment.sizeDelta = sizeDelta;
        }

        private float GetScaledUiRoiLineThickness(Pane pane, RectTransform overlay)
        {
            // Match the intent of GetScaledRoiLineThickness/GetScaledRoiMetric, but in UI space.
            // We scale thickness based on how large the RenderTexture is displayed within the overlay.
            var rt = GetSliceRenderTexture(pane);
            if (rt == null || overlay == null)
            {
                return Mathf.Max(1f, roiLineThicknessPixels);
            }

            var canvas = overlay.GetComponentInParent<Canvas>();
            var scaleFactor = canvas != null ? Mathf.Max(0.01f, canvas.scaleFactor) : 1f;
            var overlayPixelExtent = Mathf.Max(1f, Mathf.Min(overlay.rect.width, overlay.rect.height) * scaleFactor);
            var rtExtent = Mathf.Max(1f, Mathf.Min(rt.width, rt.height));

            var displayScale = overlayPixelExtent / rtExtent;
            var scaled = roiLineThicknessPixels * displayScale;
            return Mathf.Clamp(scaled, 1f, 16f);
        }

        private float GetRoiGrabHalf(Pane pane, Rect paneRect)
        {
            // 1.5x visual handle size (half = 0.75x), 16 px minimum — matches TryBeginRoiDrag.
            return Mathf.Max(16f, GetScaledRoiHandleSize(pane, paneRect) * 0.75f);
        }

        private void DrawRoiHitDebugOverlay(Pane pane, Rect contentRect)
        {
            if (contentRect.width <= 1f || contentRect.height <= 1f)
            {
                return;
            }

            var sliceRawImage = GetSliceRawImage(pane);
            if (sliceRawImage != null)
            {
                var imageRect = GetRawImageScreenRect(sliceRawImage);
                DrawRectOutline(imageRect, new Color(0.35f, 0.85f, 1f, 0.9f), 2f);
            }

            DrawRectOutline(contentRect, new Color(1f, 0.92f, 0.2f, 0.95f), 2f);

            var startPoint = GetRoiHandleScreenPoint(contentRect, pane, RoiHandle.Start);
            var endPoint = GetRoiHandleScreenPoint(contentRect, pane, RoiHandle.End);
            var grabHalf = GetRoiGrabHalf(pane, contentRect);

            DrawChebyshevGrabZone(startPoint, grabHalf, new Color(0.2f, 1f, 0.35f, 0.95f));
            DrawChebyshevGrabZone(endPoint, grabHalf, new Color(1f, 0.35f, 0.35f, 0.95f));

            if (!Application.isPlaying)
            {
                return;
            }

            var mousePos = ToGuiPosition(Input.mousePosition);
            DrawCrosshair(mousePos, 10f, Color.white);

            if (!contentRect.Contains(mousePos))
            {
                return;
            }

            var distStart = ChebyshevDistance(mousePos, startPoint);
            var distEnd = ChebyshevDistance(mousePos, endPoint);
            var inStart = distStart <= grabHalf && (distStart <= distEnd || distEnd > grabHalf);
            var inEnd = distEnd <= grabHalf;
            var label = $"{pane} grab={grabHalf:F0}px  dStart={distStart:F0}  dEnd={distEnd:F0}  hit={(inStart ? "Start" : inEnd ? "End" : "none")}";
            DrawDebugLabel(mousePos + new Vector2(12f, 12f), label);
        }

        private static float ChebyshevDistance(Vector2 a, Vector2 b)
        {
            return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }

        private static void DrawChebyshevGrabZone(Vector2 center, float grabHalf, Color color)
        {
            var size = grabHalf * 2f;
            var rect = new Rect(center.x - grabHalf, center.y - grabHalf, size, size);
            DrawSolidRect(rect, new Color(color.r, color.g, color.b, 0.12f));
            DrawRectOutline(rect, color, 2f);
            DrawCrosshair(center, 4f, color);
        }

        private static void DrawCrosshair(Vector2 center, float armLength, Color color)
        {
            DrawSolidRect(new Rect(center.x - armLength, center.y - 1f, armLength * 2f, 2f), color);
            DrawSolidRect(new Rect(center.x - 1f, center.y - armLength, 2f, armLength * 2f), color);
        }

        private static void DrawDebugLabel(Vector2 guiPosition, string text)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = Color.white }
            };

            var size = style.CalcSize(new GUIContent(text));
            var bg = new Rect(guiPosition.x, guiPosition.y, size.x + 8f, size.y + 4f);
            DrawSolidRect(bg, new Color(0f, 0f, 0f, 0.65f));
            GUI.Label(new Rect(guiPosition.x + 4f, guiPosition.y + 2f, size.x, size.y), text, style);
        }

        private Vector2 GetRoiHandleScreenPoint(Rect paneRect, Pane pane, RoiHandle handle)
        {
            var value = handle == RoiHandle.Start ? roiStart01 : roiEnd01;
            var uv = RoiToPaneUv(pane, value);
            var u = uv.x;
            var v = uv.y;
            return PaneUvToScreenPoint(paneRect, u, v);
        }

        private void SetRoiHandle(Pane pane, RoiHandle handle, float u, float v)
        {
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);

            if (handle == RoiHandle.Start)
            {
                roiStart01 = PaneUvToRoiHandle(pane, roiStart01, u, v);
            }
            else if (handle == RoiHandle.End)
            {
                roiEnd01 = PaneUvToRoiHandle(pane, roiEnd01, u, v);
            }

            roiStart01 = Clamp01Vector(roiStart01);
            roiEnd01 = Clamp01Vector(roiEnd01);
        }

        private Rect GetRoiDrawingRect(Pane pane)
        {
            if (TryGetPaneContentRect(pane, out var contentRect))
            {
                return contentRect;
            }

            var sliceRawImage = GetSliceRawImage(pane);
            if (sliceRawImage != null)
            {
                return GetAspectFittedContentRectInRawImage(sliceRawImage, GetSliceRenderTexture(pane));
            }

            return GetPaneRect(pane);
        }

        private RenderTexture GetSliceRenderTexture(Pane pane)
        {
            switch (pane)
            {
                case Pane.A:
                    return _sliceRenderTextureA;
                case Pane.B:
                    return _sliceRenderTextureB;
                case Pane.C:
                    return _sliceRenderTextureC;
                case Pane.D3:
                    return _renderTexture3D;
                default:
                    return null;
            }
        }

        private float GetScaledRoiHandleSize(Pane pane, Rect contentRect)
        {
            return GetScaledRoiMetric(pane, contentRect, roiHandleSizePixels, 6f, 96f);
        }

        private float GetScaledRoiLineThickness(Pane pane, Rect contentRect)
        {
            return GetScaledRoiMetric(pane, contentRect, roiLineThicknessPixels, 1f, 16f);
        }

        private float GetScaledRoiMetric(Pane pane, Rect contentRect, float basePixels, float minPixels, float maxPixels)
        {
            var rt = GetSliceRenderTexture(pane);
            if (rt == null || GetSliceRawImage(pane) == null)
            {
                return basePixels;
            }

            var rtExtent = Mathf.Max(1f, Mathf.Min(rt.width, rt.height));
            var contentExtent = Mathf.Max(1f, Mathf.Min(contentRect.width, contentRect.height));
            var displayScale = contentExtent / rtExtent;
            return Mathf.Clamp(basePixels * displayScale, minPixels, maxPixels);
        }

        private static bool TryGetSlicePlaneCornerPoints01(
            Pane pane,
            float sliceX,
            float sliceY,
            float sliceZ,
            out Vector3 c0,
            out Vector3 c1,
            out Vector3 c2,
            out Vector3 c3)
        {
            c0 = c1 = c2 = c3 = default;
            switch (pane)
            {
                case Pane.A:
                    c0 = new Vector3(sliceX, 0f, 0f);
                    c1 = new Vector3(sliceX, 1f, 0f);
                    c2 = new Vector3(sliceX, 1f, 1f);
                    c3 = new Vector3(sliceX, 0f, 1f);
                    return true;
                case Pane.B:
                    c0 = new Vector3(0f, sliceY, 0f);
                    c1 = new Vector3(1f, sliceY, 0f);
                    c2 = new Vector3(1f, sliceY, 1f);
                    c3 = new Vector3(0f, sliceY, 1f);
                    return true;
                case Pane.C:
                    c0 = new Vector3(0f, 0f, sliceZ);
                    c1 = new Vector3(1f, 0f, sliceZ);
                    c2 = new Vector3(1f, 1f, sliceZ);
                    c3 = new Vector3(0f, 1f, sliceZ);
                    return true;
                default:
                    return false;
            }
        }

        private static Rect GetAspectFittedContentRectInRawImage(RawImage rawImage, RenderTexture rt)
        {
            var imageRect = GetRawImageScreenRect(rawImage);
            if (imageRect.width <= 1f || imageRect.height <= 1f)
            {
                return imageRect;
            }

            if (rt == null || rt.width <= 0 || rt.height <= 0)
            {
                return imageRect;
            }

            var imageAspect = imageRect.width / imageRect.height;
            var rtAspect = rt.width / (float)rt.height;
            if (imageAspect > rtAspect)
            {
                var fittedHeight = imageRect.width / rtAspect;
                var yOffset = (imageRect.height - fittedHeight) * 0.5f;
                return new Rect(imageRect.x, imageRect.y + yOffset, imageRect.width, fittedHeight);
            }

            var fittedWidth = imageRect.height * rtAspect;
            var xOffset = (imageRect.width - fittedWidth) * 0.5f;
            return new Rect(imageRect.x + xOffset, imageRect.y, fittedWidth, imageRect.height);
        }

        private static Rect ViewportBoundsToGuiRect(Vector2 viewportMin, Vector2 viewportMax, Rect targetGuiRect)
        {
            var x0 = targetGuiRect.x + viewportMin.x * targetGuiRect.width;
            var x1 = targetGuiRect.x + viewportMax.x * targetGuiRect.width;
            var y0 = targetGuiRect.y + (1f - viewportMax.y) * targetGuiRect.height;
            var y1 = targetGuiRect.y + (1f - viewportMin.y) * targetGuiRect.height;
            return Rect.MinMaxRect(Mathf.Min(x0, x1), Mathf.Min(y0, y1), Mathf.Max(x0, x1), Mathf.Max(y0, y1));
        }

        private bool TryGetRawImageSliceContentRect(Pane pane, RawImage rawImage, Camera cam, out Rect contentRect)
        {
            contentRect = default;
            if (cam == null || targetRenderer == null || rawImage == null)
            {
                return false;
            }

            if (!TryGetSlicePlaneCornerPoints01(pane, sliceX, sliceY, sliceZ, out var c0, out var c1, out var c2, out var c3))
            {
                return false;
            }

            var imageRect = GetRawImageScreenRect(rawImage);
            if (imageRect.width <= 1f || imageRect.height <= 1f)
            {
                return false;
            }

            var t = targetRenderer.transform;
            var worldCorners = new[]
            {
                t.TransformPoint(c0 - Vector3.one * 0.5f),
                t.TransformPoint(c1 - Vector3.one * 0.5f),
                t.TransformPoint(c2 - Vector3.one * 0.5f),
                t.TransformPoint(c3 - Vector3.one * 0.5f),
            };

            var viewportMin = new Vector2(float.MaxValue, float.MaxValue);
            var viewportMax = new Vector2(float.MinValue, float.MinValue);
            var hasVisibleCorner = false;

            foreach (var wp in worldCorners)
            {
                var vp = cam.WorldToViewportPoint(wp);
                if (vp.z <= 0f)
                {
                    continue;
                }

                hasVisibleCorner = true;
                viewportMin.x = Mathf.Min(viewportMin.x, vp.x);
                viewportMin.y = Mathf.Min(viewportMin.y, vp.y);
                viewportMax.x = Mathf.Max(viewportMax.x, vp.x);
                viewportMax.y = Mathf.Max(viewportMax.y, vp.y);
            }

            if (!hasVisibleCorner)
            {
                contentRect = GetAspectFittedContentRectInRawImage(rawImage, GetSliceRenderTexture(pane));
                return contentRect.width > 1f && contentRect.height > 1f;
            }

            viewportMin.x = Mathf.Clamp01(viewportMin.x);
            viewportMin.y = Mathf.Clamp01(viewportMin.y);
            viewportMax.x = Mathf.Clamp01(viewportMax.x);
            viewportMax.y = Mathf.Clamp01(viewportMax.y);

            contentRect = ViewportBoundsToGuiRect(viewportMin, viewportMax, imageRect);
            contentRect.xMin = Mathf.Max(contentRect.xMin, imageRect.xMin);
            contentRect.yMin = Mathf.Max(contentRect.yMin, imageRect.yMin);
            contentRect.xMax = Mathf.Min(contentRect.xMax, imageRect.xMax);
            contentRect.yMax = Mathf.Min(contentRect.yMax, imageRect.yMax);
            return contentRect.width > 1f && contentRect.height > 1f;
        }

        private bool TryGetPaneContentRect(Pane pane, out Rect contentRect)
        {
            contentRect = default;

            if (pane == Pane.D3)
            {
                if (rawImage3D != null)
                {
                    contentRect = GetAspectFittedContentRectInRawImage(rawImage3D, _renderTexture3D);
                    return contentRect.width > 1f && contentRect.height > 1f;
                }

                return false;
            }

            var cam = GetPaneCamera(pane);
            if (cam == null || targetRenderer == null)
            {
                return false;
            }

            var sliceRawImage = GetSliceRawImage(pane);
            if (sliceRawImage != null)
            {
                return TryGetRawImageSliceContentRect(pane, sliceRawImage, cam, out contentRect);
            }

            var paneRect = GetPaneRect(pane);
            if (paneRect.width <= 1f || paneRect.height <= 1f)
            {
                return false;
            }

            if (!TryGetSlicePlaneCornerPoints01(pane, sliceX, sliceY, sliceZ, out var c0, out var c1, out var c2, out var c3))
            {
                return false;
            }

            var t = targetRenderer.transform;
            var worldCorners = new[]
            {
                t.TransformPoint(c0 - Vector3.one * 0.5f),
                t.TransformPoint(c1 - Vector3.one * 0.5f),
                t.TransformPoint(c2 - Vector3.one * 0.5f),
                t.TransformPoint(c3 - Vector3.one * 0.5f),
            };

            var xMin = float.MaxValue;
            var yMin = float.MaxValue;
            var xMax = float.MinValue;
            var yMax = float.MinValue;

            foreach (var wp in worldCorners)
            {
                var sp = cam.WorldToScreenPoint(wp);
                var guiX = sp.x;
                var guiY = Screen.height - sp.y;
                if (guiX < xMin) xMin = guiX;
                if (guiX > xMax) xMax = guiX;
                if (guiY < yMin) yMin = guiY;
                if (guiY > yMax) yMax = guiY;
            }

            xMin = Mathf.Max(xMin, paneRect.xMin);
            yMin = Mathf.Max(yMin, paneRect.yMin);
            xMax = Mathf.Min(xMax, paneRect.xMax);
            yMax = Mathf.Min(yMax, paneRect.yMax);

            contentRect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return contentRect.width > 1f && contentRect.height > 1f;
        }

        private Camera GetPaneCamera(Pane pane)
        {
            switch (pane)
            {
                case Pane.A:
                    return cameraA;
                case Pane.B:
                    return cameraB;
                case Pane.C:
                    return cameraC;
                case Pane.D3:
                    return camera3D;
                default:
                    return null;
            }
        }

        private MprPlane GetPanePlane(Pane pane)
        {
            switch (pane)
            {
                case Pane.A:
                    return _planeA;
                case Pane.B:
                    return _planeB;
                case Pane.C:
                    return _planeC;
                default:
                    return null;
            }
        }

        private Rect GetPaneRect(Pane pane)
        {
            var paneRawImage = GetPaneRawImage(pane);
            if (paneRawImage != null)
            {
                return GetRawImageScreenRect(paneRawImage);
            }

            var cam = GetPaneCamera(pane);
            if (cam != null)
            {
                var pixelRect = cam.pixelRect;
                return new Rect(
                    pixelRect.x,
                    Screen.height - pixelRect.y - pixelRect.height,
                    pixelRect.width,
                    pixelRect.height);
            }

            return GetDefaultPaneRect(pane);
        }

        private static Rect GetRawImageScreenRect(RawImage rawImage)
        {
            return rawImage == null ? default : GetRectTransformGuiRect(rawImage.rectTransform);
        }

        private static Camera ResolveUiScreenCamera(RectTransform rectTransform)
        {
            if (rectTransform == null)
            {
                return null;
            }

            var canvas = rectTransform.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                return null;
            }

            return canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
        }

        private static Rect GetRectTransformGuiRect(RectTransform rectTransform)
        {
            if (rectTransform == null)
            {
                return default;
            }

            var screenCamera = ResolveUiScreenCamera(rectTransform);
            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            var xMin = float.MaxValue;
            var yMin = float.MaxValue;
            var xMax = float.MinValue;
            var yMax = float.MinValue;

            for (var i = 0; i < corners.Length; ++i)
            {
                var screenPoint = RectTransformUtility.WorldToScreenPoint(screenCamera, corners[i]);
                if (screenPoint.x < xMin) xMin = screenPoint.x;
                if (screenPoint.x > xMax) xMax = screenPoint.x;
                if (screenPoint.y < yMin) yMin = screenPoint.y;
                if (screenPoint.y > yMax) yMax = screenPoint.y;
            }

            return Rect.MinMaxRect(
                xMin,
                Screen.height - yMax,
                xMax,
                Screen.height - yMin);
        }

        private static Rect GetDefaultPaneRect(Pane pane)
        {
            var screenWidth = Mathf.Max(1, Screen.width);
            var screenHeight = Mathf.Max(1, Screen.height);

            Rect normRect;
            switch (pane)
            {
                case Pane.A:
                    normRect = new Rect(0f, 0.5f, 0.5f, 0.5f);
                    break;
                case Pane.B:
                    normRect = new Rect(0.5f, 0.5f, 0.5f, 0.5f);
                    break;
                case Pane.C:
                    normRect = new Rect(0f, 0f, 0.5f, 0.5f);
                    break;
                case Pane.D3:
                    normRect = new Rect(0.5f, 0f, 0.5f, 0.5f);
                    break;
                default:
                    normRect = new Rect(0f, 0f, 1f, 1f);
                    break;
            }

            return new Rect(
                normRect.x * screenWidth,
                (1f - normRect.y - normRect.height) * screenHeight,
                normRect.width * screenWidth,
                normRect.height * screenHeight);
        }

        private static Rect PaneUvRectToScreenRect(Rect paneRect, float uMin, float vMin, float uMax, float vMax)
        {
            var x0 = paneRect.x + Mathf.Clamp01(uMin) * paneRect.width;
            var y0 = paneRect.y + (1f - Mathf.Clamp01(vMax)) * paneRect.height;
            var x1 = paneRect.x + Mathf.Clamp01(uMax) * paneRect.width;
            var y1 = paneRect.y + (1f - Mathf.Clamp01(vMin)) * paneRect.height;

            return Rect.MinMaxRect(Mathf.Min(x0, x1), Mathf.Min(y0, y1), Mathf.Max(x0, x1), Mathf.Max(y0, y1));
        }

        private static Vector2 PaneUvToScreenPoint(Rect paneRect, float u, float v)
        {
            return new Vector2(
                paneRect.x + Mathf.Clamp01(u) * paneRect.width,
                paneRect.y + (1f - Mathf.Clamp01(v)) * paneRect.height);
        }

        private static Vector2 ScreenToPaneUv(Rect paneRect, Vector2 screenPoint)
        {
            if (paneRect.width <= 0f || paneRect.height <= 0f)
            {
                return new Vector2(0.5f, 0.5f);
            }

            var u = (screenPoint.x - paneRect.x) / paneRect.width;
            var v = 1f - ((screenPoint.y - paneRect.y) / paneRect.height);
            return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
        }

        private static Vector2 RoiToPaneUv(Pane pane, Vector3 roi)
        {
            switch (pane)
            {
                case Pane.A:
                    return new Vector2(1.0f - roi.z, roi.y);
                case Pane.B:
                    return new Vector2(roi.x, 1.0f - roi.z);
                case Pane.C:
                    return new Vector2(roi.x, roi.y);
                default:
                    return new Vector2(roi.x, roi.y);
            }
        }

        private static Vector3 PaneUvToRoiHandle(Pane pane, Vector3 current, float u, float v)
        {
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);

            switch (pane)
            {
                case Pane.A:
                    current.y = v;
                    current.z = 1.0f - u;
                    break;
                case Pane.B:
                    current.x = u;
                    current.z = 1.0f - v;
                    break;
                case Pane.C:
                    current.x = u;
                    current.y = v;
                    break;
            }

            return current;
        }

        private Vector3 GetRoiMin01()
        {
            return new Vector3(
                Mathf.Min(roiStart01.x, roiEnd01.x),
                Mathf.Min(roiStart01.y, roiEnd01.y),
                Mathf.Min(roiStart01.z, roiEnd01.z));
        }

        private Vector3 GetRoiMax01()
        {
            return new Vector3(
                Mathf.Max(roiStart01.x, roiEnd01.x),
                Mathf.Max(roiStart01.y, roiEnd01.y),
                Mathf.Max(roiStart01.z, roiEnd01.z));
        }

        private static Vector3 Clamp01Vector(Vector3 value)
        {
            return new Vector3(
                Mathf.Clamp01(value.x),
                Mathf.Clamp01(value.y),
                Mathf.Clamp01(value.z));
        }

        private static void DrawRectOutline(Rect rect, Color color, float thickness)
        {
            var t = Mathf.Max(1f, thickness);
            DrawSolidRect(new Rect(rect.xMin, rect.yMin, rect.width, t), color);
            DrawSolidRect(new Rect(rect.xMin, rect.yMax - t, rect.width, t), color);
            DrawSolidRect(new Rect(rect.xMin, rect.yMin, t, rect.height), color);
            DrawSolidRect(new Rect(rect.xMax - t, rect.yMin, t, rect.height), color);
        }

        private static void DrawHandle(Vector2 center, float size, Color color)
        {
            var half = 0.5f * Mathf.Max(4f, size);
            DrawSolidRect(new Rect(center.x - half, center.y - half, half * 2f, half * 2f), color);
        }

        private static void DrawSolidRect(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private Color GetPaneColor(Pane pane)
        {
            switch (pane)
            {
                case Pane.A:
                    return _planeA != null ? _planeA.Color : PlaneColorA;
                case Pane.B:
                    return _planeB != null ? _planeB.Color : PlaneColorB;
                case Pane.C:
                    return _planeC != null ? _planeC.Color : PlaneColorC;
                default:
                    return roiMarkerColor;
            }
        }

        private static void DrawYzFace(float x, float yMin, float yMax, float zMin, float zMax)
        {
            var p0 = new Vector3(x, yMin, zMin);
            var p1 = new Vector3(x, yMax, zMin);
            var p2 = new Vector3(x, yMax, zMax);
            var p3 = new Vector3(x, yMin, zMax);
            Gizmos.DrawLine(p0, p1);
            Gizmos.DrawLine(p1, p2);
            Gizmos.DrawLine(p2, p3);
            Gizmos.DrawLine(p3, p0);
        }

        private static void DrawXzFace(float y, float xMin, float xMax, float zMin, float zMax)
        {
            var p0 = new Vector3(xMin, y, zMin);
            var p1 = new Vector3(xMax, y, zMin);
            var p2 = new Vector3(xMax, y, zMax);
            var p3 = new Vector3(xMin, y, zMax);
            Gizmos.DrawLine(p0, p1);
            Gizmos.DrawLine(p1, p2);
            Gizmos.DrawLine(p2, p3);
            Gizmos.DrawLine(p3, p0);
        }

        private static void DrawXyFace(float z, float xMin, float xMax, float yMin, float yMax)
        {
            var p0 = new Vector3(xMin, yMin, z);
            var p1 = new Vector3(xMax, yMin, z);
            var p2 = new Vector3(xMax, yMax, z);
            var p3 = new Vector3(xMin, yMax, z);
            Gizmos.DrawLine(p0, p1);
            Gizmos.DrawLine(p1, p2);
            Gizmos.DrawLine(p2, p3);
            Gizmos.DrawLine(p3, p0);
        }

        private Pane GetMousePane()
        {
            return GetPaneFromScreenPosition(Input.mousePosition);
        }

        private Pane GetPaneFromScreenPosition(Vector2 screenPosition)
        {
            if (Screen.width <= 0 || Screen.height <= 0)
            {
                return Pane.None;
            }

            var nx = screenPosition.x / Screen.width;
            var ny = screenPosition.y / Screen.height;

            if (nx < 0f || nx > 1f || ny < 0f || ny > 1f)
            {
                return Pane.None;
            }

            var rawImageAActive = rawImageA != null && rawImageA.gameObject.activeInHierarchy;
            var rawImageBActive = rawImageB != null && rawImageB.gameObject.activeInHierarchy;
            var rawImageCActive = rawImageC != null && rawImageC.gameObject.activeInHierarchy;
            var rawImage3DActive = rawImage3D != null && rawImage3D.gameObject.activeInHierarchy;
            if (rawImageAActive || rawImageBActive || rawImageCActive || rawImage3DActive)
            {
                var guiPos = ToGuiPosition(screenPosition);
                if (rawImageAActive && GetRawImageScreenRect(rawImageA).Contains(guiPos))
                {
                    return Pane.A;
                }

                if (rawImageBActive && GetRawImageScreenRect(rawImageB).Contains(guiPos))
                {
                    return Pane.B;
                }

                if (rawImageCActive && GetRawImageScreenRect(rawImageC).Contains(guiPos))
                {
                    return Pane.C;
                }

                if (rawImage3DActive && GetRawImageScreenRect(rawImage3D).Contains(guiPos))
                {
                    return Pane.D3;
                }

                return Pane.None;
            }

            if (nx < 0.5f && ny >= 0.5f)
            {
                return Pane.A;
            }

            if (nx >= 0.5f && ny >= 0.5f)
            {
                return Pane.B;
            }

            if (nx < 0.5f && ny < 0.5f)
            {
                return Pane.C;
            }

            if (nx >= 0.5f && ny < 0.5f)
            {
                return Pane.D3;
            }

            return Pane.None;
        }

        private void AdjustPosition(Pane pane, float delta)
        {
            switch (pane)
            {
                case Pane.A:
                    sliceX = Mathf.Clamp01(sliceX + delta);
                    break;
                case Pane.B:
                    sliceY = Mathf.Clamp01(sliceY + delta);
                    break;
                case Pane.C:
                    sliceZ = Mathf.Clamp01(sliceZ + delta);
                    break;
                default:
                    return;
            }

            MarkApplyDirty(MprApplyDirty.PlaneTransforms | MprApplyDirty.CameraPoses);
        }

        private void AdjustScale(Pane pane, float delta)
        {
            switch (pane)
            {
                case Pane.A:
                    scaleX = Mathf.Clamp(scaleX + delta, minScale, maxScale);
                    break;
                case Pane.B:
                    scaleY = Mathf.Clamp(scaleY + delta, minScale, maxScale);
                    break;
                case Pane.C:
                    scaleZ = Mathf.Clamp(scaleZ + delta, minScale, maxScale);
                    break;
                default:
                    return;
            }

            MarkApplyDirty(MprApplyDirty.PlaneTransforms | MprApplyDirty.CameraPoses);
        }

        private void DisableLegacySingleSlice()
        {
            var legacy = targetRenderer.transform.Find(LegacySlicePlaneName);
            if (legacy != null)
            {
                legacy.gameObject.SetActive(false);
            }
        }



        private void ConfigureIndicator(LineRenderer line, Color color)
        {
            if (line == null)
            {
                return;
            }

            if (_indicatorMaterial == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    _indicatorMaterial = new Material(shader)
                    {
                        name = "SimpleITK_MPR_IndicatorMaterial",
                        hideFlags = HideFlags.DontSave
                    };
                }
            }

            if (line.sharedMaterial != _indicatorMaterial)
            {
                line.sharedMaterial = _indicatorMaterial;
            }

            if (line.positionCount != IndicatorLocalPoints.Length)
            {
                line.positionCount = IndicatorLocalPoints.Length;
                line.SetPositions(IndicatorLocalPoints);
            }

            if (line.useWorldSpace)
            {
                line.useWorldSpace = false;
            }

            if (!Mathf.Approximately(line.widthMultiplier, 0.006f))
            {
                line.widthMultiplier = 0.006f;
            }

            if (line.loop)
            {
                line.loop = false;
            }

            if (line.numCornerVertices != 0)
            {
                line.numCornerVertices = 0;
            }

            if (line.numCapVertices != 0)
            {
                line.numCapVertices = 0;
            }

            if (line.textureMode != LineTextureMode.Stretch)
            {
                line.textureMode = LineTextureMode.Stretch;
            }

            if (line.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
            {
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            if (line.receiveShadows)
            {
                line.receiveShadows = false;
            }

            var indicatorColor = color;
            indicatorColor.a = planeIndicatorOpacity;
            if (line.startColor != indicatorColor)
            {
                line.startColor = indicatorColor;
            }

            if (line.endColor != indicatorColor)
            {
                line.endColor = indicatorColor;
            }
        }

        private static Camera CreateCamera(string name, Transform parent, bool addAudioListener)
        {
            var go = new GameObject(name);
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            var cam = go.AddComponent<Camera>();
            if (addAudioListener)
            {
                go.AddComponent<AudioListener>();
            }

            return cam;
        }

        private void EnsureCameraParent(Camera cam)
        {
            if (cam == null || _cameraRoot == null)
            {
                return;
            }

            if (cam.transform.parent != _cameraRoot)
            {
                cam.transform.SetParent(_cameraRoot, true);
            }

            if ((cam.transform.localScale - Vector3.one).sqrMagnitude > 1e-8f)
            {
                cam.transform.localScale = Vector3.one;
            }
        }

#if UNITY_EDITOR
        private void QueueEditorRefreshAfterValidate()
        {
            if (_editorValidateRefreshQueued)
            {
                return;
            }

            _editorValidateRefreshQueued = true;
            EditorApplication.delayCall += ApplyEditorRefreshAfterValidate;
        }

        private void ApplyEditorRefreshAfterValidate()
        {
            _editorValidateRefreshQueued = false;
            if (this == null || !isActiveAndEnabled || Application.isPlaying)
            {
                return;
            }



            ResolveTarget();
            _setupDirty = true;
            MarkApplyDirty(MprApplyDirty.All);
            EnsureSetup();
            _setupDirty = false;
            ApplyAll();
        }

#endif

        private static void EnsureUprCameraData(Camera cam)
        {
            if (cam == null)
            {
                return;
            }

            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null)
            {
                data = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            }

            data.renderType = CameraRenderType.Base;
            data.cameraStack.Clear();
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            data.renderShadows = false;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
        }

        private static void EnsureCameraEnabled(Camera cam)
        {
            if (cam == null)
            {
                return;
            }

            if (!cam.gameObject.activeSelf)
            {
                cam.gameObject.SetActive(true);
            }

            cam.targetTexture = null;
            cam.enabled = true;
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            if (go == null)
            {
                return;
            }

            go.layer = layer;
            var transform = go.transform;
            for (var i = 0; i < transform.childCount; ++i)
            {
                SetLayerRecursively(transform.GetChild(i).gameObject, layer);
            }
        }

        private Quaternion GetReferenceVolumeWorldRotation()
        {
            if (_initialVolumeTransformCaptured)
            {
                return _initialVolumeWorldRotation;
            }

            if (targetRenderer != null)
            {
                return targetRenderer.transform.rotation;
            }

            return Quaternion.identity;
        }

        private Vector3 GetInitial3DViewForwardLocal()
        {
            var yawRadians = Initial3DViewYawDegrees * Mathf.Deg2Rad;
            var pitchRadians = Initial3DViewPitchDegrees * Mathf.Deg2Rad;
            var cosPitch = Mathf.Cos(pitchRadians);
            var forward = new Vector3(
                Mathf.Sin(yawRadians) * cosPitch,
                -Mathf.Sin(pitchRadians),
                Mathf.Cos(yawRadians) * cosPitch);

            return forward.sqrMagnitude > 1e-8f ? forward.normalized : Vector3.forward;
        }

        private Vector3 GetInitial3DViewUpLocal(Vector3 forwardLocal)
        {
            var upLocal = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(forwardLocal, upLocal)) > 0.98f)
            {
                upLocal = Vector3.forward;
            }

            var rightLocal = Vector3.Cross(upLocal, forwardLocal);
            if (rightLocal.sqrMagnitude <= 1e-8f)
            {
                rightLocal = Vector3.right;
            }

            rightLocal.Normalize();
            upLocal = Vector3.Cross(forwardLocal, rightLocal).normalized;
            return (Quaternion.AngleAxis(Initial3DViewRollDegrees, forwardLocal) * upLocal).normalized;
        }
    }
}
