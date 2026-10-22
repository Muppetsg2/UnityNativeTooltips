using System;
using UnityEngine;

namespace NativeTooltips
{
    /// <summary>
    /// Visual and placement options for a tooltip. Every option defaults to "use the system value",
    /// so a default <see cref="TooltipStyle"/> produces a 100% native tooltip.
    ///
    /// Each backend applies what the platform allows and silently ignores the rest — check
    /// <see cref="Tooltip.Supports"/> at runtime. On macOS and WebGL, a style that changes anything
    /// switches to a native-looking custom bubble (see <see cref="TooltipSettings.CustomWindowMode"/>).
    ///
    /// All sizes are in points = logical pixels: 96-dpi pixels on Windows/Linux, pt on macOS/iOS,
    /// dp/sp on Android, CSS px on WebGL — so they look the same on HiDPI screens. Font sizes use the
    /// same unit (character height), e.g. the Windows tooltip font is 12, macOS 11, Android 14.
    /// </summary>
    [Serializable]
    public class TooltipStyle
    {
        [Header("Text")]
        [Tooltip("Multiplier for the system tooltip font size. Ignored when Font Size > 0.")]
        [Min(0.1f)]
        public float FontScale = 1f;

        [Tooltip("Absolute font size in points (logical pixels). 0 = system size × Font Scale.")]
        [Min(0f)]
        public float FontSize = 0f;

        [Tooltip("Bold text.")]
        public bool Bold;

        [Header("Bubble")]
        [Tooltip("Inner padding in points: X = left/right, Y = top/bottom. Negative = system default.")]
        public Vector2 Padding = new Vector2(-1f, -1f);

        [Tooltip("Maximum bubble width in points; longer text wraps. 0 = platform default.")]
        [Min(0f)]
        public float MaxWidth = 0f;

        [Tooltip("Corner radius in points. Negative = system default. Not available on Windows (Windows 11 rounds tooltips itself).")]
        public float CornerRadius = -1f;

        [Header("Colors")]
        [Tooltip("Use the colors below instead of the system theme. On Windows this switches the tooltip to the classic (unthemed) look.")]
        public bool OverrideColors;

        public Color BackgroundColor = new Color(0.13f, 0.13f, 0.14f, 0.96f);
        public Color TextColor = new Color(0.96f, 0.96f, 0.96f, 1f);

        [Header("Position")]
        [Tooltip("Where the bubble appears. Auto = system behaviour (below the cursor for mouse, above the element for touch).")]
        public TooltipPlacement Placement = TooltipPlacement.Auto;

        [Tooltip("Extra offset in points applied after placement. +X = right, +Y = up (Unity convention). Mirrored when the bubble flips to the other side.")]
        public Vector2 Offset = Vector2.zero;

        [Header("Timing")]
        [Tooltip("Hover delay in seconds before showing. Negative = system / TooltipSettings value.")]
        public float ShowDelay = -1f;

        [Header("Windows only")]
        [Tooltip("Use the Windows balloon tooltip (rounded bubble with a stem). Windows hangs it below the cursor / element and only flips it above when there is no room, so Placement only chooses which edge the stem points at.")]
        public bool WindowsBalloon;

        /// <summary>A copy that can be modified without affecting this instance.</summary>
        public TooltipStyle Clone() => (TooltipStyle)MemberwiseClone();

        // ------------------------------------------------------------------ helpers for backends

        /// <summary>True when any visual option differs from the system look.</summary>
        public bool HasAppearanceChanges =>
            !Mathf.Approximately(FontScale, 1f) || FontSize > 0f || Bold ||
            Padding.x >= 0f || Padding.y >= 0f || MaxWidth > 0f || CornerRadius >= 0f || OverrideColors;

        /// <summary>True when the placement differs from the system behaviour.</summary>
        public bool HasPlacementChanges => Placement != TooltipPlacement.Auto || Offset != Vector2.zero;

