using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;

/// <summary>
/// Builds the molecule construction toolbar: Row 1 (H, C, N, O, S, F, Cl, Br, I, More),
/// Row 2 (FG ▼ [edit mode], Cycloalkanes dropdown, Benzene, free-electron spawn). "More" opens the full periodic table.
/// </summary>
public class AtomQuickAddUI : MonoBehaviour
{
    [Tooltip("Legacy single prefab: used when atomPrefab2D / atomPrefab3D are not set, and as fallback for swap.")]
    [SerializeField] GameObject atomPrefab;
    [Tooltip("Orthographic (2D) molecule: sprite atom + 2D orbital/electron chain.")]
    [SerializeField] GameObject atomPrefab2D;
    [Tooltip("Perspective (3D) molecule: mesh atom + 3D orbital/electron chain.")]
    [SerializeField] GameObject atomPrefab3D;
    [Tooltip("HUD “Main 3D” 2D/3D camera toggle. Off: control stays visible but does not respond (re-enable here when bringing the feature back).")]
    [SerializeField] bool enableMain3DCameraToggleButton = false;
    [SerializeField] float viewportMargin = 0.1f;
    [Tooltip("Design reference: H button side length in px. Actual H = min(screen)×hudHShortSideFraction; hudScale = actual H / this.")]
    [SerializeField] float buttonSize = 36f;
    [SerializeField] float spacing = 4f;
    [SerializeField] int fontSize = 18;
    [Tooltip("H (square element) button side = min(Screen.width, Screen.height) × this (default 1/16).")]
    [SerializeField] float hudHShortSideFraction = 1f / 16f;

    float hudScale = 1f;
    float layoutButtonSize;
    float layoutSpacing;
    int layoutFontSize;

    PeriodicTableUI periodicTable;
    EditModeManager editModeManager;
    MoleculeBuilder moleculeBuilder;
    Image eraserToggleImage;
    Image editToggleImage;
    Image view3DToggleImage;
    Canvas hudCanvas;
    RectTransform cycloDropdownButtonRect;
    RectTransform cycloDropdownPanelRect;
    RectTransform funcGroupDropdownButtonRect;
    RectTransform funcGroupDropdownPanelRect;
    Button funcGroupMainButton;
    GameObject funcGroupToolbarRoot;
    static readonly Color EraserActiveColor = new Color(0.5f, 0.2f, 0.2f);
    static readonly Color EditActiveColor = new Color(0.4f, 0.7f, 1f);
    static readonly Color View3DActiveColor = new Color(0.65f, 0.55f, 0.95f);

    static readonly (int z, string label)[] Row1Elements = {
        (1, "H"), (6, "C"), (7, "N"), (8, "O"), (16, "S"), (9, "F"), (17, "Cl"), (35, "Br"), (53, "I")
    };

    public GameObject GetAtomPrefab() => ResolveAtomPrefabForCurrentView();

    /// <summary>Atom prefab for the current camera mode (orthographic → 2D, perspective → 3D).</summary>
    public GameObject ResolveAtomPrefabForCurrentView()
    {
        EnsurePrefabPairFromLegacy();
        bool want3D = Camera.main != null && !Camera.main.orthographic;
        GameObject p = want3D ? atomPrefab3D : atomPrefab2D;
        return p != null ? p : atomPrefab;
    }

    void EnsurePrefabPairFromLegacy()
    {
        if (atomPrefab2D == null) atomPrefab2D = atomPrefab;
        if (atomPrefab3D == null) atomPrefab3D = atomPrefab;
    }

    /// <summary>Updates EditMode / MoleculeBuilder / periodic table atom prefab; rebuilds scene atoms if 2D↔3D visuals mismatch.</summary>
    public void OnCameraViewModeChanged()
    {
        EnsurePrefabPairFromLegacy();
        GameObject active = ResolveAtomPrefabForCurrentView();
        if (editModeManager != null)
            editModeManager.SetAtomPrefab(active);
        if (moleculeBuilder != null)
            moleculeBuilder.SetAtomPrefab(active);
        MoleculeViewPrefabSwap.RebuildAllAtomsIfVariantMismatch(atomPrefab2D, atomPrefab3D, atomPrefab);
    }

