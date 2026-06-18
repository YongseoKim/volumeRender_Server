using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SimpleITKUnity
{
    public class FileBtnManager : MonoBehaviour
    {
        private static readonly Color UnselectedColor = new Color32(0xB4, 0xB4, 0xB4, 0xFF);
        private static readonly Color SelectedColor = Color.white;

        [SerializeField] private TextMeshProUGUI filePathText;
        [SerializeField] private Outline outline;
        [Tooltip("Relative path inside StreamingAssets/Sample")]
        [SerializeField] private string sampleFileName;

        private bool _isSelected;
        private float _outlineDefaultAlpha = 0.5f;

        public string SampleFileName => sampleFileName;
        public bool IsSelected => _isSelected;

        private void Awake()
        {
            ResolveFilePathText();
            ResolveOutline();
            CacheOutlineDefaultAlpha();
            ApplySelectionVisual();
        }

        private void OnEnable()
        {
            ApplySampleFileNameToText();
            ApplySelectionVisual();
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

        public void SetSelected(bool isSelected)
        {
            _isSelected = isSelected;
            ApplySelectionVisual();
        }

        private void ApplySelectionVisual()
        {
            ResolveFilePathText();
            ResolveOutline();

            var color = _isSelected ? SelectedColor : UnselectedColor;

            if (filePathText != null)
            {
                filePathText.color = color;
            }

            if (outline == null)
            {
                return;
            }

            var outlineColor = color;
            outlineColor.a = _outlineDefaultAlpha;
            outline.effectColor = outlineColor;
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

        private void ResolveOutline()
        {
            if (outline == null)
            {
                outline = GetComponent<Outline>();
            }
        }

        private void CacheOutlineDefaultAlpha()
        {
            ResolveOutline();
            if (outline != null)
            {
                _outlineDefaultAlpha = outline.effectColor.a;
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
