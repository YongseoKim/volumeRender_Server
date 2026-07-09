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
    public sealed class SpleenSentisInference : SegmentationInferenceBehaviour
    {
        public enum BackendPreference
        {
            CPU = 1,
            GPUCompute = 2,
            GPUPixel = 3
        }

        private const int InputChannels = 1;
        private const int PatchSize = 96;
        private const float IntensityMinHu = -57f;
        private const float IntensityMaxHu = 164f;

        [Header("Model")]
        [SerializeField] private ModelAsset modelAsset;
        [SerializeField] private BackendPreference backendPreference = BackendPreference.CPU;

        [Header("Inference")]
        [SerializeField, Range(0f, 1f)] private float foregroundThreshold = 0.5f;
        [SerializeField, Range(1, 4)] private int overlapDivisor = 2;
        [SerializeField] private bool useSlidingWindow = true;

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

            progress?.Invoke(0.08f, "Reading CT volume...");
            if (!TryReadCtVolume(primaryVolumePath, out var volumeData, out error))
            {
                return false;
            }

            progress?.Invoke(0.16f, "Preparing Sentis runner...");
            var requestedBackend = forceCpuBackend ? BackendPreference.CPU : backendPreference;
            if (!TryRun(volumeData, requestedBackend, progress, out segmentationData, out error))
            {
                return false;
            }

            progress?.Invoke(0.82f, "Validating segmentation output...");
            if (!TryValidateGeneratedSegmentation(segmentationData, out error))
            {
                return false;
            }

            var caseId = Path.GetFileNameWithoutExtension(primaryVolumePath);
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

            var voxelCount = volumeData.Width * volumeData.Height * volumeData.Depth;
            var foregroundSum = new float[voxelCount];
            var visitCount = new int[voxelCount];

            var stride = Mathf.Max(1, PatchSize / Mathf.Max(1, overlapDivisor));
            var patchStartsX = ComputePatchStarts(volumeData.Width, PatchSize, stride);
            var patchStartsY = ComputePatchStarts(volumeData.Height, PatchSize, stride);
            var patchStartsZ = ComputePatchStarts(volumeData.Depth, PatchSize, stride);
            var totalPatches = useSlidingWindow
                ? patchStartsX.Length * patchStartsY.Length * patchStartsZ.Length
                : 1;

            var stopwatch = Stopwatch.StartNew();
            var processedPatches = 0;

            try
            {
                if (!useSlidingWindow &&
                    (volumeData.Width > PatchSize || volumeData.Height > PatchSize || volumeData.Depth > PatchSize))
                {
                    error =
                        $"Spleen Sentis runner supports up to ({PatchSize}, {PatchSize}, {PatchSize}) without sliding window, " +
                        $"received ({volumeData.Width}, {volumeData.Height}, {volumeData.Depth}). Enable sliding window or resample the volume.";
                    return false;
                }

                var patchOriginX = useSlidingWindow ? 0 : Mathf.Max((volumeData.Width - PatchSize) / 2, 0);
                var patchOriginY = useSlidingWindow ? 0 : Mathf.Max((volumeData.Height - PatchSize) / 2, 0);
                var patchOriginZ = useSlidingWindow ? 0 : Mathf.Max((volumeData.Depth - PatchSize) / 2, 0);
                var patchOriginsX = useSlidingWindow ? patchStartsX : new[] { patchOriginX };
                var patchOriginsY = useSlidingWindow ? patchStartsY : new[] { patchOriginY };
                var patchOriginsZ = useSlidingWindow ? patchStartsZ : new[] { patchOriginZ };

                for (var patchIndexZ = 0; patchIndexZ < patchOriginsZ.Length; ++patchIndexZ)
                {
                    var originZ = patchOriginsZ[patchIndexZ];
                    for (var patchIndexY = 0; patchIndexY < patchOriginsY.Length; ++patchIndexY)
                    {
                        var originY = patchOriginsY[patchIndexY];
                        for (var patchIndexX = 0; patchIndexX < patchOriginsX.Length; ++patchIndexX)
                        {
                            var originX = patchOriginsX[patchIndexX];
                            processedPatches++;

                            var patchProgress = 0.2f + (0.58f * processedPatches / Mathf.Max(1, totalPatches));
                            progress?.Invoke(
                                patchProgress,
                                useSlidingWindow
                                    ? $"Running Sentis inference patch {processedPatches}/{totalPatches}..."
                                    : "Running Sentis inference...");

                            if (!TryRunPatch(
                                    volumeData,
                                    originX,
                                    originY,
                                    originZ,
                                    foregroundSum,
                                    visitCount,
                                    out error))
                            {
                                return false;
                            }
                        }
                    }
                }

                stopwatch.Stop();

                var labelBuffer = new byte[voxelCount];
                byte labelMaximum = 0;
                for (var i = 0; i < voxelCount; ++i)
                {
                    var visits = visitCount[i];
                    var foregroundProbability = visits > 0 ? foregroundSum[i] / visits : 0f;
                    var label = foregroundProbability >= foregroundThreshold ? (byte)1 : (byte)0;
                    labelBuffer[i] = label;
                    if (label > labelMaximum)
                    {
                        labelMaximum = label;
                    }
                }

                segmentationData = new SimpleITKLabelVolumeData
                {
                    Width = volumeData.Width,
                    Height = volumeData.Height,
                    Depth = volumeData.Depth,
                    Voxels = labelBuffer,
                    ImageInfo = volumeData.ImageInfo,
                    LabelMaximum = labelMaximum
                };

                lastRunSummary =
                    $"backend={_activeBackend}, requested={requestedBackend}, input=({volumeData.Width}, {volumeData.Height}, {volumeData.Depth}), " +
                    $"patches={processedPatches}, stride={stride}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}, labels<={labelMaximum}";

                UnityEngine.Debug.Log($"SpleenSentisInference ({name}): {lastRunSummary}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"Spleen Sentis inference failed: {ex.Message}";
                return false;
            }
        }

        private bool TryRunPatch(
            SimpleITKScalarVolumeData volumeData,
            int originX,
            int originY,
            int originZ,
            float[] foregroundSum,
            int[] visitCount,
            out string error)
        {
            error = string.Empty;

            var inputShape = new TensorShape(1, InputChannels, PatchSize, PatchSize, PatchSize);
            var inputData = new float[inputShape.length];
            CopyNormalizedPatchToModelInput(volumeData, originX, originY, originZ, inputData);

            using var inputTensor = new Tensor<float>(inputShape, inputData);
            _worker.Schedule(inputTensor);
            var outputTensor = _worker.PeekOutput() as Tensor<float>;
            if (outputTensor == null)
            {
                error = "Spleen Sentis worker did not return a float output tensor.";
                return false;
            }

            using var cpuOutput = outputTensor.ReadbackAndClone() as Tensor<float>;
            if (cpuOutput == null)
            {
                error = "Spleen Sentis output readback failed.";
                return false;
            }

            if (cpuOutput.shape.rank != 5 || cpuOutput.shape[0] != 1)
            {
                error = $"Unexpected Spleen output tensor shape: {cpuOutput.shape}.";
                return false;
            }

            var outputChannels = cpuOutput.shape[1];
            if (outputChannels != 1 && outputChannels != 2)
            {
                error = $"Spleen Sentis expects 1 or 2 output channels, received {outputChannels}.";
                return false;
            }

            for (var z = 0; z < PatchSize; ++z)
            {
                var volumeZ = originZ + z;
                if (volumeZ < 0 || volumeZ >= volumeData.Depth)
                {
                    continue;
                }

                for (var y = 0; y < PatchSize; ++y)
                {
                    var volumeY = originY + y;
                    if (volumeY < 0 || volumeY >= volumeData.Height)
                    {
                        continue;
                    }

                    for (var x = 0; x < PatchSize; ++x)
                    {
                        var volumeX = originX + x;
                        if (volumeX < 0 || volumeX >= volumeData.Width)
                        {
                            continue;
                        }

                        var foregroundProbability = outputChannels == 2
                            ? SoftmaxForeground(cpuOutput[0, 0, x, y, z], cpuOutput[0, 1, x, y, z])
                            : Sigmoid(cpuOutput[0, 0, x, y, z]);

                        var unityIndex = ((volumeZ * volumeData.Height) + volumeY) * volumeData.Width + volumeX;
                        foregroundSum[unityIndex] += foregroundProbability;
                        visitCount[unityIndex]++;
                    }
                }
            }

            return true;
        }

        private void Reset()
        {
            TryAssignDefaultModelAsset();
            backendPreference = BackendPreference.CPU;
        }

        private void OnValidate()
        {
            TryAssignDefaultModelAsset();
            if (!Enum.IsDefined(typeof(BackendPreference), backendPreference))
            {
                backendPreference = BackendPreference.CPU;
            }

            foregroundThreshold = Mathf.Clamp01(foregroundThreshold);
            overlapDivisor = Mathf.Clamp(overlapDivisor, 1, 4);
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
                error = "SpleenSentisInference requires a ModelAsset reference.";
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
            _cachedBackendPreference = BackendPreference.CPU;
        }

        private void TryAssignDefaultModelAsset()
        {
            if (modelAsset != null)
            {
                return;
            }

#if UNITY_EDITOR
            modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Models/Monai/spleen_model.onnx");
#endif
        }

        private static bool TryReadCtVolume(
            string imagePath,
            out SimpleITKScalarVolumeData volumeData,
            out string error)
        {
            if (!SimpleITKNative.TryReadVolumeFloat32Raw(imagePath, out volumeData, out error))
            {
                error = $"Failed to read CT volume '{imagePath}': {error}";
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
            if (nonZeroFraction > 0.6f)
            {
                error =
                    $"Generated segmentation looks invalid for spleen labels: non-zero fraction={nonZeroFraction:F3}. " +
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

        private static void CopyNormalizedPatchToModelInput(
            SimpleITKScalarVolumeData volumeData,
            int originX,
            int originY,
            int originZ,
            float[] destination)
        {
            for (var z = 0; z < PatchSize; ++z)
            {
                for (var y = 0; y < PatchSize; ++y)
                {
                    for (var x = 0; x < PatchSize; ++x)
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
                            value = NormalizeCtIntensity(volumeData.Voxels[sourceIndex]);
                        }

                        var destinationIndex = (x * PatchSize + y) * PatchSize + z;
                        destination[destinationIndex] = value;
                    }
                }
            }
        }

        private static float NormalizeCtIntensity(float hounsfieldValue)
        {
            return Mathf.Clamp01((hounsfieldValue - IntensityMinHu) / (IntensityMaxHu - IntensityMinHu));
        }

        private static float SoftmaxForeground(float backgroundLogit, float foregroundLogit)
        {
            var maxLogit = Mathf.Max(backgroundLogit, foregroundLogit);
            var backgroundExp = Mathf.Exp(backgroundLogit - maxLogit);
            var foregroundExp = Mathf.Exp(foregroundLogit - maxLogit);
            return foregroundExp / (backgroundExp + foregroundExp);
        }

        private static float Sigmoid(float value)
        {
            return 1.0f / (1.0f + Mathf.Exp(-value));
        }
    }
}
