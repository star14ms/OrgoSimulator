using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

public class ElectronFunction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] float orbitalWidth = 0.5f; // Fallback; overridden by SetOrbitalWidth from parent orbital
    [SerializeField] float electron3DSphereRadius = 0.16f;
    [Tooltip("3D idle (pair): offset from orbital center along local ±Y (perpendicular to bond axis +X), same convention as 2D width.")]
    [SerializeField] float electron3DIdlePairHalfSpacing = 0.11f;

    int slotIndex;
    static Mesh cachedElectronSphereMesh;
    /// <summary>Slightly above black so Lit/Unlit both read on screen; pure black often vanishes.</summary>
    static readonly Color Electron3DVisualColor = new Color(0.1f, 0.1f, 0.14f, 1f);

    public void SetOrbitalWidth(float width)
    {
        orbitalWidth = width;
        UpdatePosition();
    }
    bool isBeingHeld;
    Vector3 originalLocalPosition;
    Vector3 dragOffset;

    public void SetSlotIndex(int index) => slotIndex = index;

    public int SlotIndex => slotIndex;

    public bool IsElectronPointerDragActive => isBeingHeld;

    /// <summary>Re-apply default offset (2D) or orbital center (3D) after orbital drag ends.</summary>
    public void ApplyOrbitalSlotPosition() => UpdatePosition();

    void SetPhysicsEnabled(bool enabled)
    {
        var rb = GetComponent<Rigidbody>();
        var rb2D = GetComponent<Rigidbody2D>();
        if (rb != null) rb.isKinematic = !enabled;
        if (rb2D != null) rb2D.simulated = enabled;
    }

    public void SetPointerBlocked(bool blocked)
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col != null) col.enabled = !blocked;
        if (col2D != null) col2D.enabled = !blocked;
    }

    void Start()
    {
        StartCoroutine(Deferred3DSetup());
    }

    void OnEnable()
    {
        if (!isActiveAndEnabled) return;
        if (Use3DElectronPresentation())
        {
            var mr = GetComponent<MeshRenderer>();
            var mf = GetComponent<MeshFilter>();
            if (mr != null && (mr.sharedMaterial == null || (mf != null && mf.sharedMesh == null)))
                Refresh3DVisualAndCollider();
        }
        else
            Apply2DElectronVisualIfNeeded();
    }

    IEnumerator Deferred3DSetup()
    {
        Refresh3DVisualAndCollider();
        yield return null;
        if (Use3DElectronPresentation())
        {
            Setup3DElectronVisualIfNeeded();
            UpdatePosition();
        }
    }

    void Refresh3DVisualAndCollider()
    {
        if (Use3DElectronPresentation())
            Setup3DElectronVisualIfNeeded();
        else
            Apply2DElectronVisualIfNeeded();
        EnsureCollider();
        UpdatePosition();
    }

    /// <summary>Orthographic mode keeps the sprite; tint to match 3D (prefab is often white).</summary>
    void Apply2DElectronVisualIfNeeded()
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr == null) return;
        sr.color = Electron3DVisualColor;
    }

    static bool Use3DElectronPresentation() =>
        Camera.main != null && !Camera.main.orthographic;

    static Mesh GetElectronSphereMesh()
    {
        if (cachedElectronSphereMesh != null) return cachedElectronSphereMesh;
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        cachedElectronSphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);
        return cachedElectronSphereMesh;
    }

    static Shader TryFindElectron3DShader()
    {
        // Lit makes pure black nearly invisible with weak/wrong lighting; Unlit stays a solid sphere inside the orbital shell.
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Universal");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
        return sh;
    }

    static void ConfigureElectron3DMaterial(Material mat, Color tint)
    {
        if (mat == null) return;
        mat.color = tint;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
        mat.renderQueue = (int)RenderQueue.Geometry;
        if (mat.shader == null) return;
        string sn = mat.shader.name;
        bool lit = sn.IndexOf("Lit", System.StringComparison.OrdinalIgnoreCase) >= 0
                   && sn.IndexOf("Unlit", System.StringComparison.OrdinalIgnoreCase) < 0;
        if (lit && mat.HasProperty("_EmissionColor"))
        {
            mat.SetColor("_EmissionColor", new Color(0.06f, 0.06f, 0.09f));
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (mat.HasProperty("_Emission")) mat.SetFloat("_Emission", 1f);
            mat.EnableKeyword("_EMISSION");
        }
    }

    static void ApplyElectron3DMeshAppearance(MeshRenderer mr, Color tint)
    {
        if (mr == null) return;
        var sh = TryFindElectron3DShader();
        if (sh == null) return;
        mr.sharedMaterial = new Material(sh) { name = "Electron3D_" + sh.name };
        ConfigureElectron3DMaterial(mr.sharedMaterial, tint);
        mr.allowOcclusionWhenDynamic = false;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }

    /// <summary>Replaces 2D sprite with a URP mesh sphere; uses Unlit so the ball stays visible inside translucent orbitals.</summary>
    void Setup3DElectronVisualIfNeeded()
    {
        var sr = GetComponent<SpriteRenderer>();
        var existingMr = GetComponent<MeshRenderer>();
        if (sr == null && existingMr != null)
        {
            var mf = GetComponent<MeshFilter>();
            if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
            if (mf.sharedMesh == null) mf.sharedMesh = GetElectronSphereMesh();
            transform.localScale = Vector3.one * (electron3DSphereRadius * 2f);
            ApplyElectron3DMeshAppearance(existingMr, Electron3DVisualColor);
            return;
        }
        if (sr == null) return;

        var circle2D = GetComponent<CircleCollider2D>();
        if (circle2D != null) Destroy(circle2D);
        Destroy(sr);

        var mf2 = gameObject.GetComponent<MeshFilter>();
        if (mf2 == null) mf2 = gameObject.AddComponent<MeshFilter>();
        mf2.sharedMesh = GetElectronSphereMesh();

        var mr = gameObject.GetComponent<MeshRenderer>();
        if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
        transform.localScale = Vector3.one * (electron3DSphereRadius * 2f);
        ApplyElectron3DMeshAppearance(mr, Electron3DVisualColor);
    }

    void EnsureCollider()
    {
        if (Use3DElectronPresentation())
        {
            var box = GetComponent<BoxCollider>();
            if (box != null) Destroy(box);
            var circle2D = GetComponent<CircleCollider2D>();
            if (circle2D != null) Destroy(circle2D);
            var sph = GetComponent<SphereCollider>();
            if (sph == null) sph = gameObject.AddComponent<SphereCollider>();
            sph.isTrigger = true;
            sph.radius = 0.5f;
            return;
        }
        if (GetComponent<Collider>() != null || GetComponent<Collider2D>() != null) return;
        var col = gameObject.AddComponent<BoxCollider>();
        col.size = new Vector3(0.3f, 0.3f, 0.1f);
        col.isTrigger = true;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        var orbital = GetComponentInParent<ElectronOrbitalFunction>();
        if (orbital != null && orbital.Bond != null) return;

        isBeingHeld = true;
        originalLocalPosition = transform.localPosition;
        dragOffset = transform.position - PlanarPointerInteraction.ScreenToWorldPoint(eventData.position);
        orbital?.SetPointerBlocked(true);
        orbital?.SetPhysicsEnabled(false);
        SetPhysicsEnabled(false);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (isBeingHeld)
            transform.position = PlanarPointerInteraction.ScreenToWorldPoint(eventData.position) + dragOffset;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        isBeingHeld = false;
        SetPhysicsEnabled(true);
        var orbital = GetComponentInParent<ElectronOrbitalFunction>();
        if (orbital != null)
        {
            orbital.SetPointerBlocked(false);
            orbital.SetPhysicsEnabled(true);
            if (!orbital.ContainsPoint(transform.position))
            {
                orbital.RemoveElectron(this);
                TryAcceptIntoOrbital();
            }
            else
            {
                transform.localPosition = originalLocalPosition;
            }
            return;
        }
        TryAcceptIntoOrbital();
    }

    void TryAcceptIntoOrbital()
    {
        var orbitals = Object.FindObjectsByType<ElectronOrbitalFunction>(FindObjectsSortMode.None);
        foreach (var orb in orbitals)
        {
            if (orb.CanAcceptElectron() && orb.ContainsPoint(transform.position))
            {
                orb.AcceptElectron(this);
                return;
            }
        }
    }

    void UpdatePosition()
    {
        if (Use3DElectronPresentation())
        {
            var orb = GetComponentInParent<ElectronOrbitalFunction>();
            int n = orb != null ? orb.ElectronCount : 1;
            if (n >= 2)
            {
                float half = Mathf.Max(electron3DIdlePairHalfSpacing, electron3DSphereRadius * 0.9f);
                float pairLocalY = (slotIndex == 0 ? -1f : 1f) * half;
                transform.localPosition = new Vector3(0f, pairLocalY, 0f);
            }
            else
                transform.localPosition = Vector3.zero;
            return;
        }
        // 2D: always slot-based (±half-width); lone electron stays on slot 0 side.
        float centerOfHalf = orbitalWidth * 0.5f;
        float y = (slotIndex == 0 ? -1f : 1f) * centerOfHalf;
        transform.localPosition = new Vector3(0, y, 0);
    }
}
