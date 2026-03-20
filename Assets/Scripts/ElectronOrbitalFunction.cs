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
    [Tooltip("3D drag stretch only: scales hemisphere diameter and cone base (XZ) vs idle orbital sizing.")]
    [SerializeField] [Range(0.35f, 1f)] float dragStretch3DCrossSectionScale = 0.65f;
    [Tooltip("3D drag: offset from flat seam into the hemispherical bulk along the tip axis, as a fraction of cap radius (keeps electrons under the dome, not on the outer shell).")]
    [SerializeField] [Range(0.08f, 0.5f)] float drag3DElectronDepthInCapFraction = 0.32f;
    [Tooltip("Draw seam → target → electron paths in Scene view while dragging a 3D orbital.")]
    [SerializeField] bool debugDraw3DElectronDrag;

    /// <summary>Unity Console logs for π-bond orbital redistribution (tag <c>[π-redist]</c>). Off in non-editor release builds.</summary>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public static bool DebugPiOrbitalRedistribution = true;
#else
    public static bool DebugPiOrbitalRedistribution = false;
#endif

    public static void LogPiRedistDebug(string message)
    {
        if (!DebugPiOrbitalRedistribution) return;
        Debug.Log($"[π-redist] {message}");
    }

    AtomFunction bondedAtom;
    CovalentBond bond;
    bool isBeingHeld;
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

    public void SetPointerBlocked(bool blocked)
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col != null) col.enabled = !blocked;
        if (col2D != null) col2D.enabled = !blocked;
    }

    const string EditModeOrbitalGlowName = "EditModeSelectionGlow";
    /// <summary>2D ortho: billboard ring outline, same style as <see cref="AtomFunction"/> selection.</summary>
    GameObject orbitalSelectionHighlight2D;

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

    /// <summary>Highlight this orbital (e.g. when selected for bonding in edit mode).</summary>
    public void SetHighlighted(bool on)
    {
        if (Use3DOrbitalPresentation())
        {
            if (orbitalSelectionHighlight2D != null) orbitalSelectionHighlight2D.SetActive(false);
            SetOrbitalHighlight3D(on);
            return;
        }

        var glow3d = transform.Find(EditModeOrbitalGlowName);
        if (glow3d != null) glow3d.gameObject.SetActive(false);
        SetOrbitalHighlight2DRing(on);
    }

    static Sprite CreateRingOutlineSprite()
    {
        const int size = 32;
        var tex = new Texture2D(size, size);
        tex.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - size * 0.5f) / (size * 0.5f);
                float dy = (y - size * 0.5f) / (size * 0.5f);
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                tex.SetPixel(x, y, r >= 0.85f && r <= 1f ? Color.white : Color.clear);
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f);
    }

    void BillboardEditModeGlowTowardMainCamera(Transform ring)
    {
        var cam = Camera.main;
        if (cam == null || ring == null) return;
        Vector3 anchor = transform.position;
        Vector3 toCam = cam.transform.position - anchor;
        if (toCam.sqrMagnitude < 1e-10f) return;
        ring.position = anchor;
        ring.rotation = Quaternion.LookRotation(toCam, cam.transform.up) * Quaternion.Euler(0f, 180f, 0f);
    }

    /// <summary>Matches atom 2D selection: green ring outline, no body tint or scale change.</summary>
    void SetOrbitalHighlight2DRing(bool on)
    {
        Color ringCol = new Color(0.3f, 0.9f, 0.4f, 0.6f);

        if (!on)
        {
            if (orbitalSelectionHighlight2D != null) orbitalSelectionHighlight2D.SetActive(false);
            return;
        }

        if (orbitalSelectionHighlight2D == null)
        {
            orbitalSelectionHighlight2D = new GameObject("SelectionHighlight");
            orbitalSelectionHighlight2D.transform.SetParent(transform, false);
            orbitalSelectionHighlight2D.transform.localPosition = Vector3.zero;
            orbitalSelectionHighlight2D.transform.localRotation = Quaternion.identity;
            var sr = orbitalSelectionHighlight2D.AddComponent<SpriteRenderer>();
            sr.sprite = CreateRingOutlineSprite();
            sr.color = ringCol;
        }

        var ringSr = orbitalSelectionHighlight2D.GetComponent<SpriteRenderer>();
        if (ringSr != null)
        {
            ringSr.color = ringCol;

            int order = 0;
            var bodySr = GetComponent<SpriteRenderer>();
            if (bodySr != null) order = bodySr.sortingOrder;
            ringSr.sortingOrder = order + 1;
        }

        float mx = Mathf.Max(orbitalColliderSize.x, 0.05f);
        float my = Mathf.Max(orbitalColliderSize.y, 0.05f);
        orbitalSelectionHighlight2D.transform.localScale = new Vector3(mx * 1.4f, my * 1.4f, 1f);
        orbitalSelectionHighlight2D.SetActive(true);
    }

    void SetOrbitalHighlight3D(bool on)
    {
        var tr = transform.Find(EditModeOrbitalGlowName);
        if (!on)
        {
            if (tr != null) tr.gameObject.SetActive(false);
            return;
        }

        if (tr != null && tr.GetComponent<SpriteRenderer>() == null)
        {
            Destroy(tr.gameObject);
            tr = null;
        }

        GameObject go;
        if (tr == null)
        {
            go = new GameObject(EditModeOrbitalGlowName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = CreateRingOutlineSprite();
            sr.color = new Color(0.25f, 0.98f, 0.42f, 0.92f);
            sr.sortingOrder = 80;
        }
        else go = tr.gameObject;

        var cap = GetComponent<CapsuleCollider>();
        if (cap != null && cap.direction == 1)
        {
            float rxz = cap.radius * 2f * 1.16f;
            float h = cap.height * 1.1f;
            go.transform.localScale = new Vector3(rxz, h, 1f);
        }
        else
        {
            float u = Mathf.Max(transform.localScale.x, transform.localScale.y, transform.localScale.z);
            float s = Mathf.Max(u * 1.18f, 0.4f);
            go.transform.localScale = new Vector3(s, s, 1f);
        }

        go.SetActive(true);
        BillboardEditModeGlowTowardMainCamera(go.transform);
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

    static bool Use3DOrbitalPresentation() =>
        Camera.main != null && !Camera.main.orthographic;

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

    static Shader TryFindUrplShader()
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
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

        var cap = GetComponent<CapsuleCollider>();
        if (cap != null)
        {
            cap.direction = 1;
            cap.radius = 0.22f;
            cap.height = 0.62f;
            cap.center = Vector3.zero;
        }
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        isBeingHeld = true;
        pointerDownPosition = eventData.position;
        originalLocalPosition = transform.localPosition;
        originalLocalScale = transform.localScale;
        originalLocalRotation = transform.localRotation;
        var cam = Camera.main;
        var wp = MoleculeWorkPlane.Instance;
        if (cam != null && wp != null && wp.TryGetWorldPoint(cam, eventData.position, out var hit))
            dragOffset = transform.position - hit;
        else
            dragOffset = transform.position - PlanarPointerInteraction.ScreenToWorldPoint(eventData.position);
        mainSpriteRenderer = GetComponent<SpriteRenderer>();
        mainMeshRenderer = GetComponent<MeshRenderer>();
        if (mainSpriteRenderer != null) mainSpriteRenderer.enabled = false;
        if (mainMeshRenderer != null) mainMeshRenderer.enabled = false;
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
        isBeingHeld = false;
        Vector3 tip = transform.position;
        if (stretchVisual != null) { Destroy(stretchVisual); stretchVisual = null; }
        Reposition3DElectronsAfterOrbitalDrag();
        if (mainSpriteRenderer != null) mainSpriteRenderer.enabled = true;
        if (mainMeshRenderer != null) mainMeshRenderer.enabled = true;
        SetPhysicsEnabled(true);

        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        if (editMode != null && editMode.EditModeActive && bond == null && electronCount == 1
            && (eventData.position - pointerDownPosition).sqrMagnitude < ShortClickDragThresholdPx * ShortClickDragThresholdPx)
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

        RotateOrbitalToTip(sourceAtom, tip);
    }

    const float RotateStepDeg = 30f;
    const float MinOrbitalSeparationDeg = 30f;

    void RotateOrbitalToTip(AtomFunction atom, Vector3 tipWorld)
    {
        if (atom == null) return;
        var dir = (tipWorld - atom.transform.position);
        dir.z = 0;
        if (dir.sqrMagnitude < 0.01f) return;
        dir.Normalize();
        float preferredAngle = OrbitalAngleUtility.DirectionToAngleWorld(dir);
        float preferredNorm = NormalizeAngleTo360(preferredAngle);

        float bestAngle = Mathf.Round(preferredNorm / RotateStepDeg) * RotateStepDeg;
        bestAngle = NormalizeAngleTo360(bestAngle);
        float bestDelta = 360f;

        for (float slot = 0f; slot < 360f; slot += RotateStepDeg)
        {
            bool tooClose = false;
            foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb == this || orb.transform.parent != atom.transform) continue;
                if (orb.Bond != null) continue;
                float orbAngle = NormalizeAngleTo360(OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform));
                float delta = AngularDistanceDeg(slot, orbAngle);
                if (delta < MinOrbitalSeparationDeg) { tooClose = true; break; }
            }
            if (tooClose) continue;

            float deltaToPref = AngularDistanceDeg(slot, preferredNorm);
            if (deltaToPref < bestDelta)
            {
                bestDelta = deltaToPref;
                bestAngle = slot;
            }
        }

        var slotPos = GetCanonicalSlotPosition(bestAngle, atom.BondRadius);
        transform.localPosition = slotPos.position;
        transform.localRotation = slotPos.rotation;
        transform.localScale = originalLocalScale;
    }

    static float NormalizeAngleTo360(float deg)
    {
        deg = deg % 360f;
        if (deg < 0f) deg += 360f;
        return deg;
    }

    static float AngularDistanceDeg(float a, float b)
    {
        float d = Mathf.Abs(NormalizeAngleTo360(a - b));
        return Mathf.Min(d, 360f - d);
    }

    ElectronOrbitalFunction TryFindSwapTarget(AtomFunction atom, Vector3 tip)
    {
        if (atom == null || bond != null) return null;
        var orbitals = atom.GetComponentsInChildren<ElectronOrbitalFunction>();
        foreach (var orb in orbitals)
        {
            if (orb != this && orb.Bond == null && orb.ContainsPoint(tip))
                return orb;
        }
        return null;
    }

    void SwapPositionsWith(ElectronOrbitalFunction other)
    {
        transform.localPosition = other.transform.localPosition;
        transform.localRotation = other.transform.localRotation;
        other.transform.localPosition = originalLocalPosition;
        other.transform.localRotation = originalLocalRotation;
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
        AtomFunction returnTo = null;
        if (da <= rA && db <= rB)
            returnTo = da <= db ? a : b;
        else if (da <= rA)
            returnTo = a;
        else if (db <= rB)
            returnTo = b;
        if (returnTo != null)
            bond.BreakBond(returnTo);
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

    [Tooltip("Step 1: Align molecule. Duration in seconds.")]
    [SerializeField] float bondAnimStep1Duration = 1.0f;
    [Tooltip("Step 2: Rearrange + Redistribute. Duration in seconds.")]
    [SerializeField] float bondAnimStep2Duration = 1.0f;
    [Tooltip("Step 3: Orbital-to-line transition. Duration in seconds.")]
    [SerializeField] float bondAnimStep3Duration = 1.0f;

    /// <summary>Sigma bond: starts animated formation. Returns true if animation started.</summary>
    bool FormCovalentBondSigmaStart(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3 dropPosition, bool alreadyFlipped = false)
    {
        bool sourceAlreadyAligned = IsSourceOrbitalAlreadyAlignedWithTarget(sourceAtom, targetOrbital);
        bool cannotRearrangeSource = !sourceAlreadyAligned && (sourceAtom.GetPiBondCount() > 0 || IsSourceFlippedSideFilled(sourceAtom, targetAtom, targetOrbital));
        if (sourceAtom.CovalentBonds.Count > 0 && cannotRearrangeSource)
        {
            if (alreadyFlipped)
                return false;
            return targetOrbital.FormCovalentBondSigmaStartAsSource(targetAtom, sourceAtom, this, dropPosition);
        }
        sourceAtom.StartCoroutine(FormCovalentBondSigmaCoroutine(sourceAtom, targetAtom, targetOrbital, dropPosition, userDraggedOrbital: this));
        return true;
    }

    /// <summary>Simulates "user dragged target orbital to source" by calling the same coroutine with swapped args. No flip logic needed.</summary>
    bool FormCovalentBondSigmaStartAsSource(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3 dropPosition)
    {
        // Swap: targetOrbital is the one the user actually dragged - snap it back before bonding
        targetOrbital.SnapToOriginal();
        // Coroutine runs on this orbital (drop-site slot). It was not PointerDown'd for this gesture, so originalLocal*
        // may still be prefab defaults — Step 1 would snap it to the atom center then slide with the molecule.
        originalLocalPosition = transform.localPosition;
        originalLocalScale = transform.localScale;
        originalLocalRotation = transform.localRotation;
        sourceAtom.StartCoroutine(FormCovalentBondSigmaCoroutine(sourceAtom, targetAtom, targetOrbital, dropPosition, userDraggedOrbital: targetOrbital));
        return true;
    }

    IEnumerator FormCovalentBondSigmaCoroutine(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3 dropPosition, ElectronOrbitalFunction userDraggedOrbital)
    {
        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock) a.SetInteractionBlocked(true);

        try
        {
        ElectronOrbitalFunction sourceOrbital = this;
        // Partner = lone pair on the other atom (not the one the user dragged). Coroutine args swap in AsSource, so infer explicitly.
        ElectronOrbitalFunction partnerOrbital = ReferenceEquals(userDraggedOrbital, this) ? targetOrbital : this;
        int piBeforeSource = sourceAtom.GetPiBondCount();
        int piBeforeTarget = targetAtom.GetPiBondCount();
        bool isFlip = sourceAtom.CovalentBonds.Count > 0 && !IsSourceOrbitalAlreadyAlignedWithTarget(sourceAtom, partnerOrbital, userDraggedOrbital) && (sourceAtom.GetPiBondCount() > 0 || IsSourceFlippedSideFilled(sourceAtom, targetAtom, partnerOrbital));
        // isFlip geometry must use the dragged orbital's home slot on its parent atom — not sourceOrbital's locals when AsSource swapped args (that was the drop site, i.e. step-2-facing side).
        Transform draggedParent = userDraggedOrbital != null ? userDraggedOrbital.transform.parent : null;
        Vector3? targetPointOverride = isFlip && draggedParent != null
            ? draggedParent.TransformPoint(userDraggedOrbital.originalLocalPosition)
            : (Vector3?)null;
        AtomFunction rearrangeSource = isFlip ? targetAtom : sourceAtom;
        ElectronOrbitalFunction rearrangeTarget = targetOrbital;
        AtomFunction alignSource = isFlip ? targetAtom : sourceAtom;
        var referenceAtom = isFlip ? sourceAtom : targetAtom;
        var referenceOrbitalTip = targetPointOverride ?? targetOrbital.transform.position;

        // Step 1: AlignSourceAtomNextToTarget
        sourceOrbital.transform.localPosition = originalLocalPosition;
        sourceOrbital.transform.localRotation = originalLocalRotation;
        sourceOrbital.transform.localScale = originalLocalScale;
        var alignTargets = GetAlignTargetPositions(alignSource, referenceAtom, referenceOrbitalTip);
        const float alignPosThreshold = 0.05f;
        bool atomsAlreadyAligned = alignTargets.TrueForAll(t => Vector3.Distance(t.atom.transform.position, t.targetPos) < alignPosThreshold);
        if (!atomsAlreadyAligned)
        {
            var alignStarts = new List<(AtomFunction atom, Vector3 startPos)>();
            foreach (var (atom, targetPos) in alignTargets)
                alignStarts.Add((atom, atom.transform.position));
            for (float t = 0; t < bondAnimStep1Duration; t += Time.deltaTime)
            {
                float s = Mathf.Clamp01(t / bondAnimStep1Duration);
                s = s * s * (3f - 2f * s); // smoothstep
                for (int i = 0; i < alignTargets.Count; i++)
                {
                    var (atom, targetPos) = alignTargets[i];
                    var (_, startPos) = alignStarts[i];
                    atom.transform.position = Vector3.Lerp(startPos, targetPos, s);
                }
                yield return null;
            }
        }
        foreach (var (atom, targetPos) in alignTargets)
            atom.transform.position = targetPos;

        var rearrangeTargetInfo = GetRearrangeTarget(rearrangeSource, rearrangeTarget, targetPointOverride);

        // Create bond (instant) - source orbital (the one we dragged) stays visible through steps 2 and 3
        sourceOrbital.transform.localPosition = originalLocalPosition;
        sourceOrbital.transform.localRotation = originalLocalRotation;
        var bondOrbitalStartWorldPos = sourceOrbital.transform.position;
        var bondOrbitalStartWorldRot = sourceOrbital.transform.rotation;

        int merged = sourceOrbital.ElectronCount + targetOrbital.ElectronCount;
        sourceAtom.UnbondOrbital(sourceOrbital);
        targetAtom.UnbondOrbital(targetOrbital);
        var bond = CovalentBond.Create(isFlip ? targetAtom : sourceAtom, isFlip ? sourceAtom : targetAtom, sourceOrbital, sourceAtom, animateOrbitalToBond: true);
        if (isFlip) targetAtom.transform.SetParent(null);
        targetOrbital.transform.SetParent(bond.transform, worldPositionStays: true);
        bond.SetOrbitalBeingFaded(targetOrbital);
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        var bondOrbitalEnd = bond.GetOrbitalTargetWorldState();
        float sigmaDiff = ComputeSigmaBondAngleDiff(sourceAtom, bondOrbitalEnd.worldPos, bondOrbitalEnd.worldRot);
        bool sigmaNeedsFlip = Mathf.Abs(sigmaDiff) > 90f;
        if (sigmaNeedsFlip)
        {
            bond.orbitalRotationFlipped = true;
            bondOrbitalEnd = bond.GetOrbitalTargetWorldState(); // Re-fetch with flip applied
        }
        // Step 2: Rearrange + Redistribute + animate bonding orbital to bond position (skip if no actual movement)
        var redistA = sourceAtom.GetRedistributeTargets(piBeforeSource, targetAtom);
        var redistB = targetAtom.GetRedistributeTargets(piBeforeTarget, sourceAtom);
        const float alignThreshold = 0.05f;
        const float posThreshold = 0.01f;
        const float rotThreshold = 1f;

        bool hasRearrangeMovement = false;
        if (rearrangeTargetInfo.HasValue)
        {
            var (orbToMove, targetPos, targetRot) = rearrangeTargetInfo.Value;
            if (orbToMove != null && (Vector3.Distance(orbToMove.transform.localPosition, targetPos) > posThreshold || Quaternion.Angle(orbToMove.transform.localRotation, targetRot) > rotThreshold))
                hasRearrangeMovement = true;
        }
        bool needsRearrange = hasRearrangeMovement;

        bool hasRedistributeMovement = false;
        foreach (var entry in redistA)
        {
            if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform
                && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > posThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > rotThreshold))
            { hasRedistributeMovement = true; break; }
        }
        if (!hasRedistributeMovement)
            foreach (var entry in redistB)
            {
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform
                    && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > posThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > rotThreshold))
                { hasRedistributeMovement = true; break; }
            }
        bool needsRedistribute = hasRedistributeMovement;

        // Must check rotation too: position can match while quaternions differ (e.g. 180° flip); direction-only angle can still match both.
        bool orbitalAlreadyAtBond = Vector3.Distance(bondOrbitalStartWorldPos, bondOrbitalEnd.worldPos) < alignThreshold
            && Quaternion.Angle(bondOrbitalStartWorldRot, bondOrbitalEnd.worldRot) < rotThreshold;
        bool skipStep2 = !needsRearrange && !needsRedistribute && orbitalAlreadyAtBond;

        var redistAStarts = new List<(Vector3 pos, Quaternion rot)>();
        var redistBStarts = new List<(Vector3 pos, Quaternion rot)>();
        foreach (var entry in redistA)
            redistAStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        foreach (var entry in redistB)
            redistBStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));

        Vector3? rearrangeStartPos = null;
        Quaternion? rearrangeStartRot = null;
        if (rearrangeTargetInfo.HasValue)
        {
            var (orbToMove, _, _) = rearrangeTargetInfo.Value;
            rearrangeStartPos = orbToMove.transform.localPosition;
            rearrangeStartRot = orbToMove.transform.localRotation;
        }

        if (!skipStep2)
        for (float t = 0; t < bondAnimStep2Duration; t += Time.deltaTime)
        {
            float s = Mathf.Clamp01(t / bondAnimStep2Duration);
            s = s * s * (3f - 2f * s); // smoothstep for position
            float rotT = 1f - (1f - s) * (1f - s); // ease-out quad - rotation leads, expresses orbital rotation visibly

            sourceOrbital.transform.position = Vector3.Lerp(bondOrbitalStartWorldPos, bondOrbitalEnd.worldPos, s);
            sourceOrbital.transform.rotation = Quaternion.Slerp(bondOrbitalStartWorldRot, bondOrbitalEnd.worldRot, rotT);

            if (rearrangeTargetInfo.HasValue)
            {
                var (orbToMove, targetPos, targetRot) = rearrangeTargetInfo.Value;
                if (orbToMove != null && rearrangeStartPos.HasValue && rearrangeStartRot.HasValue)
                {
                    orbToMove.transform.localPosition = Vector3.Lerp(rearrangeStartPos.Value, targetPos, s);
                    orbToMove.transform.localRotation = Quaternion.Slerp(rearrangeStartRot.Value, targetRot, rotT);
                }
            }
            for (int i = 0; i < redistA.Count; i++)
            {
                var entry = redistA[i];
                if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform)
                {
                    var (sp, sr) = redistAStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var entry = redistB[i];
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform)
                {
                    var (sp, sr) = redistBStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            yield return null;
        }

        if (rearrangeTargetInfo.HasValue)
        {
            var (orbToMove, targetPos, targetRot) = rearrangeTargetInfo.Value;
            if (orbToMove != null)
            {
                orbToMove.transform.localPosition = targetPos;
                orbToMove.transform.localRotation = targetRot;
            }
        }
        sourceAtom.ApplyRedistributeTargets(redistA);
        targetAtom.ApplyRedistributeTargets(redistB);
        sourceOrbital.transform.SetParent(bond.transform, worldPositionStays: true); // Reparent now (orbital was left on source atom so it could animate)
        bond.UpdateBondTransformToCurrentAtoms(); // Bond may be stale (atoms moved in step 1)
        bond.SnapOrbitalToBondPosition(); // Match step 3 start position (prevents teleport)
        bond.animatingOrbitalToBondPosition = false;

        // Step 3: Orbital-to-line transformation (bonding orbital shrinking, target orbital fading, line growing simultaneously)
        if (bond != null)
        {
            yield return bond.AnimateOrbitalToLine(bondAnimStep3Duration, targetOrbital);
            sourceOrbital.ElectronCount = merged; // Show merged electrons only after step 3
        }
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();
        }
        finally
        {
            foreach (var a in atomsToBlock) a.SetInteractionBlocked(false);
        }
    }

    (ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)? GetRearrangeTarget(AtomFunction sourceAtom, ElectronOrbitalFunction targetOrbital, Vector3? targetPointOverride)
    {
        float oppositeAngleWorld;
        if (targetPointOverride.HasValue)
        {
            var dir = (targetPointOverride.Value - sourceAtom.transform.position).normalized;
            oppositeAngleWorld = OrbitalAngleUtility.DirectionToAngleWorld(dir);
            foreach (var orb in sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb != this && orb.Bond == null && IsOrbitalAlignedWithTargetPoint(orb, targetPointOverride.Value))
                    return null;
            }
        }
        else
        {
            float targetAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(targetOrbital.transform);
            oppositeAngleWorld = OrbitalAngleUtility.NormalizeAngle(targetAngleWorld + 180f);
            float draggedAngleWorld = OrbitalAngleUtility.LocalRotationToAngleWorld(sourceAtom.transform, originalLocalRotation);
            if (Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(draggedAngleWorld - oppositeAngleWorld)) < 45f)
                return null;
        }
        var sourceOrbitals = sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>();
        ElectronOrbitalFunction orbToMove = null;
        float bestDelta = 360f;
        foreach (var orb in sourceOrbitals)
        {
            if (orb == this || orb.Bond != null) continue;
            float orbAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
            float delta = Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(orbAngleWorld - oppositeAngleWorld));
            if (delta < bestDelta) { bestDelta = delta; orbToMove = orb; }
        }
        if (orbToMove == null) return null;
        float freedSlotAngle = targetPointOverride.HasValue
            ? OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.DirectionToAngleWorld((targetPointOverride.Value - sourceAtom.transform.position).normalized) + 180f)
            : NormalizeAngle(originalLocalRotation.eulerAngles.z);
        var slotPos = GetCanonicalSlotPosition(freedSlotAngle, sourceAtom.BondRadius);
        return (orbToMove, slotPos.position, slotPos.rotation);
    }

    List<(AtomFunction atom, Vector3 targetPos)> GetAlignTargetPositions(AtomFunction moleculeRoot, AtomFunction referenceAtom, Vector3 referenceOrbitalTip)
    {
        var refPos = referenceAtom.transform.position;
        var toOrbital = referenceOrbitalTip - refPos;
        var dist = toOrbital.magnitude;
        if (dist < 0.01f) return new List<(AtomFunction, Vector3)>();
        var dir = toOrbital / dist;
        var newMoleculeRootPos = referenceOrbitalTip + dir * dist;
        var delta = newMoleculeRootPos - moleculeRoot.transform.position;
        var result = new List<(AtomFunction, Vector3)>();
        foreach (var a in moleculeRoot.GetConnectedMolecule())
            result.Add((a, a.transform.position + delta));
        return result;
    }

    bool FormCovalentBondPiStart(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        targetAtom.StartCoroutine(FormCovalentBondPiCoroutine(sourceAtom, targetAtom, targetOrbital));
        return true;
    }

    IEnumerator FormCovalentBondPiCoroutine(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock) a.SetInteractionBlocked(true);

        try
        {
        int piBeforeSource = sourceAtom.GetPiBondCount();
        int piBeforeTarget = targetAtom.GetPiBondCount();
        int mergedElectrons = electronCount + targetOrbital.ElectronCount;
        LogPiRedistDebug($"FormCovalentBondPiCoroutine start: source Z={sourceAtom.AtomicNumber} π={piBeforeSource}, target Z={targetAtom.AtomicNumber} π={piBeforeTarget}, use3D={OrbitalAngleUtility.UseFull3DOrbitalGeometry}");

        // Snap source orbital to original position (like sigma bond) before animating to bond center
        transform.localPosition = originalLocalPosition;
        transform.localRotation = originalLocalRotation;
        transform.localScale = originalLocalScale;

        sourceAtom.UnbondOrbital(this);
        targetAtom.UnbondOrbital(targetOrbital);

        var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: true);
        transform.SetParent(null); // Detach source orbital but keep visible for step 2 animation (preserves world pos from snap)
        bond.SetOrbitalBeingFaded(this); // Use merged count for charge during animation (source orbital is being faded)
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        LogPiRedistDebug($"After CovalentBond.Create (π): source π={sourceAtom.GetPiBondCount()} target π={targetAtom.GetPiBondCount()} bonds A↔B={sourceAtom.GetBondsTo(targetAtom)}");

        yield return AnimateRedistributeOrbitals(sourceAtom, targetAtom, piBeforeSource, piBeforeTarget, this, targetOrbital, bond);

        LogPiRedistDebug($"After AnimateRedistributeOrbitals + TryRedistribute: source π={sourceAtom.GetPiBondCount()} target π={targetAtom.GetPiBondCount()}");

        if (bond != null)
        {
            bond.animatingOrbitalToBondPosition = false;
            yield return bond.AnimateOrbitalToLine(bondAnimStep3Duration, this);
            targetOrbital.ElectronCount = mergedElectrons; // Show merged electrons only after step 3
        }
        else
        {
            Destroy(gameObject);
        }
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();
        }
        finally
        {
            foreach (var a in atomsToBlock) a.SetInteractionBlocked(false);
        }
    }

    IEnumerator AnimateRedistributeOrbitals(AtomFunction sourceAtom, AtomFunction targetAtom, int piBeforeSource, int piBeforeTarget,
        ElectronOrbitalFunction sourceOrbital, ElectronOrbitalFunction targetOrbital, CovalentBond bond)
    {
        var redistA = sourceAtom.GetRedistributeTargets(piBeforeSource, targetAtom);
        var redistB = targetAtom.GetRedistributeTargets(piBeforeTarget, sourceAtom);
        LogPiRedistDebug($"AnimateRedistribute step2: GetRedistributeTargets counts source={redistA.Count} target={redistB.Count} (use3D={OrbitalAngleUtility.UseFull3DOrbitalGeometry}). If 0/0 after π, see following per-atom lines: lone orbitals may all be bonded, so only π orbitals animate in step 2.");

        var redistAStarts = new List<(Vector3 pos, Quaternion rot)>();
        var redistBStarts = new List<(Vector3 pos, Quaternion rot)>();
        foreach (var entry in redistA)
            redistAStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        foreach (var entry in redistB)
            redistBStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));

        // Pi bond: animate both orbitals to bond center. Flip source or target so electrons align in a row.
        Vector3 bondTargetPos = Vector3.zero;
        Quaternion bondTargetRot = Quaternion.identity;
        Quaternion sourceTargetRot = Quaternion.identity;
        Quaternion targetTargetRot = Quaternion.identity;
        if (bond != null)
        {
            var bt = bond.GetOrbitalTargetWorldState();
            bondTargetPos = bt.Item1;
            bondTargetRot = bt.Item2;
            (float sourceDiff, float targetDiff) = ComputePiBondAngleDiffs(sourceAtom, targetAtom, bondTargetPos, bondTargetRot, bond);
            bool flipTarget = Mathf.Abs(sourceDiff) < Mathf.Abs(targetDiff); // Source closer to bond → flip target
            if (flipTarget)
            {
                bond.orbitalRotationFlipped = true;
                bt = bond.GetOrbitalTargetWorldState(); // Re-fetch with flip
                bondTargetPos = bt.Item1;
                bondTargetRot = bt.Item2; // Target (bond orbital) gets flipped rotation
                sourceTargetRot = bondTargetRot * Quaternion.Euler(0f, 0f, 180f); // Source opposite to target
                targetTargetRot = bondTargetRot;
            }
            else
            {
                sourceTargetRot = bondTargetRot * Quaternion.Euler(0f, 0f, 180f);
                targetTargetRot = bondTargetRot;
            }
        }
        var sourceOrbStart = sourceOrbital != null ? (sourceOrbital.transform.position, sourceOrbital.transform.rotation) : (Vector3.zero, Quaternion.identity);
        var targetOrbStart = targetOrbital != null ? (targetOrbital.transform.position, targetOrbital.transform.rotation) : (Vector3.zero, Quaternion.identity);

        const float piAlignThreshold = 0.05f;
        const float piPosThreshold = 0.01f;
        const float piRotThreshold = 1f;
        bool sourceAtBond = sourceOrbital == null || bond == null || (Vector3.Distance(sourceOrbital.transform.position, bondTargetPos) < piAlignThreshold && Quaternion.Angle(sourceOrbital.transform.rotation, sourceTargetRot) < piRotThreshold);
        bool targetAtBond = targetOrbital == null || bond == null || (Vector3.Distance(targetOrbital.transform.position, bondTargetPos) < piAlignThreshold && Quaternion.Angle(targetOrbital.transform.rotation, targetTargetRot) < piRotThreshold);
        bool hasRedistMovement = false;
        foreach (var entry in redistA)
        {
            if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform
                && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > piPosThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > piRotThreshold))
            { hasRedistMovement = true; break; }
        }
        if (!hasRedistMovement)
            foreach (var entry in redistB)
            {
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform
                    && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > piPosThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > piRotThreshold))
                { hasRedistMovement = true; break; }
            }
        bool skipPiStep2 = sourceAtBond && targetAtBond && !hasRedistMovement;
        LogPiRedistDebug($"AnimateRedistribute motion: skipStep2={skipPiStep2} sourceAtBond={sourceAtBond} targetAtBond={targetAtBond} hasRedistMovement={hasRedistMovement}");

        if (!skipPiStep2)
        for (float t = 0; t < bondAnimStep2Duration; t += Time.deltaTime)
        {
            float s = Mathf.Clamp01(t / bondAnimStep2Duration);
            s = s * s * (3f - 2f * s); // smoothstep for position
            float rotT = 1f - (1f - s) * (1f - s); // ease-out quad - rotation leads, expresses orbital rotation visibly

            if (sourceOrbital != null && bond != null)
            {
                sourceOrbital.transform.position = Vector3.Lerp(sourceOrbStart.Item1, bondTargetPos, s);
                sourceOrbital.transform.rotation = Quaternion.Slerp(sourceOrbStart.Item2, sourceTargetRot, rotT);
            }
            if (targetOrbital != null && bond != null)
            {
                targetOrbital.transform.position = Vector3.Lerp(targetOrbStart.Item1, bondTargetPos, s);
                targetOrbital.transform.rotation = Quaternion.Slerp(targetOrbStart.Item2, targetTargetRot, rotT);
            }

            for (int i = 0; i < redistA.Count; i++)
            {
                var entry = redistA[i];
                if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform)
                {
                    var (sp, sr) = redistAStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var entry = redistB[i];
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform)
                {
                    var (sp, sr) = redistBStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            yield return null;
        }
        sourceAtom.ApplyRedistributeTargets(redistA);
        targetAtom.ApplyRedistributeTargets(redistB);

        // Snap both orbitals to exact position step 3 expects (prevents teleport)
        if (bond != null)
        {
            targetOrbital.transform.SetParent(bond.transform, worldPositionStays: true); // Reparent now (orbital was left on target atom so it could animate)
            bond.UpdateBondTransformToCurrentAtoms(); // Bond may be stale
            bond.SnapOrbitalToBondPosition();
            if (sourceOrbital != null)
            {
                var bt = bond.GetOrbitalTargetWorldState();
                sourceOrbital.transform.position = bt.Item1;
                sourceOrbital.transform.rotation = sourceTargetRot;
            }
        }

        // Partial lists only animate a subset of lone orbitals; the other atom can get an empty list while π count
        // still changed. Always run full RedistributeOrbitals on both endpoints when π count changed.
        TryRedistributeOrbitalsAfterBondChange(sourceAtom, targetAtom, piBeforeSource, piBeforeTarget);
    }

    static (float sourceDiff, float targetDiff) ComputePiBondAngleDiffs(AtomFunction sourceAtom, AtomFunction targetAtom, Vector3 bondPos, Quaternion bondRot, CovalentBond bond)
    {
        var toCenterFromSource = bondPos - sourceAtom.transform.position;
        if (toCenterFromSource.sqrMagnitude < 0.0001f) return (0f, 0f);
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

    static float ComputeSigmaBondAngleDiff(AtomFunction sourceAtom, Vector3 bondPos, Quaternion bondRot)
    {
        var toCenter = bondPos - sourceAtom.transform.position;
        if (toCenter.sqrMagnitude < 0.0001f) return 0f;
        toCenter.Normalize();
        toCenter.z = 0;
        float sourceAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenter);
        var bondDir = bondRot * Vector3.right;
        bondDir.z = 0;
        if (bondDir.sqrMagnitude < 0.0001f) return 0f;
        bondDir.Normalize();
        float bondAngle = OrbitalAngleUtility.DirectionToAngleWorld(bondDir);
        float diff = OrbitalAngleUtility.NormalizeAngle(sourceAngle - bondAngle);
        return diff;
    }

    static void TryRedistributeOrbitalsAfterBondChange(AtomFunction sourceAtom, AtomFunction targetAtom, int piBeforeSource, int piBeforeTarget)
    {
        int piSrcNow = sourceAtom != null ? sourceAtom.GetPiBondCount() : -1;
        int piTgtNow = targetAtom != null ? targetAtom.GetPiBondCount() : -1;
        bool redistSrc = sourceAtom != null && piSrcNow != piBeforeSource;
        bool redistTgt = targetAtom != null && piTgtNow != piBeforeTarget;
        LogPiRedistDebug($"TryRedistributeOrbitalsAfterBondChange: source Z={sourceAtom?.AtomicNumber} π {piBeforeSource}→{piSrcNow} → RedistributeOrbitals={(redistSrc ? "call" : "skip")}; target Z={targetAtom?.AtomicNumber} π {piBeforeTarget}→{piTgtNow} → RedistributeOrbitals={(redistTgt ? "call" : "skip")}");
        if (redistSrc) sourceAtom.RedistributeOrbitals();
        if (redistTgt) targetAtom.RedistributeOrbitals();
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

    static bool IsOrbitalAlignedWithTargetPoint(ElectronOrbitalFunction orbital, Vector3 targetPoint)
    {
        var atomPos = orbital.transform.parent != null ? orbital.transform.parent.position : orbital.transform.position;
        var dirToTarget = (targetPoint - atomPos).normalized;
        float targetAngleWorld = OrbitalAngleUtility.DirectionToAngleWorld(dirToTarget);
        float orbitalAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(orbital.transform);
        const float tolerance = 45f;
        return Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(orbitalAngleWorld - targetAngleWorld)) < tolerance;
    }

    void RearrangeOrbitalToFaceTarget(AtomFunction sourceAtom, ElectronOrbitalFunction targetOrbital, Vector3? targetPointOverride = null)
    {
        float oppositeAngleWorld;
        if (targetPointOverride.HasValue)
        {
            var dirFromSourceToTarget = (targetPointOverride.Value - sourceAtom.transform.position).normalized;
            oppositeAngleWorld = OrbitalAngleUtility.DirectionToAngleWorld(dirFromSourceToTarget);
            foreach (var orb in sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb != this && orb.Bond == null && IsOrbitalAlignedWithTargetPoint(orb, targetPointOverride.Value))
                    return;
            }
        }
        else
        {
            float targetAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(targetOrbital.transform);
            oppositeAngleWorld = OrbitalAngleUtility.NormalizeAngle(targetAngleWorld + 180f);
            float draggedAngleWorld = OrbitalAngleUtility.LocalRotationToAngleWorld(sourceAtom.transform, originalLocalRotation);
            const float tolerance = 45f;
            if (Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(draggedAngleWorld - oppositeAngleWorld)) < tolerance)
                return;
        }

        var sourceOrbitals = sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>();
        ElectronOrbitalFunction orbToMove = null;
        float bestDelta = 360f;
        foreach (var orb in sourceOrbitals)
        {
            if (orb == this || orb.Bond != null) continue;
            float orbAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
            float delta = Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(orbAngleWorld - oppositeAngleWorld));
            if (delta < bestDelta)
            {
                bestDelta = delta;
                orbToMove = orb;
            }
        }
        if (orbToMove != null)
        {
            float freedSlotAngle = targetPointOverride.HasValue
                ? OrbitalAngleUtility.DirectionToAngleWorld((targetPointOverride.Value - sourceAtom.transform.position).normalized)
                : NormalizeAngle(originalLocalRotation.eulerAngles.z);
            var slotPos = GetCanonicalSlotPosition(freedSlotAngle, sourceAtom.BondRadius);
            orbToMove.transform.localPosition = slotPos.position;
            orbToMove.transform.localRotation = slotPos.rotation;
        }
    }

    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPositionFromLocalDirection(Vector3 localDir, float bondRadius)
    {
        if (localDir.sqrMagnitude < 1e-8f)
            localDir = Vector3.right;
        localDir.Normalize();
        float offset = bondRadius * 0.6f;
        var pos = localDir * offset;
        var rot = Quaternion.FromToRotation(Vector3.right, localDir);
        return (pos, rot);
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
            circleSr.color = color;
            circleSr.sortingOrder = sortOrder;
            circleSr.sortingLayerID = sortLayer;
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
        if (originalLocalScale.sqrMagnitude < 0.01f) originalLocalScale = transform.localScale;
        var sr = GetComponent<SpriteRenderer>();
        var mr = GetComponent<MeshRenderer>();
        var mf = GetComponent<MeshFilter>();
        if (sr != null && originalColor.a < 0.01f) originalColor = sr.color;
        else if (mr != null && mr.sharedMaterial != null && originalColor.a < 0.01f)
            originalColor = GetMaterialTint(mr.sharedMaterial);

        if (Use3DOrbitalPresentation())
            Setup3DOrbitalVisual(mf, mr);

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

    [SerializeField] Vector2 orbitalColliderSize = new Vector2(1f, 0.15f); // Thin bar so it doesn't overlap electrons at y=±0.25

    void EnsureCollider()
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col == null && col2D == null)
        {
            col = gameObject.AddComponent<BoxCollider>();
            col.isTrigger = true;
        }
        if (col is BoxCollider box)
            box.size = new Vector3(orbitalColliderSize.x, orbitalColliderSize.y, 0.1f);
        if (col2D is BoxCollider2D box2D)
            box2D.size = orbitalColliderSize;
    }

    void LateUpdate()
    {
        if (Use3DOrbitalPresentation())
        {
            var glowTr = transform.Find(EditModeOrbitalGlowName);
            if (glowTr != null && glowTr.gameObject.activeSelf)
                BillboardEditModeGlowTowardMainCamera(glowTr);

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
        }

        IgnoreCollisionsWithChildren();
    }
}
