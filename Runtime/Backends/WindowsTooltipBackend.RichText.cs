#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;

namespace NativeTooltips.Backends
{
    /// <summary>
    /// Rich text for the Windows tooltip control.
    ///
    /// tooltips_class32 only draws plain text, but it supports custom draw: it sends NM_CUSTOMDRAW (as
    /// WM_NOTIFY) to its owner window. Rich windows are owned by our own hidden window (also used as the
    /// tool's hwnd) and:
    ///  • measuring pass (CDDS_PREPAINT with DT_CALCRECT) — we lay out the runs and return the text size,
    ///    so Windows sizes the bubble for the formatted text;
    ///  • painting pass — the control draws the native background / border (themed or classic), its own
    ///    text is suppressed (DT_CALCRECT), and in CDDS_POSTPAINT we draw the runs with GDI using the
    ///    tooltip's font in bold / italic / underline / strike / size variants and per-run colours.
    /// The title (+ system icon) is laid out and drawn by us too: once custom draw reports the text size,
    /// the control sizes the bubble from it alone and leaves no room for a TTM_SETTITLE title.
    /// A GDI-measured fallback resizes the window if a Windows version skips the measuring notification.
    /// </summary>
    internal sealed partial class WindowsTooltipBackend
    {
        // ------------------------------------------------------------------ layout of runs

        /// <summary>Lays out and draws formatted runs with GDI (word wrap at the tooltip's max width).</summary>
        sealed class RichLayout : IDisposable
        {
            struct FontInfo
            {
                public IntPtr Handle;
                public int Ascent;
                public int Descent;
            }

            struct Piece
            {
                public string Text;
                public int Font;
                public int X;
                public int Line;
                public bool HasColor;
                public uint ColorRef;
                public bool IsTitle;
            }

            struct Line
            {
                public int Top;
                public int Ascent;
                public int Descent;
                public int Width;
                public int Gap;        // extra space above the line
                public int MinHeight;  // e.g. the icon next to the first title line
                public int Pad;        // vertical centring when MinHeight > text height
                public int Height => Math.Max(Ascent + Descent, MinHeight);
            }

            readonly List<RichTextRun> _runs;
            readonly List<RichTextRun> _titleRuns; // null = no title
            readonly TooltipIcon _icon;
            readonly IntPtr _hwnd;
            readonly bool _themed;
            readonly bool _balloon;
            LOGFONTW _titleBase;
            readonly int _maxWidth;
            readonly uint _dpi;
            readonly List<FontInfo> _fonts = new List<FontInfo>();
            readonly Dictionary<string, int> _fontIndex = new Dictionary<string, int>();
            readonly List<Piece> _pieces = new List<Piece>();
            readonly List<Line> _lines = new List<Line>();
            LOGFONTW _base;
            IntPtr _baseHandle;
            bool _laidOut;
            int _titleFirstLine = -1;
            IntPtr _iconHandle;
            int _iconSize;

            public int Width { get; private set; }
            public int Height { get; private set; }

            /// <param name="titleRuns">
            /// Drawn by us (bold) instead of TTM_SETTITLE: with custom-drawn text the control sizes the bubble
            /// from our text size alone and would leave no room for its own title.
            /// </param>
            public RichLayout(List<RichTextRun> runs, List<RichTextRun> titleRuns, TooltipIcon icon, int maxWidth, uint dpi,
                IntPtr hwnd, bool themed, bool balloon)
            {
                _hwnd = hwnd;
                _themed = themed;
                _balloon = balloon;
                _runs = runs;
                _titleRuns = titleRuns;
                _icon = titleRuns != null ? icon : TooltipIcon.None; // like TTM_SETTITLE: an icon needs a title
                _maxWidth = Math.Max(20, maxWidth);
                _dpi = dpi == 0 ? 96u : dpi;
            }

            int Px(float logical) => Mathf.RoundToInt(logical * _dpi / 96f);