    void RefreshHudLayoutMetrics()
    {
        float shortSide = Mathf.Min(Screen.width, Screen.height);
        float frac = Mathf.Clamp(hudHShortSideFraction, 1f / 64f, 1f / 4f);
        layoutButtonSize = shortSide * frac;
        hudScale = layoutButtonSize / Mathf.Max(1e-6f, buttonSize);
        layoutSpacing = spacing * hudScale;
        layoutFontSize = Mathf.Max(10, Mathf.RoundToInt(fontSize * hudScale));
    }

    float Px(float designPixels) => designPixels * hudScale;

    int PxI(float designPixels) => Mathf.Max(0, Mathf.RoundToInt(designPixels * hudScale));

    /// <summary>HUD layout pixels scaled like toolbar (same as internal <see cref="Px"/>).</summary>
    public float HudPx(float designPixels) => Px(designPixels);

    void Update()
    {
        if (BondFormationDebugController.IsWaitingForPhase)
            return;

        var k = Keyboard.current;
        if (k == null) return;
        if (k.hKey.wasPressedThisFrame) OnElementClicked(1);
        else if (k.cKey.wasPressedThisFrame) OnElementClicked(6);
        else if (k.nKey.wasPressedThisFrame) OnElementClicked(7);
        else if (k.oKey.wasPressedThisFrame) OnElementClicked(8);
        else if (k.fKey.wasPressedThisFrame) OnElementClicked(9);
        else if (k.sKey.wasPressedThisFrame) OnElementClicked(16);
        else if (k.iKey.wasPressedThisFrame) OnElementClicked(53);

        if (editModeManager != null)
        {
            if (eraserToggleImage != null)
                eraserToggleImage.color = editModeManager.EraserMode ? EraserActiveColor : new Color(0.6f, 0.6f, 0.6f);
            if (editToggleImage != null)
                editToggleImage.color = editModeManager.EditModeActive ? EditActiveColor : new Color(0.6f, 0.6f, 0.6f);
            bool fgReady = editModeManager.FunctionalGroupAttachmentReady();
            if (funcGroupMainButton != null)
                funcGroupMainButton.interactable = fgReady;
            if (funcGroupDropdownPanel != null && funcGroupDropdownPanel.activeSelf
                && (editModeManager == null || !fgReady))
                funcGroupDropdownPanel.SetActive(false);
        }
        if (view3DToggleImage != null && Camera.main != null && enableMain3DCameraToggleButton)
        {
            bool p3 = !Camera.main.orthographic;
            view3DToggleImage.color = p3 ? View3DActiveColor : new Color(0.6f, 0.6f, 0.6f);
        }
    }

    void LateUpdate()
    {
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

        Camera uiCam = hudCanvas != null && hudCanvas.renderMode == RenderMode.ScreenSpaceCamera ? hudCanvas.worldCamera : null;
        Vector2 sp = mouse.position.ReadValue();

        void CloseIfOutside(GameObject panel, RectTransform btnRt, RectTransform panelRt)
        {
            if (panel == null || !panel.activeSelf) return;
            bool overBtn = btnRt != null && RectTransformUtility.RectangleContainsScreenPoint(btnRt, sp, uiCam);
            bool overPanel = panelRt != null && RectTransformUtility.RectangleContainsScreenPoint(panelRt, sp, uiCam);
            if (!overBtn && !overPanel)
                panel.SetActive(false);
        }

        CloseIfOutside(cycloDropdownPanel, cycloDropdownButtonRect, cycloDropdownPanelRect);
        CloseIfOutside(funcGroupDropdownPanel, funcGroupDropdownButtonRect, funcGroupDropdownPanelRect);
    }

    void Start()
    {
        EnsurePrefabPairFromLegacy();

        editModeManager = FindFirstObjectByType<EditModeManager>();
        if (editModeManager == null)
            editModeManager = gameObject.AddComponent<EditModeManager>();

        moleculeBuilder = GetComponent<MoleculeBuilder>();
        if (moleculeBuilder == null)
            moleculeBuilder = gameObject.AddComponent<MoleculeBuilder>();

        periodicTable = GetComponent<PeriodicTableUI>();
        if (periodicTable == null)
            periodicTable = gameObject.AddComponent<PeriodicTableUI>();

        RefreshHudLayoutMetrics();
        periodicTable.SetHudLayoutScale(hudScale);
        EnsureCameraViewModeToggle();

        var hudCanvas = ResolveHudCanvas();
        UiScreenSpace.EnforceOverlay(hudCanvas);

        BuildToolbar();
        BuildDisposalZone();
        HideCreateAtomButton();

        if (GetComponent<WorkPlaneDistanceScrollbar>() == null)
            gameObject.AddComponent<WorkPlaneDistanceScrollbar>();

        if (FindFirstObjectByType<WorldOriginMarker>() == null)
        {
            var originGo = new GameObject("WorldOriginMarker");
            originGo.transform.position = Vector3.zero;
            originGo.AddComponent<WorldOriginMarker>();
        }

        OnCameraViewModeChanged();
    }

