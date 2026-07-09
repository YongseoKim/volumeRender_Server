using System;
using UnityEngine;

namespace SimpleITKUnity
{
    /// <summary>
    /// Base class for volume segmentation inference components that can be wired into
    /// <see cref="SimpleITKVolumeLoader.segmentationInference"/>.
    /// </summary>
    public abstract class SegmentationInferenceBehaviour : MonoBehaviour
    {
        public abstract bool TryPrepareSegmentationForVolume(
            string primaryVolumePath,
            out SimpleITKLabelVolumeData segmentationData,
            out string summary,
            out string error,
            bool forceCpuBackend = false,
            Action<float, string> progress = null);

        public abstract void ReleaseResources();
    }
}