            /// <summary>Lays out the title and the runs with the tooltip's current font. Cheap when nothing changed.</summary>
            public void Layout(IntPtr hdc, IntPtr tooltipFont)
            {
                if (_laidOut && tooltipFont == _baseHandle) return;
                DisposeFonts();
                _baseHandle = tooltipFont;
                if (tooltipFont == IntPtr.Zero || GetObjectW(tooltipFont, Marshal.SizeOf<LOGFONTW>(), out _base) == 0)
                {
                    if (!TryGetTooltipFont(_dpi, out _base)) _base = new LOGFONTW { lfHeight = -12, lfWeight = 400, lfFaceName = "Segoe UI" };
                }

                // Title font as the control draws it: the theme's title font (standard: bold, balloon: larger,
                // regular weight); unthemed: the tooltip font in bold.
                _titleBase = _base;
                _titleBase.lfWeight = 700;
                if (_titleRuns != null && _themed && TryGetThemeTitleFont(_hwnd, hdc, _balloon, _dpi, out LOGFONTW themeTitle))
                    _titleBase = themeTitle;

                _pieces.Clear();
                _lines.Clear();
                _titleFirstLine = -1;
                var plainRun = new RichTextRun { SizeScale = 1f }; // default(RichTextRun) would mean "scale 0"
                int baseFont = FontFor(hdc, plainRun, false);
                IntPtr oldFont = SelectObject(hdc, _fonts[baseFont].Handle);

                int bodyIndent = 0;
                if (_titleRuns != null)
                {
                    int minHeight = 0;
                    if (_icon != TooltipIcon.None)
                    {
                        _iconSize = Px(16f);
                        _iconHandle = LoadSystemIcon(_icon, _iconSize);
                        if (_iconHandle != IntPtr.Zero)
                        {
                            bodyIndent = _iconSize + Px(6f);
                            minHeight = _iconSize;
                        }
                    }

                    // Like the native control: text lines start where the title starts (right of the icon),
                    // directly below it (no extra gap).
                    _titleFirstLine = _lines.Count;
                    LayoutRuns(hdc, _titleRuns, true, bodyIndent, FontFor(hdc, plainRun, true), 0, minHeight);
                }

                LayoutRuns(hdc, _runs, false, bodyIndent, baseFont, 0, 0);
                SelectObject(hdc, oldFont);

                int w = 0, h = 0;
                foreach (Line l in _lines)
                {
                    w = Math.Max(w, l.Width);
                    h = l.Top + l.Height;
                }

                Width = w;
                Height = h;
                _laidOut = true;
            }

            void LayoutRuns(IntPtr hdc, List<RichTextRun> runs, bool title, int indent, int baseFont, int gapBefore, int firstMinHeight)
            {
                var line = NewLine(baseFont);
                line.Gap = gapBefore;
                line.MinHeight = firstMinHeight;
                int maxWidth = Math.Max(20, _maxWidth - indent);
                int x = 0;

                foreach (RichTextRun run in runs)
                {
                    int font = FontFor(hdc, run, title);
                    uint color = run.HasColor ? ToColorRef(run.Color) : 0u;
                    string text = (run.Text ?? string.Empty).Replace("\r\n", "\n");

                    int i = 0;
                    while (i < text.Length)
                    {
                        if (text[i] == '\n')
                        {
                            EndLine(ref line);
                            line = NewLine(baseFont);
                            x = 0;
                            i++;
                            continue;
                        }

                        // token = word + following spaces
                        int start = i;
                        while (i < text.Length && text[i] != ' ' && text[i] != '\n') i++;
                        int wordEnd = i;
                        while (i < text.Length && text[i] == ' ') i++;
                        string token = text.Substring(start, i - start);
                        string word = text.Substring(start, wordEnd - start);

                        SelectObject(hdc, _fonts[font].Handle);
                        int wordWidth = Measure(hdc, word);
                        int tokenWidth = Measure(hdc, token);

                        if (x > 0 && x + wordWidth > maxWidth)
                        {
                            EndLine(ref line);
                            line = NewLine(baseFont);
                            x = 0;
                            if (word.Length == 0) continue; // only spaces at the start of a wrapped line
                        }

                        _pieces.Add(new Piece
                        {
                            Text = token, Font = font, X = indent + x, Line = _lines.Count,
                            HasColor = run.HasColor, ColorRef = color, IsTitle = title
                        });
                        line.Ascent = Math.Max(line.Ascent, _fonts[font].Ascent);
                        line.Descent = Math.Max(line.Descent, _fonts[font].Descent);
                        line.Width = Math.Max(line.Width, indent + x + wordWidth); // trailing spaces don't count
                        x += tokenWidth;
                    }
                }

                EndLine(ref line);
            }

