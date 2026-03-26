using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

public class ElectronOrbitalFunction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] int electronCount;
    [SerializeField] ElectronFunction electronPrefab;
    [SerializeField] float electronSpacing = 0.5f; // Distance between electrons (larger = more spread)
    [SerializeField] Sprite stretchSprite; // Optional: single sprite for stretch. If null, uses procedural triangle+circle composite.
    [SerializeField] [Range(0.05f, 1f)] float orbitalVisualAlpha = 0.05f;
    [Tooltip("Edit-mode selected orbital: body alpha (higher than orbitalVisualAlpha so the lobe reads brighter).")]
    [SerializeField] [Range(0.05f, 1f)] float orbitalHighlightAlpha = 0.14f;
    [Tooltip("3D drag stretch only: scales hemisphere diameter and cone base (XZ) vs idle orbital sizing.")]
    [SerializeField] [Range(0.35f, 1f)] float dragStretch3DCrossSectionScale = 0.65f;
    [Tooltip("3D drag: offset from flat seam into the hemispherical bulk along the tip axis, as a fraction of cap radius (keeps electrons under the dome, not on the outer shell).")]
    [SerializeField] [Range(0.08f, 0.5f)] float drag3DElectronDepthInCapFraction = 0.32f;
    [Tooltip("Draw seam → target → electron paths in Scene view while dragging a 3D orbital.")]
    [SerializeField] bool debugDraw3DElectronDrag;

    /// <summary>Unity Console logs for π-bond orbital redistribution (tag <c>[π-redist]</c>).</summary>
    public static bool DebugPiOrbitalRedistribution;

    public static void LogPiRedistDebug(string message) { }

    /// <summary>Unity + project <c>.log</c>: <c>[σ-form-pose]</c> — bonding orbital + non-bond tips during σ formation. Default off.</summary>
    public static bool DebugLogSigmaBondFormationOrbitalPose = false;

    /// <summary>Unity + <c>.log</c>: <c>[σ-form-rot]</c> — why heavy-atom lobes / shared σ tip move during animated σ formation (redistribute targets vs hybrid refresh vs bond tip apply). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogSigmaFormationHeavyOrbRotationWhy = true;

    /// <summary>Budget for <c>[σ-form-rot-hybrid]</c> / <c>[σ-form-rot-bond]</c> lines during one gesture (consumed by hybrid refresh and bond tip apply).</summary>
    public static int SigmaFormationHeavyRotDiagBudget;

    /// <summary>During σ formation, treat +X tips within this angle of the internuclear line (either direction) as “along the bond” for skip / reconcile.</summary>
    const float SigmaBondFormationTipAxisMaxSepDeg = 22f;

    /// <summary>
    /// True when both σ +X tips lie along the source→partner internuclear axis (±) and match up to sign.
    /// Covers 180° σTipΔ° cases where the bond cylinder convention disagrees with the dragged lobe but chemistry is unchanged.
    /// </summary>
    static bool BondingSigmaOrbitalWorldTipsMatchAlongInternuclearAxis(
        AtomFunction sourceAtom,
        AtomFunction partnerAtom,
        Quaternion startWorldRot,
        Quaternion endWorldRot,
        float maxAxisSepDeg,
        float maxUndirectedTipSepDeg)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || sourceAtom == null || partnerAtom == null) return false;
        var axis = partnerAtom.transform.position - sourceAtom.transform.position;
        if (axis.sqrMagnitude < 1e-10f) return false;
        axis.Normalize();
        var sTip = (startWorldRot * Vector3.right).normalized;
        var eTip = (endWorldRot * Vector3.right).normalized;
        float sAlong = Mathf.Min(Vector3.Angle(sTip, axis), Vector3.Angle(sTip, -axis));
        float eAlong = Mathf.Min(Vector3.Angle(eTip, axis), Vector3.Angle(eTip, -axis));
        if (sAlong > maxAxisSepDeg || eAlong > maxAxisSepDeg) return false;
        float undir = Mathf.Min(Vector3.Angle(sTip, eTip), Vector3.Angle(sTip, -eTip));
        return undir < maxUndirectedTipSepDeg;
    }

    /// <summary>
    /// Bond-cylinder <paramref name="bondCylinderWorldRot"/> often matches the dragged lobe on +X direction but differs by
    /// pure <b>roll</b> around +X (see Step2_precalc <c>Δrot°</c> with <c>σTipΔ°≈0</c>). Slerping to the cylinder quat
    /// spins that roll. Swing start onto the cylinder tip with minimal SO(3) motion from <paramref name="startWorldRot"/>.
    /// </summary>
    static Quaternion SigmaFormationBondingOrbitalTargetWorldRotPreservingRollAroundTip(
        Quaternion startWorldRot,
        Quaternion bondCylinderWorldRot,
        float tipUndirectedMaxDeg)
    {
        var tipS = startWorldRot * Vector3.right;
        var tipE = bondCylinderWorldRot * Vector3.right;
        if (tipS.sqrMagnitude < 1e-12f || tipE.sqrMagnitude < 1e-12f) return bondCylinderWorldRot;
        tipS.Normalize();
        tipE.Normalize();
        // Parallel or anti-parallel on the same line: +X matches up to sign (σTipUndir°≈0). Do not slerp roll or a 180° flip.
        float tipSepDeg = Vector3.Angle(tipS, tipE);
        float tipUndirDeg = Mathf.Min(tipSepDeg, 180f - tipSepDeg);
        if (tipUndirDeg < tipUndirectedMaxDeg)
            return startWorldRot;
        return Quaternion.FromToRotation(tipS, tipE) * startWorldRot;
    }

    /// <summary>Call from diagnostics only. Returns false when logging is off or the budget is exhausted.</summary>
    public static bool ConsumeSigmaFormationHeavyRotDiag()
    {
        if (!DebugLogSigmaFormationHeavyOrbRotationWhy || SigmaFormationHeavyRotDiagBudget <= 0) return false;
        SigmaFormationHeavyRotDiagBudget--;
        return true;
    }

    static void SigmaFormationRotDiagLine(string subtag, string message)
    {
        string line = "[σ-form-rot-" + subtag + "] " + message;
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
        Debug.Log(line);
    }

    /// <summary>
    /// One line per checkpoint: bonding world pose vs <see cref="CovalentBond.GetOrbitalTargetWorldState"/>, tip undirected
    /// separation, δ magnitude. Pass <paramref name="step2SmoothS"/> &lt; 0 when not in step-2 time param.
    /// </summary>
    static void LogSigmaFormationPoseCheckpoint(
        string checkpointId,
        CovalentBond bond,
        ElectronOrbitalFunction sourceOrbital,
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        float rotThreshold,
        float step2SmoothS,
        bool pureSigmaRelaxGaugePath,
        Quaternion? bondingOrbWorldRotDiagRef = null)
    {
        if (!DebugLogSigmaFormationHeavyOrbRotationWhy || !OrbitalAngleUtility.UseFull3DOrbitalGeometry
            || bond == null || sourceOrbital == null || sourceAtom == null || targetAtom == null) return;
        bond.UpdateBondTransformToCurrentAtoms();
        var (twp, twr) = bond.GetOrbitalTargetWorldState();
        Quaternion ow = sourceOrbital.transform.rotation;
        Vector3 owp = sourceOrbital.transform.position;
        float dPos = Vector3.Distance(owp, twp);
        float dRot = bondingOrbWorldRotDiagRef.HasValue
            ? Quaternion.Angle(ow, bondingOrbWorldRotDiagRef.Value)
            : Quaternion.Angle(ow, twr);
        string dRotKey = bondingOrbWorldRotDiagRef.HasValue ? "dRotOrbVsLockedStartDeg" : "dRotOrbVsGetWorldRotDeg";
        Vector3 oTip = ow * Vector3.right;
        Vector3 wTip = twr * Vector3.right;
        if (oTip.sqrMagnitude > 1e-12f) oTip.Normalize();
        if (wTip.sqrMagnitude > 1e-12f) wTip.Normalize();
        float tipUndir = (oTip.sqrMagnitude > 1e-12f && wTip.sqrMagnitude > 1e-12f)
            ? Mathf.Min(Vector3.Angle(oTip, wTip), Vector3.Angle(oTip, -wTip))
            : 0f;
        bool along = BondingSigmaOrbitalWorldTipsMatchAlongInternuclearAxis(
            sourceAtom, targetAtom, ow, twr, SigmaBondFormationTipAxisMaxSepDeg, rotThreshold);
        float deltaDeg = Quaternion.Angle(Quaternion.identity, bond.GetOrbitalRedistributionWorldDeltaForDiagnostics());
        string parentName = sourceOrbital.transform.parent != null ? sourceOrbital.transform.parent.name : "null";
        string sPart = step2SmoothS >= 0f ? " step2s=" + step2SmoothS.ToString("F3") : "";
        SigmaFormationRotDiagLine("pose",
            checkpointId + sPart + " pureSigmaGaugePath=" + pureSigmaRelaxGaugePath +
            " dPosOrbVsTgtWp=" + dPos.ToString("F5") + " " + dRotKey + "=" + dRot.ToString("F2") +
            " tipUndirDeg=" + tipUndir.ToString("F2") + " alongInternuclearTip=" + along +
            " flip=" + bond.orbitalRotationFlipped + " deltaFromIdentityDeg=" + deltaDeg.ToString("F2") +
            " parent=" + parentName);
    }

    static void LogSigmaFormationStep2Precalc(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        List<(AtomFunction atom, Vector3 startWorld, Vector3 endWorld)> sigmaRelaxList,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redistA,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redistB,
        List<(Vector3 pos, Quaternion rot)> redistAStarts,
        List<(Vector3 pos, Quaternion rot)> redistBStarts,
        Vector3 bondOrbitalStartWorldPos,
        Quaternion bondOrbitalStartWorldRot,
        (Vector3 worldPos, Quaternion worldRot) bondOrbitalEnd,
        bool isFlip,
        bool sigmaNeedsFlip,
        bool needsRearrange,
        bool needsRedistribute,
        bool hasSigmaRelaxMovement,
        bool orbitalAlreadyAtBond,
        bool hasNewmanStagger,
        bool skipStep2,
        float posThreshold,
        float rotThreshold,
        float alignThreshold,
        float rotBondThreshold)
    {
        const string tag = "precalc";
        SigmaFormationRotDiagLine(tag,
            "Step2 src=" + FmtAtomBrief(sourceAtom) + " tgt=" + FmtAtomBrief(targetAtom) +
            " isFlip=" + isFlip + " sigmaNeedsFlip=" + sigmaNeedsFlip + " skipStep2=" + skipStep2);
        SigmaFormationRotDiagLine(tag,
            "skipStep2_reason needsRearrange=" + needsRearrange + " needsRedistribute=" + needsRedistribute +
            " hasSigmaRelaxMovement=" + hasSigmaRelaxMovement + " orbitalAlreadyAtBond=" + orbitalAlreadyAtBond +
            " hasNewmanStagger=" + hasNewmanStagger);

        float tipAngleDeg = 0f, tipUndirDeg = 0f;
        bool tipsAlongInternuclear = false;
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && sourceAtom != null && targetAtom != null)
        {
            var startTipW = bondOrbitalStartWorldRot * Vector3.right;
            var endTipW = bondOrbitalEnd.worldRot * Vector3.right;
            if (startTipW.sqrMagnitude > 1e-12f && endTipW.sqrMagnitude > 1e-12f)
            {
                tipAngleDeg = Vector3.Angle(startTipW, endTipW);
                tipUndirDeg = Mathf.Min(tipAngleDeg, Vector3.Angle(startTipW, -endTipW));
                tipsAlongInternuclear = BondingSigmaOrbitalWorldTipsMatchAlongInternuclearAxis(
                    sourceAtom, targetAtom, bondOrbitalStartWorldRot, bondOrbitalEnd.worldRot,
                    SigmaBondFormationTipAxisMaxSepDeg, rotBondThreshold);
            }
        }
        SigmaFormationRotDiagLine(tag,
            "bondingOrb=" + (sourceOrbital != null ? sourceOrbital.name : "null") +
            " bondEndVsStart dPos=" + Vector3.Distance(bondOrbitalStartWorldPos, bondOrbitalEnd.worldPos).ToString("F4") +
            " dRotDeg=" + Quaternion.Angle(bondOrbitalStartWorldRot, bondOrbitalEnd.worldRot).ToString("F2") +
            " sigmaTipDeg=" + tipAngleDeg.ToString("F2") + " sigmaTipUndirDeg=" + tipUndirDeg.ToString("F2") +
            " alongAxisMatch=" + tipsAlongInternuclear + " alignTh=" + alignThreshold + " rotTh=" + rotBondThreshold);

        if (sigmaRelaxList != null && sigmaRelaxList.Count > 0)
        {
            SigmaFormationRotDiagLine(tag, "sigmaRelax n=" + sigmaRelaxList.Count);
            foreach (var (atom, st, en) in sigmaRelaxList)
            {
                if (atom == null) continue;
                float leg = Vector3.Distance(st, en);
                SigmaFormationRotDiagLine(tag, "sigmaRelax leg atom=" + atom.name + "(Z=" + atom.AtomicNumber + ") d=" + leg.ToString("F5"));
            }
        }
        else
            SigmaFormationRotDiagLine(tag, "sigmaRelax (none)");

        LogRedistHeavyPrecalcLines(tag, "redistA", sourceAtom, redistA, redistAStarts, sourceOrbital, posThreshold, rotThreshold);
        LogRedistHeavyPrecalcLines(tag, "redistB", targetAtom, redistB, redistBStarts, sourceOrbital, posThreshold, rotThreshold);

        SigmaFormationRotDiagLine(tag,
            "note: with needsRedistribute/needsRearrange false in 3D, post-formation ApplyRedistributeTargets + hybrid σ apply are skipped; " +
            "run RedistributeOrbitals3D (RedistributeOrbitals3DOld) to align nonbond + δ.");
    }

    static string FmtAtomBrief(AtomFunction a) =>
        a == null ? "null" : $"{a.name}(Z={a.AtomicNumber} id={a.GetInstanceID()})";

    static void LogRedistHeavyPrecalcLines(
        string diagTag,
        string label,
        AtomFunction nucleus,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redist,
        List<(Vector3 pos, Quaternion rot)> starts,
        ElectronOrbitalFunction sourceOrbital,
        float posThreshold,
        float rotThreshold)
    {
        if (nucleus == null || nucleus.AtomicNumber <= 1) return;
        SigmaFormationRotDiagLine(diagTag, label + " heavy=" + FmtAtomBrief(nucleus) + " entries=" + (redist?.Count ?? 0));
        if (redist == null || starts == null || redist.Count != starts.Count) return;

        float maxPos = 0f, maxRot = 0f;
        for (int i = 0; i < redist.Count; i++)
        {
            var e = redist[i];
            if (e.orb == null || e.orb.transform.parent != nucleus.transform) continue;
            float dPos = Vector3.Distance(e.orb.transform.localPosition, e.pos);
            float dRot = Quaternion.Angle(e.orb.transform.localRotation, e.rot);
            maxPos = Mathf.Max(maxPos, dPos);
            maxRot = Mathf.Max(maxRot, dRot);
            bool exceeds = dPos > posThreshold || dRot > rotThreshold;
            bool isDraggedBonding = ReferenceEquals(e.orb, sourceOrbital);
            SigmaFormationRotDiagLine(diagTag,
                label + " entry i=" + i + " orb=" + e.orb.name + " e=" + e.orb.ElectronCount +
                " dPos=" + dPos.ToString("F4") + " dRotDeg=" + dRot.ToString("F2") +
                " exceedsPoseTh=" + exceeds + " isDraggedBondingOrb=" + isDraggedBonding);
        }
        SigmaFormationRotDiagLine(diagTag,
            label + "_max dPos=" + maxPos.ToString("F4") + " dRotDeg=" + maxRot.ToString("F2") +
            " poseTh pos=" + posThreshold + " rotDeg=" + rotThreshold);
    }

    static void LogSigmaFormationBondingOrb(string phase, string label, ElectronOrbitalFunction orb) { }

    static void LogSigmaFormationNonbondTipsOnAtom(string phase, AtomFunction atom, AtomFunction towardPartner) { }

    AtomFunction bondedAtom;
    CovalentBond bond;
    bool isBeingHeld;
    Vector3 dragOffset;
    Vector2 pointerDownPosition;

    public const int MaxElectrons = 2;
    public int ElectronCount
    {
        get => electronCount;
        set
        {
            int oldCount = electronCount;
            electronCount = Mathf.Clamp(value, 0, MaxElectrons);
            int delta = electronCount - oldCount;
            SyncElectronObjects();
            if (delta != 0)
            {
                if (bond != null)
                    bond.NotifyElectronCountChanged();
                else if (bondedAtom != null)
                {
                    for (int i = 0; i < Mathf.Abs(delta); i++)
                        if (delta > 0) bondedAtom.OnElectronAdded();
                        else bondedAtom.OnElectronRemoved();
                }
            }
        }
    }

    public bool IsBonded => (bondedAtom != null || bond != null) && !isBeingHeld;
    public AtomFunction BondedAtom => bondedAtom;
    public CovalentBond Bond => bond;

    public void SetBondedAtom(AtomFunction atom) => bondedAtom = atom;

    public void SetBond(CovalentBond b) => bond = b;

    /// <summary>Prefab used for lone-pair electrons (e.g. spawn free electrons for testing).</summary>
    public ElectronFunction ElectronPrefab => electronPrefab;

    public void RemoveElectron(ElectronFunction electron)
    {
        if (electron.transform.parent != transform) return;
        electronCount = Mathf.Clamp(electronCount - 1, 0, MaxElectrons);
        electron.transform.SetParent(null);
        if (bond != null)
            bond.NotifyElectronCountChanged();
        else
            bondedAtom?.OnElectronRemoved();
        SyncElectronObjects();
    }

    public bool CanAcceptElectron() => electronCount < MaxElectrons;

    public bool AcceptElectron(ElectronFunction electron)
    {
        if (electronCount >= MaxElectrons) return false;
        Destroy(electron.gameObject);
        electronCount++;
        SyncElectronObjects();
        if (bond != null)
            bond.NotifyElectronCountChanged();
        else
        {
            bondedAtom?.OnElectronAdded();
            bondedAtom?.SetupIgnoreCollisions();
        }
        return true;
    }

    public bool ContainsPoint(Vector3 worldPos)
    {
        var col2D = GetComponent<Collider2D>();
        if (col2D != null) return col2D.OverlapPoint(worldPos);
        var col = GetComponent<Collider>();
        return col != null && col.bounds.Contains(worldPos);
    }

    const float ViewOverlapScreenPaddingPx = 14f;

    /// <summary>World bounds used to project an orbital silhouette to the screen (collider preferred, then renderer).</summary>
    public Bounds GetOrbitalBoundsForView()
    {
        var col = GetComponent<Collider>();
        if (col != null) return col.bounds;
        var col2D = GetComponent<Collider2D>();
        if (col2D != null) return col2D.bounds;
        var r = GetComponent<Renderer>();
        if (r != null) return r.bounds;
        return new Bounds(transform.position, Vector3.one * 0.25f);
    }

    /// <summary>
    /// True if the screen-space axis-aligned bounds of two orbitals overlap. Depth along the view axis is ignored
    /// so bonding matches what the player sees in the 2D window in perspective mode.
    /// </summary>
    public static bool OrbitalViewOverlaps(Camera cam, ElectronOrbitalFunction a, ElectronOrbitalFunction b, float expandPixels = ViewOverlapScreenPaddingPx)
    {
        if (cam == null || a == null || b == null) return false;
        if (!TryProjectOrbitalToScreenRect(cam, a, expandPixels, out var ra)) return false;
        if (!TryProjectOrbitalToScreenRect(cam, b, expandPixels, out var rb)) return false;
        return ra.Overlaps(rb);
    }

    /// <summary>
    /// True when the orbital’s screen-space AABB overlaps the atom’s, using the same world acceptance radius as
    /// <see cref="TryBreakBond"/> (BondRadius×1.2) and the same padding as <see cref="OrbitalViewOverlaps"/>.
    /// Lets perspective players break a bond when the lobe visibly covers the atom, even without true 3D overlap.
    /// </summary>
    public static bool OrbitalViewOverlapsAtom(Camera cam, ElectronOrbitalFunction orbital, AtomFunction atom, float expandPixels = ViewOverlapScreenPaddingPx)
    {
        if (cam == null || orbital == null || atom == null) return false;
        if (!TryProjectOrbitalToScreenRect(cam, orbital, expandPixels, out var ro)) return false;
        if (!TryProjectAtomAcceptanceBoundsToScreenRect(cam, atom, expandPixels, out var ra)) return false;
        return ro.Overlaps(ra);
    }

    static bool TryProjectAtomAcceptanceBoundsToScreenRect(Camera cam, AtomFunction atom, float padPixels, out Rect rect)
    {
        rect = default;
        if (cam == null || atom == null) return false;
        float r = atom.BondRadius * 1.2f;
        var b = new Bounds(atom.transform.position, Vector3.one * (r * 2f));
        return TryProjectWorldBoundsToScreenRect(cam, b, padPixels, out rect);
    }

    static bool TryProjectOrbitalToScreenRect(Camera cam, ElectronOrbitalFunction o, float padPixels, out Rect rect)
    {
        return TryProjectWorldBoundsToScreenRect(cam, o.GetOrbitalBoundsForView(), padPixels, out rect);
    }

    static bool TryProjectWorldPointToScreenRect(Camera cam, Vector3 worldPos, float padPixels, out Rect rect)
    {
        rect = default;
        if (cam == null) return false;
        Vector3 s = cam.WorldToScreenPoint(worldPos);
        if (s.z < cam.nearClipPlane) return false;
        rect = Rect.MinMaxRect(s.x - padPixels, s.y - padPixels, s.x + padPixels, s.y + padPixels);
        return true;
    }

    /// <summary>
    /// Screen-space overlap between a dragged electron (world position ± padding) and an orbital silhouette,
    /// same padding as <see cref="OrbitalViewOverlaps"/> for bonding.
    /// </summary>
    public static bool ElectronViewOverlapsOrbital(Camera cam, ElectronOrbitalFunction orbital, Vector3 electronWorldPos, float expandPixels = ViewOverlapScreenPaddingPx)
    {
        if (cam == null || orbital == null) return false;
        if (!TryProjectWorldPointToScreenRect(cam, electronWorldPos, expandPixels, out var re)) return false;
        if (!TryProjectOrbitalToScreenRect(cam, orbital, expandPixels, out var ro)) return false;
        return re.Overlaps(ro);
    }

    static bool TryProjectWorldBoundsToScreenRect(Camera cam, Bounds b, float padPixels, out Rect rect)
    {
        rect = default;
        if (cam == null) return false;
        Vector3 c = b.center;
        Vector3 e = b.extents;
        bool any = false;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int ix = -1; ix <= 1; ix += 2)
        {
            for (int iy = -1; iy <= 1; iy += 2)
            {
                for (int iz = -1; iz <= 1; iz += 2)
                {
                    Vector3 w = c + new Vector3(e.x * ix, e.y * iy, e.z * iz);
                    Vector3 s = cam.WorldToScreenPoint(w);
                    if (s.z < cam.nearClipPlane) continue;
                    any = true;
                    minX = Mathf.Min(minX, s.x);
                    minY = Mathf.Min(minY, s.y);
                    maxX = Mathf.Max(maxX, s.x);
                    maxY = Mathf.Max(maxY, s.y);
                }
            }
        }

        if (!any) return false;
        rect = Rect.MinMaxRect(minX - padPixels, minY - padPixels, maxX + padPixels, maxY + padPixels);
        return true;
    }

    public void SetPointerBlocked(bool blocked)
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col != null) col.enabled = !blocked;
        if (col2D != null) col2D.enabled = !blocked;
    }

    /// <summary>Re-spawns/repositions electron children after lobe layout settles (e.g. after VSEPR redistribution).</summary>
    public void RefreshElectronSyncAfterLayout() => SyncElectronObjects();

    static void ConfigureUnlitTransparent(Material mat)
    {
        if (mat == null) return;
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        else
        {
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
        }
    }

    bool editSelectionHighlightActive;

    /// <summary>Highlight this orbital in edit mode by raising body opacity (no ring overlay).</summary>
    public void SetHighlighted(bool on)
    {
        editSelectionHighlightActive = on;
        DestroyLegacyOrbitalOutlineDecorations();
        ApplyOrbitalEditSelectionVisual(on);
    }

    void DestroyLegacyOrbitalOutlineDecorations()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            string n = ch.name;
            if (n == "SelectionHighlight" || n == "EditModeSelectionGlow")
                Destroy(ch.gameObject);
        }
    }

    void EnsureOriginalColorFromRenderer()
    {
        if (originalColor.a >= 0.01f) return;
        var sr = GetComponent<SpriteRenderer>();
        var mr = GetComponent<MeshRenderer>();
        if (sr != null) originalColor = sr.color;
        else if (mr != null && mr.sharedMaterial != null) originalColor = GetMaterialTint(mr.sharedMaterial);
    }

    void ApplyOrbitalEditSelectionVisual(bool selected)
    {
        var sr = GetComponent<SpriteRenderer>();
        var mr = GetComponent<MeshRenderer>();
        if (sr == null && mr == null) return;
        EnsureOriginalColorFromRenderer();
        if (!selected)
        {
            ApplyOrbitalVisualOpacity(sr, mr);
            return;
        }

        float a = Mathf.Clamp01(Mathf.Max(orbitalHighlightAlpha, orbitalVisualAlpha));
        Color c = new Color(originalColor.r, originalColor.g, originalColor.b, a);
        if (sr != null) sr.color = c;
        else if (mr != null) SetMaterialTint(mr.material, c);
    }

    /// <summary>Show or hide the orbital and its electron visuals. Used when bond displays as a line.</summary>
    public void SetVisualsEnabled(bool enabled)
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr != null) sr.enabled = enabled;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
            r.enabled = enabled;
    }

    public void SetPhysicsEnabled(bool enabled)
    {
        void SetRb(GameObject go)
        {
            var rb = go.GetComponent<Rigidbody>();
            var rb2D = go.GetComponent<Rigidbody2D>();
            if (rb != null) rb.isKinematic = !enabled;
            if (rb2D != null) rb2D.simulated = enabled;
        }
        SetRb(gameObject);
        foreach (var e in GetComponentsInChildren<ElectronFunction>())
            SetRb(e.gameObject);
    }

    Vector3 originalLocalPosition;
    Vector3 originalLocalScale;
    Quaternion originalLocalRotation;
    Color originalColor;
    GameObject stretchVisual;
    bool stretchVisualIs3D;
    SpriteRenderer mainSpriteRenderer;
    MeshRenderer mainMeshRenderer;
    const float MinStretchLength = 0.5f;
    const float OffsetScale = 1.14f;
    [SerializeField] float apexPadding = 0.2f;
    static Sprite cachedTriangleSprite;
    static Sprite cachedCircleSprite;
    static Material cachedTriangleClipMaterial;
    static Mesh cachedBuiltinSphereMesh;
    static Mesh cachedUnitConeMesh;
    static Mesh cachedUnitHemisphereMesh;
    static readonly int ClipCenterId = Shader.PropertyToID("_ClipCenter");
    static readonly int ClipRadiusXId = Shader.PropertyToID("_ClipRadiusX");
    static readonly int ClipRadiusYId = Shader.PropertyToID("_ClipRadiusY");
    static readonly int ShaderPropBaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ShaderPropColor = Shader.PropertyToID("_Color");
    /// <summary>URP 2D Mesh2D-Lit-Default tint (see shader Properties — not _Color).</summary>
    static readonly int ShaderPropWhite = Shader.PropertyToID("_White");
    static readonly int ShaderPropRendererColor = Shader.PropertyToID("_RendererColor");

    static Color GetMaterialTint(Material mat)
    {
        if (mat == null) return Color.white;
        if (mat.HasProperty(ShaderPropBaseColor)) return mat.GetColor(ShaderPropBaseColor);
        if (mat.HasProperty(ShaderPropWhite)) return mat.GetColor(ShaderPropWhite);
        if (mat.HasProperty(ShaderPropRendererColor)) return mat.GetColor(ShaderPropRendererColor);
        if (mat.HasProperty(ShaderPropColor)) return mat.GetColor(ShaderPropColor);
        return Color.white;
    }

    static void SetMaterialTint(Material mat, Color c)
    {
        if (mat == null) return;
        if (mat.HasProperty(ShaderPropBaseColor)) mat.SetColor(ShaderPropBaseColor, c);
        else if (mat.HasProperty(ShaderPropWhite)) mat.SetColor(ShaderPropWhite, c);
        else if (mat.HasProperty(ShaderPropRendererColor)) mat.SetColor(ShaderPropRendererColor, c);
        else if (mat.HasProperty(ShaderPropColor)) mat.SetColor(ShaderPropColor, c);
    }

    static Material GetOrCreateTriangleClipMaterial()
    {
        if (cachedTriangleClipMaterial != null) return cachedTriangleClipMaterial;
        var shader = Shader.Find("OrgoSimulator/TriangleClipCircle");
        cachedTriangleClipMaterial = shader != null ? new Material(shader) : null;
        return cachedTriangleClipMaterial;
    }

    static Sprite GetOrCreateTriangleSprite()
    {
        if (cachedTriangleSprite != null) return cachedTriangleSprite;
        cachedTriangleSprite = CreateTriangleSprite();
        return cachedTriangleSprite;
    }

    static Sprite GetOrCreateCircleSprite()
    {
        if (cachedCircleSprite != null) return cachedCircleSprite;
        cachedCircleSprite = CreateCircleSprite();
        return cachedCircleSprite;
    }

    static Sprite CreateTriangleSprite(int width = 64, int height = 64)
    {
        var tex = new Texture2D(width, height);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color32[width * height];
        float cx = (width - 1) * 0.5f;
        for (int y = 0; y < height; y++)
        {
            float t = (float)y / (height - 1);
            float halfWidth = 0.5f * t;
            for (int x = 0; x < width; x++)
            {
                float dx = Mathf.Abs((x - cx) / (width * 0.5f));
                float alpha = dx <= halfWidth ? 1f - (dx / (halfWidth + 0.01f)) * 0.3f : 0f;
                if (alpha > 0.02f)
                    pixels[y * width + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255));
                else
                    pixels[y * width + x] = Color.clear;
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 32f);
    }

    static Sprite CreateCircleSprite(int size = 64)
    {
        var tex = new Texture2D(size, size);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color32[size * size];
        float cx = (size - 1) * 0.5f;
        float r = cx * 0.9f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx;
                float dy = y - cx;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = d <= r ? 1f - (d / r) * 0.3f : 0f;
                if (alpha > 0.01f)
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                else
                    pixels[y * size + x] = Color.clear;
            }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f);
    }

    static bool Use3DOrbitalPresentation() =>
        Camera.main != null && !Camera.main.orthographic;

    static Mesh GetBuiltinSphereMesh()
    {
        if (cachedBuiltinSphereMesh != null) return cachedBuiltinSphereMesh;
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        cachedBuiltinSphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);
        return cachedBuiltinSphereMesh;
    }

    /// <summary>Upper hemisphere, radius 0.5 (diameter 1 at scale 1, matches Unity sphere). Flat cap in XZ at y=0, dome toward +Y — attach y=0 flush to cone top.</summary>
    static Mesh GetOrCreateUnitHemisphereMesh()
    {
        if (cachedUnitHemisphereMesh != null) return cachedUnitHemisphereMesh;
        const float rad = 0.5f;
        const int nSeg = 24;
        const int nLat = 12;
        var verts = new List<Vector3>();
        var tris = new List<int>();

        verts.Add(new Vector3(0f, rad, 0f));

        for (int lat = 1; lat <= nLat; lat++)
        {
            float phi = (lat / (float)nLat) * (Mathf.PI * 0.5f);
            float y = rad * Mathf.Cos(phi);
            float ringR = rad * Mathf.Sin(phi);
            for (int seg = 0; seg < nSeg; seg++)
            {
                float theta = (seg / (float)nSeg) * Mathf.PI * 2f;
                verts.Add(new Vector3(ringR * Mathf.Cos(theta), y, ringR * Mathf.Sin(theta)));
            }
        }

        for (int seg = 0; seg < nSeg; seg++)
        {
            int s1 = (seg + 1) % nSeg;
            tris.Add(0);
            tris.Add(1 + s1);
            tris.Add(1 + seg);
        }

        for (int lat = 1; lat < nLat; lat++)
        {
            int ringA = 1 + (lat - 1) * nSeg;
            int ringB = 1 + lat * nSeg;
            for (int seg = 0; seg < nSeg; seg++)
            {
                int s1 = (seg + 1) % nSeg;
                int a = ringA + seg, b = ringA + s1, c = ringB + seg, d = ringB + s1;
                tris.Add(a);
                tris.Add(c);
                tris.Add(b);
                tris.Add(b);
                tris.Add(c);
                tris.Add(d);
            }
        }

        int eqBase = 1 + (nLat - 1) * nSeg;
        int capCenter = verts.Count;
        verts.Add(Vector3.zero);
        for (int seg = 0; seg < nSeg; seg++)
        {
            int s1 = (seg + 1) % nSeg;
            tris.Add(capCenter);
            tris.Add(eqBase + s1);
            tris.Add(eqBase + seg);
        }

        cachedUnitHemisphereMesh = new Mesh { name = "OrgoUnitHemisphere" };
        cachedUnitHemisphereMesh.SetVertices(verts);
        cachedUnitHemisphereMesh.SetTriangles(tris, 0);
        cachedUnitHemisphereMesh.RecalculateNormals();
        cachedUnitHemisphereMesh.RecalculateBounds();
        return cachedUnitHemisphereMesh;
    }

    static Mesh GetOrCreateUnitConeMesh()
    {
        if (cachedUnitConeMesh != null) return cachedUnitConeMesh;
        const int n = 20;
        const float h = 1f;
        const float r = 0.5f;
        var vertices = new Vector3[n + 2];
        var uv = new Vector2[n + 2];
        vertices[0] = new Vector3(0f, -h * 0.5f, 0f);
        uv[0] = new Vector2(0.5f, 0f);
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n * Mathf.PI * 2f;
            float x = Mathf.Cos(t) * r;
            float z = Mathf.Sin(t) * r;
            vertices[i + 1] = new Vector3(x, h * 0.5f, z);
            uv[i + 1] = new Vector2(i / (float)n, 1f);
        }
        var tris = new int[n * 3];
        for (int i = 0; i < n; i++)
        {
            tris[i * 3] = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = i + 2;
        }
        cachedUnitConeMesh = new Mesh { name = "OrgoUnitCone" };
        cachedUnitConeMesh.vertices = vertices;
        cachedUnitConeMesh.triangles = tris;
        cachedUnitConeMesh.uv = uv;
        cachedUnitConeMesh.RecalculateNormals();
        cachedUnitConeMesh.RecalculateBounds();
        return cachedUnitConeMesh;
    }

    static void ConfigureUrpSurfaceTransparent(Material mat)
    {
        if (mat == null) return;
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite"))
                mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }
        else
        {
            // Without _Surface (some URP variants), still avoid opaque shell overwriting inner electrons.
            if (mat.HasProperty("_ZWrite"))
                mat.SetInt("_ZWrite", 0);
            mat.renderQueue = (int)RenderQueue.Transparent;
        }
    }

    /// <summary>Prefer URP Unlit so WebGL builds keep a working transparent mesh (Lit transparent variants are often stripped).</summary>
    static Shader TryFindUrplShader()
    {
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
        return sh;
    }

    Material CreateStretchOrbitalMaterial(Color tint)
    {
        var sh = TryFindUrplShader();
        if (sh == null) return null;
        var mat = new Material(sh);
        ConfigureUrpSurfaceTransparent(mat);
        mat.color = tint;
        if (mat.HasProperty(ShaderPropBaseColor)) mat.SetColor(ShaderPropBaseColor, tint);
        return mat;
    }

    static Quaternion RotationFromUpTo(Vector3 dir)
    {
        if (dir.sqrMagnitude < 1e-10f) return Quaternion.identity;
        dir.Normalize();
        if (Vector3.Dot(Vector3.up, dir) < -0.998f)
            return Quaternion.AngleAxis(180f, Vector3.right);
        return Quaternion.FromToRotation(Vector3.up, dir);
    }

    void Setup3DOrbitalVisual(MeshFilter mf, MeshRenderer mr)
    {
        if (mf == null || mr == null) return;
        mf.sharedMesh = GetBuiltinSphereMesh();
        var sh = TryFindUrplShader();
        if (sh == null) return;
        var mat = new Material(sh);
        ConfigureUrpSurfaceTransparent(mat);
        mr.sharedMaterial = mat;
        mainMeshRenderer = mr;

        var cap = GetComponent<CapsuleCollider>();
        if (cap != null)
        {
            cap.direction = 1;
            cap.radius = 0.22f;
            cap.height = 0.62f;
            cap.center = Vector3.zero;
        }
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        isBeingHeld = true;
        pointerDownPosition = eventData.position;
        originalLocalPosition = transform.localPosition;
        originalLocalScale = transform.localScale;
        originalLocalRotation = transform.localRotation;
        var cam = Camera.main;
        var wp = MoleculeWorkPlane.Instance;
        if (cam != null && wp != null && wp.TryGetWorldPoint(cam, eventData.position, out var hit))
            dragOffset = transform.position - hit;
        else
            dragOffset = transform.position - PlanarPointerInteraction.ScreenToWorldPoint(eventData.position);
        mainSpriteRenderer = GetComponent<SpriteRenderer>();
        mainMeshRenderer = GetComponent<MeshRenderer>();
        if (mainSpriteRenderer != null) mainSpriteRenderer.enabled = false;
        if (mainMeshRenderer != null) mainMeshRenderer.enabled = false;
        CreateStretchVisual();
        SetPhysicsEnabled(false);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!isBeingHeld) return;
        var cam = Camera.main;
        Vector3 tip;
        if (cam != null && MoleculeWorkPlane.Instance != null &&
            MoleculeWorkPlane.Instance.TryGetWorldPoint(cam, eventData.position, out var hit))
        {
            tip = hit + dragOffset;
            tip = PlanarPointerInteraction.SnapWorldToWorkPlaneIfPresent(tip);
            transform.position = tip;
            dragOffset = transform.position - hit;
        }
        else
        {
            tip = PlanarPointerInteraction.ScreenToWorldPoint(eventData.position) + dragOffset;
            transform.position = tip;
        }

        UpdateStretchVisual(tip);
    }

    const float ShortClickDragThresholdPx = 10f;

    public void OnPointerUp(PointerEventData eventData)
    {
        isBeingHeld = false;
        Vector3 tip = transform.position;
        if (stretchVisual != null) { Destroy(stretchVisual); stretchVisual = null; }
        Reposition3DElectronsAfterOrbitalDrag();
        if (mainSpriteRenderer != null) mainSpriteRenderer.enabled = true;
        if (mainMeshRenderer != null) mainMeshRenderer.enabled = true;
        RefreshOrbitalBodyVisualAfterDrag();
        SetPhysicsEnabled(true);

        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        if (editMode != null && editMode.EditModeActive && bond == null && electronCount == 1
            && (eventData.position - pointerDownPosition).sqrMagnitude < ShortClickDragThresholdPx * ShortClickDragThresholdPx)
        {
            var atom = bondedAtom ?? transform.parent?.GetComponent<AtomFunction>();
            if (atom != null)
            {
                editMode.OnOrbitalClicked(atom, this);
                transform.localPosition = originalLocalPosition;
                transform.localScale = originalLocalScale;
                transform.localRotation = originalLocalRotation;
                return;
            }
        }

        if (bond != null)
        {
            TryBreakBond(tip);
            return;
        }

        var sourceAtom = bondedAtom ?? transform.parent?.GetComponent<AtomFunction>();
        var targetAtom = AtomFunction.FindBondPartner(sourceAtom, tip, this);
        if (targetAtom != null)
        {
            var targetOrbital = targetAtom.GetAvailableLoneOrbitalForBond(this, electronCount, tip);
            if (targetOrbital != null)
            {
                bool alreadyBonded = sourceAtom.GetBondsTo(targetAtom) >= 1;
                if (alreadyBonded)
                {
                    if (FormCovalentBondPiStart(sourceAtom, targetAtom, targetOrbital))
                        return;
                }
                else if (FormCovalentBondSigmaStart(sourceAtom, targetAtom, targetOrbital, tip))
                {
                    return;
                }
            }
        }

        var swapTarget = TryFindSwapTarget(sourceAtom, tip);
        if (swapTarget != null)
        {
            SwapPositionsWith(swapTarget);
            return;
        }

        ApplyVseprRotationAfterDrag(sourceAtom, tip);
    }

    const float RotateStepDeg = 30f;

    /// <summary>
    /// After a lone-orbital drag (no bond/swap): σ neighbors ≥ 2 — cannot reorient; restore drag start.
    /// 0 σ — rigid rotation of all lone lobes (VSEPR shape preserved). 1 σ — spin lone lobes around the σ axis; bonded orbital fixed.
    /// </summary>
    void ApplyVseprRotationAfterDrag(AtomFunction atom, Vector3 tipWorld)
    {
        if (atom == null) return;
        int sigmaN = atom.GetDistinctSigmaNeighborCount();
        if (sigmaN >= 2)
        {
            transform.localPosition = originalLocalPosition;
            transform.localScale = originalLocalScale;
            transform.localRotation = originalLocalRotation;
            Reposition3DElectronsAfterOrbitalDrag();
            return;
        }

        if (sigmaN == 0)
        {
            ApplyRigidWorldRotationToAllLoneOrbitals(atom, this, tipWorld);
            RefreshAllOrbitalElectronSlotPositions3D(atom);
            return;
        }

        // Exactly one σ neighbor: spin lone lobes around bond axis; σ orbital stays fixed.
        if (ApplyLoneOrbitalSpinAroundSigmaAxis(atom, this, tipWorld))
            RefreshAllOrbitalElectronSlotPositions3D(atom);
        else
            RestoreDraggedOrbitalToPointerDown();
    }

    void RestoreDraggedOrbitalToPointerDown()
    {
        transform.localPosition = originalLocalPosition;
        transform.localScale = originalLocalScale;
        transform.localRotation = originalLocalRotation;
        Reposition3DElectronsAfterOrbitalDrag();
    }

    static void RefreshAllOrbitalElectronSlotPositions3D(AtomFunction atom)
    {
        if (!Use3DOrbitalPresentation()) return;
        foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            foreach (var e in orb.GetComponentsInChildren<ElectronFunction>())
                e.ApplyOrbitalSlotPosition();
        }
    }

    static Vector3 WorldRadialFromLocalRotation(AtomFunction atom, Quaternion localRot)
    {
        Vector3 d = atom.transform.TransformDirection(localRot * Vector3.right);
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            d.z = 0f;
            if (d.sqrMagnitude < 1e-10f) return Vector3.right;
            return d.normalized;
        }
        return d.sqrMagnitude < 1e-10f ? Vector3.right : d.normalized;
    }

    static Vector3 WorldOrbitalRadialDirection(Transform orbital, AtomFunction atom)
    {
        return WorldRadialFromLocalRotation(atom, orbital.localRotation);
    }

    /// <summary>Radial direction of this lone lobe at pointer-down (drag only moves position; rotation matches original for dragged).</summary>
    static Vector3 LoneOrbitalRadialWorldAtDragStart(AtomFunction atom, ElectronOrbitalFunction orb, ElectronOrbitalFunction dragged)
    {
        if (orb == dragged)
            return WorldRadialFromLocalRotation(atom, dragged.originalLocalRotation);
        return WorldOrbitalRadialDirection(orb.transform, atom);
    }

    static Vector3 WorldTargetDirectionFromTip(AtomFunction atom, Vector3 tipWorld)
    {
        Vector3 v = tipWorld - atom.transform.position;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            v.z = 0f;
        }
        if (v.sqrMagnitude < 1e-10f) return Vector3.right;
        return v.normalized;
    }

    static Quaternion SnappedRotationBetweenWorldDirections(Vector3 fromW, Vector3 toW, float stepDeg)
    {
        if (fromW.sqrMagnitude < 1e-10f || toW.sqrMagnitude < 1e-10f) return Quaternion.identity;
        fromW.Normalize();
        toW.Normalize();
        float dot = Mathf.Clamp(Vector3.Dot(fromW, toW), -1f, 1f);
        float angle = Mathf.Acos(dot) * Mathf.Rad2Deg;
        if (angle < 0.01f) return Quaternion.identity;
        Vector3 axis = Vector3.Cross(fromW, toW);
        if (axis.sqrMagnitude < 1e-10f)
        {
            axis = Mathf.Abs(fromW.z) < 0.9f ? Vector3.Cross(fromW, Vector3.forward) : Vector3.Cross(fromW, Vector3.right);
            if (axis.sqrMagnitude < 1e-10f) return Quaternion.identity;
        }
        axis.Normalize();
        float snapped = Mathf.Round(angle / stepDeg) * stepDeg;
        if (snapped < 0.01f) return Quaternion.identity;
        return Quaternion.AngleAxis(snapped, axis);
    }

    static void ApplyRigidWorldRotationToAllLoneOrbitals(AtomFunction atom, ElectronOrbitalFunction dragged, Vector3 tipWorld)
    {
        Vector3 dDragW = WorldRadialFromLocalRotation(atom, dragged.originalLocalRotation);
        Vector3 dTargetW = WorldTargetDirectionFromTip(atom, tipWorld);
        Quaternion rWorld = SnappedRotationBetweenWorldDirections(dDragW, dTargetW, RotateStepDeg);

        foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != atom.transform) continue;
            if (orb.Bond != null) continue;
            Vector3 dW = LoneOrbitalRadialWorldAtDragStart(atom, orb, dragged);
            Vector3 dNewW = (rWorld * dW).normalized;
            Vector3 localDir = atom.transform.InverseTransformDirection(dNewW);
            if (localDir.sqrMagnitude < 1e-10f) continue;
            localDir.Normalize();
            var (pos, rot) = GetCanonicalSlotPositionFromLocalDirection(localDir, atom.BondRadius);
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
        }
    }

    static bool ApplyLoneOrbitalSpinAroundSigmaAxis(AtomFunction atom, ElectronOrbitalFunction dragged, Vector3 tipWorld)
    {
        if (!atom.TryGetSingleSigmaNeighbor(out var neighbor) || neighbor == null) return false;

        Vector3 sigmaW = neighbor.transform.position - atom.transform.position;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            sigmaW.z = 0f;
        if (sigmaW.sqrMagnitude < 1e-10f) return false;
        sigmaW.Normalize();

        Vector3 dDragW = WorldRadialFromLocalRotation(atom, dragged.originalLocalRotation);
        Vector3 dTargetW = WorldTargetDirectionFromTip(atom, tipWorld);

        Vector3 u0 = dDragW - sigmaW * Vector3.Dot(dDragW, sigmaW);
        Vector3 u1 = dTargetW - sigmaW * Vector3.Dot(dTargetW, sigmaW);
        if (u0.sqrMagnitude < 1e-8f || u1.sqrMagnitude < 1e-8f)
            return false;
        u0.Normalize();
        u1.Normalize();

        float delta = Vector3.SignedAngle(u0, u1, sigmaW);
        float snapped = Mathf.Round(delta / RotateStepDeg) * RotateStepDeg;
        Quaternion rWorld = Mathf.Abs(snapped) < 0.01f ? Quaternion.identity : Quaternion.AngleAxis(snapped, sigmaW);

        foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != atom.transform) continue;
            if (orb.Bond != null) continue;
            Vector3 dW = LoneOrbitalRadialWorldAtDragStart(atom, orb, dragged);
            Vector3 dNewW = (rWorld * dW).normalized;
            Vector3 localDir = atom.transform.InverseTransformDirection(dNewW);
            if (localDir.sqrMagnitude < 1e-10f) continue;
            localDir.Normalize();
            var (pos, rot) = GetCanonicalSlotPositionFromLocalDirection(localDir, atom.BondRadius);
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
        }
        return true;
    }

    ElectronOrbitalFunction TryFindSwapTarget(AtomFunction atom, Vector3 tip)
    {
        if (atom == null || bond != null) return null;
        var cam = Camera.main;
        var orbitals = atom.GetComponentsInChildren<ElectronOrbitalFunction>();
        ElectronOrbitalFunction best = null;
        float bestD = float.MaxValue;
        foreach (var orb in orbitals)
        {
            if (orb == this || orb.Bond != null) continue;
            if ((ElectronCount == 0) != (orb.ElectronCount == 0)) continue;
            bool hit3d = orb.ContainsPoint(tip);
            bool hitView = cam != null && OrbitalViewOverlaps(cam, this, orb);
            if (!hit3d && !hitView) continue;
            if (cam == null)
                return orb;

            Vector3 st = cam.WorldToScreenPoint(tip);
            Vector3 so = cam.WorldToScreenPoint(orb.transform.position);
            float d = st.z >= cam.nearClipPlane && so.z >= cam.nearClipPlane
                ? ((Vector2)st - (Vector2)so).sqrMagnitude
                : (hit3d ? 0f : float.MaxValue);
            if (d < bestD)
            {
                bestD = d;
                best = orb;
            }
        }

        return best;
    }

    void SwapPositionsWith(ElectronOrbitalFunction other)
    {
        transform.localPosition = other.transform.localPosition;
        transform.localRotation = other.transform.localRotation;
        other.transform.localPosition = originalLocalPosition;
        other.transform.localRotation = originalLocalRotation;
    }

    void TryBreakBond(Vector3 tip)
    {
        if (bond == null) return;
        var a = bond.AtomA;
        var b = bond.AtomB;
        if (a == null || b == null) return;
        float rA = a.BondRadius * 1.2f;
        float rB = b.BondRadius * 1.2f;
        float da = Vector3.Distance(tip, a.transform.position);
        float db = Vector3.Distance(tip, b.transform.position);
        var cam = Camera.main;
        bool hit3dA = da <= rA;
        bool hit3dB = db <= rB;
        bool hitViewA = cam != null && OrbitalViewOverlapsAtom(cam, this, a);
        bool hitViewB = cam != null && OrbitalViewOverlapsAtom(cam, this, b);
        bool hitA = hit3dA || hitViewA;
        bool hitB = hit3dB || hitViewB;

        AtomFunction returnTo = null;
        if (hitA && hitB)
        {
            if (cam != null)
            {
                Vector3 st = cam.WorldToScreenPoint(tip);
                Vector3 sa = cam.WorldToScreenPoint(a.transform.position);
                Vector3 sb = cam.WorldToScreenPoint(b.transform.position);
                if (st.z >= cam.nearClipPlane && sa.z >= cam.nearClipPlane && sb.z >= cam.nearClipPlane)
                {
                    float scoreA = ((Vector2)st - (Vector2)sa).sqrMagnitude;
                    float scoreB = ((Vector2)st - (Vector2)sb).sqrMagnitude;
                    returnTo = scoreA <= scoreB ? a : b;
                }
                else
                    returnTo = da <= db ? a : b;
            }
            else
                returnTo = da <= db ? a : b;
        }
        else if (hitA)
            returnTo = a;
        else if (hitB)
            returnTo = b;

        if (returnTo != null)
        {
            var other = returnTo == a ? b : a;
            float enDrag = AtomFunction.GetElectronegativity(returnTo.AtomicNumber);
            float enOther = AtomFunction.GetElectronegativity(other.AtomicNumber);
            if (enDrag < enOther)
            {
                returnTo.FlashChargeLabelInvalidDragFade();
                transform.localPosition = originalLocalPosition;
                transform.localScale = originalLocalScale;
                transform.localRotation = originalLocalRotation;
                bond.ReturnToLineView();
                return;
            }
            bond.BreakBond(returnTo, userDragBondCylinderBreak: true);
        }
        else
        {
            transform.localPosition = originalLocalPosition;
            transform.localScale = originalLocalScale;
            transform.localRotation = originalLocalRotation;
            bond.ReturnToLineView();
        }
    }

    /// <summary>Snaps this orbital back to its original local position/rotation/scale (before any drag).</summary>
    public void SnapToOriginal()
    {
        transform.localPosition = originalLocalPosition;
        transform.localRotation = originalLocalRotation;
        transform.localScale = originalLocalScale;
    }

    [Tooltip("Step 1: Align molecule. Duration in seconds.")]
    [SerializeField] float bondAnimStep1Duration = 1.0f;
    [Tooltip("Step 2: Rearrange + Redistribute. Duration in seconds.")]
    [SerializeField] float bondAnimStep2Duration = 1.0f;
    [Tooltip("Step 3: Orbital-to-line transition. Duration in seconds.")]
    [SerializeField] float bondAnimStep3Duration = 1.0f;

    /// <summary>Sigma bond: starts animated formation. Returns true if animation started.</summary>
    bool FormCovalentBondSigmaStart(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3 dropPosition, bool alreadyFlipped = false)
    {
        bool sourceAlreadyAligned = IsSourceOrbitalAlreadyAlignedWithTarget(sourceAtom, targetOrbital);
        bool cannotRearrangeSource = !sourceAlreadyAligned && (sourceAtom.GetPiBondCount() > 0 || IsSourceFlippedSideFilled(sourceAtom, targetAtom, targetOrbital));
        if (sourceAtom.CovalentBonds.Count > 0 && cannotRearrangeSource)
        {
            if (alreadyFlipped)
                return false;
            return targetOrbital.FormCovalentBondSigmaStartAsSource(targetAtom, sourceAtom, this, dropPosition);
        }
        sourceAtom.StartCoroutine(FormCovalentBondSigmaCoroutine(sourceAtom, targetAtom, targetOrbital, dropPosition, userDraggedOrbital: this));
        return true;
    }

    /// <summary>Simulates "user dragged target orbital to source" by calling the same coroutine with swapped args. No flip logic needed.</summary>
    bool FormCovalentBondSigmaStartAsSource(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3 dropPosition)
    {
        // Swap: targetOrbital is the one the user actually dragged - snap it back before bonding
        targetOrbital.SnapToOriginal();
        // Coroutine runs on this orbital (drop-site slot). It was not PointerDown'd for this gesture, so originalLocal*
        // may still be prefab defaults — Step 1 would snap it to the atom center then slide with the molecule.
        originalLocalPosition = transform.localPosition;
        originalLocalScale = transform.localScale;
        originalLocalRotation = transform.localRotation;
        sourceAtom.StartCoroutine(FormCovalentBondSigmaCoroutine(sourceAtom, targetAtom, targetOrbital, dropPosition, userDraggedOrbital: targetOrbital));
        return true;
    }

    IEnumerator FormCovalentBondSigmaCoroutine(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3 dropPosition, ElectronOrbitalFunction userDraggedOrbital)
    {
        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock) a.SetInteractionBlocked(true);

        try
        {
        ElectronOrbitalFunction sourceOrbital = this;
        // Partner = lone pair on the other atom (not the one the user dragged). Coroutine args swap in AsSource, so infer explicitly.
        ElectronOrbitalFunction partnerOrbital = ReferenceEquals(userDraggedOrbital, this) ? targetOrbital : this;
        int piBeforeSource = sourceAtom.GetPiBondCount();
        int piBeforeTarget = targetAtom.GetPiBondCount();
        bool isFlip = sourceAtom.CovalentBonds.Count > 0 && !IsSourceOrbitalAlreadyAlignedWithTarget(sourceAtom, partnerOrbital, userDraggedOrbital) && (sourceAtom.GetPiBondCount() > 0 || IsSourceFlippedSideFilled(sourceAtom, targetAtom, partnerOrbital));
        // isFlip geometry must use the dragged orbital's home slot on its parent atom — not sourceOrbital's locals when AsSource swapped args (that was the drop site, i.e. step-2-facing side).
        Transform draggedParent = userDraggedOrbital != null ? userDraggedOrbital.transform.parent : null;
        Vector3? targetPointOverride = isFlip && draggedParent != null
            ? draggedParent.TransformPoint(userDraggedOrbital.originalLocalPosition)
            : (Vector3?)null;
        AtomFunction rearrangeSource = isFlip ? targetAtom : sourceAtom;
        ElectronOrbitalFunction rearrangeTarget = targetOrbital;
        AtomFunction alignSource = isFlip ? targetAtom : sourceAtom;
        var referenceAtom = isFlip ? sourceAtom : targetAtom;
        var referenceOrbitalTip = targetPointOverride ?? targetOrbital.transform.position;

        // Step 1: AlignSourceAtomNextToTarget
        sourceOrbital.transform.localPosition = originalLocalPosition;
        sourceOrbital.transform.localRotation = originalLocalRotation;
        sourceOrbital.transform.localScale = originalLocalScale;
        var alignTargets = GetAlignTargetPositions(alignSource, referenceAtom, referenceOrbitalTip);
        const float alignPosThreshold = 0.05f;
        bool atomsAlreadyAligned = alignTargets.TrueForAll(t => Vector3.Distance(t.atom.transform.position, t.targetPos) < alignPosThreshold);
        if (!atomsAlreadyAligned)
        {
            var alignStarts = new List<(AtomFunction atom, Vector3 startPos)>();
            foreach (var (atom, targetPos) in alignTargets)
                alignStarts.Add((atom, atom.transform.position));
            for (float t = 0; t < bondAnimStep1Duration; t += Time.deltaTime)
            {
                float s = Mathf.Clamp01(t / bondAnimStep1Duration);
                s = s * s * (3f - 2f * s); // smoothstep
                for (int i = 0; i < alignTargets.Count; i++)
                {
                    var (atom, targetPos) = alignTargets[i];
                    var (_, startPos) = alignStarts[i];
                    atom.transform.position = Vector3.Lerp(startPos, targetPos, s);
                }
                yield return null;
            }
        }
        foreach (var (atom, targetPos) in alignTargets)
            atom.transform.position = targetPos;

        var rearrangeTargetInfo = GetRearrangeTarget(rearrangeSource, rearrangeTarget, targetPointOverride);

        int sigmaBeforeSource = sourceAtom.GetDistinctSigmaNeighborCount();
        int sigmaBeforeTarget = targetAtom.GetDistinctSigmaNeighborCount();

        // Create bond (instant) - source orbital (the one we dragged) stays visible through steps 2 and 3
        sourceOrbital.transform.localPosition = originalLocalPosition;
        sourceOrbital.transform.localRotation = originalLocalRotation;
        var bondOrbitalStartWorldPos = sourceOrbital.transform.position;
        var bondOrbitalStartWorldRot = sourceOrbital.transform.rotation;

        int merged = sourceOrbital.ElectronCount + targetOrbital.ElectronCount;
        sourceAtom.UnbondOrbital(sourceOrbital);
        targetAtom.UnbondOrbital(targetOrbital);
        var bond = CovalentBond.Create(isFlip ? targetAtom : sourceAtom, isFlip ? sourceAtom : targetAtom, sourceOrbital, sourceAtom, animateOrbitalToBond: true);
        if (isFlip) targetAtom.transform.SetParent(null);
        targetOrbital.transform.SetParent(bond.transform, worldPositionStays: true);
        bond.SetOrbitalBeingFaded(targetOrbital);
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        var bondOrbitalEnd = bond.GetOrbitalTargetWorldState();
        float sigmaDiff = ComputeSigmaBondAngleDiff(sourceAtom, targetAtom, bondOrbitalEnd.worldPos, bondOrbitalEnd.worldRot);
        // Full 3D: return in [0,180]. Planar path: signed angle; threshold is |diff|>90.
        bool sigmaNeedsFlip = OrbitalAngleUtility.UseFull3DOrbitalGeometry
            ? (sigmaDiff > 90f && sigmaDiff < 179.5f) // avoid ambiguous ~180 (and bad axis if bondPos off-line)
            : (Mathf.Abs(sigmaDiff) > 90f);
        if (sigmaNeedsFlip)
        {
            if (DebugLogSigmaFormationHeavyOrbRotationWhy)
            {
                Debug.Log(
                    "[σ-form-sigmaFlip] sigmaNeedsFlip=true src=" + sourceAtom.name +
                    "(Z=" + sourceAtom.AtomicNumber + ") tgt=" + targetAtom.name +
                    "(Z=" + targetAtom.AtomicNumber + ") sigmaDiff=" + sigmaDiff.ToString("F2") +
                    " useFull3D=" + OrbitalAngleUtility.UseFull3DOrbitalGeometry);
            }
            bond.orbitalRotationFlipped = true;
            bondOrbitalEnd = bond.GetOrbitalTargetWorldState(); // Re-fetch with flip applied
        }
        // Step 2: Rearrange + Redistribute + animate bonding orbital to bond position (skip if no actual movement)
        sourceAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        targetAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        // Tetrahedral σ-relax: 3→4 σ with no occupied lone (CH₄-style), or 2→3 σ with AX₃E (one occupied non-bond, e.g. trigonal radical → sp³).
        var sigmaRelaxById = new Dictionary<int, (AtomFunction atom, Vector3 startWorld, Vector3 endWorld)>();
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            void CollectSigmaRelax(AtomFunction center, AtomFunction partner, int sigmaBefore)
            {
                if (center == null || partner == null || sigmaBefore < 0) return;
                if (center.TryGetTetrahedralFourSigmaNeighborRelaxMovesForBondFormation(partner, sigmaBefore, out var lst4) && lst4 != null)
                {
                    foreach (var entry in lst4)
                        sigmaRelaxById[entry.atom.GetInstanceID()] = entry;
                }
                else if (center.TryGetTetrahedralThreeSigmaAx3ERelaxMovesForBondFormation(partner, sigmaBefore, out var lst3) && lst3 != null)
                {
                    foreach (var entry in lst3)
                        sigmaRelaxById[entry.atom.GetInstanceID()] = entry;
                }
            }
            CollectSigmaRelax(sourceAtom, targetAtom, sigmaBeforeSource);
            CollectSigmaRelax(targetAtom, sourceAtom, sigmaBeforeTarget);
        }
        var sigmaRelaxList = new List<(AtomFunction atom, Vector3 startWorld, Vector3 endWorld)>(sigmaRelaxById.Values);
        foreach (var m in sigmaRelaxList)
            m.atom.transform.position = m.endWorld;
        var redistA = sourceAtom.GetRedistributeTargets(piBeforeSource, targetAtom, sigmaNeighborCountBefore: sigmaBeforeSource);
        var redistB = targetAtom.GetRedistributeTargets(piBeforeTarget, sourceAtom, sigmaNeighborCountBefore: sigmaBeforeTarget);

        AtomFunction rootForStagger = referenceAtom;
        AtomFunction childForStagger = rootForStagger == sourceAtom ? targetAtom : sourceAtom;
        float newmanPsi = 0f;
        bool hasNewmanStagger = OrbitalAngleUtility.UseFull3DOrbitalGeometry
            && childForStagger != null && rootForStagger != null
            && childForStagger.AtomicNumber > 1
            && childForStagger.GetPiBondCount() == 0
            && childForStagger.TryComputeNewmanStaggerPsi(rootForStagger, true, out newmanPsi);

        // Precalc Newman-staggered ends so step 2 lerps once to final staggered geometry (no per-frame twist, no post-loop snap).
        if (hasNewmanStagger)
        {
            Vector3 cChild = childForStagger.transform.position;
            Vector3 ax = rootForStagger.transform.position - cChild;
            if (ax.sqrMagnitude <= 1e-10f)
                hasNewmanStagger = false;
            else
            {
                ax.Normalize();
                Quaternion rPsi = Quaternion.AngleAxis(newmanPsi, ax);
                for (int si = 0; si < sigmaRelaxList.Count; si++)
                {
                    var (atom, st, en) = sigmaRelaxList[si];
                    if (atom != null && atom.AtomicNumber == 1
                        && childForStagger.GetDistinctSigmaNeighborAtoms().Contains(atom) && atom != rootForStagger)
                        en = cChild + rPsi * (en - cChild);
                    sigmaRelaxList[si] = (atom, st, en);
                }
                foreach (var n in childForStagger.GetDistinctSigmaNeighborAtoms())
                {
                    if (n == null || n == rootForStagger || n.AtomicNumber != 1) continue;
                    if (sigmaRelaxById.ContainsKey(n.GetInstanceID())) continue;
                    Vector3 stH = n.transform.position;
                    sigmaRelaxList.Add((n, stH, cChild + rPsi * (stH - cChild)));
                }
                if (childForStagger == sourceAtom)
                    AtomFunction.TwistRedistributeTargetsForNewmanStaggerEnd(childForStagger, rootForStagger, newmanPsi, redistA);
                else
                    AtomFunction.TwistRedistributeTargetsForNewmanStaggerEnd(childForStagger, rootForStagger, newmanPsi, redistB);
            }
        }

        foreach (var m in sigmaRelaxList)
            m.atom.transform.position = m.startWorld;

        // σ-relax preview uses endWorld for GetRedistributeTargets, but step 2 starts with nuclei at startWorld.
        // Occupied lone targets then demand ~60° lerp "up front" while H opens to tetrahedral — mostly spurious motion on C
        // (H motion should carry the framework). Keep end-geometry targets for one Apply after relax; during step 2, hold occupied lobes.
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redistAEndAfterSigmaRelax = null;
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redistBEndAfterSigmaRelax = null;
        if (sigmaRelaxList.Count > 0 && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            redistAEndAfterSigmaRelax = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>(redistA);
            redistBEndAfterSigmaRelax = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>(redistB);
            CovalentBond.NeutralizeOccupiedRedistTargetsToCurrentLocals(sourceAtom, redistA);
            CovalentBond.NeutralizeOccupiedRedistTargetsToCurrentLocals(targetAtom, redistB);
        }

        const float alignThreshold = 0.05f;
        const float posThreshold = 0.01f;
        const float rotThreshold = 1f;

        bool hasRearrangeMovement = false;
        if (rearrangeTargetInfo.HasValue)
        {
            var (orbToMove, targetPos, targetRot) = rearrangeTargetInfo.Value;
            if (orbToMove != null && (Vector3.Distance(orbToMove.transform.localPosition, targetPos) > posThreshold || Quaternion.Angle(orbToMove.transform.localRotation, targetRot) > rotThreshold))
                hasRearrangeMovement = true;
        }
        bool needsRearrange = hasRearrangeMovement;

        bool hasRedistributeMovement = false;
        foreach (var entry in redistA)
        {
            if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform
                && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > posThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > rotThreshold))
            { hasRedistributeMovement = true; break; }
        }
        if (!hasRedistributeMovement)
            foreach (var entry in redistB)
            {
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform
                    && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > posThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > rotThreshold))
                { hasRedistributeMovement = true; break; }
            }
        bool needsRedistribute = hasRedistributeMovement;

        // Any tet σ-relax move from TryCompute (≥1e-4) should run step 2 — do not compare to posThreshold or tiny moves skip animation while targets assumed preview geometry.
        bool hasSigmaRelaxMovement = sigmaRelaxList.Count > 0;

        bool tipsAlongBondIgnoreDirectedFlip = OrbitalAngleUtility.UseFull3DOrbitalGeometry
            && BondingSigmaOrbitalWorldTipsMatchAlongInternuclearAxis(
                sourceAtom, targetAtom, bondOrbitalStartWorldRot, bondOrbitalEnd.worldRot,
                SigmaBondFormationTipAxisMaxSepDeg, rotThreshold);

        bool orbitalAlreadyAtBond = Vector3.Distance(bondOrbitalStartWorldPos, bondOrbitalEnd.worldPos) < alignThreshold
            && (
                Quaternion.Angle(bondOrbitalStartWorldRot, bondOrbitalEnd.worldRot) < rotThreshold
                || tipsAlongBondIgnoreDirectedFlip
            );
        bool skipStep2 = !needsRearrange && !needsRedistribute && !hasSigmaRelaxMovement && orbitalAlreadyAtBond && !hasNewmanStagger;

        var redistAStarts = new List<(Vector3 pos, Quaternion rot)>();
        var redistBStarts = new List<(Vector3 pos, Quaternion rot)>();
        foreach (var entry in redistA)
            redistAStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        foreach (var entry in redistB)
            redistBStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));

        // Occupied non-bond lobes neutralized for σ-relax should not move during step 2; guard against any stray writes
        // (e.g. bond-visual passes) by snapshotting locals here and restoring after relax when no redistribute/rearrange runs.
        List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> sigmaRelaxOccupiedLocalSnap = null;
        if (sigmaRelaxList.Count > 0 && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            void AddOccupiedFromRedist(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redist, AtomFunction nucleus)
            {
                if (redist == null || nucleus == null) return;
                foreach (var e in redist)
                {
                    if (e.orb == null || e.orb.transform.parent != nucleus.transform || e.orb.ElectronCount <= 0) continue;
                    sigmaRelaxOccupiedLocalSnap ??= new List<(ElectronOrbitalFunction, Vector3, Quaternion)>(4);
                    sigmaRelaxOccupiedLocalSnap.Add((e.orb, e.orb.transform.localPosition, e.orb.transform.localRotation));
                }
            }
            AddOccupiedFromRedist(redistA, sourceAtom);
            AddOccupiedFromRedist(redistB, targetAtom);
        }

        if (DebugLogSigmaFormationHeavyOrbRotationWhy)
        {
            LogSigmaFormationStep2Precalc(
                sourceAtom,
                targetAtom,
                sourceOrbital,
                sigmaRelaxList,
                redistA,
                redistB,
                redistAStarts,
                redistBStarts,
                bondOrbitalStartWorldPos,
                bondOrbitalStartWorldRot,
                bondOrbitalEnd,
                isFlip,
                sigmaNeedsFlip,
                needsRearrange,
                needsRedistribute,
                hasSigmaRelaxMovement,
                orbitalAlreadyAtBond,
                hasNewmanStagger,
                skipStep2,
                posThreshold,
                rotThreshold,
                alignThreshold,
                rotThreshold);
            SigmaFormationHeavyRotDiagBudget = skipStep2 ? 8 : 64;
        }

        Vector3? rearrangeStartPos = null;
        Quaternion? rearrangeStartRot = null;
        if (rearrangeTargetInfo.HasValue)
        {
            var (orbToMove, _, _) = rearrangeTargetInfo.Value;
            rearrangeStartPos = orbToMove.transform.localPosition;
            rearrangeStartRot = orbToMove.transform.localRotation;
        }

        bool pureSigmaRelaxOnlyStep2 = OrbitalAngleUtility.UseFull3DOrbitalGeometry
            && hasSigmaRelaxMovement
            && !needsRedistribute
            && !needsRearrange;

        // 3D: after formation, do not apply GetRedistributeTargets poses or post-hoc σ hybrid correction unless step 2
        // actually lerped electron-domain redist / rearrange. Ideal lobe alignment belongs in RedistributeOrbitals3D /
        // RedistributeOrbitals3DOld (see AtomFunction.RedistributeOrbitals3D).
        bool skipPostFormationRedistAndHybrid3D = OrbitalAngleUtility.UseFull3DOrbitalGeometry
            && !needsRedistribute
            && !needsRearrange
            && !hasSigmaRelaxMovement;
        Quaternion? step2PoseDiagLockedRot = skipPostFormationRedistAndHybrid3D ? bondOrbitalStartWorldRot : (Quaternion?)null;

        List<CovalentBond> step2PeripheralSigmaFrozen = null;
        if (!skipStep2)
        {
            if (hasSigmaRelaxMovement && !needsRedistribute && !needsRearrange
                && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                var periphery = new HashSet<CovalentBond>();
                void AddIncidentSigmaLinesExceptForming(AtomFunction nucleus)
                {
                    if (nucleus == null) return;
                    foreach (var cb in nucleus.CovalentBonds)
                    {
                        if (cb == null || !cb.IsSigmaBondLine() || cb == bond) continue;
                        periphery.Add(cb);
                    }
                }
                AddIncidentSigmaLinesExceptForming(sourceAtom);
                AddIncidentSigmaLinesExceptForming(targetAtom);
                foreach (var (a, _, _) in sigmaRelaxList)
                    AddIncidentSigmaLinesExceptForming(a);
                if (periphery.Count > 0)
                {
                    step2PeripheralSigmaFrozen = new List<CovalentBond>(periphery.Count);
                    foreach (var cb in periphery)
                    {
                        cb.BeginSigmaFormationStep2PeripheralOrbitalWorldRotFreeze();
                        step2PeripheralSigmaFrozen.Add(cb);
                    }
                }
            }

            bool step2PoseLoggedS1 = false;

            for (float t = 0; t < bondAnimStep2Duration; t += Time.deltaTime)
            {
            float s = Mathf.Clamp01(t / bondAnimStep2Duration);
            s = s * s * (3f - 2f * s); // smoothstep for position
            float rotT = 1f - (1f - s) * (1f - s); // ease-out quad - rotation leads, expresses orbital rotation visibly

            if (rearrangeTargetInfo.HasValue)
            {
                var (orbToMove, targetPos, targetRot) = rearrangeTargetInfo.Value;
                if (orbToMove != null && rearrangeStartPos.HasValue && rearrangeStartRot.HasValue)
                {
                    orbToMove.transform.localPosition = Vector3.Lerp(rearrangeStartPos.Value, targetPos, s);
                    orbToMove.transform.localRotation = Quaternion.Slerp(rearrangeStartRot.Value, targetRot, rotT);
                }
            }
            for (int i = 0; i < redistA.Count; i++)
            {
                var entry = redistA[i];
                if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform)
                {
                    var (sp, sr) = redistAStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var entry = redistB[i];
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform)
                {
                    var (sp, sr) = redistBStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            foreach (var (atom, startW, endW) in sigmaRelaxList)
                atom.transform.position = Vector3.Lerp(startW, endW, s);
            var bondVisualAtoms = new HashSet<AtomFunction> { sourceAtom, targetAtom };
            foreach (var (a, _, _) in sigmaRelaxList)
                if (a != null) bondVisualAtoms.Add(a);
            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(bondVisualAtoms);
            // Hybrid σ tip (orbitalRedistributionWorldDelta) only updates here during step 2 — without this, bond end rot ignores lerping lone lobes.
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                // During pure σ-relax (H nuclear motion), occupied lone targets may be neutralized / not moving.
                // Refreshing hybrid alignment every frame can introduce spurious 180° representation flips on the shared σ tip.
                // Only refresh during step 2 when something electron-domain driven is actually lerping.
                if (needsRedistribute || needsRearrange)
                {
                    sourceAtom.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(targetAtom);
                    targetAtom.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(sourceAtom);
                }
            }
            var bondEndLive = bond.GetOrbitalTargetWorldState();
            sourceOrbital.transform.position = Vector3.Lerp(bondOrbitalStartWorldPos, bondEndLive.worldPos, s);
            // 3D defer post-apply (no ApplyRedistributeTargets at end): keep bonding σ world rotation fixed during step 2
            // so bond-visual passes cannot spin it; lone orbital alignment is for RedistributeOrbitals(3D).
            if (skipPostFormationRedistAndHybrid3D)
                sourceOrbital.transform.rotation = bondOrbitalStartWorldRot;
            else if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                Quaternion rotTarg = SigmaFormationBondingOrbitalTargetWorldRotPreservingRollAroundTip(
                    bondOrbitalStartWorldRot, bondEndLive.worldRot, rotThreshold);
                sourceOrbital.transform.rotation = OrbitalAngleUtility.SlerpShortest(bondOrbitalStartWorldRot, rotTarg, rotT);
            }
            else
                sourceOrbital.transform.rotation = OrbitalAngleUtility.SlerpShortest(bondOrbitalStartWorldRot, bondEndLive.worldRot, rotT);
            if (DebugLogSigmaFormationHeavyOrbRotationWhy && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                if (t <= 0f)
                    LogSigmaFormationPoseCheckpoint("step2_firstFrame", bond, sourceOrbital, sourceAtom, targetAtom, rotThreshold, s, pureSigmaRelaxOnlyStep2, step2PoseDiagLockedRot);
                if (s >= 0.999f && !step2PoseLoggedS1)
                {
                    step2PoseLoggedS1 = true;
                    LogSigmaFormationPoseCheckpoint("step2_nearEnd", bond, sourceOrbital, sourceAtom, targetAtom, rotThreshold, s, pureSigmaRelaxOnlyStep2, step2PoseDiagLockedRot);
                }
            }
            yield return null;
        }
        }

        if (rearrangeTargetInfo.HasValue)
        {
            var (orbToMove, targetPos, targetRot) = rearrangeTargetInfo.Value;
            if (orbToMove != null)
            {
                orbToMove.transform.localPosition = targetPos;
                orbToMove.transform.localRotation = targetRot;
            }
        }
        foreach (var (atom, _, endW) in sigmaRelaxList)
            atom.transform.position = endW;

        if (step2PeripheralSigmaFrozen != null && step2PeripheralSigmaFrozen.Count > 0)
        {
            foreach (var cb in step2PeripheralSigmaFrozen)
                cb.EndSigmaFormationStep2PeripheralOrbitalWorldRotFreeze();
            var refreshAtoms = new HashSet<AtomFunction> { sourceAtom, targetAtom };
            foreach (var (a, _, _) in sigmaRelaxList)
                if (a != null) refreshAtoms.Add(a);
            AtomFunction.UpdateSigmaBondVisualsForAtoms(refreshAtoms);
            if (DebugLogSigmaFormationHeavyOrbRotationWhy && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                LogSigmaFormationPoseCheckpoint("postPeriphUnfreeze", bond, sourceOrbital, sourceAtom, targetAtom, rotThreshold, -1f, pureSigmaRelaxOnlyStep2, step2PoseDiagLockedRot);
        }

        // Step 2 advanced bonding world rot via gaugeRel but often left δ at pre-step-2; canonical GetOrbitalTargetWorldState
        // then disagrees by ~180° → reparent block calls ApplySigma (preserve) + Sync — can still read as a pop. Commit δ
        // once geometry is final so wrSnap matches sourceOrbital and that correction is skipped.
        if (pureSigmaRelaxOnlyStep2 && !skipStep2 && sourceOrbital != null)
        {
            bond.CommitSigmaRedistributionDeltaFromWorldOrbitalRotation(sourceOrbital.transform.rotation);
            if (DebugLogSigmaFormationHeavyOrbRotationWhy && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                LogSigmaFormationPoseCheckpoint("postCommitDelta", bond, sourceOrbital, sourceAtom, targetAtom, rotThreshold, -1f, pureSigmaRelaxOnlyStep2);
        }

        if (sigmaRelaxOccupiedLocalSnap != null && sigmaRelaxOccupiedLocalSnap.Count > 0
            && OrbitalAngleUtility.UseFull3DOrbitalGeometry
            && hasSigmaRelaxMovement
            && !needsRedistribute
            && !needsRearrange)
        {
            foreach (var (o, lp, lr) in sigmaRelaxOccupiedLocalSnap)
            {
                if (o == null) continue;
                o.transform.localPosition = lp;
                o.transform.localRotation = lr;
            }
            if (DebugLogSigmaFormationHeavyOrbRotationWhy && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                LogSigmaFormationPoseCheckpoint("postOccLocalRestore", bond, sourceOrbital, sourceAtom, targetAtom, rotThreshold, -1f, pureSigmaRelaxOnlyStep2);
        }

        if (redistAEndAfterSigmaRelax != null && redistBEndAfterSigmaRelax != null)
        {
            // If the relaxed-geometry snapshot is effectively the same as the current occupied lobe poses,
            // do not apply it. This prevents late spurious “tet vertex relabel” rotations on carbon when the chemistry is unchanged.
            bool applyEndA = false;
            float maxEndADPos = 0f, maxEndADRot = 0f;
            float maxEndADPosOcc = 0f, maxEndADRotOcc = 0f;
            foreach (var entry in redistAEndAfterSigmaRelax)
            {
                if (entry.orb == null || entry.orb.transform.parent != sourceAtom.transform) continue;
                float dPos = Vector3.Distance(entry.orb.transform.localPosition, entry.pos);
                float dRot = Quaternion.Angle(entry.orb.transform.localRotation, entry.rot);
                // Occupied lobes use NeutralizeOccupiedRedistTargetsToCurrentLocals during σ-relax step 2; end snapshot
                // was computed with nuclei at relaxed positions and can disagree by tet gauge only — must not force applyEnd.
                if (entry.orb.ElectronCount > 0)
                {
                    maxEndADPosOcc = Mathf.Max(maxEndADPosOcc, dPos);
                    maxEndADRotOcc = Mathf.Max(maxEndADRotOcc, dRot);
                    continue;
                }
                maxEndADPos = Mathf.Max(maxEndADPos, dPos);
                maxEndADRot = Mathf.Max(maxEndADRot, dRot);
                if (dPos > posThreshold || dRot > rotThreshold) { applyEndA = true; break; }
            }

            bool applyEndB = false;
            float maxEndBDPos = 0f, maxEndBDRot = 0f;
            float maxEndBDPosOcc = 0f, maxEndBDRotOcc = 0f;
            foreach (var entry in redistBEndAfterSigmaRelax)
            {
                if (entry.orb == null || entry.orb.transform.parent != targetAtom.transform) continue;
                float dPos = Vector3.Distance(entry.orb.transform.localPosition, entry.pos);
                float dRot = Quaternion.Angle(entry.orb.transform.localRotation, entry.rot);
                if (entry.orb.ElectronCount > 0)
                {
                    maxEndBDPosOcc = Mathf.Max(maxEndBDPosOcc, dPos);
                    maxEndBDRotOcc = Mathf.Max(maxEndBDRotOcc, dRot);
                    continue;
                }
                maxEndBDPos = Mathf.Max(maxEndBDPos, dPos);
                maxEndBDRot = Mathf.Max(maxEndBDRot, dRot);
                if (dPos > posThreshold || dRot > rotThreshold) { applyEndB = true; break; }
            }

            if (DebugLogSigmaFormationHeavyOrbRotationWhy)
            {
                if (!needsRedistribute && !needsRearrange && hasSigmaRelaxMovement)
                {
                    SigmaFormationRotDiagLine("endApply",
                        "SigmaRelax src=" + sourceAtom.name + "(Z=" + sourceAtom.AtomicNumber + ")" +
                        " tgt=" + targetAtom.name + "(Z=" + targetAtom.AtomicNumber + ")" +
                        " applyEndA=" + applyEndA + " applyEndB=" + applyEndB +
                        " skipPostFormationApply3D=" + skipPostFormationRedistAndHybrid3D);
                    SigmaFormationRotDiagLine("endApply",
                        "SigmaRelax empty-only maxEndADPos=" + maxEndADPos.ToString("F4") +
                        " maxEndADRotDeg=" + maxEndADRot.ToString("F2") +
                        " maxEndBDPos=" + maxEndBDPos.ToString("F4") +
                        " maxEndBDRotDeg=" + maxEndBDRot.ToString("F2"));
                    SigmaFormationRotDiagLine("endApply",
                        "SigmaRelax occupied diag (not used for applyEnd when occupied) maxEndARotOccDeg=" +
                        maxEndADRotOcc.ToString("F2") + " maxEndBRotOccDeg=" + maxEndBDRotOcc.ToString("F2"));
                }
            }

            if (!skipPostFormationRedistAndHybrid3D)
            {
                if (applyEndA) sourceAtom.ApplyRedistributeTargets(redistAEndAfterSigmaRelax);
                else sourceAtom.ApplyRedistributeTargets(redistA);

                if (applyEndB) targetAtom.ApplyRedistributeTargets(redistBEndAfterSigmaRelax);
                else targetAtom.ApplyRedistributeTargets(redistB);
            }
            else if (DebugLogSigmaFormationHeavyOrbRotationWhy && !needsRedistribute && !needsRearrange)
                SigmaFormationRotDiagLine("endApply", "skipped ApplyRedistributeTargets (defer to RedistributeOrbitals3D)");
        }
        else
        {
            if (!skipPostFormationRedistAndHybrid3D)
            {
                sourceAtom.ApplyRedistributeTargets(redistA);
                targetAtom.ApplyRedistributeTargets(redistB);
            }
            else if (DebugLogSigmaFormationHeavyOrbRotationWhy)
                SigmaFormationRotDiagLine("endApply", "skipped ApplyRedistributeTargets (defer to RedistributeOrbitals3D)");
        }

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && (needsRedistribute || needsRearrange))
        {
            // If only σ-nuclear relaxation ran (no occupied/electron redistribute motion), refreshing hybrid
            // alignment at the end can still choose an alternate representation and spin the shared σ visually.
            // Refresh again only when electron-domain targets were actually lerped / rearranged.
            sourceAtom.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(targetAtom);
            targetAtom.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(sourceAtom);
        }

        LogSigmaFormationBondingOrb("afterApplyRedistributeTargets", "bondingOrb(stillOnSource)", sourceOrbital);
        LogSigmaFormationNonbondTipsOnAtom("afterApplyRedistributeTargets", sourceAtom, targetAtom);
        LogSigmaFormationNonbondTipsOnAtom("afterApplyRedistributeTargets", targetAtom, sourceAtom);

        if (DebugLogSigmaFormationHeavyOrbRotationWhy && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            LogSigmaFormationPoseCheckpoint("preReparent", bond, sourceOrbital, sourceAtom, targetAtom, rotThreshold, -1f, pureSigmaRelaxOnlyStep2);

        sourceOrbital.transform.SetParent(bond.transform, worldPositionStays: true); // Reparent now (orbital was left on source atom so it could animate)
        bond.UpdateBondTransformToCurrentAtoms(); // Bond may be stale (atoms moved in step 1)

        if (DebugLogSigmaFormationHeavyOrbRotationWhy && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            LogSigmaFormationPoseCheckpoint("postReparent", bond, sourceOrbital, sourceAtom, targetAtom, rotThreshold, -1f, pureSigmaRelaxOnlyStep2);

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && !skipPostFormationRedistAndHybrid3D)
        {
            var (_, wrSnap) = bond.GetOrbitalTargetWorldState();
            if (BondingSigmaOrbitalWorldTipsMatchAlongInternuclearAxis(
                    sourceAtom, targetAtom, sourceOrbital.transform.rotation, wrSnap,
                    SigmaBondFormationTipAxisMaxSepDeg, rotThreshold)
                && Quaternion.Angle(sourceOrbital.transform.rotation, wrSnap) > rotThreshold)
            {
                bond.ApplySigmaOrbitalTipFromRedistribution(
                    sourceAtom, sourceOrbital.transform.rotation * Vector3.right);
                bond.UpdateBondTransformToCurrentAtoms();
            }
        }
        else if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && skipPostFormationRedistAndHybrid3D && DebugLogSigmaFormationHeavyOrbRotationWhy)
            SigmaFormationRotDiagLine("postBond", "skipped ApplySigmaOrbitalTipFromRedistribution (skipPostFormationRedistAndHybrid3D=true)");

        if (DebugLogSigmaFormationHeavyOrbRotationWhy && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            LogSigmaFormationPoseCheckpoint("preSnapOrbital", bond, sourceOrbital, sourceAtom, targetAtom, rotThreshold, -1f, pureSigmaRelaxOnlyStep2);

        bond.SnapOrbitalToBondPosition(sourceOrbital.transform.rotation); // Preserve roll vs cylinder; sync redistribution δ

        if (DebugLogSigmaFormationHeavyOrbRotationWhy && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            LogSigmaFormationPoseCheckpoint("postSnapOrbital", bond, sourceOrbital, sourceAtom, targetAtom, rotThreshold, -1f, pureSigmaRelaxOnlyStep2);

        bond.animatingOrbitalToBondPosition = false;

        LogSigmaFormationBondingOrb("afterBondSnapOrbital", "bondingOrb(onBond)", sourceOrbital);
        LogSigmaFormationNonbondTipsOnAtom("afterBondSnapOrbital", sourceAtom, targetAtom);
        LogSigmaFormationNonbondTipsOnAtom("afterBondSnapOrbital", targetAtom, sourceAtom);

        // Step 3: Orbital-to-line transformation (bonding orbital shrinking, target orbital fading, line growing simultaneously)
        if (bond != null)
        {
            yield return bond.AnimateOrbitalToLine(bondAnimStep3Duration, targetOrbital);
            sourceOrbital.ElectronCount = merged; // Show merged electrons only after step 3
        }

        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        if (hasNewmanStagger)
            childForStagger.RefreshSigmaBondTransformsAndChargesAroundAtom();

        LogSigmaFormationBondingOrb("afterStep3_newmanRefresh_charge", "bondingOrb", sourceOrbital);
        LogSigmaFormationNonbondTipsOnAtom("afterStep3_newmanRefresh_charge", sourceAtom, targetAtom);
        LogSigmaFormationNonbondTipsOnAtom("afterStep3_newmanRefresh_charge", targetAtom, sourceAtom);
        }
        finally
        {
            foreach (var a in atomsToBlock) a.SetInteractionBlocked(false);
            UnityEngine.Object.FindFirstObjectByType<EditModeManager>()?.RefreshSelectedMoleculeAfterBondChange();
        }
    }

    (ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)? GetRearrangeTarget(AtomFunction sourceAtom, ElectronOrbitalFunction targetOrbital, Vector3? targetPointOverride)
    {
        float oppositeAngleWorld;
        if (targetPointOverride.HasValue)
        {
            var dir = (targetPointOverride.Value - sourceAtom.transform.position).normalized;
            oppositeAngleWorld = OrbitalAngleUtility.DirectionToAngleWorld(dir);
            foreach (var orb in sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb != this && orb.Bond == null && IsOrbitalAlignedWithTargetPoint(orb, targetPointOverride.Value))
                    return null;
            }
        }
        else
        {
            float targetAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(targetOrbital.transform);
            oppositeAngleWorld = OrbitalAngleUtility.NormalizeAngle(targetAngleWorld + 180f);
            float draggedAngleWorld = OrbitalAngleUtility.LocalRotationToAngleWorld(sourceAtom.transform, originalLocalRotation);
            if (Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(draggedAngleWorld - oppositeAngleWorld)) < 45f)
                return null;
        }
        var sourceOrbitals = sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>();
        ElectronOrbitalFunction orbToMove = null;
        float bestDelta = 360f;
        foreach (var orb in sourceOrbitals)
        {
            if (orb == this || orb.Bond != null) continue;
            float orbAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
            float delta = Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(orbAngleWorld - oppositeAngleWorld));
            if (delta < bestDelta) { bestDelta = delta; orbToMove = orb; }
        }
        if (orbToMove == null) return null;
        float freedSlotAngle = targetPointOverride.HasValue
            ? OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.DirectionToAngleWorld((targetPointOverride.Value - sourceAtom.transform.position).normalized) + 180f)
            : NormalizeAngle(originalLocalRotation.eulerAngles.z);
        var slotPos = GetCanonicalSlotPosition(freedSlotAngle, sourceAtom.BondRadius);
        return (orbToMove, slotPos.position, slotPos.rotation);
    }

    List<(AtomFunction atom, Vector3 targetPos)> GetAlignTargetPositions(AtomFunction moleculeRoot, AtomFunction referenceAtom, Vector3 referenceOrbitalTip)
    {
        var refPos = referenceAtom.transform.position;
        var toOrbital = referenceOrbitalTip - refPos;
        var dist = toOrbital.magnitude;
        if (dist < 0.01f) return new List<(AtomFunction, Vector3)>();
        var dir = toOrbital / dist;
        var newMoleculeRootPos = referenceOrbitalTip + dir * dist;
        var delta = newMoleculeRootPos - moleculeRoot.transform.position;
        var result = new List<(AtomFunction, Vector3)>();
        foreach (var a in moleculeRoot.GetConnectedMolecule())
            result.Add((a, a.transform.position + delta));
        return result;
    }

    bool FormCovalentBondPiStart(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        targetAtom.StartCoroutine(FormCovalentBondPiCoroutine(sourceAtom, targetAtom, targetOrbital));
        return true;
    }

    IEnumerator FormCovalentBondPiCoroutine(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock) a.SetInteractionBlocked(true);

        try
        {
        int piBeforeSource = sourceAtom.GetPiBondCount();
        int piBeforeTarget = targetAtom.GetPiBondCount();
        int sigmaBeforeSource = sourceAtom.GetDistinctSigmaNeighborCount();
        int sigmaBeforeTarget = targetAtom.GetDistinctSigmaNeighborCount();
        int mergedElectrons = electronCount + targetOrbital.ElectronCount;

        // Snap source orbital to original position (like sigma bond) before animating to bond center
        transform.localPosition = originalLocalPosition;
        transform.localRotation = originalLocalRotation;
        transform.localScale = originalLocalScale;

        sourceAtom.UnbondOrbital(this);
        targetAtom.UnbondOrbital(targetOrbital);

        var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: true);
        transform.SetParent(null); // Detach source orbital but keep visible for step 2 animation (preserves world pos from snap)
        bond.SetOrbitalBeingFaded(this); // Use merged count for charge during animation (source orbital is being faded)
        sourceAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        targetAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        yield return AnimateRedistributeOrbitals(sourceAtom, targetAtom, piBeforeSource, piBeforeTarget, sigmaBeforeSource, sigmaBeforeTarget, this, targetOrbital, bond);

        if (bond != null)
        {
            bond.animatingOrbitalToBondPosition = false;
            yield return bond.AnimateOrbitalToLine(bondAnimStep3Duration, this);
            targetOrbital.ElectronCount = mergedElectrons; // Show merged electrons only after step 3
        }
        else
        {
            Destroy(gameObject);
        }
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();
        }
        finally
        {
            foreach (var a in atomsToBlock) a.SetInteractionBlocked(false);
            UnityEngine.Object.FindFirstObjectByType<EditModeManager>()?.RefreshSelectedMoleculeAfterBondChange();
        }
    }

    IEnumerator AnimateRedistributeOrbitals(AtomFunction sourceAtom, AtomFunction targetAtom, int piBeforeSource, int piBeforeTarget,
        int sigmaBeforeSource, int sigmaBeforeTarget,
        ElectronOrbitalFunction sourceOrbital, ElectronOrbitalFunction targetOrbital, CovalentBond bond)
    {
        var redistA = sourceAtom.GetRedistributeTargets(piBeforeSource, targetAtom, sigmaNeighborCountBefore: sigmaBeforeSource);
        var redistB = targetAtom.GetRedistributeTargets(piBeforeTarget, sourceAtom, sigmaNeighborCountBefore: sigmaBeforeTarget);

        var redistAStarts = new List<(Vector3 pos, Quaternion rot)>();
        var redistBStarts = new List<(Vector3 pos, Quaternion rot)>();
        foreach (var entry in redistA)
            redistAStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        foreach (var entry in redistB)
            redistBStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));

        // σ-neighbor relax: trigonal planar (3 σ + π) or linear (2 σ + triple, no lone on center); animate in step 2 with lone / π lobes.
        var neighborTrigonalMoves = new Dictionary<AtomFunction, (Vector3 start, Vector3 end)>();
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            void AddTrigonalMoves(AtomFunction a)
            {
                if (a == null) return;
                Vector3 refL = a.GetRedistributeReferenceLocal(null, null);
                if (a.TryComputeTrigonalPlanarSigmaNeighborRelaxTargets(refL, out var t) && t != null)
                {
                    foreach (var (n, end) in t)
                        neighborTrigonalMoves[n] = (n.transform.position, end);
                }
            }
            void AddLinearMoves(AtomFunction a)
            {
                if (a == null) return;
                Vector3 refL = a.GetRedistributeReferenceLocal(null, null);
                if (a.TryComputeLinearSigmaNeighborRelaxTargets(refL, out var t) && t != null)
                {
                    foreach (var (n, end) in t)
                        neighborTrigonalMoves[n] = (n.transform.position, end);
                }
            }
            AddTrigonalMoves(sourceAtom);
            AddTrigonalMoves(targetAtom);
            AddLinearMoves(sourceAtom);
            AddLinearMoves(targetAtom);
        }

        // Pi bond: animate both orbitals to bond center. Flip source or target so electrons align in a row.
        Vector3 bondTargetPos = Vector3.zero;
        Quaternion bondTargetRot = Quaternion.identity;
        Quaternion sourceTargetRot = Quaternion.identity;
        Quaternion targetTargetRot = Quaternion.identity;
        if (bond != null)
        {
            var bt = bond.GetOrbitalTargetWorldState();
            bondTargetPos = bt.Item1;
            bondTargetRot = bt.Item2;
            (float sourceDiff, float targetDiff) = ComputePiBondAngleDiffs(sourceAtom, targetAtom, bondTargetPos, bondTargetRot, bond);
            bool flipTarget = Mathf.Abs(sourceDiff) < Mathf.Abs(targetDiff); // Source closer to bond → flip target
            if (flipTarget)
            {
                bond.orbitalRotationFlipped = true;
                bt = bond.GetOrbitalTargetWorldState(); // Re-fetch with flip
                bondTargetPos = bt.Item1;
                bondTargetRot = bt.Item2; // Target (bond orbital) gets flipped rotation
                sourceTargetRot = bondTargetRot * Quaternion.Euler(0f, 0f, 180f); // Source opposite to target
                targetTargetRot = bondTargetRot;
            }
            else
            {
                sourceTargetRot = bondTargetRot * Quaternion.Euler(0f, 0f, 180f);
                targetTargetRot = bondTargetRot;
            }
        }
        var sourceOrbStart = sourceOrbital != null ? (sourceOrbital.transform.position, sourceOrbital.transform.rotation) : (Vector3.zero, Quaternion.identity);
        var targetOrbStart = targetOrbital != null ? (targetOrbital.transform.position, targetOrbital.transform.rotation) : (Vector3.zero, Quaternion.identity);

        const float piAlignThreshold = 0.05f;
        const float piPosThreshold = 0.01f;
        const float piRotThreshold = 1f;
        bool sourceAtBond = sourceOrbital == null || bond == null || (Vector3.Distance(sourceOrbital.transform.position, bondTargetPos) < piAlignThreshold && Quaternion.Angle(sourceOrbital.transform.rotation, sourceTargetRot) < piRotThreshold);
        bool targetAtBond = targetOrbital == null || bond == null || (Vector3.Distance(targetOrbital.transform.position, bondTargetPos) < piAlignThreshold && Quaternion.Angle(targetOrbital.transform.rotation, targetTargetRot) < piRotThreshold);
        bool hasRedistMovement = false;
        foreach (var entry in redistA)
        {
            if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform
                && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > piPosThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > piRotThreshold))
            { hasRedistMovement = true; break; }
        }
        if (!hasRedistMovement)
            foreach (var entry in redistB)
            {
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform
                    && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > piPosThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > piRotThreshold))
                { hasRedistMovement = true; break; }
            }
        if (!hasRedistMovement && neighborTrigonalMoves.Count > 0)
            hasRedistMovement = true;
        bool skipPiStep2 = sourceAtBond && targetAtBond && !hasRedistMovement;

        if (!skipPiStep2)
        for (float t = 0; t < bondAnimStep2Duration; t += Time.deltaTime)
        {
            float s = Mathf.Clamp01(t / bondAnimStep2Duration);
            s = s * s * (3f - 2f * s); // smoothstep for position
            float rotT = 1f - (1f - s) * (1f - s); // ease-out quad - rotation leads, expresses orbital rotation visibly

            if (sourceOrbital != null && bond != null)
            {
                sourceOrbital.transform.position = Vector3.Lerp(sourceOrbStart.Item1, bondTargetPos, s);
                sourceOrbital.transform.rotation = Quaternion.Slerp(sourceOrbStart.Item2, sourceTargetRot, rotT);
            }
            if (targetOrbital != null && bond != null)
            {
                targetOrbital.transform.position = Vector3.Lerp(targetOrbStart.Item1, bondTargetPos, s);
                targetOrbital.transform.rotation = Quaternion.Slerp(targetOrbStart.Item2, targetTargetRot, rotT);
            }

            for (int i = 0; i < redistA.Count; i++)
            {
                var entry = redistA[i];
                if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform)
                {
                    var (sp, sr) = redistAStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var entry = redistB[i];
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform)
                {
                    var (sp, sr) = redistBStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            foreach (var kv in neighborTrigonalMoves)
                kv.Key.transform.position = Vector3.Lerp(kv.Value.start, kv.Value.end, s);
            yield return null;
        }
        foreach (var kv in neighborTrigonalMoves)
            kv.Key.transform.position = kv.Value.end;

        sourceAtom.ApplyRedistributeTargets(redistA);
        targetAtom.ApplyRedistributeTargets(redistB);

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            sourceAtom.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(targetAtom);
            targetAtom.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(sourceAtom);
        }

        // Snap both orbitals to exact position step 3 expects (prevents teleport)
        if (bond != null)
        {
            targetOrbital.transform.SetParent(bond.transform, worldPositionStays: true); // Reparent now (orbital was left on target atom so it could animate)
            bond.UpdateBondTransformToCurrentAtoms(); // Bond may be stale
            bond.SnapOrbitalToBondPosition();
            if (sourceOrbital != null)
            {
                var bt = bond.GetOrbitalTargetWorldState();
                sourceOrbital.transform.position = bt.Item1;
                sourceOrbital.transform.rotation = sourceTargetRot;
            }
        }

        // Partial lists only animate a subset of lone orbitals; the other atom can get an empty list while π count
        // still changed. Always run full RedistributeOrbitals on both endpoints when π count changed.
        TryRedistributeOrbitalsAfterBondChange(sourceAtom, targetAtom, piBeforeSource, piBeforeTarget);
    }

    static (float sourceDiff, float targetDiff) ComputePiBondAngleDiffs(AtomFunction sourceAtom, AtomFunction targetAtom, Vector3 bondPos, Quaternion bondRot, CovalentBond bond)
    {
        var toCenterFromSource = bondPos - sourceAtom.transform.position;
        if (toCenterFromSource.sqrMagnitude < 0.0001f) return (0f, 0f);
        toCenterFromSource.Normalize();
        toCenterFromSource.z = 0;
        float sourceAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenterFromSource);
        var toCenterFromTarget = bondPos - targetAtom.transform.position;
        if (toCenterFromTarget.sqrMagnitude >= 0.0001f && bond != null)
        {
            toCenterFromTarget.Normalize();
            toCenterFromTarget.z = 0;
            float targetAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenterFromTarget);
            var bondDir = bondRot * Vector3.right;
            bondDir.z = 0;
            if (bondDir.sqrMagnitude >= 0.0001f)
            {
                bondDir.Normalize();
                float bondAngle = OrbitalAngleUtility.DirectionToAngleWorld(bondDir);
                float sourceDiff = OrbitalAngleUtility.NormalizeAngle(sourceAngle - bondAngle);
                float targetDiff = OrbitalAngleUtility.NormalizeAngle(targetAngle - bondAngle);
                return (sourceDiff, targetDiff);
            }
        }
        return (0f, 0f);
    }

    static float ComputeSigmaBondAngleDiff(AtomFunction sourceAtom, AtomFunction partnerAtom, Vector3 bondPos, Quaternion bondRot)
    {
        var toCenter = bondPos - sourceAtom.transform.position;
        if (toCenter.sqrMagnitude < 0.0001f) return 0f;

        // For full 3D σ formation we should not project to XY: tetrahedral C–H geometries can have
        // meaningful Z components, and the old XY-projection can falsely trigger 180° flips.
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            // Bond visual position is offset perpendicular to the axis; use internuclear direction, not source→orbitalCenter.
            Vector3 alongPartner = toCenter.normalized;
            if (partnerAtom != null)
            {
                var toP = partnerAtom.transform.position - sourceAtom.transform.position;
                if (toP.sqrMagnitude > 1e-8f) alongPartner = toP.normalized;
            }
            var bondDir = bondRot * Vector3.right;
            if (bondDir.sqrMagnitude < 0.0001f) return 0f;
            bondDir.Normalize();
            return Vector3.Angle(alongPartner, bondDir); // [0..180]
        }

        toCenter.Normalize();
        toCenter.z = 0;
        float sourceAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenter);
        var planarBondDir = bondRot * Vector3.right;
        planarBondDir.z = 0;
        if (planarBondDir.sqrMagnitude < 0.0001f) return 0f;
        planarBondDir.Normalize();
        float bondAngle = OrbitalAngleUtility.DirectionToAngleWorld(planarBondDir);
        float diff = OrbitalAngleUtility.NormalizeAngle(sourceAngle - bondAngle);
        return diff;
    }

    static void TryRedistributeOrbitalsAfterBondChange(AtomFunction sourceAtom, AtomFunction targetAtom, int piBeforeSource, int piBeforeTarget)
    {
        if (sourceAtom != null) sourceAtom.RedistributeOrbitals();
        if (targetAtom != null) targetAtom.RedistributeOrbitals();
    }

    /// <param name="partnerOrbital">Lone pair on the partner atom (receptor direction reference).</param>
    /// <param name="draggedOrbital">Orbital the user dragged; defaults to this when null (normal sigma start).</param>
    bool IsSourceOrbitalAlreadyAlignedWithTarget(AtomFunction sourceAtom, ElectronOrbitalFunction partnerOrbital, ElectronOrbitalFunction draggedOrbital = null)
    {
        var dragged = draggedOrbital != null ? draggedOrbital : this;
        var dragParent = dragged.transform.parent;
        if (dragParent == null || partnerOrbital == null) return false;
        float targetAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(partnerOrbital.transform);
        float oppositeAngleWorld = OrbitalAngleUtility.NormalizeAngle(targetAngleWorld + 180f);
        float draggedAngleWorld = OrbitalAngleUtility.LocalRotationToAngleWorld(dragParent, dragged.originalLocalRotation);
        const float tolerance = 45f;
        return Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(draggedAngleWorld - oppositeAngleWorld)) < tolerance;
    }

    static bool IsOrbitalAlignedWithTargetPoint(ElectronOrbitalFunction orbital, Vector3 targetPoint)
    {
        var atomPos = orbital.transform.parent != null ? orbital.transform.parent.position : orbital.transform.position;
        var dirToTarget = (targetPoint - atomPos).normalized;
        float targetAngleWorld = OrbitalAngleUtility.DirectionToAngleWorld(dirToTarget);
        float orbitalAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(orbital.transform);
        const float tolerance = 45f;
        return Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(orbitalAngleWorld - targetAngleWorld)) < tolerance;
    }

    void RearrangeOrbitalToFaceTarget(AtomFunction sourceAtom, ElectronOrbitalFunction targetOrbital, Vector3? targetPointOverride = null)
    {
        float oppositeAngleWorld;
        if (targetPointOverride.HasValue)
        {
            var dirFromSourceToTarget = (targetPointOverride.Value - sourceAtom.transform.position).normalized;
            oppositeAngleWorld = OrbitalAngleUtility.DirectionToAngleWorld(dirFromSourceToTarget);
            foreach (var orb in sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb != this && orb.Bond == null && IsOrbitalAlignedWithTargetPoint(orb, targetPointOverride.Value))
                    return;
            }
        }
        else
        {
            float targetAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(targetOrbital.transform);
            oppositeAngleWorld = OrbitalAngleUtility.NormalizeAngle(targetAngleWorld + 180f);
            float draggedAngleWorld = OrbitalAngleUtility.LocalRotationToAngleWorld(sourceAtom.transform, originalLocalRotation);
            const float tolerance = 45f;
            if (Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(draggedAngleWorld - oppositeAngleWorld)) < tolerance)
                return;
        }

        var sourceOrbitals = sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>();
        ElectronOrbitalFunction orbToMove = null;
        float bestDelta = 360f;
        foreach (var orb in sourceOrbitals)
        {
            if (orb == this || orb.Bond != null) continue;
            float orbAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
            float delta = Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(orbAngleWorld - oppositeAngleWorld));
            if (delta < bestDelta)
            {
                bestDelta = delta;
                orbToMove = orb;
            }
        }
        if (orbToMove != null)
        {
            float freedSlotAngle = targetPointOverride.HasValue
                ? OrbitalAngleUtility.DirectionToAngleWorld((targetPointOverride.Value - sourceAtom.transform.position).normalized)
                : NormalizeAngle(originalLocalRotation.eulerAngles.z);
            var slotPos = GetCanonicalSlotPosition(freedSlotAngle, sourceAtom.BondRadius);
            orbToMove.transform.localPosition = slotPos.position;
            orbToMove.transform.localRotation = slotPos.rotation;
        }
    }

    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPositionFromLocalDirection(Vector3 localDir, float bondRadius) =>
        GetCanonicalSlotPositionFromLocalDirection(localDir, bondRadius, null);

    /// <param name="preferClosestLocalRotation">When set, pick 0°/90°/… rolls about orbital +X after base align (<c>AngleAxis</c> uses <c>Vector3.right</c>, not <paramref name="localDir"/>, so hybrid +X stays along <paramref name="localDir"/>).</param>
    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPositionFromLocalDirection(Vector3 localDir, float bondRadius, Quaternion? preferClosestLocalRotation)
    {
        if (localDir.sqrMagnitude < 1e-8f)
            localDir = Vector3.right;
        localDir.Normalize();
        float offset = bondRadius * 0.6f;
        var pos = localDir * offset;
        var qBase = Quaternion.FromToRotation(Vector3.right, localDir);
        if (!preferClosestLocalRotation.HasValue)
            return (pos, qBase);

        float bestAng = float.MaxValue;
        int bestK = 0;
        Quaternion bestQ = qBase;
        for (int k = 0; k < 4; k++)
        {
            var q = qBase * Quaternion.AngleAxis(k * 90f, Vector3.right);
            float a = Quaternion.Angle(preferClosestLocalRotation.Value, q);
            if (a < bestAng - 0.02f || (Mathf.Abs(a - bestAng) <= 0.02f && k < bestK))
            {
                bestAng = a;
                bestK = k;
                bestQ = q;
            }
        }
        return (pos, bestQ);
    }

    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPosition(float angleDeg, float bondRadius)
    {
        float angle = NormalizeAngle(angleDeg);
        float rad = angle * Mathf.Deg2Rad;
        var dir = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f);
        return GetCanonicalSlotPositionFromLocalDirection(dir, bondRadius);
    }

    public static float NormalizeAngle(float deg)
    {
        while (deg > 180f) deg -= 360f;
        while (deg < -180f) deg += 360f;
        return deg;
    }

    bool IsSourceFlippedSideFilled(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3? orbitalTipOverride = null)
    {
        var targetPos = targetAtom.transform.position;
        var orbitalTip = orbitalTipOverride ?? targetOrbital.transform.position;
        var targetOrbitalDir = (orbitalTip - targetPos).normalized;
        float targetOrbitalAngle = OrbitalAngleUtility.DirectionToAngleWorld(targetOrbitalDir);
        float flippedAngle = OrbitalAngleUtility.NormalizeAngle(targetOrbitalAngle + 180f);
        const float toleranceDeg = 45f;
        var sourcePos = sourceAtom.transform.position;

        foreach (var b in sourceAtom.CovalentBonds)
        {
            if (b == null) continue;
            var other = b.AtomA == sourceAtom ? b.AtomB : b.AtomA;
            if (other == null) continue;
            var dirToOther = (other.transform.position - sourcePos).normalized;
            float bondAngleDeg = OrbitalAngleUtility.DirectionToAngleWorld(dirToOther);
            float bondAngleNorm = OrbitalAngleUtility.NormalizeAngle(bondAngleDeg);
            float delta = NormalizeAngle(bondAngleNorm - flippedAngle);
            float absDelta = Mathf.Abs(delta);
            bool isFlippedSide = absDelta < toleranceDeg;

            if (isFlippedSide)
                return true;
        }
        return false;
    }

    void AlignSourceAtomNextToTarget(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3? targetOrbitalTipOverride = null)
    {
        var targetPos = targetAtom.transform.position;
        var targetOrbitalTip = targetOrbitalTipOverride ?? targetOrbital.transform.position;
        var toOrbital = targetOrbitalTip - targetPos;
        var distToOrbital = toOrbital.magnitude;
        if (distToOrbital < 0.01f) return;
        var dir = toOrbital / distToOrbital;
        var newSourcePos = targetOrbitalTip + dir * distToOrbital;
        var delta = newSourcePos - sourceAtom.transform.position;
        foreach (var a in sourceAtom.GetConnectedMolecule())
            a.transform.position += delta;
    }

    void AlignTargetAtomNextToSource(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        var targetPos = targetAtom.transform.position;
        var targetOrbitalTip = targetOrbital.transform.position;
        var toOrbital = targetOrbitalTip - targetPos;
        var distToOrbital = toOrbital.magnitude;
        if (distToOrbital < 0.01f) return;
        var dir = toOrbital / distToOrbital;
        var sourceOrbitalTip = transform.position;
        var newTargetPos = sourceOrbitalTip - dir * distToOrbital;
        var delta = newTargetPos - targetAtom.transform.position;
        foreach (var a in targetAtom.GetConnectedMolecule())
            a.transform.position += delta;
    }

    void CreateStretchVisual()
    {
        if (stretchVisual != null) return;
        var tip = transform.position;
        stretchVisual = new GameObject("StretchVisual");
        stretchVisual.transform.SetParent(transform.parent);
        stretchVisual.transform.localScale = Vector3.one;

        Color color = new Color(1, 1, 1, 0.4f);
        int sortOrder = 0;
        int sortLayer = 0;
        if (mainSpriteRenderer != null)
        {
            color = mainSpriteRenderer.color;
            sortOrder = mainSpriteRenderer.sortingOrder;
            sortLayer = mainSpriteRenderer.sortingLayerID;
        }
        else if (mainMeshRenderer != null && mainMeshRenderer.sharedMaterial != null)
            color = originalColor;

        color.a = Mathf.Clamp01(orbitalVisualAlpha);
        stretchVisualIs3D = Use3DOrbitalPresentation() && stretchSprite == null;

        if (stretchSprite != null)
        {
            var sr = stretchVisual.AddComponent<SpriteRenderer>();
            sr.sprite = stretchSprite;
            sr.color = color;
            sr.sortingOrder = sortOrder;
            sr.sortingLayerID = sortLayer;
        }
        else if (stretchVisualIs3D)
        {
            var coneGo = new GameObject("StretchCone");
            coneGo.transform.SetParent(stretchVisual.transform);
            coneGo.transform.localPosition = Vector3.zero;
            coneGo.transform.localRotation = Quaternion.identity;
            coneGo.transform.localScale = Vector3.one;
            var mfCone = coneGo.AddComponent<MeshFilter>();
            mfCone.sharedMesh = GetOrCreateUnitConeMesh();
            var mrCone = coneGo.AddComponent<MeshRenderer>();
            mrCone.sharedMaterial = CreateStretchOrbitalMaterial(color);

            var hemGo = new GameObject("StretchHemisphere");
            hemGo.transform.SetParent(stretchVisual.transform);
            hemGo.transform.localPosition = Vector3.zero;
            hemGo.transform.localRotation = Quaternion.identity;
            hemGo.transform.localScale = Vector3.one;
            var mfSph = hemGo.AddComponent<MeshFilter>();
            mfSph.sharedMesh = GetOrCreateUnitHemisphereMesh();
            var mrSph = hemGo.AddComponent<MeshRenderer>();
            mrSph.sharedMaterial = CreateStretchOrbitalMaterial(color);
        }
        else
        {
            var tri = new GameObject("Triangle");
            tri.transform.SetParent(stretchVisual.transform);
            tri.transform.localPosition = Vector3.zero;
            tri.transform.localRotation = Quaternion.identity;
            tri.transform.localScale = Vector3.one;
            var triSr = tri.AddComponent<SpriteRenderer>();
            triSr.sprite = GetOrCreateTriangleSprite();
            triSr.color = color;
            triSr.sortingOrder = sortOrder;
            triSr.sortingLayerID = sortLayer;
            var clipMat = GetOrCreateTriangleClipMaterial();
            if (clipMat != null) triSr.material = clipMat;

            var circle = new GameObject("Circle");
            circle.transform.SetParent(stretchVisual.transform);
            circle.transform.localPosition = Vector3.zero;
            circle.transform.localRotation = Quaternion.identity;
            circle.transform.localScale = Vector3.one;
            var circleSr = circle.AddComponent<SpriteRenderer>();
            circleSr.sprite = GetOrCreateCircleSprite();
            // Procedural circle texture is white; use electron black (not orbital tint) so drag preview matches real electrons.
            circleSr.color = ElectronFunction.Electron2DVisualColor;
            circleSr.sortingLayerID = sortLayer;
            circleSr.sortingOrder = sortOrder + 1;
        }
        UpdateStretchVisual(tip);
    }

    /// <summary>Cone/hemisphere anchor for stretch. Bond orbitals at the bond center use an atom so tip−anchor is non‑degenerate (sigma bonds).</summary>
    Vector3 GetStretchAnchorForTip(Vector3 tip)
    {
        if (bond != null)
        {
            var atomA = bond.AtomA;
            var atomB = bond.AtomB;
            if (atomA != null && atomB != null)
            {
                Vector3 center = bond.transform.position;
                Vector3 delta = tip - center;
                const float eps = 0.02f;
                if (delta.sqrMagnitude >= eps * eps)
                    return center;
                float dA = (tip - atomA.transform.position).sqrMagnitude;
                float dB = (tip - atomB.transform.position).sqrMagnitude;
                return dA >= dB ? atomA.transform.position : atomB.transform.position;
            }
        }
        if (transform.parent != null)
            return transform.parent.position;
        return transform.position;
    }

    float Approximate3DElectronSphereRadiusWorld()
    {
        float maxR = 0f;
        foreach (var e in GetComponentsInChildren<ElectronFunction>(true))
        {
            if (e == null) continue;
            float m = Mathf.Max(Mathf.Max(e.transform.lossyScale.x, e.transform.lossyScale.y), e.transform.lossyScale.z);
            maxR = Mathf.Max(maxR, m * 0.5f);
        }
        // Slight overestimate so the black sphere stays clearly inside the translucent cap.
        return maxR > 0.001f ? maxR * 1.08f : 0.12f;
    }

    /// <summary>Shared math for 3D stretch: seam plane (cone–hemisphere) and electron center inside the dome (not protruding through the outer shell).</summary>
    bool TryCompute3DElectronTargetWorldForTip(Vector3 tip, out Vector3 seamCenterWorld, out Vector3 electronTargetWorld)
    {
        seamCenterWorld = electronTargetWorld = default;
        if (!Use3DOrbitalPresentation() || !stretchVisualIs3D || stretchVisual == null) return false;
        var anchor = GetStretchAnchorForTip(tip);
        var delta = tip - anchor;
        float distance = delta.magnitude;
        if (distance < 0.01f && bond == null) return false;

        Transform hem = stretchVisual.transform.Find("StretchHemisphere");
        if (hem == null) return false;

        // Pivot = flat-face / sphere center; cap radius from world scale (matches rendered hemisphere).
        seamCenterWorld = hem.position;
        Vector3 axis = hem.up;
        float capRadius = 0.5f * Mathf.Max(hem.lossyScale.x, Mathf.Max(hem.lossyScale.y, hem.lossyScale.z));
        if (capRadius < 0.001f) return false;

        float electronR = Approximate3DElectronSphereRadiusWorld();
        const float shellMargin = 1.28f;
        float maxAxialForSphereCenter = Mathf.Max(0.001f, capRadius - electronR * shellMargin);
        float desiredAxial = capRadius * drag3DElectronDepthInCapFraction;
        float axial = Mathf.Min(desiredAxial, maxAxialForSphereCenter);
        float minAxial = capRadius * 0.06f;
        if (axial < minAxial)
            axial = Mathf.Min(minAxial, maxAxialForSphereCenter);
        electronTargetWorld = seamCenterWorld + axis * axial;
        return true;
    }

    void Update3DElectronPositionsForStretchDrag(Vector3 tip)
    {
        if (!TryCompute3DElectronTargetWorldForTip(tip, out _, out var midWorld)) return;
        var hem = stretchVisual != null ? stretchVisual.transform.Find("StretchHemisphere") : null;
        if (hem == null) return;
        Vector3 axisWorld = hem.up.sqrMagnitude > 1e-8f ? hem.up.normalized : Vector3.up;
        float capRadius = 0.5f * Mathf.Max(hem.lossyScale.x, Mathf.Max(hem.lossyScale.y, hem.lossyScale.z));

        var elist = GetComponentsInChildren<ElectronFunction>()
            .Where(e => e != null)
            .OrderBy(e => e.SlotIndex)
            .ToArray();
        if (elist.Length == 0) return;

        if (elist.Length == 1)
        {
            elist[0].transform.localPosition = transform.InverseTransformPoint(midWorld);
        }
        else
        {
            // Spread pair symmetrically about the cap interior target, perpendicular to dome axis.
            Vector3 vWorld = Vector3.Cross(axisWorld, transform.right);
            if (vWorld.sqrMagnitude < 1e-8f)
                vWorld = Vector3.Cross(axisWorld, transform.forward);
            if (vWorld.sqrMagnitude < 1e-8f)
                vWorld = Vector3.Cross(axisWorld, Vector3.up);
            vWorld.Normalize();

            float elecR = Approximate3DElectronSphereRadiusWorld();
            float halfSep = Mathf.Max(elecR * 1.35f, capRadius * 0.2f);
            float maxHalf = Mathf.Max(0.001f, capRadius * 0.45f - elecR * 0.55f);
            halfSep = Mathf.Clamp(halfSep, elecR * 0.85f, maxHalf);

            foreach (var e in elist)
            {
                float sign = elist.Length == 2 ? (e.SlotIndex == 0 ? -1f : 1f)
                    : (e.SlotIndex % 2 == 0 ? -1f : 1f);
                Vector3 posW = midWorld + vWorld * (halfSep * sign);
                e.transform.localPosition = transform.InverseTransformPoint(posW);
            }
        }
    }

    void Reposition3DElectronsAfterOrbitalDrag()
    {
        if (!Use3DOrbitalPresentation()) return;
        foreach (var e in GetComponentsInChildren<ElectronFunction>())
            e.ApplyOrbitalSlotPosition();
    }

    void UpdateStretchVisual(Vector3 tip)
    {
        if (stretchVisual == null) return;
        var anchor = GetStretchAnchorForTip(tip);
        var dir = (tip - anchor);
        var distance = dir.magnitude;
        if (distance < 0.01f && bond == null) return;
        if (distance < 0.01f) distance = 0f;
        dir = distance > 0.01f ? dir / distance : Vector3.up;
        // Bond orbitals: allow stretch to reach origin (no min length). Lone orbitals: MinStretchLength avoids overlap.
        float minLength = (bond != null) ? 0f : MinStretchLength;
        distance = Mathf.Max(distance, minLength);

        float padding = (bond != null) ? 0f : Mathf.Min(apexPadding, distance * 0.4f);
        float triLength = Mathf.Max(0.01f, distance - padding);
        Vector3 triApex = anchor + padding * dir;
        Vector3 triBase = tip;

        if (stretchSprite != null)
        {
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
            stretchVisual.transform.rotation = Quaternion.Euler(0, 0, angle);
            stretchVisual.transform.position = (anchor + tip) * 0.5f;
            stretchVisual.transform.localScale = Vector3.one;
            return;
        }

        if (stretchVisualIs3D)
        {
            stretchVisual.transform.rotation = RotationFromUpTo(dir);
            stretchVisual.transform.position = (triApex + triBase) * 0.5f;
            stretchVisual.transform.localScale = Vector3.one;
            const float ProceduralSpriteSize = 2f;
            float targetSize = ProceduralSpriteSize * originalLocalScale.y;
            float circleScale = targetSize / ProceduralSpriteSize;
            float triWidthScale = circleScale * 2f * dragStretch3DCrossSectionScale;
            // Unit cone base diameter = 1; scale.xz = triWidthScale → matches hemisphere base (diameter triWidthScale).
            // Hemisphere flat face at local y=0 sits flush on cone top (y = coneTopY); dome extends toward the tip.
            float coneTopY = 0.5f * triLength;
            var coneT = stretchVisual.transform.Find("StretchCone");
            var sphereT = stretchVisual.transform.Find("StretchHemisphere");
            if (coneT != null)
            {
                coneT.localPosition = Vector3.zero;
                coneT.localRotation = Quaternion.identity;
                coneT.localScale = new Vector3(triWidthScale, triLength, triWidthScale);
            }
            if (sphereT != null)
            {
                sphereT.localPosition = new Vector3(0f, coneTopY, 0f);
                sphereT.localRotation = Quaternion.identity;
                sphereT.localScale = Vector3.one * triWidthScale;
            }
            Update3DElectronPositionsForStretchDrag(tip);
            return;
        }

        float angleSprite = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
        stretchVisual.transform.rotation = Quaternion.Euler(0, 0, angleSprite);
        stretchVisual.transform.localScale = Vector3.one;
        var tri = stretchVisual.transform.Find("Triangle");
        var circle = stretchVisual.transform.Find("Circle");
        stretchVisual.transform.position = (triApex + triBase) * 0.5f;
        const float ProcSize = 2f;
        float targetSz = ProcSize * originalLocalScale.y;
        float circSc = targetSz / ProcSize;
        float triW = circSc * 2f;
        float triH = triLength / ProcSize;
        if (tri != null)
        {
            tri.localPosition = Vector3.zero;
            tri.localRotation = Quaternion.identity;
            tri.localScale = new Vector3(triW, triH, 1f);
            var triSr = tri.GetComponent<SpriteRenderer>();
            if (triSr != null && triSr.material != null)
            {
                var block = new MaterialPropertyBlock();
                triSr.GetPropertyBlock(block);
                block.SetVector(ClipCenterId, new Vector4(0f, 1f, 0f, 0f));
                float clipRadius = circSc;
                block.SetFloat(ClipRadiusXId, clipRadius / Mathf.Max(triW, 0.001f));
                block.SetFloat(ClipRadiusYId, clipRadius / Mathf.Max(triH, 0.001f));
                triSr.SetPropertyBlock(block);
            }
        }
        if (circle != null)
        {
            circle.localPosition = new Vector3(0f, triH, 0f);
            circle.localRotation = Quaternion.identity;
            circle.localScale = new Vector3(circSc * OffsetScale, circSc * OffsetScale, 1f);
        }
    }

    void Start()
    {
        if (originalLocalScale.sqrMagnitude < 0.01f) originalLocalScale = transform.localScale;
        var sr = GetComponent<SpriteRenderer>();
        var mr = GetComponent<MeshRenderer>();
        var mf = GetComponent<MeshFilter>();
        if (sr != null && originalColor.a < 0.01f) originalColor = sr.color;
        else if (mr != null && mr.sharedMaterial != null && originalColor.a < 0.01f)
            originalColor = GetMaterialTint(mr.sharedMaterial);

        if (Use3DOrbitalPresentation())
            Setup3DOrbitalVisual(mf, mr);

        if (editSelectionHighlightActive)
            ApplyOrbitalEditSelectionVisual(true);
        else
            ApplyOrbitalVisualOpacity(sr, mr);
        EnsureCollider();
        SyncElectronObjects();
        IgnoreCollisionsWithChildren();
    }

    void ApplyOrbitalVisualOpacity(SpriteRenderer sr, MeshRenderer mr)
    {
        Color c = originalColor;
        c.a = Mathf.Clamp01(orbitalVisualAlpha);
        originalColor = c;
        if (sr != null) sr.color = c;
        else if (mr != null)
        {
            var mat = mr.material;
            SetMaterialTint(mat, c);
        }
    }

    void RefreshOrbitalBodyVisualAfterDrag()
    {
        if (editSelectionHighlightActive)
            ApplyOrbitalEditSelectionVisual(true);
        else
            ApplyOrbitalVisualOpacity(GetComponent<SpriteRenderer>(), GetComponent<MeshRenderer>());
    }

    void IgnoreCollisionsWithChildren()
    {
        var orbitalCol2D = GetComponent<Collider2D>();
        var orbitalCol = GetComponent<Collider>();
        var electrons = GetComponentsInChildren<ElectronFunction>();
        foreach (var electron in electrons)
        {
            IgnoreCollision(orbitalCol2D, orbitalCol, electron);
            foreach (var other in electrons)
            {
                if (electron == other) continue;
                IgnoreCollision(electron.GetComponent<Collider2D>(), electron.GetComponent<Collider>(),
                    other.GetComponent<Collider2D>(), other.GetComponent<Collider>());
            }
        }
    }

    static void IgnoreCollision(Collider2D a2D, Collider a3D, ElectronFunction b)
    {
        var b2D = b.GetComponent<Collider2D>();
        var b3D = b.GetComponent<Collider>();
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    static void IgnoreCollision(Collider2D a2D, Collider a3D, Collider2D b2D, Collider b3D)
    {
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    [SerializeField] Vector2 orbitalColliderSize = new Vector2(1f, 0.15f); // Thin bar so it doesn't overlap electrons at y=±0.25

    void EnsureCollider()
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col == null && col2D == null)
        {
            col = gameObject.AddComponent<BoxCollider>();
            col.isTrigger = true;
        }
        if (col is BoxCollider box)
            box.size = new Vector3(orbitalColliderSize.x, orbitalColliderSize.y, 0.1f);
        if (col2D is BoxCollider2D box2D)
            box2D.size = orbitalColliderSize;
    }

    void LateUpdate()
    {
        if (Use3DOrbitalPresentation())
        {
            if (debugDraw3DElectronDrag && isBeingHeld && stretchVisualIs3D && stretchVisual != null)
            {
                var tip = transform.position;
                if (TryCompute3DElectronTargetWorldForTip(tip, out var seam, out var tgt))
                {
                    Debug.DrawLine(seam, tgt, Color.green);
                    Debug.DrawRay(seam, stretchVisual.transform.up * 0.04f, Color.yellow);
                    foreach (var e in GetComponentsInChildren<ElectronFunction>())
                    {
                        Debug.DrawLine(tgt, e.transform.position, Color.cyan);
                        Debug.DrawRay(e.transform.position, Vector3.up * 0.03f, Color.magenta);
                    }
                }
            }

            if (!isBeingHeld)
            {
                foreach (var e in GetComponentsInChildren<ElectronFunction>())
                {
                    if (e == null || e.IsElectronPointerDragActive) continue;
                    e.ApplyOrbitalSlotPosition();
                }
            }
        }
    }

    void SyncElectronObjects()
    {
        if (electronPrefab == null) return;

        var existing = GetComponentsInChildren<ElectronFunction>();
        int target = electronCount;

        if (existing.Length > target)
        {
            for (int i = target; i < existing.Length; i++)
                Destroy(existing[i].gameObject);
        }
        else if (existing.Length < target)
        {
            for (int i = existing.Length; i < target; i++)
            {
                var electron = Instantiate(electronPrefab, transform);
                electron.SetSlotIndex(i);
                electron.SetOrbitalWidth(electronSpacing);
            }
        }

        var electrons = GetComponentsInChildren<ElectronFunction>();
        for (int i = 0; i < electrons.Length; i++)
        {
            electrons[i].SetSlotIndex(i);
            electrons[i].SetOrbitalWidth(electronSpacing);
            electrons[i].Sync2DAppearanceWithOrbital();
        }

        IgnoreCollisionsWithChildren();
    }
}
