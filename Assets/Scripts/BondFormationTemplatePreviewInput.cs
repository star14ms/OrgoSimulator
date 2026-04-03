using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>Lives on redistribute template preview root: click stem/tip to show description; click elsewhere (world) clears.</summary>
public class BondFormationTemplatePreviewInput : MonoBehaviour
{
    const float RaycastDistance = 250f;

    void Update()
    {
        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(-1)) return;
        var cam = Camera.main;
        if (cam == null) return;

        var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
        var hits = Physics.RaycastAll(ray, RaycastDistance);

        BondFormationTemplatePreviewPick bestPick = null;
        float bestRayDist = float.MaxValue;

        if (hits != null && hits.Length > 0)
        {
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider == null) continue;
                var pick = h.collider.GetComponent<BondFormationTemplatePreviewPick>()
                    ?? h.collider.GetComponentInParent<BondFormationTemplatePreviewPick>();
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

        if (bestPick != null)
        {
            BondFormationTemplateDescriptionUI.Show(bestPick.Description);
            BondFormationTemplatePickHighlight.ApplyFromPick(bestPick);
        }
        else
        {
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
}

/// <summary>Red highlight on picked template stem/tip, mapped orbital, and σ bond cylinder/line when bonded.</summary>
static class BondFormationTemplatePickHighlight
{
    static Renderer[] lastPreviewRenderers;
    static ElectronOrbitalFunction lastOrbital;
    static CovalentBond lastBond;

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
        var orb = pick.LinkedOrbital;
        if (orb != null)
        {
            orb.SetBondFormationTemplatePickHighlight(true);
            lastOrbital = orb;
            if (orb.Bond != null)
            {
                orb.Bond.SetBondFormationTemplatePickHighlight(true);
                lastBond = orb.Bond;
            }
        }
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
        if (lastOrbital != null)
        {
            lastOrbital.SetBondFormationTemplatePickHighlight(false);
            lastOrbital = null;
        }
        if (lastBond != null)
        {
            lastBond.SetBondFormationTemplatePickHighlight(false);
            lastBond = null;
        }
        ElectronOrbitalFunction.ReapplyBondSteppedGuideClusterHighlightAfterPickClear();
    }
}
