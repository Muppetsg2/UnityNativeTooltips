using NativeTooltips;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if NATIVE_TOOLTIPS_TMP
using TMPro;
#endif

/// <summary>
/// Native Tooltips demo. Add this script to an empty GameObject in an empty scene and press Play
/// (or make a build — the Editor shows the IMGUI fallback by default, the native tooltip appears in players).
///
/// The scene is built from code and uses Unity's default controls, exactly as created by
/// GameObject → UI → Button - TextMeshPro and GameObject → UI → Legacy → Button.
/// </summary>
public class NativeTooltipsDemo : MonoBehaviour
{
    [Tooltip("Sprite used by Unity's default UI controls. Filled in automatically in the Editor.")]
    [SerializeField] Sprite uiSprite;

    [SerializeField] bool verboseLogging = true;

    void Reset() => FindDefaultSprite();
    void OnValidate() => FindDefaultSprite();

    void FindDefaultSprite()
    {
#if UNITY_EDITOR
        if (uiSprite == null)
            uiSprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
#endif
    }

    void Start()
    {
        FindDefaultSprite();
        TooltipSettings.VerboseLogging = verboseLogging;
        Tooltip.Initialize();

        EnsureEventSystem();
        Transform canvas = CreateCanvas().transform;

        CreateLabel(canvas, $"Backend: {Tooltip.BackendName}   (native: {Tooltip.IsNative})", new Vector2(0, 310), 22);
        CreateLabel(canvas, $"Style features here: {Tooltip.SupportedFeatures}", new Vector2(0, 278), 14);

#if NATIVE_TOOLTIPS_TMP
        // ---------------------------------------------------- Button - TextMeshPro
        CreateLabel(canvas, "Button - TextMeshPro", new Vector2(0, 225), 20);

        AddTooltip(CreateTmpButton(canvas, "Save", new Vector2(-400, 180)),
            "Save the project (Ctrl+S)");

        AddTooltip(CreateTmpButton(canvas, "Export", new Vector2(-200, 180)),
            "Export the scene to FBX.\nA second line — Unicode: zażółć gęślą jaźń, 日本語, Ελληνικά.");

        AddTooltip(CreateTmpButton(canvas, "Long text", new Vector2(0, 180)),
            "A very long tooltip that the operating system should wrap, because it is wider than " +
            "the maximum width. Check how it looks on your platform.");

        GameObject tmpDisabled = CreateTmpButton(canvas, "Disabled", new Vector2(200, 180));
        tmpDisabled.GetComponent<Button>().interactable = false;
        AddTooltip(tmpDisabled, "This button is not interactable — the tooltip still shows.");

        // Dynamic text: a subclass overrides TooltipText (read every frame).
        CreateTmpButton(canvas, "Counter", new Vector2(400, 180)).AddComponent<TimeTooltip>();

        GameObject tmpManual = CreateTmpButton(canvas, "Tooltip.Show()", new Vector2(0, 130));
        tmpManual.GetComponent<RectTransform>().sizeDelta = new Vector2(220, 30);
        tmpManual.GetComponent<Button>().onClick.AddListener(() =>
            Tooltip.Show("Shown from code for 2 seconds", (RectTransform)tmpManual.transform, 2f));
#else
        CreateLabel(canvas, "TextMeshPro is not installed — showing legacy buttons only.", new Vector2(0, 180), 18);
#endif

        // ---------------------------------------------------- Styled tooltips (TooltipStyle)
        CreateLabel(canvas, "Styled tooltips (TooltipStyle)", new Vector2(0, 70), 20);

        AddTooltip(CreateButton(canvas, "Big text", new Vector2(-400, 25)),
            "Font × 1.5, bold, more padding",
            new TooltipStyle { FontScale = 1.5f, Bold = true, Padding = new Vector2(14f, 8f) });

        AddTooltip(CreateButton(canvas, "Colors", new Vector2(-200, 25)),
            "Custom colors and corner radius\n(Windows: classic unthemed look)",
            new TooltipStyle
            {
                OverrideColors = true,
                BackgroundColor = new Color(1f, 0.85f, 0.3f, 1f),
                TextColor = new Color(0.1f, 0.1f, 0.1f, 1f),
                CornerRadius = 10f,
                Padding = new Vector2(12f, 6f)
            });

        AddTooltip(CreateButton(canvas, "Title + icon", new Vector2(0, 25)),
            "Exports meshes, materials and animations.\nThe icon is shown on Windows only.",
            null, "Export to FBX", TooltipIcon.Info);

        AddTooltip(CreateButton(canvas, "Right side", new Vector2(200, 25)),
            "Placed to the right of the button,\n10 pt further right, after 0.1 s",
            new TooltipStyle { Placement = TooltipPlacement.RightOfElement, Offset = new Vector2(10f, 0f), ShowDelay = 0.1f });

        AddTooltip(CreateButton(canvas, "Balloon", new Vector2(400, 25)),
            "Windows balloon tooltip pointing at the button (other platforms: regular tooltip below the button)",
            new TooltipStyle { WindowsBalloon = true, Placement = TooltipPlacement.BelowElement });

        // ---------------------------------------------------- Legacy Button, Toggle, Slider
        CreateLabel(canvas, "Button (Legacy), Toggle, Slider", new Vector2(0, -60), 20);

        AddTooltip(CreateLegacyButton(canvas, "Open", new Vector2(-330, -110)), "Open a file (Ctrl+O)");
        AddTooltip(CreateLegacyButton(canvas, "Close", new Vector2(-150, -110)), "Close the window");
        AddTooltip(CreateToggle(canvas, new Vector2(40, -110)), "Toggle with a native tooltip");
        AddTooltip(CreateSlider(canvas, new Vector2(260, -110)), "Volume",
            new TooltipStyle { Placement = TooltipPlacement.AboveElement });

        // ---------------------------------------------------- Rich text (TooltipSettings.RichText)
        CreateLabel(canvas, "Rich text", new Vector2(0, -155), 20);

        AddTooltip(CreateButton(canvas, "Rich text", new Vector2(-200, -195)),
            "I <b>love</b> <color=#FF0000>you</color> — <i>italic</i>, <u>underline</u>, <s>strike</s>, <size=150%>big</size>");

        AddTooltip(CreateButton(canvas, "Rich + title", new Vector2(0, -195)),
            "Health: <color=#2E7D32><b>100%</b></color>\nMana: <color=#1565C0>45 / 120</color>",
            null, "<color=#2E7D32>Status</color>");

        AddTooltip(CreateButton(canvas, "No markup", new Vector2(200, -195)),
            "Plain text — stays on the fully native path (on Windows: no custom drawing).");

        // ---------------------------------------------------- Title + icon: native vs rich text, side by side
        // Same content in each pair; only the formatting differs. On Windows the "native" ones are drawn
        // entirely by the system (TTM_SETTITLE), the "rich" ones custom-draw title, icon and text.
        CreateLabel(canvas, "Title + icon: native vs rich text", new Vector2(0, -240), 20);

        var balloon = new TooltipStyle { WindowsBalloon = true, Placement = TooltipPlacement.BelowElement };

        AddTooltip(CreateButton(canvas, "Native + icon", new Vector2(-300, -280)),
            "Exports meshes, materials and animations.\nRegular tooltip, title and icon drawn by the system.",
            null, "Export to FBX", TooltipIcon.Info);

        AddTooltip(CreateButton(canvas, "Rich + icon", new Vector2(-100, -280)),
            "Exports <b>meshes</b>, <color=#C62828>materials</color> and <i>animations</i>.\nRegular tooltip, custom-drawn title and icon.",
            null, "<color=#1565C0>Export</color> to FBX", TooltipIcon.Info);

        AddTooltip(CreateButton(canvas, "Native balloon", new Vector2(100, -280)),
            "Unsaved changes will be lost.\nBalloon, title and icon drawn by the system.",
            balloon, "Unsaved changes", TooltipIcon.Warning);

        AddTooltip(CreateButton(canvas, "Rich balloon", new Vector2(300, -280)),
            "<b>Unsaved</b> changes will be <color=#C62828>lost</color>.\nBalloon, custom-drawn title and icon.",
            balloon, "<color=#E65100>Unsaved</color> changes", TooltipIcon.Warning);

        CreateLabel(canvas,
            "Mouse: hover and keep the cursor still for a moment.   Touch: press and hold (long-press).",
            new Vector2(0, -330), 16);
    }

