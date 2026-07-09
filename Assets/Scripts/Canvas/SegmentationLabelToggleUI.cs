using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleITKUnity
{
    /// <summary>
    /// Builds per-label visibility toggles from the active segmentation overlay.
    /// </summary>
    public sealed class SegmentationLabelToggleUI : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private SimpleITKVolumeRenderer targetRenderer;

        [Header("UI")]
        [SerializeField] private RectTransform contentRoot;
        [SerializeField] private Toggle togglePrefab;
        [SerializeField] private bool hideWhenNoSegmentation = true;
        [SerializeField, Min(0.1f)] private float refreshIntervalSeconds = 0.5f;

        private readonly List<Toggle> _spawnedToggles = new List<Toggle>();
        private readonly List<byte> _spawnedLabels = new List<byte>();
        private float _nextRefreshTime;
        private int _lastSliceMaterialVersion = -1;
        private bool _hasSegmentation;

        private void Reset()
        {
            ResolveTarget();
            if (contentRoot == null)
            {
                contentRoot = transform as RectTransform;
            }
        }

        private void OnEnable()
        {
            ResolveTarget();
            RefreshToggles(force: true);
        }

        private void OnDisable()
        {
            ClearSpawnedToggles();
        }

        private void Update()
        {
            if (targetRenderer == null)
            {
                ResolveTarget();
            }

            if (targetRenderer == null)
            {
                return;
            }

            var hasSegmentation = targetRenderer.HasSegmentationTexture();
            var versionChanged = targetRenderer.SliceMaterialVersion != _lastSliceMaterialVersion;
            if (hasSegmentation != _hasSegmentation || versionChanged || Time.unscaledTime >= _nextRefreshTime)
            {
                RefreshToggles(force: versionChanged || hasSegmentation != _hasSegmentation);
            }
        }

        public void RefreshToggles(bool force = false)
        {
            ResolveTarget();
            _nextRefreshTime = Time.unscaledTime + Mathf.Max(0.1f, refreshIntervalSeconds);

            if (targetRenderer == null || contentRoot == null)
            {
                SetPanelActive(false);
                return;
            }

            _hasSegmentation = targetRenderer.HasSegmentationTexture();
            _lastSliceMaterialVersion = targetRenderer.SliceMaterialVersion;

            if (!_hasSegmentation)
            {
                ClearSpawnedToggles();
                SetPanelActive(!hideWhenNoSegmentation);
                return;
            }

            SetPanelActive(true);
            var entries = targetRenderer.GetSegmentationLabelEntries();
            if (!force && entries.Count == _spawnedLabels.Count)
            {
                var same = true;
                for (var i = 0; i < entries.Count; ++i)
                {
                    if (_spawnedLabels[i] != entries[i].label)
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    for (var i = 0; i < entries.Count; ++i)
                    {
                        if (_spawnedToggles[i] != null)
                        {
                            _spawnedToggles[i].SetIsOnWithoutNotify(entries[i].visible);
                        }
                    }

                    return;
                }
            }

            ClearSpawnedToggles();
            for (var i = 0; i < entries.Count; ++i)
            {
                var entry = entries[i];
                var toggle = CreateToggle(entry.name);
                if (toggle == null)
                {
                    continue;
                }

                var label = entry.label;
                toggle.SetIsOnWithoutNotify(entry.visible);
                toggle.onValueChanged.AddListener(isOn =>
                {
                    if (targetRenderer != null)
                    {
                        targetRenderer.SetLabelVisible(label, isOn);
                    }
                });

                _spawnedToggles.Add(toggle);
                _spawnedLabels.Add(label);
            }
        }

        private Toggle CreateToggle(string labelName)
        {
            if (togglePrefab != null)
            {
                var instance = Instantiate(togglePrefab, contentRoot);
                instance.gameObject.SetActive(true);
                ApplyToggleLabel(instance, labelName);
                return instance;
            }

            var root = new GameObject($"LabelToggle_{labelName}", typeof(RectTransform), typeof(Toggle));
            var rect = root.GetComponent<RectTransform>();
            rect.SetParent(contentRoot, false);
            rect.sizeDelta = new Vector2(220f, 28f);

            var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
            var backgroundRect = background.GetComponent<RectTransform>();
            backgroundRect.SetParent(rect, false);
            backgroundRect.anchorMin = new Vector2(0f, 0.5f);
            backgroundRect.anchorMax = new Vector2(0f, 0.5f);
            backgroundRect.pivot = new Vector2(0f, 0.5f);
            backgroundRect.anchoredPosition = Vector2.zero;
            backgroundRect.sizeDelta = new Vector2(22f, 22f);
            var backgroundImage = background.GetComponent<Image>();
            backgroundImage.color = new Color(0.2f, 0.2f, 0.2f, 0.9f);

            var checkmark = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            var checkRect = checkmark.GetComponent<RectTransform>();
            checkRect.SetParent(backgroundRect, false);
            checkRect.anchorMin = Vector2.zero;
            checkRect.anchorMax = Vector2.one;
            checkRect.offsetMin = new Vector2(4f, 4f);
            checkRect.offsetMax = new Vector2(-4f, -4f);
            var checkImage = checkmark.GetComponent<Image>();
            checkImage.color = new Color(0.35f, 0.75f, 1f, 1f);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            var labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 1f);
            labelRect.offsetMin = new Vector2(30f, 0f);
            labelRect.offsetMax = Vector2.zero;

            var tmp = labelGo.AddComponent<TextMeshProUGUI>();
            tmp.text = labelName;
            tmp.fontSize = 16f;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;

            var toggle = root.GetComponent<Toggle>();
            toggle.targetGraphic = backgroundImage;
            toggle.graphic = checkImage;
            toggle.isOn = true;
            return toggle;
        }

        private static void ApplyToggleLabel(Toggle toggle, string labelName)
        {
            if (toggle == null)
            {
                return;
            }

            var tmp = toggle.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
            {
                tmp.text = labelName;
                return;
            }

            var legacy = toggle.GetComponentInChildren<Text>(true);
            if (legacy != null)
            {
                legacy.text = labelName;
            }
        }

        private void ClearSpawnedToggles()
        {
            for (var i = 0; i < _spawnedToggles.Count; ++i)
            {
                if (_spawnedToggles[i] != null)
                {
                    Destroy(_spawnedToggles[i].gameObject);
                }
            }

            _spawnedToggles.Clear();
            _spawnedLabels.Clear();
        }

        private void SetPanelActive(bool active)
        {
            if (contentRoot != null && contentRoot.gameObject.activeSelf != active)
            {
                contentRoot.gameObject.SetActive(active);
            }
        }

        private void ResolveTarget()
        {
            if (targetRenderer != null)
            {
                return;
            }

            targetRenderer = SimpleITKRendererLocator.FindInHierarchyOrScene(transform, gameObject.scene);
        }
    }
}
