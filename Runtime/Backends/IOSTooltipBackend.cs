#if UNITY_IOS
using System.Runtime.InteropServices;
using System.Text;
using NativeTooltips.Internal;
using UnityEngine;

namespace NativeTooltips.Backends
{
    /// <summary>
    /// iOS/iPadOS: UIKit UIPopoverPresentationController (Plugins/iOS/NativeTooltips_iOS.mm,
    /// compiled by Xcode together with the project — nothing to build manually).
    /// Placement maps to the popover arrow direction; the offset shifts the arrow target.
    /// Corner radius is not available (UIKit draws the popover frame).
    /// </summary>
    internal sealed class IOSTooltipBackend : ITooltipBackend
    {
        // Must match the enum in NativeTooltips_iOS.mm.
        const int ParamCount = 24;

        const int ArrowUp = 1, ArrowDown = 2, ArrowLeft = 4, ArrowRight = 8;

        readonly float[] _params = new float[ParamCount];
        float _longPress = 0.5f;

        public string Name => "iOS UIKit (UIPopoverPresentationController)";
        public bool IsNative => true;

        public TooltipFeature Features => TooltipFeature.AllCustom & ~TooltipFeature.CornerRadius; // includes RichText

        public bool UsesSystemHoverDelay(in TooltipRequest request) => false;
        public float InitialDelay => 0.5f;
        public float ReshowDelay => 0.1f;
        public float AutoPopDelay => 0f;
        public float LongPressTimeout => _longPress;

        public bool Initialize()
        {
            float d = NativeTooltips_iOS_LongPressDuration();
            if (d > 0f) _longPress = d;
            return true;
        }

        public void Show(in TooltipRequest request)
        {
            TooltipStyle s = request.Style ?? TooltipSettings.Style;
            TooltipPlacement placement = TooltipLayout.Resolve(s.Placement, request.Trigger, request.HasAnchor);
            bool useElement = request.HasAnchor &&
                              (TooltipLayout.IsElementPlacement(placement) || s.Placement == TooltipPlacement.Auto);

            // Arrow target: the element, or a tiny rect at the pointer.
            Rect a = useElement
                ? request.Anchor
                : new Rect(request.Pointer.x, request.Pointer.y, 0.001f, 0.001f);

            float[] p = _params;
            p[0] = a.x; p[1] = a.y; p[2] = a.width; p[3] = a.height;
            p[4] = request.Trigger == TooltipTrigger.LongPress && TooltipSettings.HapticFeedback ? 1f : 0f;
            p[5] = s.FontSize;
            p[6] = s.FontScale;
            p[7] = s.Bold ? 1f : 0f;
            p[8] = s.Padding.x;
            p[9] = s.Padding.y;
            p[10] = s.MaxWidth;
            p[11] = s.OverrideColors ? 1f : 0f;
            p[12] = s.BackgroundColor.r; p[13] = s.BackgroundColor.g; p[14] = s.BackgroundColor.b; p[15] = s.BackgroundColor.a;
            p[16] = s.TextColor.r; p[17] = s.TextColor.g; p[18] = s.TextColor.b; p[19] = s.TextColor.a;
            p[20] = s.Placement == TooltipPlacement.Auto ? 0f : ArrowMask(placement);
            p[21] = s.Offset.x;
            p[22] = -s.Offset.y; // UIKit: y down

            // Rich text: both strings go encoded so the title keeps its own formatting too.
            bool rich = request.HasRichText;
            p[23] = rich ? 1f : 0f;
            string text = rich ? TooltipRichText.Encode(request.TextRuns ?? TooltipRichText.Plain(request.Text)) : request.Text;
            string title = string.IsNullOrEmpty(request.Title)
                ? null
                : rich ? TooltipRichText.Encode(request.TitleRuns ?? TooltipRichText.Plain(request.Title)) : request.Title;

            NativeTooltips_iOS_Show(Utf8(text), title == null ? null : Utf8(title), p, ParamCount);
        }

        static float ArrowMask(TooltipPlacement placement)
        {
            switch (placement)
            {
                case TooltipPlacement.AboveCursor:
                case TooltipPlacement.AboveElement:
                    return ArrowDown;      // bubble above → arrow points down
                case TooltipPlacement.LeftOfElement:
                    return ArrowRight;
                case TooltipPlacement.RightOfElement:
                    return ArrowLeft;
                default:
                    return ArrowUp;        // below
            }
        }

        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes((s ?? string.Empty) + "\0");

        public void Hide() => NativeTooltips_iOS_Hide();
        public void Tick() { }
        public void Shutdown() => NativeTooltips_iOS_Hide();

        [DllImport("__Internal")]
        static extern void NativeTooltips_iOS_Show(byte[] utf8Text, byte[] utf8Title, float[] parameters, int count);

        [DllImport("__Internal")]
        static extern void NativeTooltips_iOS_Hide();

        [DllImport("__Internal")]
        static extern float NativeTooltips_iOS_LongPressDuration();
    }
}
#endif
