#if NATIVE_TOOLTIPS_UGUI
using NativeTooltips.Internal;
using UnityEngine;
using UnityEngine.EventSystems;

namespace NativeTooltips
{
    /// <summary>
    /// Add to a UGUI element (Button, Button - TextMeshPro, Image, Toggle…) or to a 3D/2D object with a
    /// collider (the camera needs a PhysicsRaycaster / Physics2DRaycaster) and a native OS tooltip appears
    /// on hover. On touch screens the tooltip appears after a long-press, like on Android.
    /// </summary>
    [AddComponentMenu("UI/Native Tooltip")]
    [DisallowMultipleComponent]
    public class NativeTooltip : MonoBehaviour,
        IRichTooltipSource,
        IHierarchicalTooltipSource,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerMoveHandler,
        IPointerDownHandler,
        IPointerUpHandler
    {
        [SerializeField, TextArea(1, 6)]
        [Tooltip("Tooltip text. Line breaks are preserved.")]
        string text = "";

        [SerializeField]
        [Tooltip("Optional bold title shown above the text.")]
        string title = "";

        [SerializeField]
        [Tooltip("Icon next to the title (Windows only).")]
        TooltipIcon icon = TooltipIcon.None;

        [SerializeField]
        [Tooltip("Shared style asset (Create → Native Tooltips → Tooltip Style). Ignored when Override Style is on.")]
        TooltipStyleAsset styleAsset;

        [SerializeField]
        [Tooltip("Use the inline style below instead of the asset / TooltipSettings.Style.")]
        bool overrideStyle;

        [SerializeField]
        TooltipStyle style = new TooltipStyle();

        [SerializeField]
        [Tooltip("When off, the component ignores the pointer.")]
        bool interactable = true;

        [SerializeField]
        [Tooltip("Also show the tooltip when the Selectable on this object is not interactable.")]
        bool showWhenSelectableDisabled = true;

        PointerEventData _pressEvent;
        Camera _eventCamera;
        UnityEngine.UI.Selectable _selectable;
        int _depth = -1;

        /// <summary>Tooltip text (can be changed at runtime — a visible bubble updates).</summary>
        public string Text
        {
            get => text;
            set => text = value;
        }

        /// <summary>Optional bold title above the text.</summary>
        public string Title
        {
            get => title;
            set => title = value;
        }

        /// <summary>Icon next to the title (Windows).</summary>
        public TooltipIcon Icon
        {
            get => icon;
            set => icon = value;
        }

        /// <summary>
        /// Style used by this tooltip. Setting it switches the component to the inline style
        /// (<c>OverrideStyle = true</c>). Reading returns the effective style.
        /// </summary>
        public TooltipStyle Style
        {
            get => TooltipStyle ?? TooltipSettings.Style;
            set
            {
                style = value ?? new TooltipStyle();
                overrideStyle = value != null;
            }
        }

        /// <summary>Shared style asset; used when <see cref="OverrideStyle"/> is off.</summary>
        public TooltipStyleAsset StyleAsset
        {
            get => styleAsset;
            set => styleAsset = value;
        }

        /// <summary>When true the inline <see cref="Style"/> is used instead of the asset / global style.</summary>
        public bool OverrideStyle
        {
            get => overrideStyle;
            set => overrideStyle = value;
        }

        /// <summary>Override to change the title dynamically.</summary>
        public virtual string TooltipTitle => title;

        TooltipIcon IRichTooltipSource.TooltipIcon => icon;

        /// <summary>Effective style, or null to use <see cref="TooltipSettings.Style"/>.</summary>
        public virtual TooltipStyle TooltipStyle
        {
            get
            {
                if (overrideStyle && style != null) return style;
                if (styleAsset != null && styleAsset.Style != null) return styleAsset.Style;
                return null;
            }
        }

        public bool Interactable
        {
            get => interactable;
            set
            {
                interactable = value;
                if (!value) Tooltip.Release(this);
            }
        }

        /// <summary>Override to generate the text dynamically.</summary>
        public virtual string TooltipText => text;

        public virtual bool IsTooltipActive
        {
            get
            {
                if (!interactable || !isActiveAndEnabled) return false;
                if (!showWhenSelectableDisabled && _selectable != null && !_selectable.IsInteractable()) return false;
                return true;
            }
        }

        public int HierarchyDepth
        {
            get
            {
                if (_depth < 0)
                {
                    int d = 0;
                    for (Transform t = transform.parent; t != null; t = t.parent) d++;
                    _depth = d;
                }

                return _depth;
            }
        }

        protected virtual void Awake()
        {
            _selectable = GetComponent<UnityEngine.UI.Selectable>();
        }

        protected virtual void OnTransformParentChanged()
        {
            _depth = -1;
        }

        protected virtual void OnDisable()
        {
            _pressEvent = null;
            Tooltip.Release(this);
        }

        public virtual bool TryGetScreenRect(out Rect screenRect)
        {
            if (transform is RectTransform rt)
                return ScreenRectUtility.TryGetScreenRect(rt, out screenRect);

            Camera cam = _eventCamera != null ? _eventCamera : Camera.main;

            if (TryGetComponent(out Collider c3d))
                return ScreenRectUtility.TryGetScreenRect(c3d.bounds, cam, out screenRect);
            if (TryGetComponent(out Collider2D c2d))
                return ScreenRectUtility.TryGetScreenRect(c2d.bounds, cam, out screenRect);
            if (TryGetComponent(out Renderer r))
                return ScreenRectUtility.TryGetScreenRect(r.bounds, cam, out screenRect);

            screenRect = default;
            return false;
        }

        // ------------------------------------------------------------------ EventSystem

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!IsTooltipActive) return;
            _eventCamera = eventData.enterEventCamera;
            Tooltip.PointerEnter(this, eventData.position, PointerKind.IsTouch(eventData));
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            Tooltip.PointerMove(this, eventData.position, PointerKind.IsTouch(eventData));
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            Tooltip.PointerExit(this, PointerKind.IsTouch(eventData));
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!IsTooltipActive) return;
            bool touch = PointerKind.IsTouch(eventData);
            _eventCamera = eventData.pressEventCamera;
            _pressEvent = touch ? eventData : null;

            Tooltip.PointerDown(this, eventData.position, touch, touch ? OnLongPressShown : (System.Action)null);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            bool touch = PointerKind.IsTouch(eventData);
            Tooltip.PointerUp(this, touch);
            if (touch) _pressEvent = null;
        }

        protected virtual void Update()
        {
            // Track the finger during a long-press (without implementing IDragHandler,
            // so drags still reach a parent ScrollRect).
            if (_pressEvent == null) return;
            if (_pressEvent.dragging)
            {
                Tooltip.PointerExit(this, true);
                _pressEvent = null;
                return;
            }

            Tooltip.PointerMove(this, _pressEvent.position, true);
        }

        void OnLongPressShown()
        {
            // Like Android's View: a long-press that showed a tooltip is no longer a click.
            if (TooltipSettings.SuppressClickAfterLongPress && _pressEvent != null)
                _pressEvent.eligibleForClick = false;
        }
    }

    internal static class PointerKind
    {
        public static bool IsTouch(PointerEventData e)
        {
#if NATIVE_TOOLTIPS_INPUT_SYSTEM
            if (e is UnityEngine.InputSystem.UI.ExtendedPointerEventData ext)
                return ext.pointerType == UnityEngine.InputSystem.UI.UIPointerType.Touch;
#endif
            // StandaloneInputModule: mouse ids are -1/-2/-3, touch = fingerId >= 0
            return e.pointerId >= 0;
        }
    }
}
#endif