            Line NewLine(int baseFont) => new Line { Ascent = _fonts[baseFont].Ascent, Descent = _fonts[baseFont].Descent };

            void EndLine(ref Line line)
            {
                int top = 0;
                if (_lines.Count > 0)
                {
                    Line prev = _lines[_lines.Count - 1];
                    top = prev.Top + prev.Height;
                }

                line.Top = top + line.Gap;
                line.Pad = (line.Height - (line.Ascent + line.Descent)) / 2;
                _lines.Add(line);
            }

            public void Draw(IntPtr hdc, RECT rc, uint defaultColor, uint titleColor)
            {
                if (_iconHandle != IntPtr.Zero && _titleFirstLine >= 0 && _titleFirstLine < _lines.Count)
                {
                    Line l = _lines[_titleFirstLine];
                    DrawIconEx(hdc, rc.left, rc.top + l.Top + (l.Height - _iconSize) / 2, _iconHandle,
                        _iconSize, _iconSize, 0, IntPtr.Zero, DI_NORMAL);
                }

                IntPtr oldFont = IntPtr.Zero;
                int oldMode = SetBkMode(hdc, TRANSPARENT);
                uint oldColor = SetTextColor(hdc, defaultColor);
                bool first = true;

                foreach (Piece p in _pieces)
                {
                    if (p.Line >= _lines.Count) continue;
                    Line line = _lines[p.Line];
                    FontInfo f = _fonts[p.Font];
                    IntPtr prev = SelectObject(hdc, f.Handle);
                    if (first)
                    {
                        oldFont = prev;
                        first = false;
                    }

                    SetTextColor(hdc, p.HasColor ? p.ColorRef : p.IsTitle ? titleColor : defaultColor);
                    int y = rc.top + line.Top + line.Pad + (line.Ascent - f.Ascent); // shared baseline
                    TextOutW(hdc, rc.left + p.X, y, p.Text, p.Text.Length);
                }

                if (!first) SelectObject(hdc, oldFont);
                SetTextColor(hdc, oldColor);
                SetBkMode(hdc, oldMode);
            }

            static IntPtr LoadSystemIcon(TooltipIcon icon, int size) => SystemIcons.Get(icon, size);

            int FontFor(IntPtr hdc, in RichTextRun run, bool title) =>
                _fontIndex.TryGetValue(Key(run, title), out int i) ? i : AddFont(hdc, run, title);

            string Key(in RichTextRun run, bool title) =>
                (title ? "T" : "B") + (run.Bold ? "b" : "") + (run.Italic ? "i" : "") + (run.Underline ? "u" : "") +
                (run.Strikethrough ? "s" : "") + "|" + (run.HasSize ? SizePx(run, title).ToString() : "-");

            int SizePx(in RichTextRun run, bool title)
            {
                LOGFONTW b = title ? _titleBase : _base;
                float basePoints = Math.Abs(b.lfHeight) * 96f / _dpi; // style sizes are 96-dpi pixels
                return Math.Max(1, Mathf.RoundToInt(run.ResolveSize(basePoints) * _dpi / 96f));
            }

