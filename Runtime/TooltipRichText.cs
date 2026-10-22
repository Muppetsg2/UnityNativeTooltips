using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace NativeTooltips
{
    /// <summary>One uniformly formatted piece of tooltip text.</summary>
    public struct RichTextRun
    {
        public string Text;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strikethrough;

        /// <summary>True when <see cref="Color"/> overrides the tooltip's text colour.</summary>
        public bool HasColor;
        public Color Color;

        /// <summary>Absolute size in points (logical pixels); 0 = not set.</summary>
        public float Size;

        /// <summary>Multiplier for the base size (from <c>&lt;size=150%&gt;</c>); 1 = not set.</summary>
        public float SizeScale;

        /// <summary>Points added to the base size (from <c>&lt;size=+2&gt;</c>); 0 = not set.</summary>
        public float SizeDelta;

        /// <summary>True when the run's size differs from the base size.</summary>
        public bool HasSize => Size > 0f || !Mathf.Approximately(SizeScale, 1f) || SizeDelta != 0f;

        /// <summary>The run's font size for a given base size (both in points).</summary>
        public float ResolveSize(float baseSize)
        {
            if (Size > 0f) return Size;
            return Mathf.Max(1f, baseSize * SizeScale + SizeDelta);
        }

        internal bool SameFormat(in RichTextRun o) =>
            Bold == o.Bold && Italic == o.Italic && Underline == o.Underline && Strikethrough == o.Strikethrough &&
            HasColor == o.HasColor && (!HasColor || Color == o.Color) &&
            Size == o.Size && SizeScale == o.SizeScale && SizeDelta == o.SizeDelta;
    }

    /// <summary>
    /// Unity-style rich text for tooltips: <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, <c>&lt;u&gt;</c>, <c>&lt;s&gt;</c>,
    /// <c>&lt;color=#RRGGBB(AA)|name&gt;</c>, <c>&lt;size=14|150%|+2&gt;</c> and <c>&lt;noparse&gt;</c>.
    /// Unknown or malformed tags stay in the text literally (like Unity).
    /// Enabled with <see cref="TooltipSettings.RichText"/>.
    /// </summary>
    public static class TooltipRichText
    {
        /// <summary>True when the text contains at least one recognised tag.</summary>
        public static bool HasMarkup(string text) => TryParse(text, out _, out _);

        /// <summary>The text with recognised tags removed.</summary>
        public static string Strip(string text) => TryParse(text, out _, out string plain) ? plain : text;

        /// <summary>
        /// Parses <paramref name="text"/>. Returns false (and no runs) when it contains no recognised tag,
        /// so callers can keep the plain, fully native rendering path.
        /// </summary>
        public static bool TryParse(string text, out List<RichTextRun> runs, out string plain)
        {
            runs = null;
            plain = text;
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return false;

            var parser = new Parser();
            if (!parser.Run(text)) return false;

            runs = parser.Result;
            plain = parser.Plain.ToString();
            return true;
        }

        sealed class Parser
        {
            public readonly List<RichTextRun> Result = new List<RichTextRun>();
            public readonly StringBuilder Plain = new StringBuilder();

            readonly StringBuilder _current = new StringBuilder();
            readonly Stack<Color> _colors = new Stack<Color>();
            readonly Stack<RichTextRun> _sizes = new Stack<RichTextRun>(); // Size / SizeScale / SizeDelta
            RichTextRun _state = new RichTextRun { SizeScale = 1f };
            int _bold, _italic, _underline, _strike;

            public bool Run(string text)
            {
                bool anyTag = false;
                bool noParse = false;
                int i = 0;

                while (i < text.Length)
                {
                    char c = text[i];
                    if (c != '<')
                    {
                        _current.Append(c);
                        i++;
                        continue;
                    }

                    int end = text.IndexOf('>', i + 1);
                    if (end < 0)
                    {
                        _current.Append(text, i, text.Length - i);
                        break;
                    }

                    string tag = text.Substring(i + 1, end - i - 1).Trim();
                    bool closing = tag.StartsWith("/");
                    string body = closing ? tag.Substring(1).Trim() : tag;
                    string name = body, value = null;
                    int eq = body.IndexOf('=');
                    if (eq >= 0)
                    {
                        name = body.Substring(0, eq).Trim();
                        value = body.Substring(eq + 1).Trim().Trim('"', '\'');
                    }

                    name = name.ToLowerInvariant();

                    if (noParse)
                    {
                        if (closing && name == "noparse")
                        {
                            noParse = false;
                            anyTag = true;
                        }
                        else
                        {
                            _current.Append(text, i, end - i + 1);
                        }

                        i = end + 1;
                        continue;
                    }

                    bool handled;
                    bool formatChange = true;
                    if (!closing)
                    {
                        handled = true;
                        if (value == null && name == "b") _bold++;
                        else if (value == null && name == "i") _italic++;
                        else if (value == null && name == "u") _underline++;
                        else if (value == null && name == "s") _strike++;
                        else if (value == null && name == "noparse")
                        {
                            noParse = true;
                            formatChange = false;
                        }
                        else if (value != null && name == "color" && TryParseColor(value, out Color color)) _colors.Push(color);
                        else if (value != null && name == "size" && TryParseSize(value, out RichTextRun size)) _sizes.Push(size);
                        else handled = false;
                    }
                    else
                    {
                        handled = true;
                        if (name == "b" && _bold > 0) _bold--;
                        else if (name == "i" && _italic > 0) _italic--;
                        else if (name == "u" && _underline > 0) _underline--;
                        else if (name == "s" && _strike > 0) _strike--;
                        else if (name == "color" && _colors.Count > 0) _colors.Pop();
                        else if (name == "size" && _sizes.Count > 0) _sizes.Pop();
                        else handled = false;
                    }

                    if (!handled)
                    {
                        _current.Append(text, i, end - i + 1); // unknown tag stays as text
                    }
                    else
                    {
                        anyTag = true;
                        if (formatChange)
                        {
                            Flush();
                            Apply();
                        }
                    }

                    i = end + 1;
                }

                Flush();
                return anyTag;
            }

            void Flush()
            {
                if (_current.Length == 0) return;
                RichTextRun run = _state;
                run.Text = _current.ToString();
                Plain.Append(run.Text);
                _current.Length = 0;

                if (Result.Count > 0 && Result[Result.Count - 1].SameFormat(run))
                {
                    RichTextRun last = Result[Result.Count - 1];
                    last.Text += run.Text;
                    Result[Result.Count - 1] = last;
                }
                else
                {
                    Result.Add(run);
                }
            }

            void Apply()
            {
                _state.Bold = _bold > 0;
                _state.Italic = _italic > 0;
                _state.Underline = _underline > 0;
                _state.Strikethrough = _strike > 0;
                _state.HasColor = _colors.Count > 0;
                _state.Color = _colors.Count > 0 ? _colors.Peek() : default(Color);
                if (_sizes.Count > 0)
                {
                    RichTextRun sz = _sizes.Peek();
                    _state.Size = sz.Size;
                    _state.SizeScale = sz.SizeScale;
                    _state.SizeDelta = sz.SizeDelta;
                }
                else
                {
                    _state.Size = 0f;
                    _state.SizeScale = 1f;
                    _state.SizeDelta = 0f;
                }
            }
        }

        static bool TryParseSize(string value, out RichTextRun size)
        {
            size = new RichTextRun { SizeScale = 1f };
            var ci = CultureInfo.InvariantCulture;
            const NumberStyles ns = NumberStyles.Float;
            string v = value.Trim();
            if (v.EndsWith("px")) v = v.Substring(0, v.Length - 2);

            if (v.EndsWith("%"))
            {
                if (!float.TryParse(v.Substring(0, v.Length - 1), ns, ci, out float pct) || pct <= 0f) return false;
                size.SizeScale = pct / 100f;
                return true;
            }

            if (v.StartsWith("+") || v.StartsWith("-"))
            {
                if (!float.TryParse(v, ns, ci, out float delta)) return false;
                size.SizeDelta = delta;
                return true;
            }

            if (!float.TryParse(v, ns, ci, out float abs) || abs <= 0f) return false;
            size.Size = abs;
            return true;
        }

        static readonly Dictionary<string, uint> s_Named = new Dictionary<string, uint>
        {
            // Unity rich text colour names
            { "aqua", 0x00FFFFFF }, { "black", 0x000000FF }, { "blue", 0x0000FFFF }, { "brown", 0xA52A2AFF },
            { "cyan", 0x00FFFFFF }, { "darkblue", 0x0000A0FF }, { "fuchsia", 0xFF00FFFF }, { "green", 0x008000FF },
            { "grey", 0x808080FF }, { "gray", 0x808080FF }, { "lightblue", 0xADD8E6FF }, { "lime", 0x00FF00FF },
            { "magenta", 0xFF00FFFF }, { "maroon", 0x800000FF }, { "navy", 0x000080FF }, { "olive", 0x808000FF },
            { "orange", 0xFFA500FF }, { "purple", 0x800080FF }, { "red", 0xFF0000FF }, { "silver", 0xC0C0C0FF },
            { "teal", 0x008080FF }, { "white", 0xFFFFFFFF }, { "yellow", 0xFFFF00FF }
        };

        internal static bool TryParseColor(string value, out Color color)
        {
            color = default;
            string v = value.Trim().ToLowerInvariant();
            uint rgba;

            if (v.StartsWith("#"))
            {
                string hex = v.Substring(1);
                if (hex.Length == 3 || hex.Length == 4)
                {
                    // #RGB / #RGBA → #RRGGBB(AA)
                    var sb = new StringBuilder();
                    foreach (char h in hex) sb.Append(h).Append(h);
                    hex = sb.ToString();
                }

                if (hex.Length == 6) hex += "ff";
                if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgba))
                    return false;
            }
            else if (!s_Named.TryGetValue(v, out rgba))
            {
                return false;
            }

            color = new Color(((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f,
                ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);
            return true;
        }

        // ------------------------------------------------------------------ serialisation for native code

        internal const char RecordSeparator = '\u001E';
        internal const char FieldSeparator = '\u001F';

        /// <summary>
        /// Compact form for native backends (macOS, iOS, WebGL): runs separated by U+001E, fields by U+001F:
        /// <c>flags␟RRGGBBAA␟size␟scale␟delta␟text</c>, flags = 1 bold, 2 italic, 4 underline, 8 strike, 16 colour.
        /// </summary>
        internal static string Encode(List<RichTextRun> runs)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            for (int r = 0; r < runs.Count; r++)
            {
                RichTextRun run = runs[r];
                if (r > 0) sb.Append(RecordSeparator);
                int flags = (run.Bold ? 1 : 0) | (run.Italic ? 2 : 0) | (run.Underline ? 4 : 0) |
                            (run.Strikethrough ? 8 : 0) | (run.HasColor ? 16 : 0);
                Color32 c = run.Color;
                sb.Append(flags.ToString(ci)).Append(FieldSeparator)
                    .Append(c.r.ToString("X2")).Append(c.g.ToString("X2")).Append(c.b.ToString("X2")).Append(c.a.ToString("X2"))
                    .Append(FieldSeparator).Append(run.Size.ToString("0.###", ci))
                    .Append(FieldSeparator).Append(run.SizeScale.ToString("0.###", ci))
                    .Append(FieldSeparator).Append(run.SizeDelta.ToString("0.###", ci))
                    .Append(FieldSeparator)
                    .Append((run.Text ?? string.Empty).Replace(RecordSeparator, ' ').Replace(FieldSeparator, ' '));
            }

            return sb.ToString();
        }

        /// <summary>Runs for a plain string (one run, default format).</summary>
        internal static List<RichTextRun> Plain(string text) =>
            new List<RichTextRun> { new RichTextRun { Text = text ?? string.Empty, SizeScale = 1f } };
    }
}
