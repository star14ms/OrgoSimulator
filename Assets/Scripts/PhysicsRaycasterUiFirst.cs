using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Skips 3D physics ray hits when overlay (or any) <see cref="GraphicRaycaster"/> already has a target
/// at the pointer. Prevents world colliders from stealing clicks from HUD buttons in 3D scenes.
/// </summary>
[AddComponentMenu("Event/Physics Raycaster (UI First)")]
public class PhysicsRaycasterUiFirst : PhysicsRaycaster
{
    static readonly List<RaycastResult> s_UiProbe = new List<RaycastResult>(24);

    public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
    {
        if (ShouldDeferToGraphicUi(eventData))
            return;
        base.Raycast(eventData, resultAppendList);
    }

    internal static bool ShouldDeferToGraphicUi(PointerEventData eventData)
    {
        s_UiProbe.Clear();
        var raycasters = FindObjectsByType<GraphicRaycaster>(FindObjectsSortMode.None);
        for (var i = 0; i < raycasters.Length; i++)
        {
            var gr = raycasters[i];
            if (!gr.isActiveAndEnabled) continue;
            s_UiProbe.Clear();
            gr.Raycast(eventData, s_UiProbe);
            if (s_UiProbe.Count > 0)
                return true;
        }
        return false;
    }
}
