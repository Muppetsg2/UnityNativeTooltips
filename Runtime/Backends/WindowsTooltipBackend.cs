#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AOT;
using NativeTooltips.Internal;
using UnityEngine;

namespace NativeTooltips.Backends
{
    /// <summary>
    /// Windows: the system tooltip control from Common Controls (comctl32.dll, class "tooltips_class32").
    /// Common Controls v6 is forced through an activation context (manifest), so the bubble uses the
    /// Windows 10/11 theme instead of the classic yellow rectangle.
    ///
    /// A "tracking" tool is used (TTF_TRACK | TTF_ABSOLUTE): we decide when and where, but the delays
    /// are read from the control itself (TTM_GETDELAYTIME), i.e. from system settings.
    /// No compiled library needed — just P/Invoke into user32/comctl32/kernel32/gdi32/uxtheme.
    ///
    /// Style support: font size/bold (WM_SETFONT based on the system status font), padding
    /// (TTM_SETMARGIN, added to the theme's own padding), max width, colors (themes ignore
    /// TTM_SETTIPBKCOLOR, so coloured tooltips use a separate, unthemed window), title + icon
    /// (TTM_SETTITLE), placement/offset and the balloon style (TTS_BALLOON).
    ///
    /// Each combination of (balloon, unthemed, rich) gets its own window that never changes theme:
    /// switching themes on a live window sends WM_THEMECHANGED, which resets the font set by WM_SETFONT.
    ///
    /// Rich text (WindowsTooltipBackend.RichText.cs): only text that actually contains tags uses a "rich"
    /// window whose text is custom-drawn (NM_CUSTOMDRAW); background, border, shadow and animation stay native.
    /// Plain text always takes the regular, fully native path.
    /// </summary>
    internal sealed partial class WindowsTooltipBackend : ITooltipBackend
    {
        /// <summary>One tooltip window (regular or balloon) with its single tracking tool.</summary>
        sealed class TipWindow
        {
            public IntPtr Hwnd;
            public TOOLINFOW Tool;
            public bool Visible;
            public bool Balloon;
            public bool Unthemed;
            public bool Rich;
            public RichLayout Layout; // rich windows only
            public RECT DefaultMargin;

            // what's currently applied (to avoid recreating fonts every show)
            public string AppliedKey;
            public string AppliedTitle;
            public TooltipIcon AppliedIcon;
            public IntPtr Font;
        }

        IntPtr _owner;
        IntPtr _actCtx = INVALID_HANDLE_VALUE;
        string _manifestPath;
        readonly TipWindow[] _windows = new TipWindow[8]; // index: (balloon ? 1 : 0) | (unthemed ? 2 : 0) | (rich ? 4 : 0)
        TipWindow _current;

        public string Name => "Win32 (comctl32 tooltips_class32)";
        public bool IsNative => true;

        public TooltipFeature Features =>
            TooltipFeature.FontSize | TooltipFeature.Bold | TooltipFeature.Padding | TooltipFeature.MaxWidth |
            TooltipFeature.Colors | TooltipFeature.Placement | TooltipFeature.Offset | TooltipFeature.ShowDelay |
            TooltipFeature.Title | TooltipFeature.Icon | TooltipFeature.Balloon | TooltipFeature.RichText;

        public bool UsesSystemHoverDelay(in TooltipRequest request) => false;
        public float InitialDelay { get; private set; } = 0.5f;
        public float ReshowDelay { get; private set; } = 0.1f;
        public float AutoPopDelay { get; private set; } = 5f;
        public float LongPressTimeout => 0.5f;

        public bool Initialize()
        {
            _owner = FindUnityWindow();
            TipWindow regular = GetWindow(false, false);
            if (regular == null) return false;

            // System delays (derived from GetDoubleClickTime() by default).
            IntPtr h = regular.Hwnd;
            InitialDelay = Ms(SendMessageW(h, TTM_GETDELAYTIME, (IntPtr)TTDT_INITIAL, IntPtr.Zero), 0.5f);
            ReshowDelay = Ms(SendMessageW(h, TTM_GETDELAYTIME, (IntPtr)TTDT_RESHOW, IntPtr.Zero), 0.1f);
            AutoPopDelay = Ms(SendMessageW(h, TTM_GETDELAYTIME, (IntPtr)TTDT_AUTOPOP, IntPtr.Zero), 5f);
            return true;
        }

