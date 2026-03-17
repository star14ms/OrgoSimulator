using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Represents a covalent bond between two atoms. Owns the shared orbital and its electrons.
/// Positions the orbital between the two atoms and notifies both when electrons change.
/// By default displays as a single line; click to show orbital while holding, returns to line on release if bond doesn't break.
/// </summary>
public class CovalentBond : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] AtomFunction atomA;
    [SerializeField] AtomFunction atomB;
    ElectronOrbitalFunction orbital;

    bool orbitalVisible;
    bool forwardedPressToOrbital;
    PointerEventData storedPressData;
    GameObject lineVisual;
    SpriteRenderer lineRenderer;
    BoxCollider2D lineCollider;
    static Sprite lineSprite;

    public AtomFunction AtomA => atomA;
    public AtomFunction AtomB => atomB;
    public ElectronOrbitalFunction Orbital => orbital;

    public int ElectronCount => orbital != null ? orbital.ElectronCount : 0;

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

    public static CovalentBond Create(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction sharedOrbital)
    {
        if (sourceAtom == null || targetAtom == null || sharedOrbital == null) return null;
        if (sourceAtom == targetAtom) return null;

        var bondGo = new GameObject("CovalentBond");
        var bond = bondGo.AddComponent<CovalentBond>();
        bond.Initialize(sourceAtom, targetAtom, sharedOrbital);
        return bond;
    }

    void Initialize(AtomFunction a, AtomFunction b, ElectronOrbitalFunction orb)
    {
        atomA = a;
        atomB = b;
        orbital = orb;

        atomA.RegisterBond(this);
        atomB.RegisterBond(this);

        orbital.SetBond(this);
        orbital.SetBondedAtom(null);
        orbital.transform.SetParent(transform);

        atomA.SetupIgnoreCollisions();
        atomB.SetupIgnoreCollisions();

        CreateLineVisual();
        orbitalVisible = false;
        ApplyDisplayMode();
    }

    void CreateLineVisual()
    {
        lineVisual = new GameObject("BondLine");
        lineVisual.transform.SetParent(transform);

        lineRenderer = lineVisual.AddComponent<SpriteRenderer>();
        lineRenderer.sprite = GetOrCreateLineSprite();
        lineRenderer.color = Color.black;
        lineRenderer.sortingOrder = 0;
        lineRenderer.sortingLayerID = 0;

        lineCollider = lineVisual.AddComponent<BoxCollider2D>();
        lineCollider.isTrigger = true;
        lineVisual.AddComponent<BondLineColliderForwarder>();
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

    void LateUpdate()
    {
        if (atomA == null || atomB == null) return;

        var posA = atomA.transform.position;
        var posB = atomB.transform.position;
        var center = (posA + posB) * 0.5f;
        // Canonical direction: same for all bonds between this pair (avoids sign flip when atomA/atomB order differs)
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;

        transform.position = center;
        var angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg - 90f;
        transform.rotation = Quaternion.Euler(0, 0, angle);

        int bondIndex = GetBondIndex();
        int bondCount = atomA.GetBondsTo(atomB);
        float offset = GetLineOffset(bondIndex, bondCount);
        var perpendicular = Vector3.Cross(Vector3.forward, delta / distance).normalized;
        // Each bond has its own lineVisual + collider; position this bond's line at its offset (not shared)
        lineVisual.transform.position = center + perpendicular * offset;
        lineVisual.transform.localRotation = Quaternion.identity;
        float lineLength = Mathf.Max(0.1f, distance * 0.5f);
        lineVisual.transform.localScale = new Vector3(0.25f, lineLength, 1f);

        // Collider size = sprite base size (0.5 x 1) so it scales with the transform and matches the line
        lineCollider.size = new Vector2(0.5f, 1f);
        lineCollider.offset = Vector2.zero;

        // Position bond orbital at the bond (not where the target orbital was); point along bond direction
        // Skip when orbitalVisible (user is dragging) so orbital and its electrons move together
        if (orbital != null && !orbitalVisible)
        {
            orbital.transform.localPosition = new Vector3(offset, 0f, 0f);
            orbital.transform.localRotation = Quaternion.Euler(0f, 0f, 90f); // orbital points along bond (local Y)
            orbital.transform.localScale = Vector3.one * 0.6f;
        }
    }

    void ApplyDisplayMode()
    {
        if (orbital != null)
        {
            orbital.SetVisualsEnabled(orbitalVisible);
            orbital.SetPointerBlocked(true);
        }
        if (lineRenderer != null) lineRenderer.enabled = !orbitalVisible;
        if (lineCollider != null) lineCollider.enabled = !orbitalVisible;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (orbitalVisible) return;
        if (atomA == null || atomB == null) return;
        var clickWorld = ScreenToWorld(eventData.position);
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;
        var dir = delta / distance;
        var right = Vector3.Cross(Vector3.forward, dir).normalized;
        int bondIndex = GetBondIndex();
        int bondCount = atomA.GetBondsTo(atomB);
        float offset = GetLineOffset(bondIndex, bondCount);
        var lineCenter = (atomA.transform.position + atomB.transform.position) * 0.5f + right * offset;
        var lineStart = lineCenter - dir * (distance * 0.5f);
        var lineEnd = lineCenter + dir * (distance * 0.5f);
        if (DistanceToLineSegment(clickWorld, lineStart, lineEnd) > 0.25f)
            return;
        orbitalVisible = true;
        forwardedPressToOrbital = false;
        storedPressData = eventData;
        ApplyDisplayMode();
    }

    static Vector3 ScreenToWorld(Vector2 screenPos)
    {
        var v = new Vector3(screenPos.x, screenPos.y, -Camera.main.transform.position.z);
        return Camera.main.ScreenToWorldPoint(v);
    }

    static float DistanceToLineSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var ap = p - a;
        var t = Mathf.Clamp01(Vector3.Dot(ap, ab) / (ab.sqrMagnitude + 0.0001f));
        var nearest = a + t * ab;
        return Vector3.Distance(p, nearest);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!orbitalVisible || orbital == null) return;
        if (!forwardedPressToOrbital)
        {
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
        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();
    }

    public void BreakBond(AtomFunction returnOrbitalTo)
    {
        if (orbital == null) return;

        int piBeforeA = atomA != null ? atomA.GetPiBondCount() : 0;
        int piBeforeB = atomB != null ? atomB.GetPiBondCount() : 0;
        float? piBondAngleFromA = null;
        float? piBondAngleFromB = null;
        if (atomA != null && atomB != null)
        {
            var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
            var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;
            if (dirAtoB.sqrMagnitude >= 0.01f)
                piBondAngleFromA = OrbitalAngleUtility.DirectionToAngleWorld(dirAtoB);
            if (dirBtoA.sqrMagnitude >= 0.01f)
                piBondAngleFromB = OrbitalAngleUtility.DirectionToAngleWorld(dirBtoA);
        }

        atomA?.UnregisterBond(this);
        atomB?.UnregisterBond(this);

        int totalElectrons = orbital.ElectronCount;
        var otherAtom = returnOrbitalTo == atomA ? atomB : atomA;
        var prefab = returnOrbitalTo.OrbitalPrefab ?? otherAtom?.OrbitalPrefab;
        if (prefab == null) return;

        orbital.SetBond(null);
        orbital.SetBondedAtom(returnOrbitalTo);
        orbital.SetPointerBlocked(false);
        orbital.SetVisualsEnabled(true);
        var dirToOther = (otherAtom.transform.position - returnOrbitalTo.transform.position).normalized;
        if (dirToOther.sqrMagnitude < 0.01f) dirToOther = Vector3.right;
        var slotA = returnOrbitalTo.GetSlotForNewOrbital(dirToOther, orbital);
        orbital.transform.SetParent(returnOrbitalTo.transform);
        orbital.transform.localPosition = slotA.position;
        orbital.transform.localRotation = slotA.rotation;
        orbital.transform.localScale = Vector3.one * 0.6f;
        orbital.ElectronCount = totalElectrons;
        returnOrbitalTo.BondOrbital(orbital);

        var newOrbital = Instantiate(prefab);
        newOrbital.transform.localScale = Vector3.one * 0.6f;
        var dirToReturn = (returnOrbitalTo.transform.position - otherAtom.transform.position).normalized;
        if (dirToReturn.sqrMagnitude < 0.01f) dirToReturn = Vector3.left;
        var slotB = otherAtom.GetSlotForNewOrbital(dirToReturn, newOrbital);
        newOrbital.transform.SetParent(otherAtom.transform);
        newOrbital.transform.localPosition = slotB.position;
        newOrbital.transform.localRotation = slotB.rotation;
        newOrbital.ElectronCount = 0;
        newOrbital.SetBondedAtom(otherAtom);
        otherAtom.BondOrbital(newOrbital);

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();

        int piAfterA = atomA != null ? atomA.GetPiBondCount() : 0;
        int piAfterB = atomB != null ? atomB.GetPiBondCount() : 0;
        if (atomA != null && piAfterA != piBeforeA)
            atomA.RedistributeOrbitals(piBondAngleFromA);
        if (atomB != null && piAfterB != piBeforeB)
            atomB.RedistributeOrbitals(piBondAngleFromB);

        orbital = null;
        Destroy(gameObject);
    }
}
