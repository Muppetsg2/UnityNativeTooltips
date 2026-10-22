using System.Runtime.CompilerServices;
using NativeTooltips.Internal;
using UnityEngine;
using UnityEngine.UIElements;

namespace NativeTooltips
{
    /// <summary>
    /// Native tooltip for a runtime UI Toolkit element. The text comes from <see cref="Text"/>, or — when
    /// empty — from the built-in <c>VisualElement.tooltip</c> property (which Unity ignores in players).
    /// <code>button.AddManipulator(new NativeTooltipManipulator("Save file"));</code>
    /// or for a whole tree: <code>root.EnableNativeTooltips();</code>
    /// </summary>
    public class NativeTooltipManipulator : PointerManipulator, IRichTooltipSource, IHierarchicalTooltipSource
    {
        string _text;
        bool _touchPressed;
        int _pressedPointerId = -1;

        public NativeTooltipManipulator(string text = null, TooltipStyle style = null, string title = null,
            TooltipIcon icon = TooltipIcon.None)
        {
            _text = text;
            Style = style;
            Title = title;
            Icon = icon;
        }

        /// <summary>Optional bold title above the text.</summary>
        public string Title { get; set; }

        /// <summary>Icon next to the title (Windows).</summary>
        public TooltipIcon Icon { get; set; }

        /// <summary>Style for this element. Null = <see cref="TooltipSettings.Style"/>.</summary>
        public TooltipStyle Style { get; set; }

        string IRichTooltipSource.TooltipTitle => Title;
        TooltipIcon IRichTooltipSource.TooltipIcon => Icon;
        TooltipStyle IRichTooltipSource.TooltipStyle => Style;

        /// <summary>Text; null/empty = use <c>target.tooltip</c>.</summary>
        public string Text
        {
            get => _text;
            set => _text = value;
        }

        public string TooltipText => !string.IsNullOrEmpty(_text) ? _text : target?.tooltip;

        public bool IsTooltipActive =>
            target != null &&
            target.panel != null &&
            target.enabledInHierarchy &&
            target.resolvedStyle.display != DisplayStyle.None &&
            target.resolvedStyle.visibility == Visibility.Visible;

        public int HierarchyDepth
        {
            get
            {
                int d = 0;
                for (VisualElement e = target?.hierarchy.parent; e != null; e = e.hierarchy.parent) d++;
                return d;
            }
        }

        public bool TryGetScreenRect(out Rect screenRect)
        {
            screenRect = default;
            if (target?.panel == null) return false;

            Rect world = target.worldBound;
            if (!TryPanelSize(out Vector2 panelSize)) return false;

            // Normalized in panel space (y down) → screen pixels (y up).
            float nx = world.x / panelSize.x, ny = world.y / panelSize.y;
            float nw = world.width / panelSize.x, nh = world.height / panelSize.y;
            screenRect = new Rect(nx * Screen.width, (1f - ny - nh) * Screen.height, nw * Screen.width, nh * Screen.height);
            return screenRect.width > 0f && screenRect.height > 0f;
        }

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<PointerEnterEvent>(OnEnter);
            target.RegisterCallback<PointerLeaveEvent>(OnLeave);
            target.RegisterCallback<PointerMoveEvent>(OnMove);
            target.RegisterCallback<PointerDownEvent>(OnDown, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerCancelEvent>(OnCancel);
            target.RegisterCallback<DetachFromPanelEvent>(OnDetach);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<PointerEnterEvent>(OnEnter);
            target.UnregisterCallback<PointerLeaveEvent>(OnLeave);
            target.UnregisterCallback<PointerMoveEvent>(OnMove);
            target.UnregisterCallback<PointerDownEvent>(OnDown, TrickleDown.TrickleDown);
            target.UnregisterCallback<PointerUpEvent>(OnUp, TrickleDown.TrickleDown);
            target.UnregisterCallback<PointerCancelEvent>(OnCancel);
            target.UnregisterCallback<DetachFromPanelEvent>(OnDetach);
            Tooltip.Release(this);
        }

