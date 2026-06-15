using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace SimpleITKUnity
{
    public class FileBtnSpawner : MonoBehaviour
    {
        private static readonly HashSet<string> VolumeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mha",
            ".nrrd",
            ".mhd",
        };

        private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zraw",
            ".raw",
            ".meta",
            ".txt",
        };

        [SerializeField] private FileBtnManager fileBtnPrefab;
        [SerializeField] private Transform contentParent;
        [SerializeField] private SimpleITKVolumeLoader volumeLoader;
        [SerializeField] private bool buildOnStart = true;
        [SerializeField] private bool loadVolumeOnSelect = true;
        [SerializeField] private SampleFileSelectedEvent onSampleFileSelected;

        private readonly List<FileBtnManager> _spawnedButtons = new();
        private CancellationTokenSource _buildCts;

        [Serializable]
        public class SampleFileSelectedEvent : UnityEvent<string> { }

        private void Awake()
        {
            ResolveVolumeLoader();
        }

        private void Start()
        {
            if (buildOnStart)
            {
                BuildFileButtons();
            }
        }

        private void OnDestroy()
        {
            CancelPendingBuild();
        }

        [ContextMenu("Build File Buttons")]
        public void BuildFileButtons()
        {
            CancelPendingBuild();
            _buildCts = new CancellationTokenSource();
            _ = BuildFileButtonsAsync(_buildCts.Token);
        }

        private async Task BuildFileButtonsAsync(CancellationToken token)
        {
            if (fileBtnPrefab == null)
            {
                Debug.LogError("FileBtnSpawner: fileBtnPrefab is not assigned.");
                return;
            }

            if (contentParent == null)
            {
                Debug.LogError("FileBtnSpawner: contentParent is not assigned.");
                return;
            }

            ClearSpawnedButtons();

            try
            {
                var sampleEntries = await GetSampleEntriesAsync(token);
                token.ThrowIfCancellationRequested();

                for (var i = 0; i < sampleEntries.Count; i++)
                {
                    var sampleFileName = sampleEntries[i];
                    var fileBtn = Instantiate(fileBtnPrefab, contentParent);
                    fileBtn.Initialize(sampleFileName);
                    WireButtonClick(fileBtn);
                    _spawnedButtons.Add(fileBtn);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogError($"FileBtnSpawner: failed to build file buttons: {ex.Message}");
            }
        }

        private void WireButtonClick(FileBtnManager fileBtn)
        {
            var button = fileBtn.GetComponent<Button>();
            if (button == null)
            {
                Debug.LogWarning($"FileBtnSpawner: Button component missing on '{fileBtn.name}'.");
                return;
            }

            button.onClick.AddListener(() => HandleFileButtonClicked(fileBtn));
        }

        private void HandleFileButtonClicked(FileBtnManager fileBtn)
        {
            if (fileBtn == null)
            {
                return;
            }

            var sampleFileName = fileBtn.SampleFileName;
            onSampleFileSelected?.Invoke(sampleFileName);

            if (!loadVolumeOnSelect)
            {
                return;
            }

            ResolveVolumeLoader();
            if (volumeLoader == null)
            {
                Debug.LogWarning("FileBtnSpawner: SimpleITKVolumeLoader is not assigned.");
                return;
            }

            volumeLoader.LoadSampleFile(sampleFileName);
        }

        private void ResolveVolumeLoader()
        {
            if (volumeLoader != null)
            {
                return;
            }

            volumeLoader = FindFirstObjectByType<SimpleITKVolumeLoader>();
        }

        private void ClearSpawnedButtons()
        {
            for (var i = 0; i < _spawnedButtons.Count; i++)
            {
                if (_spawnedButtons[i] != null)
                {
                    Destroy(_spawnedButtons[i].gameObject);
                }
            }

            _spawnedButtons.Clear();
        }

        private void CancelPendingBuild()
        {
            if (_buildCts == null)
            {
                return;
            }

            _buildCts.Cancel();
            _buildCts.Dispose();
            _buildCts = null;
        }

        public static IReadOnlyList<string> GetSampleEntries()
        {
            var sampleDirectory = Path.Combine(Application.streamingAssetsPath, "Sample");
            if (!Directory.Exists(sampleDirectory))
            {
                Debug.LogWarning($"FileBtnSpawner: sample directory not found: {sampleDirectory}");
                return Array.Empty<string>();
            }

            var entries = new List<string>();

            foreach (var filePath in Directory.GetFiles(sampleDirectory))
            {
                var extension = Path.GetExtension(filePath);
                if (ExcludedExtensions.Contains(extension) || !VolumeExtensions.Contains(extension))
                {
                    continue;
                }

                var fileName = Path.GetFileName(filePath);
                if (fileName.EndsWith("_seg.nrrd", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(fileName.Replace('\\', '/'));
            }

            foreach (var directoryPath in Directory.GetDirectories(sampleDirectory))
            {
                var directoryName = Path.GetFileName(directoryPath);
                if (string.IsNullOrWhiteSpace(directoryName) || directoryName.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                entries.Add(directoryName.Replace('\\', '/'));
            }

            entries.Sort(StringComparer.OrdinalIgnoreCase);
            return entries;
        }

        public static async Task<IReadOnlyList<string>> GetSampleEntriesAsync(CancellationToken token = default)
        {
            var sampleDirectory = GetSampleDirectoryPath();
            if (!IsRemotePath(sampleDirectory))
            {
                return GetSampleEntries();
            }

            var manifestResult = await TryLoadSampleRootManifestEntriesAsync(token);
            if (!manifestResult.Success)
            {
                Debug.LogWarning($"FileBtnSpawner: failed to load sample manifest: {manifestResult.Error}");
                return Array.Empty<string>();
            }

            return FilterSampleEntries(manifestResult.Entries);
        }

        private static async Task<(bool Success, string[] Entries, string Error)> TryLoadSampleRootManifestEntriesAsync(
            CancellationToken token)
        {
            var manifestCandidates = new[] { "__manifest.txt", "_manifest.txt", "manifest.txt", "files.txt" };
            for (var i = 0; i < manifestCandidates.Length; i++)
            {
                token.ThrowIfCancellationRequested();

                var manifestUrl = CombinePathPreservingUri(GetSampleDirectoryPath(), manifestCandidates[i]);
                using var request = UnityWebRequest.Get(manifestUrl);
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

#if UNITY_2020_2_OR_NEWER
                if (request.result != UnityWebRequest.Result.Success)
#else
                if (request.isHttpError || request.isNetworkError)
#endif
                {
                    continue;
                }

                var entries = ParseManifestEntries(request.downloadHandler?.text);
                if (entries.Length > 0)
                {
                    return (true, entries, string.Empty);
                }
            }

            return (false, Array.Empty<string>(),
                "No root sample manifest found. Add files.txt under StreamingAssets/Sample.");
        }

        private static IReadOnlyList<string> FilterSampleEntries(IEnumerable<string> rawEntries)
        {
            var entries = rawEntries
                .Select(NormalizeSampleEntry)
                .Where(IsSelectableSampleEntry)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return entries;
        }

        private static string[] ParseManifestEntries(string manifestText)
        {
            if (string.IsNullOrWhiteSpace(manifestText))
            {
                return Array.Empty<string>();
            }

            return manifestText
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !line.StartsWith("#", StringComparison.Ordinal))
                .Where(line => !line.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private static string NormalizeSampleEntry(string entry)
        {
            return (entry ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        }

        private static bool IsSelectableSampleEntry(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry) ||
                entry.StartsWith(".", StringComparison.Ordinal) ||
                entry.Contains("../") ||
                entry.StartsWith("..", StringComparison.Ordinal) ||
                entry.Contains("/.."))
            {
                return false;
            }

            var fileName = Path.GetFileName(entry);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith(".", StringComparison.Ordinal))
            {
                return false;
            }

            var extension = Path.GetExtension(fileName);
            if (ExcludedExtensions.Contains(extension))
            {
                return false;
            }

            if (string.IsNullOrEmpty(extension))
            {
                return true;
            }

            return VolumeExtensions.Contains(extension) &&
                   !fileName.EndsWith("_seg.nrrd", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSampleDirectoryPath()
        {
            return CombinePathPreservingUri(Application.streamingAssetsPath, "Sample");
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

            return Path.Combine(basePath, normalizedRelative);
        }
    }
}
