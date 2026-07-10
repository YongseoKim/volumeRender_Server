using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SimpleITKUnity
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class SimpleITKVolumeLoader : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private SimpleITKVolumeRenderer targetRenderer;
        [SerializeField] private SimpleITKProcessingController processingController;
        [SerializeField] private SimpleITKOrthogonalViewController viewController;

        [Header("Volume Source")]
        [SerializeField] private string sampleFileName = string.Empty;

        [Header("Segmentation Source")]
        [SerializeField] private string segmentationSampleFileName = string.Empty;
        [SerializeField] private string segmentationColorMapFileName = SimpleITKSegmentationColorMap.DefaultFileName;

        [Header("Inference")]
        [SerializeField] private SegmentationInferenceBehaviour segmentationInference;

        [Header("Loaded Volume Info")]
        [SerializeField] private int loadedWidth;
        [SerializeField] private int loadedHeight;
        [SerializeField] private int loadedDepth;
        [SerializeField] private float loadedSourceMin;
        [SerializeField] private float loadedSourceMax;

        [SerializeField, HideInInspector] private bool _editorAutoLoadAttempted;
        [SerializeField, HideInInspector] private string _lastEditorAutoLoadPath = string.Empty;
        [SerializeField, HideInInspector] private string _lastEditorAutoLoadSegmentationPath = string.Empty;
        [SerializeField, HideInInspector] private string _lastGeneratedSegmentationPath = string.Empty;
        [SerializeField, HideInInspector] private bool _importedLegacySettings;

        // ── Async Load State ─────────────────────────────────────────────────────
        private CancellationTokenSource _loadCts;
        private int _loadRequestVersion;

        /// <summary>True while an asynchronous load is in progress.</summary>
        public bool IsLoading { get; private set; }

        /// <summary>The most recent load error. Empty on success.</summary>
        public string LastLoadError { get; private set; } = string.Empty;

        private void Reset()
        {
            ResolveTargets();
            ImportLegacySourceSettingsIfNeeded();
        }

        private void OnEnable()
        {
            ResolveTargets();
            ImportLegacySourceSettingsIfNeeded();
            TryAutoLoadInEditor();
        }

        private void Start()
        {
            // Volume loads only after the user picks a sample (e.g. File Btn -> LoadSampleFile).
        }

        private void Update()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                ResolveTargets();
                if (targetRenderer != null && targetRenderer.GetVolumeTexture() == null)
                {
                    TryAutoLoadInEditor();
                }
            }
