using NativeTooltips;
using UnityEngine;

/// <summary>
/// Example of a dynamic tooltip: override <see cref="NativeTooltip.TooltipText"/>.
/// The text is read every frame, so a visible tooltip updates live.
/// </summary>
public class TimeTooltip : NativeTooltip
{
    public override string TooltipText => $"Time since start: {Time.time:0.0} s";
}
