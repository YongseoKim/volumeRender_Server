using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SimpleITKUnity
{
    /// <summary>
    /// Whole-head tissue from siamize (external CUDA process) plus tumor from
    /// <see cref="BratsSentisInference"/> on the Sentis CPU backend.
    /// One in-flight run is shared so the local view and the network transfer
    /// do not segment the same volume twice.
    /// </summary>
    public sealed class HeadSegmentationRunner : MonoBehaviour
    {
        private const string LogPrefix = "[HeadSegmentation]";
        public const byte TumorCoreLabel = 21;
        public const byte EdemaLabel = 22;
        public const byte EnhancingTumorLabel = 24;

        [SerializeField] private BratsSentisInference bratsInference;
        [SerializeField] private string siamizeExecutable = string.Empty;
        [SerializeField] private string compute = "cuda";
        [SerializeField] private string folds = "0";

        private readonly object _gate = new object();
        private CancellationTokenSource _runCts;
        private string _inflightKey = string.Empty;
        private Task<HeadSegmentationResult> _inflight;
        private HeadSegmentationResult _lastSuccess;

        /// <summary>
        /// Segments <paramref name="volumePath"/>. Tissue labels stay 0-17.
        /// When BraTS modalities exist and fit the 240x240x160 runner, tumor
        /// core, edema, and enhancing tumor replace tissue as 21, 22, and 24.
        /// </summary>
        public Task<HeadSegmentationResult> SegmentAsync(
            string volumePath,
            SimpleITKImageInfo referenceInfo,
            int width,
            int height,
            int depth)
        {
            if (string.IsNullOrWhiteSpace(volumePath))
            {
                return Task.FromResult(HeadSegmentationResult.Fail("Volume path is empty."));
            }

            string key;
            try
            {
                key = Path.GetFullPath(volumePath);
            }
            catch (Exception ex)
            {
                return Task.FromResult(HeadSegmentationResult.Fail(ex.Message));
            }

            lock (_gate)
            {
                if (_inflight != null &&
                    !_inflight.IsCompleted &&
                    string.Equals(_inflightKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return _inflight;
                }

                if (_lastSuccess != null &&
                    _lastSuccess.Success &&
                    string.Equals(_lastSuccess.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    _lastSuccess.Width == width &&
                    _lastSuccess.Height == height &&
                    _lastSuccess.Depth == depth)
                {
                    return Task.FromResult(_lastSuccess);
                }

                _runCts?.Cancel();
                _runCts = new CancellationTokenSource();
                _inflightKey = key;
                _inflight = RunAsync(key, referenceInfo, width, height, depth, _runCts.Token);
                return _inflight;
            }
        }

        private async Task<HeadSegmentationResult> RunAsync(
            string volumePath,
            SimpleITKImageInfo referenceInfo,
            int width,
            int height,
            int depth,
            CancellationToken token)
        {
            try
            {
                var siamize = await Task.Run(() => RunSiamize(volumePath, referenceInfo, width, height, depth, token), token);
                token.ThrowIfCancellationRequested();
                if (!siamize.Success)
                {
                    return siamize;
                }

                var merged = TryMergeBrats(volumePath, siamize, referenceInfo, width, height, depth);
                lock (_gate)
                {
                    if (merged.Success)
                    {
                        _lastSuccess = merged;
                    }

                    if (string.Equals(_inflightKey, volumePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _inflight = null;
                    }
                }

                return merged;
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    if (string.Equals(_inflightKey, volumePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _inflight = null;
                    }
                }

                throw;
            }
        }

        private HeadSegmentationResult TryMergeBrats(
            string volumePath,
            HeadSegmentationResult tissue,
            SimpleITKImageInfo referenceInfo,
            int width,
            int height,
            int depth)
        {
            if (bratsInference == null)
            {
                bratsInference = FindFirstObjectByType<BratsSentisInference>(FindObjectsInactive.Include);
            }

            if (bratsInference == null)
            {
                Debug.LogWarning($"{LogPrefix} BraTS component is missing. Sending tissue labels only.");
                return tissue;
            }

            if (!bratsInference.TryPrepareSegmentationForVolume(
                    volumePath,
                    out var tumor,
                    out var summary,
                    out var error,
                    forceCpuBackend: true))
            {
                Debug.Log($"{LogPrefix} BraTS skipped. {error}");
                return tissue;
            }

            var tumorLabels = tumor.Voxels;
            if (tumor.Width != width || tumor.Height != height || tumor.Depth != depth)
            {
                if (!SimpleITKNative.TryResampleLabelVolumeToReference(tumor, referenceInfo, out var resampled, out var resampleError))
                {
                    Debug.LogWarning($"{LogPrefix} Tumor resample failed. {resampleError}");
                    return tissue;
                }

                tumorLabels = resampled.Voxels;
                if (resampled.Width != width || resampled.Height != height || resampled.Depth != depth)
                {
                    Debug.LogWarning(
                        $"{LogPrefix} Resampled tumor is {resampled.Width}x{resampled.Height}x{resampled.Depth}, " +
                        $"volume is {width}x{height}x{depth}. Sending tissue labels only.");
                    return tissue;
                }
            }

            var combined = (byte[])tissue.Voxels.Clone();
            var labelMaximum = tissue.LabelMaximum;
            for (var i = 0; i < combined.Length && i < tumorLabels.Length; ++i)
            {
                var tumorLabel = tumorLabels[i];
                if (tumorLabel == 0)
                {
                    continue;
                }

                var mapped = tumorLabel switch
                {
                    1 => TumorCoreLabel,
                    2 => EdemaLabel,
                    4 => EnhancingTumorLabel,
                    _ => tumorLabel
                };
                combined[i] = mapped;
                if (mapped > labelMaximum)
                {
                    labelMaximum = mapped;
                }
            }

            tumor.ConsumeVoxels();
            Debug.Log($"{LogPrefix} Combined tissue and tumor. {summary}");
            return HeadSegmentationResult.Ok(
                tissue.Key,
                width,
                height,
                depth,
                combined,
                tissue.ImageInfo,
                labelMaximum,
                VolumeNetworkProtocol.DefaultSegmentationColorMapFileName,
                tissue.SourceLabel);
        }

        private HeadSegmentationResult RunSiamize(
            string volumePath,
            SimpleITKImageInfo referenceInfo,
            int width,
            int height,
            int depth,
            CancellationToken token)
        {
            var siamize = ResolveSiamizeExecutable();
            if (string.IsNullOrWhiteSpace(siamize))
            {
                return HeadSegmentationResult.Fail(
                    "siamize was not found. Set SIAMIZE_BIN or build C:\\Users\\user\\siamize\\build\\Release\\siamize.exe.");
            }

            try
            {
                return RunSiamizeCore(siamize, volumePath, referenceInfo, width, height, depth, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return HeadSegmentationResult.Fail(ex.Message);
            }
        }

        private HeadSegmentationResult RunSiamizeCore(
            string siamize,
            string volumePath,
            SimpleITKImageInfo referenceInfo,
            int width,
            int height,
            int depth,
            CancellationToken token)
        {
            var outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "volumeRender_Server",
                "head-segmentation",
                Sanitize(Path.GetFileName(volumePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
            Directory.CreateDirectory(outputDir);

            var inputPath = PrepareInput(PreferT1Sibling(volumePath), outputDir);
            var labelPath = Path.Combine(outputDir, "labels.nii.gz");
            if (File.Exists(labelPath))
            {
                File.Delete(labelPath);
            }

            var computeMode = string.IsNullOrWhiteSpace(compute) ? "cuda" : compute.Trim();
            var foldArg = string.IsNullOrWhiteSpace(folds) ? "0" : folds.Trim();
            Debug.Log($"{LogPrefix} siamize {computeMode} folds {foldArg} on '{inputPath}'.");

            var exitCode = RunProcess(
                siamize,
                new[]
                {
                    "-i", inputPath,
                    "-o", labelPath,
                    "-M", foldArg,
                    "-c", computeMode,
                    "-C", "18",
                    "-v"
                },
                computeMode.Equals("cuda", StringComparison.OrdinalIgnoreCase),
                token);
            token.ThrowIfCancellationRequested();

            if (exitCode != 0 || !File.Exists(labelPath))
            {
                return HeadSegmentationResult.Fail($"siamize exited with code {exitCode}.");
            }

            if (!SimpleITKNative.TryReadLabelVolumeUInt8(labelPath, out var labels, out var readError))
            {
                return HeadSegmentationResult.Fail(readError);
            }

            if (labels.Width != width || labels.Height != height || labels.Depth != depth)
            {
                if (!SimpleITKNative.TryResampleLabelVolumeToReference(labels, referenceInfo, out var resampled, out var resampleError))
                {
                    return HeadSegmentationResult.Fail(
                        $"siamize labels are {labels.Width}x{labels.Height}x{labels.Depth}, " +
                        $"volume is {width}x{height}x{depth}, resample failed: {resampleError}");
                }

                labels = resampled;
            }

            if (labels.Width != width || labels.Height != height || labels.Depth != depth)
            {
                return HeadSegmentationResult.Fail(
                    $"siamize labels are {labels.Width}x{labels.Height}x{labels.Depth}, volume is {width}x{height}x{depth}.");
            }

            Debug.Log($"{LogPrefix} Tissue labels ready: {labelPath}");
            return HeadSegmentationResult.Ok(
                volumePath,
                width,
                height,
                depth,
                labels.Voxels,
                labels.ImageInfo.Spacing == null ? referenceInfo : labels.ImageInfo,
                labels.LabelMaximum,
                VolumeNetworkProtocol.DefaultSegmentationColorMapFileName,
                labelPath);
        }

        private static string PreferT1Sibling(string volumePath)
        {
            if (string.IsNullOrWhiteSpace(volumePath) || Directory.Exists(volumePath) || !File.Exists(volumePath))
            {
                return volumePath;
            }

            var directory = Path.GetDirectoryName(volumePath);
            var name = Path.GetFileName(volumePath);
            string stem;
            string extension;
            if (name.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase))
            {
                stem = name.Substring(0, name.Length - 7);
                extension = ".nii.gz";
            }
            else
            {
                stem = Path.GetFileNameWithoutExtension(name);
                extension = Path.GetExtension(name);
            }

            var suffixes = new[] { "_t1ce", "_flair", "_t2", "_t1" };
            for (var i = 0; i < suffixes.Length; ++i)
            {
                var suffix = suffixes[i];
                if (!stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var t1Path = Path.Combine(directory ?? string.Empty, stem.Substring(0, stem.Length - suffix.Length) + "_t1" + extension);
                return File.Exists(t1Path) ? t1Path : volumePath;
            }

            return volumePath;
        }

        private static string PrepareInput(string volumePath, string outputDir)
        {
            if (volumePath.EndsWith(".nii", StringComparison.OrdinalIgnoreCase) ||
                volumePath.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase))
            {
                return volumePath;
            }

            var exported = Path.Combine(outputDir, "input.nii.gz");
            if (!SimpleITKNative.TryExportVolumeAsNifti(volumePath, exported, out var error))
            {
                throw new InvalidOperationException($"Failed to export NIfTI for siamize: {error}");
            }

            return exported;
        }

        private string ResolveSiamizeExecutable()
        {
            if (!string.IsNullOrWhiteSpace(siamizeExecutable) && File.Exists(siamizeExecutable))
            {
                return siamizeExecutable;
            }

            var fromEnvironment = Environment.GetEnvironmentVariable("SIAMIZE_BIN");
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
            {
                return fromEnvironment;
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            var pathFile = Path.Combine(projectRoot, "Tools", "SiamizeWorker", "siamize.path");
            if (File.Exists(pathFile))
            {
                var configured = File.ReadAllText(pathFile).Trim();
                if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                {
                    return configured;
                }
            }

            var build = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "siamize",
                "build");
            var candidates = new[]
            {
                Path.Combine(projectRoot, "Tools", "siamize", "siamize.exe"),
                Path.Combine(build, "Release", "siamize.exe"),
                Path.Combine(build, "Debug", "siamize.exe"),
                Path.Combine(build, "siamize.exe")
            };
            for (var i = 0; i < candidates.Length; ++i)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }

            return string.Empty;
        }

        private static int RunProcess(
            string fileName,
            string[] arguments,
            bool prependCuda,
            CancellationToken token)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
            };
            for (var i = 0; i < arguments.Length; ++i)
            {
                startInfo.ArgumentList.Add(arguments[i]);
            }

            if (prependCuda && TryCudaPathPrefix(out var prefix))
            {
                startInfo.Environment["PATH"] = prefix + Path.PathSeparator + startInfo.Environment["PATH"];
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            using var registration = token.Register(() => KillProcess(process));
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();

            var tail = Tail(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, 1500);
            if (!string.IsNullOrWhiteSpace(tail))
            {
                Debug.Log($"{LogPrefix} {tail}");
            }

            return process.ExitCode;
        }

        private static void KillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static bool TryCudaPathPrefix(out string prefix)
        {
            prefix = string.Empty;
            var cudaBin = FindCudaBin();
            if (cudaBin == null)
            {
                Debug.LogWarning($"{LogPrefix} CUDA 12 was not found. siamize -c cuda may fail.");
                return false;
            }

            var cudnnBin = FindCudnnBin(cudaBin);
            prefix = cudnnBin != null && !string.Equals(cudnnBin, cudaBin, StringComparison.OrdinalIgnoreCase)
                ? cudnnBin + Path.PathSeparator + cudaBin
                : cudaBin;
            return true;
        }

        private static string FindCudaBin()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (HasCuda12Runtime(fromEnvironment))
            {
                return Path.Combine(fromEnvironment, "bin");
            }

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA GPU Computing Toolkit",
                "CUDA");
            if (!Directory.Exists(root))
            {
                return null;
            }

            string best = null;
            Version bestVersion = null;
            foreach (var dir in Directory.EnumerateDirectories(root, "v12*"))
            {
                if (!HasCuda12Runtime(dir))
                {
                    continue;
                }

                var version = ParseVersion(Path.GetFileName(dir));
                if (version == null)
                {
                    continue;
                }

                if (bestVersion == null || version > bestVersion)
                {
                    bestVersion = version;
                    best = Path.Combine(dir, "bin");
                }
            }

            return best;
        }

        private static string FindCudnnBin(string cudaBin)
        {
            if (File.Exists(Path.Combine(cudaBin, "cudnn64_9.dll")))
            {
                return cudaBin;
            }

            var cudnnRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA",
                "CUDNN");
            if (!Directory.Exists(cudnnRoot))
            {
                return null;
            }

            foreach (var dll in Directory.EnumerateFiles(cudnnRoot, "cudnn64_9.dll", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(dll);
                if (!string.IsNullOrWhiteSpace(dir) &&
                    dir.IndexOf($"{Path.DirectorySeparatorChar}12.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return dir;
                }
            }

            return null;
        }

        private static bool HasCuda12Runtime(string cudaRoot)
        {
            return !string.IsNullOrWhiteSpace(cudaRoot)
                && File.Exists(Path.Combine(cudaRoot, "bin", "cudart64_12.dll"));
        }

        private static Version ParseVersion(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var text = name.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? name.Substring(1) : name;
            return Version.TryParse(text, out var version) ? version : null;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "volume";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            for (var i = 0; i < name.Length; ++i)
            {
                var c = name[i];
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            return builder.ToString();
        }

        private static string Tail(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var trimmed = text.Trim();
            return trimmed.Length <= maxChars ? trimmed : trimmed.Substring(trimmed.Length - maxChars);
        }
    }

    public sealed class HeadSegmentationResult
    {
        public bool Success { get; private set; }
        public string Key { get; private set; } = string.Empty;
        public string Error { get; private set; } = string.Empty;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Depth { get; private set; }
        public byte[] Voxels { get; private set; }
        public SimpleITKImageInfo ImageInfo { get; private set; }
        public byte LabelMaximum { get; private set; }
        public string ColorMapFileName { get; private set; } = VolumeNetworkProtocol.DefaultSegmentationColorMapFileName;
        public string SourceLabel { get; private set; } = string.Empty;

        public static HeadSegmentationResult Fail(string error)
        {
            return new HeadSegmentationResult
            {
                Success = false,
                Error = error ?? string.Empty
            };
        }

        public static HeadSegmentationResult Ok(
            string key,
            int width,
            int height,
            int depth,
            byte[] voxels,
            SimpleITKImageInfo imageInfo,
            byte labelMaximum,
            string colorMapFileName,
            string sourceLabel)
        {
            return new HeadSegmentationResult
            {
                Success = true,
                Key = key ?? string.Empty,
                Width = width,
                Height = height,
                Depth = depth,
                Voxels = voxels,
                ImageInfo = imageInfo,
                LabelMaximum = labelMaximum,
                ColorMapFileName = string.IsNullOrWhiteSpace(colorMapFileName)
                    ? VolumeNetworkProtocol.DefaultSegmentationColorMapFileName
                    : colorMapFileName,
                SourceLabel = sourceLabel ?? string.Empty
            };
        }

        public SimpleITKLabelVolumeData CopyLabelVolume()
        {
            return new SimpleITKLabelVolumeData
            {
                Width = Width,
                Height = Height,
                Depth = Depth,
                Voxels = Voxels == null ? Array.Empty<byte>() : (byte[])Voxels.Clone(),
                ImageInfo = ImageInfo,
                LabelMaximum = LabelMaximum
            };
        }
    }
}
