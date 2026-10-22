using NativeTooltips.Internal;
using UnityEngine;

namespace NativeTooltips.Backends
{
    /// <summary>
    /// Fallback bubble drawn with IMGUI — used in the Editor and wherever the native API is
    /// unavailable. Neutral look (dark rounded rectangle); supports every <see cref="TooltipStyle"/> option
    /// except the Windows-only ones.
    /// </summary>
    internal sealed class ImguiTooltipBackend : ITooltipBackend
    {
        [AddComponentMenu("")]
        sealed class ImguiHost : MonoBehaviour
        {
            public ImguiTooltipBackend Owner;

            void OnGUI()
            {
                Owner?.Draw();
            }
        }

        const float DefaultFontSize = 12f;
        const float DefaultPaddingX = 8f;
        const float DefaultPaddingY = 5f;
        const float DefaultRadius = 4f;
        const float DefaultMaxWidth = 400f;
        static readonly Color DefaultBackground = new Color(0.16f, 0.16f, 0.17f, 0.96f);
        static readonly Color DefaultText = new Color(0.95f, 0.95f, 0.95f, 1f);

        GameObject _host;
        GUIStyle _bubble, _text, _title;
        Texture2D _background;
        Color _bgColor;
        int _bgRadius = -1;
        bool _visible;
        TooltipRequest _request;
        Vector2 _guiPointer;

        public string Name => "IMGUI (fallback)";
        public bool IsNative => false;
        public TooltipFeature Features => TooltipFeature.AllCustom;
        public bool UsesSystemHoverDelay(in TooltipRequest request) => false;
        public float InitialDelay => 0.5f;
        public float ReshowDelay => 0.1f;
        public float AutoPopDelay => 5f;
        public float LongPressTimeout => 0.5f;

        public bool Initialize()
        {
            _host = new GameObject("[NativeTooltips IMGUI]") { hideFlags = HideFlags.HideAndDontSave };
            if (Application.isPlaying) Object.DontDestroyOnLoad(_host);
            _host.AddComponent<ImguiHost>().Owner = this;
            _host.SetActive(false);
            return true;
        }

        public void Show(in TooltipRequest request)
        {
            _request = request;
            // Position is frozen when shown — that's how system tooltips behave.
            if (!request.IsUpdate || !_visible)
                _guiPointer = new Vector2(request.Pointer.x * Screen.width, request.Pointer.y * Screen.height);
            _visible = true;
            if (_host != null) _host.SetActive(true);
        }

        public void Hide()
        {
            _visible = false;
            if (_host != null) _host.SetActive(false);
        }

        public void Tick() { }

        public void Shutdown()
        {
            if (_host != null) Object.Destroy(_host);
            if (_background != null) Object.Destroy(_background);
            _host = null;
            _background = null;
        }

        static float DpiScale
        {
            get
            {
                float dpi = Screen.dpi;
                if (dpi <= 0f) return 1f;
#if UNITY_ANDROID || UNITY_IOS
                return Mathf.Max(1f, dpi / 160f);
#else
                return Mathf.Max(1f, dpi / 96f);
#endif
            }
        }

        void EnsureStyles(TooltipStyle s, float scale)
        {
            Color bg = s.OverrideColors ? s.BackgroundColor : DefaultBackground;
            int radius = Mathf.Max(0, Mathf.RoundToInt(s.ResolveCornerRadius(DefaultRadius) * scale));

            if (_background == null || radius != _bgRadius || bg != _bgColor)
            {
                if (_background != null) Object.Destroy(_background);
                _background = CreateRoundedTexture(bg, radius);
                _bgRadius = radius;
                _bgColor = bg;
            }

            if (_bubble == null) _bubble = new GUIStyle();
            _bubble.normal.background = _background;
            _bubble.border = new RectOffset(radius + 1, radius + 1, radius + 1, radius + 1);

            int fontSize = Mathf.Max(1, Mathf.RoundToInt(s.ResolveFontSize(DefaultFontSize) * scale));
            Color fg = s.OverrideColors ? s.TextColor : DefaultText;

            if (_text == null) _text = new GUIStyle { richText = false, clipping = TextClipping.Overflow };
            _text.fontSize = fontSize;
            _text.fontStyle = s.Bold ? FontStyle.Bold : FontStyle.Normal;
            _text.normal.textColor = fg;

            if (_title == null) _title = new GUIStyle { richText = false, clipping = TextClipping.Overflow };
            _title.fontSize = fontSize;
            _title.fontStyle = FontStyle.Bold;
            _title.normal.textColor = fg;
        }

        /// <summary>
        /// One measured block of text. Text that fits on its lines is drawn WITHOUT word wrap, so
        /// pixel rounding can't push the last word to a new line; wrapped text is measured 2 px
        /// narrower than it's drawn, so the computed height is never too small.
        /// </summary>
        struct Block
        {
            public GUIContent Content;
            public bool Wrap;
            public Vector2 Size;
        }

