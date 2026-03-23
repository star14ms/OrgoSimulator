using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.U2D;

/// <summary>
/// Same as <see cref="Physics2DRaycasterUiFirst"/>, but prefers <see cref="ElectronFunction"/> when the ray hits
/// both an orbital and an electron (2D orthographic).
/// </summary>
[AddComponentMenu("Event/Physics 2D Raycaster (UI First, Prefer Electron)")]
public class Physics2DRaycasterPreferElectronOverOrbital : Physics2DRaycasterUiFirst
{
#if PACKAGE_PHYSICS2D
    public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
    {
        if (PhysicsRaycasterUiFirst.ShouldDeferToGraphicUi(eventData))
            return;

        Ray ray = new Ray();
        float distanceToClipPlane = 0;
        int displayIndex = 0;
        if (!ComputeRayAndDistance(eventData, ref ray, ref displayIndex, ref distanceToClipPlane))
            return;

        var hits = Physics2D.GetRayIntersectionAll(ray, distanceToClipPlane, finalEventMask);
        if (hits.Length == 0) return;
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].collider.GetComponentInParent<ElectronFunction>() == null) continue;
            AppendHit2DResults(hits[i], eventData, displayIndex, resultAppendList, ray);
            return;
        }

        for (int b = 0; b < hits.Length; b++)
            AppendHit2DResults(hits[b], eventData, displayIndex, resultAppendList, ray);
    }

    void AppendHit2DResults(RaycastHit2D mHit, PointerEventData eventData, int displayIndex, List<RaycastResult> resultAppendList, Ray ray)
    {
        Renderer r2d = null;
        var rendererResult = mHit.collider.gameObject.GetComponent<Renderer>();
        if (rendererResult != null)
        {
            if (rendererResult is SpriteRenderer) r2d = rendererResult;
#if PACKAGE_TILEMAP
            if (rendererResult is UnityEngine.Tilemaps.TilemapRenderer) r2d = rendererResult;
#endif
            if (rendererResult is SpriteShapeRenderer) r2d = rendererResult;
        }

        var result = new RaycastResult
        {
            gameObject = mHit.collider.gameObject,
            module = this,
            distance = mHit.distance,
            worldPosition = mHit.point,
            worldNormal = mHit.normal,
            screenPosition = eventData.position,
            displayIndex = displayIndex,
            index = resultAppendList.Count,
            sortingGroupID = r2d != null ? r2d.sortingGroupID : SortingGroup.invalidSortingGroupID,
            sortingGroupOrder = r2d != null ? r2d.sortingGroupOrder : 0,
            sortingLayer = r2d != null ? r2d.sortingLayerID : 0,
            sortingOrder = r2d != null ? r2d.sortingOrder : 0
        };

        if (result.sortingGroupID != SortingGroup.invalidSortingGroupID &&
            r2d != null &&
            SortingGroup.GetSortingGroupByIndex(r2d.sortingGroupID) is SortingGroup sortingGroup)
        {
            result.distance = Vector3.Dot(ray.direction, sortingGroup.transform.position - ray.origin);
            result.sortingLayer = sortingGroup.sortingLayerID;
            result.sortingOrder = sortingGroup.sortingOrder;
        }

        resultAppendList.Add(result);
    }
#else
    public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
    {
        base.Raycast(eventData, resultAppendList);
    }
#endif
}
