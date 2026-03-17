using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Forwards pointer events from the bond line collider to the parent CovalentBond.</summary>
public class BondLineColliderForwarder : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    CovalentBond bond;

    void Awake()
    {
        bond = GetComponentInParent<CovalentBond>();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        bond?.OnPointerDown(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        bond?.OnDrag(eventData);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        bond?.OnPointerUp(eventData);
    }
}
