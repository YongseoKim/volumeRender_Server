using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SimpleITKUnity
{
    /// <summary>
    /// Opens the OS file/folder picker from play mode (Editor or Windows standalone).
    /// </summary>
    public static class NativeFileDialog
    {
        private const string VolumeFilterLabel = "Volume Files";
        private const string VolumeFilterPattern = "*.nrrd;*.mha;*.mhd;*.dcm";
        private const string AllFilesLabel = "All Files";
        private const string AllFilesPattern = "*.*";

        public static bool TryPickOpenFile(string title, out string selectedPath)
        {
            selectedPath = string.Empty;

#if UNITY_EDITOR
            selectedPath = UnityEditor.EditorUtility.OpenFilePanel(
                string.IsNullOrWhiteSpace(title) ? "Select File" : title,
                string.Empty,
                string.Empty);
            return !string.IsNullOrWhiteSpace(selectedPath);
#elif UNITY_STANDALONE_WIN
            return TryPickOpenFileWindows(title, out selectedPath);
#else
            Debug.LogWarning("NativeFileDialog: Open file dialog is only supported in the Editor and Windows standalone builds.");
            return false;
#endif
        }

        public static bool TryPickOpenFolder(string title, out string selectedPath)
        {
            selectedPath = string.Empty;

#if UNITY_EDITOR
            selectedPath = UnityEditor.EditorUtility.OpenFolderPanel(
                string.IsNullOrWhiteSpace(title) ? "Select Folder" : title,
                string.Empty,
                string.Empty);
            return !string.IsNullOrWhiteSpace(selectedPath);
#elif UNITY_STANDALONE_WIN
            return TryPickOpenFolderWindows(title, out selectedPath);
#else
            Debug.LogWarning("NativeFileDialog: Open folder dialog is only supported in the Editor and Windows standalone builds.");
            return false;
#endif
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private const int MaxPath = 260;
        private const int FileOpenFlags =
            0x00080000 | // OFN_EXPLORER
            0x00001000 | // OFN_FILEMUSTEXIST
            0x00000800;  // OFN_PATHMUSTEXIST

        [DllImport("comdlg32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetOpenFileName(ref OpenFileName openFileName);

        [DllImport("shell32.dll")]
        private static extern IntPtr SHBrowseForFolder(ref BrowseInfo browseInfo);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern bool SHGetPathFromIDList(IntPtr itemIdList, StringBuilder path);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct OpenFileName
        {
            public int structSize;
            public IntPtr hwndOwner;
            public IntPtr instance;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public StringBuilder file;
            public int maxFile;
            public StringBuilder fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData;
            public IntPtr hook;
            public string templateName;
            public IntPtr reserved;
            public int reservedInt;
            public int flagsEx;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct BrowseInfo
        {
            public IntPtr hwndOwner;
            public IntPtr pidlRoot;
            public string displayName;
            public string title;
            public uint flags;
            public IntPtr callback;
            public IntPtr lParam;
            public int image;
        }

        private static bool TryPickOpenFileWindows(string title, out string selectedPath)
        {
            selectedPath = string.Empty;

            var filter = $"{VolumeFilterLabel}\0{VolumeFilterPattern}\0{AllFilesLabel}\0{AllFilesPattern}\0";

            var openFileName = new OpenFileName
            {
                structSize = Marshal.SizeOf<OpenFileName>(),
                filter = filter,
                file = new StringBuilder(MaxPath),
                maxFile = MaxPath,
                fileTitle = new StringBuilder(MaxPath),
                maxFileTitle = MaxPath,
                title = string.IsNullOrWhiteSpace(title) ? "Select File" : title,
                flags = FileOpenFlags,
                filterIndex = 1
            };

            if (!GetOpenFileName(ref openFileName))
            {
                return false;
            }

            selectedPath = openFileName.file.ToString();
            return !string.IsNullOrWhiteSpace(selectedPath);
        }

        private static bool TryPickOpenFolderWindows(string title, out string selectedPath)
        {
            selectedPath = string.Empty;

            var displayName = new StringBuilder(MaxPath);
            var browseInfo = new BrowseInfo
            {
                displayName = displayName.ToString(),
                title = string.IsNullOrWhiteSpace(title) ? "Select Folder" : title,
                flags = 0x00000040 // BIF_NEWDIALOGSTYLE
            };

            var itemIdList = SHBrowseForFolder(ref browseInfo);
            if (itemIdList == IntPtr.Zero)
            {
                return false;
            }

            var path = new StringBuilder(MaxPath);
            if (!SHGetPathFromIDList(itemIdList, path))
            {
                return false;
            }

            selectedPath = path.ToString();
            return !string.IsNullOrWhiteSpace(selectedPath);
        }
#endif
    }
}
