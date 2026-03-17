using UnityEngine;
using UnityEngine.EventSystems;

public class ElectronFunction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] float orbitalWidth = 0.5f; // Fallback; overridden by SetOrbitalWidth from parent orbital

    int slotIndex;

    public void SetOrbitalWidth(float width)
    {
        orbitalWidth = width;
        UpdatePosition();
    }
    bool isBeingHeld;
    Vector3 originalLocalPosition;
    Vector3 dragOffset;

    public void SetSlotIndex(int index) => slotIndex = index;

    void SetPhysicsEnabled(bool enabled)
    {
        var rb = GetComponent<Rigidbody>();
        var rb2D = GetComponent<Rigidbody2D>();
        if (rb != null) rb.isKinematic = !enabled;
        if (rb2D != null) rb2D.simulated = enabled;
    }

    void Start()
    {
        EnsureCollider();
        UpdatePosition();
    }

    void EnsureCollider()
    {
        if (GetComponent<Collider>() != null || GetComponent<Collider2D>() != null) return;
        var col = gameObject.AddComponent<BoxCollider>();
        col.size = new Vector3(0.3f, 0.3f, 0.1f);
        col.isTrigger = true;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        isBeingHeld = true;
        originalLocalPosition = transform.localPosition;
        dragOffset = transform.position - ScreenToWorld(eventData.position);
        var orbital = GetComponentInParent<ElectronOrbitalFunction>();
        orbital?.SetPointerBlocked(true);
        orbital?.SetPhysicsEnabled(false);
        SetPhysicsEnabled(false);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (isBeingHeld)
            transform.position = ScreenToWorld(eventData.position) + dragOffset;
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

    static Vector3 ScreenToWorld(Vector2 screenPos)
    {
        var mouse = new Vector3(screenPos.x, screenPos.y, -Camera.main.transform.position.z);
        return Camera.main.ScreenToWorldPoint(mouse);
    }

    void UpdatePosition()
    {
        // Along orbital width (perpendicular to atom–orbital axis). Slot 0: one side; slot 1: other side.
        // For left/right orbitals: electrons up/down. For top/bottom orbitals: electrons left/right.
        float centerOfHalf = orbitalWidth * 0.5f; // Half the total spacing; electrons at ±centerOfHalf
        float y = (slotIndex == 0 ? -1f : 1f) * centerOfHalf;
        transform.localPosition = new Vector3(0, y, 0);
    }
}
