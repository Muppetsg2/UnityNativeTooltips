#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
using System;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;
using AOT;
using NativeTooltips.Internal;
using UnityEngine;

namespace NativeTooltips.Backends
{
    /// <summary>
    /// Linux: GTK 3 (libgtk-3.so.0 — present on practically every desktop: GNOME, Xfce, MATE, Cinnamon, KDE with gtk3).
    ///
    /// The Unity player (SDL) is not a GTK application, so a GtkTooltip can't be attached to its window.
    /// Instead we create a popup window with the TOOLTIP type hint and — the key part — register a
    /// GtkWindow subclass whose CSS node name is "tooltip". The GTK theme (Adwaita, Yaru, Breeze-GTK,
    /// Arc…) then styles it exactly like system tooltips: background, border, radius, font, padding.
    ///
    /// The window goes through X11 (XWayland included), because pure Wayland can't position popup windows.
    /// </summary>
    internal sealed class LinuxGtkTooltipBackend : ITooltipBackend
    {
        const string Gtk = "libgtk-3.so.0";
        const string Gdk = "libgdk-3.so.0";
        const string GObject = "libgobject-2.0.so.0";
        const string Pango = "libpango-1.0.so.0";
        const string Cairo = "libcairo.so.2";

        const string TypeName = "NativeTooltipsTooltipWindow";
        const int GTK_WINDOW_POPUP = 1;
        const int GDK_WINDOW_TYPE_HINT_TOOLTIP = 10;
        const uint GTK_STYLE_PROVIDER_PRIORITY_APPLICATION = 600;
        const int DefaultMaxWidthChars = 70; // GtkTooltipWindow default

        IntPtr _window;
        IntPtr _box;
        IntPtr _label;
        IntPtr _css;
        string _appliedCss;
        float _systemFontPx = 13f;
        bool _visible;
        bool _pumpEvents;

        public string Name => "GTK 3 (CSS node \"tooltip\")";
        public bool IsNative => true;
        public TooltipFeature Features => TooltipFeature.AllCustom;
        public bool UsesSystemHoverDelay(in TooltipRequest request) => false;
        public float InitialDelay => 0.5f;   // GTK 3: constant HOVER_TIMEOUT = 500 ms
        public float ReshowDelay => 0.06f;   // GTK 3: BROWSE_TIMEOUT = 60 ms
        public float AutoPopDelay => 0f;     // GTK 3 never auto-hides tooltips
        public float LongPressTimeout => 0.5f;

        public bool Initialize()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                return false; // no X11/XWayland

#if !UNITY_EDITOR
            // Must happen before gtk_init. Wayland can't position popup windows globally.
            gdk_set_allowed_backends("x11");
            if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0) return false;
            _pumpEvents = true;
#else
            // The Unity Editor on Linux is a GTK app itself and pumps events on its own.
            _pumpEvents = false;
#endif

            uint major = gtk_get_major_version(), minor = gtk_get_minor_version();
            if (major != 3 || minor < 22)
            {
                BackendFactory.Log($"GTK {major}.{minor} is too old (need 3.22+).");
                return false;
            }

            UIntPtr type = EnsureTooltipWindowType();
            _window = g_object_new(type, "type", GTK_WINDOW_POPUP, IntPtr.Zero);
            if (_window == IntPtr.Zero) return false;

            gtk_window_set_type_hint(_window, GDK_WINDOW_TYPE_HINT_TOOLTIP);
            gtk_window_set_resizable(_window, 0);
            gtk_window_set_accept_focus(_window, 0);
            gtk_window_set_skip_taskbar_hint(_window, 1);
            gtk_window_set_skip_pager_hint(_window, 1);

            // Transparency (theme's rounded corners) when a compositor is running.
            IntPtr screen = gtk_widget_get_screen(_window);
            if (screen != IntPtr.Zero && gdk_screen_is_composited(screen) != 0)
            {
                IntPtr visual = gdk_screen_get_rgba_visual(screen);
                if (visual != IntPtr.Zero) gtk_widget_set_visual(_window, visual);
            }

            // Theme selector: "tooltip.background" (+ ".csd" for rounded variants).
            IntPtr ctx = gtk_widget_get_style_context(_window);
            gtk_style_context_add_class(ctx, "background");