#endif
        }

        private void OnValidate()
        {
            ResolveTargets();
            segmentationColorMapFileName = GetConfiguredSegmentationColorMapFileName();
            if (!Application.isPlaying)
            {
                TryAutoLoadInEditor();
            }
        }

        private void OnDisable()
        {
            _editorAutoLoadAttempted = false;
            _lastEditorAutoLoadSegmentationPath = string.Empty;
            CancelPendingLoad();
        }

        public void SetTargetRenderer(SimpleITKVolumeRenderer renderer)
        {
            if (targetRenderer == renderer)
            {
                return;
            }

            targetRenderer = renderer;
        }

        public bool TryGetAssignedProcessingController(out SimpleITKProcessingController controller)
        {
            ResolveTargets();
            controller = processingController;
            return controller != null;
        }

        public string GetSegmentationColorMapFileName()
        {
            return GetConfiguredSegmentationColorMapFileName();
        }

        /// <summary>
        /// Loads the configured volume.
        /// Runs asynchronously in Play mode to avoid blocking the main thread.
        /// Runs synchronously in the Editor so the Scene view updates immediately.
        /// </summary>
        public void LoadVolume()
        {
            if (!HasVolumeSource)
            {
                return;
            }

            if (Application.isPlaying)
            {
                _ = LoadVolumeAsync();
                return;
            }

            // Keep the synchronous path in the Editor for immediate Scene view updates.
            LoadVolumeSync();
        }

        /// <summary>
        /// Sets <see cref="sampleFileName"/> (relative to StreamingAssets/Sample) and reloads the volume.
        /// Also assigns <see cref="segmentationSampleFileName"/> to the matching {name}_seg.nrrd entry
        /// and picks a segmentation color map for the sample type.
        /// Segmentation I/O runs only after this call (user click), not at Play start.
        /// </summary>
        public void LoadSampleFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                Debug.LogWarning("SimpleITKVolumeLoader: LoadSampleFile called with an empty file name.");
                return;
            }

            sampleFileName = fileName.Replace('\\', '/').Trim().TrimStart('/');
            AssignSegmentationSourceForSample(sampleFileName);
            LoadVolume();
        }

        public string SampleFileName => sampleFileName;

        /// <summary>True when <see cref="sampleFileName"/> is set and a load may be attempted.</summary>
        public bool HasVolumeSource => !string.IsNullOrWhiteSpace(sampleFileName);

        /// <summary>
        /// Loads the configured volume asynchronously.
        /// Cancels any in-flight request before starting a new one.
        /// </summary>
        public async Task LoadVolumeAsync()
        {
            // Cancel the previous request before starting a new one.
            CancelPendingLoad(clearLoadingState: false);
            var requestVersion = unchecked(++_loadRequestVersion);
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            ResolveTargets();
            if (targetRenderer == null)
            {
                Debug.LogError("SimpleITKVolumeLoader: targetRenderer is not assigned.");
                return;
            }

            if (!TryResolveVolumePath(out var resolvedPath, out var resolveError))
            {
                if (!string.IsNullOrWhiteSpace(resolveError))
                {
                    LastLoadError = resolveError;
                }
                return;
            }

            var readablePath = resolvedPath;
            var prepareResult = await TryPrepareReadablePathAsync(resolvedPath, sampleFileName, token);
            if (!prepareResult.Success)
            {
                var prepareError = prepareResult.Error;
                if (!string.IsNullOrWhiteSpace(prepareError))
                {
                    LastLoadError = prepareError;
                    Debug.LogError($"SimpleITKVolumeLoader: failed to prepare runtime-readable path: {prepareError}");
                }
                return;
            }
            readablePath = prepareResult.ReadablePath;

            var resolvedProcessing = ResolveProcessingController();
            var processingOptions = resolvedProcessing != null && resolvedProcessing.ShouldProcessOnLoad()
                ? resolvedProcessing.BuildProcessingOptions()
                : SimpleITKVolumeProcessingOptions.Disabled;

            IsLoading = true;
            LastLoadError = string.Empty;

            try
            {
                // Run I/O, processing, and normalization on a background thread.
                var (success, volumeData, error) =
                    await SimpleITKNative.TryReadVolumeFloat32NormalizedAsync(
                        readablePath, processingOptions, token);

                // Re-check cancellation because the object may have been destroyed while awaiting.
                if (token.IsCancellationRequested || this == null || !isActiveAndEnabled)
                {
                    return;
                }

                if (!success)
                {
                    LastLoadError = error;
                    Debug.LogError($"SimpleITKVolumeLoader: failed to load volume: {error}");
                    return;
                }

                viewController?.AutoFitRoiFromData(volumeData);

                var applied = targetRenderer.UsesSpreadVolumeTextureUpload
                    ? await targetRenderer.ApplyVolumeDataAsync(volumeData, resolvedPath, token)
                    : targetRenderer.ApplyVolumeData(volumeData, resolvedPath);

                if (applied)
                {
                    loadedWidth = volumeData.Width;
                    loadedHeight = volumeData.Height;
                    loadedDepth = volumeData.Depth;
                    loadedSourceMin = volumeData.SourceMinimum;
                    loadedSourceMax = volumeData.SourceMaximum;
                    _editorAutoLoadAttempted = true;
                    _lastEditorAutoLoadPath = resolvedPath;
                    viewController?.NotifyVolumeDataApplied();

                    // Apply segmentation as soon as the volume texture exists. Do not wait for the
                    // deferred 3D-mesh frame gate — that wait can be cancelled and previously skipped seg load.
                    await ApplyConfiguredSegmentationAfterVolumeLoadAsync(token, logFailures: true);

                    if (targetRenderer.IsDeferringVolumeMeshRendering)
                    {
                        await CompleteVolumeLoadAfterSlicesAsync(token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation. Exit quietly without logging.
            }
            catch (Exception ex)
            {
                if (this != null)
                {
                    LastLoadError = ex.Message;
                    Debug.LogError($"SimpleITKVolumeLoader: unexpected error during async load: {ex.Message}");
                }
            }
            finally
            {
                if (this != null && requestVersion == _loadRequestVersion)
                {
                    IsLoading = false;
                }
            }
        }

        /// <summary>Synchronous load path used by the Editor.</summary>
        private void LoadVolumeSync()
        {
            try
            {
                LoadVolumeSyncInternal();
            }
            catch (System.DllNotFoundException ex)
            {
                // Missing ITK dylibs. Mark as attempted because retrying will not help.
                _editorAutoLoadAttempted = true;
                Debug.LogError($"SimpleITKVolumeLoader: native plugin load failed. " +
                               $"Ensure all ITK dylibs are in Assets/Plugins/macOS/.\n{ex.Message}");
            }
        }

        private void LoadVolumeSyncInternal()
        {
            ResolveTargets();
            if (targetRenderer == null)
            {
                Debug.LogError("SimpleITKVolumeLoader: targetRenderer is not assigned.");
                return;
            }

            if (!TryResolveVolumePath(out var resolvedPath, out var resolveError))
            {
                return;
            }

            var resolvedProcessing = ResolveProcessingController();
            var useProcessing = resolvedProcessing != null && resolvedProcessing.ShouldProcessOnLoad();

            SimpleITKVolumeData volumeData;
            string error;
            var loaded = useProcessing
                ? resolvedProcessing.TryReadVolumeData(resolvedPath, out volumeData, out error)
                : SimpleITKNative.TryReadVolumeFloat32Normalized(resolvedPath, out volumeData, out error);

            if (!loaded)
            {
                Debug.LogError($"SimpleITKVolumeLoader: failed to load volume: {error}");
                return;
            }

            viewController?.AutoFitRoiFromData(volumeData);

            if (targetRenderer.ApplyVolumeData(volumeData, resolvedPath))
            {
                loadedWidth = volumeData.Width;
                loadedHeight = volumeData.Height;
                loadedDepth = volumeData.Depth;
                loadedSourceMin = volumeData.SourceMinimum;
                loadedSourceMax = volumeData.SourceMaximum;
                _editorAutoLoadAttempted = true;
                _lastEditorAutoLoadPath = resolvedPath;
                viewController?.NotifyVolumeDataApplied();
                ApplyConfiguredSegmentationAfterVolumeLoad();
                _lastEditorAutoLoadSegmentationPath = GetConfiguredSegmentationTrackingPath();
            }
        }

        private void ApplyConfiguredSegmentationAfterVolumeLoad(bool logFailures = false)
        {
            if (!TryResolveConfiguredSegmentationPathForLoad(logFailures, out var resolvedPath))
            {
                return;
            }

            TryApplyConfiguredSegmentationPath(resolvedPath, logFailures);
        }

        private async Task ApplyConfiguredSegmentationAfterVolumeLoadAsync(CancellationToken token, bool logFailures = false)
        {
            if (token.IsCancellationRequested || this == null || !isActiveAndEnabled)
            {
                LogSegmentationWarning(logFailures, "skipped segmentation overlay load", "load was cancelled or loader disabled");
                return;
            }

            if (!TryResolveConfiguredSegmentationPathForLoad(logFailures, out var resolvedPath))
            {
                return;
            }

            if (logFailures)
            {
                Debug.Log($"SimpleITKVolumeLoader: resolving segmentation overlay '{resolvedPath}'.");
            }

            var readablePath = resolvedPath;
            var prepareResult = await TryPrepareReadablePathAsync(resolvedPath, segmentationSampleFileName, token);
            if (!prepareResult.Success)
            {
                LogSegmentationWarning(
                    logFailures,
                    "failed to prepare runtime-readable segmentation path",
                    prepareResult.Error);
                ClearConfiguredSegmentation();
                return;
            }

            if (token.IsCancellationRequested || this == null || !isActiveAndEnabled)
            {
                LogSegmentationWarning(logFailures, "skipped segmentation overlay load", "load was cancelled after path prepare");
                return;
            }

            readablePath = prepareResult.ReadablePath;
            if (!SimpleITKNative.TryReadLabelVolumeUInt8(readablePath, out var segmentationData, out var readError))
            {
                LogSegmentationWarning(logFailures, "failed to read segmentation overlay", readError);
                ClearConfiguredSegmentation();
                return;
            }

            if (logFailures)
            {
                Debug.Log(
                    $"SimpleITKVolumeLoader: read segmentation overlay '{readablePath}' " +
                    $"size=({segmentationData.Width},{segmentationData.Height},{segmentationData.Depth}), " +
                    $"volume=({loadedWidth},{loadedHeight},{loadedDepth}), labels<={segmentationData.LabelMaximum}.");
            }

            if (TryApplyConfiguredSegmentationData(segmentationData, readablePath, logFailures))
            {
                if (logFailures)
                {
                    Debug.Log($"SimpleITKVolumeLoader: applied segmentation overlay '{readablePath}'.");
                    if (segmentationData.LabelMaximum == 0)
                    {
                        Debug.LogWarning(
                            $"SimpleITKVolumeLoader: segmentation overlay '{readablePath}' contains only background " +
                            "(label 0). No colored regions will appear on A/B/C until a non-empty _seg.nrrd is generated.");
                    }
                }
            }
        }

        private void CancelPendingLoad(bool clearLoadingState = true)
        {
            if (_loadCts == null)
            {
                if (clearLoadingState)
                {
                    IsLoading = false;
                }
                return;
            }

            _loadCts.Cancel();
            _loadCts.Dispose();
            _loadCts = null;
            if (clearLoadingState)
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Waits for ABC slice cameras to render into RawImage RTs, then enables 3D volume raymarching.
        /// Segmentation overlay is applied earlier (immediately after volume upload).
        /// </summary>
        private async Task CompleteVolumeLoadAfterSlicesAsync(CancellationToken token)
        {
            await WaitForNextFrameAsync(token);
            await WaitForEndOfFrameAsync(token);

            if (token.IsCancellationRequested || this == null || !isActiveAndEnabled)
            {
                return;
            }

            targetRenderer?.EnableVolumeMeshRendering();
        }

        private static async Task WaitForNextFrameAsync(CancellationToken token)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
        }

        private async Task WaitForEndOfFrameAsync(CancellationToken token)
        {
            var completed = false;
            StartCoroutine(WaitForEndOfFrameCoroutine(() => completed = true));
            while (!completed)
            {
                if (token.IsCancellationRequested || this == null)
                {
                    token.ThrowIfCancellationRequested();
                }

                await Task.Yield();
            }

            token.ThrowIfCancellationRequested();
        }

        private static IEnumerator WaitForEndOfFrameCoroutine(Action onComplete)
        {
            yield return new WaitForEndOfFrame();
            onComplete?.Invoke();
        }

        public bool TryResolveVolumePath(out string resolvedPath, out string error)
        {
            resolvedPath = ResolveVolumePath();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                error = "sampleFileName is empty.";
                return false;
            }

            if (IsRemotePath(resolvedPath))
            {
                if (!Application.isPlaying)
                {
                    error = $"StreamingAssets URL path is only supported at runtime: {resolvedPath}";
                    return false;
                }

                if (!TryNormalizeRelativePath(sampleFileName, out _, out var normalizeError))
                {
                    error = normalizeError;
                    return false;
                }

                return true;
            }

            if (!PathExists(resolvedPath))
            {
                error = $"volume path does not exist: {resolvedPath}";
                return false;
            }

            return true;
        }

        public bool TryResolveSegmentationPath(out string resolvedPath, out string error)
        {
            resolvedPath = ResolveSegmentationPath();
            error = string.Empty;

            if (!ShouldLoadConfiguredSegmentation())
            {
                error = "segmentationSampleFileName is empty.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                error = "segmentationSampleFileName is empty.";
                return false;
            }

            if (IsRemotePath(resolvedPath))
            {
                if (!Application.isPlaying)
                {
                    error = $"StreamingAssets URL path is only supported at runtime: {resolvedPath}";
                    return false;
                }

                if (!TryNormalizeRelativePath(segmentationSampleFileName, out _, out var normalizeError))
                {
                    error = normalizeError;
                    return false;
                }

                return true;
            }

            if (!File.Exists(resolvedPath))
            {
                error = $"segmentation path does not exist: {resolvedPath}";
                return false;
            }

            return true;
        }

        [ContextMenu("Generate Segmentation Volume")]
        public void GenerateSegmentationVolume()
        {
#if UNITY_EDITOR
            if (Application.isPlaying)
            {
                Debug.LogError("SimpleITKVolumeLoader: segmentation volume generation is only supported before entering Play mode.");
                return;
            }

            try
            {
                ReportSegmentationGenerationProgress(0.02f, "Scanning Sample volumes (DICOM / NRRD / MHA)...");
                ResolveTargets();

                if (segmentationInference == null)
                {
                    segmentationInference = ResolveSegmentationInference();
                }

                if (segmentationInference == null)
                {
                    Debug.LogError("SimpleITKVolumeLoader: Segmentation inference component is not assigned.");
                    return;
                }

                var sourcePaths = CollectSegmentationSourcePathsUnderSample();
                if (sourcePaths.Count == 0)
                {
                    Debug.LogError(
                        "SimpleITKVolumeLoader: no segmentable volumes found under StreamingAssets/Sample. " +
                        "Expected top-level DICOM series folders (.dcm/.dicom/.ima) or volume files (.nrrd/.mha).");
                    return;
                }

                if (!TryEnsureSegmentedDirectory(out var segmentedDirectory, out var segmentedDirError))
                {
                    Debug.LogError($"SimpleITKVolumeLoader: {segmentedDirError}");
                    return;
                }

                var successCount = 0;
                var failureCount = 0;
                string lastOutputPath = null;
                string lastSummary = string.Empty;

                for (var i = 0; i < sourcePaths.Count; ++i)
                {
                    var sourcePath = sourcePaths[i];
                    var sourceLeaf = Path.GetFileName(
                        sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    var sourceStem = SimpleITKPathUtility.GetSampleSegmentationStem(sourceLeaf);
                    if (string.IsNullOrWhiteSpace(sourceStem))
                    {
                        sourceStem = sourceLeaf;
                    }

                    var seriesProgressBase = (float)i / sourcePaths.Count;
                    var seriesProgressSpan = 1f / sourcePaths.Count;

                    ReportSegmentationGenerationProgress(
                        seriesProgressBase + (seriesProgressSpan * 0.05f),
                        $"[{i + 1}/{sourcePaths.Count}] Inferring {sourceLeaf}...");

                    Action<float, string> seriesProgress = (progress, status) =>
                    {
                        var clamped = Mathf.Clamp01(progress);
                        var mapped = seriesProgressBase + (seriesProgressSpan * (0.05f + (clamped * 0.75f)));
                        ReportSegmentationGenerationProgress(
                            mapped,
                            $"[{i + 1}/{sourcePaths.Count}] {sourceLeaf}: {status}");
                    };

                    if (!segmentationInference.TryPrepareSegmentationForVolume(
                            sourcePath,
                            out var segmentationData,
                            out var segmentationSummary,
                            out var segmentationError,
                            forceCpuBackend: true,
                            progress: seriesProgress))
                    {
                        failureCount++;
                        Debug.LogError(
                            $"SimpleITKVolumeLoader: failed to segment '{sourceLeaf}': {segmentationError}");
                        continue;
                    }

                    ReportSegmentationGenerationProgress(
                        seriesProgressBase + (seriesProgressSpan * 0.85f),
                        $"[{i + 1}/{sourcePaths.Count}] Saving {sourceLeaf}...");

                    var outputPath = BuildSegmentedOutputPath(segmentedDirectory, sourceStem);
                    if (!SimpleITKNative.TryWriteLabelVolumeUInt8(outputPath, segmentationData, useCompression: true, out var writeError))
                    {
                        failureCount++;
                        Debug.LogError(
                            $"SimpleITKVolumeLoader: failed to write segmentation '{outputPath}': {writeError}");
                        continue;
                    }

                    successCount++;
                    lastOutputPath = outputPath;
                    lastSummary = segmentationSummary;
                    Debug.Log(
                        $"SimpleITKVolumeLoader: generated segmentation '{outputPath}'. {segmentationSummary}");
                }

                if (successCount == 0)
                {
                    Debug.LogError(
                        $"SimpleITKVolumeLoader: batch segmentation finished with no successes " +
                        $"(failures={failureCount}).");
                    return;
                }

                ReportSegmentationGenerationProgress(0.92f, "Assigning last generated segmentation...");
                if (!string.IsNullOrWhiteSpace(lastOutputPath) &&
                    !TryAssignSegmentationSampleFileName(lastOutputPath, out var assignError))
                {
                    Debug.LogWarning(
                        $"SimpleITKVolumeLoader: segmentation files were generated but not assigned automatically: {assignError}");
                }

                _lastGeneratedSegmentationPath = lastOutputPath ?? string.Empty;
                EditorUtility.SetDirty(this);

                ReportSegmentationGenerationProgress(0.95f, "Refreshing assets...");
                AssetDatabase.Refresh();

                ReportSegmentationGenerationProgress(0.98f, "Applying last generated segmentation...");
                if (targetRenderer != null && targetRenderer.GetVolumeTexture() != null)
                {
                    ApplyConfiguredSegmentationAfterVolumeLoad(logFailures: true);
                }

                ReportSegmentationGenerationProgress(1.0f, "Done.");
                Debug.Log(
                    $"SimpleITKVolumeLoader: batch segmentation complete. " +
                    $"successes={successCount}, failures={failureCount}, outputDir='{segmentedDirectory}'. {lastSummary}");
            }
            finally
            {
                if (segmentationInference == null)
                {
                    segmentationInference = ResolveSegmentationInference();
                }

                segmentationInference?.ReleaseResources();
                EditorUtility.ClearProgressBar();
            }
#else
            Debug.LogError("SimpleITKVolumeLoader: segmentation volume generation is only available in the Unity Editor.");
#endif
        }

        private void ResolveTargets()
        {
            var resolvedRenderer = ResolveRendererInHierarchy();
            if (resolvedRenderer != null)
            {
                targetRenderer = resolvedRenderer;
            }

            processingController ??= ResolveProcessingController();
            segmentationInference ??= ResolveSegmentationInference();

            if (targetRenderer != null)
            {
                targetRenderer.SetVolumeLoader(this);
            }
        }

        private SimpleITKVolumeRenderer ResolveRendererInHierarchy()
        {
            // Resolve the target renderer from the current hierarchy, then fall back to the scene.
            return SimpleITKRendererLocator.FindInHierarchyOrScene(transform, gameObject.scene);
        }

        private SimpleITKProcessingController ResolveProcessingController()
        {
            if (processingController != null)
            {
                return processingController;
            }

            processingController = GetComponent<SimpleITKProcessingController>();
            if (processingController != null)
            {
                return processingController;
            }

            var current = transform;
            while (current != null)
            {
                processingController = current.GetComponentInChildren<SimpleITKProcessingController>(true);
                if (processingController != null)
                {
                    return processingController;
                }

                current = current.parent;
            }
            return null;
        }

        private SegmentationInferenceBehaviour ResolveSegmentationInference()
        {
            if (segmentationInference != null)
            {
                return segmentationInference;
            }

            segmentationInference = GetComponent<SegmentationInferenceBehaviour>();
            if (segmentationInference != null)
            {
                return segmentationInference;
            }

            var current = transform;
            while (current != null)
            {
                segmentationInference = current.GetComponentInChildren<SegmentationInferenceBehaviour>(true);
                if (segmentationInference != null)
                {
                    return segmentationInference;
                }

                current = current.parent;
            }

            return null;
        }

        private void ImportLegacySourceSettingsIfNeeded()
        {
            if (_importedLegacySettings || targetRenderer == null)
            {
                return;
            }

            if (!targetRenderer.TryGetLegacyVolumeSourceSettings(
                    out _,
                    out var legacySampleName,
                    out _,
                    out _,
                    out _,
                    out var legacyProcessingController))
            {
                _importedLegacySettings = true;
                return;
            }

            // Do not fill an intentionally empty sampleFileName from legacy settings.
            var shouldImport = sampleFileName == "MRHead.mhd" || sampleFileName == "Kitware_Head_and_Neck.mha";
            if (shouldImport)
            {
                if (!string.IsNullOrWhiteSpace(legacySampleName))
                {
                    sampleFileName = legacySampleName;
                }
            }

            if (processingController == null && legacyProcessingController != null)
            {
                processingController = legacyProcessingController;
            }

            var shouldImportSegmentationColorMap =
                string.IsNullOrWhiteSpace(segmentationColorMapFileName) ||
                string.Equals(
                    segmentationColorMapFileName,
                    SimpleITKSegmentationColorMap.DefaultFileName,
                    StringComparison.Ordinal);
            if (shouldImportSegmentationColorMap)
            {
                var legacySegmentationColorMapFileName = targetRenderer.GetLegacySegmentationColorMapFileName();
                if (!string.IsNullOrWhiteSpace(legacySegmentationColorMapFileName))
                {
                    segmentationColorMapFileName = legacySegmentationColorMapFileName;
                }
            }

            _importedLegacySettings = true;
        }

        private string GetConfiguredSegmentationColorMapFileName()
        {
            var trimmed = (segmentationColorMapFileName ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(trimmed)
                ? SimpleITKSegmentationColorMap.DefaultFileName
                : trimmed;
        }

        private string ResolveVolumePath()
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

        private string ResolveSegmentationPath()
        {
            if (TryResolveMatchingSegmentedPathForCurrentSample(out var matchedSegmentedPath))
            {
                return matchedSegmentedPath;
            }

            if (string.IsNullOrWhiteSpace(segmentationSampleFileName))
            {
                return string.Empty;
            }

            var normalizedEntry = segmentationSampleFileName.Replace('\\', '/').Trim().TrimStart('/');
            var fileName = Path.GetFileName(normalizedEntry);

            // Prefer StreamingAssets/Segmented for generated DICOM batch outputs.
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var segmentedPath = CombinePathPreservingUri(GetSegmentedDirectoryPath(), fileName);
                if (!IsRemotePath(segmentedPath) && PathExists(segmentedPath))
                {
                    return Path.GetFullPath(segmentedPath);
                }

                if (IsRemotePath(segmentedPath))
                {
                    return segmentedPath;
                }
            }

            var samplePath = CombinePathPreservingUri(GetSampleDirectoryPath(), normalizedEntry);
            if (IsRemotePath(samplePath))
            {
                return samplePath;
            }

            return Path.GetFullPath(samplePath);
        }

        private async Task<(bool Success, string ReadablePath, string Error)> TryPrepareReadablePathAsync(
            string resolvedPath,
            string sampleEntry,
            CancellationToken token)
        {
            if (!IsRemotePath(resolvedPath))
            {
                return (true, resolvedPath, string.Empty);
            }

            if (!Application.isPlaying)
            {
                return (false, string.Empty, $"StreamingAssets URL path is only supported at runtime: {resolvedPath}");
            }

            return await StageStreamingAssetsEntryAsync(sampleEntry, token);
        }

        private bool ShouldLoadConfiguredSegmentation()
        {
            if (TryResolveMatchingSegmentedPathForCurrentSample(out _))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(segmentationSampleFileName);
        }

        private string GetConfiguredSegmentationTrackingPath()
        {
            if (!ShouldLoadConfiguredSegmentation())
            {
                return string.Empty;
            }

            return TryResolveSegmentationPath(out var resolvedPath, out _)
                ? resolvedPath
                : string.Empty;
        }

        /// <summary>
        /// Assigns matching {stem}_seg.nrrd and a color map for the selected sample.
        /// </summary>
        private void AssignSegmentationSourceForSample(string sampleEntry)
        {
            segmentationSampleFileName = BuildMatchingSegmentationSampleFileName(sampleEntry);
            segmentationColorMapFileName = ResolveSegmentationColorMapFileNameForSample(sampleEntry);
        }

        /// <summary>
        /// Builds {sampleStem}_seg.nrrd for a selected sample (DICOM folder name or volume file stem).
        /// DICOM folders keep dots in the name (e.g. 20220714.CTA_source_seg.nrrd).
        /// </summary>
        private static string BuildMatchingSegmentationSampleFileName(string sampleEntry)
        {
            var sampleStem = SimpleITKPathUtility.GetSampleSegmentationStem(sampleEntry);
            return string.IsNullOrWhiteSpace(sampleStem)
                ? string.Empty
                : $"{sampleStem}_seg.nrrd";
        }

        /// <summary>
        /// BraTS samples use the BraTS palette; DICOM/UNet and other samples use UnetBrain.
        /// </summary>
        private static string ResolveSegmentationColorMapFileNameForSample(string sampleEntry)
        {
            var normalizedSample = (sampleEntry ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
            var sampleLeaf = Path.GetFileName(normalizedSample.TrimEnd('/'));
            if (!string.IsNullOrWhiteSpace(sampleLeaf) &&
                sampleLeaf.IndexOf("brats", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "BraTSSegmentationColorMap.txt";
            }

            return "UnetBrainSegmentationColorMap.txt";
        }

        /// <summary>
        /// Resolves StreamingAssets/Segmented/{sampleName}_seg.nrrd for the currently selected sample.
        /// </summary>
        private bool TryResolveMatchingSegmentedPathForCurrentSample(out string resolvedPath)
        {
            resolvedPath = string.Empty;

            var segmentedFileName = BuildMatchingSegmentationSampleFileName(sampleFileName);
            if (string.IsNullOrWhiteSpace(segmentedFileName))
            {
                return false;
            }

            var segmentedPath = CombinePathPreservingUri(GetSegmentedDirectoryPath(), segmentedFileName);
            if (IsRemotePath(segmentedPath))
            {
                // Auto-match currently requires a local StreamingAssets filesystem path.
                return false;
            }

            if (!File.Exists(segmentedPath))
            {
                return false;
            }

            resolvedPath = Path.GetFullPath(segmentedPath);
            return true;
        }

#if UNITY_EDITOR
        private static void ReportSegmentationGenerationProgress(float progress, string status)
        {
            EditorUtility.DisplayProgressBar(
                "Generate Segmented Volume",
                string.IsNullOrWhiteSpace(status) ? "Working..." : status,
                Mathf.Clamp01(progress));
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
#endif

        private string ResolveGeneratedSegmentationOutputPath(string resolvedVolumePath)
        {
            var defaultOutputPath = BuildGeneratedSegmentationOutputPath(resolvedVolumePath);
            if (!string.IsNullOrWhiteSpace(segmentationSampleFileName))
            {
                var assignedPath = ResolveSegmentationPath();
                if (!string.IsNullOrWhiteSpace(assignedPath) && !IsRemotePath(assignedPath))
                {
                    var normalizedAssignedPath = Path.GetFullPath(assignedPath);
                    var normalizedDefaultOutputPath = Path.GetFullPath(defaultOutputPath);
                    var normalizedLastGeneratedPath = string.IsNullOrWhiteSpace(_lastGeneratedSegmentationPath)
                        ? string.Empty
                        : Path.GetFullPath(_lastGeneratedSegmentationPath);

                    var matchesAutoGeneratedPath =
                        string.Equals(normalizedAssignedPath, normalizedDefaultOutputPath, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normalizedAssignedPath, normalizedLastGeneratedPath, StringComparison.OrdinalIgnoreCase);

                    return matchesAutoGeneratedPath ? defaultOutputPath : normalizedAssignedPath;
                }
            }

            return defaultOutputPath;
        }

        private string BuildGeneratedSegmentationOutputPath(string resolvedVolumePath)
        {
            var seriesName = SimpleITKPathUtility.GetSampleSegmentationStem(resolvedVolumePath);
            if (string.IsNullOrWhiteSpace(seriesName))
            {
                seriesName = Path.GetFileName(
                    resolvedVolumePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            if (string.IsNullOrWhiteSpace(seriesName))
            {
                seriesName = "volume";
            }

            if (!TryEnsureSegmentedDirectory(out var segmentedDirectory, out _))
            {
                // Fall back to Sample for legacy single-volume callers if Segmented cannot be created.
                var normalizedSampleEntry = (sampleFileName ?? string.Empty).Replace('\\', '/');
                var relativeDirectory = Path.GetDirectoryName(normalizedSampleEntry)?.Replace('\\', '/') ?? string.Empty;
                var legacyFileName = $"{seriesName}_seg.nrrd";
                var relativeOutputPath = CombineRelativePath(relativeDirectory, legacyFileName);
                return Path.GetFullPath(CombinePathPreservingUri(GetSampleDirectoryPath(), relativeOutputPath));
            }

            return BuildSegmentedOutputPath(segmentedDirectory, seriesName);
        }

        private static string BuildSegmentedOutputPath(string segmentedDirectory, string seriesName)
        {
            var safeName = SimpleITKPathUtility.GetSampleSegmentationStem(seriesName);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = string.IsNullOrWhiteSpace(seriesName) ? "volume" : seriesName.Trim();
            }

            var outputFileName = $"{safeName}_seg.nrrd";
            return Path.GetFullPath(Path.Combine(segmentedDirectory, outputFileName));
        }

        private static bool TryEnsureSegmentedDirectory(out string segmentedDirectory, out string error)
        {
            segmentedDirectory = string.Empty;
            error = string.Empty;

            var candidate = GetSegmentedDirectoryPath();
            if (IsRemotePath(candidate))
            {
                error = "StreamingAssets/Segmented is not a local filesystem path on this platform.";
                return false;
            }

            segmentedDirectory = Path.GetFullPath(candidate);
            try
            {
                if (!Directory.Exists(segmentedDirectory))
                {
                    Directory.CreateDirectory(segmentedDirectory);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to create StreamingAssets/Segmented directory '{segmentedDirectory}': {ex.Message}";
                segmentedDirectory = string.Empty;
                return false;
            }
        }

        /// <summary>
        /// Collects top-level DICOM series folders and volume files (.nrrd/.mha) under StreamingAssets/Sample.
        /// </summary>
        private static List<string> CollectSegmentationSourcePathsUnderSample()
        {
            var results = new List<string>();
            var sampleDirectory = GetSampleDirectoryPath();
            if (IsRemotePath(sampleDirectory) || !Directory.Exists(sampleDirectory))
            {
                return results;
            }

            foreach (var directoryPath in Directory.GetDirectories(sampleDirectory))
            {
                var directoryName = Path.GetFileName(directoryPath);
                if (string.IsNullOrWhiteSpace(directoryName) ||
                    directoryName.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!DirectoryContainsDicomFiles(directoryPath))
                {
                    continue;
                }

                results.Add(Path.GetFullPath(directoryPath));
            }

            foreach (var filePath in Directory.GetFiles(sampleDirectory))
            {
                if (!IsSegmentableVolumeFileName(filePath))
                {
                    continue;
                }

                results.Add(Path.GetFullPath(filePath));
            }

            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        private static bool IsSegmentableVolumeFileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.StartsWith(".", StringComparison.Ordinal) ||
                fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith("_seg.nrrd", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var extension = Path.GetExtension(fileName);
            return string.Equals(extension, ".nrrd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".mha", StringComparison.OrdinalIgnoreCase);
        }

        private static bool DirectoryContainsDicomFiles(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return false;
            }

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(directoryPath))
                {
                    if (IsDicomFileName(filePath))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }

        private static bool IsDicomFileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return string.Equals(extension, ".dcm", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".dicom", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".ima", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryAssignSegmentationSampleFileName(string outputPath, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                error = "outputPath is empty.";
                return false;
            }

            var fullOutputPath = Path.GetFullPath(outputPath);
            var segmentedDirectory = GetSegmentedDirectoryPath();
            if (!IsRemotePath(segmentedDirectory))
            {
                var fullSegmentedDirectory = Path.GetFullPath(segmentedDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullOutputPath.StartsWith(fullSegmentedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fullOutputPath, fullSegmentedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    segmentationSampleFileName = Path.GetFileName(fullOutputPath).Replace('\\', '/');
                    return true;
                }
            }

            var sampleDirectory = GetSampleDirectoryPath();
            if (IsRemotePath(sampleDirectory))
            {
                error = "Cannot assign a relative segmentation sample path when StreamingAssets is exposed as a runtime URL.";
                return false;
            }

            var fullSampleDirectory = Path.GetFullPath(sampleDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!fullOutputPath.StartsWith(fullSampleDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullOutputPath, fullSampleDirectory, StringComparison.OrdinalIgnoreCase))
            {
                error =
                    $"Generated segmentation path is outside StreamingAssets/Segmented and StreamingAssets/Sample: {fullOutputPath}";
                return false;
            }

            segmentationSampleFileName = fullOutputPath
                .Substring(fullSampleDirectory.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');

            return true;
        }

        private bool TryResolveConfiguredSegmentationPathForLoad(bool logFailures, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (targetRenderer == null)
            {
                LogSegmentationWarning(logFailures, "failed to resolve segmentation volume path", "targetRenderer is not assigned");
                return false;
            }

            if (!ShouldLoadConfiguredSegmentation())
            {
                LogSegmentationWarning(
                    logFailures,
                    "skipped segmentation overlay load",
                    string.IsNullOrWhiteSpace(segmentationSampleFileName)
                        ? "segmentationSampleFileName is empty and no matching Segmented/_seg.nrrd was found"
                        : $"segmentationSampleFileName='{segmentationSampleFileName}' but no readable path was found");
                ClearConfiguredSegmentation();
                return false;
            }

            if (TryResolveSegmentationPath(out resolvedPath, out var resolveError))
            {
                return true;
            }

            LogSegmentationWarning(logFailures, "failed to resolve segmentation volume path", resolveError);
            ClearConfiguredSegmentation();
            return false;
        }

        private bool TryApplyConfiguredSegmentationPath(string resolvedPath, bool logFailures)
        {
            if (targetRenderer == null)
            {
                LogSegmentationWarning(logFailures, "failed to load segmentation overlay", "targetRenderer is not assigned");
                return false;
            }

            if (targetRenderer.TryLoadSegmentationVolume(resolvedPath, out var loadError))
            {
                if (logFailures)
                {
                    Debug.Log($"SimpleITKVolumeLoader: applied segmentation overlay '{resolvedPath}'.");
                }

                NotifySegmentationLabelToggleUi();
                return true;
            }

            LogSegmentationWarning(logFailures, "failed to load segmentation overlay", loadError);
            ClearConfiguredSegmentation();
            return false;
        }

        private bool TryApplyConfiguredSegmentationData(
            SimpleITKLabelVolumeData segmentationData,
            string sourcePathOrLabel,
            bool logFailures)
        {
            if (targetRenderer == null)
            {
                LogSegmentationWarning(logFailures, "failed to apply segmentation overlay", "targetRenderer is not assigned");
                return false;
            }

            if (targetRenderer.TryApplySegmentationVolumeData(segmentationData, sourcePathOrLabel, out var applyError))
            {
                NotifySegmentationLabelToggleUi();
                return true;
            }

            LogSegmentationWarning(logFailures, "failed to apply segmentation overlay", applyError);
            ClearConfiguredSegmentation();
            return false;
        }

        private void ClearConfiguredSegmentation()
        {
            targetRenderer?.ClearSegmentationData();
            NotifySegmentationLabelToggleUi();
        }

        private static void NotifySegmentationLabelToggleUi()
        {
            SegmentationLabelToggleUI.RefreshAllInScene(force: true);
        }

        private static void LogSegmentationWarning(bool enabled, string operation, string detail)
        {
            if (!enabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(detail))
            {
                Debug.LogWarning($"SimpleITKVolumeLoader: {operation}.");
                return;
            }

            Debug.LogWarning($"SimpleITKVolumeLoader: {operation}: {detail}");
        }

        private async Task<(bool Success, string ReadablePath, string Error)> StageStreamingAssetsEntryAsync(
            string sampleEntry,
            CancellationToken token)
        {
            if (!TryNormalizeRelativePath(sampleEntry, out var normalizedEntry, out var normalizeError))
            {
                return (false, string.Empty, normalizeError);
            }

            var cacheRoot = Path.Combine(Application.persistentDataPath, "SimpleITKStreamingAssets", "Sample");
            Directory.CreateDirectory(cacheRoot);

            var looksLikeDirectory = string.IsNullOrEmpty(Path.GetExtension(normalizedEntry));
            if (looksLikeDirectory)
            {
                return await StageStreamingAssetsDirectoryAsync(normalizedEntry, cacheRoot, token);
            }

            return await StageStreamingAssetsFileAsync(normalizedEntry, cacheRoot, token);
        }

        private async Task<(bool Success, string ReadablePath, string Error)> StageStreamingAssetsFileAsync(
            string normalizedFileEntry,
            string cacheRoot,
            CancellationToken token)
        {
            var stageResult = await StageSingleRelativeEntryAsync(
                normalizedFileEntry,
                cacheRoot,
                token,
                required: true);

            if (!stageResult.Success)
            {
                return (false, string.Empty, stageResult.Error);
            }

            var localFilePath = stageResult.ReadablePath;
            if (string.Equals(Path.GetExtension(normalizedFileEntry), ".mhd", StringComparison.OrdinalIgnoreCase))
            {
                var directoryRelative = Path.GetDirectoryName(normalizedFileEntry)?.Replace('\\', '/') ?? string.Empty;
                if (TryReadMhdElementDataRelativePath(localFilePath, out var elementDataRelative))
                {
                    var sidecarRelative = CombineRelativePath(directoryRelative, elementDataRelative);
                    if (TryNormalizeRelativePath(sidecarRelative, out var normalizedSidecar, out var sidecarNormalizeError))
                    {
                        var sidecarResult = await StageSingleRelativeEntryAsync(
                            normalizedSidecar,
                            cacheRoot,
                            token,
                            required: true);
                        if (!sidecarResult.Success)
                        {
                            return (false, string.Empty, sidecarResult.Error);
                        }
                    }
                    else
                    {
                        return (false, string.Empty, sidecarNormalizeError);
                    }
                }
                else
                {
                    // If the sidecar name cannot be read from the header, try common raw/zraw names.
                    var stem = Path.GetFileNameWithoutExtension(normalizedFileEntry);
                    var optionalCandidates = new[]
                    {
                        CombineRelativePath(directoryRelative, $"{stem}.zraw"),
                        CombineRelativePath(directoryRelative, $"{stem}.raw")
                    };

                    for (var i = 0; i < optionalCandidates.Length; ++i)
                    {
                        if (!TryNormalizeRelativePath(optionalCandidates[i], out var normalizedCandidate, out _))
                        {
                            continue;
                        }

                        await StageSingleRelativeEntryAsync(
                            normalizedCandidate,
                            cacheRoot,
                            token,
                            required: false);
                    }
                }
            }

            return (true, localFilePath, string.Empty);
        }

        private async Task<(bool Success, string ReadablePath, string Error)> StageStreamingAssetsDirectoryAsync(
            string normalizedDirectoryEntry,
            string cacheRoot,
            CancellationToken token)
        {
            var localDirectoryPath = Path.Combine(
                cacheRoot,
                normalizedDirectoryEntry.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(localDirectoryPath);

            var manifestResult = await TryGetDirectoryManifestEntriesAsync(
                normalizedDirectoryEntry,
                localDirectoryPath,
                token);

            if (!manifestResult.Success)
            {
                var existingEntries = Directory
                    .GetFiles(localDirectoryPath, "*", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (existingEntries.Length > 0)
                {
                    return (true, localDirectoryPath, string.Empty);
                }

                return (false, string.Empty, manifestResult.Error);
            }

            for (var i = 0; i < manifestResult.Entries.Length; ++i)
            {
                var entry = manifestResult.Entries[i];
                var relativeFromSampleRoot = CombineRelativePath(normalizedDirectoryEntry, entry);
                if (!TryNormalizeRelativePath(relativeFromSampleRoot, out var normalizedFileEntry, out var normalizeError))
                {
                    return (false, string.Empty, normalizeError);
                }

                var fileStageResult = await StageSingleRelativeEntryAsync(
                    normalizedFileEntry,
                    cacheRoot,
                    token,
                    required: true);
                if (!fileStageResult.Success)
                {
                    return (false, string.Empty, fileStageResult.Error);
                }
            }

            return (true, localDirectoryPath, string.Empty);
        }

        private async Task<(bool Success, string[] Entries, string Error)> TryGetDirectoryManifestEntriesAsync(
            string normalizedDirectoryEntry,
            string localDirectoryPath,
            CancellationToken token)
        {
            var manifestCandidates = new[] { "__manifest.txt", "_manifest.txt", "manifest.txt", "files.txt" };

            for (var i = 0; i < manifestCandidates.Length; ++i)
            {
                var localManifestPath = Path.Combine(localDirectoryPath, manifestCandidates[i]);
                if (!File.Exists(localManifestPath))
                {
                    continue;
                }

                var localText = File.ReadAllText(localManifestPath);
                var localEntries = ParseManifestEntries(localText);
                if (localEntries.Length > 0)
                {
                    return (true, localEntries, string.Empty);
                }
            }

            for (var i = 0; i < manifestCandidates.Length; ++i)
            {
                token.ThrowIfCancellationRequested();
                var candidateName = manifestCandidates[i];
                var candidateRelativePath = CombineRelativePath(normalizedDirectoryEntry, candidateName);
                var localManifestPath = Path.Combine(localDirectoryPath, candidateName);
                var sourceUrl = CombinePathPreservingUri(GetSampleDirectoryPath(), candidateRelativePath);

                var downloadResult = await TryDownloadToPathAsync(sourceUrl, localManifestPath, token);
                if (!downloadResult.Success)
                {
                    continue;
                }

                var text = File.ReadAllText(localManifestPath);
                var entries = ParseManifestEntries(text);
                if (entries.Length > 0)
                {
                    return (true, entries, string.Empty);
                }
            }

            return (false, Array.Empty<string>(),
                $"No manifest found for sample directory '{normalizedDirectoryEntry}'. " +
                "Add one of: __manifest.txt, _manifest.txt, manifest.txt, files.txt.");
        }

        private async Task<(bool Success, string ReadablePath, string Error)> StageSingleRelativeEntryAsync(
            string normalizedEntry,
            string cacheRoot,
            CancellationToken token,
            bool required)
        {
            var localPath = Path.Combine(cacheRoot, normalizedEntry.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localPath))
            {
                return (true, localPath, string.Empty);
            }

            var sourceUrl = CombinePathPreservingUri(GetSampleDirectoryPath(), normalizedEntry);
            var downloadResult = await TryDownloadToPathAsync(sourceUrl, localPath, token);
            if (!downloadResult.Success)
            {
                if (!required)
                {
                    return (false, localPath, downloadResult.Error);
                }

                return (false, string.Empty,
                    $"Failed to stage StreamingAssets entry '{normalizedEntry}' from '{sourceUrl}': {downloadResult.Error}");
            }

            return (true, localPath, string.Empty);
        }

        private static async Task<(bool Success, string Error)> TryDownloadToPathAsync(
            string sourceUrl,
            string destinationPath,
            CancellationToken token)
        {
            var parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using var request = UnityWebRequest.Get(sourceUrl);
            request.downloadHandler = new DownloadHandlerBuffer();
            var operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                if (token.IsCancellationRequested)
                {
                    request.Abort();
                    token.ThrowIfCancellationRequested();
                }

                await Task.Yield();
            }

            token.ThrowIfCancellationRequested();

#if UNITY_2020_2_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
#else
            if (request.isHttpError || request.isNetworkError)
#endif
            {
                return (false, request.error);
            }

            var bytes = request.downloadHandler?.data;
            if (bytes == null || bytes.Length == 0)
            {
                return (false, "empty response payload");
            }

            File.WriteAllBytes(destinationPath, bytes);
            return (true, string.Empty);
        }

        private static string[] ParseManifestEntries(string manifestText)
        {
            if (string.IsNullOrWhiteSpace(manifestText))
            {
                return Array.Empty<string>();
            }

            var entries = manifestText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
                .Where(line => !line.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return entries;
        }

        private static bool TryReadMhdElementDataRelativePath(string mhdPath, out string relativePath)
        {
            relativePath = string.Empty;
            if (string.IsNullOrWhiteSpace(mhdPath) || !File.Exists(mhdPath))
            {
                return false;
            }

            var lines = File.ReadAllLines(mhdPath);
            for (var i = 0; i < lines.Length; ++i)
            {
                var line = lines[i]?.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!line.StartsWith("ElementDataFile", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex < 0 || separatorIndex >= line.Length - 1)
                {
                    continue;
                }

                var value = line.Substring(separatorIndex + 1).Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (value.StartsWith("LOCAL", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("LIST", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                relativePath = value.Replace('\\', '/');
                return true;
            }

            return false;
        }

        private static string CombineRelativePath(string parentRelative, string childRelative)
        {
            var normalizedChild = (childRelative ?? string.Empty)
                .Replace('\\', '/')
                .TrimStart('/');
            if (string.IsNullOrWhiteSpace(parentRelative))
            {
                return normalizedChild;
            }

            var normalizedParent = parentRelative.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(normalizedParent))
            {
                return normalizedChild;
            }

            if (string.IsNullOrEmpty(normalizedChild))
            {
                return normalizedParent;
            }

            return $"{normalizedParent}/{normalizedChild}";
        }

        private static bool TryNormalizeRelativePath(string input, out string normalized, out string error)
        {
            normalized = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(input))
            {
                error = "sampleFileName is empty.";
                return false;
            }

            var candidate = input.Replace('\\', '/').Trim();
            while (candidate.StartsWith("/", StringComparison.Ordinal))
            {
                candidate = candidate.Substring(1);
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                error = "sampleFileName is empty.";
                return false;
            }

            if (Path.IsPathRooted(candidate))
            {
                error = $"sampleFileName must be a relative path inside StreamingAssets/Sample: {input}";
                return false;
            }

            if (candidate.StartsWith("jar:", StringComparison.OrdinalIgnoreCase) ||
                candidate.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                error = $"sampleFileName must be a relative path inside StreamingAssets/Sample: {input}";
                return false;
            }

            if (candidate.IndexOf("../", StringComparison.Ordinal) >= 0 ||
                candidate.StartsWith("..", StringComparison.Ordinal) ||
                candidate.IndexOf("/..", StringComparison.Ordinal) >= 0)
            {
                error = $"sampleFileName contains invalid traversal segment: {input}";
                return false;
            }

            normalized = candidate.TrimEnd('/');
            return true;
        }

        private void TryAutoLoadInEditor()
        {
            if (Application.isPlaying)
            {
                return;
            }

            ResolveTargets();
            if (targetRenderer == null)
            {
                return;
            }

            var resolvedPath = ResolveVolumePath();
            var pathExists = !string.IsNullOrWhiteSpace(resolvedPath) &&
                             (IsRemotePath(resolvedPath) ? Application.isPlaying : PathExists(resolvedPath));
            var desiredSegmentationPath = GetConfiguredSegmentationTrackingPath();
            if (!pathExists)
            {
                _editorAutoLoadAttempted = false;
                if (targetRenderer.HasSegmentationTexture() &&
                    string.IsNullOrWhiteSpace(desiredSegmentationPath))
                {
                    targetRenderer.ClearSegmentationData();
                    _lastEditorAutoLoadSegmentationPath = string.Empty;
#if UNITY_EDITOR
                    SceneView.RepaintAll();
#endif
                }
                return;
            }

            var pathChanged = !string.Equals(_lastEditorAutoLoadPath, resolvedPath, StringComparison.OrdinalIgnoreCase);
            if (pathChanged)
            {
                _editorAutoLoadAttempted = false;
            }

            var segmentationChanged = !string.Equals(
                _lastEditorAutoLoadSegmentationPath,
                desiredSegmentationPath,
                StringComparison.OrdinalIgnoreCase);
            var segmentationMissingOrDisabled =
                !ShouldLoadConfiguredSegmentation() || string.IsNullOrWhiteSpace(desiredSegmentationPath);
            var segmentationNeedsRefresh =
                targetRenderer.GetVolumeTexture() != null &&
                ((!segmentationMissingOrDisabled && !targetRenderer.HasSegmentationTexture()) ||
                 (segmentationMissingOrDisabled && targetRenderer.HasSegmentationTexture()));

            if (targetRenderer.GetVolumeTexture() != null && !pathChanged)
            {
                if (!segmentationChanged && !segmentationNeedsRefresh)
                {
                    return;
                }

                ApplyConfiguredSegmentationAfterVolumeLoad(logFailures: true);
                _lastEditorAutoLoadSegmentationPath = desiredSegmentationPath;
#if UNITY_EDITOR
                SceneView.RepaintAll();
#endif
                return;
            }

            if (targetRenderer.GetVolumeTexture() == null && _editorAutoLoadAttempted)
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
            _lastEditorAutoLoadSegmentationPath = desiredSegmentationPath;
#if UNITY_EDITOR
            SceneView.RepaintAll();
#endif
        }

        private static string GetSampleDirectoryPath()
        {
            return CombinePathPreservingUri(Application.streamingAssetsPath, "Sample");
        }

        private static string GetSegmentedDirectoryPath()
        {
            return CombinePathPreservingUri(Application.streamingAssetsPath, "Segmented");
        }

        private static bool PathExists(string path)
        {
            return File.Exists(path) || Directory.Exists(path);
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
    }
}
