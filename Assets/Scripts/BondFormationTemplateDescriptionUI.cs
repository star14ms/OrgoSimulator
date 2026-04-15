using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Screen-space blurb for picked redistribute template stem/tip (bond-steps debug).</summary>
public static class BondFormationTemplateDescriptionUI
{
    static GameObject panel;
    static TextMeshProUGUI label;
    static string lastBaseText;
    static DebugSelectionDriver debugDriver;
    static DebugUiBootstrap debugUiBootstrap;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void InstallDebugUiBootstrap()
    {
        if (debugUiBootstrap != null) return;
        var go = new GameObject("BondFormationTemplateDescriptionBootstrap");
        Object.DontDestroyOnLoad(go);
        debugUiBootstrap = go.AddComponent<DebugUiBootstrap>();
    }

    public static void Show(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Hide();
            return;
        }
        EnsurePanel();
        lastBaseText = text;
        if (label != null) label.text = BuildDisplayText(text);
        if (panel != null) panel.SetActive(true);
    }

    public static void ShowDebugSelectedAtomOnly()
    {
        EnsurePanel();
        string baseText = string.IsNullOrEmpty(lastBaseText) ? "Debug Selection" : lastBaseText;
        if (label != null) label.text = BuildDisplayText(baseText);
        if (panel != null) panel.SetActive(true);
    }

    public static void Hide()
    {
        if (panel != null) panel.SetActive(false);
    }

    static void EnsurePanel()
    {
        if (panel != null) return;
        var canvas = Object.FindFirstObjectByType<Canvas>();
        if (canvas == null)
        {
            var canvasGo = new GameObject("BondFormationTemplateFallbackCanvas");
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40000;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasGo.AddComponent<GraphicRaycaster>();
        }
        Transform parent = canvas.rootCanvas != null ? canvas.rootCanvas.transform : canvas.transform;
        float sf = canvas.scaleFactor > 0.01f ? canvas.scaleFactor : 1f;
        float w = 560f * sf;
        float h = 148f * sf;
        float bottomY = 112f * sf;

        panel = new GameObject("BondFormationTemplateDescription");
        panel.transform.SetParent(parent, false);
        panel.transform.SetAsLastSibling();
        var descCanvas = panel.AddComponent<Canvas>();
        descCanvas.overrideSorting = true;
        descCanvas.sortingOrder = 40000;
        panel.AddComponent<GraphicRaycaster>();
        var rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, bottomY);
        rt.sizeDelta = new Vector2(w, h);

        var bg = panel.AddComponent<Image>();
        bg.color = new Color(0.08f, 0.1f, 0.14f, 0.92f);
        bg.raycastTarget = false;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(panel.transform, false);
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        float pad = 12f * sf;
        textRt.offsetMin = new Vector2(pad, pad);
        textRt.offsetMax = new Vector2(-pad, -pad);
        label = textGo.AddComponent<TextMeshProUGUI>();
        label.font = AtomFunction.GetDefaultFont();
        label.fontSize = Mathf.Max(20, Mathf.RoundToInt(24f * sf));
        label.alignment = TextAlignmentOptions.Center;
        label.color = new Color(0.92f, 0.94f, 0.96f, 1f);
        label.textWrappingMode = TextWrappingModes.Normal;
        label.raycastTarget = false;

        EnsureDebugDriver();
    }

    static string BuildDisplayText(string baseText)
    {
        if (!BondFormationDebugController.SteppedModeEnabled)
            return baseText;

        var edit = Object.FindFirstObjectByType<EditModeManager>();
        var selectedAtom = edit != null ? edit.SelectedAtom : null;
        string selectedId = selectedAtom != null ? selectedAtom.GetInstanceID().ToString() : "none";
        return baseText + "\nSelected Atom InstanceID: " + selectedId;
    }

    static void EnsureDebugDriver()
    {
        if (debugDriver != null) return;
        var go = new GameObject("BondFormationTemplateDescriptionDebugDriver");
        Object.DontDestroyOnLoad(go);
        debugDriver = go.AddComponent<DebugSelectionDriver>();
    }

    sealed class DebugSelectionDriver : MonoBehaviour
    {
        void LateUpdate()
        {
            if (!BondFormationDebugController.SteppedModeEnabled) return;
            ShowDebugSelectedAtomOnly();
        }
    }

    sealed class DebugUiBootstrap : MonoBehaviour
    {
        void LateUpdate()
        {
            if (!BondFormationDebugController.SteppedModeEnabled) return;
            ShowDebugSelectedAtomOnly();
        }
    }
}
