using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SimpleITKUnity
{
    /// <summary>
    /// Copies external files or folders into StreamingAssets/Sample and keeps manifest.txt in sync.
    /// </summary>
    public static class StreamingAssetsSampleImporter
    {
        private const string ManifestFileName = "manifest.txt";

        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly HashSet<string> ManifestFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "manifest.txt",
            "files.txt",
            "_manifest.txt",
            "__manifest.txt"
        };

        public static string GetWritableSampleDirectoryPath()
        {
#if UNITY_EDITOR
            return Path.GetFullPath(Path.Combine(Application.dataPath, "StreamingAssets", "Sample"));
#else
            return Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Sample"));
#endif
        }

        public static bool TryImportFile(string sourceFilePath, out string sampleRelativePath, out string error)
        {
            sampleRelativePath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                error = "Selected file does not exist.";
                return false;
            }

            try
            {
                var sampleDirectory = GetWritableSampleDirectoryPath();
                Directory.CreateDirectory(sampleDirectory);

                var fileName = Path.GetFileName(sourceFilePath);
                var destinationPath = GetUniqueDestinationPath(sampleDirectory, fileName);
                File.Copy(sourceFilePath, destinationPath, overwrite: false);

                if (string.Equals(Path.GetExtension(fileName), ".mhd", StringComparison.OrdinalIgnoreCase))
                {
                    CopyMhdSidecarFiles(sourceFilePath, sampleDirectory, Path.GetFileName(destinationPath));
                }

                RegenerateAllManifests();
                RefreshEditorAssets();

                sampleRelativePath = NormalizeRelativePath(Path.GetFileName(destinationPath));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryImportDirectory(string sourceDirectoryPath, out string sampleRelativePath, out string error)
        {
            sampleRelativePath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(sourceDirectoryPath) || !Directory.Exists(sourceDirectoryPath))
            {
                error = "Selected folder does not exist.";
                return false;
            }

            try
            {
                var sampleDirectory = GetWritableSampleDirectoryPath();
                Directory.CreateDirectory(sampleDirectory);

                var folderName = Path.GetFileName(Path.GetFullPath(sourceDirectoryPath));
                var destinationDirectory = GetUniqueDestinationDirectory(sampleDirectory, folderName);
                CopyDirectoryRecursive(sourceDirectoryPath, destinationDirectory);

                RegenerateAllManifests();
                RefreshEditorAssets();

                sampleRelativePath = NormalizeRelativePath(Path.GetFileName(destinationDirectory));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void RegenerateAllManifests()
        {
            var sampleDirectory = GetWritableSampleDirectoryPath();
            if (!Directory.Exists(sampleDirectory))
            {
                return;
            }

            var directories = Directory.GetDirectories(sampleDirectory, "*", SearchOption.AllDirectories)
                .Append(sampleDirectory)
                .OrderBy(path => path, PathComparer)
                .ToArray();

            for (var i = 0; i < directories.Length; i++)
            {
                WriteManifestForDirectory(directories[i], sampleDirectory);
            }
        }

        private static void CopyMhdSidecarFiles(string sourceMhdPath, string sampleDirectory, string importedMhdFileName)
        {
            var sourceDirectory = Path.GetDirectoryName(sourceMhdPath);
            if (string.IsNullOrWhiteSpace(sourceDirectory))
            {
                return;
            }

            var importedMhdPath = Path.Combine(sampleDirectory, importedMhdFileName);
            if (TryReadMhdElementDataFileName(importedMhdPath, out var elementDataFileName))
            {
                var sidecarSourcePath = Path.Combine(sourceDirectory, elementDataFileName);
                if (File.Exists(sidecarSourcePath))
                {
                    var sidecarDestinationPath = Path.Combine(sampleDirectory, elementDataFileName);
                    if (!File.Exists(sidecarDestinationPath))
                    {
                        File.Copy(sidecarSourcePath, sidecarDestinationPath, overwrite: false);
                    }
                }

                return;
            }

            var stem = Path.GetFileNameWithoutExtension(importedMhdFileName);
            var fallbackNames = new[] { $"{stem}.zraw", $"{stem}.raw" };
            for (var i = 0; i < fallbackNames.Length; i++)
            {
                var sidecarSourcePath = Path.Combine(sourceDirectory, fallbackNames[i]);
                if (!File.Exists(sidecarSourcePath))
                {
                    continue;
                }

                var sidecarDestinationPath = Path.Combine(sampleDirectory, fallbackNames[i]);
                if (!File.Exists(sidecarDestinationPath))
                {
                    File.Copy(sidecarSourcePath, sidecarDestinationPath, overwrite: false);
                }
            }
        }

        private static bool TryReadMhdElementDataFileName(string mhdPath, out string elementDataFileName)
        {
            elementDataFileName = string.Empty;
            if (!File.Exists(mhdPath))
            {
                return false;
            }

            foreach (var line in File.ReadLines(mhdPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("ElementDataFile", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var equalsIndex = trimmed.IndexOf('=');
                if (equalsIndex < 0)
                {
                    continue;
                }

                elementDataFileName = trimmed[(equalsIndex + 1)..].Trim();
                return !string.IsNullOrWhiteSpace(elementDataFileName);
            }

            return false;
        }

        private static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var sourceFile in Directory.GetFiles(sourceDirectory))
            {
                var fileName = Path.GetFileName(sourceFile);
                if (ShouldSkipFile(fileName))
                {
                    continue;
                }

                File.Copy(sourceFile, Path.Combine(destinationDirectory, fileName), overwrite: true);
            }

            foreach (var sourceSubdirectory in Directory.GetDirectories(sourceDirectory))
            {
                var directoryName = Path.GetFileName(sourceSubdirectory);
                if (string.IsNullOrWhiteSpace(directoryName) || directoryName.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                CopyDirectoryRecursive(sourceSubdirectory, Path.Combine(destinationDirectory, directoryName));
            }
        }

        private static void WriteManifestForDirectory(string directoryAbsolutePath, string sampleRootAbsolutePath)
        {
            var directoryEntries = new List<string>();

            foreach (var filePath in Directory.GetFiles(directoryAbsolutePath, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(filePath);
                if (ShouldSkipFile(fileName))
                {
                    continue;
                }

                directoryEntries.Add(fileName.Replace('\\', '/'));
            }

            foreach (var subdirectoryPath in Directory.GetDirectories(directoryAbsolutePath, "*", SearchOption.TopDirectoryOnly))
            {
                var directoryName = Path.GetFileName(subdirectoryPath);
                if (string.IsNullOrWhiteSpace(directoryName) || directoryName.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                directoryEntries.Add(directoryName.Replace('\\', '/'));
            }

            directoryEntries.Sort(StringComparer.OrdinalIgnoreCase);

            var manifestText = BuildManifestText(directoryAbsolutePath, sampleRootAbsolutePath, directoryEntries);
            var manifestPath = Path.Combine(directoryAbsolutePath, ManifestFileName);
            File.WriteAllText(manifestPath, manifestText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static bool ShouldSkipFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return true;
            }

            if (ManifestFileNames.Contains(fileName))
            {
                return true;
            }

            return string.Equals(Path.GetExtension(fileName), ".meta", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildManifestText(
            string directoryAbsolutePath,
            string sampleRootAbsolutePath,
            IReadOnlyList<string> entries)
        {
            var directoryName = Path.GetFileName(directoryAbsolutePath);
            if (PathComparer.Equals(Path.GetFullPath(directoryAbsolutePath), Path.GetFullPath(sampleRootAbsolutePath)))
            {
                directoryName = "Sample";
            }

            var builder = new StringBuilder();
            builder.Append("# Auto-generated. Do not edit manually.").Append('\n');
            builder.Append("# Directory: ").Append(directoryName).Append('\n');

            for (var i = 0; i < entries.Count; i++)
            {
                builder.Append(entries[i]).Append('\n');
            }

            return builder.ToString();
        }

        private static string GetUniqueDestinationPath(string directory, string fileName)
        {
            var destinationPath = Path.Combine(directory, fileName);
            if (!File.Exists(destinationPath))
            {
                return destinationPath;
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var i = 1; i < 1000; i++)
            {
                var candidateName = $"{stem}_{i}{extension}";
                var candidatePath = Path.Combine(directory, candidateName);
                if (!File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            throw new IOException($"Could not find a unique destination name for '{fileName}'.");
        }

        private static string GetUniqueDestinationDirectory(string parentDirectory, string directoryName)
        {
            var destinationDirectory = Path.Combine(parentDirectory, directoryName);
            if (!Directory.Exists(destinationDirectory))
            {
                return destinationDirectory;
            }

            for (var i = 1; i < 1000; i++)
            {
                var candidateName = $"{directoryName}_{i}";
                var candidatePath = Path.Combine(parentDirectory, candidateName);
                if (!Directory.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            throw new IOException($"Could not find a unique destination name for folder '{directoryName}'.");
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            return (relativePath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
        }

        private static void RefreshEditorAssets()
        {
#if UNITY_EDITOR
            UnityEditor.AssetDatabase.Refresh();
#endif
        }
    }
}
