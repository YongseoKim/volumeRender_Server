using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SimpleITKUnity
{
    /// <summary>
    /// Attach to each slice RawImage to forward VR poke / UI pointer hits to
    /// pane-volume resolution, pane line drag strokes, and optional debug logging.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RawImage))]
    public sealed class SimpleITKPaneClickDebugProbe : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        [SerializeField] private SimpleITKOrthogonalViewController controller;
        [SerializeField] private SimpleITKOrthogonalViewController.UiPane pane = SimpleITKOrthogonalViewController.UiPane.C;

        private void Awake()
        {
            if (controller == null)
            {
                controller = FindFirstObjectByType<SimpleITKOrthogonalViewController>();
            }

            var parentName = transform.parent != null ? transform.parent.name : name;
            if (parentName.Contains("RawImage_A") || name.Contains("RawImage_A"))
            {
                pane = SimpleITKOrthogonalViewController.UiPane.A;
            }
            else if (parentName.Contains("RawImage_B") || name.Contains("RawImage_B"))
            {
                pane = SimpleITKOrthogonalViewController.UiPane.B;
            }
            else if (parentName.Contains("RawImage_C") || name.Contains("RawImage_C"))
            {
                pane = SimpleITKOrthogonalViewController.UiPane.C;
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (controller == null || eventData == null)
            {
                return;
            }

            if (controller.TryBeginPaneLineDrag(pane, eventData))
            {
                return;
            }

            if (controller.TryGetPaneClickVolume(
                    pane,
                    eventData.position,
                    out _,
                    out _,
                    out _))
            {
                controller.DebugLogPaneClickFromPointer(pane, eventData);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            controller?.UpdatePaneLineDrag(pane, eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            controller?.EndPaneLineDrag(pane, eventData);
        }
    }
}
