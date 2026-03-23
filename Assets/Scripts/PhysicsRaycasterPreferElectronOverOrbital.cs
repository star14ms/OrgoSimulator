using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Same as <see cref="PhysicsRaycasterUiFirst"/>, but when the ray passes through both an orbital shell and an
/// <see cref="ElectronFunction"/> collider, only the electron registers so dragging picks up the electron, not the lobe.
/// </summary>
[AddComponentMenu("Event/Physics Raycaster (UI First, Prefer Electron)")]
public class PhysicsRaycasterPreferElectronOverOrbital : PhysicsRaycasterUiFirst
{
#if PACKAGE_PHYSICS
    public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
    {
        if (PhysicsRaycasterUiFirst.ShouldDeferToGraphicUi(eventData))
            return;

        Ray ray = new Ray();
        int displayIndex = 0;
        float distanceToClipPlane = 0;
        if (!ComputeRayAndDistance(eventData, ref ray, ref displayIndex, ref distanceToClipPlane))
            return;

        var hits = Physics.RaycastAll(ray, distanceToClipPlane, finalEventMask);
        if (hits.Length == 0) return;
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].collider.GetComponentInParent<ElectronFunction>() == null) continue;
            var h = hits[i];
            resultAppendList.Add(new RaycastResult
            {
                gameObject = h.collider.gameObject,
                module = this,
                distance = h.distance,
                worldPosition = h.point,
                worldNormal = h.normal,
                screenPosition = eventData.position,
                displayIndex = displayIndex,
                index = resultAppendList.Count,
                sortingLayer = 0,
                sortingOrder = 0
            });
            return;
        }

        for (int b = 0; b < hits.Length; b++)
        {
            var h = hits[b];
            resultAppendList.Add(new RaycastResult
            {
                gameObject = h.collider.gameObject,
                module = this,
                distance = h.distance,
                worldPosition = h.point,
                worldNormal = h.normal,
                screenPosition = eventData.position,
                displayIndex = displayIndex,
                index = resultAppendList.Count,
                sortingLayer = 0,
                sortingOrder = 0
            });
        }
    }
#else
    public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
    {
        base.Raycast(eventData, resultAppendList);
    }
#endif
}