    // ------------------------------------------------------------------ helpers

    static void AddTooltip(GameObject go, string text, TooltipStyle style = null, string title = null,
        TooltipIcon icon = TooltipIcon.None)
    {
        var tooltip = go.AddComponent<NativeTooltip>();
        tooltip.Text = text;
        tooltip.Title = title;
        tooltip.Icon = icon;
        if (style != null) tooltip.Style = style; // per-component style; null = TooltipSettings.Style
    }

    GameObject CreateButton(Transform parent, string label, Vector2 position)
    {
#if NATIVE_TOOLTIPS_TMP
        return CreateTmpButton(parent, label, position);
#else
        return CreateLegacyButton(parent, label, position);
#endif
    }

    DefaultControls.Resources UiResources => new DefaultControls.Resources
    {
        standard = uiSprite,
        background = uiSprite,
        knob = uiSprite,
        checkmark = uiSprite
    };

#if NATIVE_TOOLTIPS_TMP
    /// <summary>Same hierarchy and defaults as GameObject → UI → Button - TextMeshPro.</summary>
    GameObject CreateTmpButton(Transform parent, string label, Vector2 position)
    {
        var root = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
        root.transform.SetParent(parent, false);
        var rt = (RectTransform)root.transform;
        rt.sizeDelta = new Vector2(160, 30);
        rt.anchoredPosition = position;

        var image = root.GetComponent<Image>();
        image.sprite = uiSprite;
        image.type = Image.Type.Sliced;
        image.color = Color.white;

        var textGo = new GameObject("Text (TMP)", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(root.transform, false);
        var textRt = (RectTransform)textGo.transform;
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.sizeDelta = Vector2.zero;

        var text = textGo.GetComponent<TextMeshProUGUI>();
        text.text = label;
        text.fontSize = 24;
        text.alignment = TextAlignmentOptions.Center;
        text.color = new Color32(50, 50, 50, 255);
        return root;
    }
#endif

    /// <summary>GameObject → UI → Legacy → Button (DefaultControls).</summary>
    GameObject CreateLegacyButton(Transform parent, string label, Vector2 position)
    {
        GameObject go = DefaultControls.CreateButton(UiResources);
        go.transform.SetParent(parent, false);
        ((RectTransform)go.transform).anchoredPosition = position;
        var text = go.GetComponentInChildren<Text>();
        text.text = label;
        if (text.font == null) text.font = BuiltinFont();
        return go;
    }

    GameObject CreateToggle(Transform parent, Vector2 position)
    {
        GameObject go = DefaultControls.CreateToggle(UiResources);
        go.transform.SetParent(parent, false);
        ((RectTransform)go.transform).anchoredPosition = position;
        var text = go.GetComponentInChildren<Text>();
        text.text = "Toggle";
        if (text.font == null) text.font = BuiltinFont();
        return go;
    }

    GameObject CreateSlider(Transform parent, Vector2 position)
    {
        GameObject go = DefaultControls.CreateSlider(UiResources);
        go.transform.SetParent(parent, false);
        ((RectTransform)go.transform).anchoredPosition = position;
        return go;
    }

    static void EnsureEventSystem()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindAnyObjectByType<EventSystem>() != null) return;
#else
        if (FindObjectOfType<EventSystem>() != null) return;
#endif
        var es = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER && NATIVE_TOOLTIPS_INPUT_SYSTEM
        es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
        es.AddComponent<StandaloneInputModule>();
#endif
    }

    static Canvas CreateCanvas()
    {
        var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.matchWidthOrHeight = 0.5f;
        return canvas;
    }

    static void CreateLabel(Transform parent, string content, Vector2 position, int size)
    {
        var go = new GameObject("Label", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchoredPosition = position;
        rt.sizeDelta = new Vector2(1200, 40);

#if NATIVE_TOOLTIPS_TMP
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = content;
        tmp.fontSize = size;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
#else
        var text = go.AddComponent<Text>();
        text.text = content;
        text.fontSize = size;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        text.font = BuiltinFont();
#endif
    }

    static Font BuiltinFont()
    {
        // Unity 2022.2+ renamed the built-in font.
        Font f = null;
        try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { /* older versions */ }
        if (f == null)
        {
            try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { /* newer versions */ }
        }

        return f;
    }
}
