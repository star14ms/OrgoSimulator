using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

public class ElectronOrbitalFunction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] int electronCount;
    [SerializeField] ElectronFunction electronPrefab;
    [SerializeField] float electronSpacing = 0.5f; // Distance between electrons (larger = more spread)
    [SerializeField] Sprite stretchSprite; // Optional: single sprite for stretch. If null, uses procedural triangle+circle composite.
    [SerializeField] [Range(0.05f, 1f)] float orbitalVisualAlpha = 0.05f;
    [Tooltip("Edit-mode selected orbital: body alpha (higher than orbitalVisualAlpha so the lobe reads brighter).")]
    [SerializeField] [Range(0.05f, 1f)] float orbitalHighlightAlpha = 0.14f;
    [Tooltip("3D drag stretch only: scales hemisphere diameter and cone base (XZ) vs idle orbital sizing.")]
    [SerializeField] [Range(0.35f, 1f)] float dragStretch3DCrossSectionScale = 0.65f;
    [Tooltip("3D drag: offset from flat seam into the hemispherical bulk along the tip axis, as a fraction of cap radius (keeps electrons under the dome, not on the outer shell).")]
    [SerializeField] [Range(0.08f, 0.5f)] float drag3DElectronDepthInCapFraction = 0.32f;
    [Tooltip("Draw seam → target → electron paths in Scene view while dragging a 3D orbital.")]
    [SerializeField] bool debugDraw3DElectronDrag;

    /// <summary>Unity Console logs for π-bond orbital redistribution (tag <c>[π-redist]</c>).</summary>
    public static bool DebugPiOrbitalRedistribution;

    public static void LogPiRedistDebug(string message) { }

    /// <summary>Unity + project <c>.log</c>: <c>[σ-form-pose]</c> — bonding orbital + non-bond tips during σ formation. Default off.</summary>
    public static bool DebugLogSigmaBondFormationOrbitalPose = false;
    /// <summary>Legacy log gates for σ hybrid / bond tip diagnostics (animated σ formation removed).</summary>
    public static bool DebugLogSigmaFormationHeavyOrbRotationWhy = true;
    public static int SigmaFormationHeavyRotDiagBudget;
    public static bool ConsumeSigmaFormationHeavyRotDiag()
    {
        if (!DebugLogSigmaFormationHeavyOrbRotationWhy || SigmaFormationHeavyRotDiagBudget <= 0) return false;
        SigmaFormationHeavyRotDiagBudget--;
        return true;
    }

    internal static void ReapplyBondSteppedGuideClusterHighlightAfterPickClear() { }

    AtomFunction bondedAtom;
    CovalentBond bond;
    bool isBeingHeld;
    /// <summary>True only after a non-blocked <see cref="OnPointerDown"/> for this orbital; false when carry-release blocked the down — prevents orphan <see cref="OnPointerUp"/> from peeling.</summary>
    bool orbitalPressHasPairedPointerDown;
    Vector3 dragOffset;
    Vector2 pointerDownPosition;

    public const int MaxElectrons = 2;
    public int ElectronCount
    {
        get => electronCount;
        set
        {
            int oldCount = electronCount;
            electronCount = Mathf.Clamp(value, 0, MaxElectrons);
            int delta = electronCount - oldCount;
            SyncElectronObjects();
            if (delta != 0)
            {
                if (bond != null)
                    bond.NotifyElectronCountChanged();
                else if (bondedAtom != null)
                {
                    for (int i = 0; i < Mathf.Abs(delta); i++)
                        if (delta > 0) bondedAtom.OnElectronAdded();
                        else bondedAtom.OnElectronRemoved();
                }
            }
        }
    }

    public bool IsBonded => (bondedAtom != null || bond != null) && !isBeingHeld;
    public AtomFunction BondedAtom => bondedAtom;
    public CovalentBond Bond => bond;

    public void SetBondedAtom(AtomFunction atom) => bondedAtom = atom;

    public void SetBond(CovalentBond b) => bond = b;

    /// <summary>Prefab used for lone-pair electrons (e.g. spawn free electrons for testing).</summary>
    public ElectronFunction ElectronPrefab => electronPrefab;

    public void RemoveElectron(ElectronFunction electron)
    {
        if (electron.transform.parent != transform) return;
        electronCount = Mathf.Clamp(electronCount - 1, 0, MaxElectrons);
        electron.transform.SetParent(null);
        if (bond != null)
            bond.NotifyElectronCountChanged();
        else
            bondedAtom?.OnElectronRemoved();
        SyncElectronObjects();
    }

    public bool CanAcceptElectron() => electronCount < MaxElectrons;

    public bool AcceptElectron(ElectronFunction electron)
    {
        if (electronCount >= MaxElectrons) return false;
        Destroy(electron.gameObject);
        electronCount++;
        SyncElectronObjects();
        if (bond != null)
            bond.NotifyElectronCountChanged();
        else
        {
            bondedAtom?.OnElectronAdded();
            bondedAtom?.SetupIgnoreCollisions();
        }
        return true;
    }

    public bool ContainsPoint(Vector3 worldPos)
    {
        var col2D = GetComponent<Collider2D>();
        if (col2D != null) return col2D.OverlapPoint(worldPos);
        var col = GetComponent<Collider>();
        return col != null && col.bounds.Contains(worldPos);
    }

    const float ViewOverlapScreenPaddingPx = 14f;

    /// <summary>World bounds used to project an orbital silhouette to the screen (collider preferred, then renderer).</summary>
    public Bounds GetOrbitalBoundsForView()
    {
        var col = GetComponent<Collider>();
        if (col != null) return col.bounds;
        var col2D = GetComponent<Collider2D>();
        if (col2D != null) return col2D.bounds;
        var r = GetComponent<Renderer>();
        if (r != null) return r.bounds;
        return new Bounds(transform.position, Vector3.one * 0.25f);
    }

    /// <summary>
    /// True if the screen-space axis-aligned bounds of two orbitals overlap. Depth along the view axis is ignored
    /// so bonding matches what the player sees in the 2D window in perspective mode.
    /// </summary>
    public static bool OrbitalViewOverlaps(Camera cam, ElectronOrbitalFunction a, ElectronOrbitalFunction b, float expandPixels = ViewOverlapScreenPaddingPx)
    {
        if (cam == null || a == null || b == null) return false;
        if (!TryProjectOrbitalToScreenRect(cam, a, expandPixels, out var ra)) return false;
        if (!TryProjectOrbitalToScreenRect(cam, b, expandPixels, out var rb)) return false;
        return ra.Overlaps(rb);
    }

    static bool TryProjectOrbitalToScreenRect(Camera cam, ElectronOrbitalFunction o, float padPixels, out Rect rect)
    {
        return TryProjectWorldBoundsToScreenRect(cam, o.GetOrbitalBoundsForView(), padPixels, out rect);
    }

    static bool TryProjectWorldPointToScreenRect(Camera cam, Vector3 worldPos, float padPixels, out Rect rect)
    {
        rect = default;
        if (cam == null) return false;
        Vector3 s = cam.WorldToScreenPoint(worldPos);
        if (s.z < cam.nearClipPlane) return false;
        rect = Rect.MinMaxRect(s.x - padPixels, s.y - padPixels, s.x + padPixels, s.y + padPixels);
        return true;
    }

    /// <summary>
    /// Screen-space overlap between a dragged electron (world position ± padding) and an orbital silhouette,
    /// same padding as <see cref="OrbitalViewOverlaps"/> for bonding.
    /// </summary>
    public static bool ElectronViewOverlapsOrbital(Camera cam, ElectronOrbitalFunction orbital, Vector3 electronWorldPos, float expandPixels = ViewOverlapScreenPaddingPx)
    {
        if (cam == null || orbital == null) return false;
        if (!TryProjectWorldPointToScreenRect(cam, electronWorldPos, expandPixels, out var re)) return false;
        if (!TryProjectOrbitalToScreenRect(cam, orbital, expandPixels, out var ro)) return false;
        return re.Overlaps(ro);
    }

    static bool TryProjectWorldBoundsToScreenRect(Camera cam, Bounds b, float padPixels, out Rect rect)
    {
        rect = default;
        if (cam == null) return false;
        Vector3 c = b.center;
        Vector3 e = b.extents;
        bool any = false;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int ix = -1; ix <= 1; ix += 2)
        {
            for (int iy = -1; iy <= 1; iy += 2)
            {
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    Vector3 w = c + new Vector3(e.x * ix, e.y * iy, e.z * iz);
                    Vector3 s = cam.WorldToScreenPoint(w);
                    if (s.z < cam.nearClipPlane) continue;
                    any = true;
                    minX = Mathf.Min(minX, s.x);
                    minY = Mathf.Min(minY, s.y);
                    maxX = Mathf.Max(maxX, s.x);
                    maxY = Mathf.Max(maxY, s.y);
                }
            }
        }

        if (!any) return false;
        rect = Rect.MinMaxRect(minX - padPixels, minY - padPixels, maxX + padPixels, maxY + padPixels);
        return true;
    }

    public void SetPointerBlocked(bool blocked)
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col != null) col.enabled = !blocked;
        if (col2D != null) col2D.enabled = !blocked;
    }

    /// <summary>Re-spawns/repositions electron children after lobe layout settles (e.g. after VSEPR redistribution).</summary>
    public void RefreshElectronSyncAfterLayout() => SyncElectronObjects();

    static void ConfigureUnlitTransparent(Material mat)
    {
        if (mat == null) return;
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        else
        {
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
        }
    }

    bool editSelectionHighlightActive;

    MeshRenderer GetPrimaryBodyMeshRendererForVisual()
    {
        var mr = GetComponent<MeshRenderer>();
        if (mr != null) return mr;
        if (mainMeshRenderer != null) return mainMeshRenderer;
        foreach (var r in GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r.GetComponent<ElectronFunction>() != null) continue;
            return r;
        }
        return null;
    }

    /// <summary>Highlight this orbital in edit mode by raising body opacity (no ring overlay).</summary>
    public void SetHighlighted(bool on)
    {
        editSelectionHighlightActive = on;
        DestroyLegacyOrbitalOutlineDecorations();
        ApplyOrbitalEditSelectionVisual(on);
    }

    /// <summary>Redistribute template preview click: tint main orbital body red (bond line uses <see cref="CovalentBond.SetBondFormationTemplatePickHighlight"/>).</summary>
    public void SetBondFormationTemplatePickHighlight(bool on)
    {
        var sr = GetComponent<SpriteRenderer>();
        var mr = GetPrimaryBodyMeshRendererForVisual();
        if (sr == null && mr == null) return;
        EnsureOriginalColorFromRenderer();
        if (!on)
        {
            if (editSelectionHighlightActive)
                ApplyOrbitalEditSelectionVisual(true);
            else
                ApplyOrbitalVisualOpacity(sr, mr);
            return;
        }
        float a = Mathf.Clamp01(originalColor.a);
        Color red = new Color(0.95f, 0.2f, 0.2f, Mathf.Max(0.35f, a));
        if (sr != null) sr.color = red;
        else if (mr != null) SetMaterialTint(mr.material, red);
    }

    void DestroyLegacyOrbitalOutlineDecorations()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            string n = ch.name;
            if (n == "SelectionHighlight" || n == "EditModeSelectionGlow")
                Destroy(ch.gameObject);
        }
    }

    void EnsureOriginalColorFromRenderer()
    {
        if (originalColor.a >= 0.01f) return;
        var sr = GetComponent<SpriteRenderer>();
        var mr = GetPrimaryBodyMeshRendererForVisual();
        if (sr != null) originalColor = sr.color;
        else if (mr != null && mr.sharedMaterial != null) originalColor = GetMaterialTint(mr.sharedMaterial);
    }

    void ApplyOrbitalEditSelectionVisual(bool selected)
    {
        var sr = GetComponent<SpriteRenderer>();
        var mr = GetPrimaryBodyMeshRendererForVisual();
        if (sr == null && mr == null) return;
        EnsureOriginalColorFromRenderer();
        if (!selected)
        {
            ApplyOrbitalVisualOpacity(sr, mr);
            return;
        }

        float a = Mathf.Clamp01(Mathf.Max(orbitalHighlightAlpha, orbitalVisualAlpha));
        Color c = new Color(originalColor.r, originalColor.g, originalColor.b, a);
        if (sr != null) sr.color = c;
        else if (mr != null) SetMaterialTint(mr.material, c);
    }

    /// <summary>Show or hide the orbital and its electron visuals. Used when bond displays as a line.</summary>
    public void SetVisualsEnabled(bool enabled)
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr != null) sr.enabled = enabled;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
            r.enabled = enabled;
    }

    public void SetPhysicsEnabled(bool enabled)
    {
        void SetRb(GameObject go)
        {
            var rb = go.GetComponent<Rigidbody>();
            var rb2D = go.GetComponent<Rigidbody2D>();
            if (rb != null) rb.isKinematic = !enabled;
            if (rb2D != null) rb2D.simulated = enabled;
        }
        SetRb(gameObject);
        foreach (var e in GetComponentsInChildren<ElectronFunction>())
            SetRb(e.gameObject);
    }

    Vector3 originalLocalPosition;
    Vector3 originalLocalScale;
    Quaternion originalLocalRotation;
    bool pointerDownSnapshotCapturedFromInput;

    /// <summary>Pointer-down snapshot in parent local space; σ phase-1 lerp start for the dragged forming lobe should match this after snap-back.</summary>
    public void GetPointerDownOriginalLocalPose(out Vector3 localPosition, out Quaternion localRotation)
    {
        localPosition = originalLocalPosition;
        localRotation = originalLocalRotation;
    }

    /// <summary>True only when the pointer-down snapshot was captured by input (not just Start seeding).</summary>
    public bool HasPointerDownSnapshotFromInput()
    {
        return pointerDownSnapshotCapturedFromInput;
    }
    Color originalColor;
    GameObject stretchVisual;
    bool stretchVisualIs3D;
    SpriteRenderer mainSpriteRenderer;
    MeshRenderer mainMeshRenderer;
    const float MinStretchLength = 0.5f;
    const float OffsetScale = 1.14f;
    [SerializeField] float apexPadding = 0.2f;
    static Sprite cachedTriangleSprite;
    static Sprite cachedCircleSprite;
    static Material cachedTriangleClipMaterial;
    static Mesh cachedBuiltinSphereMesh;
    static Mesh cachedUnitConeMesh;
    static Mesh cachedUnitHemisphereMesh;
    static readonly int ClipCenterId = Shader.PropertyToID("_ClipCenter");
    static readonly int ClipRadiusXId = Shader.PropertyToID("_ClipRadiusX");
    static readonly int ClipRadiusYId = Shader.PropertyToID("_ClipRadiusY");
    static readonly int ShaderPropBaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ShaderPropColor = Shader.PropertyToID("_Color");
    /// <summary>URP 2D Mesh2D-Lit-Default tint (see shader Properties — not _Color).</summary>
    static readonly int ShaderPropWhite = Shader.PropertyToID("_White");
    static readonly int ShaderPropRendererColor = Shader.PropertyToID("_RendererColor");

    static Color GetMaterialTint(Material mat)
    {
        if (mat == null) return Color.white;
        if (mat.HasProperty(ShaderPropBaseColor)) return mat.GetColor(ShaderPropBaseColor);
        if (mat.HasProperty(ShaderPropWhite)) return mat.GetColor(ShaderPropWhite);
        if (mat.HasProperty(ShaderPropRendererColor)) return mat.GetColor(ShaderPropRendererColor);
        if (mat.HasProperty(ShaderPropColor)) return mat.GetColor(ShaderPropColor);
        return Color.white;
    }

    static void SetMaterialTint(Material mat, Color c)
    {
        if (mat == null) return;
        if (mat.HasProperty(ShaderPropBaseColor)) mat.SetColor(ShaderPropBaseColor, c);
        else if (mat.HasProperty(ShaderPropWhite)) mat.SetColor(ShaderPropWhite, c);
        else if (mat.HasProperty(ShaderPropRendererColor)) mat.SetColor(ShaderPropRendererColor, c);
        else if (mat.HasProperty(ShaderPropColor)) mat.SetColor(ShaderPropColor, c);
    }

    /// <summary>Template preview stem/tip mesh tint: green default vs red when picked.</summary>
    public static void SetRedistributeTemplatePreviewRendererPickHighlight(Renderer r, bool highlightRed)
    {
        if (r == null) return;
        var m = r.material;
        var c = highlightRed
            ? new Color(0.95f, 0.18f, 0.18f, 0.45f)
            : new Color(0.22f, 0.9f, 0.32f, 0.45f);
        SetMaterialTint(m, c);
    }

    static Material GetOrCreateTriangleClipMaterial()
    {
        if (cachedTriangleClipMaterial != null) return cachedTriangleClipMaterial;
        var shader = Shader.Find("OrgoSimulator/TriangleClipCircle");
        cachedTriangleClipMaterial = shader != null ? new Material(shader) : null;
        return cachedTriangleClipMaterial;
    }

    static Sprite GetOrCreateTriangleSprite()
    {
        if (cachedTriangleSprite != null) return cachedTriangleSprite;
        cachedTriangleSprite = CreateTriangleSprite();
        return cachedTriangleSprite;
    }

    static Sprite GetOrCreateCircleSprite()
    {
        if (cachedCircleSprite != null) return cachedCircleSprite;
        cachedCircleSprite = CreateCircleSprite();
        return cachedCircleSprite;
    }

    static Sprite CreateTriangleSprite(int width = 64, int height = 64)
    {
        var tex = new Texture2D(width, height);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color32[width * height];
        float cx = (width - 1) * 0.5f;
        for (int y = 0; y < height; y++)
        {
            float t = (float)y / (height - 1);
            float halfWidth = 0.5f * t;
            for (int x = 0; x < width; x++)
            {
                float dx = Mathf.Abs((x - cx) / (width * 0.5f));
                float alpha = dx <= halfWidth ? 1f - (dx / (halfWidth + 0.01f)) * 0.3f : 0f;
                if (alpha > 0.02f)
                    pixels[y * width + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255));
                else
                    pixels[y * width + x] = Color.clear;
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 32f);
    }

    static Sprite CreateCircleSprite(int size = 64)
    {
        var tex = new Texture2D(size, size);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color32[size * size];
        float cx = (size - 1) * 0.5f;
        float r = cx * 0.9f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx;
                float dy = y - cx;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = d <= r ? 1f - (d / r) * 0.3f : 0f;
                if (alpha > 0.01f)
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                else
                    pixels[y * size + x] = Color.clear;
            }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f);
    }

    static bool Use3DOrbitalPresentation() => true;

    static Mesh GetBuiltinSphereMesh()
    {
        if (cachedBuiltinSphereMesh != null) return cachedBuiltinSphereMesh;
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        cachedBuiltinSphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);
        return cachedBuiltinSphereMesh;
    }

    /// <summary>Upper hemisphere, radius 0.5 (diameter 1 at scale 1, matches Unity sphere). Flat cap in XZ at y=0, dome toward +Y — attach y=0 flush to cone top.</summary>
    static Mesh GetOrCreateUnitHemisphereMesh()
    {
        if (cachedUnitHemisphereMesh != null) return cachedUnitHemisphereMesh;
        const float rad = 0.5f;
        const int nSeg = 24;
        const int nLat = 12;
        var verts = new List<Vector3>();
        var tris = new List<int>();

        verts.Add(new Vector3(0f, rad, 0f));

        for (int lat = 1; lat <= nLat; lat++)
        {
            float phi = (lat / (float)nLat) * (Mathf.PI * 0.5f);
            float y = rad * Mathf.Cos(phi);
            float ringR = rad * Mathf.Sin(phi);
            for (int seg = 0; seg < nSeg; seg++)
            {
                float theta = (seg / (float)nSeg) * Mathf.PI * 2f;
                verts.Add(new Vector3(ringR * Mathf.Cos(theta), y, ringR * Mathf.Sin(theta)));
            }
        }

        for (int seg = 0; seg < nSeg; seg++)
        {
            int s1 = (seg + 1) % nSeg;
            tris.Add(0);
            tris.Add(1 + s1);
            tris.Add(1 + seg);
        }

        for (int lat = 1; lat < nLat; lat++)
        {
            int ringA = 1 + (lat - 1) * nSeg;
            int ringB = 1 + lat * nSeg;
            for (int seg = 0; seg < nSeg; seg++)
            {
                int s1 = (seg + 1) % nSeg;
                int a = ringA + seg, b = ringA + s1, c = ringB + seg, d = ringB + s1;
                tris.Add(a);
                tris.Add(c);
                tris.Add(b);
                tris.Add(b);
                tris.Add(c);
                tris.Add(d);
            }
        }

        int eqBase = 1 + (nLat - 1) * nSeg;
        int capCenter = verts.Count;
        verts.Add(Vector3.zero);
        for (int seg = 0; seg < nSeg; seg++)
        {
            int s1 = (seg + 1) % nSeg;
            tris.Add(capCenter);
            tris.Add(eqBase + s1);
            tris.Add(eqBase + seg);
        }

        cachedUnitHemisphereMesh = new Mesh { name = "OrgoUnitHemisphere" };
        cachedUnitHemisphereMesh.SetVertices(verts);
        cachedUnitHemisphereMesh.SetTriangles(tris, 0);
        cachedUnitHemisphereMesh.RecalculateNormals();
        cachedUnitHemisphereMesh.RecalculateBounds();
        return cachedUnitHemisphereMesh;
    }

    static Mesh GetOrCreateUnitConeMesh()
    {
        if (cachedUnitConeMesh != null) return cachedUnitConeMesh;
        const int n = 20;
        const float h = 1f;
        const float r = 0.5f;
        var vertices = new Vector3[n + 2];
        var uv = new Vector2[n + 2];
        vertices[0] = new Vector3(0f, -h * 0.5f, 0f);
        uv[0] = new Vector2(0.5f, 0f);
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n * Mathf.PI * 2f;
            float x = Mathf.Cos(t) * r;
            float z = Mathf.Sin(t) * r;
            vertices[i + 1] = new Vector3(x, h * 0.5f, z);
            uv[i + 1] = new Vector2(i / (float)n, 1f);
        }
        var tris = new int[n * 3];
        for (int i = 0; i < n; i++)
        {
            tris[i * 3] = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = i + 2;
        }
        cachedUnitConeMesh = new Mesh { name = "OrgoUnitCone" };
        cachedUnitConeMesh.vertices = vertices;
        cachedUnitConeMesh.triangles = tris;
        cachedUnitConeMesh.uv = uv;
        cachedUnitConeMesh.RecalculateNormals();
        cachedUnitConeMesh.RecalculateBounds();
        return cachedUnitConeMesh;
    }

    static void ConfigureUrpSurfaceTransparent(Material mat)
    {
        if (mat == null) return;
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite"))
                mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }
        else
        {
            // Without _Surface (some URP variants), still avoid opaque shell overwriting inner electrons.
            if (mat.HasProperty("_ZWrite"))
                mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
        }
    }

    /// <summary>Prefer URP Unlit so WebGL builds keep a working transparent mesh (Lit transparent variants are often stripped).</summary>
    static Shader TryFindUrplShader()
    {
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
        return sh;
    }

    Material CreateStretchOrbitalMaterial(Color tint)
    {
        var sh = TryFindUrplShader();
        if (sh == null) return null;
        var mat = new Material(sh);
        ConfigureUrpSurfaceTransparent(mat);
        mat.color = tint;
        if (mat.HasProperty(ShaderPropBaseColor)) mat.SetColor(ShaderPropBaseColor, tint);
        return mat;
    }

    static Quaternion RotationFromUpTo(Vector3 dir)
    {
        if (dir.sqrMagnitude < 1e-10f) return Quaternion.identity;
        dir.Normalize();
        if (Vector3.Dot(Vector3.up, dir) < -0.998f)
            return Quaternion.AngleAxis(180f, Vector3.right);
        return Quaternion.FromToRotation(Vector3.up, dir);
    }

    void Setup3DOrbitalVisual(MeshFilter mf, MeshRenderer mr)
    {
        if (mf == null || mr == null) return;
        mf.sharedMesh = GetBuiltinSphereMesh();
        var sh = TryFindUrplShader();
        if (sh == null) return;
        var mat = new Material(sh);
        ConfigureUrpSurfaceTransparent(mat);
        mr.sharedMaterial = mat;
        mainMeshRenderer = mr;
    }

    /// <summary>Short click (no drag): remove one electron from this lone orbital and attach pointer-follow carry.</summary>
    bool TryPeelOneElectronForCarry(Vector2 screenPosition)
    {
        if (electronPrefab == null || electronCount < 1) return false;
        var electrons = GetComponentsInChildren<ElectronFunction>();
        if (electrons == null || electrons.Length == 0) return false;
        var e = electrons[electrons.Length - 1];
        RemoveElectron(e);
        e.transform.position = PlanarPointerInteraction.SnapWorldToWorkPlaneIfPresent(
            PlanarPointerInteraction.ScreenToWorldPoint(screenPosition));
        ElectronCarryInput.Instance.StartCarrying(e);
        AtomFunction.SetupGlobalIgnoreCollisions();
        return true;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (ElectronCarryInput.BlockOrbitalPointerForCarryFinalize)
        {
            orbitalPressHasPairedPointerDown = false;
            return;
        }

        // Stepped bond debug: selection resolves in OnPointerUp; do not start drag / stretch / bond gestures.
        if (BondFormationDebugController.IsWaitingForPhase)
        {
            orbitalPressHasPairedPointerDown = true;
            pointerDownPosition = eventData.position;
            originalLocalPosition = transform.localPosition;
            originalLocalScale = transform.localScale;
            originalLocalRotation = transform.localRotation;
            pointerDownSnapshotCapturedFromInput = true;
            return;
        }

        orbitalPressHasPairedPointerDown = true;
        isBeingHeld = true;
        pointerDownPosition = eventData.position;
        originalLocalPosition = transform.localPosition;
        originalLocalScale = transform.localScale;
        originalLocalRotation = transform.localRotation;
        pointerDownSnapshotCapturedFromInput = true;
        var cam = Camera.main;
        var wp = MoleculeWorkPlane.Instance;
        if (cam != null && wp != null && wp.TryGetWorldPoint(cam, eventData.position, out var hit))
            dragOffset = transform.position - hit;
        else
            dragOffset = transform.position - PlanarPointerInteraction.ScreenToWorldPoint(eventData.position);
        mainSpriteRenderer = GetComponent<SpriteRenderer>();
        mainMeshRenderer = GetComponent<MeshRenderer>();
        // Perspective σ bond: do not replace the lobe with the translucent stretch cone/hemisphere — that mesh uses
        // the transparent queue and draws over the electron spheres (geometry queue), so the pair vanishes during drag.
        bool keepBondBodyFor3DDrag = bond != null && Use3DOrbitalPresentation();
        if (mainSpriteRenderer != null && !keepBondBodyFor3DDrag) mainSpriteRenderer.enabled = false;
        if (mainMeshRenderer != null && !keepBondBodyFor3DDrag) mainMeshRenderer.enabled = false;
        if (!keepBondBodyFor3DDrag)
            CreateStretchVisual();
        SetPhysicsEnabled(false);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!isBeingHeld) return;
        var cam = Camera.main;
        Vector3 tip;
        if (cam != null && MoleculeWorkPlane.Instance != null &&
            MoleculeWorkPlane.Instance.TryGetWorldPoint(cam, eventData.position, out var hit))
        {
            tip = hit + dragOffset;
            tip = PlanarPointerInteraction.SnapWorldToWorkPlaneIfPresent(tip);
            transform.position = tip;
            dragOffset = transform.position - hit;
        }
        else
        {
            tip = PlanarPointerInteraction.ScreenToWorldPoint(eventData.position) + dragOffset;
            transform.position = tip;
        }

        UpdateStretchVisual(tip);
    }

    const float ShortClickDragThresholdPx = 10f;

    public void OnPointerUp(PointerEventData eventData)
    {
        bool hadPairedDown = orbitalPressHasPairedPointerDown;
        orbitalPressHasPairedPointerDown = false;

        isBeingHeld = false;
        Vector3 tip = transform.position;
        if (stretchVisual != null) { Destroy(stretchVisual); stretchVisual = null; }
        Reposition3DElectronsAfterOrbitalDrag();
        if (mainSpriteRenderer != null) mainSpriteRenderer.enabled = true;
        if (mainMeshRenderer != null) mainMeshRenderer.enabled = true;
        RefreshOrbitalBodyVisualAfterDrag();
        var atomForBondBlock = bondedAtom ?? transform.parent?.GetComponent<AtomFunction>();
        if (atomForBondBlock == null || !atomForBondBlock.IsInteractionBlockedByBondFormation)
            SetPhysicsEnabled(true);

        if (!hadPairedDown)
            return;

        bool shortClickNoDrag = (eventData.position - pointerDownPosition).sqrMagnitude <
                                ShortClickDragThresholdPx * ShortClickDragThresholdPx;

        // Stepped debug wait: selection only (handled here); do not peel, bond, swap, or VSEPR drag.
        if (BondFormationDebugController.IsWaitingForPhase)
        {
            var editModeWait = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
            if (editModeWait != null && editModeWait.EditModeActive && bond == null && electronCount == 1 && shortClickNoDrag)
            {
                var atomSel = bondedAtom ?? transform.parent?.GetComponent<AtomFunction>();
                if (atomSel != null)
                {
                    editModeWait.OnOrbitalClicked(atomSel, this);
                    transform.localPosition = originalLocalPosition;
                    transform.localScale = originalLocalScale;
                    transform.localRotation = originalLocalRotation;
                    Reposition3DElectronsAfterOrbitalDrag();
                }
            }
            return;
        }

        if (shortClickNoDrag && bond == null && electronCount >= 1 &&
            TryPeelOneElectronForCarry(eventData.position))
        {
            transform.localPosition = originalLocalPosition;
            transform.localScale = originalLocalScale;
            transform.localRotation = originalLocalRotation;
            Reposition3DElectronsAfterOrbitalDrag();
            return;
        }

        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        if (editMode != null && editMode.EditModeActive && bond == null && electronCount == 1
            && shortClickNoDrag)
        {
            var atom = bondedAtom ?? transform.parent?.GetComponent<AtomFunction>();
            if (atom != null)
            {
                editMode.OnOrbitalClicked(atom, this);
                transform.localPosition = originalLocalPosition;
                transform.localScale = originalLocalScale;
                transform.localRotation = originalLocalRotation;
                return;
            }
        }

        if (bond != null)
        {
            TryBreakBond(tip);
            return;
        }

        var sourceAtom = bondedAtom ?? transform.parent?.GetComponent<AtomFunction>();
        var targetAtom = AtomFunction.FindBondPartner(sourceAtom, tip, this);
        if (targetAtom != null)
        {
            var targetOrbital = targetAtom.GetAvailableLoneOrbitalForBond(this, electronCount, tip);
            if (targetOrbital != null)
            {
                bool alreadyBonded = sourceAtom.GetBondsTo(targetAtom) >= 1;
                if (alreadyBonded)
                {
                    if (FormCovalentBondPiStart(sourceAtom, targetAtom, targetOrbital))
                        return;
                }
                else if (FormCovalentBondSigmaStart(sourceAtom, targetAtom, targetOrbital, tip))
                {
                    return;
                }
            }
        }

        var swapTarget = TryFindSwapTarget(sourceAtom, tip);
        if (swapTarget != null)
        {
            SwapPositionsWith(swapTarget);
            return;
        }

        ApplyVseprRotationAfterDrag(sourceAtom, tip);
    }

    const float RotateStepDeg = 30f;

    /// <summary>
    /// After a lone-orbital drag (no bond/swap): σ neighbors ≥ 2 — cannot reorient; restore drag start.
    /// 0 σ — rigid rotation of all lone lobes (VSEPR shape preserved). 1 σ — spin lone lobes around the σ axis; bonded orbital fixed.
    /// </summary>
    void ApplyVseprRotationAfterDrag(AtomFunction atom, Vector3 tipWorld)
    {
        if (atom == null) return;
        int sigmaN = atom.GetDistinctSigmaNeighborCount();
        if (sigmaN >= 2)
        {
            transform.localPosition = originalLocalPosition;
            transform.localScale = originalLocalScale;
            transform.localRotation = originalLocalRotation;
            Reposition3DElectronsAfterOrbitalDrag();
            return;
        }

        if (sigmaN == 0)
        {
            ApplyRigidWorldRotationToAllLoneOrbitals(atom, this, tipWorld);
            RefreshAllOrbitalElectronSlotPositions3D(atom);
            return;
        }

        // Exactly one σ neighbor: spin lone lobes around bond axis; σ orbital stays fixed.
        if (ApplyLoneOrbitalSpinAroundSigmaAxis(atom, this, tipWorld))
            RefreshAllOrbitalElectronSlotPositions3D(atom);
        else
            RestoreDraggedOrbitalToPointerDown();
    }

    void RestoreDraggedOrbitalToPointerDown()
    {
        transform.localPosition = originalLocalPosition;
        transform.localScale = originalLocalScale;
        transform.localRotation = originalLocalRotation;
        Reposition3DElectronsAfterOrbitalDrag();
    }

    static void RefreshAllOrbitalElectronSlotPositions3D(AtomFunction atom)
    {
        if (!Use3DOrbitalPresentation()) return;
        foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            foreach (var e in orb.GetComponentsInChildren<ElectronFunction>())
                e.ApplyOrbitalSlotPosition();
        }
    }

    static Vector3 WorldRadialFromLocalRotation(AtomFunction atom, Quaternion localRot)
    {
        Vector3 d = atom.transform.TransformDirection(localRot * Vector3.right);
        return d.sqrMagnitude < 1e-10f ? Vector3.right : d.normalized;
    }

    static Vector3 WorldOrbitalRadialDirection(Transform orbital, AtomFunction atom)
    {
        return WorldRadialFromLocalRotation(atom, orbital.localRotation);
    }

    /// <summary>Radial direction of this lone lobe at pointer-down (drag only moves position; rotation matches original for dragged).</summary>
    static Vector3 LoneOrbitalRadialWorldAtDragStart(AtomFunction atom, ElectronOrbitalFunction orb, ElectronOrbitalFunction dragged)
    {
        if (orb == dragged)
            return WorldRadialFromLocalRotation(atom, dragged.originalLocalRotation);
        return WorldOrbitalRadialDirection(orb.transform, atom);
    }

    static Vector3 WorldTargetDirectionFromTip(AtomFunction atom, Vector3 tipWorld)
    {
        Vector3 v = tipWorld - atom.transform.position;
        if (v.sqrMagnitude < 1e-10f) return Vector3.right;
        return v.normalized;
    }

    static Quaternion SnappedRotationBetweenWorldDirections(Vector3 fromW, Vector3 toW, float stepDeg)
    {
        if (fromW.sqrMagnitude < 1e-10f || toW.sqrMagnitude < 1e-10f) return Quaternion.identity;
        fromW.Normalize();
        toW.Normalize();
        float dot = Mathf.Clamp(Vector3.Dot(fromW, toW), -1f, 1f);
        float angle = Mathf.Acos(dot) * Mathf.Rad2Deg;
        if (angle < 0.01f) return Quaternion.identity;
        Vector3 axis = Vector3.Cross(fromW, toW);
        if (axis.sqrMagnitude < 1e-10f)
        {
            axis = Mathf.Abs(fromW.z) < 0.9f ? Vector3.Cross(fromW, Vector3.forward) : Vector3.Cross(fromW, Vector3.right);
            if (axis.sqrMagnitude < 1e-10f) return Quaternion.identity;
        }
        axis.Normalize();
        float snapped = Mathf.Round(angle / stepDeg) * stepDeg;
        if (snapped < 0.01f) return Quaternion.identity;
        return Quaternion.AngleAxis(snapped, axis);
    }

    static void ApplyRigidWorldRotationToAllLoneOrbitals(AtomFunction atom, ElectronOrbitalFunction dragged, Vector3 tipWorld)
    {
        Vector3 dDragW = WorldRadialFromLocalRotation(atom, dragged.originalLocalRotation);
        Vector3 dTargetW = WorldTargetDirectionFromTip(atom, tipWorld);
        Quaternion rWorld = SnappedRotationBetweenWorldDirections(dDragW, dTargetW, RotateStepDeg);

        foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != atom.transform) continue;
            if (orb.Bond != null) continue;
            Vector3 dW = LoneOrbitalRadialWorldAtDragStart(atom, orb, dragged);
            Vector3 dNewW = (rWorld * dW).normalized;
            Vector3 localDir = atom.transform.InverseTransformDirection(dNewW);
            if (localDir.sqrMagnitude < 1e-10f) continue;
            localDir.Normalize();
            var specDrag = NucleusLobeSpec.ForCanonicalSlotAlongTipNoRollHint(localDir, atom.BondRadius);
            NucleusLobePose.ApplyToNucleusChild(atom, orb, specDrag);
        }
    }

    static bool ApplyLoneOrbitalSpinAroundSigmaAxis(AtomFunction atom, ElectronOrbitalFunction dragged, Vector3 tipWorld)
    {
        if (!atom.TryGetSingleSigmaNeighbor(out var neighbor) || neighbor == null) return false;

        Vector3 sigmaW = neighbor.transform.position - atom.transform.position;
        if (sigmaW.sqrMagnitude < 1e-10f) return false;
        sigmaW.Normalize();

        Vector3 dDragW = WorldRadialFromLocalRotation(atom, dragged.originalLocalRotation);
        Vector3 dTargetW = WorldTargetDirectionFromTip(atom, tipWorld);

        Vector3 u0 = dDragW - sigmaW * Vector3.Dot(dDragW, sigmaW);
        Vector3 u1 = dTargetW - sigmaW * Vector3.Dot(dTargetW, sigmaW);
        if (u0.sqrMagnitude < 1e-8f || u1.sqrMagnitude < 1e-8f)
            return false;
        u0.Normalize();
        u1.Normalize();

        float delta = Vector3.SignedAngle(u0, u1, sigmaW);
        float snapped = Mathf.Round(delta / RotateStepDeg) * RotateStepDeg;
        Quaternion rWorld = Mathf.Abs(snapped) < 0.01f ? Quaternion.identity : Quaternion.AngleAxis(snapped, sigmaW);

        foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != atom.transform) continue;
            if (orb.Bond != null) continue;
            Vector3 dW = LoneOrbitalRadialWorldAtDragStart(atom, orb, dragged);
            Vector3 dNewW = (rWorld * dW).normalized;
            Vector3 localDir = atom.transform.InverseTransformDirection(dNewW);
            if (localDir.sqrMagnitude < 1e-10f) continue;
            localDir.Normalize();
            var specSpin = NucleusLobeSpec.ForCanonicalSlotAlongTipNoRollHint(localDir, atom.BondRadius);
            NucleusLobePose.ApplyToNucleusChild(atom, orb, specSpin);
        }
        return true;
    }

    /// <summary>
    /// Programmatic lone-lobe aim with exactly one σ neighbor: same snapped spin as <see cref="ApplyLoneOrbitalSpinAroundSigmaAxis"/>
    /// after a drag (<see cref="ApplyVseprRotationAfterDrag"/>), but uses <b>current</b> lobe directions (no pointer-down cache).
    /// <paramref name="tipWorld"/> is any world point defining the desired radial (same as drag tip).
    /// </summary>
    public static bool TrySpinLoneOrbitalsAroundSingleSigmaFromWorldTip(
        AtomFunction atom,
        ElectronOrbitalFunction referenceLoneNonbond,
        Vector3 tipWorld)
    {
        if (atom == null || referenceLoneNonbond == null || referenceLoneNonbond.Bond != null) return false;
        if (!atom.TryGetSingleSigmaNeighbor(out var neighbor) || neighbor == null) return false;

        Vector3 sigmaW = neighbor.transform.position - atom.transform.position;
        if (sigmaW.sqrMagnitude < 1e-10f) return false;
        sigmaW.Normalize();

        Vector3 dDragW = WorldOrbitalRadialDirection(referenceLoneNonbond.transform, atom);
        Vector3 dTargetW = WorldTargetDirectionFromTip(atom, tipWorld);

        Vector3 u0 = dDragW - sigmaW * Vector3.Dot(dDragW, sigmaW);
        Vector3 u1 = dTargetW - sigmaW * Vector3.Dot(dTargetW, sigmaW);
        if (u0.sqrMagnitude < 1e-8f || u1.sqrMagnitude < 1e-8f)
            return false;
        u0.Normalize();
        u1.Normalize();

        float delta = Vector3.SignedAngle(u0, u1, sigmaW);
        float snapped = Mathf.Round(delta / RotateStepDeg) * RotateStepDeg;
        Quaternion rWorld = Mathf.Abs(snapped) < 0.01f ? Quaternion.identity : Quaternion.AngleAxis(snapped, sigmaW);

        foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != atom.transform) continue;
            if (orb.Bond != null) continue;
            Vector3 dW = WorldOrbitalRadialDirection(orb.transform, atom);
            Vector3 dNewW = (rWorld * dW).normalized;
            Vector3 localDir = atom.transform.InverseTransformDirection(dNewW);
            if (localDir.sqrMagnitude < 1e-10f) continue;
            localDir.Normalize();
            var specSpin = NucleusLobeSpec.ForCanonicalSlotAlongTipNoRollHint(localDir, atom.BondRadius);
            NucleusLobePose.ApplyToNucleusChild(atom, orb, specSpin);
        }

        RefreshAllOrbitalElectronSlotPositions3D(atom);
        return true;
    }

    ElectronOrbitalFunction TryFindSwapTarget(AtomFunction atom, Vector3 tip)
    {
        if (atom == null || bond != null) return null;
        var cam = Camera.main;
        var orbitals = atom.GetComponentsInChildren<ElectronOrbitalFunction>();
        ElectronOrbitalFunction best = null;
        float bestD = float.MaxValue;
        foreach (var orb in orbitals)
        {
            if (orb == this || orb.Bond != null) continue;
            if ((ElectronCount == 0) != (orb.ElectronCount == 0)) continue;
            bool hit3d = orb.ContainsPoint(tip);
            bool hitView = cam != null && OrbitalViewOverlaps(cam, this, orb);
            if (!hit3d && !hitView) continue;
            if (cam == null)
                return orb;

            Vector3 st = cam.WorldToScreenPoint(tip);
            Vector3 so = cam.WorldToScreenPoint(orb.transform.position);
            float d = st.z >= cam.nearClipPlane && so.z >= cam.nearClipPlane
                ? ((Vector2)st - (Vector2)so).sqrMagnitude
                : (hit3d ? 0f : float.MaxValue);
            if (d < bestD)
            {
                bestD = d;
                best = orb;
            }
        }

        return best;
    }

    void SwapPositionsWith(ElectronOrbitalFunction other)
    {
        transform.localPosition = other.transform.localPosition;
        transform.localRotation = other.transform.localRotation;
        other.transform.localPosition = originalLocalPosition;
        other.transform.localRotation = originalLocalRotation;
    }

    /// <summary>
    /// Same eligibility as lone-on-lone drag <see cref="TryFindSwapTarget"/>: both occupied or both empty; exchange current
    /// local poses (symmetric). Drag uses <see cref="SwapPositionsWith"/> with pointer-down originals on the drop target.
    /// </summary>
    public static bool TrySwapNonbondNucleusChildLocalPoses(ElectronOrbitalFunction a, ElectronOrbitalFunction b)
    {
        if (a == null || b == null || ReferenceEquals(a, b)) return false;
        if (a.Bond != null || b.Bond != null) return false;
        if ((a.ElectronCount == 0) != (b.ElectronCount == 0)) return false;
        if (a.transform.parent == null || a.transform.parent != b.transform.parent) return false;
        var atom = a.transform.parent.GetComponent<AtomFunction>();
        if (atom == null) return false;

        Vector3 lpA = a.transform.localPosition;
        Quaternion lrA = a.transform.localRotation;
        Vector3 lsA = a.transform.localScale;
        a.transform.localPosition = b.transform.localPosition;
        a.transform.localRotation = b.transform.localRotation;
        a.transform.localScale = b.transform.localScale;
        b.transform.localPosition = lpA;
        b.transform.localRotation = lrA;
        b.transform.localScale = lsA;

        RefreshAllOrbitalElectronSlotPositions3D(atom);
        atom.RefreshCharge();
        return true;
    }

    /// <summary>
    /// Bond-break by drag counts only when dropping on a partner that can house another nucleus orbital after
    /// cleavage, and the pointer tip is near that nucleus in 3D or overlaps a lone lobe (same idea as bond formation).
    /// Screen-space overlap with a padded nucleus AABB alone is too loose — drops in “empty” space still matched a partner.
    /// </summary>
    bool BondBreakDropTargetsPartnerAtom(AtomFunction atom, Vector3 tip, float nucleusSlopDistance)
    {
        if (atom == null || !atom.CanAcceptOrbital()) return false;
        if (Vector3.Distance(tip, atom.transform.position) <= nucleusSlopDistance)
            return true;
        var cam = Camera.main;
        foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>(true))
        {
            if (orb == null || orb.Bond != null) continue;
            if (orb.transform.parent != atom.transform) continue;
            if (orb.ContainsPoint(tip)) return true;
            if (cam != null && OrbitalViewOverlaps(cam, this, orb)) return true;
        }
        return false;
    }

    void TryBreakBond(Vector3 tip)
    {
        if (bond == null) return;
        var a = bond.AtomA;
        var b = bond.AtomB;
        if (a == null || b == null) return;
        float rA = a.BondRadius * 1.2f;
        float rB = b.BondRadius * 1.2f;
        float da = Vector3.Distance(tip, a.transform.position);
        float db = Vector3.Distance(tip, b.transform.position);
        var cam = Camera.main;
        bool hitA = BondBreakDropTargetsPartnerAtom(a, tip, rA);
        bool hitB = BondBreakDropTargetsPartnerAtom(b, tip, rB);

        AtomFunction returnTo = null;
        if (hitA && hitB)
        {
            if (cam != null)
            {
                Vector3 st = cam.WorldToScreenPoint(tip);
                Vector3 sa = cam.WorldToScreenPoint(a.transform.position);
                Vector3 sb = cam.WorldToScreenPoint(b.transform.position);
                if (st.z >= cam.nearClipPlane && sa.z >= cam.nearClipPlane && sb.z >= cam.nearClipPlane)
                {
                    float scoreA = ((Vector2)st - (Vector2)sa).sqrMagnitude;
                    float scoreB = ((Vector2)st - (Vector2)sb).sqrMagnitude;
                    returnTo = scoreA <= scoreB ? a : b;
                }
                else
                    returnTo = da <= db ? a : b;
            }
            else
                returnTo = da <= db ? a : b;
        }
        else if (hitA)
            returnTo = a;
        else if (hitB)
            returnTo = b;

        if (returnTo != null)
        {
            var other = returnTo == a ? b : a;
            float enDrag = AtomFunction.GetElectronegativity(returnTo.AtomicNumber);
            float enOther = AtomFunction.GetElectronegativity(other.AtomicNumber);
            if (enDrag < enOther)
            {
                returnTo.FlashChargeLabelInvalidDragFade();
                transform.localPosition = originalLocalPosition;
                transform.localScale = originalLocalScale;
                transform.localRotation = originalLocalRotation;
                bond.ReturnToLineView();
                return;
            }
            bond.BreakBond(returnTo, userDragBondCylinderBreak: true);
        }
        else
        {
            transform.localPosition = originalLocalPosition;
            transform.localScale = originalLocalScale;
            transform.localRotation = originalLocalRotation;
            bond.ReturnToLineView();
        }
    }

    /// <summary>Snaps this orbital back to its original local position/rotation/scale (before any drag).</summary>
    public void SnapToOriginal()
    {
        transform.localPosition = originalLocalPosition;
        transform.localRotation = originalLocalRotation;
        transform.localScale = originalLocalScale;
    }

    [Tooltip("σ orbital-drag phase 1 (pre-bond): non-guide fragment approach toward guide op (seconds).")]
    [SerializeField] float sigmaFormationPhase1PrebondSeconds = 1f;
    [Tooltip("σ orbital-drag phase 2a (π step 2 cylinder lerp): lerp operation orbitals toward bond-cylinder pose (seconds).")]
    [SerializeField] float sigmaFormationPhase2CylinderSeconds = 1f;
    [Tooltip("σ orbital-drag phase 2b (π orbital→line): bond orbital→line + fade partner (seconds). If ~0, π path uses Phase 3 Post-bond seconds.")]
    [SerializeField] float sigmaFormationPhase2OrbitalToLineSeconds = 1f;
    [Tooltip("σ phase 3: guide nucleus orbital lerp; π: fallback orbital→line when phase 2 is ~0 (legacy bondAnimStep3).")]
    [SerializeField] float sigmaFormationPhase3PostbondGuideSeconds = 1f;

    public float SigmaFormationPhase1PrebondSeconds => sigmaFormationPhase1PrebondSeconds;
    public float SigmaFormationPhase3PostbondGuideSeconds => sigmaFormationPhase3PostbondGuideSeconds;

    /// <summary>σ drag phase 2a duration (clamp to non-negative).</summary>
    public float SigmaFormationPhase2CylinderSecondsResolved =>
        Mathf.Max(0f, sigmaFormationPhase2CylinderSeconds);

    /// <summary>σ drag phase 2b duration (clamp to non-negative).</summary>
    public float SigmaFormationPhase2OrbitalToLineSecondsResolved =>
        Mathf.Max(0f, sigmaFormationPhase2OrbitalToLineSeconds);

    float BondAnimOrbitalToLineDuration =>
        SigmaFormationPhase2OrbitalToLineSecondsResolved > 1e-6f
            ? SigmaFormationPhase2OrbitalToLineSecondsResolved
            : sigmaFormationPhase3PostbondGuideSeconds;

    /// <summary>σ/π drag formation: orbital→line duration (phase 2b, or phase 3 when 2b ~0).</summary>
    public float BondFormationOrbitalToLineDurationResolved => BondAnimOrbitalToLineDuration;

    /// <summary>σ bond from orbital drag: <see cref="SigmaBondFormation"/> runs phase 1 approach, bond animation, then post-bond guide lerp (independent of edit mode).</summary>
    bool FormCovalentBondSigmaStart(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3 dropPosition, bool alreadyFlipped = false)
    {
        _ = dropPosition;
        bool sourceAlreadyAligned = IsSourceOrbitalAlreadyAlignedWithTarget(sourceAtom, targetOrbital);
        bool cannotRearrangeSource = !sourceAlreadyAligned && (sourceAtom.GetPiBondCount() > 0 || IsSourceFlippedSideFilled(sourceAtom, targetAtom, targetOrbital));
        if (sourceAtom.CovalentBonds.Count > 0 && cannotRearrangeSource)
        {
            if (alreadyFlipped)
                return false;
            return targetOrbital.FormCovalentBondSigmaStartAsSource(targetAtom, sourceAtom, this, dropPosition);
        }

        // σ drag: restore both lobes to pointer-down originals before any template / nTarget / coroutine reads transforms
        // (drop pose is mouseup; originals are set on mousedown for each orbital that was pressed).
        SnapToOriginal();
        // The drop target orbital is not the dragged lobe in this path; forcing SnapToOriginal here can
        // incorrectly recenter it if its cached original pose is stale/uninitialized for this gesture.

        var sigmaFormation = SigmaBondFormation.EnsureRunnerInScene();
        if (sigmaFormation != null
            && sigmaFormation.TryBeginOrbitalDragSigmaFormation(
                sourceAtom,
                targetAtom,
                this,
                targetOrbital,
                redistributionGuideTieBreakDraggedOrbital: this))
            return true;

        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        if (editMode == null) return false;
        editMode.FormSigmaBondInstant(sourceAtom, targetAtom, this, targetOrbital);
        return true;
    }

    bool FormCovalentBondSigmaStartAsSource(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction draggedOrbital, Vector3 dropPosition)
    {
        _ = dropPosition;
        draggedOrbital.SnapToOriginal();
        // In this branch, draggedOrbital is the lobe that was actively dragged; `this` is the counterpart.
        // Do not snap `this` from potentially stale cached original pose.
        var sigmaFormation = SigmaBondFormation.EnsureRunnerInScene();
        if (sigmaFormation != null
            && sigmaFormation.TryBeginOrbitalDragSigmaFormation(
                sourceAtom,
                targetAtom,
                this,
                draggedOrbital,
                redistributionGuideTieBreakDraggedOrbital: draggedOrbital))
            return true;

        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        if (editMode == null) return false;
        editMode.FormSigmaBondInstant(sourceAtom, targetAtom, this, draggedOrbital);
        return true;
    }

    bool FormCovalentBondPiStart(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        var runner = SigmaBondFormation.EnsureRunnerInScene();
        if (runner != null)
        {
            runner.EnsureEditModeManagerReference();
            if (runner.TryBeginOrbitalDragPiFormation(sourceAtom, targetAtom, this, targetOrbital, this))
                return true;
        }
        targetAtom.StartCoroutine(FormCovalentBondPiCoroutine(sourceAtom, targetAtom, targetOrbital));
        return true;
    }

    IEnumerator FormCovalentBondPiCoroutine(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock) a.SetInteractionBlocked(true);

        AtomFunction guide = null;
        AtomFunction nonGuide = null;
        try
        {
        int ecnPiEvent = AtomFunction.AllocateMoleculeEcnEventId();
        _ = sourceAtom.GetPiBondCount();
        _ = targetAtom.GetPiBondCount();
        _ = sourceAtom.GetDistinctSigmaNeighborCount();
        _ = targetAtom.GetDistinctSigmaNeighborCount();
        int mergedElectrons = electronCount + targetOrbital.ElectronCount;

        // Snap source orbital to original position (like sigma bond) before animating to bond center
        transform.localPosition = originalLocalPosition;
        transform.localRotation = originalLocalRotation;
        transform.localScale = originalLocalScale;

        var molForBondLineUpdates = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) if (a != null) molForBondLineUpdates.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) if (a != null) molForBondLineUpdates.Add(a);
        var molForBondLineList = new List<AtomFunction>(molForBondLineUpdates.Count);
        foreach (var a in molForBondLineUpdates)
            if (a != null)
                molForBondLineList.Add(a);

        OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
        ElectronRedistributionGuide.ResolveGuideAtomForPair(sourceAtom, targetAtom, this, out guide, out nonGuide);
        bool guideRedistributedInPhase1 = false;
        if (guide != null && nonGuide != null && !ReferenceEquals(guide, nonGuide))
        {
            var guideOpPhase1 = guide == sourceAtom ? this : targetOrbital;
            var nonGuideOpPhase1 = nonGuide == sourceAtom ? this : targetOrbital;
            guideRedistributedInPhase1 = SigmaBondFormation.RunOrbitalDragPiPhase1RedistributionSynchronously(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
                sourceAtom,
                targetAtom,
                this,
                targetOrbital,
                molForBondLineList);
        }
        
        AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
            sourceAtom, targetAtom, "beforePiBond", ecnPiEvent, null, "pi");
        sourceAtom.UnbondOrbital(this);
        targetAtom.UnbondOrbital(targetOrbital);

        var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: true);
        transform.SetParent(null); // Detach source orbital but keep visible for post-Create formation animation (preserves world pos from snap)
        bond.SetOrbitalBeingFaded(this); // Use merged count for charge during animation (source orbital is being faded)
        sourceAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        targetAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        yield return AnimateBondFormationOperationOrbitalsTowardBondCylinder(
            sourceAtom, targetAtom, this, targetOrbital, bond, -1f, molForBondLineUpdates);

        if (bond != null)
        {
            bond.animatingOrbitalToBondPosition = false;
            yield return bond.AnimateOrbitalToLine(BondFormationOrbitalToLineDurationResolved, this, molForBondLineUpdates);
            targetOrbital.ElectronCount = mergedElectrons; // Show merged electrons only after orbital-to-line
        }
        else
        {
            Destroy(gameObject);
        }
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();
        if (bond != null)
            AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
                sourceAtom, targetAtom, "afterPiBond", ecnPiEvent, bond, "pi");

        float phase3Sec = SigmaBondFormation.ResolvePiPhase3GuideSeconds(this, targetOrbital, this);
        if (bond != null
            && guide != null
            && nonGuide != null
            && !ReferenceEquals(guide, nonGuide)
            && guide.AtomicNumber > 1
            && phase3Sec > 1e-5f
            && !guideRedistributedInPhase1)
        {
            var guideOpPhase1 = guide == sourceAtom ? this : targetOrbital;
            var phase3GuideOp = bond.Orbital != null ? bond.Orbital : guideOpPhase1;
            var phase3GuideRedistribution = OrbitalRedistribution.BuildOrbitalRedistribution(
                guide,
                nonGuide,
                atomOrbitalOp: phase3GuideOp,
                isBondingEvent: true);
            if (phase3GuideRedistribution != null)
            {
                float t3 = 0f;
                while (t3 < phase3Sec)
                {
                    t3 += Time.deltaTime;
                    float s3 = Mathf.Clamp01(t3 / phase3Sec);
                    float smooth3 = s3 * s3 * (3f - 2f * s3);
                    phase3GuideRedistribution.Apply(smooth3);
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
                    yield return null;
                }
                phase3GuideRedistribution.Apply(1f);
                AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
            }
        }
        }
        finally
        {
            OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
            guide?.RemovePiPOrbitalDirectionsForPartnerLine(nonGuide, AtomFunction.PiPOrbitalPrebondLineIndex);
            nonGuide?.RemovePiPOrbitalDirectionsForPartnerLine(guide, AtomFunction.PiPOrbitalPrebondLineIndex);
            foreach (var a in atomsToBlock) a.SetInteractionBlocked(false);
            UnityEngine.Object.FindFirstObjectByType<EditModeManager>()?.RefreshSelectedMoleculeAfterBondChange();
        }
    }

    /// <summary>Prepared σ/π cylinder lerp: shared by timed coroutine and phase-1 parallel tracks.</summary>
    public struct BondFormationCylinderPrepared
    {
        public Vector3 BondTargetPos;
        public Vector3 SourceTargetPos;
        public Vector3 TargetTargetPos;
        public Quaternion SourceTargetRot;
        public Quaternion TargetTargetRot;
        public (Vector3 pos, Quaternion rot) SourceOrbStart;
        public (Vector3 pos, Quaternion rot) TargetOrbStart;
        /// <summary>World pivot for source OP position lerp: arc around nucleus instead of a straight chord through it.</summary>
        public Vector3 SourceArcPivot;
        /// <summary>World pivot for target OP position lerp (see <see cref="SourceArcPivot"/>).</summary>
        public Vector3 TargetArcPivot;
        public bool SkipCylinderLerp;
    }

    public enum BondFormationCylinderFinalizeMode
    {
        /// <summary>Reparent to bond and <see cref="CovalentBond.SnapOrbitalToBondPosition"/> (post–π-bond Create).</summary>
        AttachAndSnapToBond,
        /// <summary>Set world pose only (π phase 1 pre-bond, σ still the only line between atoms).</summary>
        ApplyWorldTargetsOnly,
    }

    /// <summary>Computes bond-cylinder targets and whether both operation orbitals already match them (no timed lerp needed).</summary>
    /// <param name="piPreBondSecondLineOnSigma">When true, <paramref name="bond"/> must be the σ line between the pair; targets use π line offset as if a second bond exists, without mutating σ’s <see cref="CovalentBond.orbitalRotationFlipped"/> during prep.</param>
    static bool TryPrepareBondFormationCylinderStep(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        CovalentBond bond,
        bool piPreBondSecondLineOnSigma,
        out BondFormationCylinderPrepared prepared)
    {
        prepared = default;
        if (bond == null) return false;

        // π phase-1 on σ: line index is the row the new π will occupy (1 = first π, 2 = second π / triple), and
        // bondCount includes that new row so perspective endpoints and offsets match post–Create geometry.
        int bondsBetween = bond.AtomA != null && bond.AtomB != null ? bond.AtomA.GetBondsTo(bond.AtomB) : 0;
        int lineIndex = piPreBondSecondLineOnSigma && bond.IsSigmaBondLine()
            ? bondsBetween
            : bond.GetBondIndex();
        int bondCount = piPreBondSecondLineOnSigma && bond.IsSigmaBondLine()
            ? Mathf.Max(2, bondsBetween + 1)
            : bondsBetween;

        bool rotFlip = bond.orbitalRotationFlipped;
        var bt = bond.GetOrbitalTargetWorldStateForLine(lineIndex, bondCount, rotFlip);
        Vector3 bondTargetPos = bt.Item1;
        Quaternion bondTargetRot = bt.Item2;
        Vector3 sourceTargetPos = bondTargetPos;
        Vector3 targetTargetPos = bondTargetPos;
        if (piPreBondSecondLineOnSigma
            && bond.IsSigmaBondLine()
            && sourceAtom != null
            && targetAtom != null)
        {
            Vector3 sharedPlusZ = Vector3.zero;
            bool haveSharedPlusZ =
                sourceAtom.TryGetPiPrebondPOrbitalPlusZWorld(targetAtom, out sharedPlusZ)
                || targetAtom.TryGetPiPrebondPOrbitalPlusZWorld(sourceAtom, out sharedPlusZ);
            if (haveSharedPlusZ && sharedPlusZ.sqrMagnitude > 1e-16f)
            {
                Vector3 axis = targetAtom.transform.position - sourceAtom.transform.position;
                if (axis.sqrMagnitude > 1e-10f)
                {
                    axis.Normalize();
                    Vector3 pNormal = Vector3.ProjectOnPlane(sharedPlusZ.normalized, axis);
                    if (pNormal.sqrMagnitude > 1e-10f)
                    {
                        pNormal.Normalize();
                        float lineOffset = (lineIndex % 2 == 1 ? -1f : 1f) * ((lineIndex + 1) / 2) * 0.2f;
                        if (lineOffset < 0f)
                            pNormal = -pNormal;
                        float lobeOffset = Mathf.Max(0.05f, 0.6f * 0.5f * (sourceAtom.BondRadius + targetAtom.BondRadius));
                        Vector3 plus = pNormal * lobeOffset;
                        sourceTargetPos = sourceAtom.transform.position + plus;
                        targetTargetPos = targetAtom.transform.position + plus;
                        bondTargetPos = 0.5f * (sourceTargetPos + targetTargetPos);
                    }
                }
            }
        }

        (float sourceDiff, float targetDiff) = ComputePiBondAngleDiffs(sourceAtom, targetAtom, bondTargetPos, bondTargetRot, bond);

        bool flipTarget = Mathf.Abs(sourceDiff) < Mathf.Abs(targetDiff);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            float ad = Mathf.Abs(sourceDiff);
            float bd = Mathf.Abs(targetDiff);
            float sm = Mathf.Min(ad, bd);
            float lg = Mathf.Max(ad, bd);
            if (sm + lg > 160f && sm < 35f && lg > 145f)
                flipTarget = true;
        }

        Quaternion sourceTargetRot;
        Quaternion targetTargetRot;
        if (flipTarget)
        {
            rotFlip = true;
            bt = bond.GetOrbitalTargetWorldStateForLine(lineIndex, bondCount, rotFlip);
            bondTargetPos = bt.Item1;
            bondTargetRot = bt.Item2;
            sourceTargetRot = bondTargetRot * Quaternion.Euler(0f, 0f, 180f);
            targetTargetRot = bondTargetRot;
            if (!piPreBondSecondLineOnSigma)
                bond.orbitalRotationFlipped = true;
        }
        else
        {
            sourceTargetRot = bondTargetRot * Quaternion.Euler(0f, 0f, 180f);
            targetTargetRot = bondTargetRot;
        }

        prepared.SourceOrbStart = sourceOrbital != null
            ? (sourceOrbital.transform.position, sourceOrbital.transform.rotation)
            : (Vector3.zero, Quaternion.identity);
        prepared.TargetOrbStart = targetOrbital != null
            ? (targetOrbital.transform.position, targetOrbital.transform.rotation)
            : (Vector3.zero, Quaternion.identity);

        // Skip timed cylinder lerp when world pose is already at target: position close and lobe +X direction matches
        // (Quaternion.Angle alone rejects valid cases where direction matches but twist about the bond axis differs).
        const float sigmaPhase2SkipLerpPosEps = 0.02f;
        const float sigmaPhase2SkipLerpDirDeg = 3f;

        bool CylinderTargetDelta(
            ElectronOrbitalFunction o,
            Vector3 posW,
            Quaternion rotW,
            float posEps,
            float dirEpsDeg,
            out float posErr,
            out float quatAngleDeg,
            out float dirAngleDeg)
        {
            posErr = 0f;
            quatAngleDeg = 0f;
            dirAngleDeg = 0f;
            if (o == null)
                return true;
            posErr = Vector3.Distance(o.transform.position, posW);
            quatAngleDeg = Quaternion.Angle(o.transform.rotation, rotW);
            Vector3 curDir = OrbitalAngleUtility.GetOrbitalDirectionWorld(o.transform);
            Vector3 wantDir = (rotW * Vector3.right).normalized;
            if (wantDir.sqrMagnitude < 1e-12f)
                wantDir = curDir;
            dirAngleDeg = Vector3.Angle(curDir, wantDir);
            return posErr <= posEps && dirAngleDeg <= dirEpsDeg;
        }

        bool srcMatch = CylinderTargetDelta(
            sourceOrbital,
            sourceTargetPos,
            sourceTargetRot,
            sigmaPhase2SkipLerpPosEps,
            sigmaPhase2SkipLerpDirDeg,
            out float srcPosErr,
            out float srcQuatDeg,
            out float srcDirDeg);
        bool tgtMatch = CylinderTargetDelta(
            targetOrbital,
            targetTargetPos,
            targetTargetRot,
            sigmaPhase2SkipLerpPosEps,
            sigmaPhase2SkipLerpDirDeg,
            out float tgtPosErr,
            out float tgtQuatDeg,
            out float tgtDirDeg);

        prepared.BondTargetPos = bondTargetPos;
        prepared.SourceTargetPos = sourceTargetPos;
        prepared.TargetTargetPos = targetTargetPos;
        prepared.SourceTargetRot = sourceTargetRot;
        prepared.TargetTargetRot = targetTargetRot;
        prepared.SourceArcPivot = sourceAtom != null ? sourceAtom.transform.position : prepared.SourceOrbStart.pos;
        prepared.TargetArcPivot = targetAtom != null ? targetAtom.transform.position : prepared.TargetOrbStart.pos;
        prepared.SkipCylinderLerp = srcMatch && tgtMatch;

        return true;
    }

    /// <summary>
    /// Interpolates orbital mesh center from start to end by sweeping around <paramref name="pivot"/> (nucleus):
    /// slerp the outward direction and lerp radius so the path follows a spherical arc instead of cutting through the atom.
    /// </summary>
    static Vector3 LerpOrbitalMeshCenterOnNucleusPivotArc(Vector3 pivot, Vector3 startWorld, Vector3 endWorld, float t)
    {
        t = Mathf.Clamp01(t);
        Vector3 vs = startWorld - pivot;
        Vector3 ve = endWorld - pivot;
        float ls = vs.magnitude;
        float le = ve.magnitude;
        const float eps = 1e-4f;
        if (ls < eps || le < eps)
            return Vector3.Lerp(startWorld, endWorld, t);
        Vector3 dirs = vs / ls;
        Vector3 dire = ve / le;
        float r = Mathf.Lerp(ls, le, t);
        Vector3 dir;
        if (Vector3.Dot(dirs, dire) > -0.9995f)
            dir = Vector3.Slerp(dirs, dire, t);
        else
        {
            Vector3 ortho = Vector3.Cross(dirs, Mathf.Abs(dirs.y) < 0.92f ? Vector3.up : Vector3.forward);
            if (ortho.sqrMagnitude < 1e-10f)
                ortho = Vector3.Cross(dirs, Vector3.right);
            ortho.Normalize();
            dir = (Quaternion.AngleAxis(180f * t, ortho) * dirs).normalized;
        }

        return pivot + r * dir;
    }

    /// <summary>π phase 1 (pre–π bond): cylinder targets on the σ line using second-line offset; does not flip σ’s stored flag during prep.</summary>
    public static bool TryPreparePiPhase1CylinderFromSigmaBond(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        CovalentBond sigmaBondBetweenEndpoints,
        out BondFormationCylinderPrepared prepared)
    {
        return TryPrepareBondFormationCylinderStep(
            sourceAtom,
            targetAtom,
            sourceOrbital,
            targetOrbital,
            sigmaBondBetweenEndpoints,
            piPreBondSecondLineOnSigma: true,
            out prepared);
    }

    /// <summary>One smoothstep sample <paramref name="smoothS"/> in [0,1] (already smoothstepped) for parallel phase-1 tracks.</summary>
    public static void ApplyBondFormationCylinderPoseForSmoothStep(
        BondFormationCylinderPrepared prepared,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        float smoothS)
    {
        float s = Mathf.Clamp01(smoothS);
        float rotT = prepared.SkipCylinderLerp ? 1f : s;
        if (prepared.SkipCylinderLerp)
            s = 1f;
        if (sourceOrbital != null)
        {
            sourceOrbital.transform.position = LerpOrbitalMeshCenterOnNucleusPivotArc(
                prepared.SourceArcPivot,
                prepared.SourceOrbStart.pos,
                prepared.SourceTargetPos,
                s);
            sourceOrbital.transform.rotation = Quaternion.Slerp(prepared.SourceOrbStart.rot, prepared.SourceTargetRot, rotT);
        }
        if (targetOrbital != null)
        {
            targetOrbital.transform.position = LerpOrbitalMeshCenterOnNucleusPivotArc(
                prepared.TargetArcPivot,
                prepared.TargetOrbStart.pos,
                prepared.TargetTargetPos,
                s);
            targetOrbital.transform.rotation = Quaternion.Slerp(prepared.TargetOrbStart.rot, prepared.TargetTargetRot, rotT);
        }
    }

    /// <summary>End state after cylinder lerp: full attach (phase 2) or world pose only (π phase 1).</summary>
    public static void FinalizeBondFormationCylinderPose(
        BondFormationCylinderPrepared prepared,
        AtomFunction sourceAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        CovalentBond bond,
        BondFormationCylinderFinalizeMode mode,
        ICollection<AtomFunction> atomsForPerFrameBondLineUpdate)
    {
        if (sourceOrbital != null)
            sourceOrbital.transform.SetPositionAndRotation(prepared.SourceTargetPos, prepared.SourceTargetRot);
        if (targetOrbital != null)
            targetOrbital.transform.SetPositionAndRotation(prepared.TargetTargetPos, prepared.TargetTargetRot);

        if (mode == BondFormationCylinderFinalizeMode.ApplyWorldTargetsOnly)
        {
            if (atomsForPerFrameBondLineUpdate != null && atomsForPerFrameBondLineUpdate.Count > 0)
                AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(atomsForPerFrameBondLineUpdate);
            return;
        }

        if (bond == null) return;

        if (sourceOrbital != null && sourceAtom != null && sourceOrbital.transform.parent != sourceAtom.transform)
            sourceOrbital.transform.SetParent(sourceAtom.transform, worldPositionStays: true);

        if (targetOrbital != null)
            targetOrbital.transform.SetParent(bond.transform, worldPositionStays: true);
        bond.UpdateBondTransformToCurrentAtoms();
        if (atomsForPerFrameBondLineUpdate != null && atomsForPerFrameBondLineUpdate.Count > 0)
            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(atomsForPerFrameBondLineUpdate);
        if (sourceOrbital != null && !OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            var bt = bond.GetOrbitalTargetWorldState();
            sourceOrbital.transform.position = bt.Item1;
            sourceOrbital.transform.rotation = prepared.SourceTargetRot;
        }
        bond.SnapOrbitalToBondPosition();
    }

    /// <summary>Bond-formation animation only: lerp the two orbitals in this gesture toward the shared bond cylinder pose. Electron redistribution is a separate system — no hybrid refresh or σ/π redistribution hooks here.</summary>
    /// <param name="durationSeconds">If negative, uses <see cref="SigmaFormationPhase2CylinderSecondsResolved"/>. If zero or positive, uses that many seconds (σ drag may pass 0).</param>
    /// <param name="atomsForPerFrameBondLineUpdate">When non-null, updates all incident bond cylinders (σ + π) each frame — use during π formation so existing σ lines stay aligned.</param>
    public IEnumerator AnimateBondFormationOperationOrbitalsTowardBondCylinder(AtomFunction sourceAtom, AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital, ElectronOrbitalFunction targetOrbital, CovalentBond bond, float durationSeconds = -1f,
        ICollection<AtomFunction> atomsForPerFrameBondLineUpdate = null)
    {
        if (bond == null) yield break;

        float dur = durationSeconds < 0f ? SigmaFormationPhase2CylinderSecondsResolved : durationSeconds;

        if (!TryPrepareBondFormationCylinderStep(
                sourceAtom, targetAtom, sourceOrbital, targetOrbital, bond, piPreBondSecondLineOnSigma: false, out var prepared))
            yield break;

        if (prepared.SkipCylinderLerp)
            dur = 0f;

        if (!prepared.SkipCylinderLerp && dur > 1e-6f)
        {
            for (float t = 0f; t < dur; t += Time.deltaTime)
            {
                float u = Mathf.Clamp01(t / dur);
                float s = u * u * (3f - 2f * u);
                ApplyBondFormationCylinderPoseForSmoothStep(prepared, sourceOrbital, targetOrbital, s);
                if (atomsForPerFrameBondLineUpdate != null && atomsForPerFrameBondLineUpdate.Count > 0)
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(atomsForPerFrameBondLineUpdate);
                yield return null;
            }
        }

        FinalizeBondFormationCylinderPose(
            prepared,
            sourceAtom,
            sourceOrbital,
            targetOrbital,
            bond,
            BondFormationCylinderFinalizeMode.AttachAndSnapToBond,
            atomsForPerFrameBondLineUpdate);

        // π row: hide OP meshes only after cylinder step fully finishes. Always yield once before hide so we never
        // run Hide in the same MoveNext as Finalize (SkipCylinderLerp runs Finalize immediately — otherwise OP vanishes
        // the instant phase 2 starts). Timed lerp already had many frames; one extra yield is harmless.
        if (bond.GetBondIndex() > 0)
        {
            yield return null;
            SigmaBondFormation.HidePiOperationOrbitalsAfterPhase2Cylinder(bond, sourceOrbital);
        }
    }

    static (float sourceDiff, float targetDiff) ComputePiBondAngleDiffs(AtomFunction sourceAtom, AtomFunction targetAtom, Vector3 bondPos, Quaternion bondRot, CovalentBond bond)
    {
        var toCenterFromSource = bondPos - sourceAtom.transform.position;
        if (toCenterFromSource.sqrMagnitude < 0.0001f) return (0f, 0f);

        // Full 3D, no XY stomp. Internuclear rays source→partner vs target→partner are opposite; Angle(bondDir, u) and
        // Angle(bondDir, -u) are supplementary — when bondDir is nearly parallel to σ we log degenerate 180° vs 0° (bad flip).
        // Use π plane: project bondDir and atom→orbital-center onto plane ⊥ σ; fallback to full 3D center rays if projection collapses.
        if (bond != null && targetAtom != null && sourceAtom != null)
        {
            Vector3 bondDir = bondRot * Vector3.right;
            if (bondDir.sqrMagnitude < 1e-8f) return (0f, 0f);
            bondDir.Normalize();

            Vector3 sigma = targetAtom.transform.position - sourceAtom.transform.position;
            if (sigma.sqrMagnitude < 1e-8f) return (0f, 0f);
            sigma.Normalize();

            Vector3 toCenS = bondPos - sourceAtom.transform.position;
            Vector3 toCenT = bondPos - targetAtom.transform.position;
            if (toCenS.sqrMagnitude < 1e-8f || toCenT.sqrMagnitude < 1e-8f) return (0f, 0f);

            const float projEps = 1e-6f;
            Vector3 bondDirFlat = Vector3.ProjectOnPlane(bondDir, sigma);
            if (bondDirFlat.sqrMagnitude < projEps)
            {
                float sd = Vector3.Angle(bondDir, toCenS.normalized);
                float td = Vector3.Angle(bondDir, toCenT.normalized);
                return (sd, td);
            }
            bondDirFlat.Normalize();

            Vector3 perpS = Vector3.ProjectOnPlane(toCenS, sigma);
            Vector3 perpT = Vector3.ProjectOnPlane(toCenT, sigma);
            if (perpS.sqrMagnitude < projEps || perpT.sqrMagnitude < projEps)
            {
                float sd = Vector3.Angle(bondDir, toCenS.normalized);
                float td = Vector3.Angle(bondDir, toCenT.normalized);
                return (sd, td);
            }
            perpS.Normalize();
            perpT.Normalize();
            return (Vector3.Angle(bondDirFlat, perpS), Vector3.Angle(bondDirFlat, perpT));
        }

        toCenterFromSource.Normalize();
        toCenterFromSource.z = 0;
        float sourceAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenterFromSource);
        var toCenterFromTarget = bondPos - targetAtom.transform.position;
        if (toCenterFromTarget.sqrMagnitude >= 0.0001f && bond != null)
        {
            toCenterFromTarget.Normalize();
            toCenterFromTarget.z = 0;
            float targetAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenterFromTarget);
            var bondDir = bondRot * Vector3.right;
            bondDir.z = 0;
            if (bondDir.sqrMagnitude >= 0.0001f)
            {
                bondDir.Normalize();
                float bondAngle = OrbitalAngleUtility.DirectionToAngleWorld(bondDir);
                float sourceDiff = OrbitalAngleUtility.NormalizeAngle(sourceAngle - bondAngle);
                float targetDiff = OrbitalAngleUtility.NormalizeAngle(targetAngle - bondAngle);
                return (sourceDiff, targetDiff);
            }
        }
        return (0f, 0f);
    }

    static float ComputeSigmaBondAngleDiff(AtomFunction sourceAtom, AtomFunction partnerAtom, Vector3 bondPos, Quaternion bondRot)
    {
        var toCenter = bondPos - sourceAtom.transform.position;
        if (toCenter.sqrMagnitude < 0.0001f) return 0f;

        // For full 3D σ formation we should not project to XY: tetrahedral C–H geometries can have
        // meaningful Z components, and the old XY-projection can falsely trigger 180° flips.
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            // Bond visual position is offset perpendicular to the axis; use internuclear direction, not source→orbitalCenter.
            Vector3 alongPartner = toCenter.normalized;
            if (partnerAtom != null)
            {
                var toP = partnerAtom.transform.position - sourceAtom.transform.position;
                if (toP.sqrMagnitude > 1e-8f) alongPartner = toP.normalized;
            }
            var bondDir = bondRot * Vector3.right;
            if (bondDir.sqrMagnitude < 0.0001f) return 0f;
            bondDir.Normalize();
            return Vector3.Angle(alongPartner, bondDir); // [0..180]
        }

        toCenter.Normalize();
        toCenter.z = 0;
        float sourceAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenter);
        var planarBondDir = bondRot * Vector3.right;
        planarBondDir.z = 0;
        if (planarBondDir.sqrMagnitude < 0.0001f) return 0f;
        planarBondDir.Normalize();
        float bondAngle = OrbitalAngleUtility.DirectionToAngleWorld(planarBondDir);
        float diff = OrbitalAngleUtility.NormalizeAngle(sourceAngle - bondAngle);
        return diff;
    }

    /// <param name="partnerOrbital">Lone pair on the partner atom (receptor direction reference).</param>
    /// <param name="draggedOrbital">Orbital the user dragged; defaults to this when null (normal sigma start).</param>
    bool IsSourceOrbitalAlreadyAlignedWithTarget(AtomFunction sourceAtom, ElectronOrbitalFunction partnerOrbital, ElectronOrbitalFunction draggedOrbital = null)
    {
        var dragged = draggedOrbital != null ? draggedOrbital : this;
        var dragParent = dragged.transform.parent;
        if (dragParent == null || partnerOrbital == null) return false;
        float targetAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(partnerOrbital.transform);
        float oppositeAngleWorld = OrbitalAngleUtility.NormalizeAngle(targetAngleWorld + 180f);
        float draggedAngleWorld = OrbitalAngleUtility.LocalRotationToAngleWorld(dragParent, dragged.originalLocalRotation);
        const float tolerance = 45f;
        return Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(draggedAngleWorld - oppositeAngleWorld)) < tolerance;
    }

    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPositionFromLocalDirection(Vector3 localDir, float bondRadius) =>
        GetCanonicalSlotPositionFromLocalDirection(localDir, bondRadius, null, null);

    /// <param name="preferClosestLocalRotation">When set, pick 0°/90°/… rolls about orbital +X after base align (<c>AngleAxis</c> uses <c>Vector3.right</c>, not <paramref name="localDir"/>, so hybrid +X stays along <paramref name="localDir"/>).</param>
    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPositionFromLocalDirection(
        Vector3 localDir, float bondRadius, Quaternion? preferClosestLocalRotation) =>
        GetCanonicalSlotPositionFromLocalDirection(localDir, bondRadius, preferClosestLocalRotation, null);

    /// <param name="continuityPreferLocalRotation">With <paramref name="preferClosestLocalRotation"/>, choose among 90° roll buckets by minimizing
    /// <c>angle(hint,q) + 0.25·angle(continuity,q)</c>, with ties broken toward lower continuity distance (legacy continuity-first alone could lock a bucket far from <paramref name="preferClosestLocalRotation"/> when the prior frame was stale).</param>
    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPositionFromLocalDirection(
        Vector3 localDir,
        float bondRadius,
        Quaternion? preferClosestLocalRotation,
        Quaternion? continuityPreferLocalRotation)
    {
        if (localDir.sqrMagnitude < 1e-8f)
            localDir = Vector3.right;
        localDir.Normalize();
        float offset = bondRadius * 0.6f;
        var pos = localDir * offset;
        var qBase = Quaternion.FromToRotation(Vector3.right, localDir);
        if (!preferClosestLocalRotation.HasValue)
            return (pos, qBase);

        // Roll buckets satisfy hybrid +X = localDir. Map hint/continuity into that same +X coset so
        // Quaternion.Angle measures roll residual, not vector-slerp vs quat-slerp axis drift (σ phase-1 H116).
        static Quaternion AlignHybridPlusXToTipDir(Quaternion rot, Vector3 tipDirNucleusLocal)
        {
            Vector3 xAxis = (rot * Vector3.right).normalized;
            if (xAxis.sqrMagnitude < 1e-16f)
                return rot;
            if (Vector3.Angle(xAxis, tipDirNucleusLocal) < 0.02f)
                return rot;
            return Quaternion.FromToRotation(xAxis, tipDirNucleusLocal) * rot;
        }

        Quaternion hintAligned = AlignHybridPlusXToTipDir(preferClosestLocalRotation.Value, localDir);

        if (!continuityPreferLocalRotation.HasValue)
        {
            float bestAng = float.MaxValue;
            int bestK = 0;
            Quaternion bestQ = qBase;
            for (int k = 0; k < 4; k++)
            {
                var q = qBase * Quaternion.AngleAxis(k * 90f, Vector3.right);
                float a = Quaternion.Angle(hintAligned, q);
                if (a < bestAng - 0.02f || (Mathf.Abs(a - bestAng) <= 0.02f && k < bestK))
                {
                    bestAng = a;
                    bestK = k;
                    bestQ = q;
                }
            }
            return (pos, bestQ);
        }

        Quaternion contAligned = AlignHybridPlusXToTipDir(continuityPreferLocalRotation.Value, localDir);

        // Prefer the 90° roll bucket that tracks the structural hint (e.g. phase-1 slerp target)
        // while still penalizing jumps from the prior frame. Pure lexicographic continuity-first
        // picks a bucket arbitrarily close in dCont but far in dHint when restore/snap left the
        // live rotation stale vs the current lerp quaternion (σ prebond phase-1 triage H112).
        const float kRollContinuityWeight = 0.25f;
        float bestScoreCont = float.MaxValue;
        float bestDContAtScore = float.MaxValue;
        int bestKCont = 0;
        Quaternion bestQCont = qBase;
        for (int k = 0; k < 4; k++)
        {
            var q = qBase * Quaternion.AngleAxis(k * 90f, Vector3.right);
            float dCont = Quaternion.Angle(contAligned, q);
            float dHint = Quaternion.Angle(hintAligned, q);
            float score = dHint + kRollContinuityWeight * dCont;
            bool better = score < bestScoreCont - 0.02f
                || (Mathf.Abs(score - bestScoreCont) <= 0.02f && dCont < bestDContAtScore - 0.02f)
                || (Mathf.Abs(score - bestScoreCont) <= 0.02f && Mathf.Abs(dCont - bestDContAtScore) <= 0.02f && k < bestKCont);
            if (better)
            {
                bestScoreCont = score;
                bestDContAtScore = dCont;
                bestKCont = k;
                bestQCont = q;
            }
        }
        return (pos, bestQCont);
    }

    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPosition(float angleDeg, float bondRadius)
    {
        float angle = NormalizeAngle(angleDeg);
        float rad = angle * Mathf.Deg2Rad;
        var dir = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f);
        return GetCanonicalSlotPositionFromLocalDirection(dir, bondRadius);
    }

    public static float NormalizeAngle(float deg)
    {
        while (deg > 180f) deg -= 360f;
        while (deg < -180f) deg += 360f;
        return deg;
    }

    bool IsSourceFlippedSideFilled(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3? orbitalTipOverride = null)
    {
        var targetPos = targetAtom.transform.position;
        var orbitalTip = orbitalTipOverride ?? targetOrbital.transform.position;
        var targetOrbitalDir = (orbitalTip - targetPos).normalized;
        float targetOrbitalAngle = OrbitalAngleUtility.DirectionToAngleWorld(targetOrbitalDir);
        float flippedAngle = OrbitalAngleUtility.NormalizeAngle(targetOrbitalAngle + 180f);
        const float toleranceDeg = 45f;
        var sourcePos = sourceAtom.transform.position;

        foreach (var b in sourceAtom.CovalentBonds)
        {
            if (b == null) continue;
            var other = b.AtomA == sourceAtom ? b.AtomB : b.AtomA;
            if (other == null) continue;
            var dirToOther = (other.transform.position - sourcePos).normalized;
            float bondAngleDeg = OrbitalAngleUtility.DirectionToAngleWorld(dirToOther);
            float bondAngleNorm = OrbitalAngleUtility.NormalizeAngle(bondAngleDeg);
            float delta = NormalizeAngle(bondAngleNorm - flippedAngle);
            float absDelta = Mathf.Abs(delta);
            bool isFlippedSide = absDelta < toleranceDeg;

            if (isFlippedSide)
                return true;
        }
        return false;
    }

    void AlignSourceAtomNextToTarget(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3? targetOrbitalTipOverride = null)
    {
        var targetPos = targetAtom.transform.position;
        var targetOrbitalTip = targetOrbitalTipOverride ?? targetOrbital.transform.position;
        var toOrbital = targetOrbitalTip - targetPos;
        var distToOrbital = toOrbital.magnitude;
        if (distToOrbital < 0.01f) return;
        var dir = toOrbital / distToOrbital;
        var newSourcePos = targetOrbitalTip + dir * distToOrbital;
        var delta = newSourcePos - sourceAtom.transform.position;
        foreach (var a in sourceAtom.GetConnectedMolecule())
            a.transform.position += delta;
    }

    void AlignTargetAtomNextToSource(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        var targetPos = targetAtom.transform.position;
        var targetOrbitalTip = targetOrbital.transform.position;
        var toOrbital = targetOrbitalTip - targetPos;
        var distToOrbital = toOrbital.magnitude;
        if (distToOrbital < 0.01f) return;
        var dir = toOrbital / distToOrbital;
        var sourceOrbitalTip = transform.position;
        var newTargetPos = sourceOrbitalTip - dir * distToOrbital;
        var delta = newTargetPos - targetAtom.transform.position;
        foreach (var a in targetAtom.GetConnectedMolecule())
            a.transform.position += delta;
    }

    void CreateStretchVisual()
    {
        if (stretchVisual != null) return;
        var tip = transform.position;
        stretchVisual = new GameObject("StretchVisual");
        stretchVisual.transform.SetParent(transform.parent);
        stretchVisual.transform.localScale = Vector3.one;

        Color color = new Color(1, 1, 1, 0.4f);
        int sortOrder = 0;
        int sortLayer = 0;
        if (mainSpriteRenderer != null)
        {
            color = mainSpriteRenderer.color;
            sortOrder = mainSpriteRenderer.sortingOrder;
            sortLayer = mainSpriteRenderer.sortingLayerID;
        }
        else if (mainMeshRenderer != null && mainMeshRenderer.sharedMaterial != null)
            color = originalColor;

        color.a = Mathf.Clamp01(orbitalVisualAlpha);
        stretchVisualIs3D = Use3DOrbitalPresentation() && stretchSprite == null;

        if (stretchSprite != null)
        {
            var sr = stretchVisual.AddComponent<SpriteRenderer>();
            sr.sprite = stretchSprite;
            sr.color = color;
            sr.sortingOrder = sortOrder;
            sr.sortingLayerID = sortLayer;
        }
        else if (stretchVisualIs3D)
        {
            var coneGo = new GameObject("StretchCone");
            coneGo.transform.SetParent(stretchVisual.transform);
            coneGo.transform.localPosition = Vector3.zero;
            coneGo.transform.localRotation = Quaternion.identity;
            coneGo.transform.localScale = Vector3.one;
            var mfCone = coneGo.AddComponent<MeshFilter>();
            mfCone.sharedMesh = GetOrCreateUnitConeMesh();
            var mrCone = coneGo.AddComponent<MeshRenderer>();
            mrCone.sharedMaterial = CreateStretchOrbitalMaterial(color);

            var hemGo = new GameObject("StretchHemisphere");
            hemGo.transform.SetParent(stretchVisual.transform);
            hemGo.transform.localPosition = Vector3.zero;
            hemGo.transform.localRotation = Quaternion.identity;
            hemGo.transform.localScale = Vector3.one;
            var mfSph = hemGo.AddComponent<MeshFilter>();
            mfSph.sharedMesh = GetOrCreateUnitHemisphereMesh();
            var mrSph = hemGo.AddComponent<MeshRenderer>();
            mrSph.sharedMaterial = CreateStretchOrbitalMaterial(color);
        }
        else
        {
            var tri = new GameObject("Triangle");
            tri.transform.SetParent(stretchVisual.transform);
            tri.transform.localPosition = Vector3.zero;
            tri.transform.localRotation = Quaternion.identity;
            tri.transform.localScale = Vector3.one;
            var triSr = tri.AddComponent<SpriteRenderer>();
            triSr.sprite = GetOrCreateTriangleSprite();
            triSr.color = color;
            triSr.sortingOrder = sortOrder;
            triSr.sortingLayerID = sortLayer;
            var clipMat = GetOrCreateTriangleClipMaterial();
            if (clipMat != null) triSr.material = clipMat;

            var circle = new GameObject("Circle");
            circle.transform.SetParent(stretchVisual.transform);
            circle.transform.localPosition = Vector3.zero;
            circle.transform.localRotation = Quaternion.identity;
            circle.transform.localScale = Vector3.one;
            var circleSr = circle.AddComponent<SpriteRenderer>();
            circleSr.sprite = GetOrCreateCircleSprite();
            // Procedural circle texture is white; use electron black (not orbital tint) so drag preview matches real electrons.
            circleSr.color = ElectronFunction.Electron2DVisualColor;
            circleSr.sortingLayerID = sortLayer;
            circleSr.sortingOrder = sortOrder + 1;
        }
        UpdateStretchVisual(tip);
    }

    /// <summary>Cone/hemisphere anchor for stretch. Bond orbitals at the bond center use an atom so tip−anchor is non‑degenerate (sigma bonds).</summary>
    Vector3 GetStretchAnchorForTip(Vector3 tip)
    {
        if (bond != null)
        {
            var atomA = bond.AtomA;
            var atomB = bond.AtomB;
            if (atomA != null && atomB != null)
            {
                Vector3 center = bond.transform.position;
                Vector3 delta = tip - center;
                const float eps = 0.02f;
                if (delta.sqrMagnitude >= eps * eps)
                    return center;
                float dA = (tip - atomA.transform.position).sqrMagnitude;
                float dB = (tip - atomB.transform.position).sqrMagnitude;
                return dA >= dB ? atomA.transform.position : atomB.transform.position;
            }
        }
        if (transform.parent != null)
            return transform.parent.position;
        return transform.position;
    }

    float Approximate3DElectronSphereRadiusWorld()
    {
        float maxR = 0f;
        foreach (var e in GetComponentsInChildren<ElectronFunction>(true))
        {
            if (e == null) continue;
            float m = Mathf.Max(Mathf.Max(e.transform.lossyScale.x, e.transform.lossyScale.y), e.transform.lossyScale.z);
            maxR = Mathf.Max(maxR, m * 0.5f);
        }
        // Slight overestimate so the black sphere stays clearly inside the translucent cap.
        return maxR > 0.001f ? maxR * 1.08f : 0.12f;
    }

    /// <summary>Shared math for 3D stretch: seam plane (cone–hemisphere) and electron center inside the dome (not protruding through the outer shell).</summary>
    bool TryCompute3DElectronTargetWorldForTip(Vector3 tip, out Vector3 seamCenterWorld, out Vector3 electronTargetWorld)
    {
        seamCenterWorld = electronTargetWorld = default;
        if (!Use3DOrbitalPresentation() || !stretchVisualIs3D || stretchVisual == null) return false;
        var anchor = GetStretchAnchorForTip(tip);
        var delta = tip - anchor;
        float distance = delta.magnitude;
        if (distance < 0.01f && bond == null) return false;

        Transform hem = stretchVisual.transform.Find("StretchHemisphere");
        if (hem == null) return false;

        // Pivot = flat-face / sphere center; cap radius from world scale (matches rendered hemisphere).
        seamCenterWorld = hem.position;
        Vector3 axis = hem.up;
        float capRadius = 0.5f * Mathf.Max(hem.lossyScale.x, Mathf.Max(hem.lossyScale.y, hem.lossyScale.z));
        if (capRadius < 0.001f) return false;

        float electronR = Approximate3DElectronSphereRadiusWorld();
        const float shellMargin = 1.28f;
        float maxAxialForSphereCenter = Mathf.Max(0.001f, capRadius - electronR * shellMargin);
        float desiredAxial = capRadius * drag3DElectronDepthInCapFraction;
        float axial = Mathf.Min(desiredAxial, maxAxialForSphereCenter);
        float minAxial = capRadius * 0.06f;
        if (axial < minAxial)
            axial = Mathf.Min(minAxial, maxAxialForSphereCenter);
        electronTargetWorld = seamCenterWorld + axis * axial;
        return true;
    }

    void Update3DElectronPositionsForStretchDrag(Vector3 tip)
    {
        if (!TryCompute3DElectronTargetWorldForTip(tip, out _, out var midWorld)) return;
        var hem = stretchVisual != null ? stretchVisual.transform.Find("StretchHemisphere") : null;
        if (hem == null) return;
        Vector3 axisWorld = hem.up.sqrMagnitude > 1e-8f ? hem.up.normalized : Vector3.up;
        float capRadius = 0.5f * Mathf.Max(hem.lossyScale.x, Mathf.Max(hem.lossyScale.y, hem.lossyScale.z));

        var elist = GetComponentsInChildren<ElectronFunction>()
            .Where(e => e != null)
            .OrderBy(e => e.SlotIndex)
            .ToArray();
        if (elist.Length == 0) return;

        if (elist.Length == 1)
        {
            elist[0].transform.localPosition = transform.InverseTransformPoint(midWorld);
        }
        else
        {
            // Spread pair symmetrically about the cap interior target, perpendicular to dome axis.
            Vector3 vWorld = Vector3.Cross(axisWorld, transform.right);
            if (vWorld.sqrMagnitude < 1e-8f)
                vWorld = Vector3.Cross(axisWorld, transform.forward);
            if (vWorld.sqrMagnitude < 1e-8f)
                vWorld = Vector3.Cross(axisWorld, Vector3.up);
            vWorld.Normalize();

            float elecR = Approximate3DElectronSphereRadiusWorld();
            float halfSep = Mathf.Max(elecR * 1.35f, capRadius * 0.2f);
            float maxHalf = Mathf.Max(0.001f, capRadius * 0.45f - elecR * 0.55f);
            halfSep = Mathf.Clamp(halfSep, elecR * 0.85f, maxHalf);

            foreach (var e in elist)
            {
                float sign = elist.Length == 2 ? (e.SlotIndex == 0 ? -1f : 1f)
                    : (e.SlotIndex % 2 == 0 ? -1f : 1f);
                Vector3 posW = midWorld + vWorld * (halfSep * sign);
                e.transform.localPosition = transform.InverseTransformPoint(posW);
            }
        }
    }

    void Reposition3DElectronsAfterOrbitalDrag()
    {
        if (!Use3DOrbitalPresentation()) return;
        foreach (var e in GetComponentsInChildren<ElectronFunction>())
            e.ApplyOrbitalSlotPosition();
    }

    void UpdateStretchVisual(Vector3 tip)
    {
        if (stretchVisual == null) return;
        var anchor = GetStretchAnchorForTip(tip);
        var dir = (tip - anchor);
        var distance = dir.magnitude;
        if (distance < 0.01f && bond == null) return;
        if (distance < 0.01f) distance = 0f;
        dir = distance > 0.01f ? dir / distance : Vector3.up;
        // Bond orbitals: allow stretch to reach origin (no min length). Lone orbitals: MinStretchLength avoids overlap.
        float minLength = (bond != null) ? 0f : MinStretchLength;
        distance = Mathf.Max(distance, minLength);

        float padding = (bond != null) ? 0f : Mathf.Min(apexPadding, distance * 0.4f);
        float triLength = Mathf.Max(0.01f, distance - padding);
        Vector3 triApex = anchor + padding * dir;
        Vector3 triBase = tip;

        if (stretchSprite != null)
        {
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
            stretchVisual.transform.rotation = Quaternion.Euler(0, 0, angle);
            stretchVisual.transform.position = (anchor + tip) * 0.5f;
            stretchVisual.transform.localScale = Vector3.one;
            return;
        }

        if (stretchVisualIs3D)
        {
            stretchVisual.transform.rotation = RotationFromUpTo(dir);
            stretchVisual.transform.position = (triApex + triBase) * 0.5f;
            stretchVisual.transform.localScale = Vector3.one;
            const float ProceduralSpriteSize = 2f;
            float targetSize = ProceduralSpriteSize * originalLocalScale.y;
            float circleScale = targetSize / ProceduralSpriteSize;
            float triWidthScale = circleScale * 2f * dragStretch3DCrossSectionScale;
            // Unit cone base diameter = 1; scale.xz = triWidthScale → matches hemisphere base (diameter triWidthScale).
            // Hemisphere flat face at local y=0 sits flush on cone top (y = coneTopY); dome extends toward the tip.
            float coneTopY = 0.5f * triLength;
            var coneT = stretchVisual.transform.Find("StretchCone");
            var sphereT = stretchVisual.transform.Find("StretchHemisphere");
            if (coneT != null)
            {
                coneT.localPosition = Vector3.zero;
                coneT.localRotation = Quaternion.identity;
                coneT.localScale = new Vector3(triWidthScale, triLength, triWidthScale);
            }
            if (sphereT != null)
            {
                sphereT.localPosition = new Vector3(0f, coneTopY, 0f);
                sphereT.localRotation = Quaternion.identity;
                sphereT.localScale = Vector3.one * triWidthScale;
            }
            Update3DElectronPositionsForStretchDrag(tip);
            return;
        }

        float angleSprite = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
        stretchVisual.transform.rotation = Quaternion.Euler(0, 0, angleSprite);
        stretchVisual.transform.localScale = Vector3.one;
        var tri = stretchVisual.transform.Find("Triangle");
        var circle = stretchVisual.transform.Find("Circle");
        stretchVisual.transform.position = (triApex + triBase) * 0.5f;
        const float ProcSize = 2f;
        float targetSz = ProcSize * originalLocalScale.y;
        float circSc = targetSz / ProcSize;
        float triW = circSc * 2f;
        float triH = triLength / ProcSize;
        if (tri != null)
        {
            tri.localPosition = Vector3.zero;
            tri.localRotation = Quaternion.identity;
            tri.localScale = new Vector3(triW, triH, 1f);
            var triSr = tri.GetComponent<SpriteRenderer>();
            if (triSr != null && triSr.material != null)
            {
                var block = new MaterialPropertyBlock();
                triSr.GetPropertyBlock(block);
                block.SetVector(ClipCenterId, new Vector4(0f, 1f, 0f, 0f));
                float clipRadius = circSc;
                block.SetFloat(ClipRadiusXId, clipRadius / Mathf.Max(triW, 0.001f));
                block.SetFloat(ClipRadiusYId, clipRadius / Mathf.Max(triH, 0.001f));
                triSr.SetPropertyBlock(block);
            }
        }
        if (circle != null)
        {
            circle.localPosition = new Vector3(0f, triH, 0f);
            circle.localRotation = Quaternion.identity;
            circle.localScale = new Vector3(circSc * OffsetScale, circSc * OffsetScale, 1f);
        }
    }

    void Start()
    {
        // Partner lobe in σ drag may never get OnPointerDown; seed originals from layout so SnapToOriginal() is safe.
        originalLocalPosition = transform.localPosition;
        originalLocalRotation = transform.localRotation;
        if (originalLocalScale.sqrMagnitude < 0.01f) originalLocalScale = transform.localScale;
        var sr = GetComponent<SpriteRenderer>();
        var mr = GetComponent<MeshRenderer>();
        var mf = GetComponent<MeshFilter>();
        if (sr != null && originalColor.a < 0.01f) originalColor = sr.color;
        else if (mr != null && mr.sharedMaterial != null && originalColor.a < 0.01f)
            originalColor = GetMaterialTint(mr.sharedMaterial);

        if (Use3DOrbitalPresentation())
            Setup3DOrbitalVisual(mf, mr);

        if (editSelectionHighlightActive)
            ApplyOrbitalEditSelectionVisual(true);
        else
            ApplyOrbitalVisualOpacity(sr, mr);
        EnsureCollider();
        SyncElectronObjects();
        IgnoreCollisionsWithChildren();
    }

    void ApplyOrbitalVisualOpacity(SpriteRenderer sr, MeshRenderer mr)
    {
        Color c = originalColor;
        c.a = Mathf.Clamp01(orbitalVisualAlpha);
        originalColor = c;
        if (sr != null) sr.color = c;
        else if (mr != null)
        {
            var mat = mr.material;
            SetMaterialTint(mat, c);
        }
    }

    void RefreshOrbitalBodyVisualAfterDrag()
    {
        if (editSelectionHighlightActive)
            ApplyOrbitalEditSelectionVisual(true);
        else
            ApplyOrbitalVisualOpacity(GetComponent<SpriteRenderer>() ?? mainSpriteRenderer, GetPrimaryBodyMeshRendererForVisual());
    }

    void IgnoreCollisionsWithChildren()
    {
        var orbitalCol2D = GetComponent<Collider2D>();
        var orbitalCol = GetComponent<Collider>();
        var electrons = GetComponentsInChildren<ElectronFunction>();
        foreach (var electron in electrons)
        {
            IgnoreCollision(orbitalCol2D, orbitalCol, electron);
            foreach (var other in electrons)
            {
                if (electron == other) continue;
                IgnoreCollision(electron.GetComponent<Collider2D>(), electron.GetComponent<Collider>(),
                    other.GetComponent<Collider2D>(), other.GetComponent<Collider>());
            }
        }
    }

    static void IgnoreCollision(Collider2D a2D, Collider a3D, ElectronFunction b)
    {
        var b2D = b.GetComponent<Collider2D>();
        var b3D = b.GetComponent<Collider>();
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    static void IgnoreCollision(Collider2D a2D, Collider a3D, Collider2D b2D, Collider b3D)
    {
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    [SerializeField] Vector2 orbitalColliderSize = new Vector2(1f, 0.15f); // Fallback when no sprite/mesh

    void EnsureCollider()
    {
        foreach (var c in GetComponents<Collider>())
            if (c != null) Destroy(c);
        foreach (var c2 in GetComponents<Collider2D>())
            if (c2 != null) Destroy(c2);

        if (Use3DOrbitalPresentation())
        {
            var mf = GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var mb = mf.sharedMesh.bounds;
                var sph = gameObject.AddComponent<SphereCollider>();
                sph.isTrigger = true;
                sph.center = mb.center;
                sph.radius = Mathf.Max(mb.extents.x, mb.extents.y, mb.extents.z);
                return;
            }

            var fb3 = gameObject.AddComponent<BoxCollider>();
            fb3.isTrigger = true;
            fb3.size = new Vector3(orbitalColliderSize.x, orbitalColliderSize.y, 0.1f);
            return;
        }

        var sr = GetComponent<SpriteRenderer>();
        if (sr != null && sr.sprite != null)
        {
            var sb = sr.sprite.bounds;
            Vector3 sc = transform.localScale;
            var box2d = gameObject.AddComponent<BoxCollider2D>();
            box2d.isTrigger = true;
            float sx = sb.size.x * Mathf.Abs(sc.x);
            float sy = sb.size.y * Mathf.Abs(sc.y);
            box2d.size = new Vector2(Mathf.Max(sx, 0.02f), Mathf.Max(sy, 0.02f));
            box2d.offset = new Vector2(sb.center.x * sc.x, sb.center.y * sc.y);
            return;
        }

        var fb2 = gameObject.AddComponent<BoxCollider2D>();
        fb2.isTrigger = true;
        fb2.size = orbitalColliderSize;
    }

    void LateUpdate()
    {
        if (Use3DOrbitalPresentation())
        {
            if (debugDraw3DElectronDrag && isBeingHeld && stretchVisualIs3D && stretchVisual != null)
            {
                var tip = transform.position;
                if (TryCompute3DElectronTargetWorldForTip(tip, out var seam, out var tgt))
                {
                    Debug.DrawLine(seam, tgt, Color.green);
                    Debug.DrawRay(seam, stretchVisual.transform.up * 0.04f, Color.yellow);
                    foreach (var e in GetComponentsInChildren<ElectronFunction>())
                    {
                        Debug.DrawLine(tgt, e.transform.position, Color.cyan);
                        Debug.DrawRay(e.transform.position, Vector3.up * 0.03f, Color.magenta);
                    }
                }
            }

            if (!isBeingHeld)
            {
                foreach (var e in GetComponentsInChildren<ElectronFunction>())
                {
                    if (e == null || e.IsElectronPointerDragActive) continue;
                    e.ApplyOrbitalSlotPosition();
                }
            }
        }
    }

    void SyncElectronObjects()
    {
        if (electronPrefab == null) return;

        var existing = GetComponentsInChildren<ElectronFunction>();
        int target = electronCount;

        if (existing.Length > target)
        {
            for (int i = target; i < existing.Length; i++)
                Destroy(existing[i].gameObject);
        }
        else if (existing.Length < target)
        {
            for (int i = existing.Length; i < target; i++)
            {
                var electron = Instantiate(electronPrefab, transform);
                electron.SetSlotIndex(i);
                electron.SetOrbitalWidth(electronSpacing);
            }
        }

        var electrons = GetComponentsInChildren<ElectronFunction>();
        for (int i = 0; i < electrons.Length; i++)
        {
            electrons[i].SetSlotIndex(i);
            electrons[i].SetOrbitalWidth(electronSpacing);
            electrons[i].Sync2DAppearanceWithOrbital();
        }

        IgnoreCollisionsWithChildren();
    }
}
