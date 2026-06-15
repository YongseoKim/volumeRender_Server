using System.IO;
using UnityEngine;
using TMPro;

namespace SimpleITKUnity
{
    public class FileBtnManager : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI filePathText;
        [Tooltip("Relative path inside StreamingAssets/Sample")]
        [SerializeField] private string sampleFileName;

        public string SampleFileName => sampleFileName;

        private void Awake()
        {
            ResolveFilePathText();
        }

        private void OnEnable()
        {
            ApplySampleFileNameToText();
        }

        /// <summary>
        /// Assigns a sample entry under StreamingAssets/Sample and updates the button label.
        /// </summary>
        public void Initialize(string fileName)
        {
            sampleFileName = NormalizeSampleFileName(fileName);
            ApplySampleFileNameToText();
        }

        public void SetFilePath(string filePath)
        {
            Initialize(filePath);
        }

        private void ApplySampleFileNameToText()
        {
            ResolveFilePathText();
            if (filePathText == null)
            {
                return;
            }

            filePathText.text = GetDisplayText(sampleFileName);
        }

        private void ResolveFilePathText()
        {
            if (filePathText == null)
            {
                filePathText = GetComponentInChildren<TextMeshProUGUI>(true);
            }
        }

        private static string GetDisplayText(string fileName)
        {
            var normalized = NormalizeSampleFileName(fileName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            return Path.GetFileName(normalized);
        }

        private static string NormalizeSampleFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            return fileName.Replace('\\', '/').Trim().TrimStart('/');
        }
    }
}
