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
    public sealed class UnetBrainSentisInference : SegmentationInferenceBehaviour
    {
        public enum BackendPreference
        {
            CPU = 1,
            GPUCompute = 2,
            GPUPixel = 3
        }

        public enum NormalizationMode
        {
            SliceNonZeroZScore = 0,
            VolumeNonZeroZScore = 1
        }

        private const int SliceSize = 128;
        private const byte ForegroundLabel = 1;

        [Header("Model")]
        [SerializeField] private ModelAsset modelAsset;
        [SerializeField] private BackendPreference backendPreference = BackendPreference.GPUCompute;

        [Header("Inference")]
        [SerializeField, Range(0f, 1f)] private float foregroundThreshold = 0.5f;
        [SerializeField] private NormalizationMode normalizationMode = NormalizationMode.SliceNonZeroZScore;

        [SerializeField, HideInInspector] private string lastRunSummary = string.Empty;

        private Model _runtimeModel;
        private Worker _worker;
        private BackendType _activeBackend;
        private ModelAsset _cachedModelAsset;
        private BackendPreference _cachedBackendPreference;

        public override void ReleaseResources()
        {
            DisposeWorker();
        }

        public override bool TryPrepareSegmentationForVolume(
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

            progress?.Invoke(0.08f, "Reading volume for UNet brain segmentation...");
            if (!TryReadVolume(primaryVolumePath, out var volumeData, out error))
            {
                return false;
            }

            progress?.Invoke(0.16f, "Preparing Sentis runner...");
            var requestedBackend = forceCpuBackend ? BackendPreference.CPU : backendPreference;
            if (!TryRun(volumeData, requestedBackend, progress, out segmentationData, out error))
            {
                return false;
            }

            progress?.Invoke(0.9f, "Validating segmentation output...");
            if (!TryValidateGeneratedSegmentation(segmentationData, out error))
            {
                return false;
            }

            var caseId = Path.GetFileName(primaryVolumePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(caseId))
            {
                caseId = "volume";
            }

            summary =
                $"{caseId}: ({segmentationData.Width}, {segmentationData.Height}, {segmentationData.Depth}), " +
                $"backend={(forceCpuBackend ? "CPU" : backendPreference.ToString())}, " +
                $"labels<= {segmentationData.LabelMaximum}";
            return true;
        }

        private bool TryRun(
            SimpleITKScalarVolumeData volumeData,
            BackendPreference requestedBackend,
            Action<float, string> progress,
            out SimpleITKLabelVolumeData segmentationData,
            out string error)
        {
            segmentationData = default;
            error = string.Empty;

            if (volumeData.Width <= 0 || volumeData.Height <= 0 || volumeData.Depth <= 0 || volumeData.Voxels == null)
            {
                error = "UNet brain segmentation received an empty volume.";
                return false;
            }

            if (!EnsureWorker(requestedBackend, out error))
            {
                return false;
            }

            var width = volumeData.Width;
            var height = volumeData.Height;
            var depth = volumeData.Depth;
            var labelBuffer = new byte[width * height * depth];
            var resizedSlice = new float[SliceSize * SliceSize];
            var mask128 = new byte[SliceSize * SliceSize];
            var inputData = new float[SliceSize * SliceSize];
            var inputShape = new TensorShape(1, SliceSize, SliceSize, 1);

            double volumeMean = 0.0;
            double volumeStdDev = 0.0;
            var hasVolumeStats = normalizationMode == NormalizationMode.VolumeNonZeroZScore &&
                                 TryComputeNonZeroStatistics(volumeData.Voxels, out volumeMean, out volumeStdDev);

            var stopwatch = Stopwatch.StartNew();
            byte labelMaximum = 0;

            try
            {
                for (var z = 0; z < depth; ++z)
                {
                    var sliceProgress = 0.2f + (0.68f * (z + 1) / Mathf.Max(1, depth));
                    progress?.Invoke(sliceProgress, $"Running axial UNet inference {z + 1}/{depth}...");

                    CopyAxialSliceResizedBilinear(volumeData, z, resizedSlice);

                    if (normalizationMode == NormalizationMode.VolumeNonZeroZScore)
                    {
                        NormalizeSliceInPlace(resizedSlice, hasVolumeStats, volumeMean, volumeStdDev);
                    }
                    else
                    {
                        var hasSliceStats = TryComputeNonZeroStatistics(resizedSlice, out var sliceMean, out var sliceStdDev);
                        NormalizeSliceInPlace(resizedSlice, hasSliceStats, sliceMean, sliceStdDev);
                    }

                    // NHWC contiguous: index = h * W + w
                    Array.Copy(resizedSlice, inputData, inputData.Length);

                    using var inputTensor = new Tensor<float>(inputShape, inputData);
                    _worker.Schedule(inputTensor);
                    var outputTensor = _worker.PeekOutput() as Tensor<float>;
                    if (outputTensor == null)
                    {
                        error = "UNet brain Sentis worker did not return a float output tensor.";
                        return false;
                    }

                    using var cpuOutput = outputTensor.ReadbackAndClone() as Tensor<float>;
                    if (cpuOutput == null)
                    {
                        error = "UNet brain Sentis output readback failed.";
                        return false;
                    }

                    if (!TryValidateOutputShape(cpuOutput.shape, out error))
                    {
                        return false;
                    }

                    for (var y = 0; y < SliceSize; ++y)
                    {
                        for (var x = 0; x < SliceSize; ++x)
                        {
                            var probability = cpuOutput[0, y, x, 0];
                            mask128[(y * SliceSize) + x] =
                                probability >= foregroundThreshold ? ForegroundLabel : (byte)0;
                        }
                    }

                    WriteMaskNearestToLabelBuffer(mask128, labelBuffer, width, height, z, ref labelMaximum);
                }

                stopwatch.Stop();

                segmentationData = new SimpleITKLabelVolumeData
                {
                    Width = width,
                    Height = height,
                    Depth = depth,
                    Voxels = labelBuffer,
                    ImageInfo = volumeData.ImageInfo,
                    LabelMaximum = labelMaximum
                };

                lastRunSummary =
                    $"backend={_activeBackend}, requested={requestedBackend}, input=({width}, {height}, {depth}), " +
                    $"slice={SliceSize}x{SliceSize}, norm={normalizationMode}, " +
                    $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}, labels<={labelMaximum}";

                UnityEngine.Debug.Log($"UnetBrainSentisInference ({name}): {lastRunSummary}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"UNet brain Sentis inference failed: {ex.Message}";
                return false;
            }
        }

        private void Reset()
        {
            TryAssignDefaultModelAsset();
            backendPreference = BackendPreference.GPUCompute;
            foregroundThreshold = 0.5f;
            normalizationMode = NormalizationMode.SliceNonZeroZScore;
        }

        private void OnValidate()
        {
            TryAssignDefaultModelAsset();
            if (!Enum.IsDefined(typeof(BackendPreference), backendPreference))
            {
                backendPreference = BackendPreference.GPUCompute;
            }

            if (!Enum.IsDefined(typeof(NormalizationMode), normalizationMode))
            {
                normalizationMode = NormalizationMode.SliceNonZeroZScore;
            }

            foregroundThreshold = Mathf.Clamp01(foregroundThreshold);
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
                error = "UnetBrainSentisInference requires a ModelAsset reference.";
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

            modelAsset = Resources.Load<ModelAsset>("unet_brain_segmentation");
            if (modelAsset != null)
            {
                return;
            }

#if UNITY_EDITOR
            modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(
                "Assets/Models/Unet_Brain/unet_brain_segmentation.onnx");
#endif
        }

        private static bool TryReadVolume(
            string imagePath,
            out SimpleITKScalarVolumeData volumeData,
            out string error)
        {
            if (!SimpleITKNative.TryReadVolumeFloat32Raw(imagePath, out volumeData, out error))
            {
                error = $"Failed to read volume '{imagePath}': {error}";
                return false;
            }

            return true;
        }

        private static bool TryValidateOutputShape(TensorShape shape, out string error)
        {
            error = string.Empty;
            if (shape.rank != 4 ||
                shape[0] != 1 ||
                shape[1] != SliceSize ||
                shape[2] != SliceSize ||
                shape[3] != 1)
            {
                error = $"Unexpected UNet brain output tensor shape: {shape}. Expected (1, {SliceSize}, {SliceSize}, 1).";
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
            if (nonZeroFraction > 0.85f)
            {
                error =
                    $"Generated segmentation looks invalid for UNet brain labels: non-zero fraction={nonZeroFraction:F3}. " +
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

        private static void CopyAxialSliceResizedBilinear(
            SimpleITKScalarVolumeData volumeData,
            int z,
            float[] destination128)
        {
            var width = volumeData.Width;
            var height = volumeData.Height;
            var voxels = volumeData.Voxels;
            var sliceOffset = z * width * height;

            for (var dy = 0; dy < SliceSize; ++dy)
            {
                var srcY = ((dy + 0.5f) * height / SliceSize) - 0.5f;
                var y0 = Mathf.Clamp(Mathf.FloorToInt(srcY), 0, height - 1);
                var y1 = Mathf.Min(y0 + 1, height - 1);
                var ty = Mathf.Clamp01(srcY - y0);

                for (var dx = 0; dx < SliceSize; ++dx)
                {
                    var srcX = ((dx + 0.5f) * width / SliceSize) - 0.5f;
                    var x0 = Mathf.Clamp(Mathf.FloorToInt(srcX), 0, width - 1);
                    var x1 = Mathf.Min(x0 + 1, width - 1);
                    var tx = Mathf.Clamp01(srcX - x0);

                    var v00 = voxels[sliceOffset + (y0 * width) + x0];
                    var v10 = voxels[sliceOffset + (y0 * width) + x1];
                    var v01 = voxels[sliceOffset + (y1 * width) + x0];
                    var v11 = voxels[sliceOffset + (y1 * width) + x1];

                    var top = Mathf.Lerp(v00, v10, tx);
                    var bottom = Mathf.Lerp(v01, v11, tx);
                    destination128[(dy * SliceSize) + dx] = Mathf.Lerp(top, bottom, ty);
                }
            }
        }

        private static void WriteMaskNearestToLabelBuffer(
            byte[] mask128,
            byte[] labelBuffer,
            int width,
            int height,
            int z,
            ref byte labelMaximum)
        {
            var sliceOffset = z * width * height;
            for (var y = 0; y < height; ++y)
            {
                var srcY = Mathf.Clamp((y * SliceSize) / height, 0, SliceSize - 1);
                for (var x = 0; x < width; ++x)
                {
                    var srcX = Mathf.Clamp((x * SliceSize) / width, 0, SliceSize - 1);
                    var label = mask128[(srcY * SliceSize) + srcX];
                    labelBuffer[sliceOffset + (y * width) + x] = label;
                    if (label > labelMaximum)
                    {
                        labelMaximum = label;
                    }
                }
            }
        }

        private static void NormalizeSliceInPlace(
            float[] slice,
            bool hasStats,
            double mean,
            double stdDev)
        {
            if (!hasStats)
            {
                for (var i = 0; i < slice.Length; ++i)
                {
                    slice[i] = 0f;
                }

                return;
            }

            var normalize = stdDev > 0.0;
            for (var i = 0; i < slice.Length; ++i)
            {
                var value = slice[i];
                if (Math.Abs(value) <= float.Epsilon)
                {
                    slice[i] = 0f;
                    continue;
                }

                slice[i] = normalize ? (float)((value - mean) / stdDev) : (float)(value - mean);
            }
        }

        private static bool TryComputeNonZeroStatistics(float[] values, out double mean, out double stdDev)
        {
            mean = 0.0;
            stdDev = 0.0;

            if (values == null || values.Length == 0)
            {
                return false;
            }

            double sum = 0.0;
            double sumSquares = 0.0;
            long count = 0;

            for (var i = 0; i < values.Length; ++i)
            {
                var value = values[i];
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
    }
}
