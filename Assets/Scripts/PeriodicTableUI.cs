using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Shows a periodic table panel with 118 element buttons. Created temporarily when user clicks Create Atom.
/// </summary>
public class PeriodicTableUI : MonoBehaviour
{
    GameObject atomPrefab;
    float viewportMargin = 0.1f;

    public void SetAtomPrefab(GameObject prefab) => atomPrefab = prefab;
    public void SetViewportMargin(float margin) => viewportMargin = margin;
    [SerializeField] float cellSize = 40f;
    [SerializeField] float spacing = 5f;
    [SerializeField] float blockPadding = 10f;
    [SerializeField] int fontSize = 20;

    float hudLayoutScale = 1f;
    float layoutCellSize;
    float layoutSpacing;
    int layoutBlockPadding;
    int layoutFontSize;

    /// <summary>Match toolbar scaling from AtomQuickAddUI (1 = design resolution).</summary>
    public void SetHudLayoutScale(float scale) =>
        hudLayoutScale = Mathf.Clamp(scale, 0.5f, 3f);

    void RefreshTableLayoutMetrics()
    {
        layoutCellSize = cellSize * hudLayoutScale;
        layoutSpacing = spacing * hudLayoutScale;
        layoutBlockPadding = Mathf.Max(4, Mathf.RoundToInt(blockPadding * hudLayoutScale));
        layoutFontSize = Mathf.Max(8, Mathf.RoundToInt(fontSize * hudLayoutScale));
    }

    GameObject panel;
    GameObject content;
    const int Cols = 18;

    public void Show()
    {
        if (panel != null) return;
        CreatePanel();
    }

    public void Hide()
    {
        if (panel != null)
        {
            Destroy(panel);
            panel = null;
        }
    }

    public bool IsVisible => panel != null;

    void CreatePanel()
    {
        var canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) return;
        UiScreenSpace.EnforceOverlay(canvas);

        RefreshTableLayoutMetrics();

        panel = new GameObject("PeriodicTablePanel");
        panel.transform.SetParent(canvas.transform, false);

        var rect = panel.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var image = panel.AddComponent<Image>();
        image.color = new Color(0, 0, 0, 0.6f);
        var backdropBtn = panel.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(Hide);

        var scrollGo = new GameObject("ScrollView");
        scrollGo.transform.SetParent(panel.transform, false);
        var scrollRect = scrollGo.AddComponent<RectTransform>();
        scrollRect.anchorMin = new Vector2(0.05f, 0.05f);
        scrollRect.anchorMax = new Vector2(0.95f, 0.95f);
        scrollRect.offsetMin = Vector2.zero;
        scrollRect.offsetMax = Vector2.zero;

        var scrollView = scrollGo.AddComponent<ScrollRect>();
        scrollView.horizontal = false;
        scrollView.vertical = true;

        var viewport = new GameObject("Viewport");
        viewport.transform.SetParent(scrollGo.transform, false);
        var viewportRect = viewport.AddComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = Vector2.zero;
        var viewportMask = viewport.AddComponent<Mask>();
        viewportMask.showMaskGraphic = false;
        var viewportImage = viewport.AddComponent<Image>();
        viewportImage.color = Color.white;
        scrollView.viewport = viewportRect;

