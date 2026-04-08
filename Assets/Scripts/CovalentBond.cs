using System.Collections;
using System.Collections.Generic;
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
    public static void LogExBondOrbitalPose(string _, string __, ElectronOrbitalFunction ___, AtomFunction ____, AtomFunction _____ = null) { }

    public static void LogBreakGuideOrbitalsPose(string _, AtomFunction __, AtomFunction ___, ElectronOrbitalFunction ____, ElectronOrbitalFunction _____, AtomFunction ______, AtomFunction _______) { }

    static void LogBreakEmptyTeleportBoth(string _, AtomFunction __, AtomFunction ___, Vector3? ____, Vector3? _____, ElectronOrbitalFunction ______, ElectronOrbitalFunction _______, AtomFunction ________, AtomFunction _________) { }

    [SerializeField] AtomFunction atomA;
    [SerializeField] AtomFunction atomB;
    ElectronOrbitalFunction orbital;
    AtomFunction orbitalContributor; // Atom that contributed the orbital; used for odd-electron tie-break when EN equal
    ElectronOrbitalFunction orbitalBeingFadedForCharge; // During bond formation: the orbital being faded; use its electrons for charge until destroyed

    bool orbitalVisible;

    bool forwardedPressToOrbital;
    float orbitalToLineAnimProgress = -1f; // -1 = done, 0..1 = animating
    internal bool animatingOrbitalToBondPosition; // σ/π post-Create: orbital moving from atom to bond before snap
    internal bool orbitalRotationFlipped; // Sigma: flip when source opposite to bond so electrons don't overlap

    /// <summary>
    /// When set by orbital-drag σ phase 1, <see cref="LateUpdate"/> and bond-frame snap helpers must not overwrite
    /// <see cref="orbital"/> world pose — <see cref="SigmaBondFormation"/> drives the unified shell that frame.
    /// </summary>
    internal bool suppressSigmaPrebondBondFrameOrbitalPose;

    /// <summary>
    /// Set for σ created by <see cref="SigmaBondFormation"/> orbital-drag three-phase: <see cref="ElectronRedistributionOrchestrator.ExecuteSigmaFormation12HybridAlignment"/>
    /// must not re-run <c>nonGuide_first</c> TryMatch (phase 1 already aligned the non-guide shell; duplicate refresh teleports lone pairs).
    /// </summary>
    internal bool SkipNonGuideExecuteSigmaFormation12HybridPass;

    /// <summary>Left-multiplies base σ-orbital world rotation after <see cref="AtomFunction.RedistributeOrbitals3D"/> so +X tracks VSEPR hybrid on the authoritative nucleus.</summary>
    Quaternion orbitalRedistributionWorldDelta = Quaternion.identity;
    PointerEventData storedPressData;
    GameObject lineVisual;
    SpriteRenderer lineRenderer;
    MeshRenderer cylinderRenderer;
    bool useCylinderBondVisual;
    BoxCollider2D lineCollider;
    BoxCollider lineCollider3D;
    static Sprite lineSprite;
    static Material bondCylinderMaterial;
    MaterialPropertyBlock bondFormationDebugGuideMpb;
    MaterialPropertyBlock bondFormationTemplatePickMpb;
    static readonly int BondShaderBaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int BondShaderColorId = Shader.PropertyToID("_Color");

    /// <summary>Black bond stroke with slight transparency (sprite + 3D cylinder).</summary>
    const float BondVisualAlpha = 0.82f;
    static readonly Color BondVisualColor = new Color(0f, 0f, 0f, BondVisualAlpha);

    public AtomFunction AtomA => atomA;
    public AtomFunction AtomB => atomB;
    public ElectronOrbitalFunction Orbital => orbital;

    /// <summary>Nucleus orbital being faded out and destroyed after σ/π formation; omit from post-formation VSEPR mover lists.</summary>
    internal ElectronOrbitalFunction OrbitalBeingFadedForCharge => orbitalBeingFadedForCharge;

    public int ElectronCount => orbital != null ? orbital.ElectronCount : 0;

    /// <summary>Diagnostics only — read <see cref="orbitalRedistributionWorldDelta"/> for σ-formation pose logs.</summary>
    internal Quaternion GetOrbitalRedistributionWorldDeltaForDiagnostics() => orbitalRedistributionWorldDelta;

    /// <summary>
    /// Orbital-drag σ phase 3: set δ and apply world pose so bond-parented σ animate with guide hybrid lerp (nucleus snapshots alone exclude bond children).
    /// </summary>
    internal void SetOrbitalRedistributionWorldDeltaForPhase3Lerp(Quaternion delta)
    {
        if (!IsSigmaBondLine() || orbital == null) return;
        orbitalRedistributionWorldDelta = delta;
        UpdateBondTransformToCurrentAtoms();
        SyncSigmaOrbitalWorldPoseFromRedistribution(forceApplyPoseDuringBondToLineAnim: true);
    }

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

    /// <summary>
    /// π bond end of post-Create formation: redistribution/animation sets the shared orbital world rotation, but
    /// <see cref="GetOrbitalTargetWorldState"/> uses <see cref="orbitalRedistributionWorldDelta"/>·baseR.
    /// Set δ from the current orbital world rotation after <see cref="UpdateBondTransformToCurrentAtoms"/> so
    /// <see cref="SnapOrbitalToBondPosition"/> does not swing the π tip away from the post-redist pose (runtime: OL-3 triad 120 → OL-4 60 without this).
    /// </summary>
    public void SyncPiOrbitalRedistributionDeltaFromCurrentWorldRotation()
    {
        if (orbital == null || atomA == null || atomB == null) return;
        if (IsSigmaBondLine()) return;
        UpdateBondTransformToCurrentAtoms();
        var baseR = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
        if (orbitalRotationFlipped) baseR = baseR * Quaternion.Euler(0f, 0f, 180f);
        orbitalRedistributionWorldDelta = orbital.transform.rotation * Quaternion.Inverse(baseR);
    }

    /// <summary>
    /// π and σ between the same pair share the same cylinder <see cref="BondFrameRotation"/>; with δ=identity both lobes can
    /// end up parallel (runtime: O=C=O second π, operation O <c>H-O-angle-snap</c> σ–π pair 0°). Twist δ by ±90° about the
    /// internuclear axis so π +X is not colinear with the σ bond’s current target tip.
    /// </summary>
    public void TwistPiOrbitalRedistributionDeltaAwayFromColinearSigmaPartnerIfNeeded(float minSeparationDeg = 35f)
    {
        if (orbital == null || atomA == null || atomB == null) return;
        if (IsSigmaBondLine()) return;
        CovalentBond sigmaPartner = FindSigmaBondToSamePartner();
        if (sigmaPartner == null || sigmaPartner.Orbital == null) return;

        sigmaPartner.UpdateBondTransformToCurrentAtoms();
        UpdateBondTransformToCurrentAtoms();
        var (_, sigmaWorldRot) = sigmaPartner.GetOrbitalTargetWorldState();
        Vector3 sigmaTip = (sigmaWorldRot * Vector3.right).normalized;
        if (sigmaTip.sqrMagnitude < 1e-12f) return;

        var baseR = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
        if (orbitalRotationFlipped) baseR = baseR * Quaternion.Euler(0f, 0f, 180f);

        Vector3 PiTipWorld(Quaternion delta) => (delta * baseR * Vector3.right).normalized;
        Vector3 piTip = PiTipWorld(orbitalRedistributionWorldDelta);
        float ang = Vector3.Angle(piTip, sigmaTip);
        if (ang >= minSeparationDeg) return;

        var first = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomA : atomB;
        var second = atomA.GetInstanceID() < atomB.GetInstanceID() ? atomB : atomA;
        Vector3 bondAxis = second.transform.position - first.transform.position;
        if (bondAxis.sqrMagnitude < 1e-12f) return;
        bondAxis.Normalize();

        bool TryTwists(List<Vector3> axes)
        {
            for (int ai = 0; ai < axes.Count; ai++)
            {
                Vector3 ax = axes[ai];
                if (ax.sqrMagnitude < 1e-12f) continue;
                ax.Normalize();
                for (int k = 0; k < 2; k++)
                {
                    float sign = k == 0 ? 1f : -1f;
                    Quaternion twist = Quaternion.AngleAxis(90f * sign, ax);
                    Quaternion tryDelta = twist * orbitalRedistributionWorldDelta;
                    float angNew = Vector3.Angle(PiTipWorld(tryDelta), sigmaTip);
                    if (angNew >= minSeparationDeg)
                    {
                        orbitalRedistributionWorldDelta = tryDelta;
                        return true;
                    }
                }
            }
            return false;
        }

        var axesToTry = new List<Vector3>(4) { bondAxis };
        Vector3 orthoBondPi = Vector3.Cross(bondAxis, piTip);
        if (orthoBondPi.sqrMagnitude > 1e-12f) axesToTry.Add(orthoBondPi);
        Vector3 orthoBondSig = Vector3.Cross(bondAxis, sigmaTip);
        if (orthoBondSig.sqrMagnitude > 1e-12f) axesToTry.Add(orthoBondSig);
        Vector3 orthoSigPi = Vector3.Cross(sigmaTip, piTip);
        if (orthoSigPi.sqrMagnitude > 1e-12f) axesToTry.Add(orthoSigPi);
        if (axesToTry.Count <= 1 || Mathf.Abs(Vector3.Dot(piTip, bondAxis)) > 0.995f)
        {
            Vector3 aux = Mathf.Abs(Vector3.Dot(bondAxis, Vector3.up)) < 0.95f ? Vector3.up : Vector3.right;
            Vector3 fb = Vector3.Cross(bondAxis, aux);
            if (fb.sqrMagnitude > 1e-12f) axesToTry.Add(fb);
        }
        TryTwists(axesToTry);
    }

    CovalentBond FindSigmaBondToSamePartner()
    {
        if (atomA == null || atomB == null) return null;
        CovalentBond TryPivot(AtomFunction pivot)
        {
            if (pivot == null) return null;
            foreach (var b in pivot.CovalentBonds)
            {
                if (b == null || b == this || !b.IsSigmaBondLine()) continue;
                var other = b.AtomA == pivot ? b.AtomB : b.AtomA;
                if ((pivot == atomA && other == atomB) || (pivot == atomB && other == atomA))
                    return b;
            }
            return null;
        }
        return TryPivot(atomA) ?? TryPivot(atomB);
    }

    /// <summary>
    /// Pure σ-relax + gauge path: bonding world rotation follows <c>bondEndLive.worldRot * gaugeRel</c> while
    /// <see cref="orbitalRedistributionWorldDelta"/> may still match its pre-step-2 value. Call when nuclei are at
    /// relax end so δ·baseR equals the actual bonding orbital world rot — then <see cref="GetOrbitalTargetWorldState"/>
    /// agrees and post–step-2 <see cref="ApplySigmaOrbitalTipFromRedistribution"/> (tips match, large quat angle) can skip.
    /// </summary>
    public void CommitSigmaRedistributionDeltaFromWorldOrbitalRotation(Quaternion orbitalWorldRotation)
    {
        if (!IsSigmaBondLine()) return;
        UpdateBondTransformToCurrentAtoms();
        var baseR = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
        if (orbitalRotationFlipped) baseR = baseR * Quaternion.Euler(0f, 0f, 180f);
        orbitalRedistributionWorldDelta = orbitalWorldRotation * Quaternion.Inverse(baseR);
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
        Quaternion BaseSigmaOrbitalWorldRotation(bool applyFlip)
        {
            var r = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
            if (applyFlip && orbitalRotationFlipped) r = r * Quaternion.Euler(0f, 0f, 180f);
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
        float offset = GetLineOffset(GetBondIndex(), atomA.GetBondsTo(atomB));
        var worldPos = center + perpendicular * offset;
        var worldRot = orbitalRedistributionWorldDelta * BaseSigmaOrbitalWorldRotation(true);
        return (worldPos, worldRot);
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

    /// <summary>Align shared σ lobe +X with hybrid direction expressed from <paramref name="fromAtom"/> (must be atom A or B). Uses bond axis from <paramref name="fromAtom"/> toward partner for sign.</summary>
    public void ApplySigmaOrbitalTipFromRedistribution(AtomFunction fromAtom, Vector3 hybridTipWorld)
    {
        if (orbital == null) return;
        if (fromAtom == null || (fromAtom != atomA && fromAtom != atomB)) return;
        if (!IsSigmaBondLine()) return;

        var partner = fromAtom == atomA ? atomB : atomA;
        if (partner == null) return;

        UpdateBondTransformToCurrentAtoms();

        if (hybridTipWorld.sqrMagnitude < 1e-12f) return;
        hybridTipWorld.Normalize();
        Vector3 geom = partner.transform.position - fromAtom.transform.position;
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
        if (dot > 0.9999f)
        {
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

    int GetBondIndex()
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

    /// <summary>Bond stepped-debug: tint existing line/cylinder renderers (MPB on 3D mesh so shared material stays black for other bonds).</summary>
    public void SetBondFormationDebugGuideHighlight(bool highlighted)
    {
        if (!highlighted)
        {
            if (cylinderRenderer != null)
                cylinderRenderer.SetPropertyBlock(null);
            if (lineRenderer != null)
                lineRenderer.color = BondVisualColor;
            return;
        }
        var c = new Color(0.12f, 0.88f, 0.4f, 0.92f);
        if (cylinderRenderer != null)
        {
            if (bondFormationDebugGuideMpb == null)
                bondFormationDebugGuideMpb = new MaterialPropertyBlock();
            bondFormationDebugGuideMpb.Clear();
            bondFormationDebugGuideMpb.SetColor(BondShaderBaseColorId, c);
            bondFormationDebugGuideMpb.SetColor(BondShaderColorId, c);
            cylinderRenderer.SetPropertyBlock(bondFormationDebugGuideMpb);
        }
        if (lineRenderer != null)
            lineRenderer.color = c;
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
        if (animateOrbitalToBond)
        {
            orbitalVisible = true; // Start with orbital visible for orbital-to-line animation
        }
        else
        {
            orbitalVisible = false;
            orbitalToLineAnimProgress = -1f; // Skip animation; show line immediately
        }
        ApplyDisplayMode();
    }

    /// <summary>Animates the bond orbital transforming into a line over the given duration. Optionally fades out another orbital (e.g. target orbital) in parallel.</summary>
    public IEnumerator AnimateOrbitalToLine(float duration, ElectronOrbitalFunction orbitalToFadeOut = null)
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
            yield return null;
        }

        orbitalToLineAnimProgress = -1f;
        orbitalVisible = false;
        orbitalBeingFadedForCharge = null;
        ApplyDisplayMode();
        if (orbitalToFadeOut != null)
            Destroy(orbitalToFadeOut.gameObject);
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
            var mat = GetOrCreateBondCylinderMaterial();
            if (mat != null)
                cylinderRenderer.sharedMaterial = mat;

            lineCollider3D = lineVisual.AddComponent<BoxCollider>();
            lineCollider3D.isTrigger = true;
            lineCollider3D.size = new Vector3(2f, 2f, 2f);
            lineCollider3D.center = Vector3.zero;
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

        lineVisual = null;
        lineRenderer = null;
        cylinderRenderer = null;
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
        // Each bond has its own lineVisual + collider; position this bond's line at its offset (not shared)
        lineVisual.transform.position = center + perpendicular * offset;
        lineVisual.transform.localRotation = Quaternion.identity;
        float lineLength = Mathf.Max(0.1f, distance * 0.5f);
        float lineScaleMult = orbitalToLineAnimProgress < 0 ? 1f : orbitalToLineAnimProgress;
        if (useCylinderBondVisual)
        {
            const float radiusScale = 0.15f;
            float halfHeight = 0.5f * lineLength * lineScaleMult;
            lineVisual.transform.localScale = new Vector3(radiusScale, Mathf.Max(0.001f, halfHeight), radiusScale);
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
            var orbWorldPos = center + perpendicular * offset;
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
            orbital.SetVisualsEnabled(orbitalVisible);
            orbital.SetPointerBlocked(true);
        }
        bool animating = orbitalToLineAnimProgress >= 0 && orbitalToLineAnimProgress < 1f;
        bool showLine = !orbitalVisible || animating; // Show line/cylinder from start of orbital-to-line so it grows with orbital shrinking
        if (lineRenderer != null) lineRenderer.enabled = showLine;
        if (cylinderRenderer != null) cylinderRenderer.enabled = showLine;
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
        ApplyDisplayMode();
        // Bond-line pose before any drag so the shared orbital does not start from a stale world pose.
        UpdateBondTransformToCurrentAtoms();
        SnapOrbitalToBondPosition();
    }

    static float DistanceToLineSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var ap = p - a;
        var t = Mathf.Clamp01(Vector3.Dot(ap, ab) / (ab.sqrMagnitude + 0.0001f));
        var nearest = a + t * ab;
        return Vector3.Distance(p, nearest);
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
            if (lineVisual != null && hitGo != null &&
                (hitGo == lineVisual || hitGo.transform == lineVisual.transform))
                return true;
            var cam = Camera.main;
            if (cam != null && !cam.orthographic)
            {
                var ray = cam.ScreenPointToRay(eventData.position);
                if (ApproxDistanceRayToSegment(ray, lineStart, lineEnd) <= 0.45f)
                    return true;
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
            SnapOrbitalToBondPosition();
            forwardedPressToOrbital = true;
            orbital.OnPointerDown(storedPressData);
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
    /// <param name="instantRedistributionForDestroyPartner">When true, skip post-break <see cref="AtomFunction.CoLerpBondBreakRedistribution"/> (pure σ-break layout lerp) so callers can keep nuclear/orbital framing (e.g. edit-mode replace-H / destroy-bridge before attach). Default false runs redistribution after ex-bond lobes are placed.</param>
    public void BreakBond(AtomFunction returnOrbitalTo, bool userDragBondCylinderBreak = false, bool instantRedistributionForDestroyPartner = false)
    {
        if (orbital == null) return;

        // Bond-line pose for orbitals (must capture before UnregisterBond — GetOrbitalTargetWorldState uses bond topology).
        var (bondOrbitalWorldPos, bondOrbitalWorldRot) = GetOrbitalTargetWorldState();

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

        if (fadeOrbForBreak != null && fadeOrbForBreak != orbital)
            Destroy(fadeOrbForBreak.gameObject);

        // If a lone pair is released while another empty non-bond exists, split 2e→1e+1e first.
        atomA?.TryTransferElectronFromLonePairToEmptyOrbitals();
        atomB?.TryTransferElectronFromLonePairToEmptyOrbitals();
        atomA?.RefreshCharge();
        atomB?.RefreshCharge();

        orbital.transform.SetPositionAndRotation(bondOrbitalWorldPos, bondOrbitalWorldRot);
        newOrbital.transform.SetPositionAndRotation(bondOrbitalWorldPos, bondOrbitalWorldRot);

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();
        atomA?.SetInteractionBlocked(false);
        atomB?.SetInteractionBlocked(false);

        void FinishBreakBondTail()
        {
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
            ElectronOrbitalFunction antiGuideA = otherAtom == atomA && newOrbital.ElectronCount == 0 ? newOrbital : null;
            ElectronOrbitalFunction antiGuideB = otherAtom == atomB && newOrbital.ElectronCount == 0 ? newOrbital : null;

            atomA.StartCoroutine(atomA.CoLerpBondBreakRedistribution(
                atomB,
                antiGuideA,
                antiGuideB,
                FinishBreakBondTail));
            return;
        }

        FinishBreakBondTail();
    }

    /// <summary>Legacy flag (bond-break σ-relax path removed). Some <see cref="AtomFunction"/> helpers still gate logs on this.</summary>
    public static bool DebugLogBreakBondSigmaRelaxWhy = false;

    /// <summary>Gates <see cref="AtomFunction.LogBondBreakTetraFrameworkSnapshot"/>; default off for quiet runs is not required for legacy triage here.</summary>
    public static bool DebugLogBondBreakTetraFramework = true;

    /// <summary>Gates bond-break motion source logs on <see cref="AtomFunction.RedistributeOrbitals"/> entry. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogBreakBondMotionSources = true;

    /// <summary>σ post-Create formation: set redistribute target pose to current locals for occupied nucleus-parented lobes (still used by <see cref="ElectronOrbitalFunction"/> formation).</summary>
    public static void NeutralizeOccupiedRedistTargetsToCurrentLocals(
        AtomFunction nucleus,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list)
    {
        if (nucleus == null || list == null || list.Count == 0) return;
        for (int i = 0; i < list.Count; i++)
        {
            var (o, _, _) = list[i];
            if (o == null || o.ElectronCount <= 0) continue;
            if (o.transform.parent != nucleus.transform) continue;
            list[i] = (o, o.transform.localPosition, o.transform.localRotation);
        }
    }

    /// <summary>
    /// Bond-break lerp: freeze non-guide domains to current locals so joint motion does not fight the partner (σ or π cylinder break, same idea as “π break” for redistribution). Cleaved ex-bond <paramref name="guide"/> keeps Peek targets.
    /// When <paramref name="nucleusWhoseEmptyNonbondOrbitalsKeepPeekTargets"/> is set, nucleus-parented 0e non-bond rows also keep Peek targets so an empty p can lerp to ⊥ VSEPR (π-cylinder break); otherwise they would stay frozen and appear motionless.
    /// </summary>
    public static void FreezeRedistTargetsExceptGuideToCurrentLocals(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list,
        ElectronOrbitalFunction guide,
        AtomFunction nucleusWhoseEmptyNonbondOrbitalsKeepPeekTargets = null)
    {
        if (list == null || list.Count == 0 || guide == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var (o, _, _) = list[i];
            // UnityEngine.Object: test o before o == guide so destroyed rows never touch transform.
            if (o == null) continue;
            if (o == guide) continue;
            if (nucleusWhoseEmptyNonbondOrbitalsKeepPeekTargets != null
                && o.Bond == null
                && o.ElectronCount == 0
                && o.transform.parent == nucleusWhoseEmptyNonbondOrbitalsKeepPeekTargets.transform)
                continue;
            list[i] = (o, o.transform.localPosition, o.transform.localRotation);
        }
    }

    /// <summary>
    /// True if any target row is bond-parented on a non-σ (<see cref="IsSigmaBondLine"/>) line. When the user breaks the σ bond,
    /// Peek can still list the remaining π bond orbital; joint lerp then rotates σ+π+lobes together (~165° on CO₂-style centers). Used to apply the same freeze-except-guide as π cleavage.
    /// </summary>
    public static bool TargetsListContainsPiBondLineRow(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list)
    {
        if (list == null || list.Count == 0) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i].orb;
            if (o == null) continue;
            if (o.Bond is CovalentBond cb && !cb.IsSigmaBondLine()) return true;
        }
        return false;
    }
}
