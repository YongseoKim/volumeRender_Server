using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SimpleITKUnity
{
    [CreateAssetMenu(
        fileName = "SimpleITKTransferFunctionPresetLibrary",
        menuName = "SimpleITK/Transfer Function Preset Library")]
    public sealed class SimpleITKTransferFunctionPresetLibrary : ScriptableObject
    {
        [Serializable]
        public struct ScalarColorPoint
        {
            public float Scalar;
            public Color Color;

            public ScalarColorPoint(float scalar, Color color)
            {
                Scalar = scalar;
                Color = color;
            }
        }

        [Serializable]
        public struct ScalarOpacityPoint
        {
            public float Scalar;
            public float Opacity;

            public ScalarOpacityPoint(float scalar, float opacity)
            {
                Scalar = scalar;
                Opacity = opacity;
            }
        }

        public sealed class TransferFunctionPresetDefinition
        {
            public string Name { get; }
            public string Group { get; }
            public bool AbsoluteRange { get; }
            public bool HasEffectiveRange { get; }
            public Vector2 EffectiveRange { get; }
            public ScalarColorPoint[] ColorPoints { get; }
            public ScalarOpacityPoint[] OpacityPoints { get; }

            public TransferFunctionPresetDefinition(
                string name,
                bool absoluteRange,
                bool hasEffectiveRange,
                Vector2 effectiveRange,
                ScalarColorPoint[] colorPoints,
                ScalarOpacityPoint[] opacityPoints)
            {
                Name = name ?? string.Empty;
                Group = GetPresetGroup(Name);
                AbsoluteRange = absoluteRange;
                HasEffectiveRange = hasEffectiveRange;
                EffectiveRange = effectiveRange;
                ColorPoints = colorPoints ?? Array.Empty<ScalarColorPoint>();
                OpacityPoints = opacityPoints ?? Array.Empty<ScalarOpacityPoint>();
            }
        }

        [Serializable]
        private sealed class PresetArrayWrapper
        {
            public JsonPreset[] items;
        }

        [Serializable]
        private sealed class JsonPreset
        {
            public string Name;
            public bool AbsoluteRange = true;
            public float[] EffectiveRange;
            public float[] RGBPoints;
            public float[] OpacityPoints;
        }

        private sealed class Catalog
        {
            public static readonly Catalog Empty =
                new Catalog(Array.Empty<string>(), new Dictionary<string, TransferFunctionPresetDefinition>(StringComparer.Ordinal));

            public readonly string[] Names;
            private readonly Dictionary<string, TransferFunctionPresetDefinition> _definitions;

            public Catalog(
                string[] names,
                Dictionary<string, TransferFunctionPresetDefinition> definitions)
            {
                Names = names ?? Array.Empty<string>();
                _definitions = definitions ??
                               new Dictionary<string, TransferFunctionPresetDefinition>(StringComparer.Ordinal);
            }

            public bool TryGetPreset(string presetName, out TransferFunctionPresetDefinition preset)
            {
                if (string.IsNullOrWhiteSpace(presetName))
                {
                    preset = null;
                    return false;
                }

                return _definitions.TryGetValue(presetName, out preset);
            }
        }

        [SerializeField] private TextAsset presetJson;

        [NonSerialized] private Catalog _cachedCatalog;
        [NonSerialized] private string _cachedCatalogKey = string.Empty;

        private static Catalog s_defaultCatalog;
        private static string s_defaultCatalogKey = string.Empty;

        public string[] GetPresetNames()
        {
            var catalog = GetCatalog(useDefaultFallback: true);
            return (string[])catalog.Names.Clone();
        }

        public bool TryGetPreset(string presetName, out TransferFunctionPresetDefinition preset)
        {
            return GetCatalog(useDefaultFallback: true).TryGetPreset(presetName, out preset);
        }

        public static string[] GetDefaultPresetNames()
        {
            var catalog = GetDefaultCatalog();
            return (string[])catalog.Names.Clone();
        }

        public static bool TryGetDefaultPreset(string presetName, out TransferFunctionPresetDefinition preset)
        {
            return GetDefaultCatalog().TryGetPreset(presetName, out preset);
        }

        public static string GetPresetGroup(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return "Custom";
            }

            if (presetName.StartsWith("CT-", StringComparison.OrdinalIgnoreCase))
            {
                return "CT";
            }

            if (presetName.StartsWith("MR-", StringComparison.OrdinalIgnoreCase))
            {
                return "MR";
            }

            if (presetName.StartsWith("DTI-", StringComparison.OrdinalIgnoreCase))
            {
                return "DTI";
            }

            if (presetName.StartsWith("US-", StringComparison.OrdinalIgnoreCase))
            {
                return "US";
            }

            return "Other";
        }

        private Catalog GetCatalog(bool useDefaultFallback)
        {
            if (TryLoadJson(out var json, out var catalogKey))
            {
                if (_cachedCatalog != null && string.Equals(_cachedCatalogKey, catalogKey, StringComparison.Ordinal))
                {
                    return _cachedCatalog;
                }

                _cachedCatalog = ParseCatalog(json);
                _cachedCatalogKey = catalogKey;
                return _cachedCatalog;
            }

            return useDefaultFallback ? GetDefaultCatalog() : Catalog.Empty;
        }

        private static Catalog GetDefaultCatalog()
        {
            if (TryLoadDefaultJson(out var json, out var catalogKey))
            {
                if (s_defaultCatalog != null && string.Equals(s_defaultCatalogKey, catalogKey, StringComparison.Ordinal))
                {
                    return s_defaultCatalog;
                }

                s_defaultCatalog = ParseCatalog(json);
                s_defaultCatalogKey = catalogKey;
                return s_defaultCatalog;
            }

            s_defaultCatalog = Catalog.Empty;
            s_defaultCatalogKey = string.Empty;
            return s_defaultCatalog;
        }

        private bool TryLoadJson(out string json, out string catalogKey)
        {
            json = string.Empty;
            catalogKey = string.Empty;

            if (presetJson == null || string.IsNullOrWhiteSpace(presetJson.text))
            {
                return false;
            }

            json = presetJson.text;
            catalogKey = $"asset:{presetJson.GetInstanceID()}:{presetJson.text.Length}";
            return true;
        }

        private static bool TryLoadDefaultJson(out string json, out string catalogKey)
        {
            json = string.Empty;
            catalogKey = string.Empty;

            var resourceAsset = Resources.Load<TextAsset>("MedicalColorPresets");
            if (resourceAsset != null && !string.IsNullOrWhiteSpace(resourceAsset.text))
            {
                json = resourceAsset.text;
                catalogKey = $"resources:{resourceAsset.GetInstanceID()}:{resourceAsset.text.Length}";
                return true;
            }

            var siblingPath = GetSiblingVolViewPresetPath();
            if (!string.IsNullOrWhiteSpace(siblingPath) && File.Exists(siblingPath))
            {
                json = File.ReadAllText(siblingPath);
                var info = new FileInfo(siblingPath);
                catalogKey = $"file:{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
                return true;
            }

            return false;
        }

        private static string GetSiblingVolViewPresetPath()
        {
            var assetsPath = Application.dataPath;
            if (string.IsNullOrWhiteSpace(assetsPath))
            {
                return string.Empty;
            }

            return Path.GetFullPath(Path.Combine(
                assetsPath,
                "..",
                "..",
                "VolView",
                "src",
                "vtk",
                "MedicalColorPresets.json"));
        }

        private static Catalog ParseCatalog(string json)
        {
            try
            {
                var definitions = new Dictionary<string, TransferFunctionPresetDefinition>(StringComparer.Ordinal);
                var builtinPresets = BuildBuiltinPresets();
                for (var i = 0; i < builtinPresets.Length; ++i)
                {
                    var builtin = builtinPresets[i];
                    definitions[builtin.Name] = builtin;
                }

                if (!string.IsNullOrWhiteSpace(json))
                {
                    var wrappedJson = $"{{\"items\":{json}}}";
                    var wrapper = JsonUtility.FromJson<PresetArrayWrapper>(wrappedJson);
                    if (wrapper?.items != null)
                    {
                        for (var i = 0; i < wrapper.items.Length; ++i)
                        {
                            var source = wrapper.items[i];
                            if (source == null || string.IsNullOrWhiteSpace(source.Name))
                            {
                                continue;
                            }

                            var definition = BuildDefinition(source);
                            definitions[definition.Name] = definition;
                        }
                    }
                }

                if (definitions.Count == 0)
                {
                    return Catalog.Empty;
                }

                var names = new List<string>(definitions.Keys);
                names.Sort(ComparePresetNames);
                return new Catalog(names.ToArray(), definitions);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"SimpleITKTransferFunctionPresetLibrary: failed to parse medical preset JSON: {ex.Message}");
                var builtinDefinitions =
                    new Dictionary<string, TransferFunctionPresetDefinition>(StringComparer.Ordinal);
                var builtinPresets = BuildBuiltinPresets();
                for (var i = 0; i < builtinPresets.Length; ++i)
                {
                    builtinDefinitions[builtinPresets[i].Name] = builtinPresets[i];
                }

                if (builtinDefinitions.Count == 0)
                {
                    return Catalog.Empty;
                }

                var names = new List<string>(builtinDefinitions.Keys);
                names.Sort(ComparePresetNames);
                return new Catalog(names.ToArray(), builtinDefinitions);
            }
        }

        private static TransferFunctionPresetDefinition BuildDefinition(JsonPreset source)
        {
            var hasEffectiveRange = source.EffectiveRange != null && source.EffectiveRange.Length >= 2;
            var effectiveRange = hasEffectiveRange
                ? new Vector2(source.EffectiveRange[0], source.EffectiveRange[1])
                : default;

            return new TransferFunctionPresetDefinition(
                source.Name,
                source.AbsoluteRange,
                hasEffectiveRange,
                effectiveRange,
                ConvertColorPoints(source.RGBPoints),
                ConvertOpacityPoints(source.OpacityPoints));
        }

        private static ScalarColorPoint[] ConvertColorPoints(float[] rawPoints)
        {
            if (rawPoints == null || rawPoints.Length < 4)
            {
                return Array.Empty<ScalarColorPoint>();
            }

            var count = rawPoints.Length / 4;
            var points = new ScalarColorPoint[count];
            for (var i = 0; i < count; ++i)
            {
                var index = i * 4;
                var scalar = rawPoints[index];
                var color = new Color(
                    Mathf.Clamp01(rawPoints[index + 1]),
                    Mathf.Clamp01(rawPoints[index + 2]),
                    Mathf.Clamp01(rawPoints[index + 3]),
                    1.0f);
                points[i] = new ScalarColorPoint(scalar, color);
            }

            return points;
        }

        private static ScalarOpacityPoint[] ConvertOpacityPoints(float[] rawPoints)
        {
            if (rawPoints == null || rawPoints.Length < 2)
            {
                return Array.Empty<ScalarOpacityPoint>();
            }

            var count = rawPoints.Length / 2;
            var points = new ScalarOpacityPoint[count];
            for (var i = 0; i < count; ++i)
            {
                var index = i * 2;
                points[i] = new ScalarOpacityPoint(rawPoints[index], Mathf.Clamp01(rawPoints[index + 1]));
            }

            return points;
        }

        private static int ComparePresetNames(string left, string right)
        {
            var leftGroup = GetPresetGroup(left);
            var rightGroup = GetPresetGroup(right);
            var groupCompare = GetGroupSortIndex(leftGroup).CompareTo(GetGroupSortIndex(rightGroup));
            if (groupCompare != 0)
            {
                return groupCompare;
            }

            return string.Compare(left, right, StringComparison.Ordinal);
        }

        private static int GetGroupSortIndex(string group)
        {
            switch (group)
            {
                case "CT":
                    return 0;
                case "MR":
                    return 1;
                case "DTI":
                    return 2;
                case "US":
                    return 3;
                case "Other":
                    return 4;
                default:
                    return 5;
            }
        }

        private static TransferFunctionPresetDefinition[] BuildBuiltinPresets()
        {
            return new[]
            {
                new TransferFunctionPresetDefinition(
                    "Grayscale",
                    absoluteRange: false,
                    hasEffectiveRange: false,
                    effectiveRange: default,
                    colorPoints: new[]
                    {
                        new ScalarColorPoint(0.0f, Color.black),
                        new ScalarColorPoint(1.0f, Color.white),
                    },
                    opacityPoints: new[]
                    {
                        new ScalarOpacityPoint(0.0f, 1.0f),
                        new ScalarOpacityPoint(1.0f, 1.0f),
                    }),
                new TransferFunctionPresetDefinition(
                    "Cool to Warm",
                    absoluteRange: false,
                    hasEffectiveRange: false,
                    effectiveRange: default,
                    colorPoints: new[]
                    {
                        new ScalarColorPoint(0.0f, new Color(0.231f, 0.298f, 0.753f, 1.0f)),
                        new ScalarColorPoint(0.5f, new Color(0.865f, 0.865f, 0.865f, 1.0f)),
                        new ScalarColorPoint(1.0f, new Color(0.706f, 0.016f, 0.149f, 1.0f)),
                    },
                    opacityPoints: new[]
                    {
                        new ScalarOpacityPoint(0.0f, 0.0f),
                        new ScalarOpacityPoint(1.0f, 1.0f),
                    }),
            };
        }
    }
}
