using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lower-left HUD (above charge row): phase advance (top) + debug mode toggle (below). Same width as charge row. Guide highlight uses bond line tint in code.
/// </summary>
public class BondFormationDebugHud : MonoBehaviour
{
    public static BondFormationDebugHud Instance { get; private set; }

    Toggle steppedToggle;
    Image steppedToggleBackground;
    Button nextButton;
    TextMeshProUGUI nextPhaseLabel;
    GameObject nextRowRoot;
    float rowHStored;
    float innerGapStored;

    float dockLeftPx;
    float dockBottomPx;
    float dockWidthPx;
    float dockHeightPx;

    void Start()
    {
        ApplyLowerLeftDock();
    }

    void ApplyLowerLeftDock()
    {
        var rt = transform as RectTransform;
        if (rt == null) return;
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.localScale = Vector3.one;
        rt.localRotation = Quaternion.identity;
        rt.anchoredPosition = new Vector2(dockLeftPx, dockBottomPx);
        rt.sizeDelta = new Vector2(dockWidthPx, dockHeightPx);
    }

    static readonly Color SteppedToggleBgOff = new Color(0.2f, 0.22f, 0.28f, 0.94f);
    static readonly Color SteppedToggleBgOn = new Color(0.16f, 0.36f, 0.26f, 0.96f);