            int AddFont(IntPtr hdc, in RichTextRun run, bool title)
            {
                LOGFONTW lf = title ? _titleBase : _base;
                lf.lfWidth = 0;
                if (run.Bold) lf.lfWeight = 700;
                if (run.Italic) lf.lfItalic = 1;
                if (run.Underline) lf.lfUnderline = 1;
                if (run.Strikethrough) lf.lfStrikeOut = 1;
                if (run.HasSize) lf.lfHeight = -SizePx(run, title);

                IntPtr handle = CreateFontIndirectW(ref lf);
                IntPtr old = SelectObject(hdc, handle);
                GetTextMetricsW(hdc, out TEXTMETRICW tm);
                SelectObject(hdc, old);

                _fonts.Add(new FontInfo { Handle = handle, Ascent = tm.tmAscent, Descent = tm.tmDescent });
                _fontIndex[Key(run, title)] = _fonts.Count - 1;
                return _fonts.Count - 1;
            }

            static int Measure(IntPtr hdc, string s)
            {
                if (string.IsNullOrEmpty(s)) return 0;
                return GetTextExtentPoint32W(hdc, s, s.Length, out SIZE size) ? size.cx : 0;
            }

            void DisposeFonts()
            {
                foreach (FontInfo f in _fonts)
                    if (f.Handle != IntPtr.Zero) DeleteObject(f.Handle);
                _fonts.Clear();
                _fontIndex.Clear();
                _laidOut = false;
            }

            public void Dispose() => DisposeFonts();
        }

        /// <summary>The theme's title font (TTP_STANDARDTITLE / TTP_BALLOONTITLE) for the given DPI.</summary>
        static bool TryGetThemeTitleFont(IntPtr hwnd, IntPtr hdc, bool balloon, uint dpi, out LOGFONTW font)
        {
            font = default;
            IntPtr theme = IntPtr.Zero;
            try
            {
                try { theme = OpenThemeDataForDpi(hwnd, "TOOLTIP", dpi); }
                catch (EntryPointNotFoundException) { theme = OpenThemeData(hwnd, "TOOLTIP"); } // Windows < 10 1703
                if (theme == IntPtr.Zero) return false;

                int part = balloon ? TTP_BALLOONTITLE : TTP_STANDARDTITLE;
                return GetThemeFont(theme, hdc, part, 1, TMT_FONT, out font) == 0 && font.lfHeight != 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (theme != IntPtr.Zero) CloseThemeData(theme);
            }
        }

        // ------------------------------------------------------------------ title icons

        /// <summary>
        /// Crisp, current-style system icons at an exact pixel size (cached; destroyed on shutdown).
        /// 1) Shell stock icons (imageres.dll — the modern art the tooltip control's own icons match) extracted at
        ///    the exact size; 2) LoadIconWithScaleDown (sharp downscale of user32's icon); 3) LoadImage (last resort).
        /// LoadImage + LR_SHARED alone returns the cached 32 px icon whatever size is asked for, which then gets
        /// blurred when drawn at 16 px, and user32's art is the older Vista-style design.
        /// </summary>
        static class SystemIcons
        {
            static readonly Dictionary<int, IntPtr> s_Cache = new Dictionary<int, IntPtr>();
            static readonly List<IntPtr> s_Owned = new List<IntPtr>();

            public static IntPtr Get(TooltipIcon icon, int size)
            {
                int key = ((int)icon << 16) | size;
                if (s_Cache.TryGetValue(key, out IntPtr cached)) return cached;

                IntPtr handle = FromStockIcon(icon, size);
                bool owned = handle != IntPtr.Zero;
                if (handle == IntPtr.Zero)
                {
                    handle = FromScaleDown(icon, size);
                    owned = handle != IntPtr.Zero;
                }

                if (handle == IntPtr.Zero)
                    handle = LoadImageW(IntPtr.Zero, (IntPtr)User32IconId(icon), IMAGE_ICON, size, size, LR_SHARED); // shared: never destroy

                if (owned) s_Owned.Add(handle);
                s_Cache[key] = handle;
                return handle;
            }

