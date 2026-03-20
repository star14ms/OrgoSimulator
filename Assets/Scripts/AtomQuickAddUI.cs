using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;

/// <summary>
/// Builds the molecule construction toolbar: Row 1 (H, C, N, O, S, F, Cl, Br, I, More),
/// Row 2 (Cycloalkanes dropdown, Benzene, free-electron spawn). "More" opens the full periodic table.
/// </summary>
public class AtomQuickAddUI : MonoBehaviour
{
    [SerializeField] GameObject atomPrefab;
    [SerializeField] float viewportMargin = 0.1f;
    [SerializeField] float buttonSize = 36f;
    [SerializeField] float spacing = 4f;
    [SerializeField] int fontSize = 18;

    PeriodicTableUI periodicTable;
    EditModeManager editModeManager;
    MoleculeBuilder moleculeBuilder;
    Image eraserToggleImage;
    Image editToggleImage;
    static readonly Color EraserActiveColor = new Color(0.5f, 0.2f, 0.2f);
    static readonly Color EditActiveColor = new Color(0.4f, 0.7f, 1f);

    static readonly (int z, string label)[] Row1Elements = {
        (1, "H"), (6, "C"), (7, "N"), (8, "O"), (16, "S"), (9, "F"), (17, "Cl"), (35, "Br"), (53, "I")
    };

    public GameObject GetAtomPrefab() => atomPrefab;

