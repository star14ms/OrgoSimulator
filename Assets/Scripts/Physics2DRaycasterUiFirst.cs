using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Same priority as <see cref="PhysicsRaycasterUiFirst"/> for 2D physics: UI graphics win over <see cref="Collider2D"/> hits.
/// </summary>
[AddComponentMenu("Event/Physics 2D Raycaster (UI First)")]
public class Physics2DRaycasterUiFirst : Physics2DRaycaster
{
    static readonly List<RaycastResult> s_UiProbe = new List<RaycastResult>(24);

    public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
    {
        if (PhysicsRaycasterUiFirst.ShouldDeferToGraphicUi(eventData))
            return;
        base.Raycast(eventData, resultAppendList);
    }
}
