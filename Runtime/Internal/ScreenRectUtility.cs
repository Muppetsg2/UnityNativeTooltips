using UnityEngine;

namespace NativeTooltips.Internal
{
    /// <summary>Rectangle conversions between UGUI / world / screen / normalized spaces.</summary>
    public static class ScreenRectUtility
    {
        static readonly Vector3[] s_Corners = new Vector3[4];
        static readonly Vector3[] s_BoxCorners = new Vector3[8];

        /// <summary>RectTransform rectangle in screen pixels (origin: bottom-left).</summary>
        public static bool TryGetScreenRect(RectTransform rt, out Rect rect)
        {
            rect = default;
            if (rt == null) return false;

            Camera cam = null;
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                var root = canvas.rootCanvas;
                if (root.renderMode != RenderMode.ScreenSpaceOverlay)
                    cam = root.worldCamera != null ? root.worldCamera : Camera.main;
            }

            rt.GetWorldCorners(s_Corners);
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(cam, s_Corners[i]);
                if (p.x < xMin) xMin = p.x;
                if (p.y < yMin) yMin = p.y;
                if (p.x > xMax) xMax = p.x;
                if (p.y > yMax) yMax = p.y;
            }

            rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return rect.width > 0f && rect.height > 0f;
        }

        /// <summary>Projects a box (e.g. collider bounds) onto the screen.</summary>
        public static bool TryGetScreenRect(Bounds bounds, Camera cam, out Rect rect)
        {
            rect = default;
            if (cam == null) return false;

            Vector3 c = bounds.center, e = bounds.extents;
            int n = 0;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
                s_BoxCorners[n++] = c + Vector3.Scale(e, new Vector3(x, y, z));

            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            bool any = false;
            for (int i = 0; i < 8; i++)
            {
                Vector3 p = cam.WorldToScreenPoint(s_BoxCorners[i]);
                if (p.z < 0f) continue; // behind the camera
                any = true;
                if (p.x < xMin) xMin = p.x;
                if (p.y < yMin) yMin = p.y;
                if (p.x > xMax) xMax = p.x;
                if (p.y > yMax) yMax = p.y;
            }

            if (!any) return false;
            rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            return true;
        }

        /// <summary>
        /// Screen pixels (bottom-left origin) → normalized 0..1 coordinates relative to the Unity view,
        /// with the origin at the TOP-left (the UIKit / flipped AppKit / Android / DOM convention).
        /// </summary>
        public static Rect ToNormalizedTopLeft(Rect screenRect)
        {
            float w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
            return new Rect(
                screenRect.xMin / w,
                1f - screenRect.yMax / h,
                screenRect.width / w,
                screenRect.height / h);
        }

        public static Vector2 ToNormalizedTopLeft(Vector2 screenPoint)
        {
            float w = Mathf.Max(1, Screen.width), h = Mathf.Max(1, Screen.height);
            return new Vector2(screenPoint.x / w, 1f - screenPoint.y / h);
        }

        /// <summary>Normalized (top-left origin) → GUI pixels (top-left origin), e.g. for IMGUI.</summary>
        public static Rect NormalizedToGui(Rect n)
        {
            return new Rect(n.x * Screen.width, n.y * Screen.height, n.width * Screen.width, n.height * Screen.height);
        }
    }
}