        /// <summary>True when the hover delay is overridden.</summary>
        public bool HasDelayChange => ShowDelay >= 0f;

        /// <summary>True when anything at all differs from a default style.</summary>
        public bool IsCustomized => HasAppearanceChanges || HasPlacementChanges || HasDelayChange || WindowsBalloon;

        internal float ResolveFontSize(float systemSize) =>
            FontSize > 0f ? FontSize : systemSize * Mathf.Max(0.1f, FontScale);

        internal float ResolvePaddingX(float systemValue) => Padding.x >= 0f ? Padding.x : systemValue;
        internal float ResolvePaddingY(float systemValue) => Padding.y >= 0f ? Padding.y : systemValue;
        internal float ResolveMaxWidth(float platformDefault) => MaxWidth > 0f ? MaxWidth : platformDefault;
        internal float ResolveCornerRadius(float systemValue) => CornerRadius >= 0f ? CornerRadius : systemValue;

        internal static readonly TooltipStyle Default = new TooltipStyle();
    }

    /// <summary>Where the tooltip bubble is placed.</summary>
    public enum TooltipPlacement
    {
        /// <summary>System behaviour: below the cursor for mouse hover, above the element for touch / manual show.</summary>
        Auto,
        BelowCursor,
        AboveCursor,
        AboveElement,
        BelowElement,
        LeftOfElement,
        RightOfElement
    }

    /// <summary>Standard icon shown next to the title (Windows only).</summary>
    public enum TooltipIcon
    {
        None = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>When macOS / WebGL replace the system tooltip with a native-looking custom bubble.</summary>
    public enum CustomWindowMode
    {
        /// <summary>
        /// Use the real system tooltip when possible; switch to the custom bubble only when the style
        /// or a title needs it, for <see cref="Tooltip.Show(string, float, TooltipStyle, string, TooltipIcon)"/>
        /// (system tooltips can't be shown on demand) and for touch on WebGL.
        /// </summary>
        WhenNeeded,

        /// <summary>Always use the custom bubble (consistent look and timing everywhere).</summary>
        Always,

        /// <summary>Never use it: styles are ignored and only the system tooltip is used (touch on WebGL still uses it, since browsers have no touch tooltips).</summary>
        Never
    }

    /// <summary>Features a backend can honour. Query with <see cref="Tooltip.Supports"/>.</summary>
    [Flags]
    public enum TooltipFeature
    {
        None = 0,
        FontSize = 1 << 0,
        Bold = 1 << 1,
        Padding = 1 << 2,
        MaxWidth = 1 << 3,
        CornerRadius = 1 << 4,
        Colors = 1 << 5,
        Placement = 1 << 6,
        Offset = 1 << 7,
        ShowDelay = 1 << 8,
        Title = 1 << 9,
        Icon = 1 << 10,
        Balloon = 1 << 11,

        /// <summary>Unity-style rich text tags (<c>&lt;b&gt;</c>, <c>&lt;color&gt;</c>, …) — see <see cref="TooltipRichText"/>.</summary>
        RichText = 1 << 12,

        /// <summary>Everything a fully custom-drawn bubble can do.</summary>
        AllCustom = FontSize | Bold | Padding | MaxWidth | CornerRadius | Colors | Placement | Offset | ShowDelay | Title | RichText
    }

    /// <summary>
    /// Optional extension of <see cref="ITooltipSource"/> that provides a title, an icon and a style.
    /// Implemented by <see cref="NativeTooltip"/> and <see cref="NativeTooltipManipulator"/>.
    /// </summary>
    public interface IRichTooltipSource : ITooltipSource
    {
        /// <summary>Bold first line above the text. Null/empty = none.</summary>
        string TooltipTitle { get; }

        /// <summary>Icon next to the title (Windows).</summary>
        TooltipIcon TooltipIcon { get; }

        /// <summary>Style for this source. Null = <see cref="TooltipSettings.Style"/>.</summary>
        TooltipStyle TooltipStyle { get; }
    }
}
