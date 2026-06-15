using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SimpleITKUnity
{
    [ExecuteAlways]
    [RequireComponent(typeof(MeshRenderer))]
    public class SimpleITKSliceRenderer : MonoBehaviour
    {
        [SerializeField] private SimpleITKVolumeRenderer target;
        [SerializeField] private Shader sliceShader;
        [SerializeField] private bool useTransferFunction2D;

        [Header("Segmentation Overlay")]
        [SerializeField] private bool showSegmentationOverlay = true;

        private MeshRenderer _meshRenderer;
        private Material _runtimeMaterial;
        private bool _materialDirty = true;
        private int _cachedVolumeTextureId;
        private int _cachedTransferTexture1DId;
        private int _cachedTransferTexture2DId;
        private int _cachedSegmentationTextureId;
        private int _cachedSegmentationColorMapId;
        private int _cachedSliceMaterialVersion = -1;
        private Matrix4x4 _cachedWorldToVolume;
        private Vector4 _cachedInvVolumeSize;
        private float _cachedClipMin = float.NaN;
        private float _cachedClipMax = float.NaN;
        private float _cachedGradientScale = float.NaN;
        private float _cachedUseTf2D = float.NaN;
        private float _cachedEnableRoi = float.NaN;
        private Vector4 _cachedRoiMin;
        private Vector4 _cachedRoiMax;
        private float _cachedRoiKeepInside = float.NaN;
        private float _cachedUseSegmentationOverlay = float.NaN;
        private float _cachedSegmentationOpacity = float.NaN;
        private bool _cachedMeshEnabled;
#if UNITY_EDITOR
        private bool _editorApplyQueued;
#endif

        private const string ShaderName = "SimpleITK/SliceRenderer";

        public void SetTarget(SimpleITKVolumeRenderer targetRenderer)
        {
            if (target == targetRenderer)
            {
                return;
            }

            target = targetRenderer;
            _materialDirty = true;
        }

        public void SetUseTransferFunction2D(bool enabled)
        {
            if (useTransferFunction2D == enabled)
            {
                return;
            }

            useTransferFunction2D = enabled;
            _materialDirty = true;
        }

        public void SetShaderOverride(Shader shaderOverride)
        {
            if (sliceShader == shaderOverride)
            {
                return;
            }

            sliceShader = shaderOverride;
            _materialDirty = true;
        }

        private void OnEnable()
        {
            _meshRenderer = GetComponent<MeshRenderer>();
            _materialDirty = true;
            ResolveTarget();
            EnsureMaterial();
            ApplyMaterialPropertiesIfDirty();
        }

        private void Update()
        {
            ResolveTarget();
            RefreshMaterialDirtyFromTarget();

            if (!_materialDirty)
            {
                return;
            }

            EnsureMaterial();
            ApplyMaterialPropertiesIfDirty();
        }

        private void OnValidate()
        {
            _materialDirty = true;

            if (Application.isPlaying)
            {
                _meshRenderer = GetComponent<MeshRenderer>();
                ResolveTarget();
                EnsureMaterial();
                ApplyMaterialPropertiesIfDirty();
            }
#if UNITY_EDITOR
            else
            {
                QueueEditorApplyAfterValidate();
            }
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            if (_editorApplyQueued)
            {
                EditorApplication.delayCall -= ApplyEditorAfterValidate;
                _editorApplyQueued = false;
            }
#endif
            DestroyMaterial();
        }

        private void EnsureMaterial()
        {
            if (_meshRenderer == null)
            {
                return;
            }

            var shader = sliceShader != null ? sliceShader : Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError($"SimpleITKSliceRenderer: shader '{ShaderName}' not found.");
                return;
            }

            if (_runtimeMaterial == null || _runtimeMaterial.shader != shader)
            {
                DestroyMaterial();
                _runtimeMaterial = new Material(shader)
                {
                    name = "SimpleITKSliceMaterial(Runtime)",
                    hideFlags = HideFlags.DontSave
                };
                _meshRenderer.sharedMaterial = _runtimeMaterial;
                _materialDirty = true;
            }
        }

        private void ResolveTarget()
        {
            if (target != null)
            {
                return;
            }

            var resolved = SimpleITKRendererLocator.FindInHierarchyOrScene(transform, gameObject.scene);
            if (resolved != null)
            {
                target = resolved;
                _materialDirty = true;
            }
        }

        private void RefreshMaterialDirtyFromTarget()
        {
            if (target == null)
            {
                if (_meshRenderer != null && _meshRenderer.enabled)
                {
                    _meshRenderer.enabled = false;
                    _cachedMeshEnabled = false;
                }

                return;
            }

            var volumeTexture = target.GetVolumeTexture();
            var volumeTextureId = volumeTexture != null ? volumeTexture.GetInstanceID() : 0;
            if (volumeTextureId != _cachedVolumeTextureId)
            {
                _cachedVolumeTextureId = volumeTextureId;
                _materialDirty = true;
            }

            if (target.SliceMaterialVersion != _cachedSliceMaterialVersion)
            {
                _cachedSliceMaterialVersion = target.SliceMaterialVersion;
                _materialDirty = true;
            }

            var tf1D = target.GetSliceTransferTexture1D();
            var tf1DId = tf1D != null ? tf1D.GetInstanceID() : 0;
            if (tf1DId != _cachedTransferTexture1DId)
            {
                _cachedTransferTexture1DId = tf1DId;
                _materialDirty = true;
            }

            var tf2D = target.GetSliceTransferTexture2D();
            var tf2DId = tf2D != null ? tf2D.GetInstanceID() : 0;
            if (tf2DId != _cachedTransferTexture2DId)
            {
                _cachedTransferTexture2DId = tf2DId;
                _materialDirty = true;
            }

            var worldToVolume = target.GetWorldToVolumeMatrix();
            if (worldToVolume != _cachedWorldToVolume)
            {
                _cachedWorldToVolume = worldToVolume;
                _materialDirty = true;
            }

            var invVolumeSize = target.GetInvVolumeSize();
            var invVolumeSizeVector = new Vector4(invVolumeSize.x, invVolumeSize.y, invVolumeSize.z, 0.0f);
            if (invVolumeSizeVector != _cachedInvVolumeSize)
            {
                _cachedInvVolumeSize = invVolumeSizeVector;
                _materialDirty = true;
            }

            var clipMin = target.GetClipMin();
            if (!Mathf.Approximately(clipMin, _cachedClipMin))
            {
                _cachedClipMin = clipMin;
                _materialDirty = true;
            }

            var clipMax = target.GetClipMax();
            if (!Mathf.Approximately(clipMax, _cachedClipMax))
            {
                _cachedClipMax = clipMax;
                _materialDirty = true;
            }

            var gradientScale = target.GetSliceGradientScale();
            if (!Mathf.Approximately(gradientScale, _cachedGradientScale))
            {
                _cachedGradientScale = gradientScale;
                _materialDirty = true;
            }

            var use2D = useTransferFunction2D ||
                        target.GetSliceTransferFunctionMode() == SimpleITKVolumeRenderer.TransferFunctionMode.TwoDimensional;
            var useTf2D = use2D ? 1.0f : 0.0f;
            if (!Mathf.Approximately(useTf2D, _cachedUseTf2D))
            {
                _cachedUseTf2D = useTf2D;
                _materialDirty = true;
            }

            var roiEnabled = target.IsBoxCrossSectionEnabled();
            var enableRoi = roiEnabled ? 1.0f : 0.0f;
            if (!Mathf.Approximately(enableRoi, _cachedEnableRoi))
            {
                _cachedEnableRoi = enableRoi;
                _materialDirty = true;
            }

            var roiMin = target.GetBoxMin01();
            var roiMinVector = new Vector4(roiMin.x, roiMin.y, roiMin.z, 0.0f);
            if (roiMinVector != _cachedRoiMin)
            {
                _cachedRoiMin = roiMinVector;
                _materialDirty = true;
            }

            var roiMax = target.GetBoxMax01();
            var roiMaxVector = new Vector4(roiMax.x, roiMax.y, roiMax.z, 0.0f);
            if (roiMaxVector != _cachedRoiMax)
            {
                _cachedRoiMax = roiMaxVector;
                _materialDirty = true;
            }

            var roiKeepInside = target.IsBoxCrossSectionInverted() ? 1.0f : 0.0f;
            if (!Mathf.Approximately(roiKeepInside, _cachedRoiKeepInside))
            {
                _cachedRoiKeepInside = roiKeepInside;
                _materialDirty = true;
            }

            var segmentationTexture = target.GetSegmentationTexture();
            var segmentationTextureId = segmentationTexture != null ? segmentationTexture.GetInstanceID() : 0;
            if (segmentationTextureId != _cachedSegmentationTextureId)
            {
                _cachedSegmentationTextureId = segmentationTextureId;
                _materialDirty = true;
            }

            var segmentationColorMapTexture = target.GetSegmentationColorMapTexture();
            var segmentationColorMapId = segmentationColorMapTexture != null
                ? segmentationColorMapTexture.GetInstanceID()
                : 0;
            if (segmentationColorMapId != _cachedSegmentationColorMapId)
            {
                _cachedSegmentationColorMapId = segmentationColorMapId;
                _materialDirty = true;
            }

            var overlayEnabled =
                target.GetShowSegmentationOverlayInAbc() &&
                showSegmentationOverlay &&
                segmentationTexture != null;
            var useSegmentationOverlay = overlayEnabled ? 1.0f : 0.0f;
            if (!Mathf.Approximately(useSegmentationOverlay, _cachedUseSegmentationOverlay))
            {
                _cachedUseSegmentationOverlay = useSegmentationOverlay;
                _materialDirty = true;
            }

            var segmentationOpacity = target.GetSegmentationOverlayOpacityInAbc();
            if (!Mathf.Approximately(segmentationOpacity, _cachedSegmentationOpacity))
            {
                _cachedSegmentationOpacity = segmentationOpacity;
                _materialDirty = true;
            }
        }

        private void ApplyMaterialPropertiesIfDirty()
        {
            if (!_materialDirty)
            {
                return;
            }

            ApplyMaterialProperties();
        }

        private void ApplyMaterialProperties()
        {
            if (_meshRenderer == null)
            {
                return;
            }

            if (_runtimeMaterial == null || target == null)
            {
                if (_meshRenderer.enabled)
                {
                    _meshRenderer.enabled = false;
                    _cachedMeshEnabled = false;
                }

                _materialDirty = false;
                return;
            }

            var volumeTexture = target.GetVolumeTexture();
            if (volumeTexture == null)
            {
                if (_meshRenderer.enabled)
                {
                    _meshRenderer.enabled = false;
                    _cachedMeshEnabled = false;
                }

                _materialDirty = false;
                return;
            }

            if (!_meshRenderer.enabled)
            {
                _meshRenderer.enabled = true;
            }

            _cachedMeshEnabled = true;

            _runtimeMaterial.SetTexture("_VolumeTex", volumeTexture);
            _cachedVolumeTextureId = volumeTexture.GetInstanceID();

            var tf1D = target.GetSliceTransferTexture1D();
            if (tf1D != null)
            {
                _runtimeMaterial.SetTexture("_TransferTex", tf1D);
                _cachedTransferTexture1DId = tf1D.GetInstanceID();
            }

            var tf2D = target.GetSliceTransferTexture2D();
            if (tf2D != null)
            {
                _runtimeMaterial.SetTexture("_TransferTex2D", tf2D);
                _cachedTransferTexture2DId = tf2D.GetInstanceID();
            }

            _cachedWorldToVolume = target.GetWorldToVolumeMatrix();
            _runtimeMaterial.SetMatrix("_WorldToVolume", _cachedWorldToVolume);

            var invVolumeSize = target.GetInvVolumeSize();
            _cachedInvVolumeSize = new Vector4(invVolumeSize.x, invVolumeSize.y, invVolumeSize.z, 0.0f);
            _runtimeMaterial.SetVector("_InvVolumeSize", _cachedInvVolumeSize);

            _cachedClipMin = target.GetClipMin();
            _cachedClipMax = target.GetClipMax();
            _runtimeMaterial.SetFloat("_ClipMin", _cachedClipMin);
            _runtimeMaterial.SetFloat("_ClipMax", _cachedClipMax);

            _cachedGradientScale = target.GetSliceGradientScale();
            _runtimeMaterial.SetFloat("_GradientScale", _cachedGradientScale);

            var use2D = useTransferFunction2D ||
                        target.GetSliceTransferFunctionMode() == SimpleITKVolumeRenderer.TransferFunctionMode.TwoDimensional;
            _cachedUseTf2D = use2D ? 1.0f : 0.0f;
            _runtimeMaterial.SetFloat("_UseTF2D", _cachedUseTf2D);

            var roiEnabled = target.IsBoxCrossSectionEnabled();
            _cachedEnableRoi = roiEnabled ? 1.0f : 0.0f;
            var roiMin = target.GetBoxMin01();
            _cachedRoiMin = new Vector4(roiMin.x, roiMin.y, roiMin.z, 0.0f);
            var roiMax = target.GetBoxMax01();
            _cachedRoiMax = new Vector4(roiMax.x, roiMax.y, roiMax.z, 0.0f);
            _cachedRoiKeepInside = target.IsBoxCrossSectionInverted() ? 1.0f : 0.0f;
            _runtimeMaterial.SetFloat("_EnableRoi", _cachedEnableRoi);
            _runtimeMaterial.SetVector("_RoiMin", _cachedRoiMin);
            _runtimeMaterial.SetVector("_RoiMax", _cachedRoiMax);
            _runtimeMaterial.SetFloat("_RoiKeepInside", _cachedRoiKeepInside);

            ApplySegmentationOverlayProperties();
            _cachedSliceMaterialVersion = target.SliceMaterialVersion;
            _materialDirty = false;
        }

        private void ApplySegmentationOverlayProperties()
        {
            if (_runtimeMaterial == null)
            {
                return;
            }

            var segmentationTexture = target != null ? target.GetSegmentationTexture() : null;
            var overlayEnabled =
                target != null &&
                target.GetShowSegmentationOverlayInAbc() &&
                showSegmentationOverlay &&
                segmentationTexture != null;
            _cachedUseSegmentationOverlay = overlayEnabled ? 1.0f : 0.0f;
            _cachedSegmentationOpacity = target != null ? target.GetSegmentationOverlayOpacityInAbc() : 0.0f;
            _runtimeMaterial.SetFloat("_UseSegmentationOverlay", _cachedUseSegmentationOverlay);
            _runtimeMaterial.SetFloat("_SegmentationOverlayOpacity", _cachedSegmentationOpacity);

            if (!overlayEnabled)
            {
                _cachedSegmentationTextureId = segmentationTexture != null ? segmentationTexture.GetInstanceID() : 0;
                return;
            }

            var segmentationColorMapTexture = target.GetSegmentationColorMapTexture();
            if (segmentationColorMapTexture != null)
            {
                _runtimeMaterial.SetTexture("_SegmentationColorTex", segmentationColorMapTexture);
                _cachedSegmentationColorMapId = segmentationColorMapTexture.GetInstanceID();
            }

            _runtimeMaterial.SetTexture("_SegmentationTex", segmentationTexture);
            _cachedSegmentationTextureId = segmentationTexture.GetInstanceID();
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

#if UNITY_EDITOR
        private void QueueEditorApplyAfterValidate()
        {
            if (_editorApplyQueued)
            {
                return;
            }

            _editorApplyQueued = true;
            EditorApplication.delayCall += ApplyEditorAfterValidate;
        }

        private void ApplyEditorAfterValidate()
        {
            EditorApplication.delayCall -= ApplyEditorAfterValidate;
            _editorApplyQueued = false;

            if (this == null || !isActiveAndEnabled || Application.isPlaying)
            {
                return;
            }

            _meshRenderer = GetComponent<MeshRenderer>();
            EnsureMaterial();
            ApplyMaterialPropertiesIfDirty();
        }
#endif
    }
}