            public static void Clear()
            {
                foreach (IntPtr h in s_Owned) DestroyIcon(h);
                s_Owned.Clear();
                s_Cache.Clear();
            }

            static IntPtr FromStockIcon(TooltipIcon icon, int size)
            {
                try
                {
                    var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
                    if (SHGetStockIconInfo(StockIconId(icon), SHGSI_ICONLOCATION, ref info) != 0 || string.IsNullOrEmpty(info.szPath))
                        return IntPtr.Zero;

                    var icons = new IntPtr[1];
                    var ids = new uint[1];
                    uint count = PrivateExtractIconsW(info.szPath, info.iIcon, size, size, icons, ids, 1, 0);
                    return count >= 1 && count != 0xFFFFFFFF ? icons[0] : IntPtr.Zero;
                }
                catch (Exception)
                {
                    return IntPtr.Zero; // shell32 entry points missing (very old systems)
                }
            }

            static IntPtr FromScaleDown(TooltipIcon icon, int size)
            {
                try
                {
                    return LoadIconWithScaleDown(IntPtr.Zero, (IntPtr)User32IconId(icon), size, size, out IntPtr h) == 0 ? h : IntPtr.Zero;
                }
                catch (Exception)
                {
                    return IntPtr.Zero;
                }
            }

            static uint StockIconId(TooltipIcon icon)
            {
                switch (icon)
                {
                    case TooltipIcon.Warning: return 78; // SIID_WARNING
                    case TooltipIcon.Error: return 80;   // SIID_ERROR
                    default: return 79;                  // SIID_INFO
                }
            }

            static int User32IconId(TooltipIcon icon)
            {
                switch (icon)
                {
                    case TooltipIcon.Warning: return 32515; // IDI_WARNING
                    case TooltipIcon.Error: return 32513;   // IDI_ERROR
                    default: return 32516;                  // IDI_INFORMATION
                }
            }
        }

        // ------------------------------------------------------------------ per-show preparation

        void PrepareRich(TipWindow tip, in TooltipRequest request, TooltipStyle style, float scale)
        {
            tip.Layout?.Dispose();
            int maxWidth = Mathf.RoundToInt(style.ResolveMaxWidth(400f) * scale);
            List<RichTextRun> text = request.TextRuns ?? TooltipRichText.Plain(request.Text);
            List<RichTextRun> title = string.IsNullOrEmpty(request.Title)
                ? null
                : request.TitleRuns ?? TooltipRichText.Plain(request.Title);
            tip.Layout = new RichLayout(text, title, request.Icon, maxWidth, (uint)Mathf.RoundToInt(96f * scale),
                tip.Hwnd, !tip.Unthemed, tip.Balloon);
        }

