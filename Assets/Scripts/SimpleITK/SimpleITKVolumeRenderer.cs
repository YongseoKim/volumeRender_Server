using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SimpleITKUnity
{
    internal static class SimpleITKSegmentationColorMap
    {
        private static readonly Regex EntryPattern = new Regex(
            @"^\s*(?<label>\d+)\s*,\s*(?<name>[^,]+?)\s*,\s*\(\s*(?<r>\d{1,3})\s*,\s*(?<g>\d{1,3})\s*,\s*(?<b>\d{1,3})(?:\s*,\s*(?<a>\d{1,3}))?\s*\)\s*$",
            RegexOptions.Compiled);

        public const int PaletteSize = 256;
        public const string DefaultFileName = "BraTSSegmentationColorMap.txt";

        public static bool TryResolveStreamingAssetsPath(string fileName, out string resolvedPath, out string error)
        {
            resolvedPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                error = "segmentation color map file name is empty.";
                return false;
            }

            var normalized = fileName.Replace('\\', '/').Trim();
            while (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1);
            }

            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "segmentation color map file name is empty.";
                return false;
            }

            if (Path.IsPathRooted(normalized) || normalized.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                error = $"segmentation color map file name must be relative to StreamingAssets: {fileName}";
                return false;
            }

            var streamingAssetsPath = Application.streamingAssetsPath;
            if (string.IsNullOrWhiteSpace(streamingAssetsPath))
            {
                error = "Application.streamingAssetsPath is empty.";
                return false;
            }

            if (streamingAssetsPath.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                error =
                    $"StreamingAssets path is not a local filesystem path on this platform: {streamingAssetsPath}";
                return false;
            }

            resolvedPath = Path.GetFullPath(Path.Combine(streamingAssetsPath, normalized));
            return true;
        }

        public static bool TryLoadPaletteFromStreamingAssets(
            string fileName,
            out Color[] colors,
            out string error)
        {
            colors = null;

            if (!TryResolveStreamingAssetsPath(fileName, out var resolvedPath, out error))
            {
                return false;
            }

            if (!File.Exists(resolvedPath))
            {
                error = $"segmentation color map file does not exist: {resolvedPath}";
                return false;
            }

            var lines = File.ReadAllLines(resolvedPath);
            colors = CreateEmptyPalette();
            var appliedCount = 0;

            for (var i = 0; i < lines.Length; ++i)
            {
                var line = lines[i]?.Trim();
                if (string.IsNullOrWhiteSpace(line) ||
                    line.StartsWith("#", StringComparison.Ordinal) ||
                    line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryParseEntry(line, out var label, out _, out var color, out error))
                {
                    error = $"{resolvedPath}:{i + 1}: {error}";
                    return false;
                }

                colors[label] = color;
                appliedCount++;
            }

            if (appliedCount == 0)
            {
                error = $"segmentation color map file contains no valid entries: {resolvedPath}";
                colors = null;
                return false;
            }

            error = string.Empty;
            return true;
        }

        public static Color[] CreateDefaultPalette()
        {
            var colors = CreateEmptyPalette();
            colors[1] = new Color32(255, 140, 38, 140);
            colors[2] = new Color32(64, 242, 77, 89);
            colors[4] = new Color32(242, 64, 230, 166);
            return colors;
        }

        private static Color[] CreateEmptyPalette()
        {
            var colors = new Color[PaletteSize];
            for (var i = 0; i < colors.Length; ++i)
            {
                colors[i] = Color.clear;
            }

            return colors;
        }

        private static bool TryParseEntry(
            string line,
            out int label,
            out string displayName,
            out Color color,
            out string error)
        {
            label = 0;
            displayName = string.Empty;
            color = Color.clear;
            error = string.Empty;

            var match = EntryPattern.Match(line);
            if (!match.Success)
            {
                error = "expected format 'label,name,(r,g,b)' or 'label,name,(r,g,b,a)'.";
                return false;
            }

            if (!int.TryParse(match.Groups["label"].Value, out label) ||
                label < 0 || label >= PaletteSize)
            {
                error = $"label must be between 0 and {PaletteSize - 1}.";
                return false;
            }

            displayName = match.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                error = "display name must not be empty.";
                return false;
            }

            if (!TryParseByte(match.Groups["r"].Value, out var r) ||
                !TryParseByte(match.Groups["g"].Value, out var g) ||
                !TryParseByte(match.Groups["b"].Value, out var b))
            {
                error = "r, g, b values must be between 0 and 255.";
                return false;
            }

            byte a = 255;
            var alphaGroup = match.Groups["a"];
            if (alphaGroup.Success && !string.IsNullOrWhiteSpace(alphaGroup.Value))
            {
                if (!TryParseByte(alphaGroup.Value, out a))
                {
                    error = "alpha value must be between 0 and 255.";
                    return false;
                }
            }

            color = new Color32(r, g, b, a);
            return true;
        }

        private static bool TryParseByte(string value, out byte result)
        {
            result = 0;
            if (!int.TryParse(value, out var parsed) || parsed < 0 || parsed > 255)
            {
                return false;
            }

            result = (byte)parsed;
            return true;
        }
    }

    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SimpleITKVolumeRenderer : MonoBehaviour
    {
        public enum TransferFunctionMode
        {
            OneDimensional = 0,
            TwoDimensional = 1
        }

        public enum LightingTechnique
        {
            Unlit = 0,
            Isosurface = 1,
            CinematicStandard = 2,
            CinematicAmbientOcclusion = 3,
            CinematicHybrid = 4
        }

        public enum LightDirectionPreset
        {
            Custom = 0,
            Front = 1,
            Back = 2,
            Left = 3,
            Right = 4,
            Top = 5,
            Bottom = 6,
            FrontUpperLeft = 7,
            FrontUpperRight = 8,
            FrontLowerLeft = 9,
            FrontLowerRight = 10,
            BackUpperLeft = 11,
            BackUpperRight = 12,
            BackLowerLeft = 13,
            BackLowerRight = 14
        }

        public enum ColorPreset
        {
            Custom = 0,
            CinematicWarm = 1,
            Bone = 2,
            SoftTissue = 3,
            Vascular = 4,
            Fire = 5,
            CoolMetal = 6,
            PET = 7,
            Sunset = 8,
            Emerald = 9,
            Grayscale = 10
        }

        [Serializable]
        public struct TransferFunction2DBox
        {
            [Range(0f, 1f)] public float densityMin;
            [Range(0f, 1f)] public float densityMax;
            [Range(0f, 1f)] public float gradientMin;
            [Range(0f, 1f)] public float gradientMax;
            public Color color;
            [Range(0f, 1f)] public float alpha;
            [Range(0f, 1f)] public float edgeSoftness;
        }

        [Header("Loader")]
        [SerializeField] private SimpleITKVolumeLoader volumeLoader;
        [SerializeField, HideInInspector] private bool loadFromStreamingAssetsSample = true;
        [SerializeField, HideInInspector] private string sampleFileName = "Kitware_Head_and_Neck.mha";
        [SerializeField, HideInInspector] private string volumeFilePath = string.Empty;
        [SerializeField, HideInInspector] private bool loadOnStart = true;
        [SerializeField, HideInInspector] private bool loadInEditMode = true;
        [SerializeField] private bool fitObjectScaleToSpacing = true;
        [SerializeField, Tooltip("Play mode: render ABC RawImage slices before enabling 3D volume raymarching.")]
        private bool deferVolumeMeshRenderingOnLoad = true;
        [SerializeField, Tooltip("Play mode: copy voxel data into Texture3D over multiple frames to reduce load spikes.")]
        private bool spreadVolumeTextureUpload = true;
        [SerializeField, Min(65536), Tooltip("Voxels copied into the Texture3D staging buffer per frame when spread upload is enabled.")]
        private int volumeTextureUploadVoxelsPerFrame = 2_000_000;

        [SerializeField, HideInInspector] private SimpleITKProcessingController processingController;

        [Header("Rendering")]
        [SerializeField, InspectorName("Rendering Method")] private LightingTechnique lightingTechnique = LightingTechnique.CinematicStandard;
        [SerializeField] private Shader raymarchShader;
        [SerializeField] private Shader cinematicRaymarchShader;
        [SerializeField, Range(32, 2048)] private int maxSteps = 512;
        [SerializeField, Range(0.05f, 8f)] private float stepScale = 0.85f;
        [SerializeField, Range(0f, 8f)] private float opacityMultiplier = 0.9f;
        [SerializeField, Range(0f, 8f)] private float brightness = 1.05f;
        [SerializeField, InspectorName("3D Intensity Window Min"), Range(0f, 1f)] private float intensityWindowMin = 0.21343102f;
        [SerializeField, InspectorName("3D Intensity Window Max"), Range(0f, 1f)] private float intensityWindowMax = 0.3843712f;
        [SerializeField, InspectorName("Sync ABC To 3D")] private bool syncAbcIntensityWindowToVolume = false;
        [SerializeField, InspectorName("ABC Intensity Window Min"), Range(0f, 1f)] private float sliceIntensityWindowMin = 0.21343102f;
        [SerializeField, InspectorName("ABC Intensity Window Max"), Range(0f, 1f)] private float sliceIntensityWindowMax = 0.3843712f;
        [SerializeField] private bool autoFitIntensityWindowOnLoad = false;
        [SerializeField, Range(0.0f, 0.25f)] private float autoWindowLowPercentile = 0.02f;
        [SerializeField, Range(0.75f, 1.0f)] private float autoWindowHighPercentile = 0.995f;
        [HideInInspector]
        [SerializeField] private bool preferR16Format = false;

        [Header("Lighting Direction")]
        [SerializeField] private LightDirectionPreset lightDirectionPreset = LightDirectionPreset.Custom;
        [SerializeField, InspectorName("Custom World Vector")] private Vector3 lightDirectionWorld = new Vector3(-0.5f, -0.7f, -0.5f);
        [SerializeField, HideInInspector] private int lightingModeSchemaVersion = 4;

        [Header("Isosurface Rendering")]
        [SerializeField, Range(0f, 1f)] private float isoSurfaceThreshold = 0.35f;
        [SerializeField] private Color isoSurfaceColor = Color.white;
        [SerializeField, Range(0f, 2f)] private float isoSurfaceAmbient = 0.15f;
        [SerializeField, Range(0f, 4f)] private float isoSurfaceDiffuse = 1.1f;
        [SerializeField, Range(0f, 4f)] private float isoSurfaceSpecular = 0.35f;
        [SerializeField, Range(1f, 128f)] private float isoSurfaceSpecularPower = 32f;

        [Header("Cinematic Rendering")]
        [SerializeField] private bool cinematicLightFollowsCamera = true;
        [SerializeField, Range(0f, 2f)] private float cinematicAmbient = 0.18f;
        [SerializeField, Range(0f, 4f)] private float cinematicLightIntensity = 1.15f;
        [SerializeField, Range(0f, 4f)] private float cinematicDiffuse = 0.95f;
        [SerializeField, Range(0f, 4f)] private float cinematicSpecular = 0.24f;
        [SerializeField, Range(1f, 128f)] private float cinematicSpecularPower = 36f;
        [SerializeField, Range(0f, 1f)] private float cinematicShadowStrength = 0.5f;
        [SerializeField, Range(0.25f, 4f)] private float cinematicShadowStepMultiplier = 0.75f;
        [SerializeField, Range(8, 256)] private int cinematicShadowMaxSteps = 72;
        [SerializeField, Range(0f, 2f)] private float cinematicScatterBlend = 0.22f;
        [SerializeField, Range(0f, 8f)] private float cinematicExtinction = 0.35f;
        [SerializeField, Range(0f, 2f)] private float cinematicAoStrength = 0.55f;
        [SerializeField, Range(0.25f, 8f)] private float cinematicAoRadius = 1.75f;
        [SerializeField, Range(1, 8)] private int cinematicAoSamples = 6;
        [SerializeField, HideInInspector] private int cinematicParameterSchemaVersion = 3;

        [Header("Volume Transfer Function (3D)")]
        [SerializeField, HideInInspector] private SimpleITKTransferFunctionPresetLibrary transferFunctionPresetLibrary;
        [SerializeField, HideInInspector] private string volumeTransferFunctionPresetName = "CT-AAA";
        [SerializeField, HideInInspector] private bool applyVolumePresetEffectiveRangeToWindow = false;
        [SerializeField, HideInInspector] private ColorPreset volumeColorPreset = ColorPreset.Custom;
        [SerializeField] private TransferFunctionMode transferFunctionMode = TransferFunctionMode.OneDimensional;
        [SerializeField] private Gradient colorGradient;
        [SerializeField] private AnimationCurve alphaCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [SerializeField, HideInInspector] private int transferFunctionSchemaVersion = 2;
        [SerializeField, Range(16, 1024)] private int transferFunction1DResolution = 256;
        [SerializeField, Range(16, 1024)] private int transferFunction2DResolution = 256;
        [SerializeField, Range(0.25f, 8f)] private float gradientScale = 2.0f;
        [SerializeField] private List<TransferFunction2DBox> transferFunction2DBoxes = new List<TransferFunction2DBox>();

        [Header("Slice Transfer Function (ABC 2D Views)")]
        [SerializeField, HideInInspector] private string sliceTransferFunctionPresetName = "Grayscale";
        [SerializeField, HideInInspector] private ColorPreset sliceColorPreset = ColorPreset.Custom;
        [SerializeField] private TransferFunctionMode sliceTransferFunctionMode = TransferFunctionMode.OneDimensional;
        [SerializeField] private Gradient sliceColorGradient;
        [SerializeField] private AnimationCurve sliceAlphaCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [SerializeField, Range(0.25f, 8f)] private float sliceGradientScale = 2.0f;
        [SerializeField] private List<TransferFunction2DBox> sliceTransferFunction2DBoxes = new List<TransferFunction2DBox>();

        [Header("Segmentation Overlay")]
        [SerializeField] private bool showSegmentationOverlayInAbc = true;
        [SerializeField] private bool showSegmentationOverlayInVolume = true;
        [SerializeField, Range(0f, 1f)] private float segmentationOverlayOpacityInAbc = 0.45f;
        [SerializeField, Range(0f, 1f)] private float segmentationOverlayOpacityInVolume = 0.35f;
        [SerializeField, HideInInspector] private string segmentationColorMapFileName = SimpleITKSegmentationColorMap.DefaultFileName;

        [SerializeField, HideInInspector] private bool enableBoxCrossSection = false;
        [SerializeField, HideInInspector] private Vector3 boxCenter01 = new Vector3(0.5f, 0.5f, 0.5f);
        [SerializeField, HideInInspector] private Vector3 boxSize01 = new Vector3(0.4f, 0.4f, 0.4f);
        [SerializeField, HideInInspector] private bool invertBox = false;

        private Texture3D _volumeTexture;
        private Texture3D _segmentationTexture;
        private Texture2D _segmentationColorMapTexture;
        private Texture2D _transferTexture1D;
        private Texture2D _transferTexture2D;
        private Texture2D _sliceTransferTexture1D;
        private Texture2D _sliceTransferTexture2D;
        private Material _runtimeMaterial;
        private MeshRenderer _meshRenderer;
        private int _volumeWidth;
        private int _volumeHeight;
        private int _volumeDepth;
        [SerializeField, HideInInspector] private float loadedSourceMin = 0.0f;
        [SerializeField, HideInInspector] private float loadedSourceMax = 1.0f;
        private string _segmentationSourcePath = string.Empty;

        private bool _transfer1DDirty = true;
        private bool _transfer2DDirty = true;
        private bool _sliceTransfer1DDirty = true;
        private bool _sliceTransfer2DDirty = true;
        private bool _segmentationColorMapDirty = true;
        private bool _materialDirty = true;
        [SerializeField, HideInInspector] private bool _default1DOTFInitialized;
        [SerializeField, HideInInspector] private LightDirectionPreset _lastAppliedLightDirectionPreset = LightDirectionPreset.Custom;
        [SerializeField, HideInInspector] private string _lastAppliedVolumeTransferFunctionPresetName = string.Empty;
        [SerializeField, HideInInspector] private ColorPreset _lastAppliedVolumeColorPreset = ColorPreset.Custom;
        [SerializeField, HideInInspector] private string _lastAppliedSliceTransferFunctionPresetName = string.Empty;
        [SerializeField, HideInInspector] private ColorPreset _lastAppliedSliceColorPreset = ColorPreset.Custom;
        [SerializeField, HideInInspector] private string _lastSegmentationColorMapPath = string.Empty;
        [SerializeField, HideInInspector] private long _lastSegmentationColorMapWriteTimeUtcTicks;
        [SerializeField, HideInInspector] private string _lastSegmentationColorMapError = string.Empty;
        private bool _editorAutoLoadAttempted;
        private string _lastEditorAutoLoadPath = string.Empty;
        private bool _runtimeRecoverLoadRequested;
        private bool _deferVolumeMeshRendering;
        private int _sliceMaterialVersion;
#if UNITY_EDITOR
        private double _nextEditorRecoverLoadTime;
#endif

        private const string BasicShaderName = "SimpleITK/DirectVolumeRaymarch";
        private const string CinematicShaderName = "SimpleITK/CinematicVolumeRaymarch";
        private const int LightingModeSchemaCurrentVersion = 4;
        private const int CinematicParameterSchemaCurrentVersion = 3;
        private const int TransferFunctionSchemaCurrentVersion = 2;
        private const int SegmentationPaletteSize = SimpleITKSegmentationColorMap.PaletteSize;
        private static readonly Vector3 LegacyDefaultLightDirection = new Vector3(-0.5f, -0.7f, -0.5f);

        private void Reset()
        {
            EnsureShaderReferences();
            EnsureLightingModeSchema();
            EnsureCinematicParameterSchema();
            EnsureDefaultLightDirectionPreset();
            InitializeDefault1DOTF(force: true);
            EnsureDefault2DBoxes();
            EnsureDefaultSliceGradient();
            EnsureDefaultSlice2DBoxes();
            EnsureTransferFunctionSchema();
            EnsureSelectedLightDirectionPresetApplied();
            EnsureSelectedColorPresetsApplied();
            EnsureDefaultSegmentationColorMapFileName();
            EnsureCubeMesh();
        }

        private void OnEnable()
        {
            _meshRenderer = GetComponent<MeshRenderer>();
            _runtimeRecoverLoadRequested = false;
#if UNITY_EDITOR
            _nextEditorRecoverLoadTime = 0.0;
#endif
            EnsureLightingModeSchema();
            EnsureCinematicParameterSchema();
            EnsureDefaultLightDirectionPreset();
            EnsureShaderReferences();
            ResolveVolumeLoader();
            EnsureCubeMesh();
            InitializeDefault1DOTF(force: false);
            EnsureDefaultGradient();
            EnsureDefault2DBoxes();
            EnsureDefaultSliceGradient();
            EnsureDefaultSlice2DBoxes();
            EnsureTransferFunctionSchema();
            EnsureSelectedLightDirectionPresetApplied();
            EnsureSelectedColorPresetsApplied();
            EnsureDefaultSegmentationColorMapFileName();
            EnsureMaterial();
            EnsureTransferTexture1D();
            EnsureTransferTexture2D();
            EnsureSliceTransferTexture1D();
            EnsureSliceTransferTexture2D();
            ApplyMaterialProperties();
            if (volumeLoader == null)
            {
                TryAutoLoadInEditor();
            }
        }

        private void Start()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            ResolveVolumeLoader();
            if (volumeLoader != null)
            {
                return;
            }

            if (loadOnStart)
            {
                var resolvedLegacyPath = ResolveVolumePathLegacy();
                if (IsRemotePath(resolvedLegacyPath))
                {
                    Debug.LogError(
                        "SimpleITKVolumeRenderer: URL-based StreamingAssets path detected without SimpleITKVolumeLoader. " +
                        "Attach/enable SimpleITKVolumeLoader for Android/Web runtime loading.");
                    return;
                }

                LoadVolumeLegacy();
            }
        }

        private void Update()
        {
            if (_meshRenderer == null)
            {
                _meshRenderer = GetComponent<MeshRenderer>();
                _materialDirty = true;
            }

            var selectedShader = ResolveActiveRaymarchShader();
            if (_runtimeMaterial == null ||
                (_meshRenderer != null && _meshRenderer.sharedMaterial == null) ||
                (_runtimeMaterial != null && selectedShader != null && _runtimeMaterial.shader != selectedShader))
            {
                _materialDirty = true;
            }

            EnsureSelectedLightDirectionPresetApplied();
            EnsureSelectedColorPresetsApplied();
            SyncSliceIntensityWindowToVolumeIfNeeded();

            if (_transfer1DDirty)
            {
                RebuildTransferTexture1D();
            }

            if (_transfer2DDirty)
            {
                RebuildTransferTexture2D();
            }

            if (_sliceTransfer1DDirty)
            {
                RebuildSliceTransferTexture1D();
            }

            if (_sliceTransfer2DDirty)
            {
                RebuildSliceTransferTexture2D();
            }

            if (_materialDirty)
            {
                ApplyMaterialProperties();
            }

            if (_meshRenderer != null && _volumeTexture != null && !_deferVolumeMeshRendering)
            {
                if (_runtimeMaterial != null && _meshRenderer.sharedMaterial != _runtimeMaterial)
                {
                    _meshRenderer.sharedMaterial = _runtimeMaterial;
                }

                if (!_meshRenderer.enabled)
                {
                    _meshRenderer.enabled = true;
                }
            }

            TryRecoverLoadViaVolumeLoaderIfNeeded();

#if UNITY_EDITOR
            if (!Application.isPlaying && volumeLoader == null && loadInEditMode && _volumeTexture == null)
            {
                TryAutoLoadInEditor();
            }
#endif
        }

        private void OnValidate()
        {
            EnsureLightingModeSchema();
            EnsureCinematicParameterSchema();
            EnsureDefaultLightDirectionPreset();
            EnsureShaderReferences();

            if (intensityWindowMax < intensityWindowMin)
            {
                intensityWindowMax = intensityWindowMin;
            }

            if (sliceIntensityWindowMax < sliceIntensityWindowMin)
            {
                sliceIntensityWindowMax = sliceIntensityWindowMin;
            }

            SyncSliceIntensityWindowToVolumeIfNeeded();

            autoWindowLowPercentile = Mathf.Clamp(autoWindowLowPercentile, 0.0f, 0.25f);
            autoWindowHighPercentile = Mathf.Clamp(autoWindowHighPercentile, 0.75f, 1.0f);
            if (autoWindowHighPercentile <= autoWindowLowPercentile + 0.01f)
            {
                autoWindowHighPercentile = Mathf.Min(1.0f, autoWindowLowPercentile + 0.01f);
            }

            transferFunction1DResolution = Mathf.Clamp(transferFunction1DResolution, 16, 1024);
            transferFunction2DResolution = Mathf.Clamp(transferFunction2DResolution, 16, 1024);
            maxSteps = Mathf.Clamp(maxSteps, 32, 2048);
            stepScale = Mathf.Max(0.05f, stepScale);
            gradientScale = Mathf.Max(0.25f, gradientScale);
            sliceGradientScale = Mathf.Max(0.25f, sliceGradientScale);
            isoSurfaceThreshold = Mathf.Clamp01(isoSurfaceThreshold);
            isoSurfaceAmbient = Mathf.Clamp(isoSurfaceAmbient, 0f, 2f);
            isoSurfaceDiffuse = Mathf.Clamp(isoSurfaceDiffuse, 0f, 4f);
            isoSurfaceSpecular = Mathf.Clamp(isoSurfaceSpecular, 0f, 4f);
            isoSurfaceSpecularPower = Mathf.Clamp(isoSurfaceSpecularPower, 1f, 128f);
            cinematicAmbient = Mathf.Clamp(cinematicAmbient, 0f, 2f);
            cinematicLightIntensity = Mathf.Clamp(cinematicLightIntensity, 0f, 4f);
            cinematicDiffuse = Mathf.Clamp(cinematicDiffuse, 0f, 4f);
            cinematicSpecular = Mathf.Clamp(cinematicSpecular, 0f, 4f);
            cinematicSpecularPower = Mathf.Clamp(cinematicSpecularPower, 1f, 128f);
            cinematicShadowStrength = Mathf.Clamp01(cinematicShadowStrength);
            cinematicShadowStepMultiplier = Mathf.Clamp(cinematicShadowStepMultiplier, 0.25f, 4f);
            cinematicShadowMaxSteps = Mathf.Clamp(cinematicShadowMaxSteps, 8, 256);
            cinematicScatterBlend = Mathf.Clamp(cinematicScatterBlend, 0f, 2f);
            cinematicExtinction = Mathf.Clamp(cinematicExtinction, 0f, 8f);
            cinematicAoStrength = Mathf.Clamp(cinematicAoStrength, 0f, 2f);
            cinematicAoRadius = Mathf.Clamp(cinematicAoRadius, 0.25f, 8f);
            cinematicAoSamples = Mathf.Clamp(cinematicAoSamples, 1, 8);
            segmentationOverlayOpacityInAbc = Mathf.Clamp01(segmentationOverlayOpacityInAbc);
            segmentationOverlayOpacityInVolume = Mathf.Clamp01(segmentationOverlayOpacityInVolume);
            segmentationColorMapFileName = (segmentationColorMapFileName ?? string.Empty).Trim();

            boxCenter01 = Clamp01Vector(boxCenter01);
            boxSize01 = Clamp01Vector(boxSize01);
            boxSize01 = new Vector3(
                Mathf.Max(0.01f, boxSize01.x),
                Mathf.Max(0.01f, boxSize01.y),
                Mathf.Max(0.01f, boxSize01.z));

            _transfer1DDirty = true;
            _transfer2DDirty = true;
            _sliceTransfer1DDirty = true;
            _sliceTransfer2DDirty = true;
            _segmentationColorMapDirty = true;
            _materialDirty = true;
            EnsureTransferFunctionSchema();
            EnsureSelectedLightDirectionPresetApplied();
            EnsureSelectedColorPresetsApplied();

            if (!Application.isPlaying)
            {
                _meshRenderer = GetComponent<MeshRenderer>();
                EnsureCubeMesh();
                InitializeDefault1DOTF(force: false);
                EnsureDefaultGradient();
                EnsureDefault2DBoxes();
                EnsureDefaultSliceGradient();
                EnsureDefaultSlice2DBoxes();
                EnsureDefaultSegmentationColorMapFileName();
                EnsureMaterial();
                EnsureTransferTexture1D();
                EnsureTransferTexture2D();
                EnsureSliceTransferTexture1D();
                EnsureSliceTransferTexture2D();
                ApplyMaterialProperties();
                ResolveVolumeLoader();
                if (volumeLoader == null)
                {
                    TryAutoLoadInEditor();
                }
            }
        }

        private void OnDisable()
        {
            CleanupOwnedResources();
            _editorAutoLoadAttempted = false;
        }

        public void LoadVolume()
        {
            ResolveVolumeLoader();
            if (volumeLoader != null)
            {
                if (!volumeLoader.HasVolumeSource)
                {
                    return;
                }

                volumeLoader.LoadVolume();
                return;
            }

            LoadVolumeLegacy();
        }

        /// <summary>True when a volume source path is configured on the loader or legacy fields.</summary>
        public bool HasVolumeSourceConfigured()
        {
            ResolveVolumeLoader();
            if (volumeLoader != null)
            {
                return volumeLoader.HasVolumeSource;
            }

            return !string.IsNullOrWhiteSpace(ResolveVolumePathLegacy());
        }

        public void RebuildTransferFunction()
        {
            _transfer1DDirty = true;
            _transfer2DDirty = true;
            _sliceTransfer1DDirty = true;
            _sliceTransfer2DDirty = true;
            _materialDirty = true;
        }

        public void ApplySelectedColorPresets()
        {
            if (TryResolveTransferFunctionPreset(volumeTransferFunctionPresetName, out _))
            {
                TryApplyVolumePresetEffectiveRangeToWindow();
                _transfer1DDirty = true;
                _materialDirty = true;
            }
            else
            {
                ForceApplyVolumeColorPreset(volumeColorPreset);
            }

            if (TryResolveTransferFunctionPreset(sliceTransferFunctionPresetName, out _))
            {
                _sliceTransfer1DDirty = true;
            }
            else
            {
                ForceApplySliceColorPreset(sliceColorPreset);
            }
        }

        public void ApplySelectedLightDirectionPreset()
        {
            if (lightDirectionPreset == LightDirectionPreset.Custom)
            {
                _lastAppliedLightDirectionPreset = LightDirectionPreset.Custom;
                _materialDirty = true;
                return;
            }

            lightDirectionWorld = GetDirectionForLightPreset(lightDirectionPreset);
            if (IsCinematicLightingTechnique(lightingTechnique))
            {
                cinematicLightFollowsCamera = false;
            }
            _lastAppliedLightDirectionPreset = lightDirectionPreset;
            _materialDirty = true;
        }

        public void ApplyRecommendedCinematicDefaults()
        {
            SetRecommendedCinematicDefaults();
            cinematicParameterSchemaVersion = CinematicParameterSchemaCurrentVersion;
            _materialDirty = true;
        }

        public Texture3D GetVolumeTexture()
        {
            return _volumeTexture;
        }

        /// <summary>True while 3D volume raymarching is intentionally deferred after a load.</summary>
        public bool IsDeferringVolumeMeshRendering => _deferVolumeMeshRendering;

        /// <summary>True when the volume mesh may render in the 3D view.</summary>
        public bool IsVolumeMeshRenderingEnabled =>
            _volumeTexture != null && !_deferVolumeMeshRendering;

        /// <summary>Enables 3D volume raymarching after ABC slice RawImages have rendered.</summary>
        public void EnableVolumeMeshRendering()
        {
            if (!_deferVolumeMeshRendering)
            {
                return;
            }

            _deferVolumeMeshRendering = false;
            SetVolumeMeshRendererEnabled(true);
            MarkSliceMaterialDirty();
        }

        /// <summary>Play mode spread Texture3D upload across frames.</summary>
        public bool UsesSpreadVolumeTextureUpload => Application.isPlaying && spreadVolumeTextureUpload;

        /// <summary>Increments when slice-facing material inputs change.</summary>
        public int SliceMaterialVersion => _sliceMaterialVersion;

        public Texture3D GetSegmentationTexture()
        {
            return _segmentationTexture;
        }

        public bool HasSegmentationTexture()
        {
            return _segmentationTexture != null;
        }

        public Texture2D GetSegmentationColorMapTexture()
        {
            EnsureSegmentationColorMapTexture();
            return _segmentationColorMapTexture;
        }

        public bool GetShowSegmentationOverlayInAbc()
        {
            return showSegmentationOverlayInAbc;
        }

        public float GetSegmentationOverlayOpacityInAbc()
        {
            return segmentationOverlayOpacityInAbc;
        }

        public string GetLegacySegmentationColorMapFileName()
        {
            EnsureDefaultSegmentationColorMapFileName();
            return segmentationColorMapFileName;
        }

        public void SetVolumeLoader(SimpleITKVolumeLoader loader)
        {
            if (loader == null || volumeLoader == loader)
            {
                return;
            }

            volumeLoader = loader;
        }

        public bool TryGetLegacyVolumeSourceSettings(
            out bool useStreamingAssetsSample,
            out string sampleName,
            out string filePath,
            out bool autoLoadOnStart,
            out bool autoLoadInEditMode,
            out SimpleITKProcessingController legacyProcessingController)
        {
            useStreamingAssetsSample = loadFromStreamingAssetsSample;
            sampleName = sampleFileName;
            filePath = volumeFilePath;
            autoLoadOnStart = loadOnStart;
            autoLoadInEditMode = loadInEditMode;
            legacyProcessingController = processingController;
            return true;
        }

        public bool TryResolveVolumePath(out string resolvedPath, out string error)
        {
            ResolveVolumeLoader();
            if (volumeLoader != null)
            {
                return volumeLoader.TryResolveVolumePath(out resolvedPath, out error);
            }

            return TryResolveVolumePathLegacy(out resolvedPath, out error);
        }

        private bool TryResolveVolumePathLegacy(out string resolvedPath, out string error)
        {
            resolvedPath = ResolveVolumePathLegacy();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                error = loadFromStreamingAssetsSample
                    ? "sampleFileName is empty."
                    : "volumeFilePath is empty.";
                return false;
            }

            if (IsRemotePath(resolvedPath))
            {
                error = "Legacy volume loading does not support URL-based StreamingAssets paths. " +
                        "Add SimpleITKVolumeLoader to stage assets at runtime.";
                return false;
            }

            if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                error = $"volume path does not exist: {resolvedPath}";
                return false;
            }

            return true;
        }

        private void LoadVolumeLegacy()
        {
            if (!TryResolveVolumePathLegacy(out var resolvedPath, out var resolveError))
            {
                Debug.LogError($"SimpleITKVolumeRenderer: failed to resolve volume path: {resolveError}");
                return;
            }

            var processedReader = ResolveProcessingController();
            var useProcessing = processedReader != null && processedReader.ShouldProcessOnLoad();
            SimpleITKVolumeData volumeData;
            string error;
            var loaded = useProcessing
                ? processedReader.TryReadVolumeData(resolvedPath, out volumeData, out error)
                : SimpleITKNative.TryReadVolumeFloat32Normalized(resolvedPath, out volumeData, out error);
            if (!loaded)
            {
                Debug.LogError($"SimpleITKVolumeRenderer: failed to load volume: {error}");
                return;
            }

            ApplyVolumeData(volumeData, resolvedPath);
        }

        public bool ApplyVolumeData(SimpleITKVolumeData volumeData, string sourcePathOrLabel = null)
        {
            if (!BeginApplyVolumeDataCommon(volumeData, out var imageInfo))
            {
                return false;
            }

            if (!TryCreateVolumeTextureImmediate(ref volumeData))
            {
                return false;
            }

            return FinalizeAfterVolumeTextureUpload(imageInfo, sourcePathOrLabel);
        }

        /// <summary>
        /// Play-mode load path that spreads Texture3D staging/upload across frames.
        /// </summary>
        public Task<bool> ApplyVolumeDataAsync(
            SimpleITKVolumeData volumeData,
            string sourcePathOrLabel,
            CancellationToken token)
        {
            if (!BeginApplyVolumeDataCommon(volumeData, out var imageInfo))
            {
                return Task.FromResult(false);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            StartCoroutine(ApplyVolumeDataSpreadTextureCoroutine(volumeData, imageInfo, sourcePathOrLabel, completion, token));
            return completion.Task;
        }

        private bool BeginApplyVolumeDataCommon(SimpleITKVolumeData volumeData, out SimpleITKImageInfo imageInfo)
        {
            imageInfo = volumeData.ImageInfo;

            if (!SystemInfo.supports3DTextures)
            {
                Debug.LogError("SimpleITKVolumeRenderer: this platform does not support 3D textures.");
                return false;
            }

            ClearSegmentationData();

            _volumeWidth = volumeData.Width;
            _volumeHeight = volumeData.Height;
            _volumeDepth = volumeData.Depth;
            loadedSourceMin = volumeData.SourceMinimum;
            loadedSourceMax = volumeData.SourceMaximum;

            if (autoFitIntensityWindowOnLoad)
            {
                AutoFitIntensityWindowFromVolume(volumeData.VoxelsNormalized);
            }

            return true;
        }

        private bool FinalizeAfterVolumeTextureUpload(SimpleITKImageInfo imageInfo, string sourcePathOrLabel)
        {
            _runtimeRecoverLoadRequested = false;
#if UNITY_EDITOR
            _nextEditorRecoverLoadTime = 0.0;
#endif

            ApplyVolumeTransformFromImageInfo(imageInfo);

            enableBoxCrossSection = false;
            boxCenter01 = new Vector3(0.5f, 0.5f, 0.5f);
            boxSize01 = new Vector3(1f, 1f, 1f);
            invertBox = true;
            TryApplyVolumePresetEffectiveRangeToWindow();
            SyncSliceIntensityWindowToVolumeIfNeeded();

            _transfer1DDirty = true;
            _transfer2DDirty = true;
            _sliceTransfer1DDirty = true;
            _sliceTransfer2DDirty = true;
            _materialDirty = true;
            _editorAutoLoadAttempted = true;
            if (!string.IsNullOrWhiteSpace(sourcePathOrLabel))
            {
                _lastEditorAutoLoadPath = sourcePathOrLabel;
            }

            if (_meshRenderer == null)
            {
                _meshRenderer = GetComponent<MeshRenderer>();
            }

            ApplyMaterialProperties();
            if (_runtimeMaterial == null)
            {
                EnsureMaterial();
                ApplyMaterialProperties();
            }

            _deferVolumeMeshRendering = Application.isPlaying && deferVolumeMeshRenderingOnLoad;
            PrepareSliceResourcesForImmediateRender();

            if (_meshRenderer != null && _runtimeMaterial != null)
            {
                _meshRenderer.sharedMaterial = _runtimeMaterial;
            }

            SetVolumeMeshRendererEnabled(!_deferVolumeMeshRendering);
            MarkSliceMaterialDirty();
            return true;
        }

        private IEnumerator ApplyVolumeDataSpreadTextureCoroutine(
            SimpleITKVolumeData volumeData,
            SimpleITKImageInfo imageInfo,
            string sourcePathOrLabel,
            TaskCompletionSource<bool> completion,
            CancellationToken token)
        {
            var uploaded = false;
            yield return UploadVolumeTextureSpreadCoroutine(volumeData, result => uploaded = result, token);

            if (token.IsCancellationRequested || this == null || !isActiveAndEnabled)
            {
                completion.TrySetCanceled(token);
                yield break;
            }

            if (!uploaded)
            {
                completion.TrySetResult(false);
                yield break;
            }

            completion.TrySetResult(FinalizeAfterVolumeTextureUpload(imageInfo, sourcePathOrLabel));
        }

        private void SetVolumeMeshRendererEnabled(bool enabled)
        {
            if (_meshRenderer == null)
            {
                _meshRenderer = GetComponent<MeshRenderer>();
            }

            if (_meshRenderer != null)
            {
                _meshRenderer.enabled = enabled;
            }
        }

        private void PrepareSliceResourcesForImmediateRender()
        {
            if (_sliceTransfer1DDirty)
            {
                RebuildSliceTransferTexture1D();
            }

            if (_sliceTransfer2DDirty)
            {
                RebuildSliceTransferTexture2D();
            }
        }

        public bool TryLoadSegmentationVolume(string segmentationPath, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(segmentationPath))
            {
                error = "segmentationPath must not be empty.";
                return false;
            }

            if (!SimpleITKNative.TryReadLabelVolumeUInt8(segmentationPath, out var volumeData, out error))
            {
                return false;
            }

            return ApplySegmentationData(volumeData, segmentationPath, out error);
        }

        public bool TryApplySegmentationVolumeData(SimpleITKLabelVolumeData volumeData, string sourcePathOrLabel, out string error)
        {
            return ApplySegmentationData(volumeData, sourcePathOrLabel, out error);
        }

        public void ClearSegmentationData()
        {
            DestroyTexture(ref _segmentationTexture);
            _segmentationSourcePath = string.Empty;
            _materialDirty = true;
        }

        public Texture2D GetSliceTransferTexture1D()
        {
            return _sliceTransferTexture1D != null ? _sliceTransferTexture1D : _transferTexture1D;
        }

        public Texture2D GetSliceTransferTexture2D()
        {
            return _sliceTransferTexture2D != null ? _sliceTransferTexture2D : _transferTexture2D;
        }

        public Matrix4x4 GetWorldToVolumeMatrix()
        {
            return transform.worldToLocalMatrix;
        }

        public Vector3 GetInvVolumeSize()
        {
            return new Vector3(
                1.0f / Mathf.Max(_volumeWidth, 1),
                1.0f / Mathf.Max(_volumeHeight, 1),
                1.0f / Mathf.Max(_volumeDepth, 1));
        }

        public float GetClipMin()
        {
            return GetSliceClipMin();
        }

        public float GetClipMax()
        {
            return GetSliceClipMax();
        }

        private float GetSliceClipMin()
        {
            return syncAbcIntensityWindowToVolume ? intensityWindowMin : sliceIntensityWindowMin;
        }

        private float GetSliceClipMax()
        {
            return syncAbcIntensityWindowToVolume ? intensityWindowMax : sliceIntensityWindowMax;
        }

        private void SyncSliceIntensityWindowToVolumeIfNeeded()
        {
            if (!syncAbcIntensityWindowToVolume)
            {
                return;
            }

            if (Mathf.Approximately(sliceIntensityWindowMin, intensityWindowMin) &&
                Mathf.Approximately(sliceIntensityWindowMax, intensityWindowMax))
            {
                return;
            }

            sliceIntensityWindowMin = intensityWindowMin;
            sliceIntensityWindowMax = intensityWindowMax;
            _sliceTransfer1DDirty = true;
            _materialDirty = true;
        }

        public string GetVolumeTransferFunctionPresetName()
        {
            return volumeTransferFunctionPresetName ?? string.Empty;
        }

        public string GetSliceTransferFunctionPresetName()
        {
            return sliceTransferFunctionPresetName ?? string.Empty;
        }

        public void SetVolumeTransferFunctionPresetName(string presetName)
        {
            var normalized = presetName ?? string.Empty;
            if (string.Equals(volumeTransferFunctionPresetName, normalized, StringComparison.Ordinal))
            {
                return;
            }

            volumeTransferFunctionPresetName = normalized;
            ApplySelectedColorPresets();
            SyncLastAppliedVolumeTransferFunctionPreset();
        }

        public void SetSliceTransferFunctionPresetName(string presetName)
        {
            var normalized = presetName ?? string.Empty;
            if (string.Equals(sliceTransferFunctionPresetName, normalized, StringComparison.Ordinal))
            {
                return;
            }

            sliceTransferFunctionPresetName = normalized;
            ApplySelectedColorPresets();
            SyncLastAppliedSliceTransferFunctionPreset();
        }

        private void SyncLastAppliedVolumeTransferFunctionPreset()
        {
            if (TryResolveTransferFunctionPreset(volumeTransferFunctionPresetName, out _))
            {
                _lastAppliedVolumeTransferFunctionPresetName = volumeTransferFunctionPresetName ?? string.Empty;
                _lastAppliedVolumeColorPreset = ColorPreset.Custom;
                return;
            }

            _lastAppliedVolumeTransferFunctionPresetName = string.Empty;
        }

        private void SyncLastAppliedSliceTransferFunctionPreset()
        {
            if (TryResolveTransferFunctionPreset(sliceTransferFunctionPresetName, out _))
            {
                _lastAppliedSliceTransferFunctionPresetName = sliceTransferFunctionPresetName ?? string.Empty;
                _lastAppliedSliceColorPreset = ColorPreset.Custom;
                return;
            }

            _lastAppliedSliceTransferFunctionPresetName = string.Empty;
        }

        public string[] GetAvailableTransferFunctionPresetNames()
        {
            if (transferFunctionPresetLibrary != null)
            {
                var names = transferFunctionPresetLibrary.GetPresetNames();
                if (names != null && names.Length > 0)
                {
                    return names;
                }
            }

            return SimpleITKTransferFunctionPresetLibrary.GetDefaultPresetNames();
        }

        public float GetSliceGradientScale()
        {
            return sliceGradientScale;
        }

        public TransferFunctionMode GetSliceTransferFunctionMode()
        {
            return sliceTransferFunctionMode;
        }

        public Vector3 GetLightDirectionWorld()
        {
            return NormalizeLightDirection(lightDirectionWorld);
        }

        public bool IsBoxCrossSectionEnabled()
        {
            return enableBoxCrossSection;
        }

        public bool IsBoxCrossSectionInverted()
        {
            return invertBox;
        }

        public Vector3 GetBoxMin01()
        {
            GetBoxBounds01(out var boxMin, out _);
            return boxMin;
        }

        public Vector3 GetBoxMax01()
        {
            GetBoxBounds01(out _, out var boxMax);
            return boxMax;
        }

        public void SetBoxCrossSectionEnabled(bool enabled)
        {
            if (enableBoxCrossSection == enabled)
            {
                return;
            }

            enableBoxCrossSection = enabled;
            _materialDirty = true;
            MarkSliceMaterialDirty();
        }

        public void SetBoxCrossSectionRegion(Vector3 min01, Vector3 max01, bool keepInside)
        {
            var clampedMin = Clamp01Vector(min01);
            var clampedMax = Clamp01Vector(max01);

            var min = new Vector3(
                Mathf.Min(clampedMin.x, clampedMax.x),
                Mathf.Min(clampedMin.y, clampedMax.y),
                Mathf.Min(clampedMin.z, clampedMax.z));
            var max = new Vector3(
                Mathf.Max(clampedMin.x, clampedMax.x),
                Mathf.Max(clampedMin.y, clampedMax.y),
                Mathf.Max(clampedMin.z, clampedMax.z));

            const float epsilon = 0.001f;
            max = new Vector3(
                Mathf.Max(min.x + epsilon, max.x),
                Mathf.Max(min.y + epsilon, max.y),
                Mathf.Max(min.z + epsilon, max.z));
            max = Clamp01Vector(max);

            var center = 0.5f * (min + max);
            var size = new Vector3(
                Mathf.Clamp(max.x - min.x, 0.01f, 1.0f),
                Mathf.Clamp(max.y - min.y, 0.01f, 1.0f),
                Mathf.Clamp(max.z - min.z, 0.01f, 1.0f));

            var changed =
                !Approximately(boxCenter01, center) ||
                !Approximately(boxSize01, size) ||
                invertBox != keepInside ||
                !enableBoxCrossSection;

            boxCenter01 = center;
            boxSize01 = size;
            invertBox = keepInside;
            enableBoxCrossSection = true;

            if (changed)
            {
                _materialDirty = true;
                MarkSliceMaterialDirty();
            }
        }

        private bool TryResolveVolumeTextureUpload(
            out TextureFormat textureFormat,
            out UploadMode uploadMode)
        {
            uploadMode = UploadMode.Float32;

            if (preferR16Format && SystemInfo.SupportsTextureFormat(TextureFormat.R16))
            {
                textureFormat = TextureFormat.R16;
                uploadMode = UploadMode.R16;
                return true;
            }

            if (SystemInfo.SupportsTextureFormat(TextureFormat.RFloat))
            {
                textureFormat = TextureFormat.RFloat;
                uploadMode = UploadMode.Float32;
                return true;
            }

            if (SystemInfo.SupportsTextureFormat(TextureFormat.R8))
            {
                textureFormat = TextureFormat.R8;
                uploadMode = UploadMode.R8;
                return true;
            }

            textureFormat = TextureFormat.RFloat;
            Debug.LogError("SimpleITKVolumeRenderer: neither R16, RFloat nor R8 3D texture format is supported.");
            return false;
        }

        private Texture3D CreateEmptyVolumeTexture(SimpleITKVolumeData volumeData, TextureFormat textureFormat)
        {
            var texture = new Texture3D(
                volumeData.Width,
                volumeData.Height,
                volumeData.Depth,
                textureFormat,
                mipChain: false);

            texture.name = "SimpleITKVolumeTexture";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            return texture;
        }

        private bool TryCreateVolumeTextureImmediate(ref SimpleITKVolumeData volumeData)
        {
            DestroyTexture(ref _volumeTexture);

            if (!TryResolveVolumeTextureUpload(out var textureFormat, out var uploadMode))
            {
                return false;
            }

            _volumeTexture = CreateEmptyVolumeTexture(volumeData, textureFormat);
            var voxels = volumeData.VoxelsNormalized;
            if (voxels == null || voxels.Length == 0)
            {
                Debug.LogError("SimpleITKVolumeRenderer: volume voxel buffer is empty.");
                return false;
            }

            switch (uploadMode)
            {
                case UploadMode.Float32:
                    _volumeTexture.SetPixelData(voxels, 0);
                    break;

                case UploadMode.R16:
                    _volumeTexture.SetPixelData(ConvertVoxelsToR16(voxels), 0);
                    break;

                case UploadMode.R8:
                    _volumeTexture.SetPixelData(ConvertVoxelsToR8(voxels), 0);
                    break;
            }

            _volumeTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            volumeData.ConsumeVoxels();
            return true;
        }

        private IEnumerator UploadVolumeTextureSpreadCoroutine(
            SimpleITKVolumeData volumeData,
            Action<bool> onComplete,
            CancellationToken token)
        {
            DestroyTexture(ref _volumeTexture);

            if (!TryResolveVolumeTextureUpload(out var textureFormat, out var uploadMode))
            {
                onComplete?.Invoke(false);
                yield break;
            }

            var voxels = volumeData.VoxelsNormalized;
            if (voxels == null || voxels.Length == 0)
            {
                Debug.LogError("SimpleITKVolumeRenderer: volume voxel buffer is empty.");
                onComplete?.Invoke(false);
                yield break;
            }

            _volumeTexture = CreateEmptyVolumeTexture(volumeData, textureFormat);
            var voxelCount = voxels.Length;
            var chunkSize = Mathf.Max(65536, volumeTextureUploadVoxelsPerFrame);
            var uploaded = false;

            switch (uploadMode)
            {
                case UploadMode.Float32:
                {
                    var pixelData = _volumeTexture.GetPixelData<float>(0);
                    try
                    {
                        for (var offset = 0; offset < voxelCount; offset += chunkSize)
                        {
                            if (token.IsCancellationRequested || this == null)
                            {
                                pixelData.Dispose();
                                DestroyTexture(ref _volumeTexture);
                                onComplete?.Invoke(false);
                                yield break;
                            }

                            var count = Mathf.Min(chunkSize, voxelCount - offset);
                            CopyManagedArraySlice(voxels, offset, pixelData, offset, count);
                            if (offset + count < voxelCount)
                            {
                                yield return null;
                            }
                        }

                        _volumeTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                        uploaded = true;
                    }
                    finally
                    {
                        if (pixelData.IsCreated)
                        {
                            pixelData.Dispose();
                        }
                    }

                    break;
                }

                case UploadMode.R16:
                {
                    var convertTask = Task.Run(() => ConvertVoxelsToR16(voxels));
                    while (!convertTask.IsCompleted)
                    {
                        if (token.IsCancellationRequested || this == null)
                        {
                            onComplete?.Invoke(false);
                            yield break;
                        }

                        yield return null;
                    }

                    if (convertTask.IsFaulted || convertTask.IsCanceled)
                    {
                        onComplete?.Invoke(false);
                        yield break;
                    }

                    var shorts = convertTask.Result;
                    var pixelData = _volumeTexture.GetPixelData<ushort>(0);
                    try
                    {
                        for (var offset = 0; offset < voxelCount; offset += chunkSize)
                        {
                            if (token.IsCancellationRequested || this == null)
                            {
                                pixelData.Dispose();
                                DestroyTexture(ref _volumeTexture);
                                onComplete?.Invoke(false);
                                yield break;
                            }

                            var count = Mathf.Min(chunkSize, voxelCount - offset);
                            CopyManagedArraySlice(shorts, offset, pixelData, offset, count);
                            if (offset + count < voxelCount)
                            {
                                yield return null;
                            }
                        }

                        _volumeTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                        uploaded = true;
                    }
                    finally
                    {
                        if (pixelData.IsCreated)
                        {
                            pixelData.Dispose();
                        }
                    }

                    break;
                }

                case UploadMode.R8:
                {
                    var convertTask = Task.Run(() => ConvertVoxelsToR8(voxels));
                    while (!convertTask.IsCompleted)
                    {
                        if (token.IsCancellationRequested || this == null)
                        {
                            onComplete?.Invoke(false);
                            yield break;
                        }

                        yield return null;
                    }

                    if (convertTask.IsFaulted || convertTask.IsCanceled)
                    {
                        onComplete?.Invoke(false);
                        yield break;
                    }

                    var bytes = convertTask.Result;
                    var pixelData = _volumeTexture.GetPixelData<byte>(0);
                    try
                    {
                        for (var offset = 0; offset < voxelCount; offset += chunkSize)
                        {
                            if (token.IsCancellationRequested || this == null)
                            {
                                pixelData.Dispose();
                                DestroyTexture(ref _volumeTexture);
                                onComplete?.Invoke(false);
                                yield break;
                            }

                            var count = Mathf.Min(chunkSize, voxelCount - offset);
                            CopyManagedArraySlice(bytes, offset, pixelData, offset, count);
                            if (offset + count < voxelCount)
                            {
                                yield return null;
                            }
                        }

                        _volumeTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                        uploaded = true;
                    }
                    finally
                    {
                        if (pixelData.IsCreated)
                        {
                            pixelData.Dispose();
                        }
                    }

                    break;
                }
            }

            volumeData.ConsumeVoxels();
            onComplete?.Invoke(uploaded);
        }

        private static void CopyManagedArraySlice<T>(
            T[] source,
            int sourceIndex,
            NativeArray<T> destination,
            int destinationIndex,
            int length)
            where T : struct
        {
            for (var i = 0; i < length; ++i)
            {
                destination[destinationIndex + i] = source[sourceIndex + i];
            }
        }

        private static ushort[] ConvertVoxelsToR16(float[] voxelsNormalized)
        {
            var shorts = new ushort[voxelsNormalized.Length];
            for (var i = 0; i < shorts.Length; ++i)
            {
                shorts[i] = (ushort)Mathf.Clamp(Mathf.RoundToInt(voxelsNormalized[i] * 65535f), 0, 65535);
            }

            return shorts;
        }

        private static byte[] ConvertVoxelsToR8(float[] voxelsNormalized)
        {
            var bytes = new byte[voxelsNormalized.Length];
            for (var i = 0; i < bytes.Length; ++i)
            {
                bytes[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(voxelsNormalized[i] * 255f), 0, 255);
            }

            return bytes;
        }

        private void MarkSliceMaterialDirty()
        {
            unchecked
            {
                _sliceMaterialVersion++;
            }
        }

        private bool ApplySegmentationData(
            SimpleITKLabelVolumeData volumeData,
            string sourcePathOrLabel,
            out string error)
        {
            error = string.Empty;

            if (!SystemInfo.supports3DTextures)
            {
                error = "This platform does not support 3D textures.";
                return false;
            }

            if (_volumeTexture == null)
            {
                error = "Main volume must be loaded before a segmentation overlay can be applied.";
                return false;
            }

            if (volumeData.Width != _volumeWidth || volumeData.Height != _volumeHeight || volumeData.Depth != _volumeDepth)
            {
                error =
                    $"Segmentation dimensions ({volumeData.Width}, {volumeData.Height}, {volumeData.Depth}) do not match " +
                    $"the current volume ({_volumeWidth}, {_volumeHeight}, {_volumeDepth}).";
                return false;
            }

            DestroyTexture(ref _segmentationTexture);
            _segmentationTexture = new Texture3D(
                volumeData.Width,
                volumeData.Height,
                volumeData.Depth,
                TextureFormat.R8,
                mipChain: false);

            _segmentationTexture.name = "SimpleITKSegmentationTexture";
            _segmentationTexture.wrapMode = TextureWrapMode.Clamp;
            _segmentationTexture.filterMode = FilterMode.Point;
            _segmentationTexture.SetPixelData(volumeData.Voxels, 0);
            _segmentationTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            volumeData.ConsumeVoxels();

            _segmentationSourcePath = sourcePathOrLabel ?? string.Empty;
            _materialDirty = true;
            MarkSliceMaterialDirty();
            return true;
        }

        private enum UploadMode { Float32, R16, R8 }

        private void EnsureMaterial()
        {
            if (_meshRenderer == null)
            {
                _meshRenderer = GetComponent<MeshRenderer>();
                if (_meshRenderer == null)
                {
                    return;
                }
            }

            var shader = ResolveActiveRaymarchShader();
            if (shader == null)
            {
                var shaderName = IsCinematicLightingTechnique(lightingTechnique) ? CinematicShaderName : BasicShaderName;
                Debug.LogError($"SimpleITKVolumeRenderer: shader '{shaderName}' not found.");
                return;
            }

            if (!shader.isSupported)
            {
                Debug.LogWarning($"SimpleITKVolumeRenderer: shader '{shader.name}' reports unsupported. Attempting to create material anyway.");
            }

            if (_runtimeMaterial == null || _runtimeMaterial.shader != shader)
            {
                DestroyMaterial();
                _runtimeMaterial = new Material(shader);

                _runtimeMaterial.name = "SimpleITKVolumeMaterial(Runtime)";
                _runtimeMaterial.hideFlags = HideFlags.DontSave;
            }

            _meshRenderer.sharedMaterial = _runtimeMaterial;
        }

        private void EnsureTransferTexture1D()
        {
            if (_transferTexture1D != null && _transferTexture1D.width == transferFunction1DResolution)
            {
                return;
            }

            DestroyTexture(ref _transferTexture1D);
            _transferTexture1D = new Texture2D(
                transferFunction1DResolution,
                1,
                TextureFormat.RGBA32,
                mipChain: false,
                linear: true);

            _transferTexture1D.name = "SimpleITKTransferFunction1D";
            _transferTexture1D.wrapMode = TextureWrapMode.Clamp;
            _transferTexture1D.filterMode = FilterMode.Bilinear;
            _transfer1DDirty = true;
        }

        private void EnsureTransferTexture2D()
        {
            if (_transferTexture2D != null && _transferTexture2D.width == transferFunction2DResolution && _transferTexture2D.height == transferFunction2DResolution)
            {
                return;
            }

            DestroyTexture(ref _transferTexture2D);
            _transferTexture2D = new Texture2D(
                transferFunction2DResolution,
                transferFunction2DResolution,
                TextureFormat.RGBA32,
                mipChain: false,
                linear: true);

            _transferTexture2D.name = "SimpleITKTransferFunction2D";
            _transferTexture2D.wrapMode = TextureWrapMode.Clamp;
            _transferTexture2D.filterMode = FilterMode.Bilinear;
            _transfer2DDirty = true;
        }

        private void EnsureSliceTransferTexture1D()
        {
            if (_sliceTransferTexture1D != null && _sliceTransferTexture1D.width == transferFunction1DResolution)
            {
                return;
            }

            DestroyTexture(ref _sliceTransferTexture1D);
            _sliceTransferTexture1D = new Texture2D(
                transferFunction1DResolution,
                1,
                TextureFormat.RGBA32,
                mipChain: false,
                linear: true);

            _sliceTransferTexture1D.name = "SimpleITKSliceTransferFunction1D";
            _sliceTransferTexture1D.wrapMode = TextureWrapMode.Clamp;
            _sliceTransferTexture1D.filterMode = FilterMode.Bilinear;
            _sliceTransfer1DDirty = true;
        }

        private void EnsureSliceTransferTexture2D()
        {
            if (_sliceTransferTexture2D != null && _sliceTransferTexture2D.width == transferFunction2DResolution && _sliceTransferTexture2D.height == transferFunction2DResolution)
            {
                return;
            }

            DestroyTexture(ref _sliceTransferTexture2D);
            _sliceTransferTexture2D = new Texture2D(
                transferFunction2DResolution,
                transferFunction2DResolution,
                TextureFormat.RGBA32,
                mipChain: false,
                linear: true);

            _sliceTransferTexture2D.name = "SimpleITKSliceTransferFunction2D";
            _sliceTransferTexture2D.wrapMode = TextureWrapMode.Clamp;
            _sliceTransferTexture2D.filterMode = FilterMode.Bilinear;
            _sliceTransfer2DDirty = true;
        }

        private bool TryResolveTransferFunctionPreset(
            string presetName,
            out SimpleITKTransferFunctionPresetLibrary.TransferFunctionPresetDefinition preset)
        {
            preset = null;

            if (string.IsNullOrWhiteSpace(presetName))
            {
                return false;
            }

            if (transferFunctionPresetLibrary != null &&
                transferFunctionPresetLibrary.TryGetPreset(presetName, out preset))
            {
                return true;
            }

            return SimpleITKTransferFunctionPresetLibrary.TryGetDefaultPreset(presetName, out preset);
        }

        private void FillTextureFromTransferFunctionPreset(
            Color[] colors,
            SimpleITKTransferFunctionPresetLibrary.TransferFunctionPresetDefinition preset)
        {
            if (colors == null || colors.Length == 0 || preset == null)
            {
                return;
            }

            var sampleRange = GetCurrentTransferFunctionSourceRange(
                preset.AbsoluteRange,
                intensityWindowMin,
                intensityWindowMax);
            for (var i = 0; i < colors.Length; ++i)
            {
                var t = colors.Length <= 1 ? 0f : i / (float)(colors.Length - 1);
                var scalar = preset.AbsoluteRange ? Mathf.Lerp(sampleRange.x, sampleRange.y, t) : t;
                var color = EvaluateTransferFunctionPresetColor(preset.ColorPoints, scalar);
                var alpha = EvaluateTransferFunctionPresetOpacity(preset.OpacityPoints, scalar);
                colors[i] = new Color(color.r, color.g, color.b, alpha);
            }
        }

        private void FillSliceTextureFromTransferFunctionPreset(
            Color[] colors,
            SimpleITKTransferFunctionPresetLibrary.TransferFunctionPresetDefinition preset)
        {
            if (colors == null || colors.Length == 0 || preset == null)
            {
                return;
            }

            var sampleRange = GetCurrentTransferFunctionSourceRange(
                preset.AbsoluteRange,
                GetSliceClipMin(),
                GetSliceClipMax());
            for (var i = 0; i < colors.Length; ++i)
            {
                var t = colors.Length <= 1 ? 0f : i / (float)(colors.Length - 1);
                var scalar = preset.AbsoluteRange ? Mathf.Lerp(sampleRange.x, sampleRange.y, t) : t;
                var color = EvaluateTransferFunctionPresetColor(preset.ColorPoints, scalar);
                var alpha = EvaluateTransferFunctionPresetOpacity(preset.OpacityPoints, scalar);
                colors[i] = new Color(color.r, color.g, color.b, alpha);
            }
        }

        private Vector2 GetCurrentTransferFunctionSourceRange(
            bool absoluteRange,
            float windowMinNormalized,
            float windowMaxNormalized)
        {
            if (!absoluteRange)
            {
                return new Vector2(0.0f, 1.0f);
            }

            if (_volumeWidth <= 0 || _volumeHeight <= 0 || _volumeDepth <= 0)
            {
                return new Vector2(0.0f, 1.0f);
            }

            var sourceMin = loadedSourceMin;
            var sourceMax = loadedSourceMax;
            if (Mathf.Abs(sourceMax - sourceMin) < 1e-5f)
            {
                return new Vector2(0.0f, 1.0f);
            }

            var windowMin = Mathf.Clamp01(windowMinNormalized);
            var windowMax = Mathf.Clamp(Mathf.Max(windowMinNormalized + 1e-5f, windowMaxNormalized), 0.0f, 1.0f);
            var scalarMin = Mathf.Lerp(sourceMin, sourceMax, windowMin);
            var scalarMax = Mathf.Lerp(sourceMin, sourceMax, windowMax);
            return scalarMax >= scalarMin
                ? new Vector2(scalarMin, scalarMax)
                : new Vector2(scalarMax, scalarMin);
        }

        private static Color EvaluateTransferFunctionPresetColor(
            SimpleITKTransferFunctionPresetLibrary.ScalarColorPoint[] points,
            float scalar)
        {
            if (points == null || points.Length == 0)
            {
                return Color.white;
            }

            if (points.Length == 1 || scalar <= points[0].Scalar)
            {
                return points[0].Color;
            }

            for (var i = 1; i < points.Length; ++i)
            {
                if (scalar > points[i].Scalar)
                {
                    continue;
                }

                var previous = points[i - 1];
                var current = points[i];
                var width = current.Scalar - previous.Scalar;
                if (Mathf.Abs(width) < 1e-5f)
                {
                    return current.Color;
                }

                var t = Mathf.Clamp01((scalar - previous.Scalar) / width);
                return Color.LerpUnclamped(previous.Color, current.Color, t);
            }

            return points[points.Length - 1].Color;
        }

        private static float EvaluateTransferFunctionPresetOpacity(
            SimpleITKTransferFunctionPresetLibrary.ScalarOpacityPoint[] points,
            float scalar)
        {
            if (points == null || points.Length == 0)
            {
                return 1.0f;
            }

            if (points.Length == 1 || scalar <= points[0].Scalar)
            {
                return Mathf.Clamp01(points[0].Opacity);
            }

            for (var i = 1; i < points.Length; ++i)
            {
                if (scalar > points[i].Scalar)
                {
                    continue;
                }

                var previous = points[i - 1];
                var current = points[i];
                var width = current.Scalar - previous.Scalar;
                if (Mathf.Abs(width) < 1e-5f)
                {
                    return Mathf.Clamp01(current.Opacity);
                }

                var t = Mathf.Clamp01((scalar - previous.Scalar) / width);
                return Mathf.Clamp01(Mathf.LerpUnclamped(previous.Opacity, current.Opacity, t));
            }

            return Mathf.Clamp01(points[points.Length - 1].Opacity);
        }

        private void TryApplyVolumePresetEffectiveRangeToWindow()
        {
            if (!applyVolumePresetEffectiveRangeToWindow ||
                !TryResolveTransferFunctionPreset(volumeTransferFunctionPresetName, out var preset) ||
                preset == null ||
                !preset.AbsoluteRange ||
                !preset.HasEffectiveRange)
            {
                return;
            }

            if (_volumeWidth <= 0 || _volumeHeight <= 0 || _volumeDepth <= 0)
            {
                return;
            }

            var sourceMin = loadedSourceMin;
            var sourceMax = loadedSourceMax;
            if (Mathf.Abs(sourceMax - sourceMin) < 1e-5f)
            {
                return;
            }

            var normalizedMin = Mathf.InverseLerp(sourceMin, sourceMax, preset.EffectiveRange.x);
            var normalizedMax = Mathf.InverseLerp(sourceMin, sourceMax, preset.EffectiveRange.y);
            var nextMin = Mathf.Clamp01(Mathf.Min(normalizedMin, normalizedMax));
            var nextMax = Mathf.Clamp01(Mathf.Max(normalizedMin, normalizedMax));
            if (nextMax - nextMin < 0.01f)
            {
                var center = Mathf.Clamp01(0.5f * (nextMin + nextMax));
                nextMin = Mathf.Clamp01(center - 0.05f);
                nextMax = Mathf.Clamp01(center + 0.05f);
            }

            if (nextMax - nextMin < 0.01f)
            {
                nextMin = 0.0f;
                nextMax = 1.0f;
            }

            if (Mathf.Approximately(intensityWindowMin, nextMin) &&
                Mathf.Approximately(intensityWindowMax, nextMax))
            {
                return;
            }

            intensityWindowMin = nextMin;
            intensityWindowMax = nextMax;
            SyncSliceIntensityWindowToVolumeIfNeeded();
            _transfer1DDirty = true;
            _sliceTransfer1DDirty = true;
            _materialDirty = true;
        }

        private void RebuildTransferTexture1D()
        {
            EnsureTransferTexture1D();
            if (_transferTexture1D == null)
            {
                return;
            }

            var colors = new Color[transferFunction1DResolution];
            if (TryResolveTransferFunctionPreset(volumeTransferFunctionPresetName, out var preset))
            {
                FillTextureFromTransferFunctionPreset(colors, preset);
            }
            else
            {
                EnsureDefaultGradient();
                for (var i = 0; i < transferFunction1DResolution; ++i)
                {
                    var t = transferFunction1DResolution <= 1 ? 0f : i / (float)(transferFunction1DResolution - 1);
                    var color = colorGradient.Evaluate(t);
                    var alpha = Mathf.Clamp01(alphaCurve.Evaluate(t));
                    colors[i] = new Color(color.r, color.g, color.b, alpha);
                }
            }

            _transferTexture1D.SetPixels(colors);
            _transferTexture1D.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            _transfer1DDirty = false;
            _materialDirty = true;
        }

        private void RebuildTransferTexture2D()
        {
            EnsureTransferTexture2D();
            if (_transferTexture2D == null)
            {
                return;
            }

            EnsureDefault2DBoxes();

            var resolution = transferFunction2DResolution;
            var colors = new Color[resolution * resolution];

            for (var y = 0; y < resolution; ++y)
            {
                var gradient = resolution <= 1 ? 0f : y / (float)(resolution - 1);
                for (var x = 0; x < resolution; ++x)
                {
                    var density = resolution <= 1 ? 0f : x / (float)(resolution - 1);
                    var composited = Color.clear;

                    for (var i = 0; i < transferFunction2DBoxes.Count; ++i)
                    {
                        var box = transferFunction2DBoxes[i];
                        var densityMin = Mathf.Min(box.densityMin, box.densityMax);
                        var densityMax = Mathf.Max(box.densityMin, box.densityMax);
                        var gradientMin = Mathf.Min(box.gradientMin, box.gradientMax);
                        var gradientMax = Mathf.Max(box.gradientMin, box.gradientMax);

                        if (density < densityMin || density > densityMax || gradient < gradientMin || gradient > gradientMax)
                        {
                            continue;
                        }

                        var centerDensity = 0.5f * (densityMin + densityMax);
                        var centerGradient = 0.5f * (gradientMin + gradientMax);
                        var halfDensity = Mathf.Max(0.001f, 0.5f * (densityMax - densityMin));
                        var halfGradient = Mathf.Max(0.001f, 0.5f * (gradientMax - gradientMin));

                        var edgeDistance = Mathf.Max(
                            Mathf.Abs(density - centerDensity) / halfDensity,
                            Mathf.Abs(gradient - centerGradient) / halfGradient);

                        var softness = Mathf.Clamp01(box.edgeSoftness);
                        var alphaFactor = softness <= 0.0f ? 1.0f : Mathf.Clamp01(1.0f - Mathf.Pow(edgeDistance, Mathf.Lerp(1.0f, 3.5f, softness)));
                        var alpha = Mathf.Clamp01(box.alpha) * alphaFactor;
                        if (alpha <= 0.0f)
                        {
                            continue;
                        }

                        var boxColor = box.color;
                        composited.r += (1.0f - composited.a) * alpha * boxColor.r;
                        composited.g += (1.0f - composited.a) * alpha * boxColor.g;
                        composited.b += (1.0f - composited.a) * alpha * boxColor.b;
                        composited.a += (1.0f - composited.a) * alpha;
                    }

                    colors[x + y * resolution] = composited;
                }
            }

            _transferTexture2D.SetPixels(colors);
            _transferTexture2D.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            _transfer2DDirty = false;
            _materialDirty = true;
        }

        private void RebuildSliceTransferTexture1D()
        {
            EnsureSliceTransferTexture1D();
            if (_sliceTransferTexture1D == null)
            {
                return;
            }

            var colors = new Color[transferFunction1DResolution];
            if (TryResolveTransferFunctionPreset(sliceTransferFunctionPresetName, out var preset))
            {
                FillSliceTextureFromTransferFunctionPreset(colors, preset);
            }
            else
            {
                EnsureDefaultSliceGradient();
                for (var i = 0; i < transferFunction1DResolution; ++i)
                {
                    var t = transferFunction1DResolution <= 1 ? 0f : i / (float)(transferFunction1DResolution - 1);
                    var color = sliceColorGradient.Evaluate(t);
                    var alpha = Mathf.Clamp01(sliceAlphaCurve.Evaluate(t));
                    colors[i] = new Color(color.r, color.g, color.b, alpha);
                }
            }

            _sliceTransferTexture1D.SetPixels(colors);
            _sliceTransferTexture1D.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            _sliceTransfer1DDirty = false;
            MarkSliceMaterialDirty();
        }

        private void RebuildSliceTransferTexture2D()
        {
            EnsureSliceTransferTexture2D();
            if (_sliceTransferTexture2D == null)
            {
                return;
            }

            EnsureDefaultSlice2DBoxes();

            var resolution = transferFunction2DResolution;
            var colors = new Color[resolution * resolution];

            for (var y = 0; y < resolution; ++y)
            {
                var gradient = resolution <= 1 ? 0f : y / (float)(resolution - 1);
                for (var x = 0; x < resolution; ++x)
                {
                    var density = resolution <= 1 ? 0f : x / (float)(resolution - 1);
                    var composited = Color.clear;

                    for (var i = 0; i < sliceTransferFunction2DBoxes.Count; ++i)
                    {
                        var box = sliceTransferFunction2DBoxes[i];
                        var densityMin = Mathf.Min(box.densityMin, box.densityMax);
                        var densityMax = Mathf.Max(box.densityMin, box.densityMax);
                        var gradientMin = Mathf.Min(box.gradientMin, box.gradientMax);
                        var gradientMax = Mathf.Max(box.gradientMin, box.gradientMax);

                        if (density < densityMin || density > densityMax || gradient < gradientMin || gradient > gradientMax)
                        {
                            continue;
                        }

                        var centerDensity = 0.5f * (densityMin + densityMax);
                        var centerGradient = 0.5f * (gradientMin + gradientMax);
                        var halfDensity = Mathf.Max(0.001f, 0.5f * (densityMax - densityMin));
                        var halfGradient = Mathf.Max(0.001f, 0.5f * (gradientMax - gradientMin));

                        var edgeDistance = Mathf.Max(
                            Mathf.Abs(density - centerDensity) / halfDensity,
                            Mathf.Abs(gradient - centerGradient) / halfGradient);

                        var softness = Mathf.Clamp01(box.edgeSoftness);
                        var alphaFactor = softness <= 0.0f ? 1.0f : Mathf.Clamp01(1.0f - Mathf.Pow(edgeDistance, Mathf.Lerp(1.0f, 3.5f, softness)));
                        var alpha = Mathf.Clamp01(box.alpha) * alphaFactor;
                        if (alpha <= 0.0f)
                        {
                            continue;
                        }

                        var boxColor = box.color;
                        composited.r += (1.0f - composited.a) * alpha * boxColor.r;
                        composited.g += (1.0f - composited.a) * alpha * boxColor.g;
                        composited.b += (1.0f - composited.a) * alpha * boxColor.b;
                        composited.a += (1.0f - composited.a) * alpha;
                    }

                    colors[x + y * resolution] = composited;
                }
            }

            _sliceTransferTexture2D.SetPixels(colors);
            _sliceTransferTexture2D.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            _sliceTransfer2DDirty = false;
            MarkSliceMaterialDirty();
        }

        private void EnsureSegmentationColorMapTexture()
        {
            EnsureDefaultSegmentationColorMapFileName();
            RefreshSegmentationColorMapDirtyFlag();

            if (_segmentationColorMapTexture == null || _segmentationColorMapTexture.width != SegmentationPaletteSize)
            {
                DestroyTexture(ref _segmentationColorMapTexture);
                _segmentationColorMapTexture = new Texture2D(
                    SegmentationPaletteSize,
                    1,
                    TextureFormat.RGBA32,
                    mipChain: false,
                    linear: true);
                _segmentationColorMapTexture.name = "SimpleITKVolumeSegmentationColorMap";
                _segmentationColorMapTexture.wrapMode = TextureWrapMode.Clamp;
                _segmentationColorMapTexture.filterMode = FilterMode.Point;
                _segmentationColorMapDirty = true;
            }

            if (!_segmentationColorMapDirty)
            {
                return;
            }

            Color[] colors;
            var configuredColorMapFileName = GetConfiguredSegmentationColorMapFileName();
            if (!SimpleITKSegmentationColorMap.TryLoadPaletteFromStreamingAssets(
                    configuredColorMapFileName,
                    out colors,
                    out var loadError))
            {
                colors = SimpleITKSegmentationColorMap.CreateDefaultPalette();
                if (!string.Equals(_lastSegmentationColorMapError, loadError, StringComparison.Ordinal))
                {
                    Debug.LogWarning(
                        $"SimpleITKVolumeRenderer: failed to load segmentation color map '{configuredColorMapFileName}'. " +
                        $"Using built-in defaults. {loadError}");
                }

                _lastSegmentationColorMapError = loadError;
            }
            else
            {
                _lastSegmentationColorMapError = string.Empty;
            }

            _segmentationColorMapTexture.SetPixels(colors);
            _segmentationColorMapTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _segmentationColorMapDirty = false;
        }

        private void EnsureDefaultSegmentationColorMapFileName()
        {
            if (!string.IsNullOrWhiteSpace(segmentationColorMapFileName))
            {
                return;
            }

            segmentationColorMapFileName = SimpleITKSegmentationColorMap.DefaultFileName;
            _segmentationColorMapDirty = true;
        }

        private void RefreshSegmentationColorMapDirtyFlag()
        {
            string resolvedPath = string.Empty;
            long writeTimeTicks = 0;
            var configuredColorMapFileName = GetConfiguredSegmentationColorMapFileName();

            if (SimpleITKSegmentationColorMap.TryResolveStreamingAssetsPath(
                    configuredColorMapFileName,
                    out var candidatePath,
                    out _))
            {
                resolvedPath = candidatePath;
                if (File.Exists(resolvedPath))
                {
                    writeTimeTicks = File.GetLastWriteTimeUtc(resolvedPath).Ticks;
                }
            }

            var pathChanged = !string.Equals(_lastSegmentationColorMapPath, resolvedPath, StringComparison.OrdinalIgnoreCase);
            if (pathChanged || _lastSegmentationColorMapWriteTimeUtcTicks != writeTimeTicks)
            {
                _lastSegmentationColorMapPath = resolvedPath;
                _lastSegmentationColorMapWriteTimeUtcTicks = writeTimeTicks;
                _segmentationColorMapDirty = true;
            }
        }

        private string GetConfiguredSegmentationColorMapFileName()
        {
            ResolveVolumeLoader();
            if (volumeLoader != null)
            {
                var loaderFileName = volumeLoader.GetSegmentationColorMapFileName();
                if (!string.IsNullOrWhiteSpace(loaderFileName))
                {
                    return loaderFileName;
                }
            }

            EnsureDefaultSegmentationColorMapFileName();
            return segmentationColorMapFileName;
        }

        private void ApplyMaterialProperties()
        {
            EnsureMaterial();
            if (_runtimeMaterial == null)
            {
                return;
            }

            if (_volumeTexture != null)
            {
                _runtimeMaterial.SetTexture("_VolumeTex", _volumeTexture);
            }

            var segmentationOverlayEnabled = showSegmentationOverlayInVolume && _segmentationTexture != null;
            _runtimeMaterial.SetFloat("_UseSegmentationOverlay", segmentationOverlayEnabled ? 1.0f : 0.0f);
            _runtimeMaterial.SetFloat("_SegmentationOverlayOpacity", segmentationOverlayOpacityInVolume);
            if (segmentationOverlayEnabled)
            {
                EnsureSegmentationColorMapTexture();
                _runtimeMaterial.SetTexture("_SegmentationTex", _segmentationTexture);
                if (_segmentationColorMapTexture != null)
                {
                    _runtimeMaterial.SetTexture("_SegmentationColorTex", _segmentationColorMapTexture);
                }
            }

            if (_transferTexture1D != null)
            {
                _runtimeMaterial.SetTexture("_TransferTex", _transferTexture1D);
            }

            if (_transferTexture2D != null)
            {
                _runtimeMaterial.SetTexture("_TransferTex2D", _transferTexture2D);
            }

            var maxDimension = Mathf.Max(_volumeWidth, Mathf.Max(_volumeHeight, _volumeDepth));
            var stepSize = maxDimension > 0 ? stepScale / maxDimension : 0.005f;

            _runtimeMaterial.SetFloat("_StepSize", stepSize);
            _runtimeMaterial.SetFloat("_MaxSteps", maxSteps);
            _runtimeMaterial.SetFloat("_Opacity", opacityMultiplier);
            _runtimeMaterial.SetFloat("_Brightness", brightness);
            _runtimeMaterial.SetFloat("_ClipMin", intensityWindowMin);
            _runtimeMaterial.SetFloat("_ClipMax", Mathf.Max(intensityWindowMin + 1e-5f, intensityWindowMax));
            _runtimeMaterial.SetFloat("_TFMode", transferFunctionMode == TransferFunctionMode.TwoDimensional ? 1.0f : 0.0f);
            _runtimeMaterial.SetFloat("_GradientScale", gradientScale);

            var invVolumeSize = GetInvVolumeSize();
            _runtimeMaterial.SetVector("_InvVolumeSize", new Vector4(invVolumeSize.x, invVolumeSize.y, invVolumeSize.z, 0.0f));

            GetBoxBounds01(out var boxMin, out var boxMax);

            _runtimeMaterial.SetFloat("_EnableBoxCut", enableBoxCrossSection ? 1.0f : 0.0f);
            _runtimeMaterial.SetVector("_BoxMin", new Vector4(boxMin.x, boxMin.y, boxMin.z, 0.0f));
            _runtimeMaterial.SetVector("_BoxMax", new Vector4(boxMax.x, boxMax.y, boxMax.z, 0.0f));
            _runtimeMaterial.SetFloat("_BoxInvert", invertBox ? 1.0f : 0.0f);
            ApplyLightingProperties();

            _materialDirty = false;
        }

        private void ApplyLightingProperties()
        {
            if (_runtimeMaterial == null)
            {
                return;
            }

            var lightDir = GetLightDirectionWorld();
            var lightDirOs = transform.InverseTransformDirection(lightDir).normalized;

            _runtimeMaterial.SetFloat("_LightingMode", (float)lightingTechnique);
            _runtimeMaterial.SetVector("_LightDirOS", new Vector4(lightDirOs.x, lightDirOs.y, lightDirOs.z, 0.0f));
            _runtimeMaterial.SetFloat("_IsoThreshold", isoSurfaceThreshold);
            _runtimeMaterial.SetColor("_IsoColor", isoSurfaceColor);
            _runtimeMaterial.SetFloat("_IsoAmbient", isoSurfaceAmbient);
            _runtimeMaterial.SetFloat("_IsoDiffuse", isoSurfaceDiffuse);
            _runtimeMaterial.SetFloat("_IsoSpecular", isoSurfaceSpecular);
            _runtimeMaterial.SetFloat("_IsoSpecPower", isoSurfaceSpecularPower);

            if (!IsCinematicLightingTechnique(lightingTechnique))
            {
                return;
            }

            _runtimeMaterial.SetFloat("_CinematicLightFollowsCamera", cinematicLightFollowsCamera ? 1.0f : 0.0f);
            _runtimeMaterial.SetFloat("_CinematicAmbient", cinematicAmbient);
            _runtimeMaterial.SetFloat("_CinematicLightIntensity", cinematicLightIntensity);
            _runtimeMaterial.SetFloat("_CinematicDiffuse", cinematicDiffuse);
            _runtimeMaterial.SetFloat("_CinematicSpecular", cinematicSpecular);
            _runtimeMaterial.SetFloat("_CinematicSpecPower", cinematicSpecularPower);
            _runtimeMaterial.SetFloat("_CinematicShadowStrength", cinematicShadowStrength);
            _runtimeMaterial.SetFloat("_CinematicShadowStepMultiplier", cinematicShadowStepMultiplier);
            _runtimeMaterial.SetFloat("_CinematicShadowMaxSteps", cinematicShadowMaxSteps);
            _runtimeMaterial.SetFloat("_CinematicScatterBlend", cinematicScatterBlend);
            _runtimeMaterial.SetFloat("_CinematicExtinction", cinematicExtinction);
            _runtimeMaterial.SetFloat("_CinematicAoStrength", cinematicAoStrength);
            _runtimeMaterial.SetFloat("_CinematicAoRadius", cinematicAoRadius);
            _runtimeMaterial.SetFloat("_CinematicAoSamples", cinematicAoSamples);
        }

        private void EnsureLightingModeSchema()
        {
            var mode = Mathf.Clamp((int)lightingTechnique, 0, (int)LightingTechnique.CinematicHybrid);
            var sanitized = (LightingTechnique)mode;
            if (lightingTechnique != sanitized)
            {
                lightingTechnique = sanitized;
                _materialDirty = true;
            }

            if (lightingModeSchemaVersion < LightingModeSchemaCurrentVersion)
            {
                lightingModeSchemaVersion = LightingModeSchemaCurrentVersion;
                _materialDirty = true;
            }
        }

        private void EnsureCinematicParameterSchema()
        {
            if (cinematicParameterSchemaVersion >= CinematicParameterSchemaCurrentVersion)
            {
                return;
            }

            if (cinematicParameterSchemaVersion < 2 && HasLegacyCinematicDefaults())
            {
                SetRecommendedCinematicDefaults();
                _materialDirty = true;
            }

            if (cinematicParameterSchemaVersion < 3)
            {
                cinematicLightIntensity = 1.0f;
                _materialDirty = true;
            }

            cinematicParameterSchemaVersion = CinematicParameterSchemaCurrentVersion;
        }

        private bool HasLegacyCinematicDefaults()
        {
            return cinematicLightFollowsCamera &&
                   Approximately(cinematicAmbient, 0.2f) &&
                   Approximately(cinematicDiffuse, 0.7f) &&
                   Approximately(cinematicSpecular, 0.3f) &&
                   Approximately(cinematicSpecularPower, 24f) &&
                   Approximately(cinematicShadowStrength, 0.8f) &&
                   Approximately(cinematicShadowStepMultiplier, 1.0f) &&
                   cinematicShadowMaxSteps == 48 &&
                   Approximately(cinematicScatterBlend, 0.5f) &&
                   Approximately(cinematicExtinction, 1.25f) &&
                   Approximately(cinematicAoStrength, 0.85f) &&
                   Approximately(cinematicAoRadius, 2.5f) &&
                   cinematicAoSamples == 4;
        }

        private void EnsureDefaultLightDirectionPreset()
        {
            if (lightDirectionPreset != LightDirectionPreset.Custom)
            {
                return;
            }

            if (!Approximately(lightDirectionWorld, LegacyDefaultLightDirection))
            {
                return;
            }

            lightDirectionPreset = LightDirectionPreset.FrontLowerLeft;
            _lastAppliedLightDirectionPreset = LightDirectionPreset.Custom;
        }

        private void EnsureSelectedLightDirectionPresetApplied()
        {
            if (lightDirectionPreset == LightDirectionPreset.Custom)
            {
                _lastAppliedLightDirectionPreset = LightDirectionPreset.Custom;
                return;
            }

            if (_lastAppliedLightDirectionPreset == lightDirectionPreset)
            {
                return;
            }

            ApplySelectedLightDirectionPreset();
        }

        private void SetRecommendedCinematicDefaults()
        {
            cinematicLightFollowsCamera = true;
            cinematicAmbient = 0.22f;
            cinematicLightIntensity = 1.0f;
            cinematicDiffuse = 0.9f;
            cinematicSpecular = 0.18f;
            cinematicSpecularPower = 36f;
            cinematicShadowStrength = 0.6f;
            cinematicShadowStepMultiplier = 0.75f;
            cinematicShadowMaxSteps = 72;
            cinematicScatterBlend = 0.22f;
            cinematicExtinction = 0.35f;
            cinematicAoStrength = 0.55f;
            cinematicAoRadius = 1.75f;
            cinematicAoSamples = 6;
        }

        private Shader ResolveActiveRaymarchShader()
        {
            var shaderOverride = IsCinematicLightingTechnique(lightingTechnique)
                ? cinematicRaymarchShader
                : raymarchShader;
            if (shaderOverride != null)
            {
                return shaderOverride;
            }

            var shaderName = IsCinematicLightingTechnique(lightingTechnique)
                ? CinematicShaderName
                : BasicShaderName;
            return Shader.Find(shaderName);
        }

        private static Vector3 GetDirectionForLightPreset(LightDirectionPreset preset)
        {
            switch (preset)
            {
                case LightDirectionPreset.Front:
                    return NormalizeLightDirection(new Vector3(0.0f, 0.0f, -1.0f));
                case LightDirectionPreset.Back:
                    return NormalizeLightDirection(new Vector3(0.0f, 0.0f, 1.0f));
                case LightDirectionPreset.Left:
                    return NormalizeLightDirection(new Vector3(-1.0f, 0.0f, 0.0f));
                case LightDirectionPreset.Right:
                    return NormalizeLightDirection(new Vector3(1.0f, 0.0f, 0.0f));
                case LightDirectionPreset.Top:
                    return NormalizeLightDirection(new Vector3(0.0f, 1.0f, 0.0f));
                case LightDirectionPreset.Bottom:
                    return NormalizeLightDirection(new Vector3(0.0f, -1.0f, 0.0f));
                case LightDirectionPreset.FrontUpperLeft:
                    return NormalizeLightDirection(new Vector3(-1.0f, 1.0f, -1.0f));
                case LightDirectionPreset.FrontUpperRight:
                    return NormalizeLightDirection(new Vector3(1.0f, 1.0f, -1.0f));
                case LightDirectionPreset.FrontLowerLeft:
                    return NormalizeLightDirection(new Vector3(-1.0f, -1.0f, -1.0f));
                case LightDirectionPreset.FrontLowerRight:
                    return NormalizeLightDirection(new Vector3(1.0f, -1.0f, -1.0f));
                case LightDirectionPreset.BackUpperLeft:
                    return NormalizeLightDirection(new Vector3(-1.0f, 1.0f, 1.0f));
                case LightDirectionPreset.BackUpperRight:
                    return NormalizeLightDirection(new Vector3(1.0f, 1.0f, 1.0f));
                case LightDirectionPreset.BackLowerLeft:
                    return NormalizeLightDirection(new Vector3(-1.0f, -1.0f, 1.0f));
                case LightDirectionPreset.BackLowerRight:
                    return NormalizeLightDirection(new Vector3(1.0f, -1.0f, 1.0f));
                default:
                    return NormalizeLightDirection(LegacyDefaultLightDirection);
            }
        }

        private void EnsureShaderReferences()
        {
            var changed = false;
            changed |= TryAssignShaderReference(ref raymarchShader, BasicShaderName);
            changed |= TryAssignShaderReference(ref cinematicRaymarchShader, CinematicShaderName);

#if UNITY_EDITOR
            if (changed && !Application.isPlaying)
            {
                EditorUtility.SetDirty(this);
            }
#endif
        }

        private static bool TryAssignShaderReference(ref Shader shaderField, string shaderName)
        {
            if (shaderField != null)
            {
                return false;
            }

            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                return false;
            }

            shaderField = shader;
            return true;
        }

        private static bool IsCinematicLightingTechnique(LightingTechnique technique)
        {
            return technique == LightingTechnique.CinematicStandard ||
                   technique == LightingTechnique.CinematicAmbientOcclusion ||
                   technique == LightingTechnique.CinematicHybrid;
        }

        private static Vector3 NormalizeLightDirection(Vector3 direction)
        {
            var fallback = new Vector3(-0.5f, -0.7f, -0.5f);
            if (direction.sqrMagnitude > 1e-6f)
            {
                return direction.normalized;
            }

            return fallback.normalized;
        }

        private void ResolveVolumeLoader()
        {
            if (volumeLoader != null)
            {
                volumeLoader.SetTargetRenderer(this);
                return;
            }

            var current = transform;
            while (current != null && volumeLoader == null)
            {
                volumeLoader = current.GetComponentInChildren<SimpleITKVolumeLoader>(true);
                current = current.parent;
            }

            if (volumeLoader == null)
            {
                var all = FindObjectsByType<SimpleITKVolumeLoader>(FindObjectsSortMode.None);
                var thisScene = gameObject.scene;
                for (var i = 0; i < all.Length; ++i)
                {
                    var candidate = all[i];
                    if (candidate == null || candidate.gameObject.scene != thisScene)
                    {
                        continue;
                    }

                    volumeLoader = candidate;
                    break;
                }
            }

            if (volumeLoader != null)
            {
                volumeLoader.SetTargetRenderer(this);
            }
        }

        private void TryRecoverLoadViaVolumeLoaderIfNeeded()
        {
            if (_volumeTexture != null)
            {
                _runtimeRecoverLoadRequested = false;
                return;
            }

            ResolveVolumeLoader();
            if (Application.isPlaying)
            {
                if (_runtimeRecoverLoadRequested)
                {
                    return;
                }

                if (!HasVolumeSourceConfigured())
                {
                    return;
                }

                _runtimeRecoverLoadRequested = true;
                if (volumeLoader != null)
                {
                    volumeLoader.LoadVolume();
                }
                else
                {
                    LoadVolumeLegacy();
                }
                return;
            }

#if UNITY_EDITOR
            if (volumeLoader == null)
            {
                return;
            }

            if (!volumeLoader.HasVolumeSource)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (now < _nextEditorRecoverLoadTime)
            {
                return;
            }

            _nextEditorRecoverLoadTime = now + 0.75;
            volumeLoader.LoadVolume();
#endif
        }

        private SimpleITKProcessingController ResolveProcessingController()
        {
            ResolveVolumeLoader();
            if (volumeLoader != null && volumeLoader.TryGetAssignedProcessingController(out var loaderController))
            {
                processingController = loaderController;
                return processingController;
            }

            if (processingController != null)
            {
                return processingController;
            }

            processingController = GetComponent<SimpleITKProcessingController>();
            if (processingController != null)
            {
                return processingController;
            }

            processingController = GetComponentInParent<SimpleITKProcessingController>();
            if (processingController != null)
            {
                return processingController;
            }

            if (transform.parent != null)
            {
                processingController = transform.parent.GetComponentInChildren<SimpleITKProcessingController>(true);
                if (processingController != null)
                {
                    return processingController;
                }
            }

            processingController = GetComponentInChildren<SimpleITKProcessingController>(true);
            return processingController;
        }

        private void ApplyVolumeTransformFromImageInfo(SimpleITKImageInfo info)
        {
            var scaleMagnitudes = GetCurrentScaleMagnitudes();
            if (fitObjectScaleToSpacing)
            {
                if (!TryGetNormalizedScaleFromImageInfo(info, out scaleMagnitudes))
                {
                    return;
                }
            }

            var rotation = Quaternion.identity;
            var scaleSign = Vector3.one;
            if (TryGetVolumeOrientation(info, out var imageRotation, out var imageScaleSign))
            {
                rotation = imageRotation;
                scaleSign = imageScaleSign;
            }

            transform.localRotation = rotation;
            transform.localScale = new Vector3(
                Mathf.Abs(scaleMagnitudes.x) * scaleSign.x,
                Mathf.Abs(scaleMagnitudes.y) * scaleSign.y,
                Mathf.Abs(scaleMagnitudes.z) * scaleSign.z);
            MarkSliceMaterialDirty();
        }

        private Vector3 GetCurrentScaleMagnitudes()
        {
            var scale = transform.localScale;
            return new Vector3(
                Mathf.Max(1e-6f, Mathf.Abs(scale.x)),
                Mathf.Max(1e-6f, Mathf.Abs(scale.y)),
                Mathf.Max(1e-6f, Mathf.Abs(scale.z)));
        }

        private static bool TryGetNormalizedScaleFromImageInfo(SimpleITKImageInfo info, out Vector3 scale)
        {
            scale = Vector3.one;
            if (info.Spacing == null || info.Spacing.Length < 3 || info.Size == null || info.Size.Length < 3)
            {
                return false;
            }

            var sx = Mathf.Max(1e-6f, (float)info.Spacing[0] * info.Size[0]);
            var sy = Mathf.Max(1e-6f, (float)info.Spacing[1] * info.Size[1]);
            var sz = Mathf.Max(1e-6f, (float)info.Spacing[2] * info.Size[2]);
            var maxAxis = Mathf.Max(sx, Mathf.Max(sy, sz));
            if (maxAxis <= 0f)
            {
                return false;
            }

            scale = new Vector3(sx / maxAxis, sy / maxAxis, sz / maxAxis);
            return true;
        }

        private static bool TryGetVolumeOrientation(
            SimpleITKImageInfo info,
            out Quaternion rotation,
            out Vector3 scaleSign)
        {
            rotation = Quaternion.identity;
            scaleSign = Vector3.one;

            if (info.Direction == null || info.Direction.Length < 9)
            {
                return false;
            }

            var axisX = new Vector3(
                (float)info.Direction[0],
                (float)info.Direction[3],
                (float)info.Direction[6]);
            var axisY = new Vector3(
                (float)info.Direction[1],
                (float)info.Direction[4],
                (float)info.Direction[7]);
            var axisZ = new Vector3(
                (float)info.Direction[2],
                (float)info.Direction[5],
                (float)info.Direction[8]);

            if (!TryNormalizeAxis(ref axisX) || !TryNormalizeAxis(ref axisY) || !TryNormalizeAxis(ref axisZ))
            {
                return false;
            }

            axisY -= axisX * Vector3.Dot(axisX, axisY);
            if (!TryNormalizeAxis(ref axisY))
            {
                return false;
            }

            var detSign = Vector3.Dot(axisZ, Vector3.Cross(axisX, axisY)) < 0f ? -1f : 1f;
            var rotationForward = Vector3.Cross(axisX, axisY);
            if (!TryNormalizeAxis(ref rotationForward))
            {
                return false;
            }

            rotation = Quaternion.LookRotation(rotationForward, axisY);
            scaleSign = new Vector3(1f, 1f, detSign);
            return true;
        }

        private static bool TryNormalizeAxis(ref Vector3 axis)
        {
            if (axis.sqrMagnitude < 1e-8f)
            {
                return false;
            }

            axis.Normalize();
            return true;
        }

        private string ResolveVolumePathLegacy()
        {
            if (loadFromStreamingAssetsSample)
            {
                if (string.IsNullOrWhiteSpace(sampleFileName))
                {
                    return string.Empty;
                }

                var samplePath = CombinePathPreservingUri(GetSampleDirectoryPath(), sampleFileName);
                if (IsRemotePath(samplePath))
                {
                    return samplePath;
                }

                return Path.GetFullPath(samplePath);
            }

            return ResolveGenericPath(volumeFilePath);
        }

        private void TryAutoLoadInEditor()
        {
            ResolveVolumeLoader();
            if (volumeLoader != null)
            {
                return;
            }

            if (Application.isPlaying || !loadInEditMode)
            {
                return;
            }

            var resolvedPath = ResolveVolumePathLegacy();
            var pathExists = !string.IsNullOrWhiteSpace(resolvedPath) &&
                             !IsRemotePath(resolvedPath) &&
                             (File.Exists(resolvedPath) || Directory.Exists(resolvedPath));
            if (!pathExists)
            {
                _editorAutoLoadAttempted = false;
                return;
            }

            var pathChanged = !string.Equals(_lastEditorAutoLoadPath, resolvedPath, StringComparison.OrdinalIgnoreCase);
            if (pathChanged)
            {
                _editorAutoLoadAttempted = false;
            }

            if (_volumeTexture != null && !pathChanged)
            {
                return;
            }

            // If resources were destroyed (OnDisable/domain reload) while keeping the same path,
            // allow editor auto-load to run again.
            if (_volumeTexture == null && _editorAutoLoadAttempted)
            {
                _editorAutoLoadAttempted = false;
            }

            if (_editorAutoLoadAttempted)
            {
                return;
            }

            _lastEditorAutoLoadPath = resolvedPath;
            _editorAutoLoadAttempted = true;
            LoadVolume();
#if UNITY_EDITOR
            if (_volumeTexture != null)
            {
                SceneView.RepaintAll();
            }
#endif
        }

        private static string GetSampleDirectoryPath()
        {
            return CombinePathPreservingUri(Application.streamingAssetsPath, "Sample");
        }

        private static string ResolveGenericPath(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return string.Empty;
            }

            return Path.IsPathRooted(inputPath)
                ? inputPath
                : Path.GetFullPath(Path.Combine(Application.dataPath, inputPath));
        }

        private static bool IsRemotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase) ||
                   path.IndexOf("://", StringComparison.Ordinal) >= 0;
        }

        private static string CombinePathPreservingUri(string basePath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                return relativePath ?? string.Empty;
            }

            var normalizedRelative = (relativePath ?? string.Empty)
                .Replace('\\', '/')
                .TrimStart('/');

            if (IsRemotePath(basePath))
            {
                if (string.IsNullOrEmpty(normalizedRelative))
                {
                    return basePath.TrimEnd('/');
                }

                return $"{basePath.TrimEnd('/')}/{normalizedRelative}";
            }

            if (string.IsNullOrEmpty(normalizedRelative))
            {
                return basePath;
            }

            return Path.Combine(basePath, normalizedRelative);
        }

        public void LogRenderDiagnostics()
        {
            LogRenderDiagnostics("Manual");
        }

        private void LogRenderDiagnostics(string reason)
        {
            var renderer = _meshRenderer != null ? _meshRenderer : GetComponent<MeshRenderer>();
            var material = _runtimeMaterial;
            var shader = material != null ? material.shader : null;
            var texture = _volumeTexture;

            var diag =
                $"SimpleITKVolumeRenderer[{reason}] diag: " +
                $"active={gameObject.activeInHierarchy}, layer={gameObject.layer}, " +
                $"meshRenderer={(renderer != null ? "ok" : "null")}, meshEnabled={(renderer != null && renderer.enabled)}, " +
                $"sharedMat={(renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "null")}, " +
                $"runtimeMat={(material != null ? material.name : "null")}, " +
                $"shader={(shader != null ? shader.name : "null")}, shaderSupported={(shader != null && shader.isSupported)}, " +
                $"volumeTex={(texture != null ? $"{texture.width}x{texture.height}x{texture.depth}" : "null")}, " +
                $"window3D=[{intensityWindowMin:F3},{intensityWindowMax:F3}], " +
                $"windowABC=[{GetSliceClipMin():F3},{GetSliceClipMax():F3}], syncABC={syncAbcIntensityWindowToVolume}, " +
                $"opacity={opacityMultiplier:F3}, brightness={brightness:F3}, " +
                $"boxCut={enableBoxCrossSection}, invertBox={invertBox}, boxCenter={boxCenter01}, boxSize={boxSize01}, " +
                $"tfMode={transferFunctionMode}, lighting={lightingTechnique}, lightPreset={lightDirectionPreset}, lightDir={lightDirectionWorld}, " +
                $"cinematicLightFollowsCamera={cinematicLightFollowsCamera}, lightIntensity={cinematicLightIntensity:F2}, shadow={cinematicShadowStrength:F2}/{cinematicShadowMaxSteps}, " +
                $"ao={cinematicAoStrength:F2}/{cinematicAoRadius:F2}/{cinematicAoSamples}, scatter={cinematicScatterBlend:F2}.";

            Debug.Log(diag);
        }

        private void AutoFitIntensityWindowFromVolume(float[] voxelsNormalized)
        {
            if (voxelsNormalized == null || voxelsNormalized.Length == 0)
            {
                return;
            }

            var lowPercentile = Mathf.Clamp01(autoWindowLowPercentile);
            var highPercentile = Mathf.Clamp(autoWindowHighPercentile, lowPercentile + 0.01f, 1f);

            const int bins = 512;
            var histogram = new int[bins];
            for (var i = 0; i < voxelsNormalized.Length; ++i)
            {
                var value = Mathf.Clamp01(voxelsNormalized[i]);
                var bin = Mathf.Clamp(Mathf.RoundToInt(value * (bins - 1)), 0, bins - 1);
                histogram[bin]++;
            }

            var total = voxelsNormalized.Length;
            var lowTarget = Mathf.Clamp(Mathf.RoundToInt(total * lowPercentile), 0, total - 1);
            var highTarget = Mathf.Clamp(Mathf.RoundToInt(total * highPercentile), 0, total - 1);

            var cumulative = 0;
            var lowBin = 0;
            var highBin = bins - 1;
            for (var i = 0; i < bins; ++i)
            {
                cumulative += histogram[i];
                if (cumulative > lowTarget)
                {
                    lowBin = i;
                    break;
                }
            }

            cumulative = 0;
            for (var i = 0; i < bins; ++i)
            {
                cumulative += histogram[i];
                if (cumulative > highTarget)
                {
                    highBin = i;
                    break;
                }
            }

            var fittedMin = lowBin / (float)(bins - 1);
            var fittedMax = highBin / (float)(bins - 1);
            if (fittedMax - fittedMin < 0.05f)
            {
                var center = 0.5f * (fittedMin + fittedMax);
                fittedMin = Mathf.Clamp01(center - 0.1f);
                fittedMax = Mathf.Clamp01(center + 0.1f);
            }

            fittedMax = Mathf.Max(fittedMin + 0.01f, fittedMax);

            if (!Mathf.Approximately(intensityWindowMin, fittedMin) || !Mathf.Approximately(intensityWindowMax, fittedMax))
            {
                intensityWindowMin = fittedMin;
                intensityWindowMax = fittedMax;
                SyncSliceIntensityWindowToVolumeIfNeeded();
                _transfer1DDirty = true;
                _sliceTransfer1DDirty = true;
                _materialDirty = true;
            }
        }

        private void InitializeDefault1DOTF(bool force)
        {
            if (!force && _default1DOTFInitialized)
            {
                return;
            }

            ApplyDefaultVolumeGradient();
            alphaCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            transferFunctionSchemaVersion = TransferFunctionSchemaCurrentVersion;
            _default1DOTFInitialized = true;
            _transfer1DDirty = true;
            _materialDirty = true;
        }

        private void EnsureDefaultGradient()
        {
            if (colorGradient == null)
            {
                colorGradient = new Gradient();
            }

            var colorKeys = colorGradient.colorKeys;
            var alphaKeys = colorGradient.alphaKeys;

            if (colorKeys == null || colorKeys.Length == 0 || alphaKeys == null || alphaKeys.Length == 0)
            {
                ApplyDefaultVolumeGradient();
            }
        }

        private void EnsureTransferFunctionSchema()
        {
            if (transferFunctionSchemaVersion >= TransferFunctionSchemaCurrentVersion)
            {
                return;
            }

            if (IsLegacyDefaultVolumeGradient(colorGradient))
            {
                ApplyDefaultVolumeGradient();
                _transfer1DDirty = true;
                _materialDirty = true;
            }

            if (alphaCurve == null || alphaCurve.length == 0)
            {
                alphaCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
                _transfer1DDirty = true;
                _materialDirty = true;
            }

            transferFunctionSchemaVersion = TransferFunctionSchemaCurrentVersion;
        }

        private void EnsureSelectedColorPresetsApplied()
        {
            if (TryResolveTransferFunctionPreset(volumeTransferFunctionPresetName, out _))
            {
                if (!string.Equals(
                        _lastAppliedVolumeTransferFunctionPresetName,
                        volumeTransferFunctionPresetName,
                        StringComparison.Ordinal))
                {
                    _lastAppliedVolumeTransferFunctionPresetName = volumeTransferFunctionPresetName ?? string.Empty;
                    TryApplyVolumePresetEffectiveRangeToWindow();
                    _transfer1DDirty = true;
                    _materialDirty = true;
                }

                _lastAppliedVolumeColorPreset = ColorPreset.Custom;
            }
            else if (volumeColorPreset == ColorPreset.Custom)
            {
                _lastAppliedVolumeTransferFunctionPresetName = string.Empty;
                _lastAppliedVolumeColorPreset = ColorPreset.Custom;
            }
            else if (_lastAppliedVolumeColorPreset != volumeColorPreset || !HasGradientKeys(colorGradient))
            {
                _lastAppliedVolumeTransferFunctionPresetName = string.Empty;
                ForceApplyVolumeColorPreset(volumeColorPreset);
            }

            if (TryResolveTransferFunctionPreset(sliceTransferFunctionPresetName, out _))
            {
                if (!string.Equals(
                        _lastAppliedSliceTransferFunctionPresetName,
                        sliceTransferFunctionPresetName,
                        StringComparison.Ordinal))
                {
                    _lastAppliedSliceTransferFunctionPresetName = sliceTransferFunctionPresetName ?? string.Empty;
                    _sliceTransfer1DDirty = true;
                }

                _lastAppliedSliceColorPreset = ColorPreset.Custom;
            }
            else if (sliceColorPreset == ColorPreset.Custom)
            {
                _lastAppliedSliceTransferFunctionPresetName = string.Empty;
                _lastAppliedSliceColorPreset = ColorPreset.Custom;
            }
            else if (_lastAppliedSliceColorPreset != sliceColorPreset || !HasGradientKeys(sliceColorGradient))
            {
                _lastAppliedSliceTransferFunctionPresetName = string.Empty;
                ForceApplySliceColorPreset(sliceColorPreset);
            }
        }

        private void ApplyDefaultVolumeGradient()
        {
            ApplyGradientPreset(ref colorGradient, ColorPreset.CinematicWarm);
        }

        private void ForceApplyVolumeColorPreset(ColorPreset preset)
        {
            if (preset == ColorPreset.Custom)
            {
                _lastAppliedVolumeColorPreset = ColorPreset.Custom;
                return;
            }

            ApplyGradientPreset(ref colorGradient, preset);
            _lastAppliedVolumeColorPreset = preset;
            _transfer1DDirty = true;
            _materialDirty = true;
        }

        private void ForceApplySliceColorPreset(ColorPreset preset)
        {
            if (preset == ColorPreset.Custom)
            {
                _lastAppliedSliceColorPreset = ColorPreset.Custom;
                return;
            }

            ApplyGradientPreset(ref sliceColorGradient, preset);
            _lastAppliedSliceColorPreset = preset;
            _sliceTransfer1DDirty = true;
        }

        private static bool HasGradientKeys(Gradient gradient)
        {
            return gradient != null &&
                gradient.colorKeys != null &&
                gradient.colorKeys.Length > 0 &&
                gradient.alphaKeys != null &&
                gradient.alphaKeys.Length > 0;
        }

        private static void ApplyGradientPreset(ref Gradient gradient, ColorPreset preset)
        {
            gradient ??= new Gradient();
            gradient.SetKeys(
                GetColorKeysForPreset(preset),
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 1f)
                });
        }

        private static GradientColorKey[] GetColorKeysForPreset(ColorPreset preset)
        {
            switch (preset)
            {
                case ColorPreset.CinematicWarm:
                    return new[]
                    {
                        new GradientColorKey(new Color(0.01f, 0.01f, 0.015f), 0f),
                        new GradientColorKey(new Color(0.08f, 0.14f, 0.22f), 0.16f),
                        new GradientColorKey(new Color(0.24f, 0.34f, 0.44f), 0.38f),
                        new GradientColorKey(new Color(0.68f, 0.50f, 0.28f), 0.72f),
                        new GradientColorKey(new Color(0.98f, 0.90f, 0.78f), 1f)
                    };

                case ColorPreset.Bone:
                    return new[]
                    {
                        new GradientColorKey(new Color(0.02f, 0.02f, 0.025f), 0f),
                        new GradientColorKey(new Color(0.20f, 0.17f, 0.14f), 0.18f),
                        new GradientColorKey(new Color(0.55f, 0.47f, 0.35f), 0.48f),
                        new GradientColorKey(new Color(0.86f, 0.78f, 0.64f), 0.78f),
                        new GradientColorKey(new Color(0.98f, 0.95f, 0.88f), 1f)
                    };

                case ColorPreset.SoftTissue:
                    return new[]
                    {
                        new GradientColorKey(new Color(0.02f, 0.01f, 0.02f), 0f),
                        new GradientColorKey(new Color(0.16f, 0.07f, 0.08f), 0.18f),
                        new GradientColorKey(new Color(0.46f, 0.20f, 0.18f), 0.45f),
                        new GradientColorKey(new Color(0.80f, 0.55f, 0.45f), 0.75f),
                        new GradientColorKey(new Color(0.96f, 0.82f, 0.72f), 1f)
                    };

                case ColorPreset.Vascular:
                    return new[]
                    {
                        new GradientColorKey(new Color(0.01f, 0.0f, 0.0f), 0f),
                        new GradientColorKey(new Color(0.20f, 0.0f, 0.03f), 0.16f),
                        new GradientColorKey(new Color(0.55f, 0.03f, 0.08f), 0.42f),
                        new GradientColorKey(new Color(0.86f, 0.22f, 0.12f), 0.72f),
                        new GradientColorKey(new Color(1.0f, 0.82f, 0.68f), 1f)
                    };

                case ColorPreset.Fire:
                    return new[]
                    {
                        new GradientColorKey(new Color(0.0f, 0.0f, 0.0f), 0f),
                        new GradientColorKey(new Color(0.18f, 0.01f, 0.01f), 0.12f),
                        new GradientColorKey(new Color(0.62f, 0.08f, 0.02f), 0.36f),
                        new GradientColorKey(new Color(0.95f, 0.44f, 0.04f), 0.72f),
                        new GradientColorKey(new Color(1.0f, 0.92f, 0.62f), 1f)
                    };

                case ColorPreset.CoolMetal:
                    return new[]
                    {
                        new GradientColorKey(new Color(0.01f, 0.02f, 0.03f), 0f),
                        new GradientColorKey(new Color(0.08f, 0.14f, 0.20f), 0.16f),
                        new GradientColorKey(new Color(0.34f, 0.46f, 0.56f), 0.45f),
                        new GradientColorKey(new Color(0.72f, 0.80f, 0.84f), 0.78f),
                        new GradientColorKey(new Color(0.96f, 0.98f, 1.0f), 1f)
                    };

                case ColorPreset.PET:
                    return new[]
                    {
                        new GradientColorKey(new Color(0.0f, 0.0f, 0.0f), 0f),
                        new GradientColorKey(new Color(0.0f, 0.10f, 0.40f), 0.10f),
                        new GradientColorKey(new Color(0.45f, 0.0f, 0.58f), 0.34f),
                        new GradientColorKey(new Color(0.95f, 0.28f, 0.08f), 0.70f),
                        new GradientColorKey(new Color(1.0f, 0.95f, 0.50f), 1f)
                    };

                case ColorPreset.Sunset:
                    return new[]
                    {
                        new GradientColorKey(new Color(0.03f, 0.02f, 0.08f), 0f),
                        new GradientColorKey(new Color(0.15f, 0.10f, 0.30f), 0.18f),
                        new GradientColorKey(new Color(0.52f, 0.18f, 0.42f), 0.44f),
                        new GradientColorKey(new Color(0.94f, 0.46f, 0.22f), 0.76f),
                        new GradientColorKey(new Color(1.0f, 0.88f, 0.68f), 1f)
                    };

                case ColorPreset.Emerald:
                    return new[]
                    {
                        new GradientColorKey(new Color(0.0f, 0.02f, 0.02f), 0f),
                        new GradientColorKey(new Color(0.0f, 0.12f, 0.10f), 0.16f),
                        new GradientColorKey(new Color(0.06f, 0.42f, 0.28f), 0.42f),
                        new GradientColorKey(new Color(0.30f, 0.74f, 0.44f), 0.76f),
                        new GradientColorKey(new Color(0.92f, 1.0f, 0.82f), 1f)
                    };

                case ColorPreset.Grayscale:
                    return new[]
                    {
                        new GradientColorKey(Color.black, 0f),
                        new GradientColorKey(new Color(0.35f, 0.35f, 0.35f), 0.35f),
                        new GradientColorKey(new Color(0.72f, 0.72f, 0.72f), 0.7f),
                        new GradientColorKey(Color.white, 1f)
                    };

                default:
                    return new[]
                    {
                        new GradientColorKey(Color.black, 0f),
                        new GradientColorKey(Color.white, 1f)
                    };
            }
        }

        private static bool IsLegacyDefaultVolumeGradient(Gradient gradient)
        {
            if (gradient == null)
            {
                return true;
            }

            var colorKeys = gradient.colorKeys;
            if (colorKeys == null || colorKeys.Length == 0)
            {
                return true;
            }

            if (colorKeys.Length != 2)
            {
                return false;
            }

            return
                Mathf.Approximately(colorKeys[0].time, 0f) &&
                Mathf.Approximately(colorKeys[1].time, 1f) &&
                Approximately(colorKeys[0].color, Color.black) &&
                Approximately(colorKeys[1].color, Color.white);
        }

        private static bool Approximately(Color lhs, Color rhs, float tolerance = 0.02f)
        {
            return
                Mathf.Abs(lhs.r - rhs.r) <= tolerance &&
                Mathf.Abs(lhs.g - rhs.g) <= tolerance &&
                Mathf.Abs(lhs.b - rhs.b) <= tolerance &&
                Mathf.Abs(lhs.a - rhs.a) <= tolerance;
        }

        private static bool Approximately(float lhs, float rhs, float tolerance = 1e-4f)
        {
            return Mathf.Abs(lhs - rhs) <= tolerance;
        }

        private void EnsureDefaultSliceGradient()
        {
            if (sliceColorGradient == null)
            {
                sliceColorGradient = new Gradient();
            }

            var colorKeys = sliceColorGradient.colorKeys;
            var alphaKeys = sliceColorGradient.alphaKeys;

            if (colorKeys == null || colorKeys.Length == 0 || alphaKeys == null || alphaKeys.Length == 0)
            {
                sliceColorGradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(Color.black, 0f),
                        new GradientColorKey(Color.white, 1f)
                    },
                    new[]
                    {
                        new GradientAlphaKey(0f, 0f),
                        new GradientAlphaKey(1f, 1f)
                    });
            }
        }

        private void EnsureDefault2DBoxes()
        {
            if (transferFunction2DBoxes != null && transferFunction2DBoxes.Count > 0)
            {
                return;
            }

            transferFunction2DBoxes = new List<TransferFunction2DBox>
            {
                new TransferFunction2DBox
                {
                    densityMin = 0.05f,
                    densityMax = 0.65f,
                    gradientMin = 0.04f,
                    gradientMax = 0.92f,
                    color = Color.white,
                    alpha = 0.35f,
                    edgeSoftness = 0.65f
                },
                new TransferFunction2DBox
                {
                    densityMin = 0.55f,
                    densityMax = 0.98f,
                    gradientMin = 0.12f,
                    gradientMax = 1.0f,
                    color = new Color(1.0f, 0.92f, 0.82f, 1.0f),
                    alpha = 0.6f,
                    edgeSoftness = 0.5f
                }
            };
        }

        private void EnsureDefaultSlice2DBoxes()
        {
            if (sliceTransferFunction2DBoxes != null && sliceTransferFunction2DBoxes.Count > 0)
            {
                return;
            }

            sliceTransferFunction2DBoxes = new List<TransferFunction2DBox>
            {
                new TransferFunction2DBox
                {
                    densityMin = 0.05f,
                    densityMax = 0.65f,
                    gradientMin = 0.04f,
                    gradientMax = 0.92f,
                    color = Color.white,
                    alpha = 0.35f,
                    edgeSoftness = 0.65f
                },
                new TransferFunction2DBox
                {
                    densityMin = 0.55f,
                    densityMax = 0.98f,
                    gradientMin = 0.12f,
                    gradientMax = 1.0f,
                    color = new Color(1.0f, 0.92f, 0.82f, 1.0f),
                    alpha = 0.6f,
                    edgeSoftness = 0.5f
                }
            };
        }

        private void EnsureCubeMesh()
        {
            var meshFilter = GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                return;
            }

            var temporary = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var cubeMesh = temporary.GetComponent<MeshFilter>().sharedMesh;
            meshFilter.sharedMesh = cubeMesh;

            var collider = temporary.GetComponent<Collider>();
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

            if (Application.isPlaying)
            {
                Destroy(temporary);
            }
            else
            {
                DestroyImmediate(temporary);
            }
        }

        private void CleanupOwnedResources()
        {
            DestroyTexture(ref _volumeTexture);
            DestroyTexture(ref _segmentationTexture);
            DestroyTexture(ref _segmentationColorMapTexture);
            DestroyTexture(ref _transferTexture1D);
            DestroyTexture(ref _transferTexture2D);
            DestroyTexture(ref _sliceTransferTexture1D);
            DestroyTexture(ref _sliceTransferTexture2D);
            DestroyMaterial();
        }

        private void DestroyMaterial()
        {
            if (_runtimeMaterial == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(_runtimeMaterial);
            }
            else
            {
                DestroyImmediate(_runtimeMaterial);
            }

            _runtimeMaterial = null;
        }

        private static void DestroyTexture<T>(ref T texture) where T : Texture
        {
            if (texture == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(texture);
            }
            else
            {
                DestroyImmediate(texture);
            }

            texture = null;
        }

        private static Vector3 Clamp01Vector(Vector3 value)
        {
            return new Vector3(
                Mathf.Clamp01(value.x),
                Mathf.Clamp01(value.y),
                Mathf.Clamp01(value.z));
        }

        private static bool Approximately(Vector3 a, Vector3 b)
        {
            return
                Mathf.Abs(a.x - b.x) < 1e-5f &&
                Mathf.Abs(a.y - b.y) < 1e-5f &&
                Mathf.Abs(a.z - b.z) < 1e-5f;
        }

        private void GetBoxBounds01(out Vector3 boxMin, out Vector3 boxMax)
        {
            var clampedCenter = Clamp01Vector(boxCenter01);
            var clampedSize = new Vector3(
                Mathf.Clamp(boxSize01.x, 0.01f, 1.0f),
                Mathf.Clamp(boxSize01.y, 0.01f, 1.0f),
                Mathf.Clamp(boxSize01.z, 0.01f, 1.0f));

            var halfSize = 0.5f * clampedSize;
            boxMin = Clamp01Vector(clampedCenter - halfSize);
            boxMax = Clamp01Vector(clampedCenter + halfSize);
        }
    }
}

