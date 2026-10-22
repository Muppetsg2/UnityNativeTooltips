<img alt="UnityNativeTooltips Logo" src="https://github.com/Muppetsg2/UnityNativeTooltips/blob/main/.github/unity-native-tooltips.svg" width="200">

# UnityNativeTooltips

Native, operating-system tooltips for Unity apps. The bubble looks and behaves exactly like tooltips in
every other app on the platform: system font, theme, delay, shadow and animation.

| Platform | System library / API | What the user sees | Native build step |
|---|---|---|---|
| **Windows** (player, optionally Editor) | `comctl32.dll` — `tooltips_class32` control (Common Controls v6) | A real Win32 tooltip in the Windows 10/11 theme; delays from `TTM_GETDELAYTIME` | **none** — plain P/Invoke |
| **macOS** (player) | AppKit — `NSView addToolTipRect:owner:userData:` / `NSToolTipManager` | A real AppKit tooltip (delay, look and placement are all system-provided) | **once**: `build_macos.sh` |
| **Linux** (player, optionally Editor) | GTK 3 — `libgtk-3.so.0` (3.22+) | A popup window with the `tooltip` CSS node → the GTK theme (Adwaita, Yaru, Breeze-GTK…) styles it like a system tooltip | **none** — plain P/Invoke |
| **Android** | Android framework — theme attributes `tooltipFrameBackground` / `tooltipForegroundColor` (API 26+), `PopupWindow`, `ViewConfiguration` | A bubble built from the same resources as `View.setTooltipText()`; long-press + haptics like the OS | **none** — JNI from C# |
| **iOS / iPadOS** | UIKit — `UIPopoverPresentationController` | A system popover with an arrow pointing at the element (iOS has no desktop-style tooltips) | automatic (Xcode) |
| **WebGL** | HTML `title` attribute on the `<canvas>` | The browser's native tooltip | **none** |
| Editor / other | — | IMGUI fallback bubble | — |

No third-party libraries are needed — every API above ships with the operating system.

---

## Installation

**Git URL (recommended):** *Window → Package Manager → + → Add package from git URL…*

```
https://github.com/Muppetsg2/UnityNativeTooltips.git
```

Pin a version with a tag, e.g. `https://github.com/Muppetsg2/UnityNativeTooltips.git#v1.0.0`.

**Local:** clone the repo and use *Add package from disk…* → `package.json`, or copy the repo folder into
your project's `Packages/` folder.

Requirements: Unity **2021.3+** and uGUI (present by default). Works with Mono and IL2CPP, with the legacy
Input Manager and with the Input System package (detected automatically). TextMeshPro is optional.

---

## Usage

### UGUI — Button, Button - TextMeshPro, Image, Toggle…