    void ApplySteppedToggleBackground(bool steppedOn)
    {
        if (steppedToggleBackground != null)
            steppedToggleBackground.color = steppedOn ? SteppedToggleBgOn : SteppedToggleBgOff;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Creates the HUD at lower-left, stacked above the charge-mode row.</summary>
    public static void Build(Canvas canvas, float layoutButtonSize, int layoutFontSize, System.Func<float, float> px)
    {
        if (canvas == null) return;
        if (Instance != null) return;

        float rowH = layoutButtonSize;
        float innerGap = px(4f);
        float panelW = px(178f);

        Transform hudParent = canvas.rootCanvas != null ? canvas.rootCanvas.transform : canvas.transform;
        var wrap = new GameObject("BondFormationDebugHud");
        wrap.transform.SetParent(hudParent, false);
        wrap.transform.SetAsLastSibling();
        var wrapRect = wrap.AddComponent<RectTransform>();
        wrapRect.anchorMin = new Vector2(0f, 0f);
        wrapRect.anchorMax = new Vector2(0f, 0f);
        wrapRect.pivot = new Vector2(0f, 0f);
        float stackGap = px(6f);
        float leftInset = px(10f);
        float bottomInset = px(10f) + rowH + stackGap;
        wrapRect.anchoredPosition = new Vector2(leftInset, bottomInset);

        var v = wrap.AddComponent<VerticalLayoutGroup>();
        v.spacing = innerGap;
        v.childAlignment = TextAnchor.UpperCenter;
        v.childControlWidth = true;
        v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        v.padding = new RectOffset(0, 0, 0, 0);

        var nextGo = new GameObject("BondDebugPhaseAdvance");
        nextGo.transform.SetParent(wrap.transform, false);
        var nextLe = nextGo.AddComponent<LayoutElement>();
        nextLe.preferredHeight = rowH;
        nextLe.flexibleWidth = 1f;
        nextGo.AddComponent<RectTransform>();
        var nextImg = nextGo.AddComponent<Image>();
        var nextBase = new Color(0.28f, 0.42f, 0.32f, 0.94f);
        nextImg.color = nextBase;
        var nextB = nextGo.AddComponent<Button>();
        nextB.interactable = false;
        nextB.onClick.AddListener(() => BondFormationDebugController.RequestAdvance());
        var colors = nextB.colors;
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.08f;
        colors.normalColor = nextBase;
        colors.highlightedColor = Color.Lerp(nextBase, Color.white, 0.2f);
        colors.pressedColor = Color.Lerp(nextBase, new Color(0.6f, 0.85f, 0.55f), 0.35f);
        colors.selectedColor = nextBase;
        colors.disabledColor = new Color(0.24f, 0.36f, 0.28f, 0.88f);
        nextB.colors = colors;

        var nextLabelGo = new GameObject("Label");
        nextLabelGo.transform.SetParent(nextGo.transform, false);
        var nextLabelRt = nextLabelGo.AddComponent<RectTransform>();
        nextLabelRt.anchorMin = Vector2.zero;
        nextLabelRt.anchorMax = Vector2.one;
        nextLabelRt.offsetMin = Vector2.zero;
        nextLabelRt.offsetMax = Vector2.zero;
        var nextTmp = nextLabelGo.AddComponent<TextMeshProUGUI>();
        nextTmp.font = AtomFunction.GetDefaultFont();
        nextTmp.fontSize = Mathf.Max(8, layoutFontSize - 2);
        nextTmp.alignment = TextAlignmentOptions.Center;
        nextTmp.raycastTarget = false;
        nextTmp.color = new Color(0.95f, 0.95f, 0.98f, 1f);
        nextTmp.text = "";

        var toggleGo = new GameObject("BondDebugSteppedToggle");
        toggleGo.transform.SetParent(wrap.transform, false);
        var toggleLe = toggleGo.AddComponent<LayoutElement>();
        toggleLe.preferredHeight = rowH;
        toggleLe.flexibleWidth = 1f;
        toggleGo.AddComponent<RectTransform>();
        var toggleBg = toggleGo.AddComponent<Image>();
        toggleBg.color = SteppedToggleBgOff;
        var steppedT = toggleGo.AddComponent<Toggle>();
        steppedT.isOn = BondFormationDebugController.SteppedModeEnabled;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(toggleGo.transform, false);
        var labelRt = labelGo.AddComponent<RectTransform>();
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;
        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.fontSize = Mathf.Max(8, layoutFontSize - 2);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        tmp.color = new Color(0.95f, 0.95f, 0.98f, 1f);
        tmp.text = "Debug mode";

        steppedT.graphic = null;
        steppedT.targetGraphic = toggleBg;

        var hud = wrap.AddComponent<BondFormationDebugHud>();
        hud.dockLeftPx = leftInset;
        hud.dockBottomPx = bottomInset;
        hud.dockWidthPx = panelW;
        hud.steppedToggle = steppedT;
        hud.steppedToggleBackground = toggleBg;
        hud.nextButton = nextB;
        hud.nextPhaseLabel = nextTmp;
        hud.nextRowRoot = nextGo;
        hud.rowHStored = rowH;
        hud.innerGapStored = innerGap;
        steppedT.onValueChanged.AddListener(on =>
        {
            BondFormationDebugController.SteppedModeEnabled = on;
            if (!on)
            {
                BondFormationDebugController.OnSteppedModeDisabled();
                if (hud.nextPhaseLabel != null)
                    hud.nextPhaseLabel.text = "";
            }
            hud.ApplySteppedToggleBackground(on);
            hud.SyncNextRowVisibility();
        });
        hud.ApplySteppedToggleBackground(steppedT.isOn);
        hud.SyncNextRowVisibility();
        Instance = hud;
    }

    void SyncNextRowVisibility()
    {
        bool on = steppedToggle != null && steppedToggle.isOn;
        if (nextRowRoot != null)
            nextRowRoot.SetActive(on);
        var wrapRect = transform as RectTransform;
        if (wrapRect != null)
        {
            float h = on ? (rowHStored * 2f + innerGapStored) : rowHStored;
            wrapRect.sizeDelta = new Vector2(dockWidthPx, h);
            dockHeightPx = h;
            ApplyLowerLeftDock();
        }
    }

    public void SetPhaseWaiting(int phase, bool waiting)
    {
        if (!waiting)
            BondFormationBreakPhaseUiLock.SetLocked(false);

        if (nextButton != null)
            nextButton.interactable = waiting && BondFormationDebugController.SteppedModeEnabled;
        if (nextPhaseLabel != null)
        {
            if (!waiting || !BondFormationDebugController.SteppedModeEnabled)
                nextPhaseLabel.text = "";
            else
            {
                nextPhaseLabel.text = phase switch
                {
                    1 => "1/3 template",
                    2 => "2/3 v0→guide",
                    3 => "3/3 rotate",
                    _ => ""
                };
            }
        }

        if (waiting)
            BondFormationBreakPhaseUiLock.SetLocked(true);

        AtomFunction.RefreshBondFormationInteractionAfterPhaseWaitChange();
    }
}

/// <summary>
/// During bond stepped-debug phase waits, disables every UI <see cref="Selectable"/> except the debug HUD (toggle + Next).
/// </summary>
static class BondFormationBreakPhaseUiLock
{
    static readonly Dictionary<Selectable, bool> SavedInteractable = new Dictionary<Selectable, bool>();
    static bool locked;

    public static void SetLocked(bool wantLocked)
    {
        if (wantLocked)
        {
            if (locked) return;
            var debugRoot = BondFormationDebugHud.Instance != null ? BondFormationDebugHud.Instance.transform : null;
            SavedInteractable.Clear();
            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (canvas == null) continue;
                foreach (var sel in canvas.GetComponentsInChildren<Selectable>(true))
                {
                    if (sel == null) continue;
                    if (debugRoot != null && IsUnder(debugRoot, sel.transform))
                        continue;
                    SavedInteractable[sel] = sel.interactable;
                    sel.interactable = false;
                }
            }
            locked = true;
            return;
        }

        if (!locked) return;
        foreach (var kv in SavedInteractable)
        {
            if (kv.Key != null)
                kv.Key.interactable = kv.Value;
        }
        SavedInteractable.Clear();
        locked = false;
    }

    static bool IsUnder(Transform debugHudRoot, Transform t)
    {
        return t == debugHudRoot || t.IsChildOf(debugHudRoot);
    }
}
