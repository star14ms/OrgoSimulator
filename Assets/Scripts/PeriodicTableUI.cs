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

    void CreatePanel()
    {
        var canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) return;

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
        grid.cellSize = new Vector2(cellSize, cellSize);
        grid.spacing = new Vector2(spacing, spacing);
        grid.padding = new RectOffset((int)blockPadding, (int)blockPadding, (int)blockPadding, (int)blockPadding);
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
            rect.sizeDelta = new Vector2(cellSize, cellSize);
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
                    result.Add(CreateElementButton(z.Value, GetElementColor(z.Value)));
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

    static Color GetElementColor(int z)
    {
        if (z < 1 || z > 118) return new Color(0.9f, 0.9f, 0.9f);

        // Lanthanides (La–Lu) and actinides (Ac–Lr)
        if (z >= 57 && z <= 71) return new Color(0.95f, 0.58f, 0.54f);   // rose
        if (z >= 89 && z <= 103) return new Color(0.85f, 0.45f, 0.55f);   // dusty rose

        // Metalloids: B, Si, Ge, As, Sb, Te
        if (IsMetalloid(z)) return new Color(0.72f, 0.78f, 0.65f);       // olive-green

        // Post-transition metals: Al, Ga, In, Sn, Tl, Pb, Bi, Po, Nh, Fl, Mc, Lv
        if (IsPostTransitionMetal(z)) return new Color(0.72f, 0.55f, 0.45f);  // bronze

        // Halogens: F, Cl, Br, I, At, Ts
        if (IsHalogen(z)) return new Color(0.45f, 0.82f, 0.55f);              // mint green

        // Reactive nonmetals: H, C, N, O, P, S, Se
        if (IsReactiveNonmetal(z)) return new Color(0.97f, 0.82f, 0.45f);     // amber

        int group = GetGroup(z);
        return group switch
        {
            1 => new Color(0.91f, 0.66f, 0.49f),   // alkali metals
            2 => new Color(0.91f, 0.85f, 0.55f),   // alkaline earth
            3 => new Color(0.52f, 0.76f, 0.91f),   // transition (Sc, Y)
            4 => new Color(0.52f, 0.76f, 0.91f),
            5 => new Color(0.52f, 0.76f, 0.91f),
            6 => new Color(0.52f, 0.76f, 0.91f),
            7 => new Color(0.52f, 0.76f, 0.91f),
            8 => new Color(0.52f, 0.76f, 0.91f),
            9 => new Color(0.52f, 0.76f, 0.91f),
            10 => new Color(0.52f, 0.76f, 0.91f),
            11 => new Color(0.52f, 0.76f, 0.91f),
            12 => new Color(0.52f, 0.76f, 0.91f),
            18 => new Color(0.62f, 0.62f, 0.70f),   // noble gases (He, Ne, Ar, Kr, Xe, Rn, Og) - brighter
            _ => new Color(0.9f, 0.9f, 0.9f)
        };
    }

    static bool IsMetalloid(int z) =>
        z is 5 or 14 or 32 or 33 or 51 or 52;  // B, Si, Ge, As, Sb, Te

    static bool IsPostTransitionMetal(int z) =>
        z is 13 or 31 or 49 or 50 or 81 or 82 or 83 or 84 or 113 or 114 or 115 or 116;  // Al, Ga, In, Sn, Tl, Pb, Bi, Po, Nh, Fl, Mc, Lv

    static bool IsHalogen(int z) =>
        z is 9 or 17 or 35 or 53 or 85 or 117;  // F, Cl, Br, I, At, Ts

    static bool IsReactiveNonmetal(int z) =>
        z is 1 or 6 or 7 or 8 or 15 or 16 or 34;  // H, C, N, O, P, S, Se

    static int GetGroup(int z)
    {
        int[] g = { 1, 18, 1, 2, 13, 14, 15, 16, 17, 18, 1, 2, 13, 14, 15, 16, 17, 18,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            1, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            1, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 };
        return z > 0 && z <= g.Length ? g[z - 1] : 1;
    }

    GameObject CreateElementButton(int atomicNumber, Color bgColor)
    {
        var go = new GameObject($"Element_{atomicNumber}");
        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(cellSize, cellSize);

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
        tmp.fontSize = fontSize;
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

        Vector3 pos = GetRandomPositionInView();
        var atomObj = Instantiate(atomPrefab, pos, Quaternion.identity);
        if (atomObj.TryGetComponent<AtomFunction>(out var atom))
            atom.AtomicNumber = atomicNumber;
    }

    Vector3 GetRandomPositionInView()
    {
        float min = viewportMargin;
        float max = 1f - viewportMargin;
        Vector3 minWorld = Camera.main.ViewportToWorldPoint(new Vector3(min, min, -Camera.main.transform.position.z));
        Vector3 maxWorld = Camera.main.ViewportToWorldPoint(new Vector3(max, max, -Camera.main.transform.position.z));
        return new Vector3(
            Random.Range(minWorld.x, maxWorld.x),
            Random.Range(minWorld.y, maxWorld.y),
            minWorld.z
        );
    }
}