        static bool IsTouch(IPointerEvent e) => e.pointerType == UnityEngine.UIElements.PointerType.touch;

        void OnEnter(PointerEnterEvent e)
        {
            if (!IsTooltipActive) return;
            Tooltip.PointerEnter(this, PanelToScreen(e.position), IsTouch(e));
        }

        void OnLeave(PointerLeaveEvent e)
        {
            Tooltip.PointerExit(this, IsTouch(e));
        }

        void OnMove(PointerMoveEvent e)
        {
            if (_touchPressed && e.pointerId != _pressedPointerId) return;
            Tooltip.PointerMove(this, PanelToScreen(e.position), IsTouch(e));
        }

        void OnDown(PointerDownEvent e)
        {
            if (!IsTooltipActive) return;
            bool touch = IsTouch(e);
            _touchPressed = touch;
            _pressedPointerId = e.pointerId;
            Tooltip.PointerDown(this, PanelToScreen(e.position), touch);
        }

        void OnUp(PointerUpEvent e)
        {
            bool touch = IsTouch(e);
            if (touch && e.pointerId != _pressedPointerId) return;
            _touchPressed = false;
            Tooltip.PointerUp(this, touch);
        }

        void OnCancel(PointerCancelEvent e)
        {
            // e.g. a ScrollView captured the gesture
            _touchPressed = false;
            Tooltip.PointerExit(this, IsTouch(e));
        }

        void OnDetach(DetachFromPanelEvent e)
        {
            _touchPressed = false;
            Tooltip.Release(this);
        }

        Vector2 PanelToScreen(Vector3 panelPosition)
        {
            if (!TryPanelSize(out Vector2 size)) return Vector2.zero;
            return new Vector2(
                panelPosition.x / size.x * Screen.width,
                (1f - panelPosition.y / size.y) * Screen.height);
        }

        bool TryPanelSize(out Vector2 size)
        {
            // Assumes a full-screen panel (typical PanelSettings without a RenderTexture).
            Rect root = target.panel.visualTree.worldBound;
            size = new Vector2(root.width, root.height);
            return size.x > 0f && size.y > 0f;
        }
    }

    public static class NativeTooltipUIToolkitExtensions
    {
        static readonly ConditionalWeakTable<VisualElement, NativeTooltipManipulator> s_Attached =
            new ConditionalWeakTable<VisualElement, NativeTooltipManipulator>();

        /// <summary>Adds a native tooltip to the element (idempotent).</summary>
        public static NativeTooltipManipulator AddNativeTooltip(this VisualElement element, string text = null,
            TooltipStyle style = null, string title = null, TooltipIcon icon = TooltipIcon.None)
        {
            if (s_Attached.TryGetValue(element, out var existing))
            {
                if (text != null) existing.Text = text;
                if (style != null) existing.Style = style;
                if (title != null) existing.Title = title;
                if (icon != TooltipIcon.None) existing.Icon = icon;
                return existing;
            }

            var m = new NativeTooltipManipulator(text, style, title, icon);
            element.AddManipulator(m);
            s_Attached.Add(element, m);
            return m;
        }

        public static void RemoveNativeTooltip(this VisualElement element)
        {
            if (!s_Attached.TryGetValue(element, out var m)) return;
            element.RemoveManipulator(m);
            s_Attached.Remove(element);
        }

        /// <summary>
        /// Adds native tooltips to every element in the tree that has <c>tooltip</c> set
        /// (in UXML: <c>tooltip="..."</c>). Call after building/cloning the UI.
        /// </summary>
        public static void EnableNativeTooltips(this VisualElement root, TooltipStyle style = null)
        {
            root.Query<VisualElement>().Where(e => !string.IsNullOrEmpty(e.tooltip)).ForEach(e => e.AddNativeTooltip(null, style));
        }
    }
}
