#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Runtime.InteropServices;
using System.Text;
using NativeTooltips.Internal;
using UnityEngine;

namespace NativeTooltips.Backends
{
    /// <summary>
    /// macOS: real AppKit tooltips (NSView -addToolTipRect:owner:userData:).
    ///
    /// AppKit has no "show tooltip now" API and ignores tooltip rects added while the cursor is
    /// already inside them. NativeTooltipsMac.bundle (source in Plugins/macOS/Source~) uses the
    /// proven WebKit/Chromium technique: a transparent NSView on top of Unity's view captures the
    /// tooltip manager's tracking rect and sends it fake mouseExited/mouseEntered events whenever
    /// the text changes. Delay, look, placement and animation are 100% system-provided.
    ///
    /// AppKit tooltips can't be styled, so when a <see cref="TooltipStyle"/> (or a title, or
    /// <see cref="Tooltip.Show(string, float, TooltipStyle, string, TooltipIcon)"/>) needs more, the
    /// bundle shows a custom bubble instead: an NSPanel with the system tooltip material, font and
    /// colours. See <see cref="TooltipSettings.CustomWindowMode"/>.
    /// </summary>
    internal sealed class MacOSTooltipBackend : ITooltipBackend
    {
        const string Lib = "NativeTooltipsMac";
        const int ExpectedVersion = 3;
        const int ParamCount = 17; // must match NTC_Count in NativeTooltipsMac.mm

        readonly float[] _params = new float[ParamCount];
        readonly double[] _geometry = new double[11];
        float _initialDelay = 1f;
        bool _nativeVisible;
        bool _customVisible;
        Vector2 _customPos;

        public string Name => "macOS AppKit (NSToolTipManager + custom NSPanel)";
        public bool IsNative => true;

        public TooltipFeature Features =>
            TooltipSettings.CustomWindowMode == CustomWindowMode.Never ? TooltipFeature.None : TooltipFeature.AllCustom;

        public bool UsesSystemHoverDelay(in TooltipRequest request) => !CustomWindowPolicy.UseCustom(request, true);

        public float InitialDelay => _initialDelay;
        public float ReshowDelay => 0.1f;
        public float AutoPopDelay => 0f;
        public float LongPressTimeout => 0.5f;

        public bool Initialize()
        {
            int version = NTMac_Version();
            if (version != ExpectedVersion)
            {
                Debug.LogWarning($"[NativeTooltips] NativeTooltipsMac.bundle version {version}, expected {ExpectedVersion} — rebuild it with build_macos.sh.");
                return false;
            }

            _initialDelay = (float)NTMac_InitialDelaySeconds();
            return NTMac_Initialize() != 0;
        }

        public void Show(in TooltipRequest request)
        {
            if (CustomWindowPolicy.UseCustom(request, true))
            {
                if (_nativeVisible)
                {
                    NTMac_SetToolTip(null);
                    _nativeVisible = false;
                }

                ShowCustom(request);
                return;
            }

            if (_customVisible)
            {
                NTMac_CustomHide();
                _customVisible = false;
            }

            // AppKit can't style a title — it becomes a plain first line.
            NTMac_SetToolTip(Utf8(request.PlainTextWithTitle));
            _nativeVisible = true;
        }

        void ShowCustom(in TooltipRequest request)
        {
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
            string text = rich ? TooltipRichText.Encode(request.TextRuns ?? TooltipRichText.Plain(request.Text)) : request.Text;
            string title = string.IsNullOrEmpty(request.Title)
                ? null
                : rich ? TooltipRichText.Encode(request.TitleRuns ?? TooltipRichText.Plain(request.Title)) : request.Title;

            NTMac_CustomPrepare(Utf8(text), title == null ? null : Utf8(title), p, ParamCount, out double w, out double h);

            if (request.IsUpdate && _customVisible)
            {
                NTMac_CustomShowAt(_customPos.x, _customPos.y); // keep the position, new size
                return;
            }

            if (NTMac_GetGeometry(_geometry, _geometry.Length) == 0) return;
            double[] g = _geometry;
            var view = new Rect((float)g[0], (float)g[1], (float)g[2], (float)g[3]);
            var mouse = new Vector2((float)g[4], (float)g[5]);
            var work = new Rect((float)g[6], (float)g[7], (float)g[8], (float)g[9]);

            var anchor = new Rect(
                view.x + request.Anchor.x * view.width,
                view.y + request.Anchor.y * view.height,
                request.Anchor.width * view.width,
                request.Anchor.height * view.height);

            TooltipPlacement placement = TooltipLayout.Resolve(s.Placement, request.Trigger, request.HasAnchor);
            _customPos = TooltipLayout.Place(new LayoutInput
            {
                Placement = placement,
                Pointer = mouse,
                CursorHeight = (float)g[10] + 2f,
                HasAnchor = request.HasAnchor,
                Anchor = anchor,
                Size = new Vector2((float)w, (float)h),
                Offset = TooltipLayout.OffsetToPixels(s.Offset, 1f), // AppKit coordinates are points
                Gap = TooltipLayout.IsElementPlacement(placement) ? 6f : 4f,
                WorkArea = work,
                CenterOnPointer = false
            });

            NTMac_CustomShowAt(_customPos.x, _customPos.y);
            _customVisible = true;
        }

        public void Hide()
        {
            if (_nativeVisible)
            {
                NTMac_SetToolTip(null);
                _nativeVisible = false;
            }

            if (_customVisible)
            {
                NTMac_CustomHide();
                _customVisible = false;
            }
        }

        public void Tick() { }

        public void Shutdown()
        {
            try { NTMac_Shutdown(); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            _nativeVisible = _customVisible = false;
        }

        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes((s ?? string.Empty) + "\0");

        [DllImport(Lib)] static extern int NTMac_Version();
        [DllImport(Lib)] static extern int NTMac_Initialize();
        [DllImport(Lib)] static extern void NTMac_SetToolTip(byte[] utf8NullTerminated);
        [DllImport(Lib)] static extern double NTMac_InitialDelaySeconds();
        [DllImport(Lib)] static extern void NTMac_Shutdown();

        [DllImport(Lib)] static extern void NTMac_CustomPrepare(byte[] utf8Text, byte[] utf8Title, float[] parameters,
            int count, out double width, out double height);

        [DllImport(Lib)] static extern void NTMac_CustomShowAt(double x, double yDown);
        [DllImport(Lib)] static extern void NTMac_CustomHide();
        [DllImport(Lib)] static extern int NTMac_GetGeometry([Out] double[] values, int count);
    }
}
#endif