        static Block Measure(GUIStyle style, GUIContent content, float maxWidth)
        {
            style.wordWrap = false;
            Vector2 natural = style.CalcSize(content);
            if (natural.x <= maxWidth)
                return new Block { Content = content, Wrap = false, Size = new Vector2(Mathf.Ceil(natural.x) + 1f, Mathf.Ceil(natural.y)) };

            style.wordWrap = true;
            float height = style.CalcHeight(content, maxWidth - 2f);
            return new Block { Content = content, Wrap = true, Size = new Vector2(maxWidth, Mathf.Ceil(height)) };
        }

        /// <summary>Runs → IMGUI rich text (sizes converted to pixels).</summary>
        static string ToImguiMarkup(System.Collections.Generic.List<RichTextRun> runs, float basePoints, float scale)
        {
            var sb = new System.Text.StringBuilder();
            foreach (RichTextRun run in runs)
            {
                if (run.HasSize) sb.Append("<size=").Append(Mathf.Max(1, Mathf.RoundToInt(run.ResolveSize(basePoints) * scale))).Append('>');
                if (run.HasColor) sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGBA(run.Color)).Append('>');
                if (run.Bold) sb.Append("<b>");
                if (run.Italic) sb.Append("<i>");
                sb.Append(run.Text);
                if (run.Italic) sb.Append("</i>");
                if (run.Bold) sb.Append("</b>");
                if (run.HasColor) sb.Append("</color>");
                if (run.HasSize) sb.Append("</size>");
            }

            return sb.ToString();
        }

        static void DrawBlock(Rect rect, in Block block, GUIStyle style)
        {
            style.wordWrap = block.Wrap;
            GUI.Label(rect, block.Content, style);
        }

        static Texture2D CreateRoundedTexture(Color fill, int radius)
        {
            int size = radius * 2 + 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // rounded corners from the distance to the corner circle's centre
                float cx = x < radius ? radius - 0.5f : x >= size - radius ? size - radius - 0.5f : x;
                float cy = y < radius ? radius - 0.5f : y >= size - radius ? size - radius - 0.5f : y;
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy));
                float a = radius == 0 ? 1f : Mathf.Clamp01(radius - d + 0.5f);
                tex.SetPixel(x, y, new Color(fill.r, fill.g, fill.b, fill.a * a));
            }

            tex.Apply();
            return tex;
        }

        void Draw()
        {
            if (!_visible || Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout) return;

            TooltipStyle s = _request.Style ?? TooltipSettings.Style;
            float scale = DpiScale;
            EnsureStyles(s, scale);
            GUI.depth = -10000;

            float padX = s.ResolvePaddingX(DefaultPaddingX) * scale;
            float padY = s.ResolvePaddingY(DefaultPaddingY) * scale;
            float maxInner = Mathf.Max(20f, s.ResolveMaxWidth(DefaultMaxWidth) * scale - 2f * padX);

            bool hasTitle = !string.IsNullOrEmpty(_request.Title);
            // Rich text: IMGUI understands Unity's own <b>/<i>/<color>/<size> tags (no underline/strike).
            float basePoints = s.ResolveFontSize(DefaultFontSize);
            _text.richText = _request.TextRuns != null;
            _title.richText = _request.TitleRuns != null;
            string bodyText = _request.TextRuns != null ? ToImguiMarkup(_request.TextRuns, basePoints, scale) : _request.Text;
            string titleText = _request.TitleRuns != null ? ToImguiMarkup(_request.TitleRuns, basePoints, scale) : _request.Title;

            Block body = Measure(_text, new GUIContent(bodyText), maxInner);
            Block title = hasTitle ? Measure(_title, new GUIContent(titleText), maxInner) : default;

            float innerW = hasTitle ? Mathf.Max(body.Size.x, title.Size.x) : body.Size.x;
            float titleH = hasTitle ? title.Size.y : 0f;
            float spacing = hasTitle ? 2f * scale : 0f;

            var size = new Vector2(Mathf.Ceil(innerW + 2f * padX), Mathf.Ceil(titleH + spacing + body.Size.y + 2f * padY));

            TooltipPlacement placement = TooltipLayout.Resolve(s.Placement, _request.Trigger, _request.HasAnchor);
            var input = new LayoutInput
            {
                Placement = placement,
                Pointer = _guiPointer,
                CursorHeight = 20f * scale,
                HasAnchor = _request.HasAnchor,
                Anchor = ScreenRectUtility.NormalizedToGui(_request.Anchor),
                Size = size,
                Offset = TooltipLayout.OffsetToPixels(s.Offset, scale),
                Gap = (TooltipLayout.IsElementPlacement(placement) ? 8f : 4f) * scale,
                WorkArea = new Rect(2f, 2f, Screen.width - 4f, Screen.height - 4f),
                CenterOnPointer = false
            };
            Vector2 pos = TooltipLayout.Place(input);
            pos = new Vector2(Mathf.Round(pos.x), Mathf.Round(pos.y)); // whole pixels: crisp text, stable layout

            var bubbleRect = new Rect(pos, size);
            GUI.Box(bubbleRect, GUIContent.none, _bubble);

            float y = pos.y + padY;
            if (hasTitle)
            {
                DrawBlock(new Rect(pos.x + padX, y, innerW, titleH), title, _title);
                y += titleH + spacing;
            }

            DrawBlock(new Rect(pos.x + padX, y, innerW, body.Size.y), body, _text);
        }
    }
}
