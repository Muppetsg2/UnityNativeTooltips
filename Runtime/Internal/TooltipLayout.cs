using UnityEngine;

namespace NativeTooltips.Internal
{
    /// <summary>Input for <see cref="TooltipLayout.Place"/>. Every value is in one pixel space with Y pointing DOWN.</summary>
    internal struct LayoutInput
    {
        public TooltipPlacement Placement;   // already resolved (never Auto)
        public Vector2 Pointer;              // cursor hot spot
        public float CursorHeight;           // distance from hot spot to the bottom of the cursor image
        public bool HasAnchor;
        public Rect Anchor;                  // element rectangle
        public Vector2 Size;                 // bubble size
        public Vector2 Offset;               // +x right, +y DOWN (already converted from the style)
        public float Gap;                    // distance from the element / cursor
        public Rect WorkArea;                // area the bubble must stay inside
        public bool CenterOnPointer;         // GTK/Android centre the bubble under the cursor; Win32/macOS align its left edge
    }

    /// <summary>
    /// Placement shared by every backend that positions the bubble itself (Windows, Linux, Android,
    /// macOS/WebGL custom bubble, IMGUI). Flips to the opposite side when there is no room and clamps
    /// the result to the work area.
    /// </summary>
    internal static class TooltipLayout
    {
        public static TooltipPlacement Resolve(TooltipPlacement placement, TooltipTrigger trigger, bool hasAnchor)
        {
            if (placement != TooltipPlacement.Auto) return placement;

            switch (trigger)
            {
                case TooltipTrigger.LongPress:
                    return hasAnchor ? TooltipPlacement.AboveElement : TooltipPlacement.AboveCursor;
                case TooltipTrigger.Manual:
                    return hasAnchor ? TooltipPlacement.AboveElement : TooltipPlacement.BelowCursor;
                default:
                    return TooltipPlacement.BelowCursor;
            }
        }

        public static bool IsElementPlacement(TooltipPlacement p) =>
            p == TooltipPlacement.AboveElement || p == TooltipPlacement.BelowElement ||
            p == TooltipPlacement.LeftOfElement || p == TooltipPlacement.RightOfElement;

        /// <summary>Returns the top-left corner of the bubble.</summary>
        public static Vector2 Place(in LayoutInput input)
        {
            Vector2 size = input.Size;
            Vector2 p = input.Pointer;
            Rect a = input.HasAnchor ? input.Anchor : new Rect(p.x, p.y, 0f, 0f);
            Vector2 off = input.Offset;
            float gap = input.Gap;
            Rect work = input.WorkArea;

            float cursorX = input.CenterOnPointer ? p.x - size.x * 0.5f : p.x;
            Vector2 primary, flipped;
            bool vertical;

            switch (input.Placement)
            {
                case TooltipPlacement.AboveCursor:
                    vertical = true;
                    primary = new Vector2(cursorX + off.x, p.y - gap - size.y + off.y);
                    flipped = new Vector2(cursorX + off.x, p.y + input.CursorHeight - off.y);
                    break;

                case TooltipPlacement.AboveElement:
                    vertical = true;
                    primary = new Vector2(a.center.x - size.x * 0.5f + off.x, a.yMin - gap - size.y + off.y);
                    flipped = new Vector2(primary.x, a.yMax + gap - off.y);
                    break;

                case TooltipPlacement.BelowElement:
                    vertical = true;
                    primary = new Vector2(a.center.x - size.x * 0.5f + off.x, a.yMax + gap + off.y);
                    flipped = new Vector2(primary.x, a.yMin - gap - size.y - off.y);
                    break;

                case TooltipPlacement.LeftOfElement:
                    vertical = false;
                    primary = new Vector2(a.xMin - gap - size.x + off.x, a.center.y - size.y * 0.5f + off.y);
                    flipped = new Vector2(a.xMax + gap - off.x, primary.y);
                    break;

                case TooltipPlacement.RightOfElement:
                    vertical = false;
                    primary = new Vector2(a.xMax + gap + off.x, a.center.y - size.y * 0.5f + off.y);
                    flipped = new Vector2(a.xMin - gap - size.x - off.x, primary.y);
                    break;

                default: // BelowCursor
                    vertical = true;
                    primary = new Vector2(cursorX + off.x, p.y + input.CursorHeight + off.y);
                    flipped = new Vector2(cursorX + off.x, p.y - gap - size.y - off.y);
                    break;
            }

            Vector2 pos = primary;
            float overflowPrimary = Overflow(primary, size, work, vertical);
            if (overflowPrimary > 0f && Overflow(flipped, size, work, vertical) < overflowPrimary)
                pos = flipped;

            // Clamp inside the work area (left/top edge wins when the bubble is larger than the area).
            pos.x = Mathf.Max(work.xMin, Mathf.Min(pos.x, work.xMax - size.x));
            pos.y = Mathf.Max(work.yMin, Mathf.Min(pos.y, work.yMax - size.y));
            return pos;
        }

        static float Overflow(Vector2 pos, Vector2 size, Rect work, bool vertical)
        {
            if (vertical)
                return Mathf.Max(0f, work.yMin - pos.y) + Mathf.Max(0f, pos.y + size.y - work.yMax);
            return Mathf.Max(0f, work.xMin - pos.x) + Mathf.Max(0f, pos.x + size.x - work.xMax);
        }

        /// <summary>Converts a style offset (points, +y up) to layout pixels (+y down).</summary>
        public static Vector2 OffsetToPixels(Vector2 styleOffset, float pixelsPerPoint) =>
            new Vector2(styleOffset.x * pixelsPerPoint, -styleOffset.y * pixelsPerPoint);
    }
}
