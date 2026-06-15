using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SimpleITKUnity.Editor
{
    /// <summary>
    /// Keeps manifest.txt files under StreamingAssets/Sample in sync with folder contents.
    /// </summary>
    internal sealed class StreamingAssetsSampleManifestGenerator : AssetPostprocessor
    {
        private const string SampleAssetPath = "Assets/StreamingAssets/Sample";
        private const string ManifestFileName = "manifest.txt";

        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly HashSet<string> ExcludedFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".meta"
        };
        private static readonly HashSet<string> ManifestFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "manifest.txt",
            "files.txt",
            "_manifest.txt",
            "__manifest.txt"
        };

        private static bool _isGenerating;

        [InitializeOnLoadMethod]
        private static void GenerateOnEditorLoad()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    return;
                }

                GenerateAllManifests(logResult: false);
            };
        }

        [MenuItem("Tools/StreamingAssets/Rebuild Sample Manifests")]
        private static void RebuildSampleManifestsMenu()
        {
            GenerateAllManifests(logResult: true);
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (_isGenerating)
            {
                return;
            }

            if (!DidSampleContentChange(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths))
            {
                return;
            }

            GenerateAllManifests(logResult: false);
        }

        private static bool DidSampleContentChange(
            IEnumerable<string> importedAssets,
            IEnumerable<string> deletedAssets,
            IEnumerable<string> movedAssets,
            IEnumerable<string> movedFromAssetPaths)
        {
            return HasRelevantPath(importedAssets) ||
                   HasRelevantPath(deletedAssets) ||
                   HasRelevantPath(movedAssets) ||
                   HasRelevantPath(movedFromAssetPaths);
        }

        private static bool HasRelevantPath(IEnumerable<string> assetPaths)
        {
            foreach (var assetPath in assetPaths)
            {
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    continue;
                }

                var normalized = assetPath.Replace('\\', '/');
                if (!normalized.StartsWith(SampleAssetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileName(normalized);
                if (ManifestFileNames.Contains(name))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static void GenerateAllManifests(bool logResult)
        {
            if (_isGenerating)
            {
                return;
            }

            try
            {
                _isGenerating = true;

                var sampleAbsolutePath = Path.GetFullPath(SampleAssetPath);
                if (!Directory.Exists(sampleAbsolutePath))
                {
                    return;
                }

                var directories = Directory.GetDirectories(sampleAbsolutePath, "*", SearchOption.AllDirectories)
                    .Append(sampleAbsolutePath)
                    .OrderBy(path => path, PathComparer)
                    .ToArray();

                var changedManifestCount = 0;
                for (var i = 0; i < directories.Length; i++)
                {
                    if (WriteManifestForDirectory(directories[i]))
                    {
                        changedManifestCount++;
                    }
                }

                if (changedManifestCount > 0)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                }

                if (logResult)
                {
                    Debug.Log(
                        $"StreamingAssetsSampleManifestGenerator: manifest regeneration complete. Updated {changedManifestCount} file(s).");
                }
            }
            finally
            {
                _isGenerating = false;
            }
        }

        private static bool WriteManifestForDirectory(string directoryAbsolutePath)
        {
            var directoryEntries = new List<string>();

            var files = Directory.GetFiles(directoryAbsolutePath, "*", SearchOption.TopDirectoryOnly);
            for (var i = 0; i < files.Length; i++)
            {
                var fileName = Path.GetFileName(files[i]);
                if (ShouldSkipFile(fileName))
                {
                    continue;
                }

                directoryEntries.Add(fileName.Replace('\\', '/'));
            }

            var subdirectories = Directory.GetDirectories(directoryAbsolutePath, "*", SearchOption.TopDirectoryOnly);
            for (var i = 0; i < subdirectories.Length; i++)
            {
                var directoryName = Path.GetFileName(subdirectories[i]);
                if (string.IsNullOrWhiteSpace(directoryName) || directoryName.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                directoryEntries.Add(directoryName.Replace('\\', '/'));
            }

            directoryEntries.Sort(StringComparer.OrdinalIgnoreCase);

            var manifestText = BuildManifestText(directoryAbsolutePath, directoryEntries);
            var manifestPath = Path.Combine(directoryAbsolutePath, ManifestFileName);

            if (File.Exists(manifestPath))
            {
                var existing = File.ReadAllText(manifestPath, Encoding.UTF8);
                if (string.Equals(existing, manifestText, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            File.WriteAllText(manifestPath, manifestText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
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

            var extension = Path.GetExtension(fileName);
            if (ExcludedFileExtensions.Contains(extension))
            {
                return true;
            }

            return false;
        }

        private static string BuildManifestText(string directoryAbsolutePath, IReadOnlyList<string> entries)
        {
            var directoryName = Path.GetFileName(directoryAbsolutePath);
            if (PathComparer.Equals(Path.GetFullPath(directoryAbsolutePath), Path.GetFullPath(SampleAssetPath)))
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
    }
}