            // Same structure as GtkTooltipWindow: window > box > label
            // (themes style "tooltip *", so the box and label get the theme's padding).
            _box = gtk_box_new(0 /* GTK_ORIENTATION_HORIZONTAL */, 6);
            _label = gtk_label_new(IntPtr.Zero);
            gtk_label_set_line_wrap(_label, 1);
            gtk_label_set_max_width_chars(_label, DefaultMaxWidthChars);
            gtk_label_set_xalign(_label, 0f);
            gtk_container_add(_box, _label);
            gtk_container_add(_window, _box);
            gtk_widget_show(_label);
            gtk_widget_show(_box);

            _systemFontPx = ReadThemeFontPx(_label, _systemFontPx);

            // One CSS provider shared by the three widgets; its content is replaced per style.
            _css = gtk_css_provider_new();
            gtk_style_context_add_provider(gtk_widget_get_style_context(_window), _css, GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
            gtk_style_context_add_provider(gtk_widget_get_style_context(_box), _css, GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
            gtk_style_context_add_provider(gtk_widget_get_style_context(_label), _css, GTK_STYLE_PROVIDER_PRIORITY_APPLICATION);
            _appliedCss = "";

            Pump();
            return true;
        }

        public void Show(in TooltipRequest request)
        {
            if (_window == IntPtr.Zero) return;
            TooltipStyle style = request.Style ?? TooltipSettings.Style;

            ApplyCss(style);
            ApplyPadding(style);
            SetContent(request, style);
            ApplyMaxWidth(style);
            gtk_window_resize(_window, 1, 1); // shrink after longer text

            if (request.IsUpdate && _visible)
            {
                Pump();
                return;
            }

            gtk_widget_get_preferred_size(_window, IntPtr.Zero, out GtkRequisition size);

            IntPtr display = gdk_display_get_default();
            IntPtr seat = gdk_display_get_default_seat(display);
            IntPtr pointer = seat != IntPtr.Zero ? gdk_seat_get_pointer(seat) : IntPtr.Zero;
            int px = 0, py = 0;
            if (pointer != IntPtr.Zero) gdk_device_get_position(pointer, out _, out px, out py);

            IntPtr monitor = gdk_display_get_monitor_at_point(display, px, py);
            var work = new GdkRectangle { x = 0, y = 0, width = 100000, height = 100000 };
            int scale = 1;
            if (monitor != IntPtr.Zero)
            {
                gdk_monitor_get_workarea(monitor, out work);
                scale = Math.Max(1, gdk_monitor_get_scale_factor(monitor));
            }

            // Unity window origin = global pointer − pointer in window (SDL emulates the mouse for touch).
            float viewW = Screen.width / (float)scale, viewH = Screen.height / (float)scale;
            float originX = px - request.Pointer.x * viewW;
            float originY = py - request.Pointer.y * viewH;
            var anchor = new Rect(
                originX + request.Anchor.x * viewW,
                originY + request.Anchor.y * viewH,
                request.Anchor.width * viewW,
                request.Anchor.height * viewH);

            int cursor = (int)Math.Min(gdk_display_get_default_cursor_size(display), 32u);
            if (cursor <= 0) cursor = 24;

            TooltipPlacement placement = TooltipLayout.Resolve(style.Placement, request.Trigger, request.HasAnchor);
            Vector2 pos = TooltipLayout.Place(new LayoutInput
            {
                Placement = placement,
                Pointer = new Vector2(px, py),
                CursorHeight = cursor,              // like gtk_tooltip_position()
                HasAnchor = request.HasAnchor,
                Anchor = anchor,
                Size = new Vector2(size.width, size.height),
                Offset = TooltipLayout.OffsetToPixels(style.Offset, 1f), // GTK coordinates are logical pixels
                Gap = TooltipLayout.IsElementPlacement(placement) ? 6f : 4f,
                WorkArea = new Rect(work.x, work.y, work.width, work.height),
                CenterOnPointer = true
            });

            gtk_window_move(_window, Mathf.RoundToInt(pos.x), Mathf.RoundToInt(pos.y));
            if (!_visible)
            {
                gtk_widget_show(_window);
                _visible = true;
            }

            MakeClickThrough();
            Pump();
        }

        public void Hide()
        {
            if (_window == IntPtr.Zero || !_visible) return;
            gtk_widget_hide(_window);
            _visible = false;
            Pump();
        }

        public void Tick()
        {
            Pump();
        }

        public void Shutdown()
        {
            if (_window != IntPtr.Zero)
            {
                gtk_widget_destroy(_window);
                _window = IntPtr.Zero;
                _box = IntPtr.Zero;
                _label = IntPtr.Zero;
                Pump();
            }

            if (_css != IntPtr.Zero)
            {
                g_object_unref(_css);
                _css = IntPtr.Zero;
            }

            _visible = false;
        }

        // ------------------------------------------------------------------ styling

        void SetContent(in TooltipRequest request, TooltipStyle style)
        {
            bool hasTitle = !string.IsNullOrEmpty(request.Title);
            if (!hasTitle && request.TextRuns == null)
            {
                gtk_label_set_text(_label, Utf8(request.Text));
                return;
            }

            float basePx = style.ResolveFontSize(_systemFontPx);
            string body = request.TextRuns != null ? ToPangoMarkup(request.TextRuns, basePx) : EscapeMarkup(request.Text);
            string markup = body;
            if (hasTitle)
            {
                string title = request.TitleRuns != null ? ToPangoMarkup(request.TitleRuns, basePx) : EscapeMarkup(request.Title);
                markup = "<b>" + title + "</b>\n" + body;
            }

            gtk_label_set_markup(_label, Utf8(markup));
        }

        /// <summary>Runs → Pango markup (<c>&lt;span&gt;</c> attributes; sizes in 1024ths of a point at 96 dpi).</summary>
        internal static string ToPangoMarkup(System.Collections.Generic.List<RichTextRun> runs, float basePx)
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            foreach (RichTextRun run in runs)
            {
                var attrs = new StringBuilder();
                if (run.Bold) attrs.Append(" weight=\"bold\"");
                if (run.Italic) attrs.Append(" style=\"italic\"");
                if (run.Underline) attrs.Append(" underline=\"single\"");
                if (run.Strikethrough) attrs.Append(" strikethrough=\"true\"");
                if (run.HasColor)
                {
                    Color32 c = run.Color;
                    attrs.AppendFormat(ci, " foreground=\"#{0:X2}{1:X2}{2:X2}\"", c.r, c.g, c.b);
                    if (c.a < 255) attrs.AppendFormat(ci, " alpha=\"{0}%\"", Mathf.Max(1, Mathf.RoundToInt(c.a / 2.55f)));
                }

                if (run.HasSize)
                {
                    float points = run.ResolveSize(basePx) * 72f / 96f;
                    attrs.AppendFormat(ci, " size=\"{0}\"", Mathf.RoundToInt(points * 1024f));
                }

                string text = EscapeMarkup(run.Text);
                if (attrs.Length == 0) sb.Append(text);
                else sb.Append("<span").Append(attrs).Append('>').Append(text).Append("</span>");
            }

            return sb.ToString();
        }

