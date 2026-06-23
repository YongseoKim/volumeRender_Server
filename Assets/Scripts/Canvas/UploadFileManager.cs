using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleITKUnity
{
    public class UploadFileManager : MonoBehaviour
    {
        [SerializeField] private Button uploadButton;
        [SerializeField] private Button importFolderButton;
        /*
        [SerializeField] private InputField filePathInput;
        [SerializeField] private InputField fileNameInput;
        [SerializeField] private InputField fileSizeInput;
        [SerializeField] private InputField fileTypeInput;
        [SerializeField] private InputField fileDescriptionInput;
        [SerializeField] private InputField fileTagsInput;
        [SerializeField] private InputField fileCategoryInput;
        [SerializeField] private InputField fileSubcategoryInput;
        */
        [SerializeField] private FileBtnSpawner fileBtnSpawner;
        [SerializeField] private DataNetManager dataNetManager;

        private void Awake()
        {
            if (uploadButton != null)
            {
                uploadButton.onClick.AddListener(OnImportFileButtonClicked);
            }
            if (importFolderButton != null)
            {
                importFolderButton.onClick.AddListener(OnImportFolderButtonClicked);
            }
        }

        private void OnDestroy()
        {
            if (uploadButton != null)
            {
                uploadButton.onClick.RemoveListener(OnImportFileButtonClicked);
            }
            if (importFolderButton != null)
            {
                importFolderButton.onClick.RemoveListener(OnImportFolderButtonClicked);
            }
        }

        private void OnImportFileButtonClicked()
        {
            if (!NativeFileDialog.TryPickOpenFile("Import Sample File", out var selectedPath))
            {
                return;
            }

            ImportSelectedPath(selectedPath, isDirectory: false);
        }

        private void OnImportFolderButtonClicked()
        {
            if (!NativeFileDialog.TryPickOpenFolder("Import Sample Folder", out var selectedPath))
            {
                return;
            }

            ImportSelectedPath(selectedPath, isDirectory: true);
        }

        private void ImportSelectedPath(string selectedPath, bool isDirectory)
        {
            bool success;
            string sampleRelativePath;
            string error;

            if (isDirectory)
            {
                success = StreamingAssetsSampleImporter.TryImportDirectory(selectedPath, out sampleRelativePath, out error);
            }
            else
            {
                success = StreamingAssetsSampleImporter.TryImportFile(selectedPath, out sampleRelativePath, out error);
            }

            if (!success)
            {
                Debug.LogError($"UploadFileManager: failed to import '{selectedPath}': {error}");
                return;
            }

            var importedAbsolutePath = Path.Combine(
                StreamingAssetsSampleImporter.GetWritableSampleDirectoryPath(),
                sampleRelativePath.Replace('/', Path.DirectorySeparatorChar));

            PopulateMetadataFields(sampleRelativePath, importedAbsolutePath, isDirectory);
            RefreshSampleUi();

            Debug.Log($"UploadFileManager: imported '{selectedPath}' as StreamingAssets/Sample/{sampleRelativePath}.");
        }

        private void PopulateMetadataFields(string sampleRelativePath, string importedAbsolutePath, bool isDirectory)
        {
            /*
            if (filePathInput != null)
            {
                filePathInput.text = sampleRelativePath;
            }
            if (fileNameInput != null)
            {
                fileNameInput.text = Path.GetFileName(sampleRelativePath);
            }
            if (fileSizeInput != null)
            {
                fileSizeInput.text = FormatByteSize(GetImportedByteSize(importedAbsolutePath, isDirectory));
            }

            if (fileTypeInput != null)
            {
                fileTypeInput.text = isDirectory ? "folder" : Path.GetExtension(sampleRelativePath).TrimStart('.');
            }
            */
        }

        private static long GetImportedByteSize(string importedAbsolutePath, bool isDirectory)
        {
            if (isDirectory)
            {
                return GetDirectoryByteSize(importedAbsolutePath);
            }

            return File.Exists(importedAbsolutePath) ? new FileInfo(importedAbsolutePath).Length : 0L;
        }

        private static long GetDirectoryByteSize(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return 0L;
            }

            long totalBytes = 0L;
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                totalBytes += new FileInfo(filePath).Length;
            }

            return totalBytes;
        }

        private static string FormatByteSize(long bytes)
        {
            const long kb = 1024L;
            const long mb = kb * 1024L;
            const long gb = mb * 1024L;

            if (bytes >= gb)
            {
                return $"{bytes / (double)gb:0.##} GB";
            }

            if (bytes >= mb)
            {
                return $"{bytes / (double)mb:0.##} MB";
            }

            if (bytes >= kb)
            {
                return $"{bytes / (double)kb:0.##} KB";
            }

            return $"{bytes} B";
        }

        private void RefreshSampleUi()
        {
            if (fileBtnSpawner == null)
            {
                fileBtnSpawner = FindFirstObjectByType<FileBtnSpawner>();
            }

            fileBtnSpawner?.BuildFileButtons();

            if (dataNetManager == null)
            {
                dataNetManager = FindFirstObjectByType<DataNetManager>();
            }

            dataNetManager?.RefreshSampleEntries();
        }
    }
}