#if UNITY_EDITOR
namespace SimpleITKUnity.Editor
{
    [CustomEditor(typeof(SimpleITKVolumeRenderer))]
    public sealed class SimpleITKVolumeRendererEditor : UnityEditor.Editor
    {
        private SerializedProperty _volumePresetNameProperty;
        private SerializedProperty _slicePresetNameProperty;
        private SerializedProperty _applyVolumePresetEffectiveRangeProperty;

        private void OnEnable()
        {
            _volumePresetNameProperty = serializedObject.FindProperty("volumeTransferFunctionPresetName");
            _slicePresetNameProperty = serializedObject.FindProperty("sliceTransferFunctionPresetName");
            _applyVolumePresetEffectiveRangeProperty =
                serializedObject.FindProperty("applyVolumePresetEffectiveRangeToWindow");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawVisibleProperties();

            var renderer = (SimpleITKVolumeRenderer)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("VolView Transfer Function Presets", EditorStyles.boldLabel);
            DrawPresetPopup(renderer, _volumePresetNameProperty, "Volume Preset");
            EditorGUILayout.PropertyField(
                _applyVolumePresetEffectiveRangeProperty,
                new GUIContent("Apply Volume Preset Effective Range"));
            DrawPresetPopup(renderer, _slicePresetNameProperty, "Slice Preset");

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawVisibleProperties()
        {
            var syncAbcTo3DProperty = serializedObject.FindProperty("syncAbcIntensityWindowToVolume");
            var syncAbcTo3D = syncAbcTo3DProperty != null && syncAbcTo3DProperty.boolValue;
            var property = serializedObject.GetIterator();
            var enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;

                if (property.propertyPath == "m_Script")
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.PropertyField(property, includeChildren: true);
                    }

                    continue;
                }

