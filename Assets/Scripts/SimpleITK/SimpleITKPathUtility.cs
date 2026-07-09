using System;
using System.IO;

namespace SimpleITKUnity
{
    internal static class SimpleITKPathUtility
    {
        public static string GetCompoundExtension(string path)
        {
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }

            if (fileName.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase))
            {
                return ".nii.gz";
            }

            return Path.GetExtension(fileName);
        }

        public static string GetFileStem(string path)
        {
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }

            if (fileName.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase))
            {
                return fileName.Substring(0, fileName.Length - ".nii.gz".Length);
            }

            return Path.GetFileNameWithoutExtension(fileName);
        }

        /// <summary>
        /// True when <paramref name="fileName"/> looks like a volume file with a known extension.
        /// DICOM series folders such as "20220714.CTA_source" return false so dots are preserved.
        /// </summary>
        public static bool IsKnownVolumeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            var leaf = Path.GetFileName(fileName.Trim().TrimEnd('/', '\\'));
            if (string.IsNullOrWhiteSpace(leaf))
            {
                return false;
            }

            if (leaf.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var extension = Path.GetExtension(leaf);
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            return string.Equals(extension, ".nrrd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".nhdr", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".mha", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".mhd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".nii", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".vtk", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".vti", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".dcm", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".dicom", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".ima", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sample stem used for matching Segmented/{stem}_seg.nrrd.
        /// Volume files strip known extensions; DICOM folders keep the full folder name.
        /// </summary>
        public static string GetSampleSegmentationStem(string sampleEntry)
        {
            if (string.IsNullOrWhiteSpace(sampleEntry))
            {
                return string.Empty;
            }

            var normalized = sampleEntry.Replace('\\', '/').Trim().TrimStart('/');
            var leaf = Path.GetFileName(normalized.TrimEnd('/'));
            if (string.IsNullOrWhiteSpace(leaf))
            {
                return string.Empty;
            }

            if (IsKnownVolumeFileName(leaf))
            {
                var stem = GetFileStem(leaf);
                return string.IsNullOrWhiteSpace(stem) ? leaf : stem;
            }

            return leaf;
        }
    }
}
