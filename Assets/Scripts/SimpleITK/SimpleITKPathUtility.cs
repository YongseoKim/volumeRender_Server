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
    }
}
