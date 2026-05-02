using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>Lives on redistribute template preview root: click stem/tip to show description; click elsewhere (world) clears.</summary>
[DefaultExecutionOrder(20)]
public class BondFormationTemplatePreviewInput : MonoBehaviour
{
    const float RaycastDistance = 250f;

    readonly List<RaycastResult> _uiRaycastScratch = new List<RaycastResult>();

    /// <summary>
    /// Full-screen HUD graphics make <see cref="EventSystem.IsPointerOverGameObject"/> true even over &quot;empty&quot; areas.
    /// Block world template picking only when the topmost UI hit is an interactable <see cref="Selectable"/> (e.g. Next button).
    /// </summary>
    static bool ShouldBlockWorldPickForInteractiveUi(Vector2 screenPx, Mouse mouse, List<RaycastResult> reuseList, out int uiCount, out string topNameShort)
    {
        uiCount = 0;
        topNameShort = "";
        if (EventSystem.current == null) return false;
        int pointerId = mouse != null ? mouse.deviceId : -1;
        if (!EventSystem.current.IsPointerOverGameObject(pointerId))
            return false;

        reuseList.Clear();
        var ped = new PointerEventData(EventSystem.current) { position = screenPx };
        EventSystem.current.RaycastAll(ped, reuseList);
        uiCount = reuseList.Count;
        if (reuseList.Count == 0) return false;

        var top = reuseList[0].gameObject;
        if (top == null) return false;
        string n = top.name ?? "";
        topNameShort = n.Length > 40 ? n.Substring(0, 37) + "..." : n;

        var sel = top.GetComponentInParent<Selectable>();
        return sel != null && sel.interactable;
    }

    void LateUpdate()
    {
        if (!BondFormationDebugController.IsWaitingForPhase)
        {
            BondFormationTemplatePickHighlight.Clear();
            return;
        }

        bool tryGet = TryGetPrimaryClickThisFrame(out Vector2 screenPx);
        if (!tryGet)
        {
            if (BondFormationDebugController.SteppedModeEnabled)
                BondFormationTemplateDescriptionUI.ShowDebugSelectedAtomOnly();
            return;
        }

        if (!enabled || !gameObject.activeInHierarchy)
            return;

        var mouseDev = Mouse.current ?? InputSystem.GetDevice<Mouse>();
        bool blockInteractive = ShouldBlockWorldPickForInteractiveUi(screenPx, mouseDev, _uiRaycastScratch, out _, out _);
        if (blockInteractive)
            return;

        var cam = Camera.main;
        if (cam == null)
            return;

        var ray = cam.ScreenPointToRay(screenPx);
        // Tips use trigger colliders; project Physics.queriesHitTriggers may be false — always query triggers here.
        var hits = Physics.RaycastAll(ray, RaycastDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);

        BondFormationTemplatePreviewPick bestPick = null;
        float bestRayDist = float.MaxValue;
        AtomFunction clickedAtom = null;
        float clickedAtomDist = float.MaxValue;

        if (hits != null && hits.Length > 0)
        {
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var h in hits)
            {
                if (h.collider == null) continue;
                var pick = h.collider.GetComponent<BondFormationTemplatePreviewPick>()
                    ?? h.collider.GetComponentInParent<BondFormationTemplatePreviewPick>();
                if (pick == null)
                {
                    var orb = h.collider.GetComponent<ElectronOrbitalFunction>()
                        ?? h.collider.GetComponentInParent<ElectronOrbitalFunction>();
                    if (orb != null)
                        BondFormationTemplatePreviewPick.TryGetByLinkedOrbital(orb, out pick);
                }
                if (h.distance < clickedAtomDist)
                {
                    var atom = h.collider.GetComponent<AtomFunction>()
                        ?? h.collider.GetComponentInParent<AtomFunction>();
                    if (atom != null)
                    {
                        clickedAtom = atom;
                        clickedAtomDist = h.distance;
                    }
                }
                if (pick == null || string.IsNullOrEmpty(pick.Description)) continue;
                if (h.distance < bestRayDist)
                {
                    bestRayDist = h.distance;
                    bestPick = pick;
                }
            }
        }

        // Stems have no collider (unreliable under non-uniform cylinder scale); pick by ray vs world segment.
        if (TryPickStemByRaySegment(ray, transform, out var stemPick, out float stemRayDist))
        {
            if (stemRayDist < bestRayDist)
            {
                bestRayDist = stemRayDist;
                bestPick = stemPick;
            }
        }

        if (!SigmaBondFormation.CyclicPhase1TemplatePreviewContext.IsActive
            && bestPick == null
            && clickedAtom != null
            && OrbitalRedistribution.TryGetGuideOrbitalForDebug(clickedAtom, out var debugGuideOrbital))
        {
            if (BondFormationTemplatePreviewPick.TryGetByLinkedOrbital(debugGuideOrbital, out var guidePick) && guidePick != null)
            {
                bestPick = guidePick;
            }
            else
            {
                BondFormationTemplateDescriptionUI.Show(
                    "Guide orbital A=" + clickedAtom.GetInstanceID() + " O=" + debugGuideOrbital.GetInstanceID());
                BondFormationTemplatePickHighlight.ApplyFromOrbital(debugGuideOrbital);
                return;
            }
        }

