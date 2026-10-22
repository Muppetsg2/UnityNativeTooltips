using System.Collections.Generic;
using UnityEngine;

namespace NativeTooltips.Backends
{
    /// <summary>Everything a backend needs to display the bubble.</summary>
    internal struct TooltipRequest
    {
        /// <summary>Plain text (rich text tags removed).</summary>
        public string Text;

        /// <summary>Plain title (rich text tags removed).</summary>
        public string Title;

        /// <summary>Formatted runs of <see cref="Text"/>; null when the text has no markup (or rich text is off).</summary>
        public List<RichTextRun> TextRuns;

        /// <summary>Formatted runs of <see cref="Title"/>; null when the title has no markup.</summary>
        public List<RichTextRun> TitleRuns;

        public bool HasRichText => TextRuns != null || TitleRuns != null;
        public TooltipIcon Icon;
        public TooltipTrigger Trigger;

        /// <summary>Resolved style — never null.</summary>
        public TooltipStyle Style;

        /// <summary>Element rectangle, normalized 0..1 relative to the Unity view, origin at the top-left.</summary>
        public Rect Anchor;
        public bool HasAnchor;

        /// <summary>Pointer position, normalized like <see cref="Anchor"/>.</summary>
        public Vector2 Pointer;

        /// <summary>true = the bubble is already visible and only the text changed — don't move it.</summary>
        public bool IsUpdate;

        /// <summary>Text with the title as a plain first line (for backends that can't style a title).</summary>
        public string PlainTextWithTitle =>
            string.IsNullOrEmpty(Title) ? Text : Title + "\n" + Text;
    }

    /// <summary>
    /// Implementation for a single platform. All methods are called on Unity's main thread.
    /// </summary>
    internal interface ITooltipBackend
    {
        string Name { get; }

        /// <summary>false only for the IMGUI fallback / null backend.</summary>
        bool IsNative { get; }

        /// <summary>Style features this backend honours.</summary>
        TooltipFeature Features { get; }

        /// <summary>
        /// true = for this request the OS measures the hover delay itself and decides when to show the
        /// bubble (native macOS / WebGL title). The driver then calls <see cref="Show"/> right on enter
        /// and <see cref="Hide"/> on exit.
        /// </summary>
        bool UsesSystemHoverDelay(in TooltipRequest request);

        /// <summary>Show delay (s) according to system settings.</summary>
        float InitialDelay { get; }

        /// <summary>Shorter delay when moving between elements while a bubble was just visible.</summary>
        float ReshowDelay { get; }

        /// <summary>Seconds after which a hover bubble hides by itself (0 = never).</summary>
        float AutoPopDelay { get; }

        /// <summary>Long-press duration (s) according to the system.</summary>
        float LongPressTimeout { get; }

        bool Initialize();
        void Show(in TooltipRequest request);
        void Hide();

        /// <summary>Called every frame (e.g. to pump the GTK event loop).</summary>
        void Tick();

        void Shutdown();
    }

    /// <summary>Does nothing — used when tooltips are disabled or nothing else works.</summary>
    internal sealed class NullTooltipBackend : ITooltipBackend
    {
        public string Name => "None";
        public bool IsNative => false;
        public TooltipFeature Features => TooltipFeature.None;
        public bool UsesSystemHoverDelay(in TooltipRequest request) => false;
        public float InitialDelay => 0.5f;
        public float ReshowDelay => 0.1f;
        public float AutoPopDelay => 0f;
        public float LongPressTimeout => 0.5f;
        public bool Initialize() => true;
        public void Show(in TooltipRequest request) { }
        public void Hide() { }
        public void Tick() { }
        public void Shutdown() { }
    }

    /// <summary>Decides, per request, whether macOS / WebGL should use the custom bubble.</summary>
    internal static class CustomWindowPolicy
    {
        public static bool UseCustom(in TooltipRequest request, bool customAvailable)
        {
            if (!customAvailable) return false;

            switch (TooltipSettings.CustomWindowMode)
            {
                case CustomWindowMode.Always:
                    return true;
                case CustomWindowMode.Never:
                    return false;
                default:
                    return request.Trigger != TooltipTrigger.Hover ||   // system tooltips can't be shown on demand
                           request.Style.IsCustomized ||
                           !string.IsNullOrEmpty(request.Title) ||
                           request.HasRichText;                         // system tooltips are plain text
            }
        }
    }
}
