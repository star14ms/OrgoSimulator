using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

/// <summary>
/// Represents a covalent bond between two atoms. Owns the shared orbital and its electrons.
/// Positions the orbital between the two atoms and notifies both when electrons change.
/// Orthographic scenes: 2D sprite line. Perspective (3D) scenes: solid cylinder mesh along the bond axis.
/// Click to show orbital while holding, returns to line/cylinder on release if bond doesn't break.
/// </summary>
public class CovalentBond : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    const string AgentNdjsonPath = "Assets/../.cursor/debug-8de5d1.log";
    // Compatibility debug gates still referenced from AtomFunction.
    public static bool DebugLogBreakBondSigmaRelaxWhy = false;
    public static bool DebugLogBreakBondMotionSources = false;

    [SerializeField] AtomFunction atomA;
    [SerializeField] AtomFunction atomB;
    ElectronOrbitalFunction orbital;
    AtomFunction orbitalContributor; // Atom that contributed the orbital; used for odd-electron tie-break when EN equal
    ElectronOrbitalFunction orbitalBeingFadedForCharge; // During bond formation: the orbital being faded; use its electrons for charge until destroyed

    bool orbitalVisible;

    bool forwardedPressToOrbital;
    float orbitalToLineAnimProgress = -1f; // -1 = done, 0..1 = animating
    /// <summary>π row (<see cref="GetBondIndex"/> &gt; 0): keep perspective π cylinders/lobes visible together with the bond orbital
    /// during post–Create cylinder lerp and until orbital-to-line starts, so OP lobes and π mesh do not blink out of sync.</summary>
    bool showPerspectivePiWithBondOrbitalOverlap;
    /// <summary>π row: after phase-2 cylinder, hide the shared bond orbital mesh while keeping π cylinders on so OP source
    /// and bond lobe do not stack-blink before orbital-to-line.</summary>
    bool suppressSharedOrbitalVisualForPiHandoff;
    internal bool animatingOrbitalToBondPosition; // σ/π post-Create: orbital moving from atom to bond before snap
    internal bool orbitalRotationFlipped; // Sigma: flip when source opposite to bond so electrons don't overlap

    /// <summary>
    /// When set during orbital-drag σ guide lerp, <see cref="LateUpdate"/> and bond-frame snap helpers must not overwrite
    /// <see cref="orbital"/> world pose — <see cref="SigmaBondFormation"/> drives the shell that frame.
    /// </summary>
    internal bool suppressSigmaPrebondBondFrameOrbitalPose;

    /// <summary>
    /// Set for σ created by <see cref="SigmaBondFormation"/> orbital-drag pipeline: <see cref="ElectronRedistributionOrchestrator.ExecuteSigmaFormation12HybridAlignment"/>
    /// must not re-run <c>nonGuide_first</c> TryMatch before post-bond guide refresh (duplicate refresh can teleport lone pairs).
    /// </summary>
    internal bool SkipNonGuideExecuteSigmaFormation12HybridPass;

    /// <summary>Left-multiplies base σ-orbital world rotation after <see cref="AtomFunction.RedistributeOrbitals3D"/> so +X tracks VSEPR hybrid on the authoritative nucleus.</summary>
    Quaternion orbitalRedistributionWorldDelta = Quaternion.identity;
    PointerEventData storedPressData;
    Vector2 bondLinePointerDownScreen;
    GameObject lineVisual;
    SpriteRenderer lineRenderer;
    MeshRenderer cylinderRenderer;
    MeshRenderer piCylinderRendererSecondary;
    GameObject piCylinderSecondaryVisual;
    GameObject piLobePositiveAVisual;
    GameObject piLobePositiveBVisual;
    GameObject piLobeNegativeAVisual;
    GameObject piLobeNegativeBVisual;
    MeshRenderer piLobePositiveARenderer;
    MeshRenderer piLobePositiveBRenderer;
    MeshRenderer piLobeNegativeARenderer;
    MeshRenderer piLobeNegativeBRenderer;
    bool useCylinderBondVisual;
    BoxCollider2D lineCollider;
    BoxCollider lineCollider3D;
    static Sprite lineSprite;
    static Material bondCylinderMaterial;
    static Material piBondCylinderMaterial;
    static Mesh piLobeSphereMesh;

    /// <summary>
    /// World direction for perspective π lobes (⟂ internuclear, before <see cref="GetLineOffset"/> sign);
    /// filled from π phase-1 redistribution template so visuals stay correct after template static is cleared.
    /// </summary>
    Vector3? cachedPerspectivePiAxisWorld;
    MaterialPropertyBlock bondFormationDebugGuideMpb;
    MaterialPropertyBlock bondFormationTemplatePickMpb;
    static readonly int BondShaderBaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int BondShaderColorId = Shader.PropertyToID("_Color");

    /// <summary>Black bond stroke with slight transparency (sprite + 3D cylinder).</summary>
    const float BondVisualAlpha = 0.82f;
    static readonly Color BondVisualColor = new Color(0f, 0f, 0f, BondVisualAlpha);
    const float PiBondVisualAlpha = 0.05f;
    static readonly Color PiBondVisualColor = new Color(1f, 1f, 1f, PiBondVisualAlpha);

    public AtomFunction AtomA => atomA;
    public AtomFunction AtomB => atomB;
    public ElectronOrbitalFunction Orbital => orbital;

    /// <summary>Nucleus orbital being faded out and destroyed after σ/π formation; omit from post-formation VSEPR mover lists.</summary>
    internal ElectronOrbitalFunction OrbitalBeingFadedForCharge => orbitalBeingFadedForCharge;

    public int ElectronCount => orbital != null ? orbital.ElectronCount : 0;

    /// <summary>Diagnostics only — read <see cref="orbitalRedistributionWorldDelta"/> for σ-formation pose logs.</summary>
    internal Quaternion GetOrbitalRedistributionWorldDeltaForDiagnostics() => orbitalRedistributionWorldDelta;

    internal (Quaternion delta, bool flipped) CapturePiStep2RedistributionForBake() =>
        (orbitalRedistributionWorldDelta, orbitalRotationFlipped);

    internal void RestorePiStep2RedistributionForBake(Quaternion delta, bool flipped)
    {
        orbitalRedistributionWorldDelta = delta;
        orbitalRotationFlipped = flipped;
    }

    /// <summary>Returns how many bond electrons count toward the given atom for charge calculation. Uses electronegativity; when equal EN and odd count, orbital contributor gets the extra electron.</summary>
    public int GetElectronsOwnedBy(AtomFunction atom)
    {
        if (atom == null || atomA == null || atomB == null || atom != atomA && atom != atomB) return 0;
        var other = atom == atomA ? atomB : atomA;
        if (other == null) return 0;
        int bondElectrons = ElectronCount;
        if (orbitalBeingFadedForCharge != null)
            bondElectrons += orbitalBeingFadedForCharge.ElectronCount;
        float myEN = AtomFunction.GetElectronegativity(atom.AtomicNumber);
        float otherEN = AtomFunction.GetElectronegativity(other.AtomicNumber);
        if (myEN > otherEN) return bondElectrons;
        if (myEN < otherEN) return 0;
        if (bondElectrons % 2 == 0) return bondElectrons / 2;
        if (orbitalContributor == null) return bondElectrons / 2; // Fallback when contributor unknown
        return orbitalContributor == atom ? (bondElectrons + 1) / 2 : (bondElectrons - 1) / 2;
    }

    /// <summary>Lewis formal charge: each end gets half the σ bonding e⁻; odd totals split by stable instance-id order.</summary>
    public int GetFormalChargeElectronsOwnedBy(AtomFunction atom)
    {
        if (atom == null || atomA == null || atomB == null || (atom != atomA && atom != atomB)) return 0;
        int bondElectrons = ElectronCount;
        if (orbitalBeingFadedForCharge != null)
            bondElectrons += orbitalBeingFadedForCharge.ElectronCount;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        return atom == first ? (bondElectrons + 1) / 2 : bondElectrons / 2;
    }

    /// <summary>Updates bond transform to current atom positions. Call before SnapOrbitalToBondPosition when animatingOrbitalToBondPosition was true (bond may be stale).</summary>
    public void UpdateBondTransformToCurrentAtoms()
    {
        if (atomA == null || atomB == null) return;
        var posA = atomA.transform.position;
        var posB = atomB.transform.position;
        var center = (posA + posB) * 0.5f;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;
        transform.position = center;
        transform.rotation = BondFrameRotation(delta, useCylinderBondVisual);
    }

    /// <summary>Snaps the bond orbital to the exact world position that matches lineVisual (center + perpendicular * offset). Call at end of post-Create formation to prevent teleport into orbital-to-line. Call UpdateBondTransformToCurrentAtoms first.</summary>
    /// <param name="preserveWorldRollFrom">Optional σ-formation: world rot after step-2 lerp (often matches cylinder +X but not bond cylinder roll). When set in 3D σ line, applies swing to match cylinder +X and writes <see cref="orbitalRedistributionWorldDelta"/> so <see cref="LateUpdate"/> agrees.</param>
    public void SnapOrbitalToBondPosition(Quaternion? preserveWorldRollFrom = null)
    {
        if (orbital == null || atomA == null || atomB == null) return;
        if (suppressSigmaPrebondBondFrameOrbitalPose) return;
        var (worldPos, worldRot) = GetOrbitalTargetWorldState();
        orbital.transform.position = worldPos;
        Quaternion appliedRot = worldRot;
        if (preserveWorldRollFrom.HasValue)
        {
            Quaternion pref = preserveWorldRollFrom.Value;
            Vector3 pTip = pref * Vector3.right;
            Vector3 wTip = worldRot * Vector3.right;
            if (pTip.sqrMagnitude > 1e-12f && wTip.sqrMagnitude > 1e-12f)
                appliedRot = Quaternion.FromToRotation(pTip.normalized, wTip.normalized) * pref;
        }
        if (IsSigmaBondLine())
        {
            var baseR = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
            if (orbitalRotationFlipped) baseR = baseR * Quaternion.Euler(0f, 0f, 180f);
            orbitalRedistributionWorldDelta = appliedRot * Quaternion.Inverse(baseR);
        }
        orbital.transform.rotation = appliedRot;
    }

    /// <summary>Used by <see cref="AtomFunction.UpdateSigmaBondVisualsForAtoms"/>; same guards as LateUpdate σ pose.</summary>
    internal void UpdateSigmaBondVisualsAfterBondTransform()
    {
        if (orbital == null || atomA == null || atomB == null) return;
        bool userDragging = orbitalVisible && orbitalToLineAnimProgress < 0;
        if (userDragging) return;
        if (animatingOrbitalToBondPosition) return;
        if (suppressSigmaPrebondBondFrameOrbitalPose) return;
        SnapOrbitalToBondPosition();
    }

    /// <summary>
    /// Applies <see cref="GetOrbitalTargetWorldState"/> immediately after changing <see cref="orbitalRedistributionWorldDelta"/>
    /// so σ lobe +X and nucleus-local tip reads match before <c>LateUpdate</c>. Same guards as σ pose block in <c>LateUpdate</c>.
    /// </summary>
    /// <param name="forceApplyPoseDuringBondToLineAnim">σ formation calls <see cref="ApplySigmaOrbitalTipFromRedistribution"/> while <see cref="animatingOrbitalToBondPosition"/> is still true; those updates must still push world pose to match δ/flip before snap. When true, suppress does not skip apply: phase 3 sets suppress so <see cref="LateUpdate"/> does not fight the lerp, but explicit δ updates must still write the orbital transform.</param>
    public void SyncSigmaOrbitalWorldPoseFromRedistribution(bool forceApplyPoseDuringBondToLineAnim = false)
    {
        if (orbital == null || atomA == null || atomB == null) return;
        if (!IsSigmaBondLine()) return;
        bool userDragging = orbitalVisible && orbitalToLineAnimProgress < 0;
        if (userDragging) return;
        if (suppressSigmaPrebondBondFrameOrbitalPose && !forceApplyPoseDuringBondToLineAnim)
            return;
        if (!forceApplyPoseDuringBondToLineAnim && animatingOrbitalToBondPosition) return;
        UpdateBondTransformToCurrentAtoms();
        var (worldPos, worldRot) = GetOrbitalTargetWorldState();
        orbital.transform.position = worldPos;
        orbital.transform.rotation = worldRot;
        float orbScale = orbitalToLineAnimProgress < 0 ? 0.6f : 0.6f * (1f - orbitalToLineAnimProgress);
        orbital.transform.localScale = Vector3.one * Mathf.Max(0.01f, orbScale);
    }

    /// <summary>Returns the target world position and rotation for the bond orbital (post-Create formation animation).</summary>
    public (Vector3 worldPos, Quaternion worldRot) GetOrbitalTargetWorldState()
    {
        if (atomA == null || atomB == null)
            return GetOrbitalTargetWorldStateForLine(0, 1, orbitalRotationFlipped);
        return GetOrbitalTargetWorldStateForLine(GetBondIndex(), atomA.GetBondsTo(atomB), orbitalRotationFlipped);
    }

    /// <summary>
    /// Target pose for a bond line between these atoms: <paramref name="lineIndex"/> 0 = σ (centered), 1+ = π offsets per <see cref="GetLineOffset"/>.
    /// Use <paramref name="applyOrbitalRotationFlip"/> instead of mutating <see cref="orbitalRotationFlipped"/> (e.g. π phase-1 prep on the σ bond).
    /// </summary>
    public (Vector3 worldPos, Quaternion worldRot) GetOrbitalTargetWorldStateForLine(
        int lineIndex,
        int bondCount,
        bool applyOrbitalRotationFlip)
    {
        Quaternion BaseSigmaOrbitalWorldRotation(bool applyFlip)
        {
            var r = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
            if (applyFlip && applyOrbitalRotationFlip) r = r * Quaternion.Euler(0f, 0f, 180f);
            return r;
        }

        if (atomA == null || atomB == null)
            return (transform.position, orbitalRedistributionWorldDelta * BaseSigmaOrbitalWorldRotation(false));
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var posA = atomA.transform.position;
        var posB = atomB.transform.position;
        var center = (posA + posB) * 0.5f;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f)
            return (center, orbitalRedistributionWorldDelta * BaseSigmaOrbitalWorldRotation(false));
        var perpendicular = PerpendicularToBondDirection(delta / distance);
        float offset = GetLineOffset(lineIndex, bondCount);
        var worldPos = center + perpendicular * offset;
        if (useCylinderBondVisual && lineIndex > 0 && TryGetPerspectivePiLobeEndpointsForLine(lineIndex, bondCount, out var piLobes))
            worldPos = piLobes.positiveCenter;
        var worldRot = orbitalRedistributionWorldDelta * BaseSigmaOrbitalWorldRotation(true);
        return (worldPos, worldRot);
    }

    internal struct PerspectivePiLobeEndpoints
    {
        public Vector3 positiveA;
        public Vector3 positiveB;
        public Vector3 negativeA;
        public Vector3 negativeB;
        public Vector3 positiveCenter;
        public Vector3 negativeCenter;
        public Quaternion lineRotation;
        public float lineDistance;
    }

    void TryInheritCachedPerspectivePiAxisFromPartnerBond()
    {
        if (atomA == null || atomB == null || cachedPerspectivePiAxisWorld.HasValue) return;
        // Second π (triple): lobe +Z is built ⟂ first π from the σ–π plane; inheriting the first π cache would collapse both rows.
        if (GetBondIndex() >= 2)
            return;
        foreach (var cb in atomA.CovalentBonds)
        {
            if (cb == null || ReferenceEquals(cb, this)) continue;
            if ((cb.AtomA != atomA || cb.AtomB != atomB) && (cb.AtomA != atomB || cb.AtomB != atomA)) continue;
            if (!cb.cachedPerspectivePiAxisWorld.HasValue) continue;
            cachedPerspectivePiAxisWorld = cb.cachedPerspectivePiAxisWorld;
            return;
        }
    }

    /// <summary>σ is index 0; π rows are 1, 2, … in <see cref="GetBondIndex"/> order between the same atom pair.</summary>
    internal static CovalentBond FindCovalentBondByLineIndexBetween(AtomFunction a, AtomFunction b, int lineIndex)
    {
        if (a == null || b == null) return null;
        foreach (var cb in a.CovalentBonds)
        {
            if (cb == null) continue;
            var other = cb.AtomA == a ? cb.AtomB : cb.AtomA;
            if (other != b) continue;
            if (cb.GetBondIndex() == lineIndex)
                return cb;
        }
        return null;
    }

    internal bool TryGetPerspectivePiLobeEndpointsForLine(int lineIndex, int bondCount, out PerspectivePiLobeEndpoints endpoints)
    {
        // One direction per partner atom (σ and π rows share the same neighbor); need two non-collinear
        // substituents so the cross product is the local trigonal plane normal (π axis for sp²-like cases).
        bool TryComputeLocalPlaneNormalFromUniqueNeighbors(AtomFunction center, AtomFunction exclude, out Vector3 n)
        {
            n = Vector3.zero;
            if (center == null || center.CovalentBonds == null) return false;
            var dirs = new List<Vector3>(6);
            var seen = new HashSet<AtomFunction>();
            foreach (var cb in center.CovalentBonds)
            {
                if (cb == null) continue;
                AtomFunction other = center.GetPartnerAtomOnBond(cb);
                if (other == null || other == exclude) continue;
                if (!seen.Add(other)) continue;
                Vector3 v = other.transform.position - center.transform.position;
                if (v.sqrMagnitude < 1e-10f) continue;
                dirs.Add(v.normalized);
            }
            for (int i = 0; i < dirs.Count; i++)
            {
                for (int j = i + 1; j < dirs.Count; j++)
                {
                    float d = Mathf.Abs(Vector3.Dot(dirs[i], dirs[j]));
                    if (d > 1f - 1e-4f) continue;
                    Vector3 cross = Vector3.Cross(dirs[i], dirs[j]);
                    if (cross.sqrMagnitude < 1e-10f) continue;
                    n = cross.normalized;
                    return true;
                }
            }
            return false;
        }

        endpoints = default;
        if (!useCylinderBondVisual || lineIndex <= 0 || atomA == null || atomB == null)
            return false;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        Vector3 axis = second.transform.position - first.transform.position;
        float dist = axis.magnitude;
        if (dist < 0.001f)
            return false;
        Vector3 axisN = axis / dist;
        Vector3 pNormal = PerpendicularToBondDirection(axisN);
        bool usedBondPiAxisCache = false;
        bool usedPhase1TemplatePlane = false;
        float phase1TemplateAxisDot = -2f;
        bool usedNeighborPlane = false;
        float neighborPlaneAxisDot = -2f;
        bool usedSecondPiOrthogonal = false;

        // Triple (second π): both π axes lie in the plane ⟂ internuclear (σ); second row's +Z is ⟂ first π's +Z there.
        if (lineIndex == 2)
        {
            var piFirst = FindCovalentBondByLineIndexBetween(atomA, atomB, 1);
            if (piFirst != null
                && piFirst.TryGetPerspectivePiLobeEndpointsForLine(1, bondCount, out var leFirst))
            {
                Vector3 posA = atomA.transform.position;
                Vector3 posB = atomB.transform.position;
                float da = (leFirst.positiveA - posA).sqrMagnitude;
                float db = (leFirst.positiveA - posB).sqrMagnitude;
                Vector3 plusDir = da <= db ? leFirst.positiveA - posA : leFirst.positiveA - posB;
                if (plusDir.sqrMagnitude >= 1e-14f)
                {
                    plusDir.Normalize();
                    Vector3 p1InPlane = Vector3.ProjectOnPlane(plusDir, axisN);
                    if (p1InPlane.sqrMagnitude >= 1e-10f)
                    {
                        p1InPlane.Normalize();
                        Vector3 cross = Vector3.Cross(axisN, p1InPlane);
                        if (cross.sqrMagnitude >= 1e-10f)
                        {
                            pNormal = cross.normalized;
                            usedSecondPiOrthogonal = true;
                        }
                    }
                }
            }
        }

        if (!usedSecondPiOrthogonal)
        {
            if (lineIndex < 2 && cachedPerspectivePiAxisWorld.HasValue)
            {
                Vector3 projectedCache = Vector3.ProjectOnPlane(cachedPerspectivePiAxisWorld.Value, axisN);
                if (projectedCache.sqrMagnitude > 1e-10f)
                {
                    pNormal = projectedCache.normalized;
                    usedBondPiAxisCache = true;
                }
            }
            if (!usedBondPiAxisCache
                && OrbitalRedistribution.TryGetPiPhase1PrecursorTrigonalPlaneNormalWorldForBondPair(atomA, atomB, out var nPhase1Tpl))
            {
                phase1TemplateAxisDot = Mathf.Abs(Vector3.Dot(nPhase1Tpl, axisN));
                if (phase1TemplateAxisDot < 0.35f)
                {
                    pNormal = phase1TemplateAxisDot > 1e-4f
                        ? Vector3.ProjectOnPlane(nPhase1Tpl, axisN).normalized
                        : nPhase1Tpl;
                    usedPhase1TemplatePlane = true;
                }
            }
            if (!usedBondPiAxisCache
                && !usedPhase1TemplatePlane
                && (TryComputeLocalPlaneNormalFromUniqueNeighbors(atomA, atomB, out var nPlane)
                    || TryComputeLocalPlaneNormalFromUniqueNeighbors(atomB, atomA, out nPlane)))
            {
                neighborPlaneAxisDot = Mathf.Abs(Vector3.Dot(nPlane, axisN));
                // Bond should lie in the substituent plane so π axis (plane normal) is ⟂ internuclear axis.
                if (neighborPlaneAxisDot < 0.35f)
                {
                    pNormal = neighborPlaneAxisDot > 1e-4f
                        ? Vector3.ProjectOnPlane(nPlane, axisN).normalized
                        : nPlane;
                    usedNeighborPlane = true;
                }
            }
            if (!usedBondPiAxisCache && !usedPhase1TemplatePlane && !usedNeighborPlane && Orbital != null)
            {
                Vector3 opAxis = OrbitalAngleUtility.GetOrbitalDirectionWorld(Orbital.transform);
                Vector3 projected = Vector3.ProjectOnPlane(opAxis, axisN);
            }
        }
        Vector3 pNormalBeforeLineOffsetSign = pNormal;
        float lineOffset = GetLineOffset(lineIndex, bondCount);
        if (lineOffset < 0f)
            pNormal = -pNormal;
        if (lineIndex < 2 && usedPhase1TemplatePlane)
            cachedPerspectivePiAxisWorld = pNormalBeforeLineOffsetSign.normalized;
        float lobeOffset = Mathf.Max(0.05f, 0.6f * 0.5f * (atomA.BondRadius + atomB.BondRadius));
        Vector3 plus = pNormal * lobeOffset;
        endpoints.positiveA = atomA.transform.position + plus;
        endpoints.positiveB = atomB.transform.position + plus;
        endpoints.negativeA = atomA.transform.position - plus;
        endpoints.negativeB = atomB.transform.position - plus;
        endpoints.positiveCenter = 0.5f * (endpoints.positiveA + endpoints.positiveB);
        endpoints.negativeCenter = 0.5f * (endpoints.negativeA + endpoints.negativeB);
        endpoints.lineRotation = BondFrameRotation(axis, true);
        endpoints.lineDistance = dist;
        return true;
    }

    /// <summary>Lower <see cref="AtomFunction.GetInstanceID"/> end sets shared σ orbital rotation after redistribution so both fragments agree on one world pose.</summary>
    public AtomFunction AuthoritativeAtomForOrbitalRedistributionPose()
    {
        if (atomA == null) return atomB;
        if (atomB == null) return atomA;
        return atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
    }

    public bool IsSigmaBondLine() => GetBondIndex() == 0;

    public void ResetOrbitalRedistributionDeltaIfAuthoritative(AtomFunction caller)
    {
        if (caller == null || caller != AuthoritativeAtomForOrbitalRedistributionPose()) return;
        orbitalRedistributionWorldDelta = Quaternion.identity;
    }

    /// <summary>
    /// Clears <see cref="orbitalRedistributionWorldDelta"/> without the InstanceID authority gate.
    /// Used when the σ prebond pivot recomputes hybrid/VSEPR for its shell: stale δ from a prior formation
    /// on substituent bonds must not survive when the pivot is not <see cref="AuthoritativeAtomForOrbitalRedistributionPose"/>.
    /// </summary>
    internal void ResetOrbitalRedistributionWorldDeltaIgnoringAuthority()
    {
        if (!IsSigmaBondLine()) return;
        orbitalRedistributionWorldDelta = Quaternion.identity;
    }

    /// <summary>Align shared σ lobe +X with <paramref name="hybridTipWorld"/> (must be unit, usually nucleus→partner from <see cref="AtomFunction.SyncSigmaBondOrbitalTipsFromLocks"/>). Hemisphere uses <paramref name="fromAtom"/> → partner so the target matches the caller’s outward bond axis (hybrid locks are built in that frame). Lower-InstanceID authority is still used elsewhere for δ ownership; tip hemisphere follows the applying nucleus.</summary>
    public void ApplySigmaOrbitalTipFromRedistribution(AtomFunction fromAtom, Vector3 hybridTipWorld)
    {
        if (orbital == null) return;
        if (fromAtom == null || (fromAtom != atomA && fromAtom != atomB)) return;
        if (!IsSigmaBondLine()) return;

        UpdateBondTransformToCurrentAtoms();

        if (hybridTipWorld.sqrMagnitude < 1e-12f) return;
        hybridTipWorld.Normalize();

        AtomFunction partnerFromCaller = fromAtom == atomA ? atomB : atomA;
        if (partnerFromCaller == null) return;
        Vector3 geom = partnerFromCaller.transform.position - fromAtom.transform.position;
        // #region agent log
        if (ElectronRedistributionOrchestrator.DebugLogApplySigmaTipInternuclearNdjson
            && geom.sqrMagnitude > 1e-10f)
        {
            Vector3 geomN = geom.normalized;
            float dotHybVsCallerGeom = Vector3.Dot(hybridTipWorld, geomN);
            AtomFunction authAtom = AuthoritativeAtomForOrbitalRedistributionPose();
            float dotHybVsLegacyAuthGeom = dotHybVsCallerGeom;
            if (authAtom != null)
            {
                AtomFunction pAuth = authAtom == atomA ? atomB : atomA;
                if (pAuth != null)
                {
                    Vector3 gAuth = pAuth.transform.position - authAtom.transform.position;
                    if (gAuth.sqrMagnitude > 1e-10f)
                        dotHybVsLegacyAuthGeom = Vector3.Dot(hybridTipWorld, gAuth.normalized);
                }
            }
            bool willFlipCaller = dotHybVsCallerGeom < 0f;
            ProjectAgentDebugLog.AppendCursorDebugSessionC2019eNdjson(
                "H49",
                "CovalentBond.cs:ApplySigmaOrbitalTip",
                "hybrid_vs_caller_geom_before_flip",
                "{\"bondId\":" + GetInstanceID()
                + ",\"fromAtomId\":" + fromAtom.GetInstanceID()
                + ",\"fromZ\":" + fromAtom.AtomicNumber
                + ",\"dotHybridVsCallerInternucGeom\":" + ProjectAgentDebugLog.JsonFloatInvariant(dotHybVsCallerGeom)
                + ",\"dotHybridVsLegacyAuthInternucGeom\":" + ProjectAgentDebugLog.JsonFloatInvariant(dotHybVsLegacyAuthGeom)
                + ",\"flipHybridForCallerHemisphere\":" + (willFlipCaller ? "true" : "false") + "}",
                "sigmaTipInternuc");
        }
        // #endregion
        if (geom.sqrMagnitude > 1e-10f)
        {
            geom.Normalize();
            if (Vector3.Dot(hybridTipWorld, geom) < 0f)
                hybridTipWorld = -hybridTipWorld;
        }

        var baseR = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
        if (orbitalRotationFlipped) baseR = baseR * Quaternion.Euler(0f, 0f, 180f);
        Vector3 tip0 = (baseR * Vector3.right).normalized;
        Vector3 want = hybridTipWorld;
        if (tip0.sqrMagnitude < 1e-10f || want.sqrMagnitude < 1e-10f) return;
        float dot = Vector3.Dot(tip0, want);
        Vector3 actualTipWForProbe = orbital != null ? orbital.transform.rotation * Vector3.right : Vector3.zero;
        float angTip0VsWantDegProbe = tip0.sqrMagnitude > 1e-10f && want.sqrMagnitude > 1e-10f
            ? Vector3.Angle(tip0, want)
            : -1f;
        float angActualVsWantDegProbe = -1f;
        float dotActualVsWantProbe = -2f;
        if (actualTipWForProbe.sqrMagnitude > 1e-10f && want.sqrMagnitude > 1e-10f)
        {
            actualTipWForProbe.Normalize();
            dotActualVsWantProbe = Vector3.Dot(actualTipWForProbe, want);
            angActualVsWantDegProbe = Vector3.Angle(actualTipWForProbe, want);
        }
        if (dot > 0.9999f)
        {
            // #region agent log
            if (ElectronRedistributionOrchestrator.DebugLogSigmaBondRefreshGeomAlignedEarlyExitNdjson)
            {
                Vector3 actualTipW = orbital.transform.rotation * Vector3.right;
                if (actualTipW.sqrMagnitude > 1e-10f) actualTipW.Normalize();
                float angActualVsWantDeg = actualTipW.sqrMagnitude > 1e-10f
                    ? Vector3.Angle(actualTipW, want)
                    : -1f;
                ProjectAgentDebugLog.AppendCursorDebugSessionC2019eNdjson(
                    "H54",
                    "CovalentBond.cs:ApplySigmaOrbitalTip",
                    "bond_sigma_refresh_early_exit_tip_already_along_geom_axis",
                    "{\"bondId\":" + GetInstanceID()
                    + ",\"fromAtomId\":" + fromAtom.GetInstanceID()
                    + ",\"dotTip0Want\":" + ProjectAgentDebugLog.JsonFloatInvariant(dot)
                    + ",\"angActualTipWorldVsWantDeg\":" + ProjectAgentDebugLog.JsonFloatInvariant(angActualVsWantDeg)
                    + ",\"note\":\"SyncSigmaBondOrbitalTipsFromLocks uses geometric nucleus-to-partner; TryMatch idealLocal is for lock matching only, not the apply target\"}",
                    "bondRefreshExplain");
            }
            // #endregion
            orbitalRedistributionWorldDelta = Quaternion.identity;
            SyncSigmaOrbitalWorldPoseFromRedistribution(forceApplyPoseDuringBondToLineAnim: true);
            return;
        }

        if (dot < -0.9999f)
        {
            // Anti-parallel in canonical base vs hybrid tip (dot ≈ -1). Toggling flip re-snaps world rot
            // and pops ~180° when step-2 already landed the σ on the same line (σTipUndir°≈0, alongAxisMatch).
            // If the live orbital +X already agrees with hybridTipWorld, absorb the pose into δ and skip flip.
            Vector3 actualTip = (orbital.transform.rotation * Vector3.right);
            if (actualTip.sqrMagnitude > 1e-10f)
            {
                actualTip.Normalize();
                if (Vector3.Dot(actualTip, want) > 0.9999f)
                {
                    orbitalRedistributionWorldDelta = orbital.transform.rotation * Quaternion.Inverse(baseR);
                    SyncSigmaOrbitalWorldPoseFromRedistribution(forceApplyPoseDuringBondToLineAnim: true);
                    if (ElectronOrbitalFunction.DebugLogSigmaFormationHeavyOrbRotationWhy
                        && ElectronOrbitalFunction.ConsumeSigmaFormationHeavyRotDiag())
                    {
                        Debug.Log("[σ-form-rot-bond] antiParallel: preserved world rot (actualTip∥hybrid), δ set from pose");
                    }
                    return;
                }
            }

            // Anti-parallel: avoid applying a full 180° redistribution delta (which can look like a spin
            // even when the lobe is already correct up to the cylinder convention / gauge freedom).
            orbitalRotationFlipped = !orbitalRotationFlipped;
            orbitalRedistributionWorldDelta = Quaternion.identity;
            SyncSigmaOrbitalWorldPoseFromRedistribution(forceApplyPoseDuringBondToLineAnim: true);
            // #region agent log
            if (ElectronRedistributionOrchestrator.DebugLog8de5d1NonBondRedistDirections)
            {
                ProjectAgentDebugLog.AppendCursorDebugSessionC2019eNdjson(
                    "H127",
                    "CovalentBond.cs:ApplySigmaOrbitalTipFromRedistribution",
                    "sigma_tip_apply_flip_branch_probe_exit",
                    "{\"bondId\":" + GetInstanceID()
                    + ",\"branch\":\"dot_lt_neg_threshold_toggle_flip\""
                    + ",\"orbitalRotationFlippedAfter\":" + (orbitalRotationFlipped ? "true" : "false")
                    + "}",
                    "breakFlipProbe");
            }
            // #endregion
            if (ElectronOrbitalFunction.DebugLogSigmaFormationHeavyOrbRotationWhy
                && ElectronOrbitalFunction.ConsumeSigmaFormationHeavyRotDiag())
            {
                Debug.Log("[σ-form-rot-bond] antiParallel -> toggled orbitalRotationFlipped");
            }
            return;
        }

        Quaternion deltaQ = Quaternion.FromToRotation(tip0, want);
        orbitalRedistributionWorldDelta = deltaQ;
        if (ElectronOrbitalFunction.DebugLogSigmaFormationHeavyOrbRotationWhy
            && ElectronOrbitalFunction.ConsumeSigmaFormationHeavyRotDiag())
        {
            var auth = AuthoritativeAtomForOrbitalRedistributionPose();
            Debug.Log(
                "[σ-form-rot-bond] ApplySigmaOrbitalTipFromRedistribution bond=" + name + " fromAtom=" + fromAtom.name + "(Z=" + fromAtom.AtomicNumber + ")" +
                " δrot°=" + Quaternion.Angle(Quaternion.identity, deltaQ).ToString("F2") + " dot(tip0,want)=" + Vector3.Dot(tip0, want).ToString("F4") +
                " authoritative=" + (auth != null ? auth.name : "null"));
        }
        SyncSigmaOrbitalWorldPoseFromRedistribution(forceApplyPoseDuringBondToLineAnim: true);
    }

    internal int GetBondIndex()
    {
        if (atomA == null || atomB == null) return 0;
        var bondsBetween = new List<CovalentBond>();
        foreach (var b in atomA.CovalentBonds)
        {
            if (b == null) continue;
            var other = b.AtomA == atomA ? b.AtomB : b.AtomA;
            if (other == atomB) bondsBetween.Add(b);
        }
        for (int i = 0; i < bondsBetween.Count; i++)
            if (bondsBetween[i] == this) return i;
        return 0;
    }

    /// <summary>Sigma bond (index 0) centered; pi bonds (index 1,2...) offset left/right.</summary>
    static float GetLineOffset(int bondIndex, int bondCount)
    {
        if (bondCount <= 1) return 0f;
        if (bondIndex == 0) return 0f; // sigma bond centered
        const float piOffset = 0.2f; // spacing between lines
        return (bondIndex % 2 == 1 ? -1f : 1f) * ((bondIndex + 1) / 2) * piOffset; // pi: -0.2, +0.2, -0.4, +0.4...
    }

    /// <summary>URP cylinder material; shared across bonds. Uses alpha so bonds read slightly softer in 3D.</summary>
    static Material GetOrCreateBondCylinderMaterial()
    {
        if (bondCylinderMaterial != null) return bondCylinderMaterial;
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (sh == null) sh = Shader.Find("Standard");
        bondCylinderMaterial = sh != null ? new Material(sh) : null;
        if (bondCylinderMaterial != null)
        {
            bondCylinderMaterial.enableInstancing = true;
            ApplyBondVisualColorToMaterial(bondCylinderMaterial);
        }
        return bondCylinderMaterial;
    }

    static Material GetOrCreatePiBondCylinderMaterial()
    {
        if (piBondCylinderMaterial != null) return piBondCylinderMaterial;
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Simple Lit");
        if (sh == null) sh = Shader.Find("Standard");
        piBondCylinderMaterial = sh != null ? new Material(sh) : null;
        if (piBondCylinderMaterial != null)
        {
            piBondCylinderMaterial.enableInstancing = true;
            ApplyPiBondVisualColorToMaterial(piBondCylinderMaterial);
        }
        return piBondCylinderMaterial;
    }

    static Mesh GetOrCreatePiLobeSphereMesh()
    {
        if (piLobeSphereMesh != null) return piLobeSphereMesh;
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        piLobeSphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        UnityEngine.Object.Destroy(tmp);
        return piLobeSphereMesh;
    }

    static void CreatePiLobeVisual(Transform parent, string name, Material mat, out GameObject go, out MeshRenderer mr)
    {
        go = new GameObject(name);
        go.transform.SetParent(parent);
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = GetOrCreatePiLobeSphereMesh();
        mr = go.AddComponent<MeshRenderer>();
        if (mat != null)
            mr.sharedMaterial = mat;
    }

    static void ApplyBondVisualColorToMaterial(Material mat)
    {
        if (mat == null) return;
        var c = BondVisualColor;
        mat.color = c;
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", c);
        // URP Lit / Simple Lit: use transparent surface so alpha is visible
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
    }

    static void ApplyPiBondVisualColorToMaterial(Material mat)
    {
        if (mat == null) return;
        var c = PiBondVisualColor;
        mat.color = c;
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", c);
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
    }

    /// <summary>Template preview pick: tint bond line/cylinder red (MPB on 3D mesh; sprite line color).</summary>
    public void SetBondFormationTemplatePickHighlight(bool highlighted)
    {
        if (!highlighted)
        {
            if (bondFormationTemplatePickMpb != null)
                bondFormationTemplatePickMpb.Clear();
            if (cylinderRenderer != null)
                cylinderRenderer.SetPropertyBlock(null);
            if (piCylinderRendererSecondary != null)
                piCylinderRendererSecondary.SetPropertyBlock(null);
            if (lineRenderer != null)
                lineRenderer.color = BondVisualColor;
            return;
        }
        var c = new Color(0.95f, 0.2f, 0.2f, 0.92f);
        if (cylinderRenderer != null)
        {
            if (bondFormationTemplatePickMpb == null)
                bondFormationTemplatePickMpb = new MaterialPropertyBlock();
            bondFormationTemplatePickMpb.Clear();
            bondFormationTemplatePickMpb.SetColor(BondShaderBaseColorId, c);
            bondFormationTemplatePickMpb.SetColor(BondShaderColorId, c);
            cylinderRenderer.SetPropertyBlock(bondFormationTemplatePickMpb);
            if (piCylinderRendererSecondary != null)
                piCylinderRendererSecondary.SetPropertyBlock(bondFormationTemplatePickMpb);
        }
        if (lineRenderer != null)
            lineRenderer.color = c;
    }

    Vector3 PerpendicularToBondDirection(Vector3 deltaNormalized)
    {
        if (useCylinderBondVisual)
        {
            Vector3 perp = Vector3.Cross(deltaNormalized, Vector3.up);
            if (perp.sqrMagnitude < 1e-8f)
                perp = Vector3.Cross(deltaNormalized, Vector3.right);
            return perp.normalized;
        }
        return Vector3.Cross(Vector3.forward, deltaNormalized).normalized;
    }

    static Quaternion BondFrameRotation(Vector3 delta, bool full3DAxis)
    {
        if (delta.sqrMagnitude < 1e-6f) return Quaternion.identity;
        var dn = delta.normalized;
        if (full3DAxis)
            return Quaternion.FromToRotation(Vector3.up, dn);
        return Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg - 90f);
    }

    public static CovalentBond Create(AtomFunction atomA, AtomFunction atomB, ElectronOrbitalFunction sharedOrbital, AtomFunction orbitalContributor, bool animateOrbitalToBond = false)
    {
        if (atomA == null || atomB == null || sharedOrbital == null) return null;
        if (atomA == atomB) return null;

        var bondGo = new GameObject("CovalentBond");
        var bond = bondGo.AddComponent<CovalentBond>();
        bond.Initialize(atomA, atomB, sharedOrbital, orbitalContributor, animateOrbitalToBond);
        atomA.RefreshElectronSyncOnBondedOrbitals();
        atomB.RefreshElectronSyncOnBondedOrbitals();
        return bond;
    }

    void Initialize(AtomFunction a, AtomFunction b, ElectronOrbitalFunction orb, AtomFunction contributor, bool animateOrbitalToBond = false)
    {
        atomA = a;
        atomB = b;
        orbital = orb;
        orbitalContributor = contributor;
        orbitalRedistributionWorldDelta = Quaternion.identity;
        useCylinderBondVisual = Camera.main != null && !Camera.main.orthographic;

        atomA.RegisterBond(this);
        atomB.RegisterBond(this);
        TryInheritCachedPerspectivePiAxisFromPartnerBond();
        int idxInit = GetBondIndex();
        if (idxInit > 0
            && atomA != null
            && atomB != null
            && atomA.TryGetPiPrebondPOrbitalPlusZWorld(atomB, out var prebondPlusZ)
            && prebondPlusZ.sqrMagnitude > 1e-18f)
        {
            Vector3 pz = prebondPlusZ.normalized;
            Vector3 mz = -pz;
            atomA.UpsertPiPOrbitalDirectionsForPartnerLine(atomB, idxInit, pz, mz);
            atomB.UpsertPiPOrbitalDirectionsForPartnerLine(atomA, idxInit, pz, mz);
        }

        orbital.SetBond(this);
        orbital.SetBondedAtom(null);
        PositionBondTransform(); // Set bond position BEFORE reparenting so orbital keeps correct world pos
        if (!animateOrbitalToBond)
            orbital.transform.SetParent(transform);
        // When animating: defer reparent until after post-Create formation so orbital keeps its current rotation and can animate to bond
        animatingOrbitalToBondPosition = animateOrbitalToBond;

        atomA.SetupIgnoreCollisions();
        atomB.SetupIgnoreCollisions();

        CreateLineVisual();
        int idx = GetBondIndex();
        bool piBondRow = idx > 0;
        // σ instant: hide shared orbital and show the bond line immediately.
        // π row: always keep the shared orbital visible through phase-2 cylinder (and orbital→line), even when
        // animateOrbitalToBond is false — callers still run the cylinder step; hiding here made OP vanish as soon as Create ran.
        if (animateOrbitalToBond || piBondRow)
        {
            orbitalVisible = true;
            orbitalToLineAnimProgress = -1f;
            showPerspectivePiWithBondOrbitalOverlap = useCylinderBondVisual && piBondRow;
        }
        else
        {
            orbitalVisible = false;
            orbitalToLineAnimProgress = -1f; // Skip animation; show line immediately
            showPerspectivePiWithBondOrbitalOverlap = false;
        }
        ApplyDisplayMode();
    }

    /// <summary>Animates the bond orbital transforming into a line over the given duration. Optionally fades out another orbital (e.g. target orbital) in parallel.</summary>
    /// <param name="atomsForPerFrameBondLineUpdate">When non-null, refreshes σ/π bond cylinders each frame (π formation keeps existing σ line aligned).</param>
    public IEnumerator AnimateOrbitalToLine(
        float duration,
        ElectronOrbitalFunction orbitalToFadeOut = null,
        ICollection<AtomFunction> atomsForPerFrameBondLineUpdate = null)
    {
        orbitalVisible = true;
        orbitalToLineAnimProgress = 0f;
        orbitalBeingFadedForCharge = orbitalToFadeOut;
        ApplyDisplayMode();

        var fadeOutStartScale = orbitalToFadeOut != null ? orbitalToFadeOut.transform.localScale : Vector3.one;

        for (float t = 0; t < duration; t += Time.deltaTime)
        {
            float s = Mathf.Clamp01(t / duration);
            s = s * s * (3f - 2f * s); // smoothstep
            orbitalToLineAnimProgress = s;
            if (orbitalToFadeOut != null)
                orbitalToFadeOut.transform.localScale = Vector3.Lerp(fadeOutStartScale, Vector3.zero, s);
            if (atomsForPerFrameBondLineUpdate != null && atomsForPerFrameBondLineUpdate.Count > 0)
                AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(atomsForPerFrameBondLineUpdate);
            yield return null;
        }

        orbitalToLineAnimProgress = -1f;
        orbitalVisible = false;
        orbitalBeingFadedForCharge = null;
        showPerspectivePiWithBondOrbitalOverlap = false;
        suppressSharedOrbitalVisualForPiHandoff = false;
        ApplyDisplayMode();
        if (orbitalToFadeOut != null)
            Destroy(orbitalToFadeOut.gameObject);
    }

    internal void SetSuppressSharedOrbitalVisualForPiHandoff(bool suppress)
    {
        suppressSharedOrbitalVisualForPiHandoff = suppress;
        ApplyDisplayMode();
    }

    /// <summary>Call during bond formation so charge uses merged count (bond orbital + fading orbital) until AnimateOrbitalToLine completes.</summary>
    public void SetOrbitalBeingFaded(ElectronOrbitalFunction orb)
    {
        orbitalBeingFadedForCharge = orb;
    }

    void CreateLineVisual()
    {
        lineVisual = new GameObject("BondLine");
        lineVisual.transform.SetParent(transform);

        bool use3DCollider = Camera.main != null && !Camera.main.orthographic;
        useCylinderBondVisual = use3DCollider;

        if (useCylinderBondVisual)
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            var mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);

            var mf = lineVisual.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            cylinderRenderer = lineVisual.AddComponent<MeshRenderer>();
            int bondIndex = GetBondIndex();
            int bondCount = atomA != null && atomB != null ? atomA.GetBondsTo(atomB) : 1;
            bool usePiStyle = bondIndex > 0 && bondCount > 1;
            var mat = usePiStyle ? GetOrCreatePiBondCylinderMaterial() : GetOrCreateBondCylinderMaterial();
            if (mat != null)
                cylinderRenderer.sharedMaterial = mat;

            lineCollider3D = lineVisual.AddComponent<BoxCollider>();
            lineCollider3D.isTrigger = true;
            lineCollider3D.size = new Vector3(2f, 2f, 2f);
            lineCollider3D.center = Vector3.zero;

            if (usePiStyle)
            {
                piCylinderSecondaryVisual = new GameObject("BondLinePiSecondary");
                piCylinderSecondaryVisual.transform.SetParent(transform);
                var mfSecondary = piCylinderSecondaryVisual.AddComponent<MeshFilter>();
                mfSecondary.sharedMesh = mesh;
                piCylinderRendererSecondary = piCylinderSecondaryVisual.AddComponent<MeshRenderer>();
                if (mat != null)
                    piCylinderRendererSecondary.sharedMaterial = mat;

                CreatePiLobeVisual(transform, "PiLobePosA", mat, out piLobePositiveAVisual, out piLobePositiveARenderer);
                CreatePiLobeVisual(transform, "PiLobePosB", mat, out piLobePositiveBVisual, out piLobePositiveBRenderer);
                CreatePiLobeVisual(transform, "PiLobeNegA", mat, out piLobeNegativeAVisual, out piLobeNegativeARenderer);
                CreatePiLobeVisual(transform, "PiLobeNegB", mat, out piLobeNegativeBVisual, out piLobeNegativeBRenderer);

                // Match primary cylinder: −π lobe had no collider / forwarder, so −Z side could not start drag or break like +Z.
                var colSecondary = piCylinderSecondaryVisual.AddComponent<BoxCollider>();
                colSecondary.isTrigger = true;
                colSecondary.size = new Vector3(2f, 2f, 2f);
                colSecondary.center = Vector3.zero;
                piCylinderSecondaryVisual.AddComponent<BondLineColliderForwarder>();
            }
        }
        else
        {
            lineRenderer = lineVisual.AddComponent<SpriteRenderer>();
            lineRenderer.sprite = GetOrCreateLineSprite();
            lineRenderer.color = BondVisualColor;
            lineRenderer.sortingOrder = 0;
            lineRenderer.sortingLayerID = 0;

            lineCollider = lineVisual.AddComponent<BoxCollider2D>();
            lineCollider.isTrigger = true;
        }

        if (use3DCollider && !useCylinderBondVisual)
        {
            lineCollider3D = lineVisual.AddComponent<BoxCollider>();
            lineCollider3D.isTrigger = true;
        }

        lineVisual.AddComponent<BondLineColliderForwarder>();
    }

    /// <summary>Switch cylinder vs sprite bond when toggling orthographic / perspective camera.</summary>
    public void RefreshLineVisualForCameraMode()
    {
        bool wantCylinder = Camera.main != null && !Camera.main.orthographic;
        if (wantCylinder == useCylinderBondVisual && lineVisual != null)
        {
            UpdateBondTransformToCurrentAtoms();
            PositionBondTransform();
            return;
        }

        if (lineVisual != null)
            Destroy(lineVisual);
        if (piCylinderSecondaryVisual != null)
            Destroy(piCylinderSecondaryVisual);
        if (piLobePositiveAVisual != null)
            Destroy(piLobePositiveAVisual);
        if (piLobePositiveBVisual != null)
            Destroy(piLobePositiveBVisual);
        if (piLobeNegativeAVisual != null)
            Destroy(piLobeNegativeAVisual);
        if (piLobeNegativeBVisual != null)
            Destroy(piLobeNegativeBVisual);

        lineVisual = null;
        lineRenderer = null;
        cylinderRenderer = null;
        piCylinderRendererSecondary = null;
        piCylinderSecondaryVisual = null;
        piLobePositiveAVisual = null;
        piLobePositiveBVisual = null;
        piLobeNegativeAVisual = null;
        piLobeNegativeBVisual = null;
        piLobePositiveARenderer = null;
        piLobePositiveBRenderer = null;
        piLobeNegativeARenderer = null;
        piLobeNegativeBRenderer = null;
        lineCollider = null;
        lineCollider3D = null;

        CreateLineVisual();
        PositionBondTransform();
        ApplyDisplayMode();
    }

    static Sprite GetOrCreateLineSprite()
    {
        if (lineSprite != null) return lineSprite;
        var tex = new Texture2D(16, 32);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 16; x++)
                tex.SetPixel(x, y, Color.white);
        tex.Apply();
        lineSprite = Sprite.Create(tex, new Rect(0, 0, 16, 32), new Vector2(0.5f, 0.5f), 32f);
        return lineSprite;
    }

    void PositionBondTransform()
    {
        if (atomA == null || atomB == null) return;
        var posA = atomA.transform.position;
        var posB = atomB.transform.position;
        var center = (posA + posB) * 0.5f;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;
        transform.position = center;
        transform.rotation = BondFrameRotation(delta, useCylinderBondVisual);
    }

    void LateUpdate()
    {
        if (atomA == null || atomB == null) return;

        var posA = atomA.transform.position;
        var posB = atomB.transform.position;
        var center = (posA + posB) * 0.5f;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;

        if (!animatingOrbitalToBondPosition)
        {
            transform.position = center;
            transform.rotation = BondFrameRotation(delta, useCylinderBondVisual);
        }

        int bondIndex = GetBondIndex();
        int bondCount = atomA.GetBondsTo(atomB);
        float offset = GetLineOffset(bondIndex, bondCount);
        var perpendicular = PerpendicularToBondDirection(delta / distance);
        PerspectivePiLobeEndpoints piLobes = default;
        bool usePerspectivePiLobes = useCylinderBondVisual
            && bondIndex > 0
            && TryGetPerspectivePiLobeEndpointsForLine(bondIndex, bondCount, out piLobes);
        // Each bond has its own lineVisual + collider; position this bond's line at its offset (not shared)
        lineVisual.transform.position = usePerspectivePiLobes ? piLobes.positiveCenter : center + perpendicular * offset;
        lineVisual.transform.localRotation = Quaternion.identity;
        float lineDistance = usePerspectivePiLobes ? piLobes.lineDistance : distance;
        float lineLength = usePerspectivePiLobes
            ? Mathf.Max(0.1f, lineDistance) // PI rule: cylinder spans atom-to-atom distance.
            : Mathf.Max(0.1f, lineDistance * 0.5f);
        float lineScaleMult = orbitalToLineAnimProgress < 0 ? 1f : orbitalToLineAnimProgress;
        if (useCylinderBondVisual)
        {
            float radiusScale = 0.15f;
            if (usePerspectivePiLobes)
                radiusScale = 0.6f; // Match existing orbital body diameter (orbital localScale 0.6).
            float lobeDiameter = usePerspectivePiLobes ? radiusScale : 2f * radiusScale;
            float lineLengthForScale = lineLength;
            float halfHeight = 0.5f * lineLengthForScale * lineScaleMult;
            lineVisual.transform.localScale = new Vector3(radiusScale, Mathf.Max(0.001f, halfHeight), radiusScale);
            if (piCylinderSecondaryVisual != null && piCylinderRendererSecondary != null)
            {
                piCylinderSecondaryVisual.transform.position = usePerspectivePiLobes ? piLobes.negativeCenter : lineVisual.transform.position;
                piCylinderSecondaryVisual.transform.localRotation = Quaternion.identity;
                piCylinderSecondaryVisual.transform.localScale = usePerspectivePiLobes
                    ? lineVisual.transform.localScale
                    : Vector3.zero;
                piCylinderRendererSecondary.enabled = usePerspectivePiLobes && (cylinderRenderer == null || cylinderRenderer.enabled);
            }
            bool showPiLobes = usePerspectivePiLobes
                && piLobePositiveAVisual != null
                && piLobePositiveBVisual != null
                && piLobeNegativeAVisual != null
                && piLobeNegativeBVisual != null;
            if (showPiLobes)
            {
                Vector3 lobeScale = new Vector3(lobeDiameter, lobeDiameter, lobeDiameter);
                piLobePositiveAVisual.transform.position = piLobes.positiveA;
                piLobePositiveBVisual.transform.position = piLobes.positiveB;
                piLobeNegativeAVisual.transform.position = piLobes.negativeA;
                piLobeNegativeBVisual.transform.position = piLobes.negativeB;
                piLobePositiveAVisual.transform.localScale = lobeScale;
                piLobePositiveBVisual.transform.localScale = lobeScale;
                piLobeNegativeAVisual.transform.localScale = lobeScale;
                piLobeNegativeBVisual.transform.localScale = lobeScale;
            }
            else
            {
                if (piLobePositiveAVisual != null) piLobePositiveAVisual.transform.localScale = Vector3.zero;
                if (piLobePositiveBVisual != null) piLobePositiveBVisual.transform.localScale = Vector3.zero;
                if (piLobeNegativeAVisual != null) piLobeNegativeAVisual.transform.localScale = Vector3.zero;
                if (piLobeNegativeBVisual != null) piLobeNegativeBVisual.transform.localScale = Vector3.zero;
            }
        }
        else
            lineVisual.transform.localScale = new Vector3(0.25f, lineLength * lineScaleMult, 1f);

        // Collider size = sprite base size (0.5 x 1) so it scales with the transform and matches the line
        if (lineCollider != null)
        {
            lineCollider.size = new Vector2(0.5f, 1f);
            lineCollider.offset = Vector2.zero;
        }
        if (lineCollider3D != null)
        {
            if (useCylinderBondVisual)
            {
                // Wider than the visible cylinder so raycasts are easier to land; Y matches default cylinder mesh height.
                lineCollider3D.size = new Vector3(2f, 2f, 2f);
                lineCollider3D.center = Vector3.zero;
            }
            else
            {
                lineCollider3D.size = new Vector3(0.5f, 1f, 0.2f);
                lineCollider3D.center = Vector3.zero;
            }
        }

        // Position bond orbital at same world position as lineVisual (center + perpendicular * offset)
        // Skip when user is dragging or when animating orbital to bond (post-Create formation)
        bool userDragging = orbitalVisible && orbitalToLineAnimProgress < 0;
        if (orbital != null && !userDragging && !animatingOrbitalToBondPosition && !suppressSigmaPrebondBondFrameOrbitalPose)
        {
            var orbWorldPos = usePerspectivePiLobes ? piLobes.positiveCenter : center + perpendicular * offset;
            orbital.transform.position = orbWorldPos;
            var baseOrbRot = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
            if (orbitalRotationFlipped) baseOrbRot = baseOrbRot * Quaternion.Euler(0f, 0f, 180f);
            var orbRot = orbitalRedistributionWorldDelta * baseOrbRot;
            orbital.transform.rotation = orbRot;
            float orbScale = orbitalToLineAnimProgress < 0 ? 0.6f : 0.6f * (1f - orbitalToLineAnimProgress);
            orbital.transform.localScale = Vector3.one * Mathf.Max(0.01f, orbScale);
        }
    }

    void ApplyDisplayMode()
    {
        if (orbital != null)
        {
            orbital.SetVisualsEnabled(orbitalVisible && !suppressSharedOrbitalVisualForPiHandoff);
            orbital.SetPointerBlocked(true);
        }
        bool animating = orbitalToLineAnimProgress >= 0 && orbitalToLineAnimProgress < 1f;
        // σ: hide line while the bond orbital is shown until orbital-to-line (original rule).
        // π row: also show π cylinders/lobes while the shared orbital is visible pre–orbital-to-line so they overlap
        // operation orbitals during π phase-2 cylinder lerp (same frame continuity vs OP meshes).
        bool showLine = !orbitalVisible || animating
            || (showPerspectivePiWithBondOrbitalOverlap && orbitalVisible && orbitalToLineAnimProgress < 0f)
            || suppressSharedOrbitalVisualForPiHandoff;
        if (lineRenderer != null) lineRenderer.enabled = showLine;
        if (cylinderRenderer != null) cylinderRenderer.enabled = showLine;
        if (piCylinderRendererSecondary != null) piCylinderRendererSecondary.enabled = showLine;
        if (piLobePositiveARenderer != null) piLobePositiveARenderer.enabled = showLine;
        if (piLobePositiveBRenderer != null) piLobePositiveBRenderer.enabled = showLine;
        if (piLobeNegativeARenderer != null) piLobeNegativeARenderer.enabled = showLine;
        if (piLobeNegativeBRenderer != null) piLobeNegativeBRenderer.enabled = showLine;
        if (lineCollider != null) lineCollider.enabled = !orbitalVisible && orbitalToLineAnimProgress < 0;
        if (lineCollider3D != null) lineCollider3D.enabled = !orbitalVisible && orbitalToLineAnimProgress < 0;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (BondFormationDebugController.IsWaitingForPhase)
            return;
        if (orbitalVisible) return;
        if (atomA == null || atomB == null) return;
        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        var delta = second.transform.position - first.transform.position;
        var distance = delta.magnitude;
        if (distance < 0.001f) return;
        var dir = delta / distance;
        var right = PerpendicularToBondDirection(dir);
        int bondIndex = GetBondIndex();
        int bondCount = atomA.GetBondsTo(atomB);
        float offset = GetLineOffset(bondIndex, bondCount);
        var lineCenter = (atomA.transform.position + atomB.transform.position) * 0.5f + right * offset;
        var lineStart = lineCenter - dir * (distance * 0.5f);
        var lineEnd = lineCenter + dir * (distance * 0.5f);
        if (!PassesBondPointerSlop(eventData, lineStart, lineEnd))
            return;
        orbitalVisible = true;
        forwardedPressToOrbital = false;
        storedPressData = eventData;
        bondLinePointerDownScreen = eventData.position;
        ApplyDisplayMode();
        // Bond-line pose before any drag so the shared orbital does not start from a stale world pose.
        UpdateBondTransformToCurrentAtoms();
        SnapOrbitalToBondPosition();
        // π row: show the bonding orbital at the pointer (perspective +Z center is wrong when grabbing −Z or the line).
        if (GetBondIndex() > 0 && TryGetPointerWorldOnBondDragPlane(bondLinePointerDownScreen, out var piPointerWorld))
            orbital.transform.position = piPointerWorld;
    }

    static bool TryGetPointerWorldOnBondDragPlane(Vector2 screenPosition, out Vector3 worldPos)
    {
        var cam = Camera.main;
        if (cam != null && MoleculeWorkPlane.Instance != null &&
            MoleculeWorkPlane.Instance.TryGetWorldPoint(cam, screenPosition, out worldPos))
            return true;
        worldPos = PlanarPointerInteraction.ScreenToWorldPoint(screenPosition);
        return true;
    }

    static float DistanceToLineSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var ap = p - a;
        var t = Mathf.Clamp01(Vector3.Dot(ap, ab) / (ab.sqrMagnitude + 0.0001f));
        var nearest = a + t * ab;
        return Vector3.Distance(p, nearest);
    }

    static bool PointerHitIsBondLineVisualPick(GameObject hitGo, GameObject primaryLine, GameObject piSecondary)
    {
        if (hitGo == null || primaryLine == null) return false;
        var t = hitGo.transform;
        if (hitGo == primaryLine || t == primaryLine.transform || t.IsChildOf(primaryLine.transform))
            return true;
        if (piSecondary != null
            && (hitGo == piSecondary || t == piSecondary.transform || t.IsChildOf(piSecondary.transform)))
            return true;
        return false;
    }

    /// <summary>
    /// 2D bonds: click must be near the segment on the work plane. 3D cylinder: planar projection is wrong for
    /// out-of-plane bonds — trust the physics hit on this bond's line mesh, or measure ray vs segment in 3D.
    /// </summary>
    bool PassesBondPointerSlop(PointerEventData eventData, Vector3 lineStart, Vector3 lineEnd)
    {
        if (useCylinderBondVisual)
        {
            var hitGo = eventData.pointerCurrentRaycast.gameObject;
            if (PointerHitIsBondLineVisualPick(hitGo, lineVisual, piCylinderSecondaryVisual))
                return true;
            var cam = Camera.main;
            if (cam != null && !cam.orthographic)
            {
                var ray = cam.ScreenPointToRay(eventData.position);
                if (ApproxDistanceRayToSegment(ray, lineStart, lineEnd) <= 0.45f)
                    return true;
                int bi = GetBondIndex();
                int bc = atomA != null && atomB != null ? atomA.GetBondsTo(atomB) : 1;
                if (bi > 0 && TryGetPerspectivePiLobeEndpointsForLine(bi, bc, out var pl))
                {
                    if (ApproxDistanceRayToSegment(ray, pl.positiveA, pl.positiveB) <= 0.45f) return true;
                    if (ApproxDistanceRayToSegment(ray, pl.negativeA, pl.negativeB) <= 0.45f) return true;
                }
            }
            return false;
        }

        var clickWorld = PlanarPointerInteraction.ScreenToWorldPoint(eventData.position);
        return DistanceToLineSegment(clickWorld, lineStart, lineEnd) <= 0.25f;
    }

    static float ApproxDistanceRayToSegment(Ray ray, Vector3 a, Vector3 b)
    {
        float best = float.MaxValue;
        for (int i = 0; i <= 12; i++)
        {
            float t = i / 12f;
            var p = Vector3.Lerp(a, b, t);
            float proj = Vector3.Dot(p - ray.origin, ray.direction);
            if (proj < 0f) proj = 0f;
            var onRay = ray.origin + ray.direction * proj;
            float d = Vector3.Distance(onRay, p);
            if (d < best) best = d;
        }
        return best;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (BondFormationDebugController.IsWaitingForPhase)
        {
            if (orbitalVisible)
                ReturnToLineView();
            return;
        }
        if (!orbitalVisible || orbital == null) return;
        if (!forwardedPressToOrbital)
        {
            UpdateBondTransformToCurrentAtoms();
            // π: keep orbital at press/drag plane (already under pointer on down); σ still snaps from line.
            if (GetBondIndex() <= 0)
                SnapOrbitalToBondPosition();
            forwardedPressToOrbital = true;
            var savedScreen = storedPressData.position;
            storedPressData.position = bondLinePointerDownScreen;
            orbital.OnPointerDown(storedPressData);
            storedPressData.position = savedScreen;
        }
        orbital.OnDrag(eventData);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (BondFormationDebugController.IsWaitingForPhase)
        {
            if (orbitalVisible)
                ReturnToLineView();
            return;
        }
        if (!orbitalVisible) return;
        if (forwardedPressToOrbital && orbital != null)
            orbital.OnPointerUp(eventData);
        else
            ReturnToLineView();
    }

    public void ReturnToLineView()
    {
        orbitalVisible = false;
        ApplyDisplayMode();
    }

    public void NotifyElectronCountChanged()
    {
        ApplyDisplayMode(); // Re-apply so newly created electrons respect orbitalVisible (hidden when bond shows as line)
        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();
    }

    /// <param name="userDragBondCylinderBreak">When true (bond broken by dragging the bond orbital), both bonding electrons stay on the dragged atom's orbital.</param>
    /// <param name="instantRedistributionForDestroyPartner">When true, skip post-break <see cref="OrbitalRedistribution.BuildOrbitalRedistribution"/> lerp (pure σ-break layout lerp) so callers can keep nuclear/orbital framing (e.g. edit-mode replace-H / destroy-bridge before attach). Default false runs redistribution after ex-bond lobes are placed.</param>
    public void BreakBond(AtomFunction returnOrbitalTo, bool userDragBondCylinderBreak = false, bool instantRedistributionForDestroyPartner = false)
    {
        if (orbital == null) return;

        int bondsBetweenPairBeforeBreak = atomA != null && atomB != null ? atomA.GetBondsTo(atomB) : 0;
        int breakBondRowIndex = GetBondIndex();

        // Bond-line pose for orbitals (must capture before UnregisterBond — GetOrbitalTargetWorldState uses bond topology).
        var (bondOrbitalWorldPos, bondOrbitalWorldRot) = GetOrbitalTargetWorldState();
        AtomPoseDirectionDebugLog.LogBondBreakCaptureTargetState(
            "pre_unregister_capture_target_state",
            GetInstanceID(),
            atomA,
            atomB,
            orbital,
            bondOrbitalWorldPos,
            bondOrbitalWorldRot,
            orbitalRotationFlipped,
            GetOrbitalRedistributionWorldDeltaForDiagnostics());
        AtomPoseDirectionDebugLog.LogBondBreakOrbitalPose(
            "capture",
            "H-break-capture",
            GetInstanceID(),
            atomA,
            atomB,
            orbital,
            null,
            bondOrbitalWorldPos,
            bondOrbitalWorldRot,
            userDragBondCylinderBreak,
            instantRedistributionForDestroyPartner);

        // π break: remove the broken row axis; any remaining π slots become the single source for redistribution alignment.
        if (breakBondRowIndex > 0 && atomA != null && atomB != null)
        {
            int bid = atomB.GetInstanceID();
            var aSlots = atomA.PiPOrbitalDirectionSlots;
            int effectiveRemovedRow = breakBondRowIndex;
            bool hasRequestedRow = false;
            int highestCommittedRow = 0;
            for (int i = 0; i < aSlots.Count; i++)
            {
                var s = aSlots[i];
                if (s.Partner == null || s.Partner.GetInstanceID() != bid)
                    continue;
                if (s.LineIndex == breakBondRowIndex)
                    hasRequestedRow = true;
                if (s.LineIndex > highestCommittedRow)
                    highestCommittedRow = s.LineIndex;
            }
            // If bond row index and stored slot row diverge, remove the highest committed row as the broken π slot.
            if (!hasRequestedRow && highestCommittedRow > 0)
                effectiveRemovedRow = highestCommittedRow;
            atomA.RemovePiPOrbitalDirectionsForPartnerLine(atomB, effectiveRemovedRow);
            atomB.RemovePiPOrbitalDirectionsForPartnerLine(atomA, effectiveRemovedRow);
        }

        // Clear cached perspective π axis for this pair so re-formation does not reuse stale pre-break orientation.
        cachedPerspectivePiAxisWorld = null;
        if (atomA != null && atomB != null)
        {
            foreach (var cb in atomA.CovalentBonds)
            {
                if (cb == null) continue;
                var other = cb.AtomA == atomA ? cb.AtomB : cb.AtomA;
                if (other != atomB) continue;
                cb.cachedPerspectivePiAxisWorld = null;
            }
        }

        atomA?.UnregisterBond(this);
        atomB?.UnregisterBond(this);

        // Animated σ/π formation: shared bond orbital may not yet have merged ElectronCount (orbital-to-line applies merged);
        // partner lobe is still fading under bond with the remaining valence. Breaking mid-formation must count both.
        ElectronOrbitalFunction fadeOrbForBreak = orbitalBeingFadedForCharge;
        orbitalBeingFadedForCharge = null;
        int totalElectrons = orbital.ElectronCount;
        if (fadeOrbForBreak != null)
            totalElectrons += fadeOrbForBreak.ElectronCount;
        var otherAtom = returnOrbitalTo == atomA ? atomB : atomA;
        var prefab = returnOrbitalTo.OrbitalPrefab ?? otherAtom?.OrbitalPrefab;
        if (prefab == null) return;

        orbital.SetBond(null);
        orbital.SetBondedAtom(returnOrbitalTo);
        orbital.SetPointerBlocked(false);
        orbital.SetVisualsEnabled(true);
        // Former bond axis: returned lobe points toward the other atom (guide fallback when no π and no multi-bond ref).
        var dirToOther = (otherAtom.transform.position - returnOrbitalTo.transform.position).normalized;
        if (dirToOther.sqrMagnitude < 0.01f) dirToOther = Vector3.right;
        var slotA = returnOrbitalTo.GetSlotForNewOrbital(dirToOther, orbital);
        orbital.transform.SetParent(returnOrbitalTo.transform);
        orbital.transform.localScale = Vector3.one * 0.6f;
        ElectronOrbitalFunction newOrbital;
        var dirToReturn = (returnOrbitalTo.transform.position - otherAtom.transform.position).normalized;
        if (dirToReturn.sqrMagnitude < 0.01f) dirToReturn = Vector3.left;

        (Vector3 position, Quaternion rotation) slotB = default;
        if (userDragBondCylinderBreak)
        {
            int maxOnReturn = ElectronOrbitalFunction.MaxElectrons;
            orbital.ElectronCount = Mathf.Clamp(totalElectrons, 0, maxOnReturn);
            int spill = Mathf.Max(0, totalElectrons - orbital.ElectronCount);
            returnOrbitalTo.BondOrbital(orbital);

            newOrbital = Instantiate(prefab);
            newOrbital.transform.localScale = Vector3.one * 0.6f;
            slotB = otherAtom.GetSlotForNewOrbital(dirToReturn, newOrbital);
            newOrbital.transform.SetParent(otherAtom.transform);
            newOrbital.ElectronCount = Mathf.Clamp(spill, 0, maxOnReturn);
            newOrbital.SetBondedAtom(otherAtom);
            otherAtom.BondOrbital(newOrbital);
        }
        else
        {
            orbital.ElectronCount = totalElectrons > 0 ? totalElectrons - 1 : 0;
            returnOrbitalTo.BondOrbital(orbital);

            newOrbital = Instantiate(prefab);
            newOrbital.transform.localScale = Vector3.one * 0.6f;
            slotB = otherAtom.GetSlotForNewOrbital(dirToReturn, newOrbital);
            newOrbital.transform.SetParent(otherAtom.transform);
            newOrbital.ElectronCount = totalElectrons > 0 ? 1 : 0;
            newOrbital.SetBondedAtom(otherAtom);
            otherAtom.BondOrbital(newOrbital);
        }

        AtomPoseDirectionDebugLog.LogBondBreakOtherSideSlotPick(
            "post_slot_pick_pre_world_apply",
            GetInstanceID(),
            returnOrbitalTo,
            otherAtom,
            orbital,
            newOrbital,
            dirToReturn,
            slotB.position,
            slotB.rotation);
        AtomPoseDirectionDebugLog.LogBondBreakTargetVsChosenSlot(
            "post_slot_pick_pre_world_apply",
            GetInstanceID(),
            otherAtom,
            returnOrbitalTo,
            bondOrbitalWorldRot,
            slotB.rotation,
            orbitalRotationFlipped);

        if (fadeOrbForBreak != null && fadeOrbForBreak != orbital)
            Destroy(fadeOrbForBreak.gameObject);

        // If a lone pair is released while another empty non-bond exists, split 2e→1e+1e first.
        atomA?.TryTransferElectronFromLonePairToEmptyOrbitals();
        atomB?.TryTransferElectronFromLonePairToEmptyOrbitals();
        atomA?.RefreshCharge();
        atomB?.RefreshCharge();

        // π row: GetOrbitalTargetWorldState uses perspective π lobe centers (offset from nuclei). Freed orbitals must use
        // VSEPR slots (slotA/slotB) in local space — same rule as σ — or they appear ~1 lobe radius too far (logs H1/H2).
        if (breakBondRowIndex > 0)
        {
            orbital.transform.localPosition = slotA.position;
            orbital.transform.localRotation = slotA.rotation;
            newOrbital.transform.localPosition = slotB.position;
            newOrbital.transform.localRotation = slotB.rotation;
        }
        else
        {
            orbital.transform.SetPositionAndRotation(bondOrbitalWorldPos, bondOrbitalWorldRot);
            newOrbital.transform.SetPositionAndRotation(bondOrbitalWorldPos, bondOrbitalWorldRot);
        }
        AtomPoseDirectionDebugLog.LogBondBreakOrbitalPose(
            "after_world_apply",
            "H-break-apply",
            GetInstanceID(),
            atomA,
            atomB,
            orbital,
            newOrbital,
            bondOrbitalWorldPos,
            bondOrbitalWorldRot,
            userDragBondCylinderBreak,
            instantRedistributionForDestroyPartner);
        AtomPoseDirectionDebugLog.LogBondBreakOtherSideOverwriteVsChosenSlot(
            "after_world_apply",
            GetInstanceID(),
            otherAtom,
            returnOrbitalTo,
            newOrbital,
            slotB.rotation);

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();
        atomA?.SetInteractionBlocked(false);
        atomB?.SetInteractionBlocked(false);

        void FinishBreakBondTail()
        {
            AtomPoseDirectionDebugLog.LogBondBreakOrbitalPose(
                "finish_tail",
                "H-break-final",
                GetInstanceID(),
                atomA,
                atomB,
                orbital,
                newOrbital,
                null,
                null,
                userDragBondCylinderBreak,
                instantRedistributionForDestroyPartner);
            // UnityEngine.Object: ?. uses reference null only — destroyed AtomFunction still invokes and can touch stale orbitals.
            if (atomA != null) atomA.RefreshElectronSyncOnBondedOrbitals();
            if (atomB != null) atomB.RefreshElectronSyncOnBondedOrbitals();
            var atomsForSigmaLine = new List<AtomFunction>();
            if (atomA != null) atomsForSigmaLine.Add(atomA);
            if (atomB != null) atomsForSigmaLine.Add(atomB);
            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(atomsForSigmaLine);
            AtomFunction.SetupGlobalIgnoreCollisions();

            orbital = null;
            Destroy(gameObject);
        }

        if (!instantRedistributionForDestroyPartner && atomA != null && atomB != null)
        {
            bool breakingPiLine = !IsSigmaBondLine();
            ElectronOrbitalFunction antiGuideA;
            ElectronOrbitalFunction antiGuideB;
            if (breakingPiLine)
            {
                // π-only break: keep redistribution anchored to the released ex-bond orbital on each endpoint.
                antiGuideA = returnOrbitalTo == atomA ? orbital : newOrbital;
                antiGuideB = returnOrbitalTo == atomB ? orbital : newOrbital;
            }
            else
            {
                antiGuideA = otherAtom == atomA && newOrbital.ElectronCount == 0 ? newOrbital : null;
                antiGuideB = otherAtom == atomB && newOrbital.ElectronCount == 0 ? newOrbital : null;
            }

            // Single bond between the two atoms (full cleavage): redistribute both fragments. π break or any multiply-bonded pair: heavier substituent only.
            bool runBothFragments = bondsBetweenPairBeforeBreak <= 1 && !breakingPiLine;
            bool cyclicSigmaRingBondBreak =
                runBothFragments && SigmaBondFormation.TryGetCyclicSigmaBondBreakRingSize(this, out _);

            List<AtomFunction> ringPathOrderedC1ToCn = null;
            if (cyclicSigmaRingBondBreak)
                SigmaBondFormation.TryGetSigmaShortestPathBetween(atomA, atomB, out ringPathOrderedC1ToCn);

            // Shortest path is always atomA→atomB; cyclic σ break naming wants C1 = 2e redistribution recipient, not 0e.
            if (ringPathOrderedC1ToCn != null && ringPathOrderedC1ToCn.Count >= 3)
            {
                AtomFunction twoERecipient = ResolveCyclicSigmaBreakRedistributionRecipientAtom(
                    returnOrbitalTo, otherAtom, orbital, newOrbital);
                int last = ringPathOrderedC1ToCn.Count - 1;
                if (twoERecipient != null
                    && ringPathOrderedC1ToCn[0] != twoERecipient
                    && ringPathOrderedC1ToCn[last] == twoERecipient)
                    ringPathOrderedC1ToCn.Reverse();
            }

            bool debugSteppedChainVisualization =
                cyclicSigmaRingBondBreak
                && ringPathOrderedC1ToCn != null
                && ringPathOrderedC1ToCn.Count >= 3
                && OrbitalRedistribution.DebugCyclicSigmaBondBreakSteppedTemplate
                && BondFormationDebugController.SteppedModeEnabled;

            OrbitalRedistribution.CyclicSigmaChainAtomAnimation cyclicChainAtomAnim = null;
            if (cyclicSigmaRingBondBreak
                && ringPathOrderedC1ToCn != null
                && ringPathOrderedC1ToCn.Count >= 3
                && !debugSteppedChainVisualization)
            {
                OrbitalRedistribution.TryBuildCyclicSigmaBondBreakStaggeredChainAtomAnimation(
                    ringPathOrderedC1ToCn,
                    out cyclicChainAtomAnim);
            }

            OrbitalRedistribution.RedistributionAnimation animA = null;
            OrbitalRedistribution.RedistributionAnimation animB = null;
            bool applySecondaryFragmentRedistribution = false;
            int? piPOrbitalSlotLineToRemoveAfterRedistribution = null;
            AtomFunction cyclicSigmaBreakRecipient = null;
            AtomFunction cyclicSigmaBreakPartner = null;
            ElectronOrbitalFunction cyclicSigmaBreakAntiRecipient = null;
            OrbitalRedistribution.CyclicRedistributionContext cyclicSigmaBreakRedistContext = null;
            if (runBothFragments)
            {
                if (cyclicSigmaRingBondBreak)
                {
                    cyclicSigmaBreakRecipient = ResolveCyclicSigmaBreakRedistributionRecipientAtom(
                        returnOrbitalTo, otherAtom, orbital, newOrbital);
                    cyclicSigmaBreakPartner = cyclicSigmaBreakRecipient == atomA ? atomB : atomA;
                    cyclicSigmaBreakAntiRecipient = cyclicSigmaBreakRecipient == atomA ? antiGuideA : antiGuideB;
                    cyclicSigmaBreakRedistContext =
                        OrbitalRedistribution.CreateCyclicSigmaBondBreakRedistributionBlockContext(
                            ringPathOrderedC1ToCn);
                    animA = OrbitalRedistribution.BuildOrbitalRedistribution(
                        cyclicSigmaBreakRecipient,
                        cyclicSigmaBreakPartner,
                        guideAtomOrbitalOp: null,
                        atomOrbitalOp: cyclicSigmaBreakAntiRecipient,
                        guideOrbitalPredetermined: null,
                        finalDirectionForGuideOrbital: default,
                        isBondingEvent: false,
                        cyclicContext: cyclicSigmaBreakRedistContext);
                    animB = null;
                    applySecondaryFragmentRedistribution = false;
                }
                else
                {
                    animA = OrbitalRedistribution.BuildOrbitalRedistribution(atomA, atomB, null, antiGuideA, isBondingEvent: false);
                    animB = OrbitalRedistribution.BuildOrbitalRedistribution(atomB, atomA, null, antiGuideB, isBondingEvent: false);
                    applySecondaryFragmentRedistribution = true;
                }
            }
            else
            {
                float massSideA = ElectronRedistributionGuide.SumSubstituentMassThroughSigmaEdge(atomA, atomB);
                float massSideB = ElectronRedistributionGuide.SumSubstituentMassThroughSigmaEdge(atomB, atomA);
                AtomFunction heavy = massSideA > massSideB + 1e-4f ? atomA
                    : massSideB > massSideA + 1e-4f ? atomB
                    : returnOrbitalTo;
                AtomFunction partner = heavy == atomA ? atomB : atomA;
                var antiHeavy = heavy == atomA ? antiGuideA : antiGuideB;

                animA = OrbitalRedistribution.BuildOrbitalRedistribution(
                    heavy,
                    partner,
                    null,
                    antiHeavy,
                    isBondingEvent: false,
                    cyclicContext: null);
                applySecondaryFragmentRedistribution = false;
            }

            if (debugSteppedChainVisualization)
            {
                atomA.StartCoroutine(CoBondBreakCyclicStaggerChainSteppedDebugThenRedistribute(
                    ringPathOrderedC1ToCn,
                    cyclicSigmaBreakRecipient,
                    cyclicSigmaBreakPartner,
                    cyclicSigmaBreakAntiRecipient,
                    cyclicSigmaBreakRedistContext,
                    animA,
                    animB,
                    applySecondaryFragmentRedistribution,
                    FinishBreakBondTail));
                return;
            }

            atomA.StartCoroutine(CoLerpBondBreakRedistributionFromAnimations(
                animA,
                animB,
                applySecondaryFragmentRedistribution,
                FinishBreakBondTail,
                cyclicChainAtomAnim,
                piPOrbitalSlotLineToRemoveAfterRedistribution));
            return;
        }

        FinishBreakBondTail();
    }

    /// <summary>
    /// Cyclic σ bond break: run <see cref="OrbitalRedistribution.BuildOrbitalRedistribution"/> once on the fragment that
    /// keeps the larger electron count on the cleaved σ lobes (2e drag-cleave on <paramref name="returnOrbitalTo"/> vs 0e
    /// on <paramref name="otherAtom"/>); 1+1 homolytic tie-breaks to <paramref name="returnOrbitalTo"/>.
    /// </summary>
    static AtomFunction ResolveCyclicSigmaBreakRedistributionRecipientAtom(
        AtomFunction returnOrbitalTo,
        AtomFunction otherAtom,
        ElectronOrbitalFunction returnSideExBondLobe,
        ElectronOrbitalFunction otherSideNewLobe)
    {
        if (returnOrbitalTo == null) return otherAtom;
        if (otherAtom == null) return returnOrbitalTo;
        int eRet = returnSideExBondLobe != null ? returnSideExBondLobe.ElectronCount : 0;
        int eOth = otherSideNewLobe != null ? otherSideNewLobe.ElectronCount : 0;
        if (eRet > eOth) return returnOrbitalTo;
        if (eOth > eRet) return otherAtom;
        return returnOrbitalTo;
    }

    const float BondBreakRedistributionDuration = 0.65f;

    /// <summary>
    /// Stepped cyclic σ-chain: builds stagger targets invisibly, pauses on HUD Next for redistribution template preview, then bond-break lerp.
    /// </summary>
    IEnumerator CoBondBreakCyclicStaggerChainSteppedDebugThenRedistribute(
        List<AtomFunction> ringPathOrderedC1ToCn,
        AtomFunction cyclicSigmaBreakRecipient,
        AtomFunction cyclicSigmaBreakPartner,
        ElectronOrbitalFunction cyclicSigmaBreakAntiRecipient,
        global::OrbitalRedistribution.CyclicRedistributionContext cyclicSigmaBreakRedistContext,
        global::OrbitalRedistribution.RedistributionAnimation animPrimary,
        global::OrbitalRedistribution.RedistributionAnimation animSecondary,
        bool applySecondary,
        Action onComplete)
    {
        global::OrbitalRedistribution.CyclicSigmaChainAtomAnimation chainAnim = null;
        if (atomA == null || atomB == null)
        {
            onComplete?.Invoke();
            yield break;
        }

        global::OrbitalRedistribution.ClearCyclicSigmaBondBreakTemplateDebugVisuals();
        int n = ringPathOrderedC1ToCn.Count;
        var targetWorld = new Vector3[n];
        for (int i = 0; i < n; i++)
            targetWorld[i] = ringPathOrderedC1ToCn[i].transform.position;

        float bondLen = Vector3.Distance(targetWorld[0], targetWorld[1]);
        if (bondLen < 1e-4f)
        {
            global::OrbitalRedistribution.TryBuildCyclicSigmaBondBreakStaggeredChainAtomAnimation(
                ringPathOrderedC1ToCn,
                out chainAnim);
        }
        else
        {
            bool failed = false;
            for (int i = 2; i < n; i++)
            {
                if (!global::OrbitalRedistribution.TryComputeCyclicSigmaStaggerChainTargetsOneStep(
                        ringPathOrderedC1ToCn, targetWorld, bondLen, i,
                        out _, out _, out _))
                {
                    failed = true;
                    break;
                }
            }

            if (failed)
            {
                global::OrbitalRedistribution.ClearCyclicSigmaBondBreakTemplateDebugVisuals();
                global::OrbitalRedistribution.TryBuildCyclicSigmaBondBreakStaggeredChainAtomAnimation(
                    ringPathOrderedC1ToCn,
                    out chainAnim);
            }
            else
            {
                chainAnim = global::OrbitalRedistribution.BuildCyclicSigmaChainAtomAnimationFromTargetWorld(
                    ringPathOrderedC1ToCn, targetWorld);
            }
        }

        if (atomA == null || atomB == null)
        {
            global::OrbitalRedistribution.ClearCyclicSigmaBondBreakTemplateDebugVisuals();
            onComplete?.Invoke();
            yield break;
        }

        if (cyclicSigmaBreakRecipient != null
            && cyclicSigmaBreakPartner != null
            && cyclicSigmaBreakRedistContext != null)
        {
            global::OrbitalRedistribution.AppendCyclicSigmaBondBreakRedistributionTemplateDebugVisuals(
                cyclicSigmaBreakRecipient,
                cyclicSigmaBreakPartner,
                cyclicSigmaBreakAntiRecipient,
                cyclicSigmaBreakRedistContext);
            yield return BondFormationDebugController.WaitPhase(
                4,
                "cyclo σ bond break redistribution templates");
        }

        global::OrbitalRedistribution.ClearCyclicSigmaBondBreakTemplateDebugVisuals();
        yield return StartCoroutine(CoLerpBondBreakRedistributionFromAnimations(
            animPrimary, animSecondary, applySecondary, onComplete, chainAnim, piSlotLineToRemoveAfterRedistribution: null));
    }

    /// <summary>
    /// Smoothstep-lerp <see cref="OrbitalRedistribution.BuildOrbitalRedistribution"/> result(s). Started via <see cref="AtomFunction.StartCoroutine"/> on an endpoint so this bond GameObject can be destroyed in <paramref name="onComplete"/>.
    /// </summary>
    IEnumerator CoLerpBondBreakRedistributionFromAnimations(
        global::OrbitalRedistribution.RedistributionAnimation animPrimary,
        global::OrbitalRedistribution.RedistributionAnimation animSecondary,
        bool applySecondary,
        Action onComplete,
        global::OrbitalRedistribution.CyclicSigmaChainAtomAnimation cyclicChainAtomAnim = null,
        int? piSlotLineToRemoveAfterRedistribution = null)
    {
        if (atomA == null || atomB == null)
        {
            onComplete?.Invoke();
            yield break;
        }

        var sigmaLineAtoms = new HashSet<AtomFunction> { atomA, atomB };
        cyclicChainAtomAnim?.GatherAtoms(sigmaLineAtoms);

        float elapsed = 0f;
        while (elapsed < BondBreakRedistributionDuration)
        {
            elapsed += Time.deltaTime;
            float s = BondBreakRedistributionDuration > 1e-6f ? Mathf.Clamp01(elapsed / BondBreakRedistributionDuration) : 1f;
            float smooth = s * s * (3f - 2f * s);
            // Redistribution (incl. fragment rigid children) must run first; cyclic chain lerp then overwrites
            // ring-atom nuclei to dynamic stagger targets so FragmentStartWorld does not cancel atom motion.
            animPrimary?.Apply(smooth);
            if (applySecondary)
                animSecondary?.Apply(smooth);
            cyclicChainAtomAnim?.Apply(smooth);
            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(sigmaLineAtoms);
            yield return null;
        }

        animPrimary?.Apply(1f);
        if (applySecondary)
            animSecondary?.Apply(1f);
        cyclicChainAtomAnim?.Apply(1f);
        AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(sigmaLineAtoms);

        atomA.RefreshCharge();
        atomB.RefreshCharge();
        AtomFunction.SetupGlobalIgnoreCollisions();

        if (piSlotLineToRemoveAfterRedistribution.HasValue)
        {
            int li = piSlotLineToRemoveAfterRedistribution.Value;
            atomA.RemovePiPOrbitalDirectionsForPartnerLine(atomB, li);
            atomB.RemovePiPOrbitalDirectionsForPartnerLine(atomA, li);
        }

        onComplete?.Invoke();
    }
}