        public void Show(in TooltipRequest request)
        {
            TooltipStyle style = request.Style ?? TooltipSettings.Style;

            // Rich (custom-drawn) window only when the text or title really contains rich text tags.
            bool rich = request.HasRichText;
            TipWindow tip = (rich ? GetWindow(style.WindowsBalloon, style.OverrideColors, true) : null)
                            ?? GetWindow(style.WindowsBalloon, style.OverrideColors)
                            ?? GetWindow(false, false);
            if (tip == null) return;

            if (_current != null && _current != tip) HideWindow(_current);
            _current = tip;

            float scale = DpiScale();
            ApplyStyle(tip, style, scale);
            // Rich windows draw the title themselves (see WindowsTooltipBackend.RichText.cs).
            ApplyTitle(tip, tip.Rich ? null : request.Title, tip.Rich ? TooltipIcon.None : request.Icon);
            if (tip.Rich) PrepareRich(tip, request, style, scale); // before the text: it triggers measuring
            SetText(tip, request.Text);

            if (request.IsUpdate && tip.Visible) return; // the control resizes itself; keep the position

            GetCursorPos(out POINT cursor);
            var pointer = new Vector2(cursor.x, cursor.y);
            Rect anchor = default;
            bool hasAnchor = request.HasAnchor && TryAnchorToScreen(request.Anchor, out anchor);

            TooltipPlacement placement = TooltipLayout.Resolve(style.Placement, request.Trigger, hasAnchor);
            Vector2 offset = TooltipLayout.OffsetToPixels(style.Offset, scale);
            float cursorHeight = GetSystemMetrics(SM_CYCURSOR) * 0.65f;

            if (tip.Balloon)
            {
                // Balloon: the track position is where the stem points and Windows positions the bubble
                // itself (it hangs BELOW the point, flipping above only when there's no room). No measuring
                // needed — and it must NOT be activated off-screen: the balloon's fit-to-monitor logic can't
                // handle a point outside every monitor (hangs, then shows at the top-left corner).
                Vector2 target = BalloonTarget(placement, pointer, cursorHeight, hasAnchor, anchor) + offset;
                SendMessageW(tip.Hwnd, TTM_TRACKPOSITION, IntPtr.Zero,
                    MakeLParam(Mathf.RoundToInt(target.x), Mathf.RoundToInt(target.y)));
                if (!tip.Visible)
                {
                    SendMessageW(tip.Hwnd, TTM_TRACKACTIVATE, (IntPtr)1, ref tip.Tool);
                    tip.Visible = true;
                }
            }
            else
            {
                // An inactive tracking tooltip doesn't re-measure itself after TTM_UPDATETIPTEXT, so
                // TTM_GETBUBBLESIZE would return the size of the PREVIOUS text (≈0 on the first show).
                // Activate it off-screen first (TTF_ABSOLUTE = placed exactly there, no fitting) and read
                // the real window size (includes title, margins, font).
                if (!tip.Visible)
                {
                    int hiddenX = GetSystemMetrics(SM_XVIRTUALSCREEN) - 4000;
                    int hiddenY = GetSystemMetrics(SM_YVIRTUALSCREEN) - 4000;
                    SendMessageW(tip.Hwnd, TTM_TRACKPOSITION, IntPtr.Zero, MakeLParam(hiddenX, hiddenY));
                    SendMessageW(tip.Hwnd, TTM_TRACKACTIVATE, (IntPtr)1, ref tip.Tool);
                    tip.Visible = true;
                }

                Vector2 size = MeasureWindow(tip);
                if (tip.Rich) size = EnsureRichSize(tip, size);

                Vector2 reference = TooltipLayout.IsElementPlacement(placement) && hasAnchor ? anchor.center : pointer;
                RECT work = WorkAreaAt(new POINT { x = Mathf.RoundToInt(reference.x), y = Mathf.RoundToInt(reference.y) });

                Vector2 pos = TooltipLayout.Place(new LayoutInput
                {
                    Placement = placement,
                    Pointer = pointer,
                    CursorHeight = cursorHeight,
                    HasAnchor = hasAnchor,
                    Anchor = anchor,
                    Size = size,
                    Offset = offset,
                    Gap = (TooltipLayout.IsElementPlacement(placement) ? 8f : 2f) * scale,
                    WorkArea = Rect.MinMaxRect(work.left, work.top, work.right, work.bottom),
                    CenterOnPointer = false
                });

                SendMessageW(tip.Hwnd, TTM_TRACKPOSITION, IntPtr.Zero,
                    MakeLParam(Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.y)));
                if (tip.Rich) EnsureRichSize(tip, size); // the control may re-measure on TTM_TRACKPOSITION
            }

