using UnityEngine;
using UnityEngine.EventSystems;

public class ElectronOrbitalFunction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] int electronCount;
    [SerializeField] ElectronFunction electronPrefab;
    [SerializeField] float electronSpacing = 0.5f; // Distance between electrons (larger = more spread)
    [SerializeField] Sprite stretchSprite; // Optional: single sprite for stretch. If null, uses procedural triangle+circle composite.
    AtomFunction bondedAtom;
    CovalentBond bond;
    bool isBeingHeld;
    Vector3 dragOffset;

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
    GameObject stretchVisual;
    SpriteRenderer mainSpriteRenderer;
    const float MinStretchLength = 0.5f;
    const float OffsetScale = 1.14f;
    [SerializeField] float apexPadding = 0.2f;
    float lastDebugLogTime;
    static Sprite cachedTriangleSprite;
    static Sprite cachedCircleSprite;
    static Material cachedTriangleClipMaterial;
    static readonly int ClipCenterId = Shader.PropertyToID("_ClipCenter");
    static readonly int ClipRadiusXId = Shader.PropertyToID("_ClipRadiusX");
    static readonly int ClipRadiusYId = Shader.PropertyToID("_ClipRadiusY");

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

    public void OnPointerDown(PointerEventData eventData)
    {
        isBeingHeld = true;
        originalLocalPosition = transform.localPosition;
        originalLocalScale = transform.localScale;
        originalLocalRotation = transform.localRotation;
        dragOffset = transform.position - ScreenToWorld(eventData.position);
        mainSpriteRenderer = GetComponent<SpriteRenderer>();
        if (mainSpriteRenderer != null) mainSpriteRenderer.enabled = false;
        CreateStretchVisual();
        SetPhysicsEnabled(false);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!isBeingHeld) return;
        Vector3 tip = ScreenToWorld(eventData.position) + dragOffset;
        transform.position = tip;
        UpdateStretchVisual(tip);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        isBeingHeld = false;
        Vector3 tip = transform.position;
        if (stretchVisual != null) { Destroy(stretchVisual); stretchVisual = null; }
        if (mainSpriteRenderer != null) mainSpriteRenderer.enabled = true;
        SetPhysicsEnabled(true);

        if (bond != null)
        {
            TryBreakBond(tip);
            return;
        }

        var sourceAtom = bondedAtom ?? transform.parent?.GetComponent<AtomFunction>();
        var targetAtom = AtomFunction.FindBondPartner(sourceAtom, tip, this);
        if (targetAtom != null && FormCovalentBond(sourceAtom, targetAtom, tip))
            return;

        var swapTarget = TryFindSwapTarget(sourceAtom, tip);
        if (swapTarget != null)
        {
            SwapPositionsWith(swapTarget);
            return;
        }

        transform.localPosition = originalLocalPosition;
        transform.localScale = originalLocalScale;
        transform.localRotation = originalLocalRotation;
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
        var pos = transform.localPosition;
        var rot = transform.localRotation;
        transform.localPosition = other.transform.localPosition;
        transform.localRotation = other.transform.localRotation;
        other.transform.localPosition = pos;
        other.transform.localRotation = rot;
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

    bool FormCovalentBond(AtomFunction sourceAtom, AtomFunction targetAtom, Vector3 dropPosition)
    {
        if (sourceAtom == null || targetAtom == null || sourceAtom == targetAtom) return false;
        var targetOrbital = targetAtom.GetAvailableLoneOrbitalForBond(this, electronCount, dropPosition);
        if (targetOrbital == null) return false;

        RearrangeOrbitalToFaceTarget(sourceAtom, targetOrbital);
        AlignSourceAtomNextToTarget(sourceAtom, targetAtom, targetOrbital);

        int mergedElectrons = electronCount + targetOrbital.ElectronCount;
        targetOrbital.ElectronCount = mergedElectrons;

        sourceAtom.UnbondOrbital(this);
        targetAtom.UnbondOrbital(targetOrbital);

        CovalentBond.Create(sourceAtom, targetAtom, targetOrbital);
        transform.SetParent(null);
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();
        Destroy(gameObject);
        return true;
    }

    void RearrangeOrbitalToFaceTarget(AtomFunction sourceAtom, ElectronOrbitalFunction targetOrbital)
    {
        float targetAngle = NormalizeAngle(targetOrbital.transform.localEulerAngles.z);
        float oppositeAngle = NormalizeAngle(targetAngle + 180f);
        float draggedAngle = NormalizeAngle(originalLocalRotation.eulerAngles.z);
        if (Mathf.Abs(NormalizeAngle(draggedAngle - oppositeAngle)) < 45f)
            return;
        var sourceOrbitals = sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>();
        ElectronOrbitalFunction orbToMove = null;
        float bestDelta = 360f;
        foreach (var orb in sourceOrbitals)
        {
            if (orb == this || orb.Bond != null) continue;
            float orbAngle = NormalizeAngle(orb.transform.localEulerAngles.z);
            float delta = Mathf.Abs(NormalizeAngle(orbAngle - oppositeAngle));
            if (delta < bestDelta)
            {
                bestDelta = delta;
                orbToMove = orb;
            }
        }
        if (orbToMove != null)
        {
            float freedSlotAngle = NormalizeAngle(originalLocalRotation.eulerAngles.z);
            var slotPos = GetCanonicalSlotPosition(freedSlotAngle, sourceAtom.BondRadius);
            orbToMove.transform.localPosition = slotPos.position;
            orbToMove.transform.localRotation = slotPos.rotation;
        }
    }

    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPosition(float angleDeg, float bondRadius)
    {
        float angle = NormalizeAngle(angleDeg);
        float rad = angle * Mathf.Deg2Rad;
        float offset = bondRadius * 0.6f;
        var dir = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f);
        var pos = dir * offset;
        var rot = Quaternion.Euler(0, 0, angle);
        return (pos, rot);
    }

    public static float NormalizeAngle(float deg)
    {
        while (deg > 180f) deg -= 360f;
        while (deg < -180f) deg += 360f;
        return deg;
    }

    void AlignSourceAtomNextToTarget(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        var targetPos = targetAtom.transform.position;
        var targetOrbitalTip = targetOrbital.transform.position;
        var toOrbital = targetOrbitalTip - targetPos;
        var distToOrbital = toOrbital.magnitude;
        if (distToOrbital < 0.01f) return;
        var dir = toOrbital / distToOrbital;
        var newSourcePos = targetOrbitalTip + dir * distToOrbital;
        var delta = newSourcePos - sourceAtom.transform.position;
        foreach (var a in sourceAtom.GetConnectedMolecule())
            a.transform.position += delta;
    }

    void CreateStretchVisual()
    {
        if (stretchVisual != null) return;
        var anchor = GetAtomCenter();
        var tip = transform.position;
        stretchVisual = new GameObject("StretchVisual");
        stretchVisual.transform.SetParent(transform.parent);
        stretchVisual.transform.localScale = Vector3.one;

        var color = mainSpriteRenderer != null ? mainSpriteRenderer.color : new Color(1, 1, 1, 0.4f);
        var sortOrder = mainSpriteRenderer != null ? mainSpriteRenderer.sortingOrder : 0;
        var sortLayer = mainSpriteRenderer != null ? mainSpriteRenderer.sortingLayerID : 0;

        if (stretchSprite != null)
        {
            var sr = stretchVisual.AddComponent<SpriteRenderer>();
            sr.sprite = stretchSprite;
            sr.color = color;
            sr.sortingOrder = sortOrder;
            sr.sortingLayerID = sortLayer;
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

    Vector3 GetAtomCenter() => transform.parent != null ? transform.parent.position : transform.position;

    void UpdateStretchVisual(Vector3 tip)
    {
        if (stretchVisual == null) return;
        var anchor = GetAtomCenter();
        var dir = (tip - anchor);
        var distance = dir.magnitude;
        if (distance < 0.01f) return;
        dir /= distance;
        distance = Mathf.Max(distance, MinStretchLength);

        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
        stretchVisual.transform.rotation = Quaternion.Euler(0, 0, angle);

        if (stretchSprite != null)
        {
            stretchVisual.transform.position = (anchor + tip) * 0.5f;
            stretchVisual.transform.localScale = Vector3.one;
        }
        else
        {
            stretchVisual.transform.localScale = Vector3.one;
            var tri = stretchVisual.transform.Find("Triangle");
            var circle = stretchVisual.transform.Find("Circle");
            float padding = Mathf.Min(apexPadding, distance * 0.4f);
            float triLength = distance - padding;
            Vector3 triApex = anchor + padding * dir;
            Vector3 triBase = tip;
            stretchVisual.transform.position = (triApex + triBase) * 0.5f;
            const float ProceduralSpriteSize = 2f;
            float targetSize = ProceduralSpriteSize * originalLocalScale.y;
            float circleScale = targetSize / ProceduralSpriteSize;
            float triWidthScale = circleScale * 2f;
            float triHeightScale = triLength / ProceduralSpriteSize;
            if (tri != null)
            {
                tri.localPosition = Vector3.zero;
                tri.localRotation = Quaternion.identity;
                tri.localScale = new Vector3(triWidthScale, triHeightScale, 1f);
                var triSr = tri.GetComponent<SpriteRenderer>();
                if (triSr != null && triSr.material != null)
                {
                    var block = new MaterialPropertyBlock();
                    triSr.GetPropertyBlock(block);
                    block.SetVector(ClipCenterId, new Vector4(0f, 1f, 0f, 0f));
                    float clipRadius = circleScale;
                    block.SetFloat(ClipRadiusXId, clipRadius / triWidthScale);
                    block.SetFloat(ClipRadiusYId, clipRadius / triHeightScale);
                    triSr.SetPropertyBlock(block);
                }
            }
            if (circle != null)
            {
                circle.localPosition = new Vector3(0f, triHeightScale, 0f);
                circle.localRotation = Quaternion.identity;
                circle.localScale = new Vector3(circleScale * OffsetScale, circleScale * OffsetScale, 1f);
            }
            if (tri != null && circle != null && Time.time - lastDebugLogTime > 0.5f)
            {
                lastDebugLogTime = Time.time;
                var circleSr = circle.GetComponent<SpriteRenderer>();
                float orbitalCircleDiameter = circleSr != null ? circleSr.bounds.size.x : 0f;
                float clipCircleDiameter = 2f * circleScale;
                Debug.Log($"[Orbital] Orbital circle diameter: {orbitalCircleDiameter:F3}, Clip circle diameter: {clipCircleDiameter:F3} (should match)");
            }
        }
    }

    static Vector3 ScreenToWorld(Vector2 screenPos)
    {
        var mouse = new Vector3(screenPos.x, screenPos.y, -Camera.main.transform.position.z);
        return Camera.main.ScreenToWorldPoint(mouse);
    }

    void Start()
    {
        EnsureCollider();
        SyncElectronObjects();
        IgnoreCollisionsWithChildren();
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

    void Update() { }

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