        /// <summary>
        /// Fallback for Windows versions that skip the measuring notification: lay the runs out ourselves
        /// and grow the window (never for balloons — their shape is a window region with a stem).
        /// Returns the final window size.
        /// </summary>
        Vector2 EnsureRichSize(TipWindow tip, Vector2 current)
        {
            if (tip.Balloon || tip.Layout == null) return current;

            IntPtr hdc = GetDC(tip.Hwnd);
            if (hdc == IntPtr.Zero) return current;
            try
            {
                tip.Layout.Layout(hdc, SendMessageW(tip.Hwnd, WM_GETFONT, IntPtr.Zero, IntPtr.Zero));
            }
            finally
            {
                ReleaseDC(tip.Hwnd, hdc);
            }

            // Text rectangle → window rectangle (adds margins and borders).
            var r = new RECT { left = 0, top = 0, right = tip.Layout.Width, bottom = tip.Layout.Height };
            SendMessageW(tip.Hwnd, TTM_ADJUSTRECT, (IntPtr)1, ref r);
            int needW = r.right - r.left, needH = r.bottom - r.top;

            if (needW <= current.x && needH <= current.y) return current;

            var size = new Vector2(Math.Max(needW, current.x), Math.Max(needH, current.y));
            SetWindowPos(tip.Hwnd, IntPtr.Zero, 0, 0, (int)size.x, (int)size.y,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            return size;
        }

        // ------------------------------------------------------------------ notification window

        static readonly Dictionary<IntPtr, TipWindow> s_RichWindows = new Dictionary<IntPtr, TipWindow>();
        static WndProcDelegate s_WndProc; // keep alive: native code holds a pointer to it
        static IntPtr s_NotifyHwnd;
        static string s_NotifyClass;
        static bool s_NotifyFailed;

        static void RegisterRich(TipWindow tip) => s_RichWindows[tip.Hwnd] = tip;

        static void UnregisterRich(TipWindow tip)
        {
            s_RichWindows.Remove(tip.Hwnd);
            tip.Layout?.Dispose();
            tip.Layout = null;
        }

        static IntPtr EnsureNotifyWindow(IntPtr unityWindow)
        {
            if (s_NotifyHwnd != IntPtr.Zero || s_NotifyFailed) return s_NotifyHwnd;

            try
            {
                s_WndProc = NotifyWndProc;
                // Unique class per domain: after an Editor domain reload, a class registered earlier
                // would still point at a WndProc from the unloaded domain.
                s_NotifyClass = "NativeTooltipsNotify_" + Guid.NewGuid().ToString("N");
                IntPtr instance = GetModuleHandleW(null);
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_WndProc),
                    hInstance = instance,
                    lpszClassName = s_NotifyClass
                };

                if (RegisterClassExW(ref wc) == 0)
                {
                    s_NotifyFailed = true;
                    return IntPtr.Zero;
                }

                // A hidden (never shown) top-level popup owned by Unity's window. Not a message-only window:
                // those can't own popups, and comctl32 sends NM_CUSTOMDRAW to the tooltip's owner.
                s_NotifyHwnd = CreateWindowExW(WS_EX_TOOLWINDOW, s_NotifyClass, "", WS_POPUP, 0, 0, 0, 0, unityWindow,
                    IntPtr.Zero, instance, IntPtr.Zero);
                if (s_NotifyHwnd == IntPtr.Zero) s_NotifyFailed = true;
            }
            catch (Exception e)
            {
                BackendFactory.Log("Rich text notification window unavailable: " + e.Message);
                s_NotifyFailed = true;
            }

            return s_NotifyHwnd;
        }

        static void DestroyNotifyWindow()
        {
            if (s_NotifyHwnd != IntPtr.Zero)
            {
                DestroyWindow(s_NotifyHwnd);
                s_NotifyHwnd = IntPtr.Zero;
            }

            if (s_NotifyClass != null)
            {
                UnregisterClassW(s_NotifyClass, GetModuleHandleW(null));
                s_NotifyClass = null;
            }

            s_RichWindows.Clear();
            s_NotifyFailed = false;
            SystemIcons.Clear();
        }