            SetWindowPos(tip.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public void Hide()
        {
            foreach (TipWindow w in _windows)
                if (w != null) HideWindow(w);
        }

        public void Tick() { }

        public void Shutdown()
        {
            Hide();
            for (int i = 0; i < _windows.Length; i++)
            {
                DestroyTipWindow(_windows[i]);
                _windows[i] = null;
            }

            _current = null;
            DestroyNotifyWindow();

            if (_actCtx != INVALID_HANDLE_VALUE && _actCtx != IntPtr.Zero)
            {
                ReleaseActCtx(_actCtx);
                _actCtx = INVALID_HANDLE_VALUE;
            }

            if (_manifestPath != null)
            {
                try { File.Delete(_manifestPath); } catch { /* ignored */ }
                _manifestPath = null;
            }
        }

        // ------------------------------------------------------------------ tooltip windows

        TipWindow GetWindow(bool balloon, bool unthemed, bool rich = false)
        {
            int index = (balloon ? 1 : 0) | (unthemed ? 2 : 0) | (rich ? 4 : 0);
            return _windows[index] ?? (_windows[index] = CreateTipWindow(balloon, unthemed, rich));
        }

        TipWindow CreateTipWindow(bool balloon, bool unthemed, bool rich)
        {
            // Rich windows: our hidden notification window is both the tooltip's owner and the tool's hwnd,
            // so NM_CUSTOMDRAW reaches us whichever of the two comctl32 notifies.
            IntPtr toolOwner = _owner;
            if (rich)
            {
                toolOwner = EnsureNotifyWindow(_owner);
                if (toolOwner == IntPtr.Zero) return null; // falls back to the plain window
            }

            IntPtr hwnd;
            bool activated = TryActivateCommonControlsV6(out IntPtr cookie);
            try
            {
                // The comctl32 calls MUST happen inside the context — then the loader picks version 6
                // from WinSxS and the versioned "tooltips_class32" class is used.
                var icc = new INITCOMMONCONTROLSEX
                {
                    dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
                    dwICC = ICC_TAB_CLASSES | ICC_BAR_CLASSES
                };
                InitCommonControlsEx(ref icc);

                hwnd = CreateWindowExW(
                    WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                    TOOLTIPS_CLASS,
                    null,
                    WS_POPUP | TTS_NOPREFIX | TTS_ALWAYSTIP | (balloon ? TTS_BALLOON : 0u),
                    CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT,
                    toolOwner,
                    IntPtr.Zero,
                    GetModuleHandleW(null),
                    IntPtr.Zero);
            }
            finally
            {
                if (activated) DeactivateActCtx(0, cookie);
            }

            if (hwnd == IntPtr.Zero) return null;

            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            // Themes ignore TTM_SETTIPBKCOLOR — coloured tooltips need an unthemed window.
            // Done once, before any font is set, because a theme change resets the font.
            if (unthemed)
            {
                try { SetWindowTheme(hwnd, "", ""); }
                catch (DllNotFoundException) { /* no uxtheme (very old systems) */ }
            }

            var tip = new TipWindow
            {
                Hwnd = hwnd,
                Balloon = balloon,
                Unthemed = unthemed,
                Rich = rich,
                Tool = new TOOLINFOW
                {
                    cbSize = (uint)Marshal.SizeOf<TOOLINFOW>(),
                    // Balloons without TTF_ABSOLUTE point their stem at the track position.
                    // TTF_TRANSPARENT: the bubble returns HTTRANSPARENT, so the mouse passes through to Unity
                    // (otherwise a bubble covering another button would block hovering it).
                    uFlags = TTF_TRACK | TTF_TRANSPARENT | (balloon ? 0u : TTF_ABSOLUTE),
                    hwnd = toolOwner,
                    uId = (UIntPtr)1
                }
            };

            IntPtr empty = Marshal.StringToHGlobalUni(" ");
            try
            {
                tip.Tool.lpszText = empty;
                if (SendMessageW(hwnd, TTM_ADDTOOLW, IntPtr.Zero, ref tip.Tool) == IntPtr.Zero)
                {
                    // comctl32 v5 (no v6) doesn't know lpReserved — use the "V2" size.
                    tip.Tool.cbSize = (uint)(Marshal.SizeOf<TOOLINFOW>() - IntPtr.Size);
                    if (SendMessageW(hwnd, TTM_ADDTOOLW, IntPtr.Zero, ref tip.Tool) == IntPtr.Zero)
                    {
                        DestroyWindow(hwnd);
                        return null;
                    }
                }
            }
            finally
            {
                tip.Tool.lpszText = IntPtr.Zero;
                Marshal.FreeHGlobal(empty);
            }

            SendMessageW(hwnd, TTM_GETMARGIN, IntPtr.Zero, ref tip.DefaultMargin);
            if (rich) RegisterRich(tip);
            return tip;
        }

        static Vector2 MeasureWindow(TipWindow tip)
        {
            if (GetWindowRect(tip.Hwnd, out RECT r) && r.right > r.left && r.bottom > r.top)
                return new Vector2(r.right - r.left, r.bottom - r.top);

            // Fallback: the control's own estimate.
            long bubble = SendMessageW(tip.Hwnd, TTM_GETBUBBLESIZE, IntPtr.Zero, ref tip.Tool).ToInt64();
            return new Vector2(bubble & 0xFFFF, (bubble >> 16) & 0xFFFF);
        }

        static void DestroyTipWindow(TipWindow tip)
        {
            if (tip == null) return;
            if (tip.Rich) UnregisterRich(tip);
            if (tip.Hwnd != IntPtr.Zero) DestroyWindow(tip.Hwnd);
            if (tip.Font != IntPtr.Zero) DeleteObject(tip.Font);
            tip.Hwnd = IntPtr.Zero;
            tip.Font = IntPtr.Zero;
        }

        static void HideWindow(TipWindow tip)
        {
            if (tip.Hwnd == IntPtr.Zero || !tip.Visible) return;
            SendMessageW(tip.Hwnd, TTM_TRACKACTIVATE, IntPtr.Zero, ref tip.Tool);
            tip.Visible = false;
        }

        static void SetText(TipWindow tip, string text)
        {
            // The control copies the text — the buffer can be freed right away.
            IntPtr buffer = Marshal.StringToHGlobalUni((text ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n"));
            try
            {
                tip.Tool.lpszText = buffer;
                SendMessageW(tip.Hwnd, TTM_UPDATETIPTEXTW, IntPtr.Zero, ref tip.Tool);
            }
            finally
            {
                tip.Tool.lpszText = IntPtr.Zero;
                Marshal.FreeHGlobal(buffer);
            }
        }

        static void ApplyTitle(TipWindow tip, string title, TooltipIcon icon)
        {
            title = title ?? string.Empty;
            if (title.Length > 99) title = title.Substring(0, 99); // TTM_SETTITLE limit: 100 chars incl. NUL
            if (string.IsNullOrEmpty(title)) icon = TooltipIcon.None; // an icon needs a title
            if (title == (tip.AppliedTitle ?? string.Empty) && icon == tip.AppliedIcon) return;

            SendMessageW(tip.Hwnd, TTM_SETTITLEW, (IntPtr)(int)icon, title);
            tip.AppliedTitle = title;
            tip.AppliedIcon = icon;
        }

        void ApplyStyle(TipWindow tip, TooltipStyle s, float scale)
        {
            uint dpi = (uint)Mathf.RoundToInt(96f * scale);
            string key = string.Concat(
                s.FontScale.ToString("R"), "|", s.FontSize.ToString("R"), "|", s.Bold ? "b" : "n", "|",
                s.Padding.x.ToString("R"), ",", s.Padding.y.ToString("R"), "|", s.MaxWidth.ToString("R"), "|",
                s.OverrideColors ? ColorUtility.ToHtmlStringRGB(s.BackgroundColor) + ColorUtility.ToHtmlStringRGB(s.TextColor) : "-",
                "|", dpi.ToString());
            if (key == tip.AppliedKey) return;
            tip.AppliedKey = key;

            // Font --------------------------------------------------------------
            IntPtr oldFont = tip.Font;
            tip.Font = IntPtr.Zero;
            if (!Mathf.Approximately(s.FontScale, 1f) || s.FontSize > 0f || s.Bold)
            {
                if (TryGetTooltipFont(dpi, out LOGFONTW lf))
                {
                    // lfHeight < 0 = character height in pixels; style sizes are 96-dpi pixels.
                    float systemSize = Mathf.Abs(lf.lfHeight) * 96f / dpi;
                    float size = s.ResolveFontSize(systemSize);
                    lf.lfHeight = -Mathf.Max(1, Mathf.RoundToInt(size * dpi / 96f));
                    lf.lfWidth = 0;
                    if (s.Bold) lf.lfWeight = 700;
                    tip.Font = CreateFontIndirectW(ref lf);
                }
            }

            SendMessageW(tip.Hwnd, WM_SETFONT, tip.Font, (IntPtr)1);
            if (oldFont != IntPtr.Zero) DeleteObject(oldFont);

            // Colors (only on the unthemed window) ----------------------------------
            if (tip.Unthemed && s.OverrideColors)
            {
                SendMessageW(tip.Hwnd, TTM_SETTIPBKCOLOR, (IntPtr)ToColorRef(s.BackgroundColor), IntPtr.Zero);
                SendMessageW(tip.Hwnd, TTM_SETTIPTEXTCOLOR, (IntPtr)ToColorRef(s.TextColor), IntPtr.Zero);
            }

            // Padding (added to the theme's own content margins) -------------------
            RECT margin = tip.DefaultMargin;
            if (s.Padding.x >= 0f) margin.left = margin.right = Mathf.RoundToInt(s.Padding.x * scale);
            if (s.Padding.y >= 0f) margin.top = margin.bottom = Mathf.RoundToInt(s.Padding.y * scale);
            SendMessageW(tip.Hwnd, TTM_SETMARGIN, IntPtr.Zero, ref margin);

            // Max width — also required for "\n" and word wrapping -----------------
            int maxWidth = Mathf.RoundToInt(s.ResolveMaxWidth(400f) * scale);
            SendMessageW(tip.Hwnd, TTM_SETMAXTIPWIDTH, IntPtr.Zero, (IntPtr)maxWidth);
        }

        static bool TryGetTooltipFont(uint dpi, out LOGFONTW font)
        {
            // Tooltips use the "status" font from NONCLIENTMETRICS.
            var ncm = new NONCLIENTMETRICSW { cbSize = (uint)Marshal.SizeOf<NONCLIENTMETRICSW>() };
            bool ok;
            try
            {
                ok = SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, ncm.cbSize, ref ncm, 0, dpi);
            }
            catch (EntryPointNotFoundException)
            {
                ok = SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, ncm.cbSize, ref ncm, 0); // Windows < 10 1607
            }

            font = ncm.lfStatusFont;
            return ok && font.lfHeight != 0;
        }

        static Vector2 BalloonTarget(TooltipPlacement placement, Vector2 pointer, float cursorHeight, bool hasAnchor, Rect a)
        {
            if (!TooltipLayout.IsElementPlacement(placement) || !hasAnchor)
                return new Vector2(pointer.x, pointer.y + cursorHeight); // tip of the stem just below the cursor

            switch (placement)
            {
                case TooltipPlacement.LeftOfElement: return new Vector2(a.xMin, a.yMax);
                case TooltipPlacement.RightOfElement: return new Vector2(a.xMax, a.yMax);
                default: return new Vector2(a.center.x, a.yMax); // Above/BelowElement: bottom centre
            }
        }

        static uint ToColorRef(Color c)
        {
            Color32 c32 = c;
            return (uint)(c32.r | (c32.g << 8) | (c32.b << 16));
        }

        // ------------------------------------------------------------------ helpers

        bool TryActivateCommonControlsV6(out IntPtr cookie)
        {
            cookie = IntPtr.Zero;
            try
            {
                if (_actCtx == INVALID_HANDLE_VALUE)
                {
                    _manifestPath = Path.Combine(Application.temporaryCachePath,
                        $"NativeTooltips.comctl6.{Guid.NewGuid():N}.manifest");
                    File.WriteAllText(_manifestPath, CommonControlsManifest, new UTF8Encoding(false));

                    var ctx = new ACTCTXW
                    {
                        cbSize = (uint)Marshal.SizeOf<ACTCTXW>(),
                        lpSource = _manifestPath
                    };
                    _actCtx = CreateActCtxW(ref ctx);
                }

                return _actCtx != INVALID_HANDLE_VALUE && ActivateActCtx(_actCtx, out cookie);
            }
            catch (Exception e)
            {
                BackendFactory.Log("Common Controls v6 activation context unavailable: " + e.Message);
                return false;
            }
        }

        bool TryAnchorToScreen(Rect normalized, out Rect result)
        {
            result = default;
            if (_owner == IntPtr.Zero || !GetClientRect(_owner, out RECT client)) return false;

            var origin = new POINT();
            if (!ClientToScreen(_owner, ref origin)) return false;

            int w = client.right - client.left, h = client.bottom - client.top;
            result = Rect.MinMaxRect(
                origin.x + normalized.xMin * w,
                origin.y + normalized.yMin * h,
                origin.x + normalized.xMax * w,
                origin.y + normalized.yMax * h);
            return true;
        }

        static RECT WorkAreaAt(POINT p)
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            IntPtr monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info)) return info.rcWork;
            return new RECT { left = -100000, top = -100000, right = 100000, bottom = 100000 };
        }

        float DpiScale()
        {
            try
            {
                uint dpi = _owner != IntPtr.Zero ? GetDpiForWindow(_owner) : 96u;
                return dpi > 0 ? dpi / 96f : 1f;
            }
            catch (EntryPointNotFoundException)
            {
                return 1f; // Windows < 10 1607
            }
        }

        static float Ms(IntPtr value, float fallback)
        {
            long ms = value.ToInt64();
            return ms > 0 ? ms / 1000f : fallback;
        }

        static IntPtr MakeLParam(int lo, int hi) => (IntPtr)((hi << 16) | (lo & 0xFFFF));

        // --------------------------------------------- finding Unity's window

        static IntPtr s_FoundWindow;
        static IntPtr s_FirstVisibleWindow;
        static uint s_Pid;

        static IntPtr FindUnityWindow()
        {
            s_FoundWindow = IntPtr.Zero;
            s_FirstVisibleWindow = IntPtr.Zero;
            s_Pid = GetCurrentProcessId();
            EnumWindows(EnumWindowsCallback, IntPtr.Zero);
            return s_FoundWindow != IntPtr.Zero ? s_FoundWindow : s_FirstVisibleWindow;
        }

        [MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != s_Pid || !IsWindowVisible(hwnd)) return true;

            var cls = new StringBuilder(64);
            GetClassNameW(hwnd, cls, cls.Capacity);
            string name = cls.ToString();

            // Player: "UnityWndClass"; Editor: "UnityContainerWndClass".
            if (name == "UnityWndClass" || name == "UnityContainerWndClass")
            {
                s_FoundWindow = hwnd;
                return false;
            }

            if (s_FirstVisibleWindow == IntPtr.Zero) s_FirstVisibleWindow = hwnd;
            return true;
        }

        // ------------------------------------------------------------------ Win32

        const string CommonControlsManifest =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">\n" +
            "  <dependency>\n" +
            "    <dependentAssembly>\n" +
            "      <assemblyIdentity type=\"win32\" name=\"Microsoft.Windows.Common-Controls\" version=\"6.0.0.0\" " +
            "processorArchitecture=\"*\" publicKeyToken=\"6595b64144ccf1df\" language=\"*\"/>\n" +
            "    </dependentAssembly>\n" +
            "  </dependency>\n" +
            "</assembly>\n";

        const string TOOLTIPS_CLASS = "tooltips_class32";
        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_TOPMOST = 0x00000008;
        const uint WS_EX_TOOLWINDOW = 0x00000080;
        const int CW_USEDEFAULT = unchecked((int)0x80000000);
        const uint TTS_ALWAYSTIP = 0x01;
        const uint TTS_NOPREFIX = 0x02;
        const uint TTS_BALLOON = 0x40;

        const uint ICC_BAR_CLASSES = 0x00000004;
        const uint ICC_TAB_CLASSES = 0x00000008;

        const uint TTF_TRACK = 0x0020;
        const uint TTF_ABSOLUTE = 0x0080;
        const uint TTF_TRANSPARENT = 0x0100;

        const uint WM_SETFONT = 0x0030;
        const uint WM_USER = 0x0400;
        const uint TTM_TRACKACTIVATE = WM_USER + 17;
        const uint TTM_TRACKPOSITION = WM_USER + 18;
        const uint TTM_SETTIPBKCOLOR = WM_USER + 19;
        const uint TTM_SETTIPTEXTCOLOR = WM_USER + 20;
        const uint TTM_GETDELAYTIME = WM_USER + 21;
        const uint TTM_SETMAXTIPWIDTH = WM_USER + 24;
        const uint TTM_SETMARGIN = WM_USER + 26;
        const uint TTM_GETMARGIN = WM_USER + 27;
        const uint TTM_GETBUBBLESIZE = WM_USER + 30;
        const uint TTM_SETTITLEW = WM_USER + 33;
        const uint TTM_ADDTOOLW = WM_USER + 50;
        const uint TTM_UPDATETIPTEXTW = WM_USER + 57;

        const int TTDT_RESHOW = 1;
        const int TTDT_AUTOPOP = 2;
        const int TTDT_INITIAL = 3;

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int SM_CYCURSOR = 14;
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const uint SPI_GETNONCLIENTMETRICS = 0x0029;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int left, top, right, bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int x, y;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INITCOMMONCONTROLSEX
        {
            public uint dwSize;
            public uint dwICC;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TOOLINFOW
        {
            public uint cbSize;
            public uint uFlags;
            public IntPtr hwnd;
            public UIntPtr uId;
            public RECT rect;
            public IntPtr hinst;
            public IntPtr lpszText;
            public IntPtr lParam;
            public IntPtr lpReserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct LOGFONTW
        {
            public int lfHeight;
            public int lfWidth;
            public int lfEscapement;
            public int lfOrientation;
            public int lfWeight;
            public byte lfItalic;
            public byte lfUnderline;
            public byte lfStrikeOut;
            public byte lfCharSet;
            public byte lfOutPrecision;
            public byte lfClipPrecision;
            public byte lfQuality;
            public byte lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NONCLIENTMETRICSW
        {
            public uint cbSize;
            public int iBorderWidth;
            public int iScrollWidth;
            public int iScrollHeight;
            public int iCaptionWidth;
            public int iCaptionHeight;
            public LOGFONTW lfCaptionFont;
            public int iSmCaptionWidth;
            public int iSmCaptionHeight;
            public LOGFONTW lfSmCaptionFont;
            public int iMenuWidth;
            public int iMenuHeight;
            public LOGFONTW lfMenuFont;
            public LOGFONTW lfStatusFont;
            public LOGFONTW lfMessageFont;
            public int iPaddedBorderWidth;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ACTCTXW
        {
            public uint cbSize;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpSource;
            public ushort wProcessorArchitecture;
            public ushort wLangId;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpAssemblyDirectory;
            public IntPtr lpResourceName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpApplicationName;
            public IntPtr hModule;
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("comctl32.dll", SetLastError = true)]
        static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, ref TOOLINFOW lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, ref RECT lParam);


        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SystemParametersInfoForDpi(uint action, uint param, ref NONCLIENTMETRICSW value, uint winIni, uint dpi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SystemParametersInfoW(uint action, uint param, ref NONCLIENTMETRICSW value, uint winIni);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFontIndirectW(ref LOGFONTW font);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateActCtxW(ref ACTCTXW actctx);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ActivateActCtx(IntPtr actCtx, out IntPtr cookie);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeactivateActCtx(uint flags, IntPtr cookie);

        [DllImport("kernel32.dll")]
        static extern void ReleaseActCtx(IntPtr actCtx);
    }
}
#endif
