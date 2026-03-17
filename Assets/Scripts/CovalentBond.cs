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

        lineCollider = gameObject.AddComponent<BoxCollider2D>();
        lineCollider.isTrigger = true;
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
        var delta = posB - posA;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;

        transform.position = center;
        var angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg - 90f;
        transform.rotation = Quaternion.Euler(0, 0, angle);

        lineVisual.transform.localPosition = Vector3.zero;
        lineVisual.transform.localRotation = Quaternion.identity;
        lineVisual.transform.localScale = new Vector3(0.25f, Mathf.Max(0.1f, distance * 0.5f), 1f);

        lineCollider.size = new Vector2(0.5f, distance);
        lineCollider.offset = Vector2.zero;
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
        orbitalVisible = true;
        forwardedPressToOrbital = false;
        storedPressData = eventData;
        ApplyDisplayMode();
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
        orbital.transform.SetParent(returnOrbitalTo.transform);
        var dirToOther = (otherAtom.transform.position - returnOrbitalTo.transform.position).normalized;
        if (dirToOther.sqrMagnitude < 0.01f) dirToOther = Vector3.right;
        var slotA = returnOrbitalTo.GetSlotForNewOrbital(dirToOther, null);
        orbital.transform.localPosition = slotA.position;
        orbital.transform.localRotation = slotA.rotation;
        orbital.transform.localScale = Vector3.one * 0.6f;
        orbital.ElectronCount = totalElectrons;
        returnOrbitalTo.BondOrbital(orbital);

        var newOrbital = Instantiate(prefab, otherAtom.transform);
        newOrbital.transform.localScale = Vector3.one * 0.6f;
        var dirToReturn = (returnOrbitalTo.transform.position - otherAtom.transform.position).normalized;
        if (dirToReturn.sqrMagnitude < 0.01f) dirToReturn = Vector3.left;
        var slotB = otherAtom.GetSlotForNewOrbital(dirToReturn, null);
        newOrbital.transform.localPosition = slotB.position;
        newOrbital.transform.localRotation = slotB.rotation;
        newOrbital.ElectronCount = 0;
        newOrbital.SetBondedAtom(otherAtom);
        otherAtom.BondOrbital(newOrbital);

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();

        orbital = null;
        Destroy(gameObject);
    }
}