                if (TryGetDisplayNameOverride(property.name, out var displayName))
                {
                    var disableEditing =
                        syncAbcTo3D &&
                        (property.name == "sliceIntensityWindowMin" || property.name == "sliceIntensityWindowMax");
                    using (new EditorGUI.DisabledScope(disableEditing))
                    {
                        EditorGUILayout.PropertyField(property, new GUIContent(displayName), includeChildren: true);
                    }
                    continue;
                }

                var disableDefaultEditing =
                    syncAbcTo3D &&
                    (property.name == "sliceIntensityWindowMin" || property.name == "sliceIntensityWindowMax");
                using (new EditorGUI.DisabledScope(disableDefaultEditing))
                {
                    EditorGUILayout.PropertyField(property, includeChildren: true);
                }
            }
        }

        private static bool TryGetDisplayNameOverride(string propertyName, out string displayName)
        {
            switch (propertyName)
            {
                case "intensityWindowMin":
                    displayName = "3D Intensity Window Min";
                    return true;
                case "intensityWindowMax":
                    displayName = "3D Intensity Window Max";
                    return true;
                case "syncAbcIntensityWindowToVolume":
                    displayName = "Sync ABC To 3D";
                    return true;
                case "sliceIntensityWindowMin":
                    displayName = "ABC Intensity Window Min";
                    return true;
                case "sliceIntensityWindowMax":
                    displayName = "ABC Intensity Window Max";
                    return true;
                default:
                    displayName = string.Empty;
                    return false;
            }
        }

        private static void DrawPresetPopup(
            SimpleITKVolumeRenderer renderer,
            SerializedProperty property,
            string label)
        {
            var presetNames = renderer.GetAvailableTransferFunctionPresetNames();
            if (presetNames == null || presetNames.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No transfer-function presets were found. " +
                    "Keep Assets/Resources/MedicalColorPresets.json in the project.",
                    MessageType.Warning);
                EditorGUILayout.PropertyField(property, new GUIContent(label));
                return;
            }

            var values = new string[presetNames.Length + 1];
            var displayOptions = new GUIContent[values.Length];
            values[0] = string.Empty;
            displayOptions[0] = new GUIContent("Custom");

            for (var i = 0; i < presetNames.Length; ++i)
            {
                var presetName = presetNames[i];
                values[i + 1] = presetName;
                var group = SimpleITKTransferFunctionPresetLibrary.GetPresetGroup(presetName);
                displayOptions[i + 1] = new GUIContent($"[{group}] {presetName}");
            }

            var currentIndex = Array.IndexOf(values, property.stringValue ?? string.Empty);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            var nextIndex = EditorGUILayout.Popup(new GUIContent(label), currentIndex, displayOptions);
            if (nextIndex >= 0 && nextIndex < values.Length)
            {
                property.stringValue = values[nextIndex];
            }
        }
    }
}
#endif
