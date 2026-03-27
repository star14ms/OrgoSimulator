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

    /// <summary>
    /// σ cleavage: each ex-bond lobe should point from this nucleus toward the partner. Preview / bond-line capture often gives
    /// both fragments the same world rotation, so one center keeps tip ∥ A→B while its partner needs tip ∥ B→A — fix 180° when
    /// <see cref="ElectronOrbitalFunction"/> +X (tip) is anti-parallel to partner in local space.
    /// </summary>
    static (Vector3 position, Quaternion rotation) CorrectBreakGuideSlotTowardPartner(
        (Vector3 position, Quaternion rotation) slot,
        AtomFunction nucleus,
        AtomFunction partner)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || nucleus == null || partner == null) return slot;
        Vector3 toPartner = partner.transform.position - nucleus.transform.position;
        if (toPartner.sqrMagnitude < 1e-10f) return slot;
        toPartner.Normalize();
        Vector3 tipLocal = (slot.rotation * Vector3.right).normalized;
        Vector3 wantLocal = nucleus.transform.InverseTransformDirection(toPartner).normalized;
        if (Vector3.Dot(tipLocal, wantLocal) >= 0f) return slot;
        Quaternion newR = Quaternion.FromToRotation(tipLocal, wantLocal) * slot.rotation;
        Vector3 newDir = (newR * Vector3.right).normalized;
        return ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, nucleus.BondRadius);
    }

    [SerializeField] AtomFunction atomA;
    [SerializeField] AtomFunction atomB;
    ElectronOrbitalFunction orbital;
    AtomFunction orbitalContributor; // Atom that contributed the orbital; used for odd-electron tie-break when EN equal
    ElectronOrbitalFunction orbitalBeingFadedForCharge; // During bond formation: the orbital being faded; use its electrons for charge until destroyed

    bool orbitalVisible;
    bool forwardedPressToOrbital;
    float orbitalToLineAnimProgress = -1f; // -1 = done, 0..1 = animating
    internal bool animatingOrbitalToBondPosition; // Step 2: orbital moving from atom to bond
    /// <summary>Step 2 σ-relax only: existing σ line orbitals would re-snap every frame as neighbor H moves — freezes world rot until <see cref="EndSigmaFormationStep2PeripheralOrbitalWorldRotFreeze"/> + full snap.</summary>
    bool sigmaFormationStep2FreezePeripheralOrbitalWorldRotation;
    Quaternion sigmaFormationStep2FrozenOrbitalWorldRotation;
    internal bool orbitalRotationFlipped; // Sigma: flip when source opposite to bond so electrons don't overlap
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

    /// <summary>Black bond stroke with slight transparency (sprite + 3D cylinder).</summary>
    const float BondVisualAlpha = 0.82f;
    static readonly Color BondVisualColor = new Color(0f, 0f, 0f, BondVisualAlpha);

    public AtomFunction AtomA => atomA;
    public AtomFunction AtomB => atomB;
    public ElectronOrbitalFunction Orbital => orbital;

    public int ElectronCount => orbital != null ? orbital.ElectronCount : 0;

    /// <summary>Diagnostics only — read <see cref="orbitalRedistributionWorldDelta"/> for σ-formation pose logs.</summary>
    internal Quaternion GetOrbitalRedistributionWorldDeltaForDiagnostics() => orbitalRedistributionWorldDelta;

    internal void BeginSigmaFormationStep2PeripheralOrbitalWorldRotFreeze()
    {
        if (!IsSigmaBondLine() || orbital == null) return;
        sigmaFormationStep2FrozenOrbitalWorldRotation = orbital.transform.rotation;
        sigmaFormationStep2FreezePeripheralOrbitalWorldRotation = true;
    }

    internal void EndSigmaFormationStep2PeripheralOrbitalWorldRotFreeze()
    {
        sigmaFormationStep2FreezePeripheralOrbitalWorldRotation = false;
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

    /// <summary>Snaps the bond orbital to the exact world position that matches lineVisual (center + perpendicular * offset). Call at end of step 2 to prevent teleport into step 3. Call UpdateBondTransformToCurrentAtoms first.</summary>
    /// <param name="preserveWorldRollFrom">Optional σ-formation: world rot after step-2 lerp (often matches cylinder +X but not bond cylinder roll). When set in 3D σ line, applies swing to match cylinder +X and writes <see cref="orbitalRedistributionWorldDelta"/> so <see cref="LateUpdate"/> agrees.</param>
    public void SnapOrbitalToBondPosition(Quaternion? preserveWorldRollFrom = null)
    {
        if (orbital == null || atomA == null || atomB == null) return;
        var (worldPos, worldRot) = GetOrbitalTargetWorldState();
        orbital.transform.position = worldPos;
        Quaternion appliedRot = worldRot;
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && preserveWorldRollFrom.HasValue)
        {
            Quaternion pref = preserveWorldRollFrom.Value;
            Vector3 pTip = pref * Vector3.right;
            Vector3 wTip = worldRot * Vector3.right;
            if (pTip.sqrMagnitude > 1e-12f && wTip.sqrMagnitude > 1e-12f)
                appliedRot = Quaternion.FromToRotation(pTip.normalized, wTip.normalized) * pref;
        }
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && IsSigmaBondLine())
        {
            var baseR = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
            if (orbitalRotationFlipped) baseR = baseR * Quaternion.Euler(0f, 0f, 180f);
            orbitalRedistributionWorldDelta = appliedRot * Quaternion.Inverse(baseR);
        }
        orbital.transform.rotation = appliedRot;
    }

    /// <summary>
    /// Pure σ-relax + gauge path: bonding world rotation follows <c>bondEndLive.worldRot * gaugeRel</c> while
    /// <see cref="orbitalRedistributionWorldDelta"/> may still match its pre-step-2 value. Call when nuclei are at
    /// relax end so δ·baseR equals the actual bonding orbital world rot — then <see cref="GetOrbitalTargetWorldState"/>
    /// agrees and post–step-2 <see cref="ApplySigmaOrbitalTipFromRedistribution"/> (tips match, large quat angle) can skip.
    /// </summary>
    public void CommitSigmaRedistributionDeltaFromWorldOrbitalRotation(Quaternion orbitalWorldRotation)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || !IsSigmaBondLine()) return;
        UpdateBondTransformToCurrentAtoms();
        var baseR = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
        if (orbitalRotationFlipped) baseR = baseR * Quaternion.Euler(0f, 0f, 180f);
        orbitalRedistributionWorldDelta = orbitalWorldRotation * Quaternion.Inverse(baseR);
    }

    /// <summary>Used by <see cref="AtomFunction.UpdateSigmaBondVisualsForAtoms"/>: same as <see cref="SnapOrbitalToBondPosition"/> except during step-2 pure σ-relax when peripheral σ line orbitals freeze world rotation.</summary>
    internal void UpdateSigmaBondVisualsAfterBondTransform()
    {
        if (orbital == null || atomA == null || atomB == null) return;
        bool userDragging = orbitalVisible && orbitalToLineAnimProgress < 0;
        if (userDragging) return;
        // σ formation step 2: coroutine sets bonding world pose (gauge vs canonical). SnapOrbitalToBondPosition here
        // would overwrite with δ·baseR while δ is still identity → 180° pop or fight every frame with pure σ-relax.
        if (animatingOrbitalToBondPosition) return;

        if (sigmaFormationStep2FreezePeripheralOrbitalWorldRotation && IsSigmaBondLine())
        {
            var (worldPos, _) = GetOrbitalTargetWorldState();
            orbital.transform.position = worldPos;
            orbital.transform.rotation = sigmaFormationStep2FrozenOrbitalWorldRotation;
            float orbScale = orbitalToLineAnimProgress < 0 ? 0.6f : 0.6f * (1f - orbitalToLineAnimProgress);
            orbital.transform.localScale = Vector3.one * Mathf.Max(0.01f, orbScale);
            return;
        }
        SnapOrbitalToBondPosition();
    }

    /// <summary>
    /// Applies <see cref="GetOrbitalTargetWorldState"/> immediately after changing <see cref="orbitalRedistributionWorldDelta"/>
    /// so σ lobe +X and nucleus-local tip reads match before <c>LateUpdate</c>. Same guards as σ pose block in <c>LateUpdate</c>.
    /// </summary>
    /// <param name="forceApplyPoseDuringBondToLineAnim">σ formation calls <see cref="ApplySigmaOrbitalTipFromRedistribution"/> while <see cref="animatingOrbitalToBondPosition"/> is still true; those updates must still push world pose to match δ/flip before snap.</param>
    public void SyncSigmaOrbitalWorldPoseFromRedistribution(bool forceApplyPoseDuringBondToLineAnim = false)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || orbital == null || atomA == null || atomB == null) return;
        if (!IsSigmaBondLine()) return;
        bool userDragging = orbitalVisible && orbitalToLineAnimProgress < 0;
        if (userDragging) return;
        if (!forceApplyPoseDuringBondToLineAnim && animatingOrbitalToBondPosition) return;
        UpdateBondTransformToCurrentAtoms();
        var (worldPos, worldRot) = GetOrbitalTargetWorldState();
        orbital.transform.position = worldPos;
        orbital.transform.rotation = worldRot;
        float orbScale = orbitalToLineAnimProgress < 0 ? 0.6f : 0.6f * (1f - orbitalToLineAnimProgress);
        orbital.transform.localScale = Vector3.one * Mathf.Max(0.01f, orbScale);
    }

    /// <summary>Returns the target world position and rotation for the bond orbital (for step 2 animation).</summary>
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
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || orbital == null) return;
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
        // When animating: defer reparent until after step 2 so orbital keeps its current rotation and can animate to bond
        animatingOrbitalToBondPosition = animateOrbitalToBond;

        atomA.SetupIgnoreCollisions();
        atomB.SetupIgnoreCollisions();

        CreateLineVisual();
        if (animateOrbitalToBond)
        {
            orbitalVisible = true; // Start with orbital visible for step 3 animation
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
        // Skip when user is dragging or when animating orbital to bond (step 2)
        bool userDragging = orbitalVisible && orbitalToLineAnimProgress < 0;
        if (orbital != null && !userDragging && !animatingOrbitalToBondPosition)
        {
            var orbWorldPos = center + perpendicular * offset;
            orbital.transform.position = orbWorldPos;
            if (sigmaFormationStep2FreezePeripheralOrbitalWorldRotation && IsSigmaBondLine())
                orbital.transform.rotation = sigmaFormationStep2FrozenOrbitalWorldRotation;
            else
            {
                var baseOrbRot = transform.rotation * Quaternion.Euler(0f, 0f, 90f);
                if (orbitalRotationFlipped) baseOrbRot = baseOrbRot * Quaternion.Euler(0f, 0f, 180f);
                var orbRot = orbitalRedistributionWorldDelta * baseOrbRot;
                orbital.transform.rotation = orbRot;
            }
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
        bool showLine = !orbitalVisible || animating; // Show line/cylinder from start of step 3 so it grows with orbital shrinking
        if (lineRenderer != null) lineRenderer.enabled = showLine;
        if (cylinderRenderer != null) cylinderRenderer.enabled = showLine;
        if (lineCollider != null) lineCollider.enabled = !orbitalVisible && orbitalToLineAnimProgress < 0;
        if (lineCollider3D != null) lineCollider3D.enabled = !orbitalVisible && orbitalToLineAnimProgress < 0;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
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
    /// <param name="instantRedistributionForDestroyPartner">When true (e.g. edit mode replaces H then destroys it), skip the 3D coroutine and apply final σ + VSEPR layout immediately so no async code holds the removed atom.</param>
    public void BreakBond(AtomFunction returnOrbitalTo, bool userDragBondCylinderBreak = false, bool instantRedistributionForDestroyPartner = false)
    {
        if (orbital == null) return;

        int piBeforeA = atomA != null ? atomA.GetPiBondCount() : 0;
        int piBeforeB = atomB != null ? atomB.GetPiBondCount() : 0;
        float? piBondAngleFromA = null;
        float? piBondAngleFromB = null;
        Vector3 dirAtoB = Vector3.zero;
        Vector3 dirBtoA = Vector3.zero;
        if (atomA != null && atomB != null)
        {
            dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
            dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;
            if (dirAtoB.sqrMagnitude >= 0.01f)
                piBondAngleFromA = OrbitalAngleUtility.DirectionToAngleWorld(dirAtoB);
            if (dirBtoA.sqrMagnitude >= 0.01f)
                piBondAngleFromB = OrbitalAngleUtility.DirectionToAngleWorld(dirBtoA);
        }

        // Bond-line pose for orbitals (must capture before UnregisterBond — GetOrbitalTargetWorldState uses bond topology).
        var (bondOrbitalWorldPos, bondOrbitalWorldRot) = GetOrbitalTargetWorldState();

        atomA?.UnregisterBond(this);
        atomB?.UnregisterBond(this);

        // π break (σ remains): keep the two centers fixed; σ-relax moves substituents only.
        HashSet<AtomFunction> sigmaRelaxPins = null;
        if (atomA != null && atomB != null && atomA.GetBondsTo(atomB) > 0)
            sigmaRelaxPins = new HashSet<AtomFunction> { atomA, atomB };
        // Full σ cleavage (no bond left between A and B): same CH₃–CH₃ rules — empty along broken axis, σ neighbors tetrahedral. π break: empty ⊥ remaining electron framework.
        bool sigmaCleavageBetweenPartners = atomA != null && atomB != null && atomA.GetBondsTo(atomB) == 0;

        // Animated σ/π formation: shared bond orbital may not yet have merged ElectronCount (step 3 applies merged);
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

        if (DebugLogBreakBondStampSigmaFilterDetail)
        {
            Debug.Log("[break-motion] BreakBond returnOrbitalTo nucleus is where original bond lobe was reparented (returned side) name=" + (returnOrbitalTo != null ? returnOrbitalTo.name : "null") + " id=" + (returnOrbitalTo != null ? returnOrbitalTo.GetInstanceID().ToString() : "null"));
            Debug.Log("[break-motion] BreakBond otherAtom is spawn side for new lobe name=" + (otherAtom != null ? otherAtom.name : "null") + " id=" + (otherAtom != null ? otherAtom.GetInstanceID().ToString() : "null"));
            if (orbital != null)
                Debug.Log("[break-motion] BreakBond returned lobe orbital id=" + orbital.GetInstanceID() + " ec=" + orbital.ElectronCount + " parentAtomId=" + (orbital.transform.parent != null ? orbital.transform.parent.GetInstanceID().ToString() : "null"));
            if (newOrbital != null)
                Debug.Log("[break-motion] BreakBond newOrbital id=" + newOrbital.GetInstanceID() + " ec=" + newOrbital.ElectronCount + " parentAtomId=" + (newOrbital.transform.parent != null ? newOrbital.transform.parent.GetInstanceID().ToString() : "null"));
        }

        // Keep this before redistribution/group counting:
        // if a lone pair is released while another empty non-bond exists, split 2e→1e+1e first.
        atomA?.TryTransferElectronFromLonePairToEmptyOrbitals();
        atomB?.TryTransferElectronFromLonePairToEmptyOrbitals();
        atomA?.RefreshCharge();
        atomB?.RefreshCharge();

        int piAfterA = atomA != null ? atomA.GetPiBondCount() : 0;
        int piAfterB = atomB != null ? atomB.GetPiBondCount() : 0;
        bool sigmaOnlyUnchangedPiBoth = piBeforeA == piAfterA && piBeforeB == piAfterB;
        Vector3? refWorldA = dirAtoB.sqrMagnitude >= 0.01f ? dirAtoB : (Vector3?)null;
        Vector3? refWorldB = dirBtoA.sqrMagnitude >= 0.01f ? dirBtoA : (Vector3?)null;

        // No predictive VSEPR preview: staged and cleavage paths both use post-break actual state only.
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            orbital.transform.SetPositionAndRotation(bondOrbitalWorldPos, bondOrbitalWorldRot);
            newOrbital.transform.SetPositionAndRotation(bondOrbitalWorldPos, bondOrbitalWorldRot);
        }
        else
        {
            orbital.transform.localPosition = slotA.position;
            orbital.transform.localRotation = slotA.rotation;
            newOrbital.transform.localPosition = slotB.position;
            newOrbital.transform.localRotation = slotB.rotation;
        }

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && returnOrbitalTo != null && otherAtom != null)
        {
            slotA = CorrectBreakGuideSlotTowardPartner(slotA, returnOrbitalTo, otherAtom);
            slotB = CorrectBreakGuideSlotTowardPartner(slotB, otherAtom, returnOrbitalTo);
            if (orbital != null && orbital.transform.parent == returnOrbitalTo.transform)
            {
                orbital.transform.localPosition = slotA.position;
                orbital.transform.localRotation = slotA.rotation;
            }
            if (newOrbital != null && newOrbital.transform.parent == otherAtom.transform)
            {
                newOrbital.transform.localPosition = slotB.position;
                newOrbital.transform.localRotation = slotB.rotation;
            }
        }

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();
        atomA?.SetInteractionBlocked(false);
        atomB?.SetInteractionBlocked(false);

        // Perspective: animate σ neighbors + bond→slot orbital motion, then lone-orbital redistribution.
        var sigmaCleavagePinnedEmptyOrbitals = BuildSigmaCleavagePinnedEmptyOrbitals(sigmaCleavageBetweenPartners, orbital, newOrbital);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && (atomA != null || atomB != null))
        {
            if (instantRedistributionForDestroyPartner)
            {
                ApplyInstantBreakBondRedistribution3D(
                    atomA, atomB,
                    piBeforeA, piBeforeB,
                    piBondAngleFromA, refWorldA, piAfterA,
                    piBondAngleFromB, refWorldB, piAfterB,
                    orbital, newOrbital,
                    returnOrbitalTo, otherAtom,
                    slotA, slotB,
                    sigmaRelaxPins,
                    sigmaCleavageBetweenPartners,
                    sigmaCleavagePinnedEmptyOrbitals,
                    preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak: false);
            }
            else
            {
                var runner = atomA != null ? atomA : atomB;
                runner.StartCoroutine(CoAnimateBreakBondRedistribution(
                    atomA, atomB,
                    piBeforeA, piBeforeB,
                    piBondAngleFromA, refWorldA, piAfterA,
                    piBondAngleFromB, refWorldB, piAfterB,
                    orbital, newOrbital,
                    returnOrbitalTo, otherAtom,
                    slotA, slotB,
                    bondOrbitalWorldPos, bondOrbitalWorldRot,
                    sigmaRelaxPins,
                    userDragBondCylinderBreak,
                    sigmaCleavageBetweenPartners,
                    sigmaCleavagePinnedEmptyOrbitals));
            }
        }
        else
        {
            var gA = BondBreakGuideLoneOrbitalOnAtom(atomA, orbital, newOrbital, returnOrbitalTo, otherAtom);
            var gB = BondBreakGuideLoneOrbitalOnAtom(atomB, orbital, newOrbital, returnOrbitalTo, otherAtom);
            LogBreakBondRedistributeCall("BreakBond2D", atomA, piBondAngleFromA, refWorldA, true, false, sigmaRelaxPins, false, gA);
            var sigmaPartnerHintA = SurvivingSigmaPartnerAfterBreakStep(atomA, atomA, atomB, sigmaCleavageBetweenPartners);
            var sigmaPartnerHintB = SurvivingSigmaPartnerAfterBreakStep(atomB, atomA, atomB, sigmaCleavageBetweenPartners);
            atomA?.RedistributeOrbitals(piBondAngleFromA, refWorldA, relaxCoplanarSigmaToTetrahedral: true, pinAtomsForSigmaRelax: sigmaRelaxPins, bondBreakGuideLoneOrbital: gA, newSigmaBondPartnerHint: sigmaPartnerHintA, bondBreakIsSigmaCleavageBetweenFormerPartners: sigmaCleavageBetweenPartners);
            LogBreakBondRedistributeCall("BreakBond2D", atomB, piBondAngleFromB, refWorldB, true, false, sigmaRelaxPins, false, gB);
            atomB?.RedistributeOrbitals(piBondAngleFromB, refWorldB, relaxCoplanarSigmaToTetrahedral: true, pinAtomsForSigmaRelax: sigmaRelaxPins, bondBreakGuideLoneOrbital: gB, newSigmaBondPartnerHint: sigmaPartnerHintB, bondBreakIsSigmaCleavageBetweenFormerPartners: sigmaCleavageBetweenPartners);
            var guideA2D = BondBreakGuideLoneOrbitalOnAtom(atomA, orbital, newOrbital, returnOrbitalTo, otherAtom);
            var guideB2D = BondBreakGuideLoneOrbitalOnAtom(atomB, orbital, newOrbital, returnOrbitalTo, otherAtom);
            atomA?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(refWorldA, guideA2D, sigmaCleavageBetweenPartners);
            atomB?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(refWorldB, guideB2D, sigmaCleavageBetweenPartners);
        }

        orbital = null;
        Destroy(gameObject);
    }

    /// <summary>Duration for σ-neighbor + orbital motion after a bond break (3D); σ-neighbor relax + optional lone layout after.</summary>
    const float BreakRedistributionDuration = 0.55f;

    /// <summary>Log bond-break diagnostics when on: <c>[break-σ-relax]</c> (nuclear targets, <see cref="AtomFunction.TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets"/>, rigid cleavage) and <c>[break-redistribute]</c> (<see cref="AtomFunction.RedistributeOrbitals"/> args). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogBreakBondSigmaRelaxWhy = false;

    /// <summary>Log <c>[break-motion]</c>: why the bond-break coroutine still animates when <see cref="AtomFunction.RedistributeOrbitals3D"/> is a no-op — <see cref="AtomFunction.GetRedistributeTargets3D"/>, empty append, bond→slot lerp, Newman, lobe lerp. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogBreakBondMotionSources = true;

    /// <summary><c>[break-motion]</c> frozen non-bond snapshot and σ-cleavage redist filter reasons. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogBreakBondStampSigmaFilterDetail = true;

    /// <summary>Log <c>[break-tetra]</c>: nuclear σ-axes vs electron-domain pairwise angles (and σ-tip vs bond-axis alignment) across VSEPR preview and <see cref="CoAnimateBreakBondRedistribution"/>. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogBondBreakTetraFramework = false;

    static void LogBreakBondTetraFrameworkPair(string phase, AtomFunction atomA, AtomFunction atomB)
    {
        if (!DebugLogBondBreakTetraFramework) return;
        atomA?.LogBondBreakTetraFrameworkSnapshot(phase + "/A");
        atomB?.LogBondBreakTetraFrameworkSnapshot(phase + "/B");
    }

    static void LogBreakBondCoMotionPrecis(
        AtomFunction atomA,
        AtomFunction atomB,
        int sigmaRelaxMoves,
        bool sigmaRelaxPreApplied,
        bool sigmaOnlyUnchangedPiBoth,
        int redistRawA,
        int redistRawB,
        int redistFilteredA,
        int redistFilteredB,
        int redistAfterEmptyAppendA,
        int redistAfterEmptyAppendB,
        bool animOrbA,
        bool animOrbB,
        float bondSlotWorldDistA,
        float bondSlotWorldAngA,
        float bondSlotWorldDistB,
        float bondSlotWorldAngB,
        bool hasRedistAnim,
        float maxRedistLocalPosA,
        float maxRedistLocalRotA,
        float maxRedistLocalPosB,
        float maxRedistLocalRotB,
        bool breakNewmanA,
        bool breakNewmanB,
        float psiBreakA,
        float psiBreakB,
        bool runStep)
    {
        if (!DebugLogBreakBondMotionSources) return;
        Debug.Log(
            "[break-motion] CoAnimateBreakBondRedistribution precis | " +
            $"runStep={runStep} (σMoves={sigmaRelaxMoves} animOrbA={animOrbA} animOrbB={animOrbB} hasRedistLobeLerp={hasRedistAnim} NewmanA={breakNewmanA} NewmanB={breakNewmanB}) | " +
            $"skipFinalΣNeigh={sigmaRelaxPreApplied} σOnlyBothπUnchanged={sigmaOnlyUnchangedPiBoth} (σ-only forces bond→slot lerp targets even if Δ≈0) | " +
            $"GetRedistributeTargets raw A={redistRawA} B={redistRawB} afterFilter A={redistFilteredA} B={redistFilteredB} afterEmptyAppend A={redistAfterEmptyAppendA} B={redistAfterEmptyAppendB} | " +
            $"redist max local Δpos A={maxRedistLocalPosA:F5} B={maxRedistLocalPosB:F5} max Δrot° A={maxRedistLocalRotA:F2} B={maxRedistLocalRotB:F2} | " +
            $"bond→slot world |A|={bondSlotWorldDistA:F5} ∠A={bondSlotWorldAngA:F2}° |B|={bondSlotWorldDistB:F5} ∠B={bondSlotWorldAngB:F2}° | " +
            $"ψNewman A={psiBreakA:F2}° B={psiBreakB:F2}° | " +
            "Note: RedistributeOrbitals→RedistributeOrbitals3D does not run GetRedistributeTargets; lobe motion here is independent of stubbing RedistributeOrbitals3D.");
    }

    static string FormatBreakBondCenterBrief(AtomFunction a) =>
        a == null ? "null" : $"{a.name}(Z={a.AtomicNumber})";

    static void LogBreakBondSigmaRelaxBranch(string branch, AtomFunction center, List<(AtomFunction neighbor, Vector3 targetWorld)> list)
    {
        if (!DebugLogBreakBondSigmaRelaxWhy || list == null || list.Count == 0) return;
        float maxD = 0f;
        foreach (var (n, end) in list)
        {
            if (n == null) continue;
            maxD = Mathf.Max(maxD, Vector3.Distance(n.transform.position, end));
        }
        Debug.Log($"[break-σ-relax] {branch} center={FormatBreakBondCenterBrief(center)} n={list.Count} maxWorldΔ={maxD:F5}");
    }

    static void LogBreakBondSigmaRelaxRigidDelta(AtomFunction center, Dictionary<AtomFunction, (Vector3 start, Vector3 end)> moves, Dictionary<AtomFunction, (Vector3 start, Vector3 end)> before)
    {
        if (!DebugLogBreakBondSigmaRelaxWhy || moves == null) return;
        foreach (var kv in moves)
        {
            if (kv.Key == null) continue;
            before.TryGetValue(kv.Key, out var prev);
            bool novel = !before.ContainsKey(kv.Key);
            bool endChanged = novel || (prev.end - kv.Value.end).sqrMagnitude > 1e-12f;
            if (!endChanged) continue;
            float leg = Vector3.Distance(kv.Value.start, kv.Value.end);
            Debug.Log($"[break-σ-relax] RigidCleavage append center={FormatBreakBondCenterBrief(center)} atom={kv.Key.name}(Z={kv.Key.AtomicNumber}) leg={leg:F5} novel={novel}");
        }
    }

    static string FormatRefWorldBrief(Vector3? refW)
    {
        if (!refW.HasValue) return "null";
        var w = refW.Value;
        return $"({w.x:F3},{w.y:F3},{w.z:F3})";
    }

    /// <summary>
    /// Full σ cleavage: fragment orbitals that stay fixed in step-2 (redist / bond→slot). Set is built in <see cref="BreakBond"/>
    /// from the two known lobe references (0e fragments only).
    /// </summary>
    static HashSet<ElectronOrbitalFunction> BuildSigmaCleavagePinnedEmptyOrbitals(
        bool sigmaCleavageBetweenPartners,
        ElectronOrbitalFunction returnedBondLobe,
        ElectronOrbitalFunction spawnedBondLobe)
    {
        if (!sigmaCleavageBetweenPartners) return null;
        var set = new HashSet<ElectronOrbitalFunction>();
        if (returnedBondLobe != null && returnedBondLobe.ElectronCount == 0) set.Add(returnedBondLobe);
        if (spawnedBondLobe != null && spawnedBondLobe.ElectronCount == 0) set.Add(spawnedBondLobe);
        return set;
    }

    /// <summary>After <see cref="BreakBond"/>, when σ still links the two centers (π-only step), return the other atom for VSEPR ref-axis hints. Null when fully cleaved or nucleus unknown.</summary>
    static AtomFunction SurvivingSigmaPartnerAfterBreakStep(AtomFunction nucleus, AtomFunction atomA, AtomFunction atomB, bool sigmaCleavageBetweenPartners)
    {
        if (sigmaCleavageBetweenPartners || nucleus == null) return null;
        if (nucleus == atomA && atomB != null && nucleus.GetBondsTo(atomB) > 0) return atomB;
        if (nucleus == atomB && atomA != null && nucleus.GetBondsTo(atomA) > 0) return atomA;
        return null;
    }

    /// <summary>Logs <see cref="AtomFunction.RedistributeOrbitals"/> args for bond-break paths when <see cref="DebugLogBreakBondSigmaRelaxWhy"/> is on.</summary>
    static void LogBreakBondRedistributeCall(
        string phase,
        AtomFunction atom,
        float? piBondAngle,
        Vector3? refBondWorld,
        bool relaxCoplanarSigmaToTetrahedral,
        bool skipLoneLobeLayout,
        HashSet<AtomFunction> pinAtomsForSigmaRelax,
        bool skipSigmaNeighborRelax,
        ElectronOrbitalFunction bondBreakGuideLoneOrbital,
        bool skipBondBreakSparseNonbondSpread = false)
    {
        if (!DebugLogBreakBondSigmaRelaxWhy) return;
        string pi = piBondAngle.HasValue ? $"{piBondAngle.Value:F1}°" : "null";
        int pinN = pinAtomsForSigmaRelax != null ? pinAtomsForSigmaRelax.Count : 0;
        Debug.Log(
            $"[break-redistribute] {phase} atom={FormatBreakBondCenterBrief(atom)} pi∠={pi} refW={FormatRefWorldBrief(refBondWorld)} " +
            $"relaxCoplanarΣ→Tet={relaxCoplanarSigmaToTetrahedral} skipLoneLayout={skipLoneLobeLayout} skipΣNeigh={skipSigmaNeighborRelax} " +
            $"skipSparseSpread={skipBondBreakSparseNonbondSpread} pins={pinN} guideOrb={(bondBreakGuideLoneOrbital != null)}");
    }

    static List<(AtomFunction atom, Vector3 pos, Quaternion rot)> SnapshotMoleculeAtoms(IEnumerable<AtomFunction> atoms)
    {
        var list = new List<(AtomFunction, Vector3, Quaternion)>();
        foreach (var a in atoms)
            if (a != null) list.Add((a, a.transform.position, a.transform.rotation));
        return list;
    }

    static void RestoreMoleculeAtoms(List<(AtomFunction atom, Vector3 pos, Quaternion rot)> snap)
    {
        foreach (var (atom, pos, rot) in snap)
            if (atom != null) atom.transform.SetPositionAndRotation(pos, rot);
    }

    static void CollectMoleculeAtoms(AtomFunction atom, HashSet<AtomFunction> dst)
    {
        if (atom == null) return;
        foreach (var a in atom.GetConnectedMolecule())
            if (a != null) dst.Add(a);
    }

    static List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> FilterRedistExcludingBreakOrbitals(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB)
    {
        var r = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        foreach (var e in list)
        {
            if (e.orb != null && e.orb != orbA && e.orb != orbB)
                r.Add(e);
        }
        return r;
    }

    /// <summary>
    /// Bond-break triage: set redistribute target pose to current locals for <b>occupied</b> nucleus-parented lobes only
    /// (see break coroutine).
    /// </summary>
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

    /// <summary>Set redist target pose to current locals for all nucleus-parented lobes in <paramref name="list"/>.</summary>
    static void NeutralizeAllRedistTargetsToCurrentLocals(
        AtomFunction nucleus,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list)
    {
        if (nucleus == null || list == null || list.Count == 0) return;
        for (int i = 0; i < list.Count; i++)
        {
            var (o, _, _) = list[i];
            if (o == null) continue;
            if (o.transform.parent != nucleus.transform) continue;
            list[i] = (o, o.transform.localPosition, o.transform.localRotation);
        }
    }

    /// <summary>Set redist targets to current locals except explicitly kept orbitals.</summary>
    static void NeutralizeRedistTargetsToCurrentLocalsExcept(
        AtomFunction nucleus,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list,
        HashSet<ElectronOrbitalFunction> keepOrbs)
    {
        if (nucleus == null || list == null || list.Count == 0) return;
        for (int i = 0; i < list.Count; i++)
        {
            var (o, _, _) = list[i];
            if (o == null) continue;
            if (o.transform.parent != nucleus.transform) continue;
            if (keepOrbs != null && keepOrbs.Contains(o)) continue;
            list[i] = (o, o.transform.localPosition, o.transform.localRotation);
        }
    }

    /// <summary>σ-relax end poses for bond-break animation: tet (AX₃E), linear, open-from-linear, trigonal (skipped for 3σ+carbocation empty), then sp² σ placement (trigonal carbocation / tet radical).
    /// <see cref="AtomFunction.TryAppendRigidSigmaCleavageSubstituentAtomMoves"/> runs only when sp² yields no moves — otherwise its tet rotation about ref would overwrite CH₃⁺ trigonal H targets.
    /// Use <see cref="DebugLogBreakBondSigmaRelaxWhy"/> to log which branches fire and max target displacement (even when already ~tetrahedral, remapping can yield non-zero Δ).</summary>
    static Dictionary<AtomFunction, (Vector3 start, Vector3 end)> BuildSigmaRelaxMovesForBreakBond(
        AtomFunction atomA,
        AtomFunction atomB,
        float? piAngleA, Vector3? refA, int piAfterA,
        float? piAngleB, Vector3? refB, int piAfterB,
        HashSet<AtomFunction> pinSigmaRelaxAtoms)
    {
        var moves = new Dictionary<AtomFunction, (Vector3 start, Vector3 end)>();
        void Collect(AtomFunction atom, float? piAngle, Vector3? refWorld, int piAfter)
        {
            if (atom == null) return;
            if (DebugLogBreakBondSigmaRelaxWhy)
            {
                string rw = "null";
                if (refWorld.HasValue)
                {
                    var w = refWorld.Value;
                    rw = $"({w.x:F3},{w.y:F3},{w.z:F3})";
                }
                Debug.Log($"[break-σ-relax] Collect begin center={FormatBreakBondCenterBrief(atom)} piAfter={piAfter} refWorld={rw}");
            }
            Vector3 refLocal = atom.GetRedistributeReferenceLocal(piAfter == 0 ? piAngle : null, piAfter == 0 ? refWorld : null);
            if (atom.TryComputeCoplanarTetrahedralSigmaNeighborRelaxTargets(refLocal, out var targets, pinSigmaRelaxAtoms) && targets != null)
            {
                LogBreakBondSigmaRelaxBranch("CoplanarTetrahedral", atom, targets);
                foreach (var (n, end) in targets)
                    moves[n] = (n.transform.position, end);
            }
            if (atom.TryComputeLinearSigmaNeighborRelaxTargets(refLocal, out var lin, pinSigmaRelaxAtoms) && lin != null)
            {
                LogBreakBondSigmaRelaxBranch("LinearSigma", atom, lin);
                foreach (var (n, end) in lin)
                    moves[n] = (n.transform.position, end);
            }
            if (atom.TryComputeOpenedTrigonalSigmaNeighborRelaxTargetsFromLinear(refLocal, out var opened, pinSigmaRelaxAtoms) && opened != null)
            {
                LogBreakBondSigmaRelaxBranch("OpenedTrigonalFromLinear", atom, opened);
                foreach (var (n, end) in opened)
                    moves[n] = (n.transform.position, end);
            }
            // Match RedistributeOrbitals3D: generic trigonal relax places one σ along ref — skip for CH₃⁺ / CH₂⁺ carbocation; sp² placement runs next.
            if (!atom.IsSp2BondBreakEmptyAlongRefCase() && !atom.IsBondBreakTrigonalPlanarFrameworkCase() &&
                atom.TryComputeTrigonalPlanarSigmaNeighborRelaxTargets(refLocal, out var trig) && trig != null)
            {
                LogBreakBondSigmaRelaxBranch("TrigonalPlanarSigma", atom, trig);
                foreach (var (n, end) in trig)
                    moves[n] = (n.transform.position, end);
            }
            List<(AtomFunction neighbor, Vector3 targetWorld)> sp2cc = null;
            bool sp2Moves =
                piAfter == 0 && refWorld.HasValue
                && atom.TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets(refWorld, pinSigmaRelaxAtoms, out sp2cc)
                && sp2cc != null && sp2cc.Count > 0;
            if (sp2Moves)
            {
                LogBreakBondSigmaRelaxBranch("Sp2TrigonalPlanarBreak", atom, sp2cc);
                foreach (var (n, end) in sp2cc)
                    moves[n] = (n.transform.position, end);
            }
            else if (piAfter == 0 && refWorld.HasValue && refWorld.Value.sqrMagnitude >= 0.01f)
            {
                Dictionary<AtomFunction, (Vector3 start, Vector3 end)> beforeRigid = null;
                if (DebugLogBreakBondSigmaRelaxWhy)
                {
                    beforeRigid = new Dictionary<AtomFunction, (Vector3 start, Vector3 end)>(moves.Count);
                    foreach (var kv in moves)
                        beforeRigid[kv.Key] = kv.Value;
                }
                atom.TryAppendRigidSigmaCleavageSubstituentAtomMoves(refWorld.Value, moves);
                if (beforeRigid != null)
                    LogBreakBondSigmaRelaxRigidDelta(atom, moves, beforeRigid);
            }
        }
        Collect(atomA, piAngleA, refA, piAfterA);
        Collect(atomB, piAngleB, refB, piAfterB);
        if (DebugLogBreakBondSigmaRelaxWhy)
            Debug.Log($"[break-σ-relax] BuildSigmaRelaxMovesForBreakBond done totalNuclearTargets={moves.Count}");
        return moves;
    }

    /// <summary>On each atom, the lobe parented there that came from the broken bond (return orbital vs new orbital) — VSEPR guide pin.
    /// <paramref name="nucleusForOrbOnA"/> / <paramref name="nucleusForOrbOnB"/> are the nuclei that will own <paramref name="orbOnA"/> / <paramref name="orbOnB"/> after break (e.g. <c>returnOrbitalTo</c> / <c>otherAtom</c>, or <c>parentA</c> / <c>parentB</c> in animation). They need not match <see cref="CovalentBond.AtomA"/> / <see cref="CovalentBond.AtomB"/> order.</summary>
    static ElectronOrbitalFunction BondBreakGuideLoneOrbitalOnAtom(AtomFunction atom, ElectronOrbitalFunction orbOnA, ElectronOrbitalFunction orbOnB, AtomFunction nucleusForOrbOnA, AtomFunction nucleusForOrbOnB)
    {
        if (atom == null) return null;
        static bool IsOnAtom(ElectronOrbitalFunction o, AtomFunction a) =>
            o != null && a != null && (o.transform.parent == a.transform || o.BondedAtom == a);
        // Prefer the actual 0e ex-bond lobe on this nucleus for σ-cleavage / OrientEmpty (skipOrbital), not the 1e/2e returned lobe.
        ElectronOrbitalFunction emptyOnAtom = null;
        int emptyBestId = int.MinValue;
        void ConsiderBreakFragmentEmpty(ElectronOrbitalFunction o)
        {
            if (o == null || o.Bond != null || !IsOnAtom(o, atom) || o.ElectronCount != 0) return;
            int id = o.GetInstanceID();
            if (emptyOnAtom == null || id > emptyBestId)
            {
                emptyBestId = id;
                emptyOnAtom = o;
            }
        }
        ConsiderBreakFragmentEmpty(orbOnA);
        ConsiderBreakFragmentEmpty(orbOnB);
        if (emptyOnAtom != null) return emptyOnAtom;
        if (IsOnAtom(orbOnA, atom) && IsOnAtom(orbOnB, atom))
            return orbOnA.GetInstanceID() >= orbOnB.GetInstanceID() ? orbOnA : orbOnB;
        if (orbOnA != null && orbOnA.transform.parent == atom.transform) return orbOnA;
        if (orbOnB != null && orbOnB.transform.parent == atom.transform) return orbOnB;
        if (IsOnAtom(orbOnA, atom)) return orbOnA;
        if (IsOnAtom(orbOnB, atom)) return orbOnB;

        static CovalentBond BondFromOrbParent(ElectronOrbitalFunction o)
        {
            if (o == null || o.transform.parent == null) return null;
            return o.transform.parent.GetComponent<CovalentBond>();
        }

        var bA = BondFromOrbParent(orbOnA);
        var bB = BondFromOrbParent(orbOnB);

        // Both break lobes still under the bond: map by intended parent nuclei, not bond.AtomA/AtomB order.
        if (bA != null && bB != null && bA == bB && bA.AtomA != null && bA.AtomB != null)
        {
            if (nucleusForOrbOnA != null && nucleusForOrbOnB != null)
            {
                if (atom == nucleusForOrbOnA) return orbOnA;
                if (atom == nucleusForOrbOnB) return orbOnB;
                return null;
            }
            if (atom == bA.AtomA) return orbOnA;
            if (atom == bA.AtomB) return orbOnB;
            return null;
        }

        // One lobe still on the bond, the other already parented to a nucleus — guide for the nucleus without the spawned lobe is the bond-side orbital.
        if (bA != null && orbOnA != null && orbOnB != null)
        {
            Transform pB = orbOnB.transform.parent;
            if (pB != null && pB != orbOnA.transform.parent && BondFromOrbParent(orbOnB) == null)
            {
                var hostOfB = pB.GetComponent<AtomFunction>();
                if (hostOfB != null && bA.AtomA != null && bA.AtomB != null && (hostOfB == bA.AtomA || hostOfB == bA.AtomB))
                {
                    AtomFunction otherNucleus = null;
                    if (nucleusForOrbOnA != null && nucleusForOrbOnB != null)
                    {
                        if (hostOfB == nucleusForOrbOnA) otherNucleus = nucleusForOrbOnB;
                        else if (hostOfB == nucleusForOrbOnB) otherNucleus = nucleusForOrbOnA;
                    }
                    else
                        otherNucleus = bA.AtomA == hostOfB ? bA.AtomB : bA.AtomA;
                    if (otherNucleus != null && atom == otherNucleus) return orbOnA;
                }
            }
        }

        if (bB != null && orbOnA != null && orbOnB != null)
        {
            Transform pA = orbOnA.transform.parent;
            if (pA != null && pA != orbOnB.transform.parent && BondFromOrbParent(orbOnA) == null)
            {
                var hostOfA = pA.GetComponent<AtomFunction>();
                if (hostOfA != null && bB.AtomA != null && bB.AtomB != null && (hostOfA == bB.AtomA || hostOfA == bB.AtomB))
                {
                    AtomFunction otherNucleus = null;
                    if (nucleusForOrbOnA != null && nucleusForOrbOnB != null)
                    {
                        if (hostOfA == nucleusForOrbOnA) otherNucleus = nucleusForOrbOnB;
                        else if (hostOfA == nucleusForOrbOnB) otherNucleus = nucleusForOrbOnA;
                    }
                    else
                        otherNucleus = bB.AtomA == hostOfA ? bB.AtomB : bB.AtomA;
                    if (otherNucleus != null && atom == otherNucleus) return orbOnB;
                }
            }
        }

        return null;
    }

    // Prediction preview path removed: break animation now uses only post-break actual state.

    /// <summary>Same end state as <see cref="CoAnimateBreakBondRedistribution"/> without lerping — for breaks where one atom is destroyed in the same frame (edit-mode H replacement, eraser).</summary>
    /// <param name="sigmaCleavagePinnedEmptyOrbitals">From <see cref="BuildSigmaCleavagePinnedEmptyOrbitals"/>; orbitals in this set skip pre-redist bond→slot snap so their break-layout pose is preserved into Redistribute/OrientEmpty.</param>
    static void ApplyInstantBreakBondRedistribution3D(
        AtomFunction atomA,
        AtomFunction atomB,
        int piBeforeA,
        int piBeforeB,
        float? piAngleA, Vector3? refA, int piAfterA,
        float? piAngleB, Vector3? refB, int piAfterB,
        ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB,
        AtomFunction parentA, AtomFunction parentB,
        (Vector3 position, Quaternion rotation) slotLocalA,
        (Vector3 position, Quaternion rotation) slotLocalB,
        HashSet<AtomFunction> pinSigmaRelaxAtoms,
        bool sigmaCleavageBetweenPartners,
        HashSet<ElectronOrbitalFunction> sigmaCleavagePinnedEmptyOrbitals,
        bool preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak)
    {
        bool sigmaOnlyUnchangedPiBoth = piBeforeA == piAfterA && piBeforeB == piAfterB;
        var moves = BuildSigmaRelaxMovesForBreakBond(atomA, atomB, piAngleA, refA, piAfterA, piAngleB, refB, piAfterB, pinSigmaRelaxAtoms);
        if (!preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak)
        {
            foreach (var kv in moves)
            {
                if (kv.Key != null)
                    kv.Key.transform.position = kv.Value.end;
            }
        }
        float psiInstA = 0f, psiInstB = 0f;
        bool breakNewmanInstA = OrbitalAngleUtility.UseFull3DOrbitalGeometry && atomA != null && atomA.GetPiBondCount() == 0
            && atomA.TryComputeNewmanStaggerPsi(atomB, atomA.GetBondsTo(atomB) > 0, out psiInstA);
        bool breakNewmanInstB = OrbitalAngleUtility.UseFull3DOrbitalGeometry && atomB != null && atomB.GetPiBondCount() == 0
            && atomB.TryComputeNewmanStaggerPsi(atomA, atomB.GetBondsTo(atomA) > 0, out psiInstB);
        AtomFunction partnerInstA = parentA == atomA ? atomB : atomA;
        AtomFunction partnerInstB = parentB == atomA ? atomB : atomA;
        var slotCorInstA = parentA != null
            ? CorrectBreakGuideSlotTowardPartner(slotLocalA, parentA, partnerInstA)
            : slotLocalA;
        var slotCorInstB = parentB != null
            ? CorrectBreakGuideSlotTowardPartner(slotLocalB, parentB, partnerInstB)
            : slotLocalB;
        (Vector3 position, Quaternion rotation) slotFinInstA = slotCorInstA;
        (Vector3 position, Quaternion rotation) slotFinInstB = slotCorInstB;
        if (parentA != null && orbA != null)
        {
            if (parentA == atomA && breakNewmanInstA && AtomFunction.TwistSlotLocalForNewmanStaggerEnd(parentA, atomB, psiInstA, slotCorInstA.position, slotCorInstA.rotation, out var spa, out var sra))
                slotFinInstA = (spa, sra);
            else if (parentA == atomB && breakNewmanInstB && AtomFunction.TwistSlotLocalForNewmanStaggerEnd(parentA, atomA, psiInstB, slotCorInstA.position, slotCorInstA.rotation, out var spa2, out var sra2))
                slotFinInstA = (spa2, sra2);
        }
        if (parentB != null && orbB != null)
        {
            if (parentB == atomA && breakNewmanInstA && AtomFunction.TwistSlotLocalForNewmanStaggerEnd(parentB, atomB, psiInstA, slotCorInstB.position, slotCorInstB.rotation, out var spb, out var srb))
                slotFinInstB = (spb, srb);
            else if (parentB == atomB && breakNewmanInstB && AtomFunction.TwistSlotLocalForNewmanStaggerEnd(parentB, atomA, psiInstB, slotCorInstB.position, slotCorInstB.rotation, out var spb2, out var srb2))
                slotFinInstB = (spb2, srb2);
        }
        bool skipInstSlotA = sigmaCleavagePinnedEmptyOrbitals != null && orbA != null && sigmaCleavagePinnedEmptyOrbitals.Contains(orbA);
        bool skipInstSlotB = sigmaCleavagePinnedEmptyOrbitals != null && orbB != null && sigmaCleavagePinnedEmptyOrbitals.Contains(orbB);
        if (orbA != null && parentA != null && !skipInstSlotA)
        {
            // Do not use GetOrbitalTargetWorldState here: that pose is on the bond cylinder (midpoint + line offset), not on
            // the nucleus — parenting to the heavy atom leaves the lobe "stuck" in space and +X can face away from the ex-partner.
            bool preserveHeavyGuide = preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak && parentA.AtomicNumber > 1;
            if (preserveHeavyGuide)
            {
                Vector3? refTowardExPartner = parentA == atomA ? refA : refB;
                if (refTowardExPartner.HasValue && refTowardExPartner.Value.sqrMagnitude >= 0.01f)
                {
                    Vector3 dLocal = parentA.transform.InverseTransformDirection(refTowardExPartner.Value.normalized);
                    if (dLocal.sqrMagnitude < 1e-8f) dLocal = Vector3.right;
                    else dLocal.Normalize();
                    var (lp, lr) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(dLocal, parentA.BondRadius, orbA.transform.localRotation);
                    orbA.transform.localPosition = lp;
                    orbA.transform.localRotation = lr;
                }
                else
                {
                    orbA.transform.localPosition = slotFinInstA.position;
                    orbA.transform.localRotation = slotFinInstA.rotation;
                }
            }
            else
            {
                orbA.transform.localPosition = slotFinInstA.position;
                orbA.transform.localRotation = slotFinInstA.rotation;
            }
        }
        if (orbB != null && parentB != null && !skipInstSlotB)
        {
            orbB.transform.localPosition = slotFinInstB.position;
            orbB.transform.localRotation = slotFinInstB.rotation;
        }

        LogBreakGuideOrbitalsPose("instant_afterSlotSnap", atomA, atomB, orbA, orbB, parentA, parentB);
        LogBreakEmptyTeleportBoth("instant_postSlotSnap", atomA, atomB, piAfterA == 0 ? refA : null, piAfterB == 0 ? refB : null, orbA, orbB, parentA, parentB);

        bool skipFinalSigmaRelax = moves.Count > 0;
        bool heavyA = atomA != null && atomA.AtomicNumber > 1;
        bool heavyB = atomB != null && atomB.AtomicNumber > 1;
        bool minimalA = preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak && heavyA;
        bool minimalB = preserveHeavyFrameworkAfterInstantHydrogenPartnerBreak && heavyB;

        var gInstA = BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB, parentA, parentB);
        var gInstB = BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB, parentA, parentB);
        var instPartnerHintA = SurvivingSigmaPartnerAfterBreakStep(atomA, atomA, atomB, sigmaCleavageBetweenPartners);
        var instPartnerHintB = SurvivingSigmaPartnerAfterBreakStep(atomB, atomA, atomB, sigmaCleavageBetweenPartners);
        LogBreakBondRedistributeCall("InstantBreak3D", atomA, piAngleA, refA, !minimalA, sigmaOnlyUnchangedPiBoth, pinSigmaRelaxAtoms, skipFinalSigmaRelax || minimalA, gInstA);
        atomA?.RedistributeOrbitals(
            piAngleA,
            refA,
            relaxCoplanarSigmaToTetrahedral: !minimalA,
            skipLoneLobeLayout: sigmaOnlyUnchangedPiBoth,
            pinAtomsForSigmaRelax: pinSigmaRelaxAtoms,
            skipSigmaNeighborRelax: skipFinalSigmaRelax || minimalA,
            bondBreakGuideLoneOrbital: gInstA,
            newSigmaBondPartnerHint: instPartnerHintA,
            bondBreakIsSigmaCleavageBetweenFormerPartners: sigmaCleavageBetweenPartners);
        LogBreakBondRedistributeCall("InstantBreak3D", atomB, piAngleB, refB, !minimalB, sigmaOnlyUnchangedPiBoth, pinSigmaRelaxAtoms, skipFinalSigmaRelax || minimalB, gInstB);
        atomB?.RedistributeOrbitals(
            piAngleB,
            refB,
            relaxCoplanarSigmaToTetrahedral: !minimalB,
            skipLoneLobeLayout: sigmaOnlyUnchangedPiBoth,
            pinAtomsForSigmaRelax: pinSigmaRelaxAtoms,
            skipSigmaNeighborRelax: skipFinalSigmaRelax || minimalB,
            bondBreakGuideLoneOrbital: gInstB,
            newSigmaBondPartnerHint: instPartnerHintB,
            bondBreakIsSigmaCleavageBetweenFormerPartners: sigmaCleavageBetweenPartners);

        LogBreakEmptyTeleportBoth("instant_preRedistributeOrbitals", atomA, atomB, piAfterA == 0 ? refA : null, piAfterB == 0 ? refB : null, orbA, orbB, parentA, parentB);
        LogBreakGuideOrbitalsPose("instant_afterRedistributeOrbitals", atomA, atomB, orbA, orbB, parentA, parentB);
        LogBreakEmptyTeleportBoth("instant_postRedistributeOrbitals", atomA, atomB, piAfterA == 0 ? refA : null, piAfterB == 0 ? refB : null, orbA, orbB, parentA, parentB);

        var guideInstA = BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB, parentA, parentB);
        var guideInstB = BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB, parentA, parentB);
        if (!minimalA)
            atomA?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(refA, guideInstA, sigmaCleavageBetweenPartners);
        if (!minimalB)
            atomB?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(refB, guideInstB, sigmaCleavageBetweenPartners);

        LogBreakGuideOrbitalsPose("instant_afterOrientEmpty", atomA, atomB, orbA, orbB, parentA, parentB);
        LogBreakEmptyTeleportBoth("instant_postOrientEmpty", atomA, atomB, piAfterA == 0 ? refA : null, piAfterB == 0 ? refB : null, orbA, orbB, parentA, parentB);

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.RefreshElectronSyncOnBondedOrbitals();
        atomB?.RefreshElectronSyncOnBondedOrbitals();

        LogBreakGuideOrbitalsPose("instant_final_afterRefreshElectronSync", atomA, atomB, orbA, orbB, parentA, parentB);
        LogBreakEmptyTeleportBoth("instant_postRefreshElectronSync", atomA, atomB, piAfterA == 0 ? refA : null, piAfterB == 0 ? refB : null, orbA, orbB, parentA, parentB);

        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    static IEnumerator CoAnimateBreakBondRedistribution(
        AtomFunction atomA,
        AtomFunction atomB,
        int piBeforeA,
        int piBeforeB,
        float? piAngleA, Vector3? refA, int piAfterA,
        float? piAngleB, Vector3? refB, int piAfterB,
        ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB,
        AtomFunction parentA, AtomFunction parentB,
        (Vector3 position, Quaternion rotation) slotLocalA,
        (Vector3 position, Quaternion rotation) slotLocalB,
        Vector3 bondWorldPos, Quaternion bondWorldRot,
        HashSet<AtomFunction> pinSigmaRelaxAtoms,
        bool userDragBondCylinderBreak,
        bool sigmaCleavageBetweenPartners,
        HashSet<ElectronOrbitalFunction> sigmaCleavagePinnedEmptyOrbitals)
    {
        // σ-only break (π unchanged on both ends): ensure bond→slot orbital lerp runs; otherwise epsilon + empty GetRedistributeTargets can skip the whole animation loop.
        bool sigmaOnlyUnchangedPiBoth = piBeforeA == piAfterA && piBeforeB == piAfterB;

        var moves = BuildSigmaRelaxMovesForBreakBond(atomA, atomB, piAngleA, refA, piAfterA, piAngleB, refB, piAfterB, pinSigmaRelaxAtoms);
        bool sigmaRelaxPreApplied = moves.Count > 0;

        const float posEps = 0.0004f;
        const float rotEps = 0.35f;

        // π count changed on an atom → use normal GetRedistributeTargets π check; σ-only break → bypass so layout still runs.
        bool topologyBypassA = piBeforeA == piAfterA;
        bool topologyBypassB = piBeforeB == piAfterB;

        var snapAtoms = new HashSet<AtomFunction>();
        CollectMoleculeAtoms(atomA, snapAtoms);
        CollectMoleculeAtoms(atomB, snapAtoms);
        foreach (var kv in moves)
            if (kv.Key != null) snapAtoms.Add(kv.Key);
        var moleculeSnap = SnapshotMoleculeAtoms(snapAtoms);

        foreach (var kv in moves)
            kv.Key.transform.position = kv.Value.end;
        if (orbA != null && parentA != null)
        {
            orbA.transform.localPosition = slotLocalA.position;
            orbA.transform.localRotation = slotLocalA.rotation;
        }
        if (orbB != null && parentB != null)
        {
            orbB.transform.localPosition = slotLocalB.position;
            orbB.transform.localRotation = slotLocalB.rotation;
        }

        var gAnimA = BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB, parentA, parentB);
        var gAnimB = BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB, parentA, parentB);
        // Three-step deterministic path:
        // 1) Build final configuration for this break step.
        // 2) Snapshot final non-bond targets (occupied + empty).
        // 3) Animate only toward those frozen targets (no competing target builders in this coroutine).
        var phaseAtomSnap = SnapshotMoleculeAtoms(snapAtoms);
        var phaseOrbitalSnap = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
        foreach (var a in snapAtoms)
            a?.AppendBondedOrbitalLocalSnapshot(phaseOrbitalSnap);
        bool skipFinalSigmaRelaxPreview = sigmaRelaxPreApplied;
        bool skipSparseSpreadPreview = sigmaOnlyUnchangedPiBoth;
        var gFinalA = BondBreakGuideLoneOrbitalOnAtom(atomA, orbA, orbB, parentA, parentB);
        var gFinalB = BondBreakGuideLoneOrbitalOnAtom(atomB, orbA, orbB, parentA, parentB);
        var coPartnerHintA = SurvivingSigmaPartnerAfterBreakStep(atomA, atomA, atomB, sigmaCleavageBetweenPartners);
        var coPartnerHintB = SurvivingSigmaPartnerAfterBreakStep(atomB, atomA, atomB, sigmaCleavageBetweenPartners);
        atomA?.RedistributeOrbitals(piAngleA, refA, relaxCoplanarSigmaToTetrahedral: true, skipLoneLobeLayout: sigmaOnlyUnchangedPiBoth, pinAtomsForSigmaRelax: pinSigmaRelaxAtoms, skipSigmaNeighborRelax: skipFinalSigmaRelaxPreview, bondBreakGuideLoneOrbital: gFinalA, newSigmaBondPartnerHint: coPartnerHintA, skipBondBreakSparseNonbondSpread: skipSparseSpreadPreview, bondBreakIsSigmaCleavageBetweenFormerPartners: sigmaCleavageBetweenPartners);
        atomB?.RedistributeOrbitals(piAngleB, refB, relaxCoplanarSigmaToTetrahedral: true, skipLoneLobeLayout: sigmaOnlyUnchangedPiBoth, pinAtomsForSigmaRelax: pinSigmaRelaxAtoms, skipSigmaNeighborRelax: skipFinalSigmaRelaxPreview, bondBreakGuideLoneOrbital: gFinalB, newSigmaBondPartnerHint: coPartnerHintB, skipBondBreakSparseNonbondSpread: skipSparseSpreadPreview, bondBreakIsSigmaCleavageBetweenFormerPartners: sigmaCleavageBetweenPartners);
        atomA?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(refA, gFinalA, sigmaCleavageBetweenPartners);
        atomB?.OrientEmptyNonbondedOrbitalsPerpendicularToFramework(refB, gFinalB, sigmaCleavageBetweenPartners);
        var frozenEmptyA = new List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)>();
        var frozenEmptyB = new List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)>();
        var frozenNonbondA = new List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)>();
        var frozenNonbondB = new List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)>();
        atomA?.AppendCurrentEmptyNonbondOrbitalTargets(frozenEmptyA);
        atomB?.AppendCurrentEmptyNonbondOrbitalTargets(frozenEmptyB);
        atomA?.AppendCurrentNonbondOrbitalTargets(frozenNonbondA);
        atomB?.AppendCurrentNonbondOrbitalTargets(frozenNonbondB);
        if (DebugLogBreakBondStampSigmaFilterDetail && sigmaCleavageBetweenPartners)
        {
            Debug.Log("[break-motion] CoAnimate frozenNonbondA count=" + frozenNonbondA.Count);
            for (int fi = 0; fi < frozenNonbondA.Count; fi++)
            {
                var fo = frozenNonbondA[fi].orb;
                if (fo == null) continue;
                Debug.Log("[break-motion] frozenNonbondA i=" + fi + " id=" + fo.GetInstanceID() + " ec=" + fo.ElectronCount);
            }
            Debug.Log("[break-motion] CoAnimate frozenNonbondB count=" + frozenNonbondB.Count);
            for (int fi = 0; fi < frozenNonbondB.Count; fi++)
            {
                var fo = frozenNonbondB[fi].orb;
                if (fo == null) continue;
                Debug.Log("[break-motion] frozenNonbondB i=" + fi + " id=" + fo.GetInstanceID() + " ec=" + fo.ElectronCount);
            }
        }
        RestoreMoleculeAtoms(phaseAtomSnap);
        AtomFunction.RestoreBondedOrbitalLocalSnapshot(phaseOrbitalSnap);
        var redistA = new List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)>();
        var redistB = new List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)>();
        const float frozenPosEps = 0.0004f;
        const float frozenRotEps = 0.35f;
        foreach (var t in frozenNonbondA)
        {
            if (t.orb == null || atomA == null || t.orb.transform.parent != atomA.transform) continue;
            if (Vector3.SqrMagnitude(t.orb.transform.localPosition - t.pos) > frozenPosEps * frozenPosEps ||
                Quaternion.Angle(t.orb.transform.localRotation, t.rot) > frozenRotEps)
                redistA.Add(t);
        }
        foreach (var t in frozenNonbondB)
        {
            if (t.orb == null || atomB == null || t.orb.transform.parent != atomB.transform) continue;
            if (Vector3.SqrMagnitude(t.orb.transform.localPosition - t.pos) > frozenPosEps * frozenPosEps ||
                Quaternion.Angle(t.orb.transform.localRotation, t.rot) > frozenRotEps)
                redistB.Add(t);
        }
        int redistRawACount = redistA.Count;
        int redistRawBCount = redistB.Count;
        if (DebugLogBreakBondStampSigmaFilterDetail && sigmaCleavageBetweenPartners)
        {
            string FixedGuideReason(ElectronOrbitalFunction o)
            {
                if (o == null) return "nullOrb";
                if (sigmaCleavagePinnedEmptyOrbitals != null && sigmaCleavagePinnedEmptyOrbitals.Contains(o)) return "explicitPin";
                return "notPinned";
            }
            for (int ri = 0; ri < redistA.Count; ri++)
            {
                var ro = redistA[ri].orb;
                if (ro == null) continue;
                Debug.Log("[break-motion] sigma redistRawA i=" + ri + " id=" + ro.GetInstanceID() + " ec=" + ro.ElectronCount + " pinReason=" + FixedGuideReason(ro));
            }
            for (int ri = 0; ri < redistB.Count; ri++)
            {
                var ro = redistB[ri].orb;
                if (ro == null) continue;
                Debug.Log("[break-motion] sigma redistRawB i=" + ri + " id=" + ro.GetInstanceID() + " ec=" + ro.ElectronCount + " pinReason=" + FixedGuideReason(ro));
            }
        }
        if (DebugLogBreakBondMotionSources)
        {
            string RedistIds(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list)
            {
                if (list == null || list.Count == 0) return "";
                var ids = new List<string>(list.Count);
                foreach (var e in list)
                    ids.Add(e.orb != null ? e.orb.GetInstanceID().ToString() : "null");
                return string.Join(",", ids);
            }
            string PinIds()
            {
                if (sigmaCleavagePinnedEmptyOrbitals == null || sigmaCleavagePinnedEmptyOrbitals.Count == 0) return "";
                var ids = new List<string>(sigmaCleavagePinnedEmptyOrbitals.Count);
                foreach (var p in sigmaCleavagePinnedEmptyOrbitals)
                    ids.Add(p != null ? p.GetInstanceID().ToString() : "null");
                return string.Join(",", ids);
            }
            Debug.Log("[break-motion] CoAnimate redistRaw ids A=" + RedistIds(redistA) + " B=" + RedistIds(redistB));
            Debug.Log(
                "[break-motion] CoAnimate guide ids explicitPin=" + PinIds() +
                " gAnimA=" + (gAnimA != null ? gAnimA.GetInstanceID().ToString() : "null") +
                " gAnimB=" + (gAnimB != null ? gAnimB.GetInstanceID().ToString() : "null") +
                " gFinalA=" + (gFinalA != null ? gFinalA.GetInstanceID().ToString() : "null") +
                " gFinalB=" + (gFinalB != null ? gFinalB.GetInstanceID().ToString() : "null") +
                " orbA=" + (orbA != null ? orbA.GetInstanceID().ToString() : "null") +
                " orbB=" + (orbB != null ? orbB.GetInstanceID().ToString() : "null") +
                " sigmaCleavage=" + sigmaCleavageBetweenPartners);
        }
        // Full σ cleavage: explicit pin (BreakBond: 0e fragments) plus every other 0e non-bond — all skip step-2 redist lerp.
        // Pre-existing empty placeholders still differ frozen vs restored and enter redist; lerping them fights OrientEmpty and
        // the break fragment (failure.log: pinned -44242 but -44178 ec=0 still in redistB). Final pose for all frozen non-bond
        // targets runs in ApplyFrozenNonbondFinal below, so 0e do not need this lerp.
        // Also append BOTH break guide lobes (returned + spawned) to redist from frozenNonbond* so they smooth-lerp to Redistribute
        // preview poses. bond→slot uses slotFin (cylinder geometry), which can disagree with frozen — see failure.log: animOrbA
        // still true with large effective B-slot metric while pinned 0e skips bond→slot.
        if (sigmaCleavageBetweenPartners)
        {
            bool IsPinnedSigmaCleavageFragment(ElectronOrbitalFunction o) =>
                o != null && sigmaCleavagePinnedEmptyOrbitals != null && sigmaCleavagePinnedEmptyOrbitals.Contains(o);

            redistA.RemoveAll(e => IsPinnedSigmaCleavageFragment(e.orb));
            redistB.RemoveAll(e => IsPinnedSigmaCleavageFragment(e.orb));
            redistA.RemoveAll(e => e.orb != null && e.orb.ElectronCount == 0);
            redistB.RemoveAll(e => e.orb != null && e.orb.ElectronCount == 0);

            void AppendBreakGuideLerpFromFrozen(
                ElectronOrbitalFunction guide,
                AtomFunction host,
                List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> frozenOnHost,
                List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redistTarget)
            {
                if (guide == null || host == null) return;
                if (guide.transform.parent != host.transform) return;
                for (int fi = 0; fi < frozenOnHost.Count; fi++)
                {
                    if (frozenOnHost[fi].orb != guide) continue;
                    for (int ri = 0; ri < redistTarget.Count; ri++)
                    {
                        if (redistTarget[ri].orb == guide) return;
                    }
                    var f = frozenOnHost[fi];
                    redistTarget.Add((guide, f.pos, f.rot));
                    return;
                }
            }

            if (parentA != null && orbA != null)
            {
                if (parentA == atomA) AppendBreakGuideLerpFromFrozen(orbA, parentA, frozenNonbondA, redistA);
                else AppendBreakGuideLerpFromFrozen(orbA, parentA, frozenNonbondB, redistB);
            }
            if (parentB != null && orbB != null)
            {
                if (parentB == atomA) AppendBreakGuideLerpFromFrozen(orbB, parentB, frozenNonbondA, redistA);
                else AppendBreakGuideLerpFromFrozen(orbB, parentB, frozenNonbondB, redistB);
            }
            if (DebugLogBreakBondMotionSources)
            {
                string RedistIds(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list)
                {
                    if (list == null || list.Count == 0) return "";
                    var ids = new List<string>(list.Count);
                    foreach (var e in list)
                        ids.Add(e.orb != null ? e.orb.GetInstanceID().ToString() : "null");
                    return string.Join(",", ids);
                }
                Debug.Log("[break-motion] CoAnimate redistAfterSigmaPinFilter ids A=" + RedistIds(redistA) + " B=" + RedistIds(redistB));
            }
        }
        int redistFilteredACount = redistA.Count;
        int redistFilteredBCount = redistB.Count;
        int redistAfterAppendACount = redistA.Count;
        int redistAfterAppendBCount = redistB.Count;
        bool piStageA = atomA != null && atomA.GetPiBondCount() > 0;
        bool piStageB = atomB != null && atomB.GetPiBondCount() > 0;

        float psiBreakA = 0f, psiBreakB = 0f;
        bool breakNewmanA = OrbitalAngleUtility.UseFull3DOrbitalGeometry && atomA != null && atomA.GetPiBondCount() == 0
            && atomA.TryComputeNewmanStaggerPsi(atomB, atomA.GetBondsTo(atomB) > 0, out psiBreakA);
        bool breakNewmanB = OrbitalAngleUtility.UseFull3DOrbitalGeometry && atomB != null && atomB.GetPiBondCount() == 0
            && atomB.TryComputeNewmanStaggerPsi(atomA, atomB.GetBondsTo(atomA) > 0, out psiBreakB);

        AtomFunction partnerOfParentA = parentA == atomA ? atomB : atomA;
        AtomFunction partnerOfParentB = parentB == atomA ? atomB : atomA;
        var slotCorA = parentA != null
            ? CorrectBreakGuideSlotTowardPartner(slotLocalA, parentA, partnerOfParentA)
            : slotLocalA;
        var slotCorB = parentB != null
            ? CorrectBreakGuideSlotTowardPartner(slotLocalB, parentB, partnerOfParentB)
            : slotLocalB;

        (Vector3 position, Quaternion rotation) slotFinA = slotCorA;
        (Vector3 position, Quaternion rotation) slotFinB = slotCorB;
        if (parentA != null && orbA != null)
        {
            if (parentA == atomA && breakNewmanA && AtomFunction.TwistSlotLocalForNewmanStaggerEnd(parentA, atomB, psiBreakA, slotCorA.position, slotCorA.rotation, out var spa, out var sra))
                slotFinA = (spa, sra);
            else if (parentA == atomB && breakNewmanB && AtomFunction.TwistSlotLocalForNewmanStaggerEnd(parentA, atomA, psiBreakB, slotCorA.position, slotCorA.rotation, out var spa2, out var sra2))
                slotFinA = (spa2, sra2);
        }
        if (parentB != null && orbB != null)
        {
            if (parentB == atomA && breakNewmanA && AtomFunction.TwistSlotLocalForNewmanStaggerEnd(parentB, atomB, psiBreakA, slotCorB.position, slotCorB.rotation, out var spb, out var srb))
                slotFinB = (spb, srb);
            else if (parentB == atomB && breakNewmanB && AtomFunction.TwistSlotLocalForNewmanStaggerEnd(parentB, atomA, psiBreakB, slotCorB.position, slotCorB.rotation, out var spb2, out var srb2))
                slotFinB = (spb2, srb2);
        }

        RestoreMoleculeAtoms(moleculeSnap);
        bool IsSigmaCleavageFixedGuide(ElectronOrbitalFunction o) =>
            sigmaCleavageBetweenPartners
            && o != null
            && sigmaCleavagePinnedEmptyOrbitals != null
            && sigmaCleavagePinnedEmptyOrbitals.Contains(o);
        if (orbA != null && !IsSigmaCleavageFixedGuide(orbA))
            orbA.transform.SetPositionAndRotation(bondWorldPos, bondWorldRot);
        if (orbB != null && !IsSigmaCleavageFixedGuide(orbB))
            orbB.transform.SetPositionAndRotation(bondWorldPos, bondWorldRot);

        // Frozen preview (RedistributeOrbitals + OrientEmpty) is authoritative for non-bond targets. Lerping uses redist lists
        // built from preview vs restored pose — do not strip occupied-lobe motion when σ-relax had no nuclear moves (e.g.
        // σN=0 fragments after C–C homolysis would otherwise get hasRedistAnim=false and teleport at ApplyFrozenNonbondFinal).

        // Bond→slot lerp ends at Newman-adjusted locals when stagger applies; anim threshold uses restored parent + slotFin.
        Vector3 endWorldA0 = parentA != null && orbA != null
            ? parentA.transform.TransformPoint(slotFinA.position)
            : bondWorldPos;
        Quaternion endRotA0 = parentA != null && orbA != null
            ? parentA.transform.rotation * slotFinA.rotation
            : bondWorldRot;
        Vector3 endWorldB0 = parentB != null && orbB != null
            ? parentB.transform.TransformPoint(slotFinB.position)
            : bondWorldPos;
        Quaternion endRotB0 = parentB != null && orbB != null
            ? parentB.transform.rotation * slotFinB.rotation
            : bondWorldRot;
        bool animOrbA = orbA != null && parentA != null &&
            (Vector3.SqrMagnitude(bondWorldPos - endWorldA0) > posEps * posEps || Quaternion.Angle(bondWorldRot, endRotA0) > rotEps);
        bool animOrbB = orbB != null && parentB != null &&
            (Vector3.SqrMagnitude(bondWorldPos - endWorldB0) > posEps * posEps || Quaternion.Angle(bondWorldRot, endRotB0) > rotEps);
        // Break guides that became empty (0e) should not lerp bond→slot during step-2.
        // They are positioned by final Redistribute+OrientEmpty, which avoids transient center/slot drift.
        if (orbA != null && orbA.ElectronCount == 0) animOrbA = false;
        if (orbB != null && orbB.ElectronCount == 0) animOrbB = false;
        // Full σ cleavage: explicit pinned fragments skip bond→slot lerp (overrides threshold above if still true).
        if (sigmaCleavageBetweenPartners && sigmaCleavagePinnedEmptyOrbitals != null)
        {
            if (orbA != null && sigmaCleavagePinnedEmptyOrbitals.Contains(orbA)) animOrbA = false;
            if (orbB != null && sigmaCleavagePinnedEmptyOrbitals.Contains(orbB)) animOrbB = false;
        }
        // Staged π-break: if this break guide belongs to a π>0 center, keep it fixed through step-2.
        if (gAnimA != null)
        {
            bool guideOnPiStageA = (parentA == atomA && piStageA) || (parentA == atomB && piStageB);
            if (orbA == gAnimA && guideOnPiStageA) animOrbA = false;
            bool guideOnPiStageA2 = (parentB == atomA && piStageA) || (parentB == atomB && piStageB);
            if (orbB == gAnimA && guideOnPiStageA2) animOrbB = false;
        }
        if (gAnimB != null)
        {
            bool guideOnPiStageB = (parentA == atomA && piStageA) || (parentA == atomB && piStageB);
            if (orbA == gAnimB && guideOnPiStageB) animOrbA = false;
            bool guideOnPiStageB2 = (parentB == atomA && piStageA) || (parentB == atomB && piStageB);
            if (orbB == gAnimB && guideOnPiStageB2) animOrbB = false;
        }
        // Original: σ-only always forced bond→slot lerp so runStep ran. When σ-relax already applied nuclear motion, keep
        // that. When there are zero nuclear moves, use eps above only — avoids motion when bond midpoint already matches slot.
        if (sigmaOnlyUnchangedPiBoth && moves.Count > 0)
        {
            if (orbA != null && parentA != null) animOrbA = true;
            if (orbB != null && parentB != null) animOrbB = true;
        }
        // Full σ cleavage: break fragments use frozen VSEPR preview as motion targets (redist append above), not bond-line slotFin.
        if (sigmaCleavageBetweenPartners)
        {
            animOrbA = false;
            animOrbB = false;
        }

        var breakNewmanFixedStartA = new Dictionary<int, Vector3>();
        var breakNewmanFixedStartB = new Dictionary<int, Vector3>();
        if (breakNewmanA && atomA != null)
        {
            foreach (var n in atomA.GetDistinctSigmaNeighborAtoms())
            {
                if (n == null || n == atomB || n.AtomicNumber != 1) continue;
                if (!moves.ContainsKey(n))
                    breakNewmanFixedStartA[n.GetInstanceID()] = n.transform.position;
            }
        }
        if (breakNewmanB && atomB != null)
        {
            foreach (var n in atomB.GetDistinctSigmaNeighborAtoms())
            {
                if (n == null || n == atomA || n.AtomicNumber != 1) continue;
                if (!moves.ContainsKey(n))
                    breakNewmanFixedStartB[n.GetInstanceID()] = n.transform.position;
            }
        }

        var redistAStarts = new List<(Vector3 pos, Quaternion rot)>();
        var redistBStarts = new List<(Vector3 pos, Quaternion rot)>();
        foreach (var e in redistA)
            redistAStarts.Add(e.orb != null ? (e.orb.transform.localPosition, e.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        foreach (var e in redistB)
            redistBStarts.Add(e.orb != null ? (e.orb.transform.localPosition, e.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));

        // Match bond-orbital eps so small VSEPR adjustments still animate (not only large lone jumps).
        const float lonePosThreshold = 0.0004f;
        const float loneRotThreshold = 0.35f;
        bool HasRedistMovement()
        {
            for (int i = 0; i < redistA.Count; i++)
            {
                var e = redistA[i];
                if (e.orb == null || e.orb.transform.parent != atomA.transform) continue;
                if (Vector3.Distance(redistAStarts[i].pos, e.pos) > lonePosThreshold || Quaternion.Angle(redistAStarts[i].rot, e.rot) > loneRotThreshold)
                    return true;
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var e = redistB[i];
                if (e.orb == null || e.orb.transform.parent != atomB.transform) continue;
                if (Vector3.Distance(redistBStarts[i].pos, e.pos) > lonePosThreshold || Quaternion.Angle(redistBStarts[i].rot, e.rot) > loneRotThreshold)
                    return true;
            }
            return false;
        }

        bool hasRedistAnim = HasRedistMovement();

        float maxRPosA = 0f, maxRRotA = 0f, maxRPosB = 0f, maxRRotB = 0f;
        for (int i = 0; i < redistA.Count; i++)
        {
            var e = redistA[i];
            if (e.orb == null || atomA == null || e.orb.transform.parent != atomA.transform) continue;
            maxRPosA = Mathf.Max(maxRPosA, Vector3.Distance(redistAStarts[i].pos, e.pos));
            maxRRotA = Mathf.Max(maxRRotA, Quaternion.Angle(redistAStarts[i].rot, e.rot));
        }
        for (int i = 0; i < redistB.Count; i++)
        {
            var e = redistB[i];
            if (e.orb == null || atomB == null || e.orb.transform.parent != atomB.transform) continue;
            maxRPosB = Mathf.Max(maxRPosB, Vector3.Distance(redistBStarts[i].pos, e.pos));
            maxRRotB = Mathf.Max(maxRRotB, Quaternion.Angle(redistBStarts[i].rot, e.rot));
        }

        float bondWorldDistA = parentA != null && orbA != null ? Vector3.Distance(bondWorldPos, endWorldA0) : 0f;
        float bondWorldAngA = parentA != null && orbA != null ? Quaternion.Angle(bondWorldRot, endRotA0) : 0f;
        float bondWorldDistB = parentB != null && orbB != null ? Vector3.Distance(bondWorldPos, endWorldB0) : 0f;
        float bondWorldAngB = parentB != null && orbB != null ? Quaternion.Angle(bondWorldRot, endRotB0) : 0f;

        bool runStep = moves.Count > 0 || animOrbA || animOrbB || hasRedistAnim || breakNewmanA || breakNewmanB;
        LogBreakBondCoMotionPrecis(
            atomA,
            atomB,
            moves.Count,
            sigmaRelaxPreApplied,
            sigmaOnlyUnchangedPiBoth,
            redistRawACount,
            redistRawBCount,
            redistFilteredACount,
            redistFilteredBCount,
            redistAfterAppendACount,
            redistAfterAppendBCount,
            animOrbA,
            animOrbB,
            bondWorldDistA,
            bondWorldAngA,
            bondWorldDistB,
            bondWorldAngB,
            hasRedistAnim,
            maxRPosA,
            maxRRotA,
            maxRPosB,
            maxRRotB,
            breakNewmanA,
            breakNewmanB,
            psiBreakA,
            psiBreakB,
            runStep);

        var atomsToBlock = new HashSet<AtomFunction>();
        if (atomA != null) atomsToBlock.Add(atomA);
        if (atomB != null) atomsToBlock.Add(atomB);
        foreach (var kv in moves)
            atomsToBlock.Add(kv.Key);

        foreach (var a in atomsToBlock)
            a.SetInteractionBlocked(true);
        if (orbA != null) orbA.SetPointerBlocked(true);
        if (orbB != null) orbB.SetPointerBlocked(true);
        foreach (var e in redistA)
            if (e.orb != null) e.orb.SetPointerBlocked(true);
        foreach (var e in redistB)
            if (e.orb != null) e.orb.SetPointerBlocked(true);

        if (runStep)
        {
            for (float t = 0; t < BreakRedistributionDuration; t += Time.deltaTime)
            {
                float s = Mathf.Clamp01(t / BreakRedistributionDuration);
                s = s * s * (3f - 2f * s); // smoothstep
                float rotS = 1f - (1f - s) * (1f - s); // ease-out quad (matches π step 2)
                if (breakNewmanA && atomA != null)
                {
                    foreach (var n in atomA.GetDistinctSigmaNeighborAtoms())
                    {
                        if (n == null || n == atomB || n.AtomicNumber != 1) continue;
                        if (breakNewmanFixedStartA.TryGetValue(n.GetInstanceID(), out Vector3 p0))
                            n.transform.position = p0;
                    }
                }
                if (breakNewmanB && atomB != null)
                {
                    foreach (var n in atomB.GetDistinctSigmaNeighborAtoms())
                    {
                        if (n == null || n == atomA || n.AtomicNumber != 1) continue;
                        if (breakNewmanFixedStartB.TryGetValue(n.GetInstanceID(), out Vector3 p0))
                            n.transform.position = p0;
                    }
                }
                foreach (var kv in moves)
                    kv.Key.transform.position = Vector3.Lerp(kv.Value.start, kv.Value.end, s);
                if (animOrbA && parentA != null && orbA != null)
                {
                    Vector3 endWA = parentA.transform.TransformPoint(slotFinA.position);
                    Quaternion endRA = parentA.transform.rotation * slotFinA.rotation;
                    orbA.transform.position = Vector3.Lerp(bondWorldPos, endWA, s);
                    orbA.transform.rotation = OrbitalAngleUtility.SlerpShortest(bondWorldRot, endRA, rotS);
                }
                if (animOrbB && parentB != null && orbB != null)
                {
                    Vector3 endWB = parentB.transform.TransformPoint(slotFinB.position);
                    Quaternion endRB = parentB.transform.rotation * slotFinB.rotation;
                    orbB.transform.position = Vector3.Lerp(bondWorldPos, endWB, s);
                    orbB.transform.rotation = OrbitalAngleUtility.SlerpShortest(bondWorldRot, endRB, rotS);
                }
                if (hasRedistAnim)
                {
                    for (int i = 0; i < redistA.Count; i++)
                    {
                        var e = redistA[i];
                        if (e.orb == null || e.orb.transform.parent != atomA.transform) continue;
                        var (sp, sr) = redistAStarts[i];
                        e.orb.transform.localPosition = Vector3.Lerp(sp, e.pos, s);
                        e.orb.transform.localRotation = OrbitalAngleUtility.SlerpShortest(sr, e.rot, rotS);
                    }
                    for (int i = 0; i < redistB.Count; i++)
                    {
                        var e = redistB[i];
                        if (e.orb == null || e.orb.transform.parent != atomB.transform) continue;
                        var (sp, sr) = redistBStarts[i];
                        e.orb.transform.localPosition = Vector3.Lerp(sp, e.pos, s);
                        e.orb.transform.localRotation = OrbitalAngleUtility.SlerpShortest(sr, e.rot, rotS);
                    }
                }
                if (breakNewmanA && atomA != null && atomB != null)
                {
                    Vector3 axA = atomB.transform.position - atomA.transform.position;
                    if (axA.sqrMagnitude > 1e-10f)
                        atomA.ApplyNewmanStaggerTwistProgress(psiBreakA, s, axA.normalized, atomB, false, parentA == atomA ? orbA : orbB);
                }
                if (breakNewmanB && atomA != null && atomB != null)
                {
                    Vector3 axB = atomA.transform.position - atomB.transform.position;
                    if (axB.sqrMagnitude > 1e-10f)
                        atomB.ApplyNewmanStaggerTwistProgress(psiBreakB, s, axB.normalized, atomA, false, parentA == atomB ? orbA : orbB);
                }
                // Same frame as lobe lerps + σ-relax: refresh σ line / bond-orbital poses (non-bond lobes do not use Bond).
                AtomFunction.UpdateSigmaBondVisualsForAtoms(atomsToBlock);
                yield return null;
            }
            foreach (var kv in moves)
            {
                var moved = kv.Key;
                var end = kv.Value.end;
                bool placed = false;
                if (breakNewmanA && moved != null && moved.AtomicNumber == 1 && atomA != null
                    && atomA.GetDistinctSigmaNeighborAtoms().Contains(moved) && moved != atomB)
                {
                    Vector3 c = atomA.transform.position;
                    Vector3 ax = atomB.transform.position - c;
                    if (ax.sqrMagnitude > 1e-10f)
                    {
                        moved.transform.position = c + Quaternion.AngleAxis(psiBreakA, ax.normalized) * (end - c);
                        placed = true;
                    }
                }
                if (!placed && breakNewmanB && moved != null && moved.AtomicNumber == 1 && atomB != null
                    && atomB.GetDistinctSigmaNeighborAtoms().Contains(moved) && moved != atomA)
                {
                    Vector3 c = atomB.transform.position;
                    Vector3 ax = atomA.transform.position - c;
                    if (ax.sqrMagnitude > 1e-10f)
                    {
                        moved.transform.position = c + Quaternion.AngleAxis(psiBreakB, ax.normalized) * (end - c);
                        placed = true;
                    }
                }
                if (!placed)
                    moved.transform.position = end;
            }
        }

        // Bond→slot settle: only for lobes that actually use that path. Full σ cleavage: both break fragments use frozen
        // preview only (redist + ApplyFrozenNonbondFinal) — slotFin follows bond-cylinder geometry, not Redistribute+OrientEmpty.
        // Staged / π-only break: σCleavagePartners false — keep slot snap for non-fixed guides.
        if (orbA != null && parentA != null && !sigmaCleavageBetweenPartners && !IsSigmaCleavageFixedGuide(orbA))
        {
            orbA.transform.localPosition = slotFinA.position;
            orbA.transform.localRotation = slotFinA.rotation;
        }
        if (orbB != null && parentB != null && !sigmaCleavageBetweenPartners && !IsSigmaCleavageFixedGuide(orbB))
        {
            orbB.transform.localPosition = slotFinB.position;
            orbB.transform.localRotation = slotFinB.rotation;
        }
        if (hasRedistAnim)
        {
            for (int i = 0; i < redistA.Count; i++)
            {
                var e = redistA[i];
                if (e.orb != null && e.orb.transform.parent == atomA.transform)
                {
                    if (!breakNewmanA || e.orb.ElectronCount <= 0)
                    {
                        e.orb.transform.localPosition = e.pos;
                        e.orb.transform.localRotation = e.rot;
                    }
                }
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var e = redistB[i];
                if (e.orb != null && e.orb.transform.parent == atomB.transform)
                {
                    if (!breakNewmanB || e.orb.ElectronCount <= 0)
                    {
                        e.orb.transform.localPosition = e.pos;
                        e.orb.transform.localRotation = e.rot;
                    }
                }
            }
        }

        if (breakNewmanA)
            atomA?.RefreshSigmaBondTransformsAndChargesAroundAtom();
        if (breakNewmanB)
            atomB?.RefreshSigmaBondTransformsAndChargesAroundAtom();

        LogBreakGuideOrbitalsPose("afterAnimSlot_redistSnap_refreshSigma", atomA, atomB, orbA, orbB, parentA, parentB);
        LogBreakEmptyTeleportBoth("co_postAnim_redistSnap_refreshSigma", atomA, atomB, piAfterA == 0 ? refA : null, piAfterB == 0 ? refB : null, orbA, orbB, parentA, parentB);
        LogBreakBondTetraFrameworkPair("CoAnimate_postAnim", atomA, atomB);

        bool skipFinalSigmaRelax = sigmaRelaxPreApplied;

        foreach (var e in redistA)
            if (e.orb != null) e.orb.SetPointerBlocked(false);
        foreach (var e in redistB)
            if (e.orb != null) e.orb.SetPointerBlocked(false);

        // Final authority in this coroutine: frozen non-bond targets (preview after Redistribute+OrientEmpty).
        // σ-cleavage guides were excluded from redist lerp — they still need this apply or they stay on the restored
        // pre-preview pose while slotFin snap incorrectly moved non-guides (see post-anim slot settle guard above).

        for (int i = 0; i < frozenNonbondA.Count; i++)
        {
            var t = frozenNonbondA[i];
            if (t.orb == null || atomA == null || t.orb.transform.parent != atomA.transform) continue;
            t.orb.transform.localPosition = t.pos;
            t.orb.transform.localRotation = t.rot;
        }
        for (int i = 0; i < frozenNonbondB.Count; i++)
        {
            var t = frozenNonbondB[i];
            if (t.orb == null || atomB == null || t.orb.transform.parent != atomB.transform) continue;
            t.orb.transform.localPosition = t.pos;
            t.orb.transform.localRotation = t.rot;
        }
        LogBreakGuideOrbitalsPose("afterApplyFrozenNonbondFinal", atomA, atomB, orbA, orbB, parentA, parentB);
        LogBreakEmptyTeleportBoth("co_postApplyFrozenNonbondFinal", atomA, atomB, refA, refB, orbA, orbB, parentA, parentB);
        LogBreakBondTetraFrameworkPair("CoAnimate_postApplyFrozenNonbondFinal", atomA, atomB);

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.RefreshElectronSyncOnBondedOrbitals();
        atomB?.RefreshElectronSyncOnBondedOrbitals();

        LogBreakGuideOrbitalsPose("final_afterRefreshElectronSync", atomA, atomB, orbA, orbB, parentA, parentB);
        LogBreakEmptyTeleportBoth("co_postRefreshElectronSync", atomA, atomB, piAfterA == 0 ? refA : null, piAfterB == 0 ? refB : null, orbA, orbB, parentA, parentB);
        LogBreakBondTetraFrameworkPair("CoAnimate_postRefreshElectronSync", atomA, atomB);

        if (orbA != null) orbA.SetPointerBlocked(false);
        if (orbB != null) orbB.SetPointerBlocked(false);
        foreach (var a in atomsToBlock)
            a.SetInteractionBlocked(false);
        AtomFunction.SetupGlobalIgnoreCollisions();
    }
}
