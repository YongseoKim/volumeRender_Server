using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace SimpleITKUnity
{
    public class SelectRenderTypeManager : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private SimpleITKVolumeRenderer targetRenderer;

        [Header("UI")]
        [SerializeField] private TMP_Dropdown volumeTypeDropdown;
        [SerializeField] private TMP_Dropdown sliceTypeDropdown;

        private string[] _presetValues = Array.Empty<string>();
        private bool _suppressVolumeDropdownCallback;
        private bool _suppressSliceDropdownCallback;

        private void Reset()
        {
            ResolveTarget();
        }

        private void OnEnable()
        {
            ResolveTarget();
            RefreshDropdownOptions();
            SyncDropdownsFromRenderer();
            RegisterDropdownListeners();
        }

        private void OnDisable()
        {
            UnregisterDropdownListeners();
        }

        private void RegisterDropdownListeners()
        {
            if (volumeTypeDropdown != null)
            {
                volumeTypeDropdown.onValueChanged.AddListener(OnVolumeDropdownChanged);
            }

            if (sliceTypeDropdown != null)
            {
                sliceTypeDropdown.onValueChanged.AddListener(OnSliceDropdownChanged);
            }
        }

        private void UnregisterDropdownListeners()
        {
            if (volumeTypeDropdown != null)
            {
                volumeTypeDropdown.onValueChanged.RemoveListener(OnVolumeDropdownChanged);
            }

            if (sliceTypeDropdown != null)
            {
                sliceTypeDropdown.onValueChanged.RemoveListener(OnSliceDropdownChanged);
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

        private void RefreshDropdownOptions()
        {
            if (targetRenderer == null)
            {
                _presetValues = new[] { string.Empty };
                ApplyDropdownOptions(volumeTypeDropdown, null);
                ApplyDropdownOptions(sliceTypeDropdown, null);
                return;
            }

            var presetNames = targetRenderer.GetAvailableTransferFunctionPresetNames();
            if (presetNames == null || presetNames.Length == 0)
            {
                Debug.LogWarning(
                    "SelectRenderTypeManager: No transfer-function presets were found. " +
                    "Keep Assets/Resources/MedicalColorPresets.json in the project.");
                _presetValues = new[] { string.Empty };
                ApplyDropdownOptions(volumeTypeDropdown, null);
                ApplyDropdownOptions(sliceTypeDropdown, null);
                return;
            }

            _presetValues = new string[presetNames.Length + 1];
            _presetValues[0] = string.Empty;
            Array.Copy(presetNames, 0, _presetValues, 1, presetNames.Length);

            ApplyDropdownOptions(volumeTypeDropdown, presetNames);
            ApplyDropdownOptions(sliceTypeDropdown, presetNames);
        }

        private static void ApplyDropdownOptions(TMP_Dropdown dropdown, string[] presetNames)
        {
            if (dropdown == null)
            {
                return;
            }

            var options = new List<TMP_Dropdown.OptionData> { new("Custom") };
            if (presetNames != null)
            {
                for (var i = 0; i < presetNames.Length; ++i)
                {
                    var presetName = presetNames[i];
                    var group = SimpleITKTransferFunctionPresetLibrary.GetPresetGroup(presetName);
                    options.Add(new TMP_Dropdown.OptionData($"[{group}] {presetName}"));
                }
            }

            dropdown.ClearOptions();
            dropdown.AddOptions(options);
        }

        private void SyncDropdownsFromRenderer()
        {
            if (targetRenderer == null)
            {
                return;
            }

            SetDropdownSelection(
                volumeTypeDropdown,
                targetRenderer.GetVolumeTransferFunctionPresetName(),
                suppressVolume: true);
            SetDropdownSelection(
                sliceTypeDropdown,
                targetRenderer.GetSliceTransferFunctionPresetName(),
                suppressVolume: false);
        }

        private void SetDropdownSelection(
            TMP_Dropdown dropdown,
            string presetName,
            bool suppressVolume)
        {
            if (dropdown == null)
            {
                return;
            }

            var index = FindPresetIndex(presetName);
            if (suppressVolume)
            {
                _suppressVolumeDropdownCallback = true;
            }
            else
            {
                _suppressSliceDropdownCallback = true;
            }

            dropdown.SetValueWithoutNotify(index);
            dropdown.RefreshShownValue();

            if (suppressVolume)
            {
                _suppressVolumeDropdownCallback = false;
            }
            else
            {
                _suppressSliceDropdownCallback = false;
            }
        }

        private int FindPresetIndex(string presetName)
        {
            var normalized = presetName ?? string.Empty;
            if (_presetValues == null || _presetValues.Length == 0)
            {
                return 0;
            }

            for (var i = 0; i < _presetValues.Length; ++i)
            {
                if (string.Equals(_presetValues[i], normalized, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }

        private string GetPresetNameAt(int index)
        {
            if (_presetValues == null || index < 0 || index >= _presetValues.Length)
            {
                return string.Empty;
            }

            return _presetValues[index] ?? string.Empty;
        }

        private void OnVolumeDropdownChanged(int index)
        {
            if (_suppressVolumeDropdownCallback || targetRenderer == null)
            {
                return;
            }

            targetRenderer.SetVolumeTransferFunctionPresetName(GetPresetNameAt(index));
        }

        private void OnSliceDropdownChanged(int index)
        {
            if (_suppressSliceDropdownCallback || targetRenderer == null)
            {
                return;
            }

            targetRenderer.SetSliceTransferFunctionPresetName(GetPresetNameAt(index));
        }
    }
}
