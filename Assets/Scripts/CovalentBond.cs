using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

/// <summary>
/// Represents a covalent bond between two atoms. Owns the shared orbital and its electrons.
/// Positions the orbital between the two atoms and notifies both when electrons change.
/// Orthographic scenes: 2D sprite line. Perspective (3D) scenes: solid cylinder mesh along the bond axis.
/// Click to show orbital while holding, returns to line/cylinder on release if bond doesn't break.
/// </summary>
public class CovalentBond : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    /// <summary>Console + optional project <c>.log</c>: <c>[bond-break]</c> orbital tips. Default on; set false to silence.</summary>
    public static bool DebugLogBreakBondOrbitalAngles = true;

    static void LogBondBreakLine(string line)
    {
        if (!DebugLogBreakBondOrbitalAngles) return;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    [SerializeField] AtomFunction atomA;
    [SerializeField] AtomFunction atomB;
    ElectronOrbitalFunction orbital;
    AtomFunction orbitalContributor; // Atom that contributed the orbital; used for odd-electron tie-break when EN equal
    ElectronOrbitalFunction orbitalBeingFadedForCharge; // During bond formation: the orbital being faded; use its electrons for charge until destroyed

    bool orbitalVisible;
    bool forwardedPressToOrbital;
    float orbitalToLineAnimProgress = -1f; // -1 = done, 0..1 = animating
    internal bool animatingOrbitalToBondPosition; // Step 2: orbital moving from atom to bond
    internal bool orbitalRotationFlipped; // Sigma: flip when source opposite to bond so electrons don't overlap
    PointerEventData storedPressData;
    GameObject lineVisual;
    SpriteRenderer lineRenderer;
    MeshRenderer cylinderRenderer;
    bool useCylinderBondVisual;
    BoxCollider2D lineCollider;
    BoxCollider lineCollider3D;
    static Sprite lineSprite;
    static Material bondCylinderMaterial;

    /// <summary>Black bond stroke with slight transparency (sprite + 3D cylinder).</summary>
    const float BondVisualAlpha = 0.82f;
    static readonly Color BondVisualColor = new Color(0f, 0f, 0f, BondVisualAlpha);

    public AtomFunction AtomA => atomA;
    public AtomFunction AtomB => atomB;
    public ElectronOrbitalFunction Orbital => orbital;

    public int ElectronCount => orbital != null ? orbital.ElectronCount : 0;

    /// <summary>Returns how many bond electrons count toward the given atom for charge calculation. Uses electronegativity; when equal EN and odd count, orbital contributor gets the extra electron.</summary>
    public int GetElectronsOwnedBy(AtomFunction atom)
    {
        if (atom == null || atomA == null || atomB == null || atom != atomA && atom != atomB) return 0;
        var other = atom == atomA ? atomB : atomA;
        if (other == null) return 0;
        int bondElectrons = ElectronCount;
        if (orbitalBeingFadedForCharge != null)
            bondElectrons += orbitalBeingFadedForCharge.ElectronCount;
        float myEN = AtomFunction.GetElectronegativity(atom.AtomicNumber);
        float otherEN = AtomFunction.GetElectronegativity(other.AtomicNumber);
        if (myEN > otherEN) return bondElectrons;
        if (myEN < otherEN) return 0;
        if (bondElectrons % 2 == 0) return bondElectrons / 2;
        if (orbitalContributor == null) return bondElectrons / 2; // Fallback when contributor unknown
        return orbitalContributor == atom ? (bondElectrons + 1) / 2 : (bondElectrons - 1) / 2;
    }

    /// <summary>Updates bond transform to current atom positions. Call before SnapOrbitalToBondPosition when animatingOrbitalToBondPosition was true (bond may be stale).</summary>
    public void UpdateBondTransformToCurrentAtoms()
    {
        if (atomA == null || atomB == null) return;
        var posA = atomA.transform.position;
        var posB = atomB.transform.position;
        var center = (posA + posB) * 0.5f;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;
        transform.position = center;
        transform.rotation = BondFrameRotation(delta, useCylinderBondVisual);
    }

    /// <summary>Snaps the bond orbital to the exact world position that matches lineVisual (center + perpendicular * offset). Call at end of step 2 to prevent teleport into step 3. Call UpdateBondTransformToCurrentAtoms first.</summary>
    public void SnapOrbitalToBondPosition()
    {
        if (orbital == null || atomA == null || atomB == null) return;
        var (worldPos, worldRot) = GetOrbitalTargetWorldState();
        orbital.transform.position = worldPos;
        orbital.transform.rotation = worldRot;
    }

    /// <summary>Returns the target world position and rotation for the bond orbital (for step 2 animation).</summary>
    public (Vector3 worldPos, Quaternion worldRot) GetOrbitalTargetWorldState()
    {
        if (atomA == null || atomB == null) return (transform.position, transform.rotation * Quaternion.Euler(0, 0, 90f));
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var posA = atomA.transform.position;
        var posB = atomB.transform.position;
        var center = (posA + posB) * 0.5f;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return (center, transform.rotation * Quaternion.Euler(0, 0, 90f));
        var perpendicular = PerpendicularToBondDirection(delta / distance);
        float offset = GetLineOffset(GetBondIndex(), atomA.GetBondsTo(atomB));
        var worldPos = center + perpendicular * offset;
        var worldRot = transform.rotation * Quaternion.Euler(0, 0, 90f);
        if (orbitalRotationFlipped) worldRot = worldRot * Quaternion.Euler(0f, 0f, 180f);
        return (worldPos, worldRot);
    }

    int GetBondIndex()
    {
        if (atomA == null || atomB == null) return 0;
        var bondsBetween = new List<CovalentBond>();
        foreach (var b in atomA.CovalentBonds)
        {
            if (b == null) continue;
            var other = b.AtomA == atomA ? b.AtomB : b.AtomA;
            if (other == atomB) bondsBetween.Add(b);
        }
        for (int i = 0; i < bondsBetween.Count; i++)
            if (bondsBetween[i] == this) return i;
        return 0;
    }

    /// <summary>Sigma bond (index 0) centered; pi bonds (index 1,2...) offset left/right.</summary>
    static float GetLineOffset(int bondIndex, int bondCount)
    {
        if (bondCount <= 1) return 0f;
        if (bondIndex == 0) return 0f; // sigma bond centered
        const float piOffset = 0.2f; // spacing between lines
        return (bondIndex % 2 == 1 ? -1f : 1f) * ((bondIndex + 1) / 2) * piOffset; // pi: -0.2, +0.2, -0.4, +0.4...
    }

    /// <summary>URP cylinder material; shared across bonds. Uses alpha so bonds read slightly softer in 3D.</summary>
    static Material GetOrCreateBondCylinderMaterial()
    {
        if (bondCylinderMaterial != null) return bondCylinderMaterial;
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (sh == null) sh = Shader.Find("Standard");
        bondCylinderMaterial = sh != null ? new Material(sh) : null;
        if (bondCylinderMaterial != null)
        {
            bondCylinderMaterial.enableInstancing = true;
            ApplyBondVisualColorToMaterial(bondCylinderMaterial);
        }
        return bondCylinderMaterial;
    }

    static void ApplyBondVisualColorToMaterial(Material mat)
    {
        if (mat == null) return;
        var c = BondVisualColor;
        mat.color = c;
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", c);
        // URP Lit / Simple Lit: use transparent surface so alpha is visible
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
    }

    Vector3 PerpendicularToBondDirection(Vector3 deltaNormalized)
    {
        if (useCylinderBondVisual)
        {
            Vector3 perp = Vector3.Cross(deltaNormalized, Vector3.up);
            if (perp.sqrMagnitude < 1e-8f)
                perp = Vector3.Cross(deltaNormalized, Vector3.right);
            return perp.normalized;
        }
        return Vector3.Cross(Vector3.forward, deltaNormalized).normalized;
    }

    static Quaternion BondFrameRotation(Vector3 delta, bool full3DAxis)
    {
        if (delta.sqrMagnitude < 1e-6f) return Quaternion.identity;
        var dn = delta.normalized;
        if (full3DAxis)
            return Quaternion.FromToRotation(Vector3.up, dn);
        return Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg - 90f);
    }

    public static CovalentBond Create(AtomFunction atomA, AtomFunction atomB, ElectronOrbitalFunction sharedOrbital, AtomFunction orbitalContributor, bool animateOrbitalToBond = false)
    {
        if (atomA == null || atomB == null || sharedOrbital == null) return null;
        if (atomA == atomB) return null;

        var bondGo = new GameObject("CovalentBond");
        var bond = bondGo.AddComponent<CovalentBond>();
        bond.Initialize(atomA, atomB, sharedOrbital, orbitalContributor, animateOrbitalToBond);
        return bond;
    }

    void Initialize(AtomFunction a, AtomFunction b, ElectronOrbitalFunction orb, AtomFunction contributor, bool animateOrbitalToBond = false)
    {
        atomA = a;
        atomB = b;
        orbital = orb;
        orbitalContributor = contributor;
        useCylinderBondVisual = Camera.main != null && !Camera.main.orthographic;

        atomA.RegisterBond(this);
        atomB.RegisterBond(this);

        orbital.SetBond(this);
        orbital.SetBondedAtom(null);
        PositionBondTransform(); // Set bond position BEFORE reparenting so orbital keeps correct world pos
        if (!animateOrbitalToBond)
            orbital.transform.SetParent(transform);
        // When animating: defer reparent until after step 2 so orbital keeps its current rotation and can animate to bond
        animatingOrbitalToBondPosition = animateOrbitalToBond;

        atomA.SetupIgnoreCollisions();
        atomB.SetupIgnoreCollisions();

        CreateLineVisual();
        if (animateOrbitalToBond)
        {
            orbitalVisible = true; // Start with orbital visible for step 3 animation
        }
        else
        {
            orbitalVisible = false;
            orbitalToLineAnimProgress = -1f; // Skip animation; show line immediately
        }
        ApplyDisplayMode();
    }

    /// <summary>Animates the bond orbital transforming into a line over the given duration. Optionally fades out another orbital (e.g. target orbital) in parallel.</summary>
    public IEnumerator AnimateOrbitalToLine(float duration, ElectronOrbitalFunction orbitalToFadeOut = null)
    {
        orbitalVisible = true;
        orbitalToLineAnimProgress = 0f;
        orbitalBeingFadedForCharge = orbitalToFadeOut;
        ApplyDisplayMode();

        var fadeOutStartScale = orbitalToFadeOut != null ? orbitalToFadeOut.transform.localScale : Vector3.one;

        for (float t = 0; t < duration; t += Time.deltaTime)
        {
            float s = Mathf.Clamp01(t / duration);
            s = s * s * (3f - 2f * s); // smoothstep
            orbitalToLineAnimProgress = s;
            if (orbitalToFadeOut != null)
                orbitalToFadeOut.transform.localScale = Vector3.Lerp(fadeOutStartScale, Vector3.zero, s);
            yield return null;
        }

        orbitalToLineAnimProgress = -1f;
        orbitalVisible = false;
        orbitalBeingFadedForCharge = null;
        ApplyDisplayMode();
        if (orbitalToFadeOut != null)
            Destroy(orbitalToFadeOut.gameObject);
    }

    /// <summary>Call during bond formation so charge uses merged count (bond orbital + fading orbital) until AnimateOrbitalToLine completes.</summary>
    public void SetOrbitalBeingFaded(ElectronOrbitalFunction orb)
    {
        orbitalBeingFadedForCharge = orb;
    }

    void CreateLineVisual()
    {
        lineVisual = new GameObject("BondLine");
        lineVisual.transform.SetParent(transform);

        bool use3DCollider = Camera.main != null && !Camera.main.orthographic;
        useCylinderBondVisual = use3DCollider;

        if (useCylinderBondVisual)
        {
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        var mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);

            var mf = lineVisual.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            cylinderRenderer = lineVisual.AddComponent<MeshRenderer>();
            var mat = GetOrCreateBondCylinderMaterial();
            if (mat != null)
                cylinderRenderer.sharedMaterial = mat;

            lineCollider3D = lineVisual.AddComponent<BoxCollider>();
            lineCollider3D.isTrigger = true;
            lineCollider3D.size = new Vector3(2f, 2f, 2f);
            lineCollider3D.center = Vector3.zero;
        }
        else
        {
            lineRenderer = lineVisual.AddComponent<SpriteRenderer>();
            lineRenderer.sprite = GetOrCreateLineSprite();
            lineRenderer.color = BondVisualColor;
            lineRenderer.sortingOrder = 0;
            lineRenderer.sortingLayerID = 0;

            lineCollider = lineVisual.AddComponent<BoxCollider2D>();
            lineCollider.isTrigger = true;
        }

        if (use3DCollider && !useCylinderBondVisual)
        {
            lineCollider3D = lineVisual.AddComponent<BoxCollider>();
            lineCollider3D.isTrigger = true;
        }

        lineVisual.AddComponent<BondLineColliderForwarder>();
    }

    /// <summary>Switch cylinder vs sprite bond when toggling orthographic / perspective camera.</summary>
    public void RefreshLineVisualForCameraMode()
    {
        bool wantCylinder = Camera.main != null && !Camera.main.orthographic;
        if (wantCylinder == useCylinderBondVisual && lineVisual != null)
        {
            UpdateBondTransformToCurrentAtoms();
            PositionBondTransform();
            return;
        }

        if (lineVisual != null)
            Destroy(lineVisual);

        lineVisual = null;
        lineRenderer = null;
        cylinderRenderer = null;
        lineCollider = null;
        lineCollider3D = null;

        CreateLineVisual();
        PositionBondTransform();
        ApplyDisplayMode();
    }

    static Sprite GetOrCreateLineSprite()
    {
        if (lineSprite != null) return lineSprite;
        var tex = new Texture2D(16, 32);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 16; x++)
                tex.SetPixel(x, y, Color.white);
        tex.Apply();
        lineSprite = Sprite.Create(tex, new Rect(0, 0, 16, 32), new Vector2(0.5f, 0.5f), 32f);
        return lineSprite;
    }

    void PositionBondTransform()
    {
        if (atomA == null || atomB == null) return;
        var posA = atomA.transform.position;
        var posB = atomB.transform.position;
        var center = (posA + posB) * 0.5f;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;
        transform.position = center;
        transform.rotation = BondFrameRotation(delta, useCylinderBondVisual);
    }

    void LateUpdate()
    {
        if (atomA == null || atomB == null) return;

        var posA = atomA.transform.position;
        var posB = atomB.transform.position;
        var center = (posA + posB) * 0.5f;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;

        if (!animatingOrbitalToBondPosition)
        {
            transform.position = center;
            transform.rotation = BondFrameRotation(delta, useCylinderBondVisual);
        }

        int bondIndex = GetBondIndex();
        int bondCount = atomA.GetBondsTo(atomB);
        float offset = GetLineOffset(bondIndex, bondCount);
        var perpendicular = PerpendicularToBondDirection(delta / distance);
        // Each bond has its own lineVisual + collider; position this bond's line at its offset (not shared)
        lineVisual.transform.position = center + perpendicular * offset;
        lineVisual.transform.localRotation = Quaternion.identity;
        float lineLength = Mathf.Max(0.1f, distance * 0.5f);
        float lineScaleMult = orbitalToLineAnimProgress < 0 ? 1f : orbitalToLineAnimProgress;
        if (useCylinderBondVisual)
        {
            const float radiusScale = 0.15f;
            float halfHeight = 0.5f * lineLength * lineScaleMult;
            lineVisual.transform.localScale = new Vector3(radiusScale, Mathf.Max(0.001f, halfHeight), radiusScale);
        }
        else
            lineVisual.transform.localScale = new Vector3(0.25f, lineLength * lineScaleMult, 1f);

        // Collider size = sprite base size (0.5 x 1) so it scales with the transform and matches the line
        if (lineCollider != null)
        {
            lineCollider.size = new Vector2(0.5f, 1f);
            lineCollider.offset = Vector2.zero;
        }
        if (lineCollider3D != null)
        {
            if (useCylinderBondVisual)
            {
                // Wider than the visible cylinder so raycasts are easier to land; Y matches default cylinder mesh height.
                lineCollider3D.size = new Vector3(2f, 2f, 2f);
                lineCollider3D.center = Vector3.zero;
            }
            else
            {
                lineCollider3D.size = new Vector3(0.5f, 1f, 0.2f);
                lineCollider3D.center = Vector3.zero;
            }
        }

        // Position bond orbital at same world position as lineVisual (center + perpendicular * offset)
        // Skip when user is dragging or when animating orbital to bond (step 2)
        bool userDragging = orbitalVisible && orbitalToLineAnimProgress < 0;
        if (orbital != null && !userDragging && !animatingOrbitalToBondPosition)
        {
            var orbWorldPos = center + perpendicular * offset;
            var orbRot = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
            if (orbitalRotationFlipped) orbRot = orbRot * Quaternion.Euler(0f, 0f, 180f);
            orbital.transform.position = orbWorldPos;
            orbital.transform.rotation = orbRot;
            float orbScale = orbitalToLineAnimProgress < 0 ? 0.6f : 0.6f * (1f - orbitalToLineAnimProgress);
            orbital.transform.localScale = Vector3.one * Mathf.Max(0.01f, orbScale);
        }
    }

    void ApplyDisplayMode()
    {
        if (orbital != null)
        {
            orbital.SetVisualsEnabled(orbitalVisible);
            orbital.SetPointerBlocked(true);
        }
        bool animating = orbitalToLineAnimProgress >= 0 && orbitalToLineAnimProgress < 1f;
        bool showLine = !orbitalVisible || animating; // Show line/cylinder from start of step 3 so it grows with orbital shrinking
        if (lineRenderer != null) lineRenderer.enabled = showLine;
        if (cylinderRenderer != null) cylinderRenderer.enabled = showLine;
        if (lineCollider != null) lineCollider.enabled = !orbitalVisible && orbitalToLineAnimProgress < 0;
        if (lineCollider3D != null) lineCollider3D.enabled = !orbitalVisible && orbitalToLineAnimProgress < 0;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (orbitalVisible) return;
        if (atomA == null || atomB == null) return;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;
        var dir = delta / distance;
        var right = PerpendicularToBondDirection(dir);
        int bondIndex = GetBondIndex();
        int bondCount = atomA.GetBondsTo(atomB);
        float offset = GetLineOffset(bondIndex, bondCount);
        var lineCenter = (atomA.transform.position + atomB.transform.position) * 0.5f + right * offset;
        var lineStart = lineCenter - dir * (distance * 0.5f);
        var lineEnd = lineCenter + dir * (distance * 0.5f);
        if (!PassesBondPointerSlop(eventData, lineStart, lineEnd))
            return;
        orbitalVisible = true;
        forwardedPressToOrbital = false;
        storedPressData = eventData;
        ApplyDisplayMode();
        // Bond-line pose before any drag so the shared orbital does not start from a stale world pose.
        UpdateBondTransformToCurrentAtoms();
        SnapOrbitalToBondPosition();
    }

    static float DistanceToLineSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var ap = p - a;
        var t = Mathf.Clamp01(Vector3.Dot(ap, ab) / (ab.sqrMagnitude + 0.0001f));
        var nearest = a + t * ab;
        return Vector3.Distance(p, nearest);
    }

    /// <summary>
    /// 2D bonds: click must be near the segment on the work plane. 3D cylinder: planar projection is wrong for
    /// out-of-plane bonds — trust the physics hit on this bond's line mesh, or measure ray vs segment in 3D.
    /// </summary>
    bool PassesBondPointerSlop(PointerEventData eventData, Vector3 lineStart, Vector3 lineEnd)
    {
        if (useCylinderBondVisual)
        {
            var hitGo = eventData.pointerCurrentRaycast.gameObject;
            if (lineVisual != null && hitGo != null &&
                (hitGo == lineVisual || hitGo.transform == lineVisual.transform))
                return true;
            var cam = Camera.main;
            if (cam != null && !cam.orthographic)
            {
                var ray = cam.ScreenPointToRay(eventData.position);
                if (ApproxDistanceRayToSegment(ray, lineStart, lineEnd) <= 0.45f)
                    return true;
            }
            return false;
        }

        var clickWorld = PlanarPointerInteraction.ScreenToWorldPoint(eventData.position);
        return DistanceToLineSegment(clickWorld, lineStart, lineEnd) <= 0.25f;
    }

    static float ApproxDistanceRayToSegment(Ray ray, Vector3 a, Vector3 b)
    {
        float best = float.MaxValue;
        for (int i = 0; i <= 12; i++)
        {
            float t = i / 12f;
            var p = Vector3.Lerp(a, b, t);
            float proj = Vector3.Dot(p - ray.origin, ray.direction);
            if (proj < 0f) proj = 0f;
            var onRay = ray.origin + ray.direction * proj;
            float d = Vector3.Distance(onRay, p);
            if (d < best) best = d;
        }
        return best;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!orbitalVisible || orbital == null) return;
        if (!forwardedPressToOrbital)
        {
            UpdateBondTransformToCurrentAtoms();
            SnapOrbitalToBondPosition();
            forwardedPressToOrbital = true;
            orbital.OnPointerDown(storedPressData);
        }
        orbital.OnDrag(eventData);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (!orbitalVisible) return;
        if (forwardedPressToOrbital && orbital != null)
            orbital.OnPointerUp(eventData);
        else
            ReturnToLineView();
    }

    public void ReturnToLineView()
    {
        orbitalVisible = false;
        ApplyDisplayMode();
    }

    public void NotifyElectronCountChanged()
    {
        ApplyDisplayMode(); // Re-apply so newly created electrons respect orbitalVisible (hidden when bond shows as line)
        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();
    }

    /// <param name="userDragBondCylinderBreak">When true (bond broken by dragging the bond orbital), both bonding electrons stay on the dragged atom's orbital.</param>
    /// <param name="instantRedistributionForDestroyPartner">When true (e.g. edit mode replaces H then destroys it), skip the 3D coroutine and apply final σ + VSEPR layout immediately so no async code holds the removed atom.</param>
    public void BreakBond(AtomFunction returnOrbitalTo, bool userDragBondCylinderBreak = false, bool instantRedistributionForDestroyPartner = false)
    {
        if (orbital == null) return;

        int piBeforeA = atomA != null ? atomA.GetPiBondCount() : 0;
        int piBeforeB = atomB != null ? atomB.GetPiBondCount() : 0;
        float? piBondAngleFromA = null;
        float? piBondAngleFromB = null;
        Vector3 dirAtoB = Vector3.zero;
        Vector3 dirBtoA = Vector3.zero;
        if (atomA != null && atomB != null)
        {
            dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
            dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;
            if (dirAtoB.sqrMagnitude >= 0.01f)
                piBondAngleFromA = OrbitalAngleUtility.DirectionToAngleWorld(dirAtoB);
            if (dirBtoA.sqrMagnitude >= 0.01f)
                piBondAngleFromB = OrbitalAngleUtility.DirectionToAngleWorld(dirBtoA);
        }

        // Bond-line pose for orbitals (must capture before UnregisterBond — GetOrbitalTargetWorldState uses bond topology).
        var (bondOrbitalWorldPos, bondOrbitalWorldRot) = GetOrbitalTargetWorldState();

        atomA?.UnregisterBond(this);
        atomB?.UnregisterBond(this);

        // π break (σ remains): keep the two centers fixed; σ-relax moves substituents only.
        HashSet<AtomFunction> sigmaRelaxPins = null;
        if (atomA != null && atomB != null && atomA.GetBondsTo(atomB) > 0)
            sigmaRelaxPins = new HashSet<AtomFunction> { atomA, atomB };
        // Full σ cleavage (no bond left between A and B): same CH₃–CH₃ rules — empty along broken axis, σ neighbors tetrahedral. π break: empty ⊥ remaining electron framework.
        bool sigmaCleavageBetweenPartners = atomA != null && atomB != null && atomA.GetBondsTo(atomB) == 0;

        int totalElectrons = orbital.ElectronCount;
        var otherAtom = returnOrbitalTo == atomA ? atomB : atomA;
        var prefab = returnOrbitalTo.OrbitalPrefab ?? otherAtom?.OrbitalPrefab;
        if (prefab == null) return;

        orbital.SetBond(null);
        orbital.SetBondedAtom(returnOrbitalTo);
        orbital.SetPointerBlocked(false);
        orbital.SetVisualsEnabled(true);
        // Former bond axis: returned lobe points toward the other atom (guide fallback when no π and no multi-bond ref).
        var dirToOther = (otherAtom.transform.position - returnOrbitalTo.transform.position).normalized;
        if (dirToOther.sqrMagnitude < 0.01f) dirToOther = Vector3.right;
        var slotA = returnOrbitalTo.GetSlotForNewOrbital(dirToOther, orbital);
        orbital.transform.SetParent(returnOrbitalTo.transform);
        orbital.transform.localScale = Vector3.one * 0.6f;
        ElectronOrbitalFunction newOrbital;
        var dirToReturn = (returnOrbitalTo.transform.position - otherAtom.transform.position).normalized;
        if (dirToReturn.sqrMagnitude < 0.01f) dirToReturn = Vector3.left;

        (Vector3 position, Quaternion rotation) slotB = default;
        if (userDragBondCylinderBreak)
        {
            int maxOnReturn = ElectronOrbitalFunction.MaxElectrons;
            orbital.ElectronCount = Mathf.Clamp(totalElectrons, 0, maxOnReturn);
            int spill = Mathf.Max(0, totalElectrons - orbital.ElectronCount);
            returnOrbitalTo.BondOrbital(orbital);

            newOrbital = Instantiate(prefab);
            newOrbital.transform.localScale = Vector3.one * 0.6f;
            slotB = otherAtom.GetSlotForNewOrbital(dirToReturn, newOrbital);
            newOrbital.transform.SetParent(otherAtom.transform);
            newOrbital.ElectronCount = Mathf.Clamp(spill, 0, maxOnReturn);
            newOrbital.SetBondedAtom(otherAtom);
            otherAtom.BondOrbital(newOrbital);
        }
        else
        {
            orbital.ElectronCount = totalElectrons > 0 ? totalElectrons - 1 : 0;
            returnOrbitalTo.BondOrbital(orbital);

            newOrbital = Instantiate(prefab);
            newOrbital.transform.localScale = Vector3.one * 0.6f;
            slotB = otherAtom.GetSlotForNewOrbital(dirToReturn, newOrbital);
            newOrbital.transform.SetParent(otherAtom.transform);
            newOrbital.ElectronCount = totalElectrons > 0 ? 1 : 0;
            newOrbital.SetBondedAtom(otherAtom);
            otherAtom.BondOrbital(newOrbital);
        }

        atomA?.TryTransferElectronFromLonePairToEmptyOrbitals();
        atomB?.TryTransferElectronFromLonePairToEmptyOrbitals();
        atomA?.RefreshCharge();
        atomB?.RefreshCharge();

        int piAfterA = atomA != null ? atomA.GetPiBondCount() : 0;
        int piAfterB = atomB != null ? atomB.GetPiBondCount() : 0;
        if (AtomFunction.DebugLogCcBondBreakGeometry)
        {
            string cc = $"[bond-break-cc] BreakBond sigmaCleavageBetweenPartners={sigmaCleavageBetweenPartners} piAfterA={piAfterA} piAfterB={piAfterB} (true=full σ cleavage CH₃–CH₃ style; false=π component break σ remains)";
            Debug.Log(cc);
            ProjectAgentDebugLog.MirrorToProjectDotLog(cc);
        }
        bool sigmaOnlyUnchangedPiBoth = piBeforeA == piAfterA && piBeforeB == piAfterB;
        Vector3? refWorldA = dirAtoB.sqrMagnitude >= 0.01f ? dirAtoB : (Vector3?)null;
        Vector3? refWorldB = dirBtoA.sqrMagnitude >= 0.01f ? dirBtoA : (Vector3?)null;

        // Edit replace-H / eraser: H is destroyed next frame. VSEPR preview applies σ-relax in a temp pose then restores
        // atom positions while still using preview slot locals — the heavy center's other σ orbitals look "rotated" for no reason.
        bool preserveHeavyFrameworkForEditInstantHBreak = instantRedistributionForDestroyPartner
            && otherAtom != null && otherAtom.AtomicNumber == 1
            && returnOrbitalTo != null && returnOrbitalTo.AtomicNumber > 1;
        var slotAKeep = slotA;
        var slotBKeep = slotB;

        // Perspective: animation targets = full VSEPR lone layout (preview), not GetSlotForNewOrbital alone.
        // Orbitals must be at the bond-line world pose before preview: Redistribute / σ-relax can depend on that pose,
        // and CoAnimate builds σ moves after this snap — mismatch caused preview sumEnd vs final and C lone newDir drift.
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            orbital.transform.SetPositionAndRotation(bondOrbitalWorldPos, bondOrbitalWorldRot);
            newOrbital.transform.SetPositionAndRotation(bondOrbitalWorldPos, bondOrbitalWorldRot);
            if (!preserveHeavyFrameworkForEditInstantHBreak &&
                TryPreviewVseprSlotsForBreakBond(atomA, atomB, orbital, newOrbital, piAfterA, piAfterB, piBondAngleFromA, piBondAngleFromB, refWorldA, refWorldB, sigmaRelaxPins, sigmaOnlyUnchangedPiBoth, out var vseprSlotA, out var vseprSlotB))
            {
                slotA = vseprSlotA;
                slotB = vseprSlotB;
            }
            else if (preserveHeavyFrameworkForEditInstantHBreak)
            {
                orbital.transform.localPosition = slotAKeep.position;
                orbital.transform.localRotation = slotAKeep.rotation;
                newOrbital.transform.localPosition = slotBKeep.position;
                newOrbital.transform.localRotation = slotBKeep.rotation;
            }

            // Homolytic C–C (same Z, 2e σ bond → 1e + 1e): pair the same tetrahedral vertex index on both centers so preview / lex tie-break cannot pick mismatched slots.
            if (!preserveHeavyFrameworkForEditInstantHBreak && !userDragBondCylinderBreak && atomA != null && atomB != null && atomA.AtomicNumber == atomB.AtomicNumber &&
                orbital != null && newOrbital != null && orbital.ElectronCount == 1 && newOrbital.ElectronCount == 1)
                TrySymmetricizeHomolyticBreakBondSlotLocals(atomA, atomB, orbital, newOrbital, ref slotA, ref slotB);
        }
        else
        {
            orbital.transform.localPosition = slotA.position;
            orbital.transform.localRotation = slotA.rotation;
            newOrbital.transform.localPosition = slotB.position;
            newOrbital.transform.localRotation = slotB.rotation;
        }

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();
        atomA?.SetInteractionBlocked(false);
        atomB?.SetInteractionBlocked(false);

        // Perspective: animate σ neighbors + bond→slot orbital motion, then lone-orbital redistribution.
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && (atomA != null || atomB != null))
        {
            if (instantRedistributionForDestroyPartner)
            {
                ApplyInstantBreakBondRedistribution3D(
                    atomA, atomB,
                    piBeforeA, piBeforeB,
                    piAfterA == 0 ? piBondAngleFromA : null, piAfterA == 0 ? refWorldA : null, piAfterA,
                    piAfterB == 0 ? piBondAngleFromB : null, piAfterB == 0 ? refWorldB : null, piAfterB,
                    orbital, newOrbital,
                    returnOrbitalTo, otherAtom,
                    slotA, slotB,
                    sigmaRelaxPins,
                    sigmaCleavageBetweenPartners,
                    preserveHeavyFrameworkForEditInstantHBreak);
            }
            else
            {
                var runner = atomA != null ? atomA : atomB;
                runner.StartCoroutine(CoAnimateBreakBondRedistribution(
                    atomA, atomB,
                    piBeforeA, piBeforeB,
                    piAfterA == 0 ? piBondAngleFromA : null, piAfterA == 0 ? refWorldA : null, piAfterA,
                    piAfterB == 0 ? piBondAngleFromB : null, piAfterB == 0 ? refWorldB : null, piAfterB,
                    orbital, newOrbital,
                    returnOrbitalTo, otherAtom,
                    slotA, slotB,
                    bondOrbitalWorldPos, bondOrbitalWorldRot,
                    sigmaRelaxPins,
                    userDragBondCylinderBreak,
                    sigmaCleavageBetweenPartners));
            }
        }
        else
        {
            atomA?.RedistributeOrbitals(piAfterA == 0 ? piBondAngleFromA : null, piAfterA == 0 ? refWorldA : null, relaxCoplanarSigmaToTetrahedral: true, pinAtomsForSigmaRelax: sigmaRelaxPins, bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomA, orbital, newOrbital));
            atomB?.RedistributeOrbitals(piAfterB == 0 ? piBondAngleFromB : null, piAfterB == 0 ? refWorldB : null, relaxCoplanarSigmaToTetrahedral: true, pinAtomsForSigmaRelax: sigmaRelaxPins, bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomB, orbital, newOrbital));
            atomA?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(piAfterA == 0 ? refWorldA : null, BondBreakGuideLoneOrbitalOnAtom(atomA, orbital, newOrbital), sigmaCleavageBetweenPartners);
            atomB?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(piAfterB == 0 ? refWorldB : null, BondBreakGuideLoneOrbitalOnAtom(atomB, orbital, newOrbital), sigmaCleavageBetweenPartners);
        }

        orbital = null;
        Destroy(gameObject);
    }

    /// <summary>Duration for σ-neighbor + orbital motion after a bond break (3D); σ-neighbor relax + optional lone layout after.</summary>
    const float BreakRedistributionDuration = 0.55f;

    static List<(AtomFunction atom, Vector3 pos, Quaternion rot)> SnapshotMoleculeAtoms(IEnumerable<AtomFunction> atoms)
    {
        var list = new List<(AtomFunction, Vector3, Quaternion)>();
        foreach (var a in atoms)
            if (a != null) list.Add((a, a.transform.position, a.transform.rotation));
        return list;
    }

    static void RestoreMoleculeAtoms(List<(AtomFunction atom, Vector3 pos, Quaternion rot)> snap)
    {
        foreach (var (atom, pos, rot) in snap)
            if (atom != null) atom.transform.SetPositionAndRotation(pos, rot);
    }

    static void CollectMoleculeAtoms(AtomFunction atom, HashSet<AtomFunction> dst)
    {
        if (atom == null) return;
        foreach (var a in atom.GetConnectedMolecule())
            if (a != null) dst.Add(a);
    }

    static List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> FilterRedistExcludingBreakOrbitals(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB)
    {
        var r = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        foreach (var e in list)
        {
            if (e.orb != null && e.orb != orbA && e.orb != orbB)
                r.Add(e);
        }
        return r;
    }

    /// <summary>σ-relax end poses for bond-break animation: tet (AX₃E), linear, open-from-linear, trigonal (skipped for 3σ+carbocation empty), then sp² carbocation σ plane — aligned with <see cref="AtomFunction.RedistributeOrbitals"/> bond-break path.</summary>
    static Dictionary<AtomFunction, (Vector3 start, Vector3 end)> BuildSigmaRelaxMovesForBreakBond(
        AtomFunction atomA,
        AtomFunction atomB,
        float? piAngleA, Vector3? refA, int piAfterA,
        float? piAngleB, Vector3? refB, int piAfterB,
        HashSet<AtomFunction> pinSigmaRelaxAtoms)
    {
        var moves = new Dictionary<AtomFunction, (Vector3 start, Vector3 end)>();
        void Collect(AtomFunction atom, float? piAngle, Vector3? refWorld, int piAfter)
        {
            if (atom == null) return;
            Vector3 refLocal = atom.GetRedistributeReferenceLocal(piAfter == 0 ? piAngle : null, piAfter == 0 ? refWorld : null);
            if (atom.TryComputeCoplanarTetrahedralSigmaNeighborRelaxTargets(refLocal, out var targets, pinSigmaRelaxAtoms) && targets != null)
            {
                foreach (var (n, end) in targets)
                    moves[n] = (n.transform.position, end);
            }
            if (atom.TryComputeLinearSigmaNeighborRelaxTargets(refLocal, out var lin, pinSigmaRelaxAtoms) && lin != null)
            {
                foreach (var (n, end) in lin)
                    moves[n] = (n.transform.position, end);
            }
            if (atom.TryComputeOpenedTrigonalSigmaNeighborRelaxTargetsFromLinear(refLocal, out var opened, pinSigmaRelaxAtoms) && opened != null)
            {
                foreach (var (n, end) in opened)
                    moves[n] = (n.transform.position, end);
            }
            // Match RedistributeOrbitals3D: generic trigonal relax places one σ along ref (same axis as empty p) — skip for carbocation; sp² placement runs next.
            if (!atom.IsSp2BondBreakEmptyAlongRefCase() &&
                atom.TryComputeTrigonalPlanarSigmaNeighborRelaxTargets(refLocal, out var trig) && trig != null)
            {
                foreach (var (n, end) in trig)
                    moves[n] = (n.transform.position, end);
            }
            if (piAfter == 0 && refWorld.HasValue &&
                atom.TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets(refWorld, pinSigmaRelaxAtoms, out var sp2cc) && sp2cc != null)
            {
                foreach (var (n, end) in sp2cc)
                    moves[n] = (n.transform.position, end);
            }
        }
        Collect(atomA, piAngleA, refA, piAfterA);
        Collect(atomB, piAngleB, refB, piAfterB);
        return moves;
    }

    /// <summary>On each atom, the lobe parented there that came from the broken bond (return orbital vs new orbital) — VSEPR guide pin.</summary>
    static ElectronOrbitalFunction BondBreakGuideLoneOrbitalOnAtom(AtomFunction atom, ElectronOrbitalFunction orbOnA, ElectronOrbitalFunction orbOnB)
    {
        if (atom == null) return null;
        if (orbOnA != null && orbOnA.transform.parent == atom.transform) return orbOnA;
        if (orbOnB != null && orbOnB.transform.parent == atom.transform) return orbOnB;
        return null;
    }

    /// <summary>Runs full <see cref="AtomFunction.RedistributeOrbitals"/> on both endpoints, then restores atom transforms and bonded-orbital locals.
    /// σ-relax can move neighbor atoms during preview; reading <c>localPosition</c> before restore would bake parent displacement into locals,
    /// so the animation lerp would aim at the wrong world pose until the final redistribute. We capture world pose before restore and convert
    /// back to locals using the restored parent so targets match the pre-break molecule layout.
    /// <see cref="RestoreMoleculeAtoms"/> does not reset child orbitals — without also restoring orbital locals, preview would teleport lone lobes to the final VSEPR pose and <see cref="CoAnimateBreakBondRedistribution"/> would see no movement.</summary>
    static bool TryPreviewVseprSlotsForBreakBond(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        int piAfterA,
        int piAfterB,
        float? piBondAngleFromA,
        float? piBondAngleFromB,
        Vector3? refWorldA,
        Vector3? refWorldB,
        HashSet<AtomFunction> pinSigmaRelaxAtoms,
        bool skipLoneLobeLayoutForSigmaOnlyBreak,
        out (Vector3 position, Quaternion rotation) slotA,
        out (Vector3 position, Quaternion rotation) slotB)
    {
        slotA = default;
        slotB = default;
        if (orbA == null || orbB == null) return false;

        var atoms = new HashSet<AtomFunction>();
        CollectMoleculeAtoms(atomA, atoms);
        CollectMoleculeAtoms(atomB, atoms);
        var snap = SnapshotMoleculeAtoms(atoms);
        var orbitalSnapPrePreview = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
        foreach (var a in atoms)
            a?.AppendBondedOrbitalLocalSnapshot(orbitalSnapPrePreview);

        // Apply the same σ-relax end poses the animation will use, then lone VSEPR only. Sequential atomA→atomB
        // Redistribute with built-in σ-relax used different intermediate geometry than applying both relax targets first.
        Dictionary<AtomFunction, (Vector3 start, Vector3 end)> sigmaMoves;
        void ApplyFreshSigmaRelaxMoves()
        {
            sigmaMoves = BuildSigmaRelaxMovesForBreakBond(
                atomA, atomB,
                piAfterA == 0 ? piBondAngleFromA : null, piAfterA == 0 ? refWorldA : null, piAfterA,
                piAfterB == 0 ? piBondAngleFromB : null, piAfterB == 0 ? refWorldB : null, piAfterB,
                pinSigmaRelaxAtoms);
            foreach (var kv in sigmaMoves)
                kv.Key.transform.position = kv.Value.end;
        }

        ApplyFreshSigmaRelaxMoves();
        bool sigmaRelaxPreAppliedPreview = sigmaMoves != null && sigmaMoves.Count > 0;

        // Pass 1: σ-relaxed geometry + full VSEPR on occupied lone lobes (0e excluded); bond-break guide lobe pinned.
        atomA?.RedistributeOrbitals(piAfterA == 0 ? piBondAngleFromA : null, piAfterA == 0 ? refWorldA : null, relaxCoplanarSigmaToTetrahedral: true, skipLoneLobeLayout: skipLoneLobeLayoutForSigmaOnlyBreak, pinAtomsForSigmaRelax: pinSigmaRelaxAtoms, skipSigmaNeighborRelax: sigmaRelaxPreAppliedPreview, bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB));
        atomB?.RedistributeOrbitals(piAfterB == 0 ? piBondAngleFromB : null, piAfterB == 0 ? refWorldB : null, relaxCoplanarSigmaToTetrahedral: true, skipLoneLobeLayout: skipLoneLobeLayoutForSigmaOnlyBreak, pinAtomsForSigmaRelax: pinSigmaRelaxAtoms, skipSigmaNeighborRelax: sigmaRelaxPreAppliedPreview, bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB));

        Vector3 worldA = orbA.transform.position;
        Quaternion rotWorldA = orbA.transform.rotation;
        Vector3 worldB = orbB.transform.position;
        Quaternion rotWorldB = orbB.transform.rotation;

        RestoreMoleculeAtoms(snap);

        Transform parentA = orbA.transform.parent;
        Transform parentB = orbB.transform.parent;
        if (parentA == null || parentB == null) return false;

        slotA = (parentA.InverseTransformPoint(worldA), Quaternion.Inverse(parentA.rotation) * rotWorldA);
        slotB = (parentB.InverseTransformPoint(worldB), Quaternion.Inverse(parentB.rotation) * rotWorldB);

        // Pass 2: match CoAnimateBreakBondRedistribution — σ ends applied, orbitals snapped to pass-1 slot locals, then
        // lone VSEPR only. Rebuild σ moves here: preview pass 1 can change neighbor geometry so stale end targets
        // would disagree with CoAnimate's BuildSigmaRelaxMovesForBreakBond (sumEnd / lone layout mismatch).
        ApplyFreshSigmaRelaxMoves();
        sigmaRelaxPreAppliedPreview = sigmaMoves != null && sigmaMoves.Count > 0;
        if (orbA != null && parentA != null)
        {
            orbA.transform.localPosition = slotA.position;
            orbA.transform.localRotation = slotA.rotation;
        }
        if (orbB != null && parentB != null)
        {
            orbB.transform.localPosition = slotB.position;
            orbB.transform.localRotation = slotB.rotation;
        }
        atomA?.RedistributeOrbitals(piAfterA == 0 ? piBondAngleFromA : null, piAfterA == 0 ? refWorldA : null, relaxCoplanarSigmaToTetrahedral: true, skipLoneLobeLayout: skipLoneLobeLayoutForSigmaOnlyBreak, pinAtomsForSigmaRelax: pinSigmaRelaxAtoms, skipSigmaNeighborRelax: sigmaRelaxPreAppliedPreview, bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB));
        atomB?.RedistributeOrbitals(piAfterB == 0 ? piBondAngleFromB : null, piAfterB == 0 ? refWorldB : null, relaxCoplanarSigmaToTetrahedral: true, skipLoneLobeLayout: skipLoneLobeLayoutForSigmaOnlyBreak, pinAtomsForSigmaRelax: pinSigmaRelaxAtoms, skipSigmaNeighborRelax: sigmaRelaxPreAppliedPreview, bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB));

        RestoreMoleculeAtoms(snap);
        // RestoreMoleculeAtoms only resets atom transforms; RedistributeOrbitals mutates lone-lobe locals. Without restoring
        // those, orbitals stay at the post-VSEPR pose and CoAnimate sees start==target (no ease).
        AtomFunction.RestoreBondedOrbitalLocalSnapshot(orbitalSnapPrePreview);
        return true;
    }

    /// <summary>After VSEPR preview, align partner B's break-lobe slot to the same ideal vertex index as A when they differ (symmetric σ homolysis).</summary>
    static void TrySymmetricizeHomolyticBreakBondSlotLocals(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ref (Vector3 position, Quaternion rotation) slotA,
        ref (Vector3 position, Quaternion rotation) slotB)
    {
        int n = atomA.GetOrbitalSlotCount();
        if (n != atomB.GetOrbitalSlotCount() || n < 2) return;

        var slots = VseprLayout.GetIdealLocalDirections(n);
        float slotToleranceDeg = 360f / (2f * Mathf.Max(1, n));

        Vector3 tipA = (slotA.rotation * Vector3.right).normalized;
        Vector3 tipB = (slotB.rotation * Vector3.right).normalized;

        int idxA = AtomFunction.ClosestIdealLocalDirectionIndex(tipA, n);
        int idxB = AtomFunction.ClosestIdealLocalDirectionIndex(tipB, n);
        if (idxA == idxB) return;

        Vector3 targetDir = slots[idxA].normalized;
        if (!AtomFunction.IdealSlotDirectionUnoccupiedFor3D(atomB, targetDir, slotToleranceDeg, orbB)) return;

        slotB = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(targetDir, atomB.BondRadius);
    }

    static Vector3 BreakBondOrbitalTipLocal(ElectronOrbitalFunction orb) =>
        orb != null ? (orb.transform.localRotation * Vector3.right).normalized : Vector3.zero;

    static void LogBreakBondNewOrbitals(string phase, ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB, AtomFunction parentA, AtomFunction parentB)
    {
        if (!DebugLogBreakBondOrbitalAngles) return;
        void One(string label, ElectronOrbitalFunction o, AtomFunction p)
        {
            if (o == null)
            {
                LogBondBreakLine($"[bond-break] {phase} {label}: (null)");
                return;
            }
            Vector3 tipL = BreakBondOrbitalTipLocal(o);
            Vector3 tipW = p != null ? p.transform.TransformDirection(tipL) : o.transform.TransformDirection(tipL);
            if (tipW.sqrMagnitude > 1e-8f) tipW.Normalize();
            LogBondBreakLine(
                $"[bond-break] {phase} {label}: orbId={o.GetInstanceID()} parentZ={p?.AtomicNumber} localEuler={o.transform.localEulerAngles} tipLocal={tipL} tipWorld={tipW}");
        }
        One("orbA", orbA, parentA);
        One("orbB", orbB, parentB);
    }

    /// <summary>Same end state as <see cref="CoAnimateBreakBondRedistribution"/> without lerping — for breaks where one atom is destroyed in the same frame (edit-mode H replacement, eraser).</summary>
    static void ApplyInstantBreakBondRedistribution3D(
        AtomFunction atomA,
        AtomFunction atomB,
        int piBeforeA,
        int piBeforeB,
        float? piAngleA, Vector3? refA, int piAfterA,
        float? piAngleB, Vector3? refB, int piAfterB,
        ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB,
        AtomFunction parentA, AtomFunction parentB,
        (Vector3 position, Quaternion rotation) slotLocalA,
        (Vector3 position, Quaternion rotation) slotLocalB,
        HashSet<AtomFunction> pinSigmaRelaxAtoms,
        bool sigmaCleavageBetweenPartners,
        bool preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak)
    {
        bool sigmaOnlyUnchangedPiBoth = piBeforeA == piAfterA && piBeforeB == piAfterB;
        var moves = BuildSigmaRelaxMovesForBreakBond(atomA, atomB, piAngleA, refA, piAfterA, piAngleB, refB, piAfterB, pinSigmaRelaxAtoms);
        if (!preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak)
        {
            foreach (var kv in moves)
            {
                if (kv.Key != null)
                    kv.Key.transform.position = kv.Value.end;
            }
        }
        if (orbA != null && parentA != null)
        {
            orbA.transform.localPosition = slotLocalA.position;
            orbA.transform.localRotation = slotLocalA.rotation;
        }
        if (orbB != null && parentB != null)
        {
            orbB.transform.localPosition = slotLocalB.position;
            orbB.transform.localRotation = slotLocalB.rotation;
        }

        bool skipFinalSigmaRelax = moves.Count > 0;
        bool heavyA = atomA != null && atomA.AtomicNumber > 1;
        bool heavyB = atomB != null && atomB.AtomicNumber > 1;
        bool minimalA = preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak && heavyA;
        bool minimalB = preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak && heavyB;

        atomA?.RedistributeOrbitals(
            piAfterA == 0 ? piAngleA : null,
            piAfterA == 0 ? refA : null,
            relaxCoplanarSigmaToTetrahedral: !minimalA,
            skipLoneLobeLayout: sigmaOnlyUnchangedPiBoth,
            pinAtomsForSigmaRelax: pinSigmaRelaxAtoms,
            skipSigmaNeighborRelax: skipFinalSigmaRelax || minimalA,
            bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB));
        atomB?.RedistributeOrbitals(
            piAfterB == 0 ? piAngleB : null,
            piAfterB == 0 ? refB : null,
            relaxCoplanarSigmaToTetrahedral: !minimalB,
            skipLoneLobeLayout: sigmaOnlyUnchangedPiBoth,
            pinAtomsForSigmaRelax: pinSigmaRelaxAtoms,
            skipSigmaNeighborRelax: skipFinalSigmaRelax || minimalB,
            bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB));

        if (!minimalA)
            atomA?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(piAfterA == 0 ? refA : null, BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB), sigmaCleavageBetweenPartners);
        if (!minimalB)
            atomB?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(piAfterB == 0 ? refB : null, BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB), sigmaCleavageBetweenPartners);

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.RefreshElectronSyncOnBondedOrbitals();
        atomB?.RefreshElectronSyncOnBondedOrbitals();
        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    static IEnumerator CoAnimateBreakBondRedistribution(
        AtomFunction atomA,
        AtomFunction atomB,
        int piBeforeA,
        int piBeforeB,
        float? piAngleA, Vector3? refA, int piAfterA,
        float? piAngleB, Vector3? refB, int piAfterB,
        ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB,
        AtomFunction parentA, AtomFunction parentB,
        (Vector3 position, Quaternion rotation) slotLocalA,
        (Vector3 position, Quaternion rotation) slotLocalB,
        Vector3 bondWorldPos, Quaternion bondWorldRot,
        HashSet<AtomFunction> pinSigmaRelaxAtoms,
        bool userDragBondCylinderBreak,
        bool sigmaCleavageBetweenPartners)
    {
        // σ-only break (π unchanged on both ends): ensure bond→slot orbital lerp runs; otherwise epsilon + empty GetRedistributeTargets can skip the whole animation loop.
        bool sigmaOnlyUnchangedPiBoth = piBeforeA == piAfterA && piBeforeB == piAfterB;

        var moves = BuildSigmaRelaxMovesForBreakBond(atomA, atomB, piAngleA, refA, piAfterA, piAngleB, refB, piAfterB, pinSigmaRelaxAtoms);
        bool sigmaRelaxPreApplied = moves.Count > 0;

        // Slot targets at t=0 (parent may move during σ-neighbor relaxation — lerp must use per-frame slot world pose).
        Vector3 endWorldA0 = parentA != null && orbA != null
            ? parentA.transform.TransformPoint(slotLocalA.position)
            : bondWorldPos;
        Quaternion endRotA0 = parentA != null && orbA != null
            ? parentA.transform.rotation * slotLocalA.rotation
            : bondWorldRot;
        Vector3 endWorldB0 = parentB != null && orbB != null
            ? parentB.transform.TransformPoint(slotLocalB.position)
            : bondWorldPos;
        Quaternion endRotB0 = parentB != null && orbB != null
            ? parentB.transform.rotation * slotLocalB.rotation
            : bondWorldRot;

        const float posEps = 0.0004f;
        const float rotEps = 0.35f;
        bool animOrbA = orbA != null && parentA != null &&
            (Vector3.SqrMagnitude(bondWorldPos - endWorldA0) > posEps * posEps || Quaternion.Angle(bondWorldRot, endRotA0) > rotEps);
        bool animOrbB = orbB != null && parentB != null &&
            (Vector3.SqrMagnitude(bondWorldPos - endWorldB0) > posEps * posEps || Quaternion.Angle(bondWorldRot, endRotB0) > rotEps);
        if (sigmaOnlyUnchangedPiBoth)
        {
            if (orbA != null && parentA != null) animOrbA = true;
            if (orbB != null && parentB != null) animOrbB = true;
        }

        // π count changed on an atom → use normal GetRedistributeTargets π check; σ-only break → bypass so layout still runs.
        bool topologyBypassA = piBeforeA == piAfterA;
        bool topologyBypassB = piBeforeB == piAfterB;

        var snapAtoms = new HashSet<AtomFunction>();
        CollectMoleculeAtoms(atomA, snapAtoms);
        CollectMoleculeAtoms(atomB, snapAtoms);
        foreach (var kv in moves)
            if (kv.Key != null) snapAtoms.Add(kv.Key);
        var moleculeSnap = SnapshotMoleculeAtoms(snapAtoms);

        foreach (var kv in moves)
            kv.Key.transform.position = kv.Value.end;
        if (orbA != null && parentA != null)
        {
            orbA.transform.localPosition = slotLocalA.position;
            orbA.transform.localRotation = slotLocalA.rotation;
        }
        if (orbB != null && parentB != null)
        {
            orbB.transform.localPosition = slotLocalB.position;
            orbB.transform.localRotation = slotLocalB.rotation;
        }

        var redistARaw = atomA != null ? atomA.GetRedistributeTargets(piBeforeA, null, topologyBypassA, sigmaOnlyUnchangedPiBoth, piAfterA == 0 ? piAngleA : null, piAfterA == 0 ? refA : null, BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB), sigmaNeighborCountBefore: -1) : new List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)>();
        var redistBRaw = atomB != null ? atomB.GetRedistributeTargets(piBeforeB, null, topologyBypassB, sigmaOnlyUnchangedPiBoth, piAfterB == 0 ? piAngleB : null, piAfterB == 0 ? refB : null, BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB), sigmaNeighborCountBefore: -1) : new List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)>();
        var redistA = FilterRedistExcludingBreakOrbitals(redistARaw, orbA, orbB);
        var redistB = FilterRedistExcludingBreakOrbitals(redistBRaw, orbA, orbB);
        int afterFilterA = redistA.Count;
        int afterFilterB = redistB.Count;
        // 0e lobes (perpendicular / carbocation along ref) — include σ-only so empty lobes ease instead of popping after the coroutine.
        atomA?.AppendBondBreakEmptyOrbitalAnimTargets(redistA, piAfterA == 0 ? refA : null, sigmaCleavageBetweenPartners);
        atomB?.AppendBondBreakEmptyOrbitalAnimTargets(redistB, piAfterB == 0 ? refB : null, sigmaCleavageBetweenPartners);

        RestoreMoleculeAtoms(moleculeSnap);
        if (orbA != null)
            orbA.transform.SetPositionAndRotation(bondWorldPos, bondWorldRot);
        if (orbB != null)
            orbB.transform.SetPositionAndRotation(bondWorldPos, bondWorldRot);

        var redistAStarts = new List<(Vector3 pos, Quaternion rot)>();
        var redistBStarts = new List<(Vector3 pos, Quaternion rot)>();
        foreach (var e in redistA)
            redistAStarts.Add(e.orb != null ? (e.orb.transform.localPosition, e.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        foreach (var e in redistB)
            redistBStarts.Add(e.orb != null ? (e.orb.transform.localPosition, e.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));

        // Match bond-orbital eps so small VSEPR adjustments still animate (not only large lone jumps).
        const float lonePosThreshold = 0.0004f;
        const float loneRotThreshold = 0.35f;
        bool HasRedistMovement()
        {
            for (int i = 0; i < redistA.Count; i++)
            {
                var e = redistA[i];
                if (e.orb == null || e.orb.transform.parent != atomA.transform) continue;
                if (Vector3.Distance(redistAStarts[i].pos, e.pos) > lonePosThreshold || Quaternion.Angle(redistAStarts[i].rot, e.rot) > loneRotThreshold)
                    return true;
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var e = redistB[i];
                if (e.orb == null || e.orb.transform.parent != atomB.transform) continue;
                if (Vector3.Distance(redistBStarts[i].pos, e.pos) > lonePosThreshold || Quaternion.Angle(redistBStarts[i].rot, e.rot) > loneRotThreshold)
                    return true;
            }
            return false;
        }

        bool hasRedistAnim = HasRedistMovement();

        var atomsToBlock = new HashSet<AtomFunction>();
        if (atomA != null) atomsToBlock.Add(atomA);
        if (atomB != null) atomsToBlock.Add(atomB);
        foreach (var kv in moves)
            atomsToBlock.Add(kv.Key);

        foreach (var a in atomsToBlock)
            a.SetInteractionBlocked(true);
        if (orbA != null) orbA.SetPointerBlocked(true);
        if (orbB != null) orbB.SetPointerBlocked(true);
        foreach (var e in redistA)
            if (e.orb != null) e.orb.SetPointerBlocked(true);
        foreach (var e in redistB)
            if (e.orb != null) e.orb.SetPointerBlocked(true);

        bool runStep = moves.Count > 0 || animOrbA || animOrbB || hasRedistAnim;
        if (runStep)
        {
            for (float t = 0; t < BreakRedistributionDuration; t += Time.deltaTime)
            {
                float s = Mathf.Clamp01(t / BreakRedistributionDuration);
                s = s * s * (3f - 2f * s); // smoothstep
                float rotS = 1f - (1f - s) * (1f - s); // ease-out quad (matches π step 2)
                foreach (var kv in moves)
                    kv.Key.transform.position = Vector3.Lerp(kv.Value.start, kv.Value.end, s);
                if (animOrbA && parentA != null && orbA != null)
                {
                    Vector3 endWA = parentA.transform.TransformPoint(slotLocalA.position);
                    Quaternion endRA = parentA.transform.rotation * slotLocalA.rotation;
                    orbA.transform.position = Vector3.Lerp(bondWorldPos, endWA, s);
                    orbA.transform.rotation = Quaternion.Slerp(bondWorldRot, endRA, rotS);
                }
                if (animOrbB && parentB != null && orbB != null)
                {
                    Vector3 endWB = parentB.transform.TransformPoint(slotLocalB.position);
                    Quaternion endRB = parentB.transform.rotation * slotLocalB.rotation;
                    orbB.transform.position = Vector3.Lerp(bondWorldPos, endWB, s);
                    orbB.transform.rotation = Quaternion.Slerp(bondWorldRot, endRB, rotS);
                }
                if (hasRedistAnim)
                {
                    for (int i = 0; i < redistA.Count; i++)
                    {
                        var e = redistA[i];
                        if (e.orb == null || e.orb.transform.parent != atomA.transform) continue;
                        var (sp, sr) = redistAStarts[i];
                        e.orb.transform.localPosition = Vector3.Lerp(sp, e.pos, s);
                        e.orb.transform.localRotation = Quaternion.Slerp(sr, e.rot, rotS);
                    }
                    for (int i = 0; i < redistB.Count; i++)
                    {
                        var e = redistB[i];
                        if (e.orb == null || e.orb.transform.parent != atomB.transform) continue;
                        var (sp, sr) = redistBStarts[i];
                        e.orb.transform.localPosition = Vector3.Lerp(sp, e.pos, s);
                        e.orb.transform.localRotation = Quaternion.Slerp(sr, e.rot, rotS);
                    }
                }
                yield return null;
            }
            foreach (var kv in moves)
                kv.Key.transform.position = kv.Value.end;
        }

        if (orbA != null && parentA != null)
        {
            orbA.transform.localPosition = slotLocalA.position;
            orbA.transform.localRotation = slotLocalA.rotation;
        }
        if (orbB != null && parentB != null)
        {
            orbB.transform.localPosition = slotLocalB.position;
            orbB.transform.localRotation = slotLocalB.rotation;
        }
        if (hasRedistAnim)
        {
            for (int i = 0; i < redistA.Count; i++)
            {
                var e = redistA[i];
                if (e.orb != null && e.orb.transform.parent == atomA.transform)
                {
                    e.orb.transform.localPosition = e.pos;
                    e.orb.transform.localRotation = e.rot;
                }
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var e = redistB[i];
                if (e.orb != null && e.orb.transform.parent == atomB.transform)
                {
                    e.orb.transform.localPosition = e.pos;
                    e.orb.transform.localRotation = e.rot;
                }
            }
        }

        LogBreakBondNewOrbitals("afterAnimSnap (before Redistribute)", orbA, orbB, parentA, parentB);

        bool skipFinalSigmaRelax = sigmaRelaxPreApplied;

        foreach (var e in redistA)
            if (e.orb != null) e.orb.SetPointerBlocked(false);
        foreach (var e in redistB)
            if (e.orb != null) e.orb.SetPointerBlocked(false);

        atomA?.RedistributeOrbitals(piAfterA == 0 ? piAngleA : null, piAfterA == 0 ? refA : null, relaxCoplanarSigmaToTetrahedral: true, skipLoneLobeLayout: sigmaOnlyUnchangedPiBoth, pinAtomsForSigmaRelax: pinSigmaRelaxAtoms, skipSigmaNeighborRelax: skipFinalSigmaRelax, bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB));
        atomB?.RedistributeOrbitals(piAfterB == 0 ? piAngleB : null, piAfterB == 0 ? refB : null, relaxCoplanarSigmaToTetrahedral: true, skipLoneLobeLayout: sigmaOnlyUnchangedPiBoth, pinAtomsForSigmaRelax: pinSigmaRelaxAtoms, skipSigmaNeighborRelax: skipFinalSigmaRelax, bondBreakGuideLoneOrbital: BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB));

        atomA?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(piAfterA == 0 ? refA : null, BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB), sigmaCleavageBetweenPartners);
        atomB?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(piAfterB == 0 ? refB : null, BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB), sigmaCleavageBetweenPartners);

        if (AtomFunction.DebugLogCcBondBreakGeometry)
        {
            atomA?.LogCcBondBreakGeometryDiagnostics("afterCoAnimateBreakBond-Final");
            atomB?.LogCcBondBreakGeometryDiagnostics("afterCoAnimateBreakBond-Final");
        }

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.RefreshElectronSyncOnBondedOrbitals();
        atomB?.RefreshElectronSyncOnBondedOrbitals();

        LogBreakBondNewOrbitals("afterRedistribute fullVsepr", orbA, orbB, parentA, parentB);

        if (orbA != null) orbA.SetPointerBlocked(false);
        if (orbB != null) orbB.SetPointerBlocked(false);
        foreach (var a in atomsToBlock)
            a.SetInteractionBlocked(false);
        AtomFunction.SetupGlobalIgnoreCollisions();
    }
}
