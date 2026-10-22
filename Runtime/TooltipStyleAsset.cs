using UnityEngine;

namespace NativeTooltips
{
    /// <summary>A reusable <see cref="TooltipStyle"/> stored as an asset (Create → Native Tooltips → Tooltip Style).</summary>
    [CreateAssetMenu(menuName = "Native Tooltips/Tooltip Style", fileName = "TooltipStyle", order = 1000)]
    public class TooltipStyleAsset : ScriptableObject
    {
        public TooltipStyle Style = new TooltipStyle();
    }
}