    void Update()
    {
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
        }
    }

    void Start()
    {
        editModeManager = FindFirstObjectByType<EditModeManager>();
        if (editModeManager == null)
        {
            editModeManager = gameObject.AddComponent<EditModeManager>();
            editModeManager.SetAtomPrefab(atomPrefab);
        }

        moleculeBuilder = GetComponent<MoleculeBuilder>();
        if (moleculeBuilder == null)
        {
            moleculeBuilder = gameObject.AddComponent<MoleculeBuilder>();
            moleculeBuilder.SetAtomPrefab(atomPrefab);
        }

        periodicTable = GetComponent<PeriodicTableUI>();
        if (periodicTable == null)
            periodicTable = gameObject.AddComponent<PeriodicTableUI>();

        var hudCanvas = ResolveHudCanvas();
        UiScreenSpace.EnforceOverlay(hudCanvas);

        BuildToolbar();
        BuildDisposalZone();
        HideCreateAtomButton();
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
        rect.anchoredPosition = new Vector2(-20, 20);
        rect.sizeDelta = new Vector2(150, 100);

        var image = go.AddComponent<Image>();
        image.color = new Color(0.5f, 0.2f, 0.2f, 0.8f);

        var iconGo = new GameObject("TrashIcon");
        iconGo.transform.SetParent(go.transform, false);
        var iconRect = iconGo.AddComponent<RectTransform>();
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.sizeDelta = new Vector2(80, 80);

        var tmp = iconGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = "\u00D7"; // × (multiplication sign) as delete icon - works in standard fonts
        tmp.fontSize = 48;
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
        labelRect.sizeDelta = new Vector2(140, 24);

        var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
        labelTmp.font = AtomFunction.GetDefaultFont();
        labelTmp.text = "Trash";
        labelTmp.fontSize = 14;
        labelTmp.alignment = TextAlignmentOptions.Center;
        labelTmp.raycastTarget = false;
        labelTmp.color = new Color(1f, 1f, 1f, 0.9f);

        go.AddComponent<DisposalZone>();
    }

    void BuildToolbar()
    {
        var canvas = ResolveHudCanvas();
        if (canvas == null) return;

        var toolbar = new GameObject("MoleculeToolbar");
        toolbar.transform.SetParent(canvas.transform, false);

        var toolbarRect = toolbar.AddComponent<RectTransform>();
        toolbarRect.anchorMin = new Vector2(0, 1);
        toolbarRect.anchorMax = new Vector2(1, 1);
        toolbarRect.pivot = new Vector2(0.5f, 1);
        toolbarRect.anchoredPosition = new Vector2(0, 0);
        toolbarRect.sizeDelta = new Vector2(0, 100);

        var vLayout = toolbar.AddComponent<VerticalLayoutGroup>();
        vLayout.spacing = spacing;
        vLayout.padding = new RectOffset(10, 10, 10, 10);
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
    }

    void BuildTogglesPanelUpperRight(Canvas canvas)
    {
        var panel = new GameObject("TogglesPanel");
        panel.transform.SetParent(canvas.transform, false);

        var rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(1, 1);
        rect.anchorMax = new Vector2(1, 1);
        rect.pivot = new Vector2(1, 1);
        rect.anchoredPosition = new Vector2(-10, -10);
        rect.sizeDelta = new Vector2(400, buttonSize + 10);

        var hLayout = panel.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = spacing;
        hLayout.childAlignment = TextAnchor.MiddleRight;
        hLayout.childControlWidth = false;
        hLayout.childControlHeight = false;
        hLayout.childForceExpandWidth = false;
        hLayout.childForceExpandHeight = false;
        hLayout.padding = new RectOffset(0, 0, 0, 0);

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
        rect.sizeDelta = new Vector2(120, buttonSize);

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
        tmp.fontSize = fontSize - 2;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return go;
    }

    GameObject CreateRow1()
    {
        var row = new GameObject("Row1");
        var rect = row.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(800, buttonSize);

        var hLayout = row.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = spacing;
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
        rect.sizeDelta = new Vector2(520, buttonSize);

        var hLayout = row.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing = spacing;
        hLayout.childAlignment = TextAnchor.MiddleLeft;
        hLayout.childControlWidth = false;
        hLayout.childControlHeight = false;
        hLayout.childForceExpandWidth = false;
        hLayout.childForceExpandHeight = false;

        var cycloDropdown = CreateCycloalkanesDropdown();
        cycloDropdown.transform.SetParent(row.transform, false);

        var benzeneBtn = CreateBenzeneButton();
        benzeneBtn.transform.SetParent(row.transform, false);

        var electronTestBtn = CreateElectronTestButton();
        electronTestBtn.transform.SetParent(row.transform, false);

        return row;
    }

    GameObject cycloDropdownPanel;

    GameObject CreateCycloalkanesDropdown()
    {
        var container = new GameObject("CycloalkanesDropdown");
        var containerRect = container.AddComponent<RectTransform>();
        containerRect.sizeDelta = new Vector2(140, buttonSize);

        var mainBtn = new GameObject("CycloalkanesButton");
        mainBtn.transform.SetParent(container.transform, false);
        var mainRect = mainBtn.AddComponent<RectTransform>();
        mainRect.anchorMin = Vector2.zero;
        mainRect.anchorMax = new Vector2(1, 1);
        mainRect.offsetMin = Vector2.zero;
        mainRect.offsetMax = Vector2.zero;

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
        tmp.fontSize = fontSize - 2;
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
        panelRect.sizeDelta = new Vector2(0, buttonSize * 4 + spacing * 3);

        var panelImage = cycloDropdownPanel.AddComponent<Image>();
        panelImage.color = new Color(0.5f, 0.5f, 0.55f);

        var vLayout = cycloDropdownPanel.AddComponent<VerticalLayoutGroup>();
        vLayout.spacing = 2;
        vLayout.padding = new RectOffset(4, 4, 4, 4);
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
        if (cycloDropdownPanel != null)
            cycloDropdownPanel.SetActive(!cycloDropdownPanel.activeSelf);
    }

    GameObject CreateCycloalkaneDropdownItem(string label, int ringSize)
    {
        var go = new GameObject($"Item_{label}");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0, buttonSize - 4);

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
        tmp.fontSize = fontSize - 2;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        tmp.color = Color.white;

        return go;
    }

    GameObject CreateElementButton(int atomicNumber, string label)
    {
        var go = new GameObject($"Btn_{label}");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(buttonSize, buttonSize);

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
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return go;
    }

    GameObject CreateMoreButton()
    {
        var go = new GameObject("Btn_More");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(buttonSize * 1.5f, buttonSize);

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
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return go;
    }

    GameObject CreateBenzeneButton()
    {
        var go = new GameObject("Btn_Benzene");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(100, buttonSize);

        var image = go.AddComponent<Image>();
        image.color = new Color(0.97f, 0.82f, 0.45f);

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
        tmp.fontSize = fontSize - 2;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return go;
    }

    GameObject CreateElectronTestButton()
    {
        var go = new GameObject("Btn_ElectronTest");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(92, buttonSize);

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
        tmp.fontSize = fontSize - 2;
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
            moleculeBuilder.CreateCycloalkane(ringSize);
    }

    void OnBenzeneClicked()
    {
        if (moleculeBuilder != null)
            moleculeBuilder.CreateBenzene();
    }

    void ShowPeriodicTable()
    {
        if (periodicTable == null) return;
        periodicTable.SetAtomPrefab(atomPrefab);
        periodicTable.SetViewportMargin(viewportMargin);
        periodicTable.Show();
    }

    public void CreateAtomAtViewport(int atomicNumber)
    {
        if (atomPrefab == null || Camera.main == null) return;

        Vector3 pos = GetRandomPositionInView();
        var atomObj = Instantiate(atomPrefab, pos, Quaternion.identity);
        if (atomObj.TryGetComponent<AtomFunction>(out var atom))
        {
            atom.AtomicNumber = atomicNumber;
            if (editModeManager != null && editModeManager.HAutoMode)
                editModeManager.SaturateWithHydrogen(atom);
        }
    }

    Vector3 GetRandomPositionInView() => PlanarPointerInteraction.RandomWorldPointInMarginedViewport(viewportMargin);
}