    void HideCreateAtomButton()
    {
        var buttons = FindObjectsByType<ButtonCreateAtom>(FindObjectsSortMode.None);
        foreach (var btn in buttons)
            btn.gameObject.SetActive(false);
    }

    Canvas ResolveHudCanvas()
    {
        var canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            canvas = GetComponentInChildren<Canvas>();
        if (canvas == null)
            canvas = FindFirstObjectByType<Canvas>();
        return canvas;
    }

    void EnsureCameraViewModeToggle()
    {
        var cam = Camera.main;
        if (cam == null) return;
        if (cam.GetComponent<CameraViewModeToggle>() == null)
            cam.gameObject.AddComponent<CameraViewModeToggle>();
    }

    void BuildDisposalZone()
    {
        var canvas = ResolveHudCanvas();
        if (canvas == null) return;

        var go = new GameObject("DisposalZone");
        go.transform.SetParent(canvas.transform, false);

        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(1, 0);
        rect.anchorMax = new Vector2(1, 0);
        rect.pivot = new Vector2(1, 0);
        float inset = Px(20f);
        rect.anchoredPosition = new Vector2(-inset, inset);
        rect.sizeDelta = new Vector2(Px(150f), Px(100f));

        var image = go.AddComponent<Image>();
        image.color = new Color(0.5f, 0.2f, 0.2f, 0.8f);

        var iconGo = new GameObject("TrashIcon");
        iconGo.transform.SetParent(go.transform, false);
        var iconRect = iconGo.AddComponent<RectTransform>();
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        float iconSide = Px(80f);
        iconRect.sizeDelta = new Vector2(iconSide, iconSide);

        var tmp = iconGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = "\u00D7"; // × (multiplication sign) as delete icon - works in standard fonts
        tmp.fontSize = PxI(48f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        tmp.color = new Color(1f, 1f, 1f, 0.9f);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0.5f, 0.1f);
        labelRect.anchorMax = new Vector2(0.5f, 0.35f);
        labelRect.pivot = new Vector2(0.5f, 0.5f);
        labelRect.anchoredPosition = Vector2.zero;
        labelRect.sizeDelta = new Vector2(Px(140f), Px(24f));

        var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
        labelTmp.font = AtomFunction.GetDefaultFont();
        labelTmp.text = "Trash";
        labelTmp.fontSize = Mathf.Max(8, PxI(14f));
        labelTmp.alignment = TextAlignmentOptions.Center;
        labelTmp.raycastTarget = false;
        labelTmp.color = new Color(1f, 1f, 1f, 0.9f);

        go.AddComponent<DisposalZone>();

        BuildClearAllButton(canvas);
    }

