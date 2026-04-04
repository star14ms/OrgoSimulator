using System.Collections.Generic;
using System.Text;
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

    #region agent log
    [System.Serializable]
    class AgentTemplatePickDbg
    {
        public bool tryGet;
        public bool rawNewInputDown;
        public bool legacyMouseDown;
        public bool enabled;
        public bool activeInHierarchy;
        public string previewRootName;
        public bool pointerOverUI;
        public int mouseDeviceId;
        public bool pointerOverUiLegacyMinus1;
        public int uiRaycastCount;
        public string uiTopHitShort;
        public bool blockInteractiveUi;
        public bool camNull;
        public int hitCount;
        public string hitsSummary;
        public int stemCount;
        public bool stemPickOk;
        public float stemT;
        public float bestRayPickDist;
        public bool finalPickNonNull;
        public string exitReason;
    }

    const string AgentDebugLogFile = "debug-9ddc95.log";
    const string AgentDebugSessionId = "9ddc95";

    static void AgentLogTemplatePick(string hypothesisId, AgentTemplatePickDbg dbg)
    {
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            AgentDebugLogFile,
            AgentDebugSessionId,
            hypothesisId,
            "BondFormationTemplatePreviewInput.LateUpdate",
            "template_pick_mousedown",
            JsonUtility.ToJson(dbg),
            "post-fix-v2");
    }
    #endregion

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
        bool tryGet = TryGetPrimaryClickThisFrame(out Vector2 screenPx);
        var mouseDev = Mouse.current ?? InputSystem.GetDevice<Mouse>();
        bool rawNew = mouseDev != null && mouseDev.leftButton.wasPressedThisFrame;
#if ENABLE_LEGACY_INPUT_MANAGER
        bool legacyDown = Input.GetMouseButtonDown(0);
#else
        bool legacyDown = false;
#endif

        if (!tryGet)
        {
            #region agent log
            if (rawNew || legacyDown)
            {
                AgentLogTemplatePick("H2", new AgentTemplatePickDbg
                {
                    tryGet = false,
                    rawNewInputDown = rawNew,
                    legacyMouseDown = legacyDown,
                    enabled = enabled,
                    activeInHierarchy = gameObject.activeInHierarchy,
                    previewRootName = gameObject.name,
                    exitReason = "H2_tryGet_false"
                });
            }
            #endregion
            return;
        }

        #region agent log
        var dbg = new AgentTemplatePickDbg
        {
            tryGet = true,
            rawNewInputDown = rawNew,
            legacyMouseDown = legacyDown,
            enabled = enabled,
            activeInHierarchy = gameObject.activeInHierarchy,
            previewRootName = gameObject.name,
            bestRayPickDist = float.MaxValue
        };
        #endregion

        if (!enabled || !gameObject.activeInHierarchy)
        {
            #region agent log
            dbg.exitReason = "H1_disabled_or_inactive";
            AgentLogTemplatePick("H1", dbg);
            #endregion
            return;
        }

        dbg.mouseDeviceId = mouseDev != null ? mouseDev.deviceId : -1;
        dbg.pointerOverUiLegacyMinus1 = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(-1);
        bool blockInteractive = ShouldBlockWorldPickForInteractiveUi(screenPx, mouseDev, _uiRaycastScratch, out int uiCnt, out string uiTopShort);
        dbg.uiRaycastCount = uiCnt;
        dbg.uiTopHitShort = uiTopShort ?? "";
        dbg.blockInteractiveUi = blockInteractive;
        if (blockInteractive)
        {
            #region agent log
            dbg.pointerOverUI = true;
            dbg.exitReason = "H3_interactive_ui";
            AgentLogTemplatePick("H3", dbg);
            #endregion
            return;
        }

        var cam = Camera.main;
        if (cam == null)
        {
            #region agent log
            dbg.camNull = true;
            dbg.exitReason = "H4_cam_null";
            AgentLogTemplatePick("H4", dbg);
            #endregion
            return;
        }

        var ray = cam.ScreenPointToRay(screenPx);
        // Tips use trigger colliders; project Physics.queriesHitTriggers may be false — always query triggers here.
        var hits = Physics.RaycastAll(ray, RaycastDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);

        BondFormationTemplatePreviewPick bestPick = null;
        float bestRayDist = float.MaxValue;

        dbg.hitCount = hits != null ? hits.Length : 0;
        var hb = new StringBuilder(320);
        if (hits != null && hits.Length > 0)
        {
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            int nShow = Mathf.Min(hits.Length, 8);
            for (int i = 0; i < nShow; i++)
            {
                var h = hits[i];
                if (h.collider == null) continue;
                string cname = h.collider.name ?? "";
                if (cname.Length > 48) cname = cname.Substring(0, 45) + "...";
                var pickOnHit = h.collider.GetComponent<BondFormationTemplatePreviewPick>()
                    ?? h.collider.GetComponentInParent<BondFormationTemplatePreviewPick>();
                if (hb.Length > 0) hb.Append('|');
                hb.Append(h.distance.ToString("F2")).Append(':').Append(cname).Append(":pk=").Append(pickOnHit != null);
            }

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
        dbg.hitsSummary = hb.ToString();

        var stemsArr = transform.GetComponentsInChildren<BondFormationTemplateStemRayPick>(true);
        dbg.stemCount = stemsArr != null ? stemsArr.Length : 0;

        // Stems have no collider (unreliable under non-uniform cylinder scale); pick by ray vs world segment.
        if (TryPickStemByRaySegment(ray, transform, out var stemPick, out float stemRayDist))
        {
            dbg.stemPickOk = true;
            dbg.stemT = stemRayDist;
            if (stemRayDist < bestRayDist)
            {
                bestRayDist = stemRayDist;
                bestPick = stemPick;
            }
        }

        dbg.bestRayPickDist = bestRayDist;
        dbg.finalPickNonNull = bestPick != null;
        dbg.exitReason = bestPick != null ? "ok" : "H5_H6_no_template_pick";
        #region agent log
        AgentLogTemplatePick("H5_H6", dbg);
        #endregion

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
