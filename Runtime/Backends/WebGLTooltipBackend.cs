#if UNITY_WEBGL
using System.Runtime.InteropServices;
using System.Text;
using NativeTooltips.Internal;
using UnityEngine;

namespace NativeTooltips.Backends
{
    /// <summary>
    /// WebGL: the browser's native tooltip via the <c>title</c> attribute of the &lt;canvas&gt; element
    /// (Plugins/WebGL/NativeTooltips.jslib).
    ///
    /// The title attribute can't be styled, placed, shown on demand or shown for touch, so for those
    /// cases an HTML bubble is used instead (see <see cref="TooltipSettings.CustomWindowMode"/>); it copies
    /// the detected browser's own tooltip or the OS tooltip (<see cref="TooltipSettings.WebGLBubbleTheme"/>).
    /// Touch long-press always uses it.
    /// </summary>
    internal sealed class WebGLTooltipBackend : ITooltipBackend
    {
        const int ParamCount = 18; // must match the jslib

        readonly float[] _params = new float[ParamCount];
        bool _titleSet;
        bool _divVisible;
        Vector2 _divPos;

        string _environment;

        public string Name => string.IsNullOrEmpty(_environment)
            ? "Browser (HTML title attribute + styled bubble)"
            : "Browser: " + _environment + " (HTML title attribute + styled bubble)";
        public bool IsNative => true;

        public TooltipFeature Features =>
            TooltipSettings.CustomWindowMode == CustomWindowMode.Never ? TooltipFeature.None : TooltipFeature.AllCustom;

        public bool UsesSystemHoverDelay(in TooltipRequest request) => !UseBubble(request);
        public float InitialDelay => 0.5f;
        public float ReshowDelay => 0.1f;
        public float AutoPopDelay => 0f;
        public float LongPressTimeout => 0.5f;

        static bool UseBubble(in TooltipRequest request) =>
            request.Trigger == TooltipTrigger.LongPress || CustomWindowPolicy.UseCustom(request, true);

        public bool Initialize()
        {
            if (NativeTooltips_IsAvailable() == 0) return false;
            _environment = NativeTooltips_Environment(); // e.g. "Opera GX, Windows"
            return true;
        }

        public void Show(in TooltipRequest request)
        {
            if (!UseBubble(request))
            {
                HideDiv();
                NativeTooltips_SetTitle(Utf8(request.PlainTextWithTitle));
                _titleSet = true;
                return;
            }

            ClearTitle();

            TooltipStyle s = request.Style ?? TooltipSettings.Style;
            float[] p = _params;
            p[0] = s.FontSize;
            p[1] = s.FontScale;
            p[2] = s.Bold ? 1f : 0f;
            p[3] = s.Padding.x;
            p[4] = s.Padding.y;
            p[5] = s.MaxWidth;
            p[6] = s.OverrideColors ? 1f : 0f;
            p[7] = s.BackgroundColor.r; p[8] = s.BackgroundColor.g; p[9] = s.BackgroundColor.b; p[10] = s.BackgroundColor.a;
            p[11] = s.TextColor.r; p[12] = s.TextColor.g; p[13] = s.TextColor.b; p[14] = s.TextColor.a;
            p[15] = s.CornerRadius;

            bool rich = request.HasRichText;
            p[16] = rich ? 1f : 0f;
            p[17] = TooltipSettings.WebGLBubbleTheme == WebGLBubbleTheme.OperatingSystem ? 1f : 0f;
            string text = rich ? TooltipRichText.Encode(request.TextRuns ?? TooltipRichText.Plain(request.Text)) : request.Text;
            string title = string.IsNullOrEmpty(request.Title)
                ? null
                : rich ? TooltipRichText.Encode(request.TitleRuns ?? TooltipRichText.Plain(request.Title)) : request.Title;

            if (NativeTooltips_DivPrepare(Utf8(text), title == null ? null : Utf8(title), p, ParamCount) == 0) return;

            if (request.IsUpdate && _divVisible)
            {
                NativeTooltips_DivShowAt(_divPos.x, _divPos.y);
                return;
            }

            if (NativeTooltips_DivSnapshot() == 0) return;
            var canvas = new Rect(
                (float)NativeTooltips_DivGeometry(0), (float)NativeTooltips_DivGeometry(1),
                (float)NativeTooltips_DivGeometry(2), (float)NativeTooltips_DivGeometry(3));
            var viewport = new Rect(0f, 0f, (float)NativeTooltips_DivGeometry(4), (float)NativeTooltips_DivGeometry(5));
            var size = new Vector2((float)NativeTooltips_DivSize(0), (float)NativeTooltips_DivSize(1));

            var pointer = new Vector2(canvas.x + request.Pointer.x * canvas.width, canvas.y + request.Pointer.y * canvas.height);
            var anchor = new Rect(
                canvas.x + request.Anchor.x * canvas.width,
                canvas.y + request.Anchor.y * canvas.height,
                request.Anchor.width * canvas.width,
                request.Anchor.height * canvas.height);

            bool touch = request.Trigger == TooltipTrigger.LongPress;
            TooltipPlacement placement = TooltipLayout.Resolve(s.Placement, request.Trigger, request.HasAnchor);
            _divPos = TooltipLayout.Place(new LayoutInput
            {
                Placement = placement,
                Pointer = pointer,
                CursorHeight = touch ? 16f : 20f,
                HasAnchor = request.HasAnchor,
                Anchor = anchor,
                Size = size,
                Offset = TooltipLayout.OffsetToPixels(s.Offset, 1f), // CSS pixels
                Gap = touch ? 16f : TooltipLayout.IsElementPlacement(placement) ? 8f : 4f,
                WorkArea = new Rect(4f, 4f, viewport.width - 8f, viewport.height - 8f),
                CenterOnPointer = touch
            });

            NativeTooltips_DivShowAt(_divPos.x, _divPos.y);
            _divVisible = true;
        }

        public void Hide()
        {
            ClearTitle();
            HideDiv();
        }

        public void Tick() { }

        public void Shutdown() => Hide();

        void ClearTitle()
        {
            if (!_titleSet) return;
            NativeTooltips_ClearTitle();
            _titleSet = false;
        }

        void HideDiv()
        {
            if (!_divVisible) return;
            NativeTooltips_DivHide();
            _divVisible = false;
        }

        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes((s ?? string.Empty) + "\0");

        [DllImport("__Internal")] static extern int NativeTooltips_SetTitle(byte[] utf8);
        [DllImport("__Internal")] static extern void NativeTooltips_ClearTitle();
        [DllImport("__Internal")] static extern int NativeTooltips_IsAvailable();
        [DllImport("__Internal")] static extern string NativeTooltips_Environment();
        [DllImport("__Internal")] static extern int NativeTooltips_DivPrepare(byte[] text, byte[] title, float[] parameters, int count);
        [DllImport("__Internal")] static extern double NativeTooltips_DivSize(int index);
        [DllImport("__Internal")] static extern int NativeTooltips_DivSnapshot();
        [DllImport("__Internal")] static extern double NativeTooltips_DivGeometry(int index);
        [DllImport("__Internal")] static extern void NativeTooltips_DivShowAt(float x, float y);
        [DllImport("__Internal")] static extern void NativeTooltips_DivHide();
    }
}
#endif
