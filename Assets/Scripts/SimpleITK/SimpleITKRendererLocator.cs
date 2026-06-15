using UnityEngine;
using UnityEngine.SceneManagement;

namespace SimpleITKUnity
{
    /// <summary>
    /// Shared utility for locating a <see cref="SimpleITKVolumeRenderer"/> in the scene.
    /// Consolidates the renderer-selection logic that used to be duplicated across multiple classes.
    /// </summary>
    internal static class SimpleITKRendererLocator
    {
        /// <summary>
        /// Picks the best renderer from the provided candidates.
        /// Priority: active + has texture > active > first valid item.
        /// </summary>
        public static SimpleITKVolumeRenderer PickBest(SimpleITKVolumeRenderer[] candidates)
        {
            if (candidates == null || candidates.Length == 0)
            {
                return null;
            }

            SimpleITKVolumeRenderer firstValid = null;
            SimpleITKVolumeRenderer firstActive = null;
            SimpleITKVolumeRenderer firstWithTexture = null;

            for (var i = 0; i < candidates.Length; ++i)
            {
                var candidate = candidates[i];
                if (candidate == null) continue;

                firstValid ??= candidate;

                if (candidate.isActiveAndEnabled)
                {
                    firstActive ??= candidate;
                }

                if (candidate.GetVolumeTexture() != null)
                {
                    if (candidate.isActiveAndEnabled)
                    {
                        return candidate; // Highest priority: active + has texture.
                    }
                    firstWithTexture ??= candidate;
                }
            }

            return firstWithTexture ?? firstActive ?? firstValid;
        }

        /// <summary>
        /// Searches from <paramref name="source"/> through the hierarchy
        /// (self -> parents -> whole scene) and returns the best renderer.
        /// </summary>
        /// <param name="source">Transform to start searching from.</param>
        /// <param name="ownerScene">Scene used to prefer renderers from the same scene.</param>
        public static SimpleITKVolumeRenderer FindInHierarchyOrScene(
            Transform source,
            Scene ownerScene)
        {
            if (source == null) return null;

            // Step 1: check the local component.
            var local = source.GetComponent<SimpleITKVolumeRenderer>();
            if (local != null) return local;

            // Step 2: walk up the hierarchy and search children at each level.
            var current = source;
            while (current != null)
            {
                var found = PickBest(current.GetComponentsInChildren<SimpleITKVolumeRenderer>(true));
                if (found != null) return found;
                current = current.parent;
            }

            // Step 3: search the entire scene.
            return FindBestInScene(ownerScene);
        }

        /// <summary>
        /// Returns the best renderer in the scene.
        /// Renderers from the same scene are preferred, with other scenes used only as a final fallback.
        /// </summary>
        public static SimpleITKVolumeRenderer FindBestInScene(Scene ownerScene)
        {
            var all = Object.FindObjectsByType<SimpleITKVolumeRenderer>(FindObjectsSortMode.None);
            if (all == null || all.Length == 0) return null;

            SimpleITKVolumeRenderer firstSameScene = null;
            SimpleITKVolumeRenderer firstActiveSameScene = null;
            SimpleITKVolumeRenderer firstWithTextureSameScene = null;

            for (var i = 0; i < all.Length; ++i)
            {
                var candidate = all[i];
                if (candidate == null || candidate.gameObject.scene != ownerScene) continue;

                firstSameScene ??= candidate;

                if (candidate.isActiveAndEnabled)
                {
                    firstActiveSameScene ??= candidate;
                }

                if (candidate.GetVolumeTexture() != null)
                {
                    if (candidate.isActiveAndEnabled) return candidate;
                    firstWithTextureSameScene ??= candidate;
                }
            }

            // If nothing is found in the same scene, fall back to all[0] to preserve previous behavior.
            return firstWithTextureSameScene
                ?? firstActiveSameScene
                ?? firstSameScene
                ?? all[0];
        }
    }
}