Add **Add Component → UI → Native Tooltip** to the object that has the `Button` (for *Button - TextMeshPro*
that's the root `Button` object, not the `Text (TMP)` child) and type the text. Done.

```csharp
using NativeTooltips;

saveButton.gameObject.AddComponent<NativeTooltip>().Text = "Save the project (Ctrl+S)";
```

- mouse/pen: the tooltip appears after hovering and briefly resting the cursor (system delay),
- touch: the tooltip appears after a long-press; that long-press **does not** trigger `onClick`
  (like Android — disable with `TooltipSettings.SuppressClickAfterLongPress = false`),
- clicking hides the tooltip until the pointer leaves the element,
- the text is read every frame, so changing `Text` updates a visible tooltip,
- non-interactable buttons still show their tooltip (configurable per component),
- also works on 3D/2D objects with colliders (camera with `PhysicsRaycaster` / `Physics2DRaycaster`).

Dynamic text — override `TooltipText`:

```csharp
public class AmmoTooltip : NativeTooltip
{
    public Weapon weapon;
    public override string TooltipText => $"Ammo: {weapon.Ammo}/{weapon.MaxAmmo}";
}
```

### UI Toolkit

```csharp
using NativeTooltips;

// a single element
saveButton.AddNativeTooltip("Save the project (Ctrl+S)");

// or a whole tree: every element with tooltip="..." in UXML
root.EnableNativeTooltips();
```

Assumes a full-screen panel (typical `PanelSettings` without a `RenderTexture`).

### From code

```csharp
Tooltip.Show("Saved!", 2f);                        // at the cursor, for 2 s (hover tooltips wait, then resume)
Tooltip.Show("Click here", someRectTransform);     // anchored to an element
Tooltip.Show("Text", new Rect(x, y, w, h));        // screen-pixel rectangle
Tooltip.Hide();

Debug.Log(Tooltip.BackendName + " native=" + Tooltip.IsNative);
```

Custom sources (map regions, IMGUI, …): implement `ITooltipSource` and call
`Tooltip.PointerEnter/PointerMove/PointerExit/PointerDown/PointerUp/Release`.

### Settings (`TooltipSettings`)

| Field | Default | Description |
|---|---|---|
| `Enabled` | `true` | Global switch |
| `UseFallbackWhenNativeUnavailable` | `true` | Use the IMGUI bubble when the native one fails |
| `ForceFallback` | `false` | Always use IMGUI (call `Tooltip.Reinitialize()` after changing) |
| `UseNativeInEditor` | `false` | Native tooltips in the Editor too (Windows, Linux) |
| `InitialDelayOverride` | `-1` | Show delay; `-1` = system value |
| `AutoPopDelayOverride` | `-1` | Auto-hide; `-1` = system value, `0` = never |
| `LongPressTimeoutOverride` | `-1` | Long-press duration; `-1` = system value |
| `TouchHideDelay` | `1.5` | How long the bubble stays after the finger is lifted |
| `RequireStationaryPointer` | `true` | Restart the delay while the cursor moves (like Win32) |
| `SuppressClickAfterLongPress` | `true` | A long-press that showed a tooltip cancels the click |
| `HapticFeedback` | `true` | Vibrate on long-press (Android/iOS) |
| `Style` | default | Default `TooltipStyle` for every tooltip (see below) |
| `RichText` | `true` | Interpret `<b>`, `<color>`, … tags (see *Rich text*) |
| `CustomWindowMode` | `WhenNeeded` | When macOS/WebGL use the custom bubble (see below) |
| `WebGLBubbleTheme` | `Browser` | WebGL bubble copies the browser's own tooltip (`Browser`) or the OS tooltip (`OperatingSystem`) |
| `VerboseLogging` | `false` | Log which backend was selected |

---

## Customizing tooltips (`TooltipStyle`)

Every option defaults to "system value", so an untouched style gives a 100% native tooltip.

```csharp
// Globally
TooltipSettings.Style.FontScale = 1.25f;
TooltipSettings.Style.Placement = TooltipPlacement.AboveCursor;
TooltipSettings.Style.Offset = new Vector2(0, 8);          // points, +y = up

// Per tooltip (component, UI Toolkit manipulator or Tooltip.Show)
var tooltip = button.gameObject.AddComponent<NativeTooltip>();
tooltip.Text = "Exports meshes, materials and animations.";
tooltip.Title = "Export to FBX";
tooltip.Icon = TooltipIcon.Info;                          // Windows
tooltip.Style = new TooltipStyle
{
    FontScale = 1.5f, Bold = true,
    Padding = new Vector2(14, 8), MaxWidth = 320, CornerRadius = 10,
    OverrideColors = true, BackgroundColor = new Color(1f, .85f, .3f), TextColor = Color.black,
    Placement = TooltipPlacement.RightOfElement, Offset = new Vector2(10, 0),
    ShowDelay = 0.2f
};

element.AddNativeTooltip("Save", new TooltipStyle { FontScale = 1.2f });
Tooltip.Show("Saved!", 2f, new TooltipStyle { Bold = true }, title: "Done");
```

In the Inspector, the **Native Tooltip** component has *Title*, *Icon*, a *Style Asset* slot and an
*Override Style* toggle with an inline style. Shared styles: *Create → Native Tooltips → Tooltip Style*.
Priority: inline style (Override Style) → style asset → `TooltipSettings.Style`.

| Option | Meaning |
|---|---|
| `FontScale` / `FontSize` | Multiplier for the system font size, or an absolute size |
| `Bold` | Bold text |
| `Padding` | Inner padding (X = left/right, Y = top/bottom); negative = system |
| `MaxWidth` | Wrap width; 0 = platform default |
| `CornerRadius` | Negative = system |
| `OverrideColors`, `BackgroundColor`, `TextColor` | Replace the theme colours |
| `Placement` | `Auto`, `BelowCursor`, `AboveCursor`, `AboveElement`, `BelowElement`, `LeftOfElement`, `RightOfElement` |
| `Offset` | Shift after placement (+x right, +y up); mirrored when the bubble flips to the other side |
| `ShowDelay` | Hover delay in seconds; negative = system |
| `WindowsBalloon` | Windows balloon tooltip with a stem. Windows always hangs it **below** the target and flips it above only when there's no room, so the stem points at the bottom of the cursor / element (Left/Right of element: its bottom-left / bottom-right corner) |
| `Title`, `Icon` (on the source) | Bold first line; standard icon (Windows) |

All sizes are **points = logical pixels** (96-dpi pixels on Windows/Linux, pt on Apple, dp/sp on Android,
CSS px on the web), so they look the same on HiDPI screens. When there is no room, the bubble flips to the
opposite side and is clamped to the screen / work area.

### What each platform supports

| | Font / bold | Padding | Max width | Radius | Colors | Placement / offset | Delay | Title | Rich text | Icon | Balloon |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **Windows** | ✅ | ✅ ¹ | ✅ | — ² | ✅ ³ | ✅ | ✅ | ✅ | ✅ ⁶ | ✅ | ✅ |
| **Linux (GTK)** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — | — |
| **Android** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — | — |
| **iOS** | ✅ | ✅ | ✅ | — | ✅ | ✅ ⁴ | ✅ | ✅ | ✅ | — | — |
| **macOS** | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | — | — |
| **WebGL** | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | ✅ ⁵ | — | — |
| **IMGUI fallback** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ ⁷ | — | — |

1. Added to the theme's own padding. 2. Windows 11 rounds tooltips itself. 3. Themes ignore custom colours,
so that tooltip switches to the classic, unthemed look. 4. Placement chooses the popover arrow direction;
the offset moves the arrow target. 5. Through the custom bubble (below). 6. Custom-drawn text, title and icon
inside the native tooltip, only when the text or title has tags. 7. No underline / strikethrough.

Check at runtime: `Tooltip.Supports(TooltipFeature.FontSize | TooltipFeature.Colors)`.

### Rich text

Tooltip texts and titles understand Unity's rich text tags (`TooltipSettings.RichText`, on by default):

```csharp
tooltip.Text = "I <b>love</b> <color=#FF0000>you</color>";
tooltip.Text = "Health: <color=#2E7D32><b>100%</b></color>\nMana: <color=#1565C0>45 / 120</color>";
```

| Tag | Effect |
|---|---|
| `<b>`, `<i>`, `<u>`, `<s>` | Bold, italic, underline, strikethrough |
| `<color=#RRGGBB>`, `<color=#RRGGBBAA>`, `<color=#RGB>`, `<color=red>` | Text colour (Unity colour names) |
| `<size=16>`, `<size=150%>`, `<size=+2>` | Absolute size in points, relative to the tooltip font, or a delta |
| `<noparse>…</noparse>` | Everything inside is shown literally |

Tags can be nested. Unknown or malformed tags stay in the text, like in Unity. Text **without** tags is not
affected at all and stays on the plain, fully native path. `TooltipRichText.Strip(text)` /
`TooltipRichText.HasMarkup(text)` are available for your own use; with `TooltipSettings.RichText = false` tags
are shown literally.

| Platform | How it's rendered |
|---|---|
| **Windows** | Only when the text or title contains tags: the native tooltip (regular or balloon) still draws its background, border, shadow and animation; text, title and icon are custom-drawn (`NM_CUSTOMDRAW`) with the tooltip's own font and the system icons. |
| **Linux (GTK)** | Pango markup in the native tooltip label |
| **Android** | Text spans (`StyleSpan`, `ForegroundColorSpan`, …) |
| **iOS** | `NSAttributedString` in the popover |
| **macOS / WebGL** | Custom bubble (system tooltips are plain text); with `CustomWindowMode.Never` the tags are removed |
| **IMGUI** | Unity's own rich text (no underline / strikethrough) |

### Custom bubble on macOS and WebGL

Real AppKit tooltips and the browser's `title` tooltip can't be styled, placed or shown on demand. For those
cases the plugin shows a **native-looking custom bubble** instead:

- **macOS** — a borderless, non-activating `NSPanel` with `NSVisualEffectMaterialToolTip` (the system tooltip
  material, light/dark automatically), `+[NSFont toolTipsFontOfSize:]`, the label colour and a system shadow.
- **WebGL** — a `<div>` styled like the tooltip **of the browser it runs in** (`TooltipSettings.WebGLBubbleTheme`),
  following `prefers-color-scheme`, with a short fade-in. A web page can't read how the browser draws its
  tooltip, so the plugin detects the browser (`navigator.userAgentData.brands`, then the user agent) and uses a
  matching preset:

  | Browser | Who draws the `title` tooltip | Bubble preset |
  |---|---|---|
  | Chrome, Brave, Vivaldi, Opera — Windows / Linux | Chromium itself (`views::TooltipAura`) | `chromium`: square, 1 px border in the text colour, Chromium's padding |
  | Edge — Windows / Linux | Chromium, Edge colours | `edge`: like `chromium` in Edge's colours (the same in light and dark mode) |
  | Opera GX — Windows / Linux | Chromium, in GX colours | `operagx`: Frutti Di Mare |
  | Firefox — Windows | Firefox itself | `firefox`: dark (rounded, 1 px border, light border); light (rounded, 1 px border, dark border) |
  | Firefox — Linux | GTK | `gtk` |
  | Any browser on macOS (Safari, Chrome, …) | AppKit | `mac` |
  | Mobile browsers | — (no hover tooltips) | neutral dark bubble |

  The detected browser appears in `Tooltip.BackendName` (e.g. `Browser: Opera GX, Windows (…)`). Opera GX is
  recognised only in a secure context (https or localhost), where `userAgentData` exists — on plain http it is
  treated as Opera. Opera GX lets users recolour the browser and a page can't read those colours, so the preset
  matches one GX theme (navy) and may differ from yours. `WebGLBubbleTheme.OperatingSystem` gives the previous
  OS-styled bubble (macOS, Windows 11 or a neutral dark bubble). To compare presets in any browser, add
  `?ntTheme=chromium` (or `edge`, `operagx`, `firefox`, `windows`, `mac`, `gtk`) to the page URL. Colours set in a `TooltipStyle`
  always win.

`TooltipSettings.CustomWindowMode` decides when:

| Mode | Behaviour |
|---|---|
| `WhenNeeded` (default) | System tooltip for plain hover tooltips; custom bubble when the style changes anything, a title is set, the text contains rich text tags, for `Tooltip.Show()` (system tooltips can't be shown on demand) and for touch on WebGL |
| `Always` | Always the custom bubble — same look and timing everywhere |
| `Never` | Always the system tooltip — styles are ignored (titles become a plain first line). Touch on WebGL still uses the bubble, since browsers have no touch tooltips |

After updating the package, **rebuild `NativeTooltipsMac.bundle`** (`build_macos.sh`) — version 1.2 (bundle API 3)
adds rich text and an older bundle is rejected (the plugin falls back to IMGUI with a warning).

### Demo

*Package Manager → Unity Native Tooltips → Samples → Demo → Import*, then open
`Assets/Samples/Unity Native Tooltips/<version>/Demo/Demo.unity` and press Play — the scene already contains a
GameObject with the `NativeTooltipsDemo` script. To see the native tooltips (the Editor shows the IMGUI fallback
by default), make a build of the scene or set `TooltipSettings.UseNativeInEditor = true` (Windows, Linux).
You can also add `NativeTooltipsDemo` to a GameObject in any other scene.

The demo builds Unity's default controls from code — **Button - TextMeshPro** (regular, multi-line,
long text, non-interactable, live-updating, `Tooltip.Show()`), **styled tooltips** (big text, custom colours,
title + icon, right-side placement with offset, Windows balloon), **rich text** (with and without a title, plus a plain-text
control), a side-by-side comparison of **title + icon** (native vs rich text, regular tooltip and Windows balloon), **legacy Button**, **Toggle** and **Slider**,
and shows which style features the current platform supports.
TextMeshPro buttons need TMP Essential Resources (Unity offers to import them the first time).

---

## How it works per platform

**Windows.** We create a `tooltips_class32` window with `TTS_ALWAYSTIP | TTS_NOPREFIX` and one *tracking*
tool (`TTF_TRACK | TTF_ABSOLUTE`). To get the themed look instead of the classic yellow rectangle, Common
Controls v6 is loaded through an activation context (`CreateActCtx` with a temporary manifest) — the .exe
is not modified. Placement: below the cursor tip, clamped to the monitor work area
(`MonitorFromPoint` / `GetMonitorInfo`). Delays are read from the control (`TTDT_INITIAL`, `TTDT_RESHOW`,
`TTDT_AUTOPOP`), so they follow system settings.
*Note:* in Exclusive Fullscreen no window can appear above the game — use *Fullscreen Window* (default) or
windowed mode.

**macOS.** AppKit has no "show tooltip now" API and ignores tooltip rects added while the cursor is already
inside them (and in Unity it always is — the whole UI is a single view). `NativeTooltipsMac.bundle` uses the
WebKit/Chromium technique: a transparent `NSView` above Unity's view captures the tracking rect registered
by `NSToolTipManager` and sends it fake `mouseExited`/`mouseEntered` events when the text changes. The OS
then measures the delay and draws a real tooltip. The view returns `nil` from `hitTest:`, so it never
intercepts the mouse. If the bubble doesn't appear on some macOS version, build with
`-DNT_OVERLAY_ACCEPTS_HITS=1` (the view then accepts hits and forwards them to Unity via the responder chain).

**Linux.** The Unity player (SDL) is not a GTK app, so at runtime we register a `GtkWindow` subclass whose
CSS node name is `tooltip`, with the `POPUP` window type and `GDK_WINDOW_TYPE_HINT_TOOLTIP`, using the same
structure as `GtkTooltipWindow` (window → box → label, `max-width-chars = 70`). The GTK theme styles it like
any system tooltip. GDK is forced to X11 (`gdk_set_allowed_backends`) because Wayland can't position popup
windows — Wayland sessions work through XWayland. Without `libgtk-3.so.0` or `DISPLAY` → IMGUI fallback.

**Android.** There's no public "show this view's tooltip now" API, and overlaying real `View`s with
`tooltipText` would block touch input for the game. So the bubble is built exactly like AOSP's
`TooltipPopup`: a `PopupWindow` + `TextView` with the background `?android:attr/tooltipFrameBackground` and
text colour `?android:attr/tooltipForegroundColor` from the activity theme (One UI, Pixel etc. have their own
variants), dimensions from `tooltip.xml` (16/6.5 dp padding, 14 sp, max 256 dp), placed above the finger for
touch and below the cursor for a mouse, long-press duration from `ViewConfiguration.getLongPressTimeout()`,
haptics via `HapticFeedbackConstants.LONG_PRESS`. Everything goes through JNI from C# — no `.java`/`.aar`
files, so R8/ProGuard can't strip anything.

**iOS/iPadOS.** iPhone has no desktop-style tooltips (`UIToolTipInteraction` only works with a pointer and
can't be triggered programmatically). The closest native equivalent is a popover with an arrow — the
popover style is forced on iPhone too (the delegate returns `UIModalPresentationNone`) and
`passthroughViews` keeps touches reaching the game. Text uses `UIFontTextStyleFootnote` with Dynamic Type.

**WebGL.** We set `title` on the `<canvas>` on hover and restore the previous value on exit. Browsers don't
show `title` for touch, so long-press on phones (and styled / rich / manual tooltips) use the HTML bubble,
styled after the detected browser's own tooltip (see *Custom bubble on macOS and WebGL*).

---

## Repository layout

The repository root **is** the package root, so it can be installed straight from a git URL:

```
UnityNativeTooltips/
├── package.json
├── README.md                      (+ .meta)
├── CHANGELOG.md                   (+ .meta)
├── LICENSE                        (+ .meta)
├── .github/workflows/
│   └── build-macos-bundle.yml     builds NativeTooltipsMac.bundle on a GitHub macOS runner
├── Runtime/
│   ├── NativeTooltips.Runtime.asmdef
│   ├── Tooltip.cs                 public API + TooltipSettings
│   ├── TooltipStyle.cs            TooltipStyle + enums
│   ├── TooltipStyleAsset.cs       ScriptableObject preset
│   ├── TooltipRichText.cs         rich text parser (<b>, <color>, …)
│   ├── NativeTooltip.cs           UGUI / physics component
│   ├── Internal/                  state machine, shared placement, rect helpers
│   ├── UIToolkit/                 NativeTooltipManipulator
│   ├── Backends/                  Windows, macOS, Linux, Android, iOS, WebGL, IMGUI
│   └── Plugins/
│       ├── iOS/NativeTooltips_iOS.mm
│       ├── WebGL/NativeTooltips.jslib
│       └── macOS/
│           ├── NativeTooltipsMac.bundle   (built on a Mac and committed)
│           └── Source~/                   .mm source + build_macos.sh (ignored by Unity)
└── Samples~/Demo/                 importable sample (ignored by Unity until imported)
    ├── Demo.unity                 ready-to-play scene with the demo object
    ├── NativeTooltipsDemo.cs      builds the demo UI from code
    ├── TimeTooltip.cs             dynamic tooltip example
    └── NativeTooltips.Samples.Demo.asmdef
```

---

## Tested platforms

| Platform | Backend | Tested | Notes |
|---|---|:---:|---|
| **Unity Editor** | IMGUI fallback | ✅ | Every style option, placements, manual `Tooltip.Show()` |
| **Windows** (10) | Win32 `tooltips_class32` | ✅ | Themed + coloured (unthemed) tooltips, balloon, placements, font/padding, rich text with title + icon |
| **macOS** | AppKit + custom `NSPanel` | ⬜ | Needs `NativeTooltipsMac.bundle` built on a Mac |
| **Linux** | GTK 3 | ⬜ | Only automated: rendered on Xvfb (Adwaita), click-through verified via XShape |
| **Android** | `PopupWindow` (theme tooltip style) | ⬜ | |
| **iOS / iPadOS** | `UIPopoverPresentationController` | ⬜ | |
| **WebGL** | `title` attribute + HTML bubble | ✅ | |

✅ = tested in a real build / Editor, ⬜ = not tested on a device yet (written against the documented APIs).
Feedback and test reports for the remaining platforms are welcome.

## Limitations

- The bubble does not follow the cursor (same as system tooltips).
- Only one tooltip is visible at a time (like the OS). Moving to another element hides it and shows the next one
  after the short "reshow" delay. Bubbles are click-through on every platform, so a bubble covering another
  element never blocks hovering it. While a `Tooltip.Show()` tooltip is visible, hover tooltips wait and resume
  when it ends.
- Windows balloons always hang below their target (Windows decides the side).
- Windows rich text: a rich balloon relies on Windows' own measuring (no size fallback, since its shape is a window region).
- UI Toolkit: a full-screen panel is assumed.

## License

See [LICENSE](LICENSE).