        void ApplyPadding(TooltipStyle s)
        {
            // GtkWindow ignores CSS padding, so custom padding = label margins (CSS zeroes the theme's padding).
            bool custom = s.Padding.x >= 0f || s.Padding.y >= 0f;
            int mx = custom ? Mathf.RoundToInt(s.ResolvePaddingX(6f)) : 0;
            int my = custom ? Mathf.RoundToInt(s.ResolvePaddingY(6f)) : 0;
            gtk_widget_set_margin_start(_label, mx);
            gtk_widget_set_margin_end(_label, mx);
            gtk_widget_set_margin_top(_label, my);
            gtk_widget_set_margin_bottom(_label, my);
        }

        void ApplyMaxWidth(TooltipStyle style)
        {
            if (style.MaxWidth <= 0f)
            {
                gtk_label_set_max_width_chars(_label, DefaultMaxWidthChars);
                return;
            }

            // A wrapping GtkLabel's natural width = max-width-chars × the font's approximate character
            // width (same metric GTK uses). Convert MaxWidth (minus the window's horizontal padding).
            gtk_widget_get_preferred_size(_window, IntPtr.Zero, out GtkRequisition current); // validates the new style
            gtk_widget_get_preferred_size(_label, IntPtr.Zero, out GtkRequisition labelSize);
            int chrome = Math.Max(0, current.width - labelSize.width);
            float labelMax = Mathf.Max(20f, style.MaxWidth - chrome);

            float charWidth = ApproximateCharWidth(_label);
            int chars = charWidth > 0f ? Mathf.Max(4, Mathf.FloorToInt(labelMax / charWidth)) : DefaultMaxWidthChars;
            gtk_label_set_max_width_chars(_label, chars);
        }

