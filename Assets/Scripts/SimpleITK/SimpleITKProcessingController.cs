using System;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SimpleITKUnity
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class SimpleITKProcessingController : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private SimpleITKVolumeRenderer targetRenderer;

        [Header("Preprocess")]
        [SerializeField] private bool enableGaussianSmoothing = true;
        [SerializeField, Range(0.1f, 4.0f)] private float gaussianSigma = 0.8f;
        [SerializeField] private bool enableMedianFilter;
        [SerializeField, Range(1, 6)] private int medianRadius = 1;
        [SerializeField] private bool enableBilateralFilter;
        [SerializeField, Range(0.1f, 8.0f)] private float bilateralDomainSigma = 2.0f;
        [SerializeField, Range(0.001f, 1.0f)] private float bilateralRangeSigma = 0.08f;
        [SerializeField, Range(4, 128)] private int bilateralRangeGaussianSamples = 32;

        [Header("Postprocess")]
        [SerializeField] private bool enableAdaptiveHistogramEqualization = true;
        [SerializeField, Range(1, 8)] private int adaptiveHistogramRadius = 2;
        [SerializeField, Range(0.0f, 1.0f)] private float adaptiveHistogramAlpha = 0.3f;
        [SerializeField, Range(0.0f, 1.0f)] private float adaptiveHistogramBeta = 0.3f;

        private void Reset()
        {
            ResolveTarget();
        }

        private void OnEnable()
        {
            ResolveTarget();
        }

        private void OnValidate()
        {
            ResolveTarget();

            gaussianSigma = Mathf.Max(0.1f, gaussianSigma);
            medianRadius = Mathf.Max(1, medianRadius);
            bilateralDomainSigma = Mathf.Max(0.1f, bilateralDomainSigma);
            bilateralRangeSigma = Mathf.Max(0.001f, bilateralRangeSigma);
            bilateralRangeGaussianSamples = Mathf.Clamp(bilateralRangeGaussianSamples, 4, 128);

            adaptiveHistogramRadius = Mathf.Max(1, adaptiveHistogramRadius);
            adaptiveHistogramAlpha = Mathf.Clamp01(adaptiveHistogramAlpha);
            adaptiveHistogramBeta = Mathf.Clamp01(adaptiveHistogramBeta);
        }

        public bool ShouldProcessOnLoad()
        {
            return isActiveAndEnabled && (HasPreprocessStepEnabled() || HasPostprocessStepEnabled());
        }

        public bool TryReadVolumeData(string imagePath, out SimpleITKVolumeData volumeData, out string error)
        {
            var options = BuildProcessingOptions();
            return SimpleITKNative.TryReadVolumeFloat32NormalizedProcessed(imagePath, options, out volumeData, out error);
        }

        public void ReloadVolumeWithProcessing()
        {
            ResolveTarget();
            if (targetRenderer == null)
            {
                Debug.LogError("SimpleITKProcessingController: targetRenderer is not assigned.");
                return;
            }

            if (!targetRenderer.HasVolumeSourceConfigured())
            {
                return;
            }

            if (!targetRenderer.TryResolveVolumePath(out var resolvedPath, out var resolveError))
            {
                Debug.LogError($"SimpleITKProcessingController: {resolveError}");
                return;
            }

            if (!TryReadVolumeData(resolvedPath, out var volumeData, out var readError))
            {
                Debug.LogError($"SimpleITKProcessingController: failed to read processed volume: {readError}");
                return;
            }

            targetRenderer.ApplyVolumeData(volumeData, resolvedPath);
        }

        public void ReloadVolumeWithCurrentState()
        {
            ResolveTarget();
            if (targetRenderer == null)
            {
                Debug.LogError("SimpleITKProcessingController: targetRenderer is not assigned.");
                return;
            }

            if (!targetRenderer.HasVolumeSourceConfigured())
            {
                return;
            }

            targetRenderer.LoadVolume();
        }

        public string BuildPipelineDescription()
        {
            var pre = HasPreprocessStepEnabled()
                ? $"Pre[(Gaussian={enableGaussianSmoothing}, Median={enableMedianFilter}, Bilateral={enableBilateralFilter})]"
                : "Pre[Off]";
            var post = HasPostprocessStepEnabled()
                ? $"Post[(AHE={enableAdaptiveHistogramEqualization})]"
                : "Post[Off]";

            return $"{pre}, {post}";
        }

        public SimpleITKVolumeProcessingOptions BuildProcessingOptions()
        {
            return new SimpleITKVolumeProcessingOptions
            {
                EnablePreGaussianSmoothing = enableGaussianSmoothing,
                PreGaussianSigma = Math.Max(0.1, gaussianSigma),
                EnablePreMedianFilter = enableMedianFilter,
                PreMedianRadius = (uint)Math.Max(1, medianRadius),
                EnablePreBilateralFilter = enableBilateralFilter,
                PreBilateralDomainSigma = Math.Max(0.1, bilateralDomainSigma),
                PreBilateralRangeSigma = Math.Max(0.001, bilateralRangeSigma),
                PreBilateralRangeGaussianSamples = (uint)Math.Clamp(bilateralRangeGaussianSamples, 4, 128),
                EnablePostAdaptiveHistogramEqualization = enableAdaptiveHistogramEqualization,
                PostAdaptiveHistogramRadius = (uint)Math.Max(1, adaptiveHistogramRadius),
                PostAdaptiveHistogramAlpha = Math.Clamp(adaptiveHistogramAlpha, 0.0f, 1.0f),
                PostAdaptiveHistogramBeta = Math.Clamp(adaptiveHistogramBeta, 0.0f, 1.0f)
            };
        }

        private void ResolveTarget()
        {
            if (targetRenderer != null && targetRenderer.GetVolumeTexture() != null)
            {
                return;
            }

            var resolved = SimpleITKRendererLocator.FindInHierarchyOrScene(transform, gameObject.scene);
            if (resolved != null)
            {
                targetRenderer = resolved;
            }
        }

        private bool HasPreprocessStepEnabled()
        {
            return enableGaussianSmoothing || enableMedianFilter || enableBilateralFilter;
        }

        private bool HasPostprocessStepEnabled()
        {
            return enableAdaptiveHistogramEqualization;
        }
    }
}