        [MonoPInvokeCallback(typeof(WndProcDelegate))]
        static IntPtr NotifyWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_NOTIFY && lParam != IntPtr.Zero)
                {
                    var hdr = Marshal.PtrToStructure<NMHDR>(lParam);
                    if (hdr.code == NM_CUSTOMDRAW && s_RichWindows.TryGetValue(hdr.hwndFrom, out TipWindow tip))
                        return (IntPtr)OnCustomDraw(tip, lParam);
                }
            }
            catch (Exception)
            {
                // never let an exception cross into native code
            }

            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        static int OnCustomDraw(TipWindow tip, IntPtr lParam)
        {
            var cd = Marshal.PtrToStructure<NMTTCUSTOMDRAW>(lParam);
            RichLayout layout = tip.Layout;
            if (layout == null) return CDRF_DODEFAULT;

            switch (cd.nmcd.dwDrawStage)
            {
                case CDDS_PREPAINT:
                    if ((cd.uDrawFlags & DT_CALCRECT) != 0)
                    {
                        // Measuring pass: report the formatted text size.
                        layout.Layout(cd.nmcd.hdc, SendMessageW(tip.Hwnd, WM_GETFONT, IntPtr.Zero, IntPtr.Zero));
                        cd.nmcd.rc.right = cd.nmcd.rc.left + layout.Width;
                        cd.nmcd.rc.bottom = cd.nmcd.rc.top + layout.Height;
                        Marshal.StructureToPtr(cd, lParam, false);
                        return CDRF_SKIPDEFAULT;
                    }

                    // Painting pass: let the control draw background/border/title, but not its plain text
                    // (DT_CALCRECT makes its DrawText measure only). Our text is drawn in POSTPAINT.
                    cd.uDrawFlags |= DT_CALCRECT;
                    Marshal.StructureToPtr(cd, lParam, false);
                    return CDRF_NOTIFYPOSTPAINT;

                case CDDS_POSTPAINT:
                    layout.Layout(cd.nmcd.hdc, SendMessageW(tip.Hwnd, WM_GETFONT, IntPtr.Zero, IntPtr.Zero));
                    uint textColor = EraseTextArea(tip, cd.nmcd.hdc, cd.nmcd.rc, out uint titleColor);
                    layout.Draw(cd.nmcd.hdc, cd.nmcd.rc, textColor, titleColor);
                    return CDRF_DODEFAULT;
            }

            return CDRF_DODEFAULT;
        }

        /// <summary>
        /// Repaints the background under the text rectangle (in case the control drew its plain text anyway)
        /// with exactly what the control uses — the TOOLTIP theme part, or the tooltip colour when unthemed —
        /// and returns the default text colour.
        /// </summary>
        static uint EraseTextArea(TipWindow tip, IntPtr hdc, RECT rc, out uint titleColor)
        {
            uint textColor;
            if (tip.Unthemed)
            {
                uint bk = (uint)SendMessageW(tip.Hwnd, TTM_GETTIPBKCOLOR, IntPtr.Zero, IntPtr.Zero).ToInt64();
                IntPtr brush = CreateSolidBrush(bk);
                FillRect(hdc, ref rc, brush);
                DeleteObject(brush);
                textColor = (uint)SendMessageW(tip.Hwnd, TTM_GETTIPTEXTCOLOR, IntPtr.Zero, IntPtr.Zero).ToInt64();
                titleColor = textColor;
                return textColor;
            }

            textColor = GetSysColor(COLOR_INFOTEXT);
            titleColor = textColor;
            IntPtr theme = IntPtr.Zero;
            try
            {
                theme = OpenThemeData(tip.Hwnd, "TOOLTIP");
                if (theme == IntPtr.Zero) return textColor;

                int part = tip.Balloon ? TTP_BALLOON : TTP_STANDARD;
                GetClientRect(tip.Hwnd, out RECT client);
                int saved = SaveDC(hdc);
                IntersectClipRect(hdc, rc.left, rc.top, rc.right, rc.bottom);
                DrawThemeBackground(theme, hdc, part, 1 /* normal */, ref client, IntPtr.Zero);
                RestoreDC(hdc, saved);

                if (GetThemeColor(theme, part, 1, TMT_TEXTCOLOR, out uint themed) == 0) textColor = themed;
                titleColor = textColor;
                // The theme's title part (TTP_STANDARDTITLE / TTP_BALLOONTITLE) may use its own colour.
                if (GetThemeColor(theme, tip.Balloon ? TTP_BALLOONTITLE : TTP_STANDARDTITLE, 1, TMT_TEXTCOLOR, out uint themedTitle) == 0)
                    titleColor = themedTitle;
            }
            catch (DllNotFoundException)
            {
                // no uxtheme
            }
            finally
            {
                if (theme != IntPtr.Zero) CloseThemeData(theme);
            }

            return textColor;
        }

        // ------------------------------------------------------------------ interop

        delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        const uint WM_NOTIFY = 0x004E;
        const uint WM_GETFONT = 0x0031;
        const int NM_CUSTOMDRAW = -12;
        const uint CDDS_PREPAINT = 0x00000001;
        const uint CDDS_POSTPAINT = 0x00000002;
        const int CDRF_DODEFAULT = 0x00000000;
        const int CDRF_SKIPDEFAULT = 0x00000004;
        const int CDRF_NOTIFYPOSTPAINT = 0x00000010;
        const uint DT_CALCRECT = 0x00000400;
        const uint TTM_GETTIPBKCOLOR = WM_USER + 22;
        const uint TTM_GETTIPTEXTCOLOR = WM_USER + 23;
        const uint TTM_ADJUSTRECT = WM_USER + 31;
        const int TTP_STANDARD = 1;
        const int TTP_STANDARDTITLE = 2;
        const int TTP_BALLOON = 3;
        const int TTP_BALLOONTITLE = 4;
        const uint IMAGE_ICON = 1;
        const uint LR_SHARED = 0x00008000;
        const uint DI_NORMAL = 0x0003;
        const uint SHGSI_ICONLOCATION = 0x00000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetStockIconInfo(uint siid, uint flags, ref SHSTOCKICONINFO info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint PrivateExtractIconsW(string file, int iconIndex, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

        [DllImport("comctl32.dll")]
        static extern int LoadIconWithScaleDown(IntPtr instance, IntPtr name, int cx, int cy, out IntPtr icon);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);
        const int TMT_TEXTCOLOR = 3803;
        const int TMT_FONT = 210;
        const int COLOR_INFOTEXT = 23;
        const int TRANSPARENT = 1;
        const uint SWP_NOZORDER = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        struct NMHDR
        {
            public IntPtr hwndFrom;
            public UIntPtr idFrom;
            public int code;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NMCUSTOMDRAW
        {
            public NMHDR hdr;
            public uint dwDrawStage;
            public IntPtr hdc;
            public RECT rc;
            public UIntPtr dwItemSpec;
            public uint uItemState;
            public IntPtr lItemlParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NMTTCUSTOMDRAW
        {
            public NMCUSTOMDRAW nmcd;
            public uint uDrawFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE
        {
            public int cx, cy;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TEXTMETRICW
        {
            public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading;
            public int tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
            public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
            public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool UnregisterClassW(string className, IntPtr instance);

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll")]
        static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

        [DllImport("user32.dll")]
        static extern uint GetSysColor(int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadImageW(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, uint step, IntPtr brush, uint flags);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern int GetObjectW(IntPtr obj, int size, out LOGFONTW font);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out SIZE size);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW metrics);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int length);

        [DllImport("gdi32.dll")]
        static extern uint SetTextColor(IntPtr hdc, uint color);

        [DllImport("gdi32.dll")]
        static extern int SetBkMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("gdi32.dll")]
        static extern int SaveDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool RestoreDC(IntPtr hdc, int saved);

        [DllImport("gdi32.dll")]
        static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr OpenThemeData(IntPtr hwnd, string classList);

        [DllImport("uxtheme.dll")]
        static extern int CloseThemeData(IntPtr theme);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr OpenThemeDataForDpi(IntPtr hwnd, string classList, uint dpi);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int GetThemeFont(IntPtr theme, IntPtr hdc, int part, int state, int property, out LOGFONTW font);

        [DllImport("uxtheme.dll")]
        static extern int DrawThemeBackground(IntPtr theme, IntPtr hdc, int part, int state, ref RECT rect, IntPtr clip);

        [DllImport("uxtheme.dll")]
        static extern int GetThemeColor(IntPtr theme, int part, int state, int property, out uint color);
    }
}
#endif
