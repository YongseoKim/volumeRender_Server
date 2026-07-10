using UnityEditor;
using UnityEngine;

namespace SimpleITKUnity.Editor
{
    [CustomEditor(typeof(SimpleITKVolumeLoader))]
    public sealed class SimpleITKVolumeLoaderEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();

            var loader = (SimpleITKVolumeLoader)target;
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Generate Segmentation Volume"))
                {
                    loader.GenerateSegmentationVolume();
                }
            }

            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Generate Segmentation Volume is only available before entering Play mode.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Scans StreamingAssets/Sample for DICOM series folders and volume files " +
                    "(.nrrd/.mha), then writes {name}_seg.nrrd files to StreamingAssets/Segmented.",
                    MessageType.Info);
            }
        }
    }
}