        static float ApproximateCharWidth(IntPtr widget)
        {
            try
            {
                IntPtr ctx = gtk_widget_get_pango_context(widget);
                IntPtr desc = ctx != IntPtr.Zero ? pango_context_get_font_description(ctx) : IntPtr.Zero;
                if (desc == IntPtr.Zero) return 0f;
                IntPtr metrics = pango_context_get_metrics(ctx, desc, IntPtr.Zero);
                if (metrics == IntPtr.Zero) return 0f;
                int cw = pango_font_metrics_get_approximate_char_width(metrics);
                int dw = pango_font_metrics_get_approximate_digit_width(metrics);
                pango_font_metrics_unref(metrics);
                return Math.Max(cw, dw) / 1024f; // PANGO_SCALE
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        void ApplyCss(TooltipStyle s)
        {
            string css = BuildCss(s, _systemFontPx);
            if (css == _appliedCss) return;
            _appliedCss = css;
            gtk_css_provider_load_from_data(_css, Utf8(css), (IntPtr)(-1), IntPtr.Zero);
        }

        internal static string BuildCss(TooltipStyle s, float systemFontPx)
        {
            var ci = CultureInfo.InvariantCulture;
            var window = new StringBuilder();
            var label = new StringBuilder();
            var children = new StringBuilder();

            if (s.Padding.x >= 0f || s.Padding.y >= 0f)
            {
                // Themes pad both "tooltip" and "tooltip *"; zero them — the padding becomes label margins.
                window.Append("padding: 0; ");
                children.Append("padding: 0; ");
            }

            if (s.CornerRadius >= 0f)
                window.AppendFormat(ci, "border-radius: {0:0.##}px; ", s.CornerRadius);

            if (s.OverrideColors)
            {
                window.AppendFormat(ci, "background-color: {0}; background-image: none; ", Rgba(s.BackgroundColor));
                children.Append("background-color: transparent; ");
                label.AppendFormat(ci, "color: {0}; text-shadow: none; ", Rgba(s.TextColor));
            }

            if (!Mathf.Approximately(s.FontScale, 1f) || s.FontSize > 0f)
                label.AppendFormat(ci, "font-size: {0:0.##}px; ", s.ResolveFontSize(systemFontPx));

            if (s.Bold)
                label.Append("font-weight: bold; ");

            var css = new StringBuilder();
            if (window.Length > 0) css.Append("tooltip { ").Append(window).Append("}\n");
            if (children.Length > 0) css.Append("tooltip * { ").Append(children).Append("}\n");
            if (label.Length > 0) css.Append("tooltip label { ").Append(label).Append("}\n");
            return css.ToString();
        }

        static string Rgba(Color c)
        {
            Color32 c32 = c;
            return string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3:0.###})", c32.r, c32.g, c32.b, c.a);
        }

        internal static string EscapeMarkup(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        static float ReadThemeFontPx(IntPtr widget, float fallback)
        {
            try
            {
                IntPtr ctx = gtk_widget_get_pango_context(widget);
                IntPtr desc = ctx != IntPtr.Zero ? pango_context_get_font_description(ctx) : IntPtr.Zero;
                if (desc == IntPtr.Zero) return fallback;

                int size = pango_font_description_get_size(desc); // PANGO_SCALE units
                if (size <= 0) return fallback;
                float value = size / 1024f;
                // Absolute = device pixels; otherwise points at Pango's default 96 dpi.
                return pango_font_description_get_size_is_absolute(desc) != 0 ? value : value * 96f / 72f;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        bool _inputShapeFailed;

        /// <summary>
        /// Empty X input shape on the realized GDK window, so the pointer passes to the Unity window
        /// underneath (otherwise a bubble covering another button would block hovering it). Applied after
        /// showing: GtkWindow resets the input shape when it realizes / reallocates the window.
        /// </summary>
        void MakeClickThrough()
        {
            if (_inputShapeFailed) return;
            try
            {
                IntPtr gdkWindow = gtk_widget_get_window(_window);
                if (gdkWindow == IntPtr.Zero) return;
                IntPtr empty = cairo_region_create();
                gdk_window_input_shape_combine_region(gdkWindow, empty, 0, 0);
                cairo_region_destroy(empty);
            }
            catch (Exception e)
            {
                _inputShapeFailed = true;
                BackendFactory.Log("Input shape unavailable: " + e.Message);
            }
        }

        void Pump()
        {
            if (!_pumpEvents) return;
            for (int guard = 0; guard < 100 && gtk_events_pending() != 0; guard++)
                gtk_main_iteration_do(0);
        }

        static byte[] Utf8(string s) => Encoding.UTF8.GetBytes((s ?? string.Empty) + "\0");

        // --------------------------------------------- GtkWindow subclass with CSS node "tooltip"

        static GClassInitFunc s_ClassInit; // keep a reference so the GC doesn't collect the delegate

        static UIntPtr EnsureTooltipWindowType()
        {
            UIntPtr existing = g_type_from_name(TypeName);
            if (existing != UIntPtr.Zero) return existing;

            UIntPtr parent = gtk_window_get_type();
            g_type_query(parent, out GTypeQuery query);
            s_ClassInit = ClassInit;
            return g_type_register_static_simple(parent, TypeName, query.class_size, s_ClassInit,
                query.instance_size, IntPtr.Zero, 0);
        }

        [MonoPInvokeCallback(typeof(GClassInitFunc))]
        static void ClassInit(IntPtr klass, IntPtr classData)
        {
            gtk_widget_class_set_css_name(klass, "tooltip");
        }

        // ------------------------------------------------------------------ interop

        delegate void GClassInitFunc(IntPtr klass, IntPtr classData);

        [StructLayout(LayoutKind.Sequential)]
        struct GTypeQuery
        {
            public UIntPtr type;
            public IntPtr type_name;
            public uint class_size;
            public uint instance_size;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GtkRequisition
        {
            public int width, height;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GdkRectangle
        {
            public int x, y, width, height;
        }

        [DllImport(Gdk)] static extern void gdk_set_allowed_backends(string backends);
        [DllImport(Gtk)] static extern int gtk_init_check(IntPtr argc, IntPtr argv);
        [DllImport(Gtk)] static extern uint gtk_get_major_version();
        [DllImport(Gtk)] static extern uint gtk_get_minor_version();
        [DllImport(Gtk)] static extern int gtk_events_pending();
        [DllImport(Gtk)] static extern int gtk_main_iteration_do(int blocking);

        [DllImport(GObject)] static extern UIntPtr g_type_from_name(string name);
        [DllImport(GObject)] static extern void g_type_query(UIntPtr type, out GTypeQuery query);
        [DllImport(GObject)] static extern UIntPtr g_type_register_static_simple(UIntPtr parentType, string typeName,
            uint classSize, GClassInitFunc classInit, uint instanceSize, IntPtr instanceInit, int flags);

        // g_object_new is variadic; one (name, int) pair + NULL has the same ABI on x86_64/arm64 Linux.
        [DllImport(GObject)] static extern IntPtr g_object_new(UIntPtr type, string firstProperty, int value, IntPtr terminator);

        [DllImport(Gtk)] static extern UIntPtr gtk_window_get_type();
        [DllImport(Gtk)] static extern void gtk_widget_class_set_css_name(IntPtr widgetClass, string name);
        [DllImport(Gtk)] static extern void gtk_window_set_type_hint(IntPtr window, int hint);
        [DllImport(Gtk)] static extern void gtk_window_set_resizable(IntPtr window, int resizable);
        [DllImport(Gtk)] static extern void gtk_window_set_accept_focus(IntPtr window, int setting);
        [DllImport(Gtk)] static extern void gtk_window_set_skip_taskbar_hint(IntPtr window, int setting);
        [DllImport(Gtk)] static extern void gtk_window_set_skip_pager_hint(IntPtr window, int setting);
        [DllImport(Gtk)] static extern void gtk_window_move(IntPtr window, int x, int y);
        [DllImport(Gtk)] static extern void gtk_window_resize(IntPtr window, int width, int height);
        [DllImport(Gtk)] static extern IntPtr gtk_widget_get_screen(IntPtr widget);
        [DllImport(Gtk)] static extern void gtk_widget_set_visual(IntPtr widget, IntPtr visual);
        [DllImport(Gtk)] static extern IntPtr gtk_widget_get_style_context(IntPtr widget);
        [DllImport(Gtk)] static extern void gtk_style_context_add_class(IntPtr context, string className);
        [DllImport(Gtk)] static extern void gtk_widget_show(IntPtr widget);
        [DllImport(Gtk)] static extern void gtk_widget_hide(IntPtr widget);
        [DllImport(Gtk)] static extern void gtk_widget_destroy(IntPtr widget);
        [DllImport(Gtk)] static extern void gtk_widget_get_preferred_size(IntPtr widget, IntPtr minimum, out GtkRequisition natural);
        [DllImport(Gtk)] static extern void gtk_container_add(IntPtr container, IntPtr child);
        [DllImport(Gtk)] static extern IntPtr gtk_box_new(int orientation, int spacing);
        [DllImport(Gtk)] static extern IntPtr gtk_label_new(IntPtr text);
        [DllImport(Gtk)] static extern void gtk_label_set_text(IntPtr label, byte[] utf8);
        [DllImport(Gtk)] static extern void gtk_label_set_line_wrap(IntPtr label, int wrap);
        [DllImport(Gtk)] static extern void gtk_label_set_max_width_chars(IntPtr label, int chars);
        [DllImport(Gtk)] static extern void gtk_label_set_xalign(IntPtr label, float xalign);
        [DllImport(Gtk)] static extern void gtk_label_set_markup(IntPtr label, byte[] utf8);
        [DllImport(Gtk)] static extern void gtk_widget_set_size_request(IntPtr widget, int width, int height);
        [DllImport(Gtk)] static extern IntPtr gtk_widget_get_pango_context(IntPtr widget);
        [DllImport(Gtk)] static extern IntPtr gtk_css_provider_new();
        [DllImport(Gtk)] static extern int gtk_css_provider_load_from_data(IntPtr provider, byte[] data, IntPtr length, IntPtr error);
        [DllImport(Gtk)] static extern void gtk_style_context_add_provider(IntPtr context, IntPtr provider, uint priority);
        [DllImport(GObject)] static extern void g_object_unref(IntPtr obj);
        [DllImport(Pango)] static extern IntPtr pango_context_get_font_description(IntPtr context);
        [DllImport(Pango)] static extern int pango_font_description_get_size(IntPtr desc);
        [DllImport(Pango)] static extern int pango_font_description_get_size_is_absolute(IntPtr desc);
        [DllImport(Pango)] static extern IntPtr pango_context_get_metrics(IntPtr context, IntPtr desc, IntPtr language);
        [DllImport(Pango)] static extern int pango_font_metrics_get_approximate_char_width(IntPtr metrics);
        [DllImport(Pango)] static extern int pango_font_metrics_get_approximate_digit_width(IntPtr metrics);
        [DllImport(Pango)] static extern void pango_font_metrics_unref(IntPtr metrics);
        [DllImport(Gtk)] static extern IntPtr gtk_widget_get_window(IntPtr widget);
        [DllImport(Gdk)] static extern void gdk_window_input_shape_combine_region(IntPtr window, IntPtr region, int offsetX, int offsetY);
        [DllImport(Cairo)] static extern IntPtr cairo_region_create();
        [DllImport(Cairo)] static extern void cairo_region_destroy(IntPtr region);
        [DllImport(Gtk)] static extern void gtk_widget_set_margin_start(IntPtr widget, int margin);
        [DllImport(Gtk)] static extern void gtk_widget_set_margin_end(IntPtr widget, int margin);
        [DllImport(Gtk)] static extern void gtk_widget_set_margin_top(IntPtr widget, int margin);
        [DllImport(Gtk)] static extern void gtk_widget_set_margin_bottom(IntPtr widget, int margin);

        [DllImport(Gdk)] static extern IntPtr gdk_display_get_default();
        [DllImport(Gdk)] static extern IntPtr gdk_display_get_default_seat(IntPtr display);
        [DllImport(Gdk)] static extern IntPtr gdk_seat_get_pointer(IntPtr seat);
        [DllImport(Gdk)] static extern void gdk_device_get_position(IntPtr device, out IntPtr screen, out int x, out int y);
        [DllImport(Gdk)] static extern uint gdk_display_get_default_cursor_size(IntPtr display);
        [DllImport(Gdk)] static extern IntPtr gdk_display_get_monitor_at_point(IntPtr display, int x, int y);
        [DllImport(Gdk)] static extern void gdk_monitor_get_workarea(IntPtr monitor, out GdkRectangle workarea);
        [DllImport(Gdk)] static extern int gdk_monitor_get_scale_factor(IntPtr monitor);
        [DllImport(Gdk)] static extern int gdk_screen_is_composited(IntPtr screen);
        [DllImport(Gdk)] static extern IntPtr gdk_screen_get_rgba_visual(IntPtr screen);
    }
}
#endif