        if (bestPick != null)
        {
            BondFormationTemplateDescriptionUI.Show(bestPick.Description);
            BondFormationTemplatePickHighlight.ApplyFromPick(bestPick);
        }
        else
        {
            if (BondFormationDebugController.SteppedModeEnabled)
                BondFormationTemplateDescriptionUI.ShowDebugSelectedAtomOnly();
            else
                BondFormationTemplateDescriptionUI.Hide();
            BondFormationTemplatePickHighlight.Clear();
        }
    }

    /// <summary>Closest template stem along the ray within each stem's pick radius (world units).</summary>
    static bool TryPickStemByRaySegment(Ray ray, Transform previewRoot, out BondFormationTemplatePreviewPick pick, out float distanceAlongRay)
    {
        pick = null;
        distanceAlongRay = float.MaxValue;
        if (previewRoot == null) return false;
        Vector3 dir = ray.direction;
        if (dir.sqrMagnitude < 1e-12f) return false;
        dir.Normalize();

        var stems = previewRoot.GetComponentsInChildren<BondFormationTemplateStemRayPick>(true);
        if (stems == null || stems.Length == 0) return false;

        float bestT = float.MaxValue;
        BondFormationTemplatePreviewPick best = null;
        foreach (var s in stems)
        {
            if (s == null || s.Pick == null || string.IsNullOrEmpty(s.Pick.Description)) continue;
            if (!TryRayClosestOnSegment(ray.origin, dir, s.SegmentAWorld, s.SegmentBWorld, s.PickRadiusWorld, out float tRay))
                continue;
            if (tRay < bestT)
            {
                bestT = tRay;
                best = s.Pick;
            }
        }

        if (best == null) return false;
        pick = best;
        distanceAlongRay = bestT;
        return true;
    }

    /// <summary>Sample the segment and test distance from ray (robust for very thin stems).</summary>
    static bool TryRayClosestOnSegment(Vector3 rayOrigin, Vector3 rayDirUnit, Vector3 segA, Vector3 segB, float maxPerpDistance, out float distanceAlongRay)
    {
        distanceAlongRay = float.MaxValue;
        const int samples = 48;
        bool found = false;
        float bestT = float.MaxValue;
        for (int i = 0; i <= samples; i++)
        {
            float u = i / (float)samples;
            Vector3 p = Vector3.Lerp(segA, segB, u);
            Vector3 op = p - rayOrigin;
            float t = Vector3.Dot(op, rayDirUnit);
            if (t < 0f || t > RaycastDistance) continue;
            Vector3 onRay = rayOrigin + rayDirUnit * t;
            float perp = Vector3.Distance(p, onRay);
            if (perp <= maxPerpDistance && t < bestT)
            {
                bestT = t;
                found = true;
            }
        }
        if (!found) return false;
        distanceAlongRay = bestT;
        return true;
    }

    void OnDestroy()
    {
        BondFormationTemplateDescriptionUI.Hide();
        BondFormationTemplatePickHighlight.Clear();
    }

    static bool TryGetPrimaryClickThisFrame(out Vector2 screenPx)
    {
        screenPx = default;
        var mouse = Mouse.current ?? InputSystem.GetDevice<Mouse>();
        if (mouse != null)
        {
            if (!mouse.leftButton.wasPressedThisFrame) return false;
            screenPx = mouse.position.ReadValue();
            return true;
        }
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetMouseButtonDown(0))
        {
            screenPx = Input.mousePosition;
            return true;
        }
#endif
        return false;
    }
}

/// <summary>Picked template stem/tip mesh tint only (no orbital or σ-bond body tint).</summary>
static class BondFormationTemplatePickHighlight
{
    static Renderer[] lastPreviewRenderers;

    public static void ApplyFromPick(BondFormationTemplatePreviewPick pick)
    {
        Clear();
        if (pick == null) return;
        var renderers = pick.LinkedGroupRenderers;
        if (renderers != null)
        {
            foreach (var r in renderers)
                if (r != null)
                    ElectronOrbitalFunction.SetRedistributeTemplatePreviewRendererPickHighlight(r, true);
            lastPreviewRenderers = renderers;
        }
    }

    public static void ApplyFromOrbital(ElectronOrbitalFunction orb)
    {
        Clear();
    }

    public static void Clear()
    {
        if (lastPreviewRenderers != null)
        {
            foreach (var r in lastPreviewRenderers)
                if (r != null)
                    ElectronOrbitalFunction.SetRedistributeTemplatePreviewRendererPickHighlight(r, false);
            lastPreviewRenderers = null;
        }
        ElectronOrbitalFunction.ReapplyBondSteppedGuideClusterHighlightAfterPickClear();
    }
}