        content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        var contentRect = content.AddComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0, 1);
        contentRect.anchorMax = new Vector2(1, 1);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;
        scrollView.content = contentRect;

        var grid = content.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(layoutCellSize, layoutCellSize);
        grid.spacing = new Vector2(layoutSpacing, layoutSpacing);
        int pad = layoutBlockPadding;
        grid.padding = new RectOffset(pad, pad, pad, pad);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = Cols;
        grid.childAlignment = TextAnchor.UpperLeft;

        var contentSizeFitter = content.AddComponent<ContentSizeFitter>();
        contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        var gridCells = BuildPeriodicTableGrid();
        foreach (var cell in gridCells)
        {
            cell.transform.SetParent(content.transform, false);
        }
    }

    GameObject[] BuildPeriodicTableGrid()
    {
        var result = new System.Collections.Generic.List<GameObject>();

        void AddEmpty()
        {
            var spacer = new GameObject("Spacer");
            var rect = spacer.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(layoutCellSize, layoutCellSize);
            var img = spacer.AddComponent<Image>();
            img.color = Color.clear;
            img.raycastTarget = true;
            var btn = spacer.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(Hide);
            result.Add(spacer);
        }

        for (int row = 0; row < 10; row++)
        {
            for (int col = 0; col < Cols; col++)
            {
                int? z = GetElementAt(row, col);
                if (z.HasValue)
                {
                    result.Add(CreateElementButton(z.Value, GetUiElementColor(z.Value)));
                }
                else
                {
                    AddEmpty();
                }
            }
        }
        return result.ToArray();
    }

    int? GetElementAt(int row, int col)
    {
        if (row == 0)
        {
            if (col == 0) return 1;
            if (col == 17) return 2;
            return null;
        }
        if (row == 1)
        {
            if (col <= 1) return 3 + col;
            if (col >= 12) return 5 + (col - 12);
            return null;
        }
        if (row == 2)
        {
            if (col <= 1) return 11 + col;
            if (col >= 12) return 13 + (col - 12);
            return null;
        }
        if (row == 3)
        {
            if (col <= 1) return 19 + col;
            if (col >= 2 && col <= 11) return 21 + (col - 2);
            if (col >= 12) return 31 + (col - 12);
            return null;
        }
        if (row == 4)
        {
            if (col <= 17) return 37 + col;
            return null;
        }
        if (row == 5)
        {
            if (col <= 1) return 55 + col;
            if (col >= 3) return 72 + (col - 3);
            return null;
        }
        if (row == 6)
        {
            if (col <= 1) return 87 + col;
            if (col >= 3) return 104 + (col - 3);
            return null;
        }
        if (row == 7)
            return null;
        if (row == 8)
        {
            if (col >= 2 && col <= 16) return 57 + (col - 2);
            return null;
        }
        if (row == 9)
        {
            if (col >= 2 && col <= 16) return 89 + (col - 2);
            return null;
        }
        return null;
    }

    /// <summary>Colors for periodic table / HUD buttons (group-based palette).</summary>
    public static Color GetElementColorStatic(int z) => GetUiElementColor(z);

    /// <summary>CPK-style colors for 3D atom spheres only (e.g. C gray, O red, N blue).</summary>
    public static Color GetAtomSphereColor(int z)
    {
        if (z < 1 || z > 118) return new Color(0.82f, 0.82f, 0.84f);
        int i = (z - 1) * 3;
        return new Color(ElementRgb[i], ElementRgb[i + 1], ElementRgb[i + 2]);
    }

    static Color GetUiElementColor(int z)
    {
        if (z < 1 || z > 118) return new Color(0.9f, 0.9f, 0.9f);

        if (z >= 57 && z <= 71) return new Color(0.95f, 0.58f, 0.54f);
        if (z >= 89 && z <= 103) return new Color(0.85f, 0.45f, 0.55f);

        if (IsMetalloid(z)) return new Color(0.72f, 0.78f, 0.65f);
        if (IsPostTransitionMetal(z)) return new Color(0.72f, 0.55f, 0.45f);
        if (IsHalogen(z)) return new Color(0.45f, 0.82f, 0.55f);
        if (IsReactiveNonmetal(z)) return new Color(0.97f, 0.82f, 0.45f);

        int group = GetPeriodicTableGroup(z);
        return group switch
        {
            1 => new Color(0.91f, 0.66f, 0.49f),
            2 => new Color(0.91f, 0.85f, 0.55f),
            3 => new Color(0.52f, 0.76f, 0.91f),
            4 => new Color(0.52f, 0.76f, 0.91f),
            5 => new Color(0.52f, 0.76f, 0.91f),
            6 => new Color(0.52f, 0.76f, 0.91f),
            7 => new Color(0.52f, 0.76f, 0.91f),
            8 => new Color(0.52f, 0.76f, 0.91f),
            9 => new Color(0.52f, 0.76f, 0.91f),
            10 => new Color(0.52f, 0.76f, 0.91f),
            11 => new Color(0.52f, 0.76f, 0.91f),
            12 => new Color(0.52f, 0.76f, 0.91f),
            18 => new Color(0.62f, 0.62f, 0.70f),
            _ => new Color(0.9f, 0.9f, 0.9f)
        };
    }

    static bool IsMetalloid(int z) =>
        z is 5 or 14 or 32 or 33 or 51 or 52;

    static bool IsPostTransitionMetal(int z) =>
        z is 13 or 31 or 49 or 50 or 81 or 82 or 83 or 84 or 113 or 114 or 115 or 116;

    static bool IsHalogen(int z) =>
        z is 9 or 17 or 35 or 53 or 85 or 117;

    static bool IsReactiveNonmetal(int z) =>
        z is 1 or 6 or 7 or 8 or 15 or 16 or 34;

    static int GetPeriodicTableGroup(int z)
    {
        int[] g = { 1, 18, 1, 2, 13, 14, 15, 16, 17, 18, 1, 2, 13, 14, 15, 16, 17, 18,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            1, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            1, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 };
        return z > 0 && z <= g.Length ? g[z - 1] : 1;
    }

    /// <summary>Linearized sRGB 0–1 triples for Z = 1 … 118 (atom spheres only).</summary>
    static readonly float[] ElementRgb =
    {
        0.82353f, 0.82353f, 0.82353f, // 1 H — light gray
        0.85098f, 1.00000f, 1.00000f, // 2 He
        0.80000f, 0.50196f, 1.00000f, // 3 Li
        0.76078f, 1.00000f, 0.00000f, // 4 Be
        1.00000f, 0.70980f, 0.70980f, // 5 B
        0.25098f, 0.25098f, 0.25098f, // 6 C — dark gray
        0.18824f, 0.31373f, 0.97255f, // 7 N — blue
        1.00000f, 0.05098f, 0.05098f, // 8 O — red
        0.56471f, 1.00000f, 0.56471f, // 9 F
        0.70196f, 0.89020f, 0.96078f, // 10 Ne
        0.67059f, 0.36078f, 0.94902f, // 11 Na
        0.54118f, 1.00000f, 0.00000f, // 12 Mg
        0.74902f, 0.65098f, 0.65098f, // 13 Al
        0.94118f, 0.78431f, 0.62745f, // 14 Si
        1.00000f, 0.50196f, 0.00000f, // 15 P — orange
        1.00000f, 1.00000f, 0.18824f, // 16 S — yellow
        0.12157f, 0.94118f, 0.12157f, // 17 Cl
        0.50196f, 0.81961f, 0.89020f, // 18 Ar
        0.56078f, 0.25098f, 0.83137f, // 19 K
        0.23922f, 1.00000f, 0.00000f, // 20 Ca
        0.90196f, 0.90196f, 0.90196f, // 21 Sc
        0.74902f, 0.76078f, 0.78039f, // 22 Ti
        0.65098f, 0.65098f, 0.67059f, // 23 V
        0.54118f, 0.60000f, 0.78039f, // 24 Cr
        0.61176f, 0.47843f, 0.78039f, // 25 Mn
        0.87843f, 0.40000f, 0.20000f, // 26 Fe
        0.94118f, 0.56471f, 0.62745f, // 27 Co
        0.31373f, 0.81569f, 0.31373f, // 28 Ni
        0.78431f, 0.50196f, 0.20000f, // 29 Cu
        0.49020f, 0.50196f, 0.69020f, // 30 Zn
        0.76078f, 0.56078f, 0.56078f, // 31 Ga
        0.40000f, 0.56078f, 0.56078f, // 32 Ge
        0.74118f, 0.50196f, 0.89020f, // 33 As
        1.00000f, 0.63137f, 0.00000f, // 34 Se
        0.65098f, 0.16078f, 0.16078f, // 35 Br
        0.36078f, 0.72157f, 0.81961f, // 36 Kr
        0.43922f, 0.18039f, 0.69020f, // 37 Rb
        0.00000f, 1.00000f, 0.00000f, // 38 Sr
        0.58039f, 1.00000f, 1.00000f, // 39 Y
        0.90196f, 0.90196f, 0.90196f, // 40 Zr
        0.45098f, 0.76078f, 0.78824f, // 41 Nb
        0.71765f, 0.71765f, 0.82745f, // 42 Mo
        0.45882f, 0.36078f, 0.69020f, // 43 Tc
        0.14118f, 0.50196f, 0.56471f, // 44 Ru
        0.03922f, 0.49020f, 0.54902f, // 45 Rh
        0.41176f, 0.55686f, 0.64706f, // 46 Pd
        0.75294f, 0.75294f, 0.75294f, // 47 Ag
        1.00000f, 0.85098f, 0.56078f, // 48 Cd
        0.65098f, 0.45882f, 0.45098f, // 49 In
        0.40000f, 0.50196f, 0.50196f, // 50 Sn
        0.61961f, 0.38824f, 0.70980f, // 51 Sb
        0.83137f, 0.47843f, 0.00000f, // 52 Te
        0.58039f, 0.00000f, 0.58039f, // 53 I
        0.25882f, 0.61961f, 0.69020f, // 54 Xe
        0.34118f, 0.09020f, 0.56078f, // 55 Cs
        0.00000f, 0.78824f, 0.00000f, // 56 Ba
        0.43922f, 0.83137f, 1.00000f, // 57 La — lanthanides: distinct greens / teals
        1.00000f, 1.00000f, 0.78039f, // 58 Ce
        0.85098f, 1.00000f, 0.78039f, // 59 Pr
        0.78039f, 1.00000f, 0.78039f, // 60 Nd
        0.63922f, 1.00000f, 0.78039f, // 61 Pm
        0.56078f, 1.00000f, 0.78039f, // 62 Sm
        0.38039f, 1.00000f, 0.78039f, // 63 Eu
        0.27059f, 1.00000f, 0.78039f, // 64 Gd
        0.18824f, 1.00000f, 0.78039f, // 65 Tb
        0.12157f, 1.00000f, 0.78039f, // 66 Dy
        0.09020f, 1.00000f, 0.78039f, // 67 Ho
        0.03922f, 1.00000f, 0.78039f, // 68 Er
        0.00000f, 1.00000f, 0.61176f, // 69 Tm
        0.00000f, 0.90196f, 0.45882f, // 70 Yb
        0.00000f, 0.83137f, 0.32157f, // 71 Lu
        0.30196f, 0.76078f, 1.00000f, // 72 Hf
        0.30196f, 0.65098f, 1.00000f, // 73 Ta
        0.12941f, 0.58039f, 0.83922f, // 74 W
        0.14902f, 0.49020f, 0.67059f, // 75 Re
        0.14902f, 0.40000f, 0.58824f, // 76 Os
        0.09020f, 0.32941f, 0.52941f, // 77 Ir
        0.81569f, 0.81569f, 0.87843f, // 78 Pt
        1.00000f, 0.81961f, 0.13725f, // 79 Au
        0.72157f, 0.72157f, 0.81569f, // 80 Hg
        0.65098f, 0.32941f, 0.30196f, // 81 Tl
        0.34118f, 0.34902f, 0.38039f, // 82 Pb
        0.61961f, 0.30980f, 0.70980f, // 83 Bi
        0.67059f, 0.36078f, 0.00000f, // 84 Po
        0.45882f, 0.30980f, 0.27059f, // 85 At
        0.25882f, 0.50980f, 0.58824f, // 86 Rn
        0.25882f, 0.00000f, 0.40000f, // 87 Fr
        0.00000f, 0.49020f, 0.00000f, // 88 Ra
        0.43922f, 0.67059f, 0.98039f, // 89 Ac — actinides: purple / magenta steps
        1.00000f, 0.90196f, 1.00000f, // 90 Th
        1.00000f, 0.85098f, 1.00000f, // 91 Pa
        1.00000f, 0.80000f, 1.00000f, // 92 U
        1.00000f, 0.74902f, 1.00000f, // 93 Np
        1.00000f, 0.69804f, 1.00000f, // 94 Pu
        1.00000f, 0.64706f, 1.00000f, // 95 Am
        1.00000f, 0.60000f, 1.00000f, // 96 Cm
        1.00000f, 0.54902f, 1.00000f, // 97 Bk
        1.00000f, 0.50196f, 1.00000f, // 98 Cf
        1.00000f, 0.45098f, 1.00000f, // 99 Es
        1.00000f, 0.40000f, 1.00000f, // 100 Fm
        0.34902f, 0.34902f, 1.00000f, // 101 Md
        0.25882f, 0.25882f, 1.00000f, // 102 No
        0.21176f, 0.21176f, 1.00000f, // 103 Lr
        0.80000f, 0.80000f, 0.78039f, // 104 Rf — superheavy: steel gray ramp
        0.72157f, 0.72157f, 0.70980f, // 105
        0.65098f, 0.65098f, 0.66667f, // 106
        0.60000f, 0.60000f, 0.65882f, // 107
        0.56078f, 0.56078f, 0.65098f, // 108
        0.52157f, 0.52157f, 0.64314f, // 109
        0.49020f, 0.49020f, 0.63529f, // 110
        0.45882f, 0.45882f, 0.62745f, // 111
        0.43137f, 0.43137f, 0.61961f, // 112
        0.40000f, 0.40000f, 0.61176f, // 113
        0.37647f, 0.37647f, 0.60392f, // 114
        0.35294f, 0.35294f, 0.59608f, // 115
        0.32941f, 0.32941f, 0.58824f, // 116
        0.30588f, 0.30588f, 0.58039f, // 117
        0.28235f, 0.28235f, 0.57255f, // 118 Og
    };

    GameObject CreateElementButton(int atomicNumber, Color bgColor)
    {
        var go = new GameObject($"Element_{atomicNumber}");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(layoutCellSize, layoutCellSize);

        var image = go.AddComponent<Image>();
        image.color = bgColor;

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = Color.Lerp(bgColor, Color.white, 0.2f);
        colors.pressedColor = Color.Lerp(bgColor, Color.black, 0.1f);
        btn.colors = colors;

        int z = atomicNumber;
        btn.onClick.AddListener(() => OnElementSelected(z));

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var labelRect = labelGo.AddComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        var tmp = labelGo.AddComponent<TextMeshProUGUI>();
        tmp.font = AtomFunction.GetDefaultFont();
        tmp.text = AtomFunction.GetElementSymbol(atomicNumber);
        tmp.fontSize = layoutFontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return go;
    }

    void OnElementSelected(int atomicNumber)
    {
        CreateAtom(atomicNumber);
        Hide();
    }

    void CreateAtom(int atomicNumber)
    {
        if (atomPrefab == null || Camera.main == null) return;

        var editMode = FindFirstObjectByType<EditModeManager>();
        if (editMode != null && editMode.EditModeActive && editMode.SelectedAtom != null)
        {
            if (editMode.TryAddAtomToSelected(atomicNumber))
                return;
        }

        Vector3 pos = PlanarPointerInteraction.SnapWorldToWorkPlaneIfPresent(GetRandomPositionInView());
        var atomObj = Instantiate(atomPrefab, pos, Quaternion.identity);
        if (atomObj.TryGetComponent<AtomFunction>(out var atom))
        {
            atom.AtomicNumber = atomicNumber;
            if (editMode != null && editMode.HAutoMode)
                editMode.SaturateWithHydrogen(atom);
        }
    }

    Vector3 GetRandomPositionInView() => PlanarPointerInteraction.RandomWorldPointInMarginedViewport(viewportMargin);
}
