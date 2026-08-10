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
    /// <summary>
    /// HD-BET (nnU-Net backbone) whole-brain extraction via Unity Sentis.
    /// Expects ONNX exported by Tools/HdBet/export_hd_bet_onnx.py with patch (96,192,192),
    /// 1 mm isotropic spacing, 1 input channel, 2 logit outputs (background/brain).
    /// Tensor layout matches nnU-Net: (N, C, Z, Y, X) with transpose_forward = identity.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class HdBetSentisInference : SegmentationInferenceBehaviour
    {
        public enum BackendPreference
        {
            CPU = 1,
            GPUCompute = 2,
            GPUPixel = 3
        }

        private const int InputChannels = 1;
        private const int OutputChannels = 2;
        private const int PatchSizeZ = 96;
        private const int PatchSizeY = 192;
        private const int PatchSizeX = 192;
        private const float TargetSpacingMm = 1f;
        private const float TileStepSize = 0.5f;
        private const byte BrainLabel = 1;

        [Header("Model")]
        [SerializeField] private ModelAsset modelAsset;
        [SerializeField] private BackendPreference backendPreference = BackendPreference.GPUCompute;

        [Header("Inference")]
        [SerializeField, Range(0f, 1f)] private float foregroundThreshold = 0.5f;
        [SerializeField] private bool useGaussianImportance = true;

        [SerializeField, HideInInspector] private string lastRunSummary = string.Empty;

        private Model _runtimeModel;
        private Worker _worker;
        private BackendType _activeBackend;
        private ModelAsset _cachedModelAsset;
        private BackendPreference _cachedBackendPreference;
        private float[] _gaussianImportance;

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

            progress?.Invoke(0.06f, "Reading volume for HD-BET...");
            if (!SimpleITKNative.TryReadVolumeFloat32Raw(primaryVolumePath, out var volumeData, out error))
            {
                error = $"Failed to read volume '{primaryVolumePath}': {error}";
                return false;
            }

            progress?.Invoke(0.12f, "Preparing HD-BET Sentis runner...");
            var requestedBackend = forceCpuBackend ? BackendPreference.CPU : backendPreference;
            if (!TryRun(volumeData, requestedBackend, progress, out segmentationData, out error))
            {
                return false;
            }

            progress?.Invoke(0.92f, "Validating HD-BET mask...");
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

            if (!EnsureWorker(requestedBackend, out error))
            {
                return false;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                progress?.Invoke(0.18f, "Cropping non-zero region...");
                if (!SimpleITKNative.TryCropScalarVolumeToNonZero(
                        volumeData,
                        out var cropped,
                        out _,
                        out _,
                        out _,
                        out error))
                {
                    return false;
                }

                progress?.Invoke(0.26f, "Resampling to 1 mm isotropic...");
                if (!SimpleITKNative.TryResampleScalarVolume(
                        cropped,
                        new[] { (double)TargetSpacingMm, TargetSpacingMm, TargetSpacingMm },
                        out var resampled,
                        out error))
                {
                    return false;
                }

                progress?.Invoke(0.34f, "Normalizing MRI intensities...");
                NormalizeNonZeroInPlace(resampled.Voxels);

                // nnU-Net layout after identity transpose: (Z, Y, X) == (Depth, Height, Width)
                var sizeZ = resampled.Depth;
                var sizeY = resampled.Height;
                var sizeX = resampled.Width;
                var voxelCount = sizeX * sizeY * sizeZ;
                var foregroundSum = new float[voxelCount];
                var weightSum = new float[voxelCount];

                var strideZ = Mathf.Max(1, Mathf.FloorToInt(PatchSizeZ * TileStepSize));
                var strideY = Mathf.Max(1, Mathf.FloorToInt(PatchSizeY * TileStepSize));
                var strideX = Mathf.Max(1, Mathf.FloorToInt(PatchSizeX * TileStepSize));
                var startsZ = ComputePatchStarts(sizeZ, PatchSizeZ, strideZ);
                var startsY = ComputePatchStarts(sizeY, PatchSizeY, strideY);
                var startsX = ComputePatchStarts(sizeX, PatchSizeX, strideX);
                var totalPatches = startsZ.Length * startsY.Length * startsX.Length;
                var importance = useGaussianImportance ? GetOrCreateGaussianImportance() : null;

                var processedPatches = 0;
                for (var iz = 0; iz < startsZ.Length; ++iz)
                {
                    var originZ = startsZ[iz];
                    for (var iy = 0; iy < startsY.Length; ++iy)
                    {
                        var originY = startsY[iy];
                        for (var ix = 0; ix < startsX.Length; ++ix)
                        {
                            var originX = startsX[ix];
                            processedPatches++;
                            var patchProgress = 0.36f + (0.48f * processedPatches / Mathf.Max(1, totalPatches));
                            progress?.Invoke(
                                patchProgress,
                                $"HD-BET sliding window {processedPatches}/{totalPatches}...");

                            if (!TryRunPatch(
                                    resampled,
                                    originX,
                                    originY,
                                    originZ,
                                    foregroundSum,
                                    weightSum,
                                    importance,
                                    out error))
                            {
                                return false;
                            }
                        }
                    }
                }

                progress?.Invoke(0.86f, "Thresholding and restoring original geometry...");
                var labelOnResampled = new byte[voxelCount];
                byte labelMaximum = 0;
                for (var i = 0; i < voxelCount; ++i)
                {
                    var weight = weightSum[i];
                    var probability = weight > 0f ? foregroundSum[i] / weight : 0f;
                    var label = probability >= foregroundThreshold ? BrainLabel : (byte)0;
                    labelOnResampled[i] = label;
                    if (label > labelMaximum)
                    {
                        labelMaximum = label;
                    }
                }

                var resampledLabels = new SimpleITKLabelVolumeData
                {
                    Width = sizeX,
                    Height = sizeY,
                    Depth = sizeZ,
                    Voxels = labelOnResampled,
                    ImageInfo = resampled.ImageInfo,
                    LabelMaximum = labelMaximum
                };

                if (!SimpleITKNative.TryResampleLabelVolumeToReference(
                        resampledLabels,
                        volumeData.ImageInfo,
                        out segmentationData,
                        out error))
                {
                    return false;
                }

                stopwatch.Stop();
                lastRunSummary =
                    $"backend={_activeBackend}, requested={requestedBackend}, " +
                    $"input=({volumeData.Width}, {volumeData.Height}, {volumeData.Depth}), " +
                    $"model=({sizeX}, {sizeY}, {sizeZ}), patches={processedPatches}, " +
                    $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}, labels<={segmentationData.LabelMaximum}";
                UnityEngine.Debug.Log($"HdBetSentisInference ({name}): {lastRunSummary}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"HD-BET Sentis inference failed: {ex.Message}";
                return false;
            }
        }

        private bool TryRunPatch(
            SimpleITKScalarVolumeData volumeData,
            int originX,
            int originY,
            int originZ,
            float[] foregroundSum,
            float[] weightSum,
            float[] importance,
            out string error)
        {
            error = string.Empty;

            var inputShape = new TensorShape(1, InputChannels, PatchSizeZ, PatchSizeY, PatchSizeX);
            var inputData = new float[inputShape.length];
            CopyPatchToModelInput(volumeData, originX, originY, originZ, inputData);

            using var inputTensor = new Tensor<float>(inputShape, inputData);
            _worker.Schedule(inputTensor);
            var outputTensor = _worker.PeekOutput() as Tensor<float>;
            if (outputTensor == null)
            {
                error = "HD-BET Sentis worker did not return a float output tensor.";
                return false;
            }

            using var cpuOutput = outputTensor.ReadbackAndClone() as Tensor<float>;
            if (cpuOutput == null)
            {
                error = "HD-BET Sentis output readback failed.";
                return false;
            }

            if (cpuOutput.shape.rank != 5 ||
                cpuOutput.shape[0] != 1 ||
                cpuOutput.shape[1] < OutputChannels ||
                cpuOutput.shape[2] != PatchSizeZ ||
                cpuOutput.shape[3] != PatchSizeY ||
                cpuOutput.shape[4] != PatchSizeX)
            {
                error =
                    $"Unexpected HD-BET output tensor shape: {cpuOutput.shape}. " +
                    $"Expected (1, >=2, {PatchSizeZ}, {PatchSizeY}, {PatchSizeX}).";
                return false;
            }

            for (var z = 0; z < PatchSizeZ; ++z)
            {
                var volumeZ = originZ + z;
                if (volumeZ < 0 || volumeZ >= volumeData.Depth)
                {
                    continue;
                }

                for (var y = 0; y < PatchSizeY; ++y)
                {
                    var volumeY = originY + y;
                    if (volumeY < 0 || volumeY >= volumeData.Height)
                    {
                        continue;
                    }

                    for (var x = 0; x < PatchSizeX; ++x)
                    {
                        var volumeX = originX + x;
                        if (volumeX < 0 || volumeX >= volumeData.Width)
                        {
                            continue;
                        }

                        var backgroundLogit = cpuOutput[0, 0, z, y, x];
                        var brainLogit = cpuOutput[0, 1, z, y, x];
                        var brainProbability = SoftmaxForeground(backgroundLogit, brainLogit);
                        var weight = importance != null
                            ? importance[((z * PatchSizeY) + y) * PatchSizeX + x]
                            : 1f;

                        var unityIndex = ((volumeZ * volumeData.Height) + volumeY) * volumeData.Width + volumeX;
                        foregroundSum[unityIndex] += brainProbability * weight;
                        weightSum[unityIndex] += weight;
                    }
                }
            }

            return true;
        }

        private void Reset()
        {
            TryAssignDefaultModelAsset();
            backendPreference = BackendPreference.GPUCompute;
            foregroundThreshold = 0.5f;
            useGaussianImportance = true;
        }

        private void OnValidate()
        {
            TryAssignDefaultModelAsset();
            if (!Enum.IsDefined(typeof(BackendPreference), backendPreference))
            {
                backendPreference = BackendPreference.GPUCompute;
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
                error = "HdBetSentisInference requires a ModelAsset reference.";
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

            modelAsset = Resources.Load<ModelAsset>("hd_bet");
            if (modelAsset != null)
            {
                return;
            }

#if UNITY_EDITOR
            modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Models/HdBet/hd_bet.onnx");
#endif
        }

        private float[] GetOrCreateGaussianImportance()
        {
            if (_gaussianImportance != null)
            {
                return _gaussianImportance;
            }

            _gaussianImportance = CreateGaussianImportance(PatchSizeZ, PatchSizeY, PatchSizeX);
            return _gaussianImportance;
        }

        private static float[] CreateGaussianImportance(int sizeZ, int sizeY, int sizeX)
        {
            var weights = new float[sizeZ * sizeY * sizeX];
            var sigmaZ = sizeZ / 8f;
            var sigmaY = sizeY / 8f;
            var sigmaX = sizeX / 8f;
            var centerZ = (sizeZ - 1) * 0.5f;
            var centerY = (sizeY - 1) * 0.5f;
            var centerX = (sizeX - 1) * 0.5f;
            var maxWeight = 0f;

            for (var z = 0; z < sizeZ; ++z)
            {
                var dz = (z - centerZ) / sigmaZ;
                for (var y = 0; y < sizeY; ++y)
                {
                    var dy = (y - centerY) / sigmaY;
                    for (var x = 0; x < sizeX; ++x)
                    {
                        var dx = (x - centerX) / sigmaX;
                        var weight = Mathf.Exp(-0.5f * (dx * dx + dy * dy + dz * dz));
                        weights[((z * sizeY) + y) * sizeX + x] = weight;
                        if (weight > maxWeight)
                        {
                            maxWeight = weight;
                        }
                    }
                }
            }

            if (maxWeight > 0f)
            {
                for (var i = 0; i < weights.Length; ++i)
                {
                    weights[i] = Mathf.Max(weights[i] / maxWeight, 1e-3f);
                }
            }

            return weights;
        }

        private static void CopyPatchToModelInput(
            SimpleITKScalarVolumeData volumeData,
            int originX,
            int originY,
            int originZ,
            float[] destination)
        {
            // destination layout: (Z, Y, X) with X innermost
            for (var z = 0; z < PatchSizeZ; ++z)
            {
                for (var y = 0; y < PatchSizeY; ++y)
                {
                    for (var x = 0; x < PatchSizeX; ++x)
                    {
                        var volumeX = originX + x;
                        var volumeY = originY + y;
                        var volumeZ = originZ + z;
                        var value = 0f;

                        if (volumeX >= 0 && volumeX < volumeData.Width &&
                            volumeY >= 0 && volumeY < volumeData.Height &&
                            volumeZ >= 0 && volumeZ < volumeData.Depth)
                        {
                            var sourceIndex = ((volumeZ * volumeData.Height) + volumeY) * volumeData.Width + volumeX;
                            value = volumeData.Voxels[sourceIndex];
                        }

                        destination[((z * PatchSizeY) + y) * PatchSizeX + x] = value;
                    }
                }
            }
        }

        private static void NormalizeNonZeroInPlace(float[] values)
        {
            if (values == null || values.Length == 0)
            {
                return;
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
                return;
            }

            var mean = sum / count;
            var variance = Math.Max((sumSquares / count) - (mean * mean), 0.0);
            var stdDev = Math.Sqrt(variance);
            var normalize = stdDev > 0.0;

            for (var i = 0; i < values.Length; ++i)
            {
                var value = values[i];
                if (Math.Abs(value) <= float.Epsilon)
                {
                    values[i] = 0f;
                    continue;
                }

                values[i] = normalize ? (float)((value - mean) / stdDev) : (float)(value - mean);
            }
        }

        private static int[] ComputePatchStarts(int size, int patchSize, int stride)
        {
            if (size <= patchSize)
            {
                return new[] { 0 };
            }

            var count = 1;
            for (var start = 0; start < size - patchSize; start += stride)
            {
                count++;
            }

            var starts = new int[count];
            var index = 0;
            for (var start = 0; start < size - patchSize; start += stride)
            {
                starts[index++] = start;
            }

            starts[index] = size - patchSize;
            return starts;
        }

        private static bool TryValidateGeneratedSegmentation(SimpleITKLabelVolumeData segmentationData, out string error)
        {
            error = string.Empty;

            if (segmentationData.Voxels == null || segmentationData.Voxels.Length == 0)
            {
                error = "Generated HD-BET segmentation is empty.";
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

            if (nonZeroCount == 0)
            {
                error = "Generated HD-BET segmentation has no brain voxels.";
                return false;
            }

            var nonZeroFraction = (float)nonZeroCount / segmentationData.Voxels.Length;
            if (nonZeroFraction > 0.95f)
            {
                error =
                    $"Generated HD-BET segmentation looks invalid: non-zero fraction={nonZeroFraction:F3}. " +
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

        private static float SoftmaxForeground(float backgroundLogit, float foregroundLogit)
        {
            var maxLogit = Mathf.Max(backgroundLogit, foregroundLogit);
            var backgroundExp = Mathf.Exp(backgroundLogit - maxLogit);
            var foregroundExp = Mathf.Exp(foregroundLogit - maxLogit);
            return foregroundExp / (backgroundExp + foregroundExp);
        }
    }
}
