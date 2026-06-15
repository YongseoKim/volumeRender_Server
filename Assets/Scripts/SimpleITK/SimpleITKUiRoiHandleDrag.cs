using UnityEngine;
using UnityEngine.EventSystems;

namespace SimpleITKUnity
{
    /// <summary>
    /// UI-driven ROI handle interaction for the orthogonal slice views.
    /// Attach this to the Start/End handle UI prefab (Image).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimpleITKUiRoiHandleDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IPointerDownHandler
    {
        public enum UiPane
        {
            A,
            B,
            C
        }

        public enum UiHandle
        {
            Start,
            End
        }

        [Header("Binding")]
        [SerializeField] private SimpleITKOrthogonalViewController controller;
        [SerializeField] private RectTransform overlayRect;
        [SerializeField] private UiPane pane = UiPane.C;
        [SerializeField] private UiHandle handle = UiHandle.Start;

        private RectTransform _selfRect;

        private void Awake()
        {
            _selfRect = transform as RectTransform;
            if (overlayRect == null && _selfRect != null)
            {
                overlayRect = _selfRect.parent as RectTransform;
            }

            if (controller == null)
            {
                controller = FindFirstObjectByType<SimpleITKOrthogonalViewController>();
            }

            // Best-effort auto-detect pane from hierarchy naming.
            if (overlayRect != null)
            {
                var parentName = overlayRect.parent != null ? overlayRect.parent.name : string.Empty;
                if (parentName.Contains("RawImage_A"))
                {
                    pane = UiPane.A;
                }
                else if (parentName.Contains("RawImage_B"))
                {
                    pane = UiPane.B;
                }
                else if (parentName.Contains("RawImage_C"))
                {
                    pane = UiPane.C;
                }
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            // Ensure the handle jumps to the pointer on click.
            ApplyPointer(eventData);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            ApplyPointer(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            ApplyPointer(eventData);
        }

        private void ApplyPointer(PointerEventData eventData)
        {
            if (controller == null || overlayRect == null)
            {
                return;
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    overlayRect,
                    eventData.position,
                    eventData.pressEventCamera,
                    out var local))
            {
                return;
            }

            var rect = overlayRect.rect;
            if (rect.width <= 1e-4f || rect.height <= 1e-4f)
            {
                return;
            }

            // local is relative to pivot; convert to 0..1 in overlay space.
            var u = (local.x / rect.width) + overlayRect.pivot.x;
            var v = (local.y / rect.height) + overlayRect.pivot.y;

            controller.SetRoiHandleFromUi(
                pane == UiPane.A ? SimpleITKOrthogonalViewController.UiPane.A :
                pane == UiPane.B ? SimpleITKOrthogonalViewController.UiPane.B :
                                   SimpleITKOrthogonalViewController.UiPane.C,
                handle == UiHandle.Start,
                u,
                v);
        }
    }
}