    void BuildClearAllButton(Canvas canvas)
    {
        if (canvas == null || editModeManager == null) return;

        float inset = Px(20f);
        float trashWidth = Px(150f);
        float gap = Px(8f);
        // Bottom-right stack: Clear all sits just left of DisposalZone (same vertical baseline as trash).
        CreateClearAllButton(canvas, "ClearAll",
            new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-inset - trashWidth - gap, inset),
            new Vector2(Px(88f), Px(36f)));
    }

    void CreateClearAllButton(Canvas canvas, string objectName, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition, Vector2 sizeDelta)
    {
        var go = new GameObject(objectName);
        go.transform.SetParent(canvas.transform, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;

        var image = go.AddComponent<Image>();
        image.color = new Color(0.22f, 0.26f, 0.34f, 0.94f);

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = Color.Lerp(image.color, Color.white, 0.18f);
        colors.pressedColor = Color.Lerp(image.color, new Color(0.9f, 0.55f, 0.45f), 0.35f);
        btn.colors = colors;
        btn.onClick.AddListener(() => editModeManager.ClearAllMolecules());

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = "Clear all";
        tmp.fontSize = Mathf.Max(12, layoutFontSize - 5);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        tmp.color = new Color(0.95f, 0.95f, 0.98f, 1f);
    }

    void BuildToolbar()
    {
        var canvas = ResolveHudCanvas();
        if (canvas == null) return;
        hudCanvas = canvas;

        var toolbar = new GameObject("MoleculeToolbar");
        toolbar.transform.SetParent(canvas.transform, false);

        var toolbarRect = toolbar.AddComponent<RectTransform>();
        toolbarRect.anchorMin = new Vector2(0, 1);
        toolbarRect.anchorMax = new Vector2(1, 1);
        toolbarRect.pivot = new Vector2(0.5f, 1);
        toolbarRect.anchoredPosition = new Vector2(0, 0);
        toolbarRect.sizeDelta = new Vector2(0, Px(100f));

        var vLayout = toolbar.AddComponent<VerticalLayoutGroup>();
        vLayout.spacing = layoutSpacing;
        int pad = PxI(10f);
        vLayout.padding = new RectOffset(pad, pad, pad, pad);
        vLayout.childAlignment = TextAnchor.UpperLeft;
        vLayout.childControlWidth = true;
        vLayout.childControlHeight = true;
        vLayout.childForceExpandWidth = false;
        vLayout.childForceExpandHeight = false;

        var row1 = CreateRow1();
        row1.transform.SetParent(toolbar.transform, false);

        var row2 = CreateRow2();
        row2.transform.SetParent(toolbar.transform, false);

        BuildTogglesPanelUpperRight(canvas);
        BondFormationDebugHud.Build(canvas, layoutButtonSize, layoutFontSize, Px);
        BuildChargeModeToggleLowerLeft(canvas);
    }

    void BuildChargeModeToggleLowerLeft(Canvas canvas)
    {
        var wrap = new GameObject("ChargeModePanel");
        wrap.transform.SetParent(canvas.transform, false);
        var wrapRect = wrap.AddComponent<RectTransform>();
        wrapRect.anchorMin = new Vector2(0, 0);
        wrapRect.anchorMax = new Vector2(0, 0);
        wrapRect.pivot = new Vector2(0, 0);
        wrapRect.anchoredPosition = new Vector2(Px(10f), Px(10f));
        wrapRect.sizeDelta = new Vector2(Px(178f), layoutButtonSize);

        var activeAtomicCharge = new Color(0.42f, 0.72f, 0.52f);
        var activeOx = new Color(0.75f, 0.62f, 0.40f);

        var go = new GameObject("ChargeModeToggle");
        go.transform.SetParent(wrap.transform, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var image = go.AddComponent<Image>();
        var btn = go.AddComponent<Button>();

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.fontSize = Mathf.Max(8, layoutFontSize - 2);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        tmp.color = new Color(0.95f, 0.95f, 0.98f, 1f);

        void ApplyVisuals()
        {
            bool oxidation = AtomFunction.ChargeDisplayMode == AtomChargeDisplayMode.OxidationState;
            image.color = oxidation ? activeOx : activeAtomicCharge;
            tmp.text = oxidation ? "Oxidation State" : "Atomic charge";
        }

        ApplyVisuals();
        btn.onClick.AddListener(() =>
        {
            AtomFunction.ChargeDisplayMode = AtomFunction.ChargeDisplayMode == AtomChargeDisplayMode.OxidationState
                ? AtomChargeDisplayMode.OctetFormal
                : AtomChargeDisplayMode.OxidationState;
            ApplyVisuals();
            AtomFunction.RefreshAllDisplayedCharges();
        });
    }

    void BuildTogglesPanelUpperRight(Canvas canvas)
    {
        var panel = new GameObject("TogglesPanel");
        panel.transform.SetParent(canvas.transform, false);

        var rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(1, 1);
        rect.anchorMax = new Vector2(1, 1);
        rect.pivot = new Vector2(1, 1);
        rect.anchoredPosition = new Vector2(-Px(10f), -Px(10f));
        rect.sizeDelta = new Vector2(Px(520f), layoutButtonSize + Px(10f));

        var hLayout = panel.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = layoutSpacing;
        hLayout.childAlignment = TextAnchor.MiddleRight;
        hLayout.childControlWidth = false;
        hLayout.childControlHeight = false;
        hLayout.childForceExpandWidth = false;
        hLayout.childForceExpandHeight = false;
        hLayout.padding = new RectOffset(0, 0, 0, 0);

        // Off = SampleScene-style 2D; on = Main3D-style camera (see CameraViewModeToggle).
        var viewToggle = CreateToggle("Main 3D", Camera.main != null && !Camera.main.orthographic, on =>
        {
            var vt = Camera.main != null ? Camera.main.GetComponent<CameraViewModeToggle>() : null;
            if (vt != null) vt.SetPerspective3D(on);
        }, View3DActiveColor);
        viewToggle.transform.SetParent(panel.transform, false);
        view3DToggleImage = viewToggle.GetComponent<Image>();
        if (!enableMain3DCameraToggleButton)
        {
            if (viewToggle.TryGetComponent<Button>(out var viewModeBtn))
                viewModeBtn.interactable = false;
            var cg = viewToggle.GetComponent<CanvasGroup>();
            if (cg == null) cg = viewToggle.AddComponent<CanvasGroup>();
            cg.alpha = 0f;
            cg.interactable = false;
            cg.blocksRaycasts = false;
        }

        var hAutoToggle = CreateToggle("H-auto", editModeManager != null && editModeManager.HAutoMode, on =>
        {
            if (editModeManager != null) editModeManager.SetHAutoMode(on);
        });
        hAutoToggle.transform.SetParent(panel.transform, false);

        var editToggle = CreateToggle("Edit (E)", editModeManager != null && editModeManager.EditModeActive, on =>
        {
            if (editModeManager != null) editModeManager.SetEditMode(on);
        }, EditActiveColor);
        editToggle.transform.SetParent(panel.transform, false);
        editToggleImage = editToggle.GetComponent<Image>();

        var eraserToggle = CreateToggle("Eraser (D)", editModeManager != null && editModeManager.EraserMode, on =>
        {
            if (editModeManager != null) editModeManager.SetEraserMode(on);
        }, EraserActiveColor);
        eraserToggle.transform.SetParent(panel.transform, false);
        eraserToggleImage = eraserToggle.GetComponent<Image>();
    }

    GameObject CreateToggle(string label, bool isOn, System.Action<bool> onValueChanged, Color? activeColor = null)
    {
        var active = activeColor ?? new Color(0.5f, 0.8f, 0.5f);
        var inactive = new Color(0.6f, 0.6f, 0.6f);

        var go = new GameObject($"Toggle_{label}");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(Px(120f), layoutButtonSize);

        var image = go.AddComponent<Image>();
        image.color = isOn ? active : inactive;

        var btn = go.AddComponent<Button>();
        bool state = isOn;
        btn.onClick.AddListener(() =>
        {
            state = !state;
            image.color = state ? active : inactive;
            onValueChanged?.Invoke(state);
        });

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = label;
        tmp.fontSize = Mathf.Max(8, layoutFontSize - 2);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return go;
    }

    GameObject CreateRow1()
    {
        var row = new GameObject("Row1");
        var rect = row.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(Px(800f), layoutButtonSize);

        var hLayout = row.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = layoutSpacing;
        hLayout.childAlignment = TextAnchor.MiddleLeft;
        hLayout.childControlWidth = false;
        hLayout.childControlHeight = false;
        hLayout.childForceExpandWidth = false;
        hLayout.childForceExpandHeight = false;

        foreach (var (z, label) in Row1Elements)
        {
            var btn = CreateElementButton(z, label);
            btn.transform.SetParent(row.transform, false);
        }

        var moreBtn = CreateMoreButton();
        moreBtn.transform.SetParent(row.transform, false);

        return row;
    }

    GameObject CreateRow2()
    {
        var row = new GameObject("Row2");
        var rect = row.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(Px(780f), layoutButtonSize);

        var hLayout = row.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = layoutSpacing;
        hLayout.childAlignment = TextAnchor.MiddleLeft;
        hLayout.childControlWidth = false;
        hLayout.childControlHeight = false;
        hLayout.childForceExpandWidth = false;
        hLayout.childForceExpandHeight = false;

        var fgDropdown = CreateFunctionalGroupDropdown();
        fgDropdown.transform.SetParent(row.transform, false);

        var cycloDropdown = CreateCycloalkanesDropdown();
        cycloDropdown.transform.SetParent(row.transform, false);

        var benzeneBtn = CreateBenzeneButton();
        benzeneBtn.transform.SetParent(row.transform, false);

        var electronTestBtn = CreateElectronTestButton();
        electronTestBtn.transform.SetParent(row.transform, false);

        return row;
    }

    GameObject cycloDropdownPanel;
    GameObject funcGroupDropdownPanel;

    GameObject CreateFunctionalGroupDropdown()
    {
        var container = new GameObject("FunctionalGroupDropdown");
        funcGroupToolbarRoot = container;
        var containerRect = container.AddComponent<RectTransform>();
        containerRect.sizeDelta = new Vector2(Px(118f), layoutButtonSize);

        var mainBtnGo = new GameObject("FunctionalGroupButton");
        mainBtnGo.transform.SetParent(container.transform, false);
        var mainRect = mainBtnGo.AddComponent<RectTransform>();
        mainRect.anchorMin = Vector2.zero;
        mainRect.anchorMax = new Vector2(1, 1);
        mainRect.offsetMin = Vector2.zero;
        mainRect.offsetMax = Vector2.zero;
        funcGroupDropdownButtonRect = mainRect;

        var mainImage = mainBtnGo.AddComponent<Image>();
        mainImage.color = new Color(0.35f, 0.48f, 0.42f);

        funcGroupMainButton = mainBtnGo.AddComponent<Button>();
        funcGroupMainButton.interactable = editModeManager != null && editModeManager.FunctionalGroupAttachmentReady();
        funcGroupMainButton.onClick.AddListener(ToggleFunctionalGroupDropdown);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(mainBtnGo.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = "Func. grp. \u25BC";
        tmp.fontSize = Mathf.Max(7, layoutFontSize - 2);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.raycastTarget = false;
        tmp.color = Color.white;

        funcGroupDropdownPanel = new GameObject("FunctionalGroupPanel");
        funcGroupDropdownPanel.transform.SetParent(container.transform, false);
        var panelRect = funcGroupDropdownPanel.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0, 0);
        panelRect.anchorMax = new Vector2(1, 0);
        panelRect.pivot = new Vector2(0.5f, 1f);
        panelRect.anchoredPosition = Vector2.zero;
        const int nItems = 9;
        panelRect.sizeDelta = new Vector2(0, layoutButtonSize * nItems + layoutSpacing * (nItems - 1) + Px(8f));
        funcGroupDropdownPanelRect = panelRect;

        var panelImage = funcGroupDropdownPanel.AddComponent<Image>();
        panelImage.color = new Color(0.42f, 0.5f, 0.46f);

        var vLayout = funcGroupDropdownPanel.AddComponent<VerticalLayoutGroup>();
        vLayout.spacing = Mathf.Max(1, PxI(2f));
        int dPad = PxI(4f);
        vLayout.padding = new RectOffset(dPad, dPad, dPad, dPad);
        vLayout.childAlignment = TextAnchor.UpperCenter;
        vLayout.childControlWidth = true;
        vLayout.childControlHeight = false;
        vLayout.childForceExpandWidth = true;
        vLayout.childForceExpandHeight = false;

        (FunctionalGroupKind kind, string label)[] items =
        {
            (FunctionalGroupKind.AmineNH2, "NH2"),
            (FunctionalGroupKind.Hydroxyl, "OH"),
            (FunctionalGroupKind.Methoxy, "OCH3"),
            (FunctionalGroupKind.Aldehyde, "C(=O)-H"),
            (FunctionalGroupKind.Carboxyl, "C(=O)-OH"),
            (FunctionalGroupKind.Sulfo, "SO3H"),
            (FunctionalGroupKind.Nitrile, "C\u2261N"),
            (FunctionalGroupKind.Nitro, "NO2H"),
            (FunctionalGroupKind.PhosphateDihydrogen, "PO3H2")
        };

        foreach (var (kind, label) in items)
        {
            var itemBtn = CreateFunctionalGroupDropdownItem(label, kind);
            itemBtn.transform.SetParent(funcGroupDropdownPanel.transform, false);
        }

        funcGroupDropdownPanel.SetActive(false);
        return container;
    }

    void ToggleFunctionalGroupDropdown()
    {
        if (editModeManager == null || !editModeManager.FunctionalGroupAttachmentReady()) return;
        if (cycloDropdownPanel != null)
            cycloDropdownPanel.SetActive(false);
        if (funcGroupDropdownPanel != null)
            funcGroupDropdownPanel.SetActive(!funcGroupDropdownPanel.activeSelf);
    }

    GameObject CreateFunctionalGroupDropdownItem(string label, FunctionalGroupKind kind)
    {
        var go = new GameObject($"FG_{kind}");
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0, layoutButtonSize - Px(4f));

        var image = go.AddComponent<Image>();
        image.color = new Color(0.5f, 0.58f, 0.52f);

        var btn = go.AddComponent<Button>();
        var k = kind;
        btn.onClick.AddListener(() =>
        {
            OnFunctionalGroupClicked(k);
            if (funcGroupDropdownPanel != null)
                funcGroupDropdownPanel.SetActive(false);
        });

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = label;
        tmp.fontSize = Mathf.Max(7, layoutFontSize - 3);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.raycastTarget = false;
        tmp.color = Color.white;

        return go;
    }

    void OnFunctionalGroupClicked(FunctionalGroupKind kind)
    {
        if (editModeManager == null || !editModeManager.FunctionalGroupAttachmentReady()) return;
        editModeManager.TryAttachFunctionalGroup(kind);
    }

    GameObject CreateCycloalkanesDropdown()
    {
        var container = new GameObject("CycloalkanesDropdown");
        var containerRect = container.AddComponent<RectTransform>();
        containerRect.sizeDelta = new Vector2(Px(140f), layoutButtonSize);

        var mainBtn = new GameObject("CycloalkanesButton");
        mainBtn.transform.SetParent(container.transform, false);
        var mainRect = mainBtn.AddComponent<RectTransform>();
        mainRect.anchorMin = Vector2.zero;
        mainRect.anchorMax = new Vector2(1, 1);
        mainRect.offsetMin = Vector2.zero;
        mainRect.offsetMax = Vector2.zero;
        cycloDropdownButtonRect = mainRect;

        var image = mainBtn.AddComponent<Image>();
        image.color = new Color(0.45f, 0.45f, 0.5f);

        var btn = mainBtn.AddComponent<Button>();
        btn.onClick.AddListener(ToggleCycloalkanesDropdown);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(mainBtn.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = "Cycloalkanes \u25BC";
        tmp.fontSize = Mathf.Max(8, layoutFontSize - 2);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        tmp.color = Color.white;

        cycloDropdownPanel = new GameObject("DropdownPanel");
        cycloDropdownPanel.transform.SetParent(container.transform, false);
        var panelRect = cycloDropdownPanel.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0, 0);
        panelRect.anchorMax = new Vector2(1, 0);
        panelRect.pivot = new Vector2(0.5f, 1);
        panelRect.anchoredPosition = new Vector2(0, 0); // Top of panel at bottom of button, no gap
        panelRect.sizeDelta = new Vector2(0, layoutButtonSize * 4f + layoutSpacing * 3f);
        cycloDropdownPanelRect = panelRect;

        var panelImage = cycloDropdownPanel.AddComponent<Image>();
        panelImage.color = new Color(0.5f, 0.5f, 0.55f);

        var vLayout = cycloDropdownPanel.AddComponent<VerticalLayoutGroup>();
        vLayout.spacing = Mathf.Max(1, PxI(2f));
        int dPad = PxI(4f);
        vLayout.padding = new RectOffset(dPad, dPad, dPad, dPad);
        vLayout.childAlignment = TextAnchor.UpperCenter;
        vLayout.childControlWidth = true;
        vLayout.childControlHeight = false;
        vLayout.childForceExpandWidth = true;
        vLayout.childForceExpandHeight = false;

        string[] names = { "Cyclopropane", "Cyclobutane", "Cyclopentane", "Cyclohexane" };
        for (int n = 3; n <= 6; n++)
        {
            var itemBtn = CreateCycloalkaneDropdownItem(names[n - 3], n);
            itemBtn.transform.SetParent(cycloDropdownPanel.transform, false);
        }

        cycloDropdownPanel.SetActive(false);

        return container;
    }

    void ToggleCycloalkanesDropdown()
    {
        if (funcGroupDropdownPanel != null)
            funcGroupDropdownPanel.SetActive(false);
        if (cycloDropdownPanel != null)
            cycloDropdownPanel.SetActive(!cycloDropdownPanel.activeSelf);
    }

    GameObject CreateCycloalkaneDropdownItem(string label, int ringSize)
    {
        var go = new GameObject($"Item_{label}");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0, layoutButtonSize - Px(4f));

        var image = go.AddComponent<Image>();
        image.color = new Color(0.55f, 0.55f, 0.6f);

        var btn = go.AddComponent<Button>();
        int n = ringSize;
        btn.onClick.AddListener(() =>
        {
            OnCycloalkaneClicked(n);
            if (cycloDropdownPanel != null)
                cycloDropdownPanel.SetActive(false);
        });

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = label;
        tmp.fontSize = Mathf.Max(8, layoutFontSize - 2);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        tmp.color = Color.white;

        return go;
    }

    GameObject CreateElementButton(int atomicNumber, string label)
    {
        var go = new GameObject($"Btn_{label}");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(layoutButtonSize, layoutButtonSize);

        var image = go.AddComponent<Image>();
        image.color = PeriodicTableUI.GetElementColorStatic(atomicNumber);

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = Color.Lerp(image.color, Color.white, 0.2f);
        colors.pressedColor = Color.Lerp(image.color, Color.black, 0.1f);
        btn.colors = colors;

        int z = atomicNumber;
        btn.onClick.AddListener(() => OnElementClicked(z));

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = label;
        tmp.fontSize = layoutFontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return go;
    }

    GameObject CreateMoreButton()
    {
        var go = new GameObject("Btn_More");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(layoutButtonSize * 1.5f, layoutButtonSize);

        var image = go.AddComponent<Image>();
        image.color = new Color(0.6f, 0.6f, 0.7f);

        var btn = go.AddComponent<Button>();
        btn.onClick.AddListener(ShowPeriodicTable);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = "More";
        tmp.fontSize = layoutFontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return go;
    }

    GameObject CreateBenzeneButton()
    {
        var go = new GameObject("Btn_Benzene");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(Px(100f), layoutButtonSize);

        var image = go.AddComponent<Image>();
        image.color = new Color(0.45f, 0.45f, 0.5f);

        var btn = go.AddComponent<Button>();
        btn.onClick.AddListener(OnBenzeneClicked);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = "Benzene";
        tmp.fontSize = Mathf.Max(8, layoutFontSize - 2);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        tmp.color = Color.white;

        return go;
    }

    GameObject CreateElectronTestButton()
    {
        var go = new GameObject("Btn_ElectronTest");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(layoutButtonSize, layoutButtonSize);

        var image = go.AddComponent<Image>();
        image.color = new Color(0.35f, 0.42f, 0.55f);

        var btn = go.AddComponent<Button>();
        btn.onClick.AddListener(OnElectronTestClicked);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        // LiberationSans SDF has no U+207B (superscript minus); use rich-text sup + ASCII hyphen.
        tmp.richText = true;
        tmp.text = "e<sup>-</sup>";
        tmp.fontSize = Mathf.Max(8, layoutFontSize - 2);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return go;
    }

    void OnElectronTestClicked()
    {
        if (moleculeBuilder != null)
            moleculeBuilder.CreateFreeElectronAtViewport();
    }

    void OnElementClicked(int atomicNumber)
    {
        if (editModeManager != null && editModeManager.EditModeActive && editModeManager.SelectedAtom != null)
        {
            if (editModeManager.TryAddAtomToSelected(atomicNumber))
                return;
        }
        CreateAtomAtViewport(atomicNumber);
    }

    void OnCycloalkaneClicked(int ringSize)
    {
        if (moleculeBuilder != null)
        {
            var k = Keyboard.current;
            bool attachToSelection = k != null && (k.leftShiftKey.isPressed || k.rightShiftKey.isPressed);
            moleculeBuilder.CreateCycloalkane(ringSize, attachToSelectedOrbital: attachToSelection);
        }
    }

    void OnBenzeneClicked()
    {
        if (moleculeBuilder != null)
            moleculeBuilder.CreateBenzene();
    }

    void ShowPeriodicTable()
    {
        if (periodicTable == null) return;
        periodicTable.SetAtomPrefab(GetAtomPrefab());
        periodicTable.SetViewportMargin(viewportMargin);
        periodicTable.Show();
    }

    public void CreateAtomAtViewport(int atomicNumber)
    {
        GameObject prefab = GetAtomPrefab();
        if (prefab == null || Camera.main == null) return;

        Vector3 pos = PlanarPointerInteraction.SnapWorldToWorkPlaneIfPresent(GetRandomPositionInView());
        var atomObj = Instantiate(prefab, pos, Quaternion.identity);
        if (atomObj.TryGetComponent<AtomFunction>(out var atom))
        {
            atom.AtomicNumber = atomicNumber;
            atom.ForceInitialize();
            if (editModeManager != null && editModeManager.HAutoMode)
                editModeManager.SaturateWithHydrogen(atom);
            if (editModeManager != null)
                editModeManager.OnAtomClicked(atom);
        }
    }

    Vector3 GetRandomPositionInView() => PlanarPointerInteraction.RandomWorldPointInMarginedViewport(viewportMargin);
}
