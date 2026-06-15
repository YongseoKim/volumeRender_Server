using System;
using System.Diagnostics;
using System.IO;
using Unity.InferenceEngine;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SimpleITKUnity
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class BratsSentisInference : MonoBehaviour
    {
        public enum BackendPreference
        {
            CPU = 1,
            GPUCompute = 2,
            GPUPixel = 3
        }

        private const int InputChannels = 4;
        private const int OutputChannels = 3;
        private const int RoiWidth = 240;
        private const int RoiHeight = 240;
        private const int RoiDepth = 160;
        private static readonly string[] BratsModalitySuffixes = { "_t1ce", "_flair", "_t1", "_t2" };

        [Header("Model")]
        [SerializeField] private ModelAsset modelAsset;
        [SerializeField] private BackendPreference backendPreference = BackendPreference.GPUCompute;

        [Header("Thresholds")]
        [SerializeField, Range(0f, 1f)] private float tumorCoreThreshold = 0.5f;
        [SerializeField, Range(0f, 1f)] private float wholeTumorThreshold = 0.5f;
        [SerializeField, Range(0f, 1f)] private float enhancingTumorThreshold = 0.5f;

        [SerializeField, HideInInspector] private string lastRunSummary = string.Empty;

        private Model _runtimeModel;
        private Worker _worker;
        private BackendType _activeBackend;
        private ModelAsset _cachedModelAsset;
        private BackendPreference _cachedBackendPreference;

        private readonly struct SpatialPadding
        {
            public readonly int PadBeforeX;
            public readonly int PadAfterX;
            public readonly int PadBeforeY;
            public readonly int PadAfterY;
            public readonly int PadBeforeZ;
            public readonly int PadAfterZ;

            public SpatialPadding(int width, int height, int depth)
            {
                PadBeforeX = ComputePadBefore(width, RoiWidth);
                PadAfterX = RoiWidth - width - PadBeforeX;
                PadBeforeY = ComputePadBefore(height, RoiHeight);
                PadAfterY = RoiHeight - height - PadBeforeY;
                PadBeforeZ = ComputePadBefore(depth, RoiDepth);
                PadAfterZ = RoiDepth - depth - PadBeforeZ;
            }

            private static int ComputePadBefore(int inputSize, int roiSize)
            {
                return Mathf.Max(roiSize - inputSize, 0) / 2;
            }
        }

        private readonly struct BratsModalityPaths
        {
            public BratsModalityPaths(string caseId, string flairPath, string t1Path, string t1cePath, string t2Path)
            {
                CaseId = caseId;
                FlairPath = flairPath;
                T1Path = t1Path;
                T1cePath = t1cePath;
                T2Path = t2Path;
            }

            public string CaseId { get; }
            public string FlairPath { get; }
            public string T1Path { get; }
            public string T1cePath { get; }
            public string T2Path { get; }
        }

        public bool TryRun(
            SimpleITKScalarVolumeData t1ce,
            SimpleITKScalarVolumeData t1,
            SimpleITKScalarVolumeData t2,
            SimpleITKScalarVolumeData flair,
            out SimpleITKLabelVolumeData segmentationData,
            out string error)
        {
            return TryRunInternal(t1ce, t1, t2, flair, backendPreference, out segmentationData, out error);
        }

        public bool TryRunWithBackend(
            SimpleITKScalarVolumeData t1ce,
            SimpleITKScalarVolumeData t1,
            SimpleITKScalarVolumeData t2,
            SimpleITKScalarVolumeData flair,
            BackendPreference backendOverride,
            out SimpleITKLabelVolumeData segmentationData,
            out string error)
        {
            return TryRunInternal(t1ce, t1, t2, flair, backendOverride, out segmentationData, out error);
        }

        public void ReleaseResources()
        {
            DisposeWorker();
        }

        public bool TryPrepareSegmentationForVolume(
            string primaryVolumePath,
            out SimpleITKLabelVolumeData segmentationData,
            out string summary,
            out string error,
            bool forceCpuBackend = false,
            Action<float, string> progress = null)
        {
            segmentationData = default;
            summary = string.Empty;
            error = string.Empty;

            progress?.Invoke(0.08f, "Resolving BraTS modality files...");
            if (!TryResolveBratsModalityPathsByConvention(primaryVolumePath, out var modalities, out error))
            {
                return false;
            }

            if (!ReportAndTryReadBratsVolume(progress, 0.16f, modalities.T1cePath, "t1ce", out var t1ce, out error) ||
                !ReportAndTryReadBratsVolume(progress, 0.24f, modalities.T1Path, "t1", out var t1, out error) ||
                !ReportAndTryReadBratsVolume(progress, 0.32f, modalities.T2Path, "t2", out var t2, out error) ||
                !ReportAndTryReadBratsVolume(progress, 0.4f, modalities.FlairPath, "flair", out var flair, out error))
            {
                return false;
            }

            progress?.Invoke(0.48f, "Validating BraTS input sizes...");
            if (!TryValidateMatchingVolumeSizes(t1ce, t1, t2, flair, out error))
            {
                return false;
            }

            progress?.Invoke(0.54f, "Preparing Sentis runner...");
            progress?.Invoke(0.62f, forceCpuBackend ? "Running Sentis inference (CPU)..." : "Running Sentis inference...");

            var inferenceSucceeded = forceCpuBackend
                ? TryRunWithBackend(
                    t1ce,
                    t1,
                    t2,
                    flair,
                    BackendPreference.CPU,
                    out segmentationData,
                    out error)
                : TryRun(t1ce, t1, t2, flair, out segmentationData, out error);

            if (!inferenceSucceeded)
            {
                return false;
            }

            progress?.Invoke(0.76f, "Validating segmentation output...");
            if (!TryValidateGeneratedSegmentation(segmentationData, out error))
            {
                return false;
            }

            summary =
                $"{modalities.CaseId}: ({segmentationData.Width}, {segmentationData.Height}, {segmentationData.Depth}), " +
                $"backend={(forceCpuBackend ? "CPU" : backendPreference.ToString())}, " +
                $"labels<= {segmentationData.LabelMaximum}";
            return true;
        }

        private bool TryRunInternal(
            SimpleITKScalarVolumeData t1ce,
            SimpleITKScalarVolumeData t1,
            SimpleITKScalarVolumeData t2,
            SimpleITKScalarVolumeData flair,
            BackendPreference requestedBackend,
            out SimpleITKLabelVolumeData segmentationData,
            out string error)
        {
            segmentationData = default;
            error = string.Empty;

            if (!TryValidateInputDimensions(t1ce, t1, t2, flair, out error))
            {
                return false;
            }

            if (t1ce.Width > RoiWidth || t1ce.Height > RoiHeight || t1ce.Depth > RoiDepth)
            {
                error =
                    $"BraTS Sentis runner currently supports inputs up to ({RoiWidth}, {RoiHeight}, {RoiDepth}), " +
                    $"received ({t1ce.Width}, {t1ce.Height}, {t1ce.Depth}).";
                return false;
            }

            if (!EnsureWorker(requestedBackend, out error))
            {
                return false;
            }

            var padding = new SpatialPadding(t1ce.Width, t1ce.Height, t1ce.Depth);
            var inputShape = new TensorShape(1, InputChannels, RoiWidth, RoiHeight, RoiDepth);
            var inputData = new float[inputShape.length];
            var channelStride = RoiWidth * RoiHeight * RoiDepth;

            CopyNormalizedChannelToModelInput(t1ce, inputData, 0 * channelStride, padding);
            CopyNormalizedChannelToModelInput(t1, inputData, 1 * channelStride, padding);
            CopyNormalizedChannelToModelInput(t2, inputData, 2 * channelStride, padding);
            CopyNormalizedChannelToModelInput(flair, inputData, 3 * channelStride, padding);

            try
            {
                using var inputTensor = new Tensor<float>(inputShape, inputData);
                var stopwatch = Stopwatch.StartNew();
                _worker.Schedule(inputTensor);
                var outputTensor = _worker.PeekOutput() as Tensor<float>;
                if (outputTensor == null)
                {
                    error = "BraTS Sentis worker did not return a float output tensor.";
                    return false;
                }

                using var cpuOutput = outputTensor.ReadbackAndClone() as Tensor<float>;
                stopwatch.Stop();

                if (cpuOutput == null)
                {
                    error = "BraTS Sentis output readback failed.";
                    return false;
                }

                if (cpuOutput.shape.rank != 5 ||
                    cpuOutput.shape[0] != 1 ||
                    cpuOutput.shape[1] != OutputChannels ||
                    cpuOutput.shape[2] != RoiWidth ||
                    cpuOutput.shape[3] != RoiHeight ||
                    cpuOutput.shape[4] != RoiDepth)
                {
                    error = $"Unexpected BraTS output tensor shape: {cpuOutput.shape}.";
                    return false;
                }

                var labelBuffer = new byte[t1ce.Width * t1ce.Height * t1ce.Depth];
                byte labelMaximum = 0;

                for (var z = 0; z < t1ce.Depth; ++z)
                {
                    for (var y = 0; y < t1ce.Height; ++y)
                    {
                        for (var x = 0; x < t1ce.Width; ++x)
                        {
                            var tensorX = x + padding.PadBeforeX;
                            var tensorY = y + padding.PadBeforeY;
                            var tensorZ = z + padding.PadBeforeZ;
                            var tumorCore = Sigmoid(cpuOutput[0, 0, tensorX, tensorY, tensorZ]);
                            var wholeTumor = Sigmoid(cpuOutput[0, 1, tensorX, tensorY, tensorZ]);
                            var enhancingTumor = Sigmoid(cpuOutput[0, 2, tensorX, tensorY, tensorZ]);

                            var label = enhancingTumor > enhancingTumorThreshold
                                ? (byte)4
                                : tumorCore > tumorCoreThreshold
                                    ? (byte)1
                                    : wholeTumor > wholeTumorThreshold
                                        ? (byte)2
                                        : (byte)0;

                            var unityIndex = ((z * t1ce.Height) + y) * t1ce.Width + x;
                            labelBuffer[unityIndex] = label;
                            if (label > labelMaximum)
                            {
                                labelMaximum = label;
                            }
                        }
                    }
                }

                segmentationData = new SimpleITKLabelVolumeData
                {
                    Width = t1ce.Width,
                    Height = t1ce.Height,
                    Depth = t1ce.Depth,
                    Voxels = labelBuffer,
                    ImageInfo = flair.ImageInfo,
                    LabelMaximum = labelMaximum
                };

                lastRunSummary =
                    $"backend={_activeBackend}, requested={requestedBackend}, input=({t1ce.Width}, {t1ce.Height}, {t1ce.Depth}), " +
                    $"pad=[x:{padding.PadBeforeX}/{padding.PadAfterX}, y:{padding.PadBeforeY}/{padding.PadAfterY}, z:{padding.PadBeforeZ}/{padding.PadAfterZ}], " +
                    $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}, labels<={labelMaximum}";

                UnityEngine.Debug.Log($"BratsSentisInference ({name}): {lastRunSummary}");

                return true;
            }
            catch (Exception ex)
            {
                error = $"BraTS Sentis inference failed: {ex.Message}";
                return false;
            }
        }

        private void Reset()
        {
            TryAssignDefaultModelAsset();
            backendPreference = BackendPreference.GPUCompute;
        }

        private void OnValidate()
        {
            TryAssignDefaultModelAsset();
            if (!Enum.IsDefined(typeof(BackendPreference), backendPreference))
            {
                backendPreference = BackendPreference.GPUCompute;
            }

            tumorCoreThreshold = Mathf.Clamp01(tumorCoreThreshold);
            wholeTumorThreshold = Mathf.Clamp01(wholeTumorThreshold);
            enhancingTumorThreshold = Mathf.Clamp01(enhancingTumorThreshold);
            DisposeWorker();
        }

        private void OnDisable()
        {
            DisposeWorker();
        }

        private void OnDestroy()
        {
            DisposeWorker();
        }

        private bool EnsureWorker(BackendPreference requestedBackend, out string error)
        {
            error = string.Empty;

            if (modelAsset == null)
            {
                error = "BratsSentisInference requires a ModelAsset reference.";
                return false;
            }

            if (_worker != null && _cachedModelAsset == modelAsset && _cachedBackendPreference == requestedBackend)
            {
                return true;
            }

            DisposeWorker();

            if (!TryResolveBackendType(requestedBackend, out _activeBackend, out error))
            {
                return false;
            }

            _runtimeModel = ModelLoader.Load(modelAsset);
            if (_runtimeModel == null)
            {
                error = $"Failed to load runtime model from asset '{modelAsset.name}'.";
                return false;
            }

            _worker = new Worker(_runtimeModel, _activeBackend);
            _cachedModelAsset = modelAsset;
            _cachedBackendPreference = requestedBackend;
            return true;
        }

        private void DisposeWorker()
        {
            _worker?.Dispose();
            _worker = null;
            _runtimeModel = null;
            _cachedModelAsset = null;
            _cachedBackendPreference = BackendPreference.GPUCompute;
        }

        private void TryAssignDefaultModelAsset()
        {
            if (modelAsset != null)
            {
                return;
            }

            modelAsset = Resources.Load<ModelAsset>("brats_mri_segmentation");
            if (modelAsset != null)
            {
                return;
            }

#if UNITY_EDITOR
            modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Models/brats_mri_segmentation.onnx");
#endif
        }

        private static bool TryResolveBratsModalityPathsByConvention(
            string imagePath,
            out BratsModalityPaths modalityPaths,
            out string error)
        {
            modalityPaths = default;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "imagePath must not be empty.";
                return false;
            }

            if (!File.Exists(imagePath))
            {
                error = $"volume path does not exist: {imagePath}";
                return false;
            }

            var fullPath = Path.GetFullPath(imagePath);
            var directoryPath = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                error = $"Could not determine parent directory for volume path: {imagePath}";
                return false;
            }

            var extension = SimpleITKPathUtility.GetCompoundExtension(fullPath);
            var stem = SimpleITKPathUtility.GetFileStem(fullPath);
            string casePrefix = null;
            for (var i = 0; i < BratsModalitySuffixes.Length; ++i)
            {
                var suffix = BratsModalitySuffixes[i];
                if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    casePrefix = stem.Substring(0, stem.Length - suffix.Length);
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(casePrefix))
            {
                error =
                    "BraTS segmentation expects the selected volume to follow '<case>_flair.nrrd' or '<case>_flair.nii.gz'.";
                return false;
            }

            var flairPath = Path.Combine(directoryPath, $"{casePrefix}_flair{extension}");
            var t1Path = Path.Combine(directoryPath, $"{casePrefix}_t1{extension}");
            var t1cePath = Path.Combine(directoryPath, $"{casePrefix}_t1ce{extension}");
            var t2Path = Path.Combine(directoryPath, $"{casePrefix}_t2{extension}");

            var missing = string.Empty;
            if (!File.Exists(flairPath)) missing += $" flair={flairPath}";
            if (!File.Exists(t1Path)) missing += $" t1={t1Path}";
            if (!File.Exists(t1cePath)) missing += $" t1ce={t1cePath}";
            if (!File.Exists(t2Path)) missing += $" t2={t2Path}";
            if (!string.IsNullOrWhiteSpace(missing))
            {
                error = $"BraTS modality files are incomplete.{missing}";
                return false;
            }

            modalityPaths = new BratsModalityPaths(casePrefix, flairPath, t1Path, t1cePath, t2Path);
            return true;
        }

        private static bool ReportAndTryReadBratsVolume(
            Action<float, string> progress,
            float progressValue,
            string imagePath,
            string modalityName,
            out SimpleITKScalarVolumeData volumeData,
            out string error)
        {
            progress?.Invoke(progressValue, $"Reading {modalityName}: {Path.GetFileName(imagePath)}");
            return TryReadBratsVolume(imagePath, modalityName, out volumeData, out error);
        }

        private static bool TryReadBratsVolume(
            string imagePath,
            string modalityName,
            out SimpleITKScalarVolumeData volumeData,
            out string error)
        {
            if (!SimpleITKNative.TryReadVolumeFloat32Raw(imagePath, out volumeData, out error))
            {
                error = $"Failed to read BraTS {modalityName} volume '{imagePath}': {error}";
                return false;
            }

            return true;
        }

        private static bool TryValidateMatchingVolumeSizes(
            SimpleITKScalarVolumeData t1ce,
            SimpleITKScalarVolumeData t1,
            SimpleITKScalarVolumeData t2,
            SimpleITKScalarVolumeData flair,
            out string error)
        {
            error = string.Empty;
            if (t1ce.Width != t1.Width || t1ce.Height != t1.Height || t1ce.Depth != t1.Depth ||
                t1ce.Width != t2.Width || t1ce.Height != t2.Height || t1ce.Depth != t2.Depth ||
                t1ce.Width != flair.Width || t1ce.Height != flair.Height || t1ce.Depth != flair.Depth)
            {
                error =
                    "BraTS modalities must all have identical dimensions. " +
                    $"t1ce=({t1ce.Width}, {t1ce.Height}, {t1ce.Depth}), " +
                    $"t1=({t1.Width}, {t1.Height}, {t1.Depth}), " +
                    $"t2=({t2.Width}, {t2.Height}, {t2.Depth}), " +
                    $"flair=({flair.Width}, {flair.Height}, {flair.Depth}).";
                return false;
            }

            return true;
        }

        private static bool TryValidateGeneratedSegmentation(SimpleITKLabelVolumeData segmentationData, out string error)
        {
            error = string.Empty;

            if (segmentationData.Voxels == null || segmentationData.Voxels.Length == 0)
            {
                error = "Generated segmentation is empty.";
                return false;
            }

            var nonZeroCount = 0;
            for (var i = 0; i < segmentationData.Voxels.Length; ++i)
            {
                if (segmentationData.Voxels[i] != 0)
                {
                    nonZeroCount++;
                }
            }

            var nonZeroFraction = (float)nonZeroCount / segmentationData.Voxels.Length;
            if (nonZeroFraction > 0.35f)
            {
                error =
                    $"Generated segmentation looks invalid for BraTS tumor labels: non-zero fraction={nonZeroFraction:F3}. " +
                    "This usually indicates a backend or tensor-layout issue.";
                return false;
            }

            return true;
        }

        private static bool TryResolveBackendType(
            BackendPreference preference,
            out BackendType backendType,
            out string error)
        {
            error = string.Empty;
            backendType = BackendType.CPU;

            switch (preference)
            {
                case BackendPreference.CPU:
                    backendType = BackendType.CPU;
                    return true;
                case BackendPreference.GPUCompute:
                    if (!SystemInfo.supportsComputeShaders)
                    {
                        error = "GPUCompute backend was selected, but this platform does not support compute shaders.";
                        return false;
                    }
                    backendType = BackendType.GPUCompute;
                    return true;
                case BackendPreference.GPUPixel:
                    backendType = BackendType.GPUPixel;
                    return true;
                default:
                    error = $"Unsupported Sentis backend preference: {preference}.";
                    return false;
            }
        }

        private static bool TryValidateInputDimensions(
            SimpleITKScalarVolumeData t1ce,
            SimpleITKScalarVolumeData t1,
            SimpleITKScalarVolumeData t2,
            SimpleITKScalarVolumeData flair,
            out string error)
        {
            error = string.Empty;

            if (!HasMatchingDimensions(t1ce, t1) || !HasMatchingDimensions(t1ce, t2) || !HasMatchingDimensions(t1ce, flair))
            {
                error =
                    "BraTS modalities must share the same dimensions. " +
                    $"t1ce=({t1ce.Width}, {t1ce.Height}, {t1ce.Depth}), " +
                    $"t1=({t1.Width}, {t1.Height}, {t1.Depth}), " +
                    $"t2=({t2.Width}, {t2.Height}, {t2.Depth}), " +
                    $"flair=({flair.Width}, {flair.Height}, {flair.Depth}).";
                return false;
            }

            return true;
        }

        private static bool HasMatchingDimensions(SimpleITKScalarVolumeData left, SimpleITKScalarVolumeData right)
        {
            return left.Width == right.Width && left.Height == right.Height && left.Depth == right.Depth;
        }

        private static void CopyNormalizedChannelToModelInput(
            SimpleITKScalarVolumeData volumeData,
            float[] destination,
            int destinationBaseOffset,
            SpatialPadding padding)
        {
            var voxels = volumeData.Voxels;
            var hasNonZero = TryComputeNonZeroStatistics(voxels, out var mean, out var stdDev);
            var normalizeNonZero = hasNonZero && stdDev > 0.0;

            for (var z = 0; z < volumeData.Depth; ++z)
            {
                for (var y = 0; y < volumeData.Height; ++y)
                {
                    for (var x = 0; x < volumeData.Width; ++x)
                    {
                        var sourceIndex = ((z * volumeData.Height) + y) * volumeData.Width + x;
                        var value = voxels[sourceIndex];
                        if (hasNonZero)
                        {
                            if (value != 0f)
                            {
                                value = normalizeNonZero ? (float)((value - mean) / stdDev) : (float)(value - mean);
                            }
                        }
                        else
                        {
                            value = 0f;
                        }

                        var destinationX = x + padding.PadBeforeX;
                        var destinationY = y + padding.PadBeforeY;
                        var destinationZ = z + padding.PadBeforeZ;
                        var destinationIndex = destinationBaseOffset + ((destinationX * RoiHeight) + destinationY) * RoiDepth + destinationZ;
                        destination[destinationIndex] = value;
                    }
                }
            }
        }

        private static bool TryComputeNonZeroStatistics(float[] voxels, out double mean, out double stdDev)
        {
            mean = 0.0;
            stdDev = 0.0;

            if (voxels == null || voxels.Length == 0)
            {
                return false;
            }

            double sum = 0.0;
            double sumSquares = 0.0;
            long count = 0;

            for (var i = 0; i < voxels.Length; ++i)
            {
                var value = voxels[i];
                if (Math.Abs(value) <= float.Epsilon)
                {
                    continue;
                }

                sum += value;
                sumSquares += value * value;
                count++;
            }

            if (count == 0)
            {
                return false;
            }

            mean = sum / count;
            var variance = Math.Max((sumSquares / count) - (mean * mean), 0.0);
            stdDev = Math.Sqrt(variance);
            return true;
        }

        private static float Sigmoid(float value)
        {
            return 1.0f / (1.0f + Mathf.Exp(-value));
        }
    }
}
