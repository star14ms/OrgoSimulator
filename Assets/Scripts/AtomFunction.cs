using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;

public class AtomFunction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] float bondRadius = 0.8f;
    [SerializeField] int atomicNumber = 1;
    [SerializeField] int charge;
    [SerializeField] [Range(0.05f, 1f)] float atomBodyAlpha = 0.42f;
    [SerializeField] ElectronOrbitalFunction orbitalPrefab;
    readonly List<ElectronOrbitalFunction> bondedOrbitals = new List<ElectronOrbitalFunction>();
    readonly List<CovalentBond> covalentBonds = new List<CovalentBond>();

    bool isBeingHeld;
    Vector3 dragOffset;
    Vector3 atomDragPlanePoint;
    Vector3 atomDragPlaneNormal;
    bool atomDragPlaneValid;
    HashSet<AtomFunction> moleculeAtoms;
    Transform elementLabelTransform;
    Coroutine chargeLabelInvalidDragFlashRoutine;

    public float BondRadius => bondRadius;
    public ElectronOrbitalFunction OrbitalPrefab => orbitalPrefab;
    public int BondedOrbitalCount => bondedOrbitals.Count;
    public int AtomicNumber { get => atomicNumber; set => atomicNumber = Mathf.Clamp(value, 1, 118); }
    public int Charge { get => charge; set { charge = value; RefreshChargeLabel(); } }

    /// <summary>Console + optional project <c>.log</c>: <c>[vsepr3d]</c> trace. Default on in all builds; set false to silence.</summary>
    public static bool DebugLogVseprRedistribute3D = true;

    static void LogVsepr3D(string message)
    {
        if (!DebugLogVseprRedistribute3D) return;
        string line = "[vsepr3d] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    /// <summary>Console + project <c>.log</c>: <c>[bond-break-cc]</c> C–C σ-cleavage carbocation geometry (empty vs three σ axes). Default on.</summary>
    public static bool DebugLogCcBondBreakGeometry = true;

    /// <summary>Console + project <c>.log</c>: <c>[attach-anchor-pos]</c> — world position of carbons during edit-mode attach, instant σ redistribution, and σ-bond-formation tet relax. Synced from <see cref="EditModeManager"/> in play mode.</summary>
    public static bool DebugLogAttachAnchorCarbonPosition = true;

    /// <summary>Console + project <c>.log</c>: <c>[attach-added-group]</c> — new fragment (non-H) world position, σ count, and non-bond orbital counts during replace-H / add-atom + H-auto.</summary>
    public static bool DebugLogAttachAddedGroup = true;

    /// <summary>One-line valence snapshot for <see cref="LogAttachAddedGroupPhase"/> (σ neighbors, non-bond 0e/1e/2e lobes, π count).</summary>
    public string DebugSummarizeValenceShapeForLog()
    {
        int sig = GetDistinctSigmaNeighborCount();
        int n0 = 0, n1 = 0, n2 = 0, nb = 0;
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null) continue;
            nb++;
            if (o.ElectronCount <= 0) n0++;
            else if (o.ElectronCount == 1) n1++;
            else n2++;
        }
        int pi = GetPiBondCount();
        return $"Z={atomicNumber} σN={sig} π={pi} nonBondOrbs={nb} 0e/1e/2e={n0}/{n1}/{n2} covalentEdges={covalentBonds.Count}";
    }

    public static void LogAttachAddedGroupPhase(string phase, AtomFunction atom, Vector3? baselineAtAttachStart = null)
    {
        if (!DebugLogAttachAddedGroup || atom == null || atom.AtomicNumber <= 1) return;
        Vector3 p = atom.transform.position;
        string delta = baselineAtAttachStart.HasValue
            ? $" deltaFromBaseline={Vector3.Distance(p, baselineAtAttachStart.Value):F6}"
            : "";
        string shape = atom.DebugSummarizeValenceShapeForLog();
        string line = $"[attach-added-group] {phase} {atom.name} id={atom.GetInstanceID()} worldPos={p}{delta} | {shape}";
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    public static void LogAttachAnchorCarbonPhase(string phase, AtomFunction atom, Vector3? baselineAtAttachStart = null)
    {
        if (!DebugLogAttachAnchorCarbonPosition || atom == null || atom.AtomicNumber != 6) return;
        Vector3 p = atom.transform.position;
        string delta = baselineAtAttachStart.HasValue
            ? $" deltaFromBaseline={Vector3.Distance(p, baselineAtAttachStart.Value):F6}"
            : "";
        string line = $"[attach-anchor-pos] {phase} {atom.name} id={atom.GetInstanceID()} worldPos={p}{delta}";
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    public static void LogAttachAnchorCarbonMove(string phase, AtomFunction atom, Vector3 worldFrom, Vector3 worldTo)
    {
        if (!DebugLogAttachAnchorCarbonPosition || atom == null || atom.AtomicNumber != 6) return;
        float d = Vector3.Distance(worldFrom, worldTo);
        if (d < 1e-7f) return;
        string line = $"[attach-anchor-pos] {phase} {atom.name} id={atom.GetInstanceID()} {worldFrom} -> {worldTo} Δ={d:F6}";
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    static void LogCcBondBreak(string message)
    {
        if (!DebugLogCcBondBreakGeometry) return;
        string line = "[bond-break-cc] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    /// <summary>
    /// After bond-break steps: σ neighbor directions vs empty p. Carbocation target: three σ coplanar (trigonal), empty ⟂ that plane → ~90° between empty and each σ, <c>|σ2·n|</c>→0, <c>|empty·n|</c>→1 (<c>n = σ0×σ1</c>).
    /// Radical tetrahedral case: ~109.5° empty–σ and <c>|empty·n|</c> not necessarily 1.
    /// </summary>
    public void LogCcBondBreakGeometryDiagnostics(string phase)
    {
        if (!DebugLogCcBondBreakGeometry) return;
        if (!IsSp2BondBreakEmptyAlongRefCase()) return;
        if (GetDistinctSigmaNeighborCount() != 3) return;
        var emptyOrb = bondedOrbitals.FirstOrDefault(o => o != null && o.Bond == null && o.ElectronCount == 0);
        if (emptyOrb == null) return;

        Vector3 eTipW = transform.TransformDirection(OrbitalTipLocalDirection(emptyOrb)).normalized;
        var neighbors = GetDistinctSigmaNeighborAtoms();
        float a0 = Vector3.Angle(eTipW, (neighbors[0].transform.position - transform.position).normalized);
        float a1 = Vector3.Angle(eTipW, (neighbors[1].transform.position - transform.position).normalized);
        float a2 = Vector3.Angle(eTipW, (neighbors[2].transform.position - transform.position).normalized);

        Vector3 d0 = (neighbors[0].transform.position - transform.position).normalized;
        Vector3 d1 = (neighbors[1].transform.position - transform.position).normalized;
        Vector3 d2 = (neighbors[2].transform.position - transform.position).normalized;
        Vector3 nCross = Vector3.Cross(d0, d1);
        float nMag = nCross.magnitude;
        Vector3 nrm = nMag > 1e-8f ? nCross / nMag : Vector3.up;
        float sigma2Coplanar01 = Mathf.Abs(Vector3.Dot(d2, nrm));
        float emptyCoplanarSigma01Plane = Mathf.Abs(Vector3.Dot(eTipW, nrm));

        LogCcBondBreak(
            $"{phase} atom={name} Z={atomicNumber} ∠(empty,σ0..2)={a0:F1}° {a1:F1}° {a2:F1}° | σ-plane test n=σ0×σ1: |σ2·n|={sigma2Coplanar01:F3} (→0 if 3σ coplanar) |empty·n|={emptyCoplanarSigma01Plane:F3} (carbocation: |σ2·n|→0, |empty·n|→1)");
    }

    public bool CanAcceptOrbital() => bondedOrbitals.Count < GetOrbitalSlotCount();

    public int GetBondsTo(AtomFunction other)
    {
        if (other == null) return 0;
        int count = 0;
        foreach (var b in covalentBonds)
        {
            if (b == null) continue;
            var o = b.AtomA == this ? b.AtomB : b.AtomA;
            if (o == other) count++;
        }
        return count;
    }

    public int GetPiBondCount()
    {
        var counted = new HashSet<(int, int)>();
        int pi = 0;
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            int idA = GetInstanceID();
            int idB = other.GetInstanceID();
            var pair = (Mathf.Min(idA, idB), Mathf.Max(idA, idB));
            if (counted.Contains(pair)) continue;
            counted.Add(pair);
            pi += Mathf.Max(0, GetBondsTo(other) - 1);
        }
        return pi;
    }

    /// <summary>
    /// Sum of bond-order edges to all neighbors (each covalent σ or π is one edge; a double bond counts as 2).
    /// Used to cap π formation so total bond order around an atom stays within valence limits.
    /// </summary>
    public int GetSumBondOrderToNeighbors()
    {
        var seen = new HashSet<AtomFunction>();
        int sum = 0;
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (other == null || !seen.Add(other)) continue;
            sum += GetBondsTo(other);
        }
        return sum;
    }

    /// <summary>
    /// Maximum total bond-order sum around this atom (octet-style for period 2; orbital slots for period 3+ expanded octet).
    /// </summary>
    public int GetMaxBondOrderSumAroundAtom()
    {
        if (atomicNumber <= 2) return atomicNumber == 1 ? 1 : 0;
        if (atomicNumber <= 10) return 4; // period 2 octet
        return GetOrbitalSlotCount();
    }

    public int GetOrbitalSlotCount()
    {
        if (atomicNumber == 2) return 1; // He: 1s² only
        int group = GetGroupFromAtomicNumber(atomicNumber);
        if (group == 1) return 1;
        if (group == 2) return 2;
        if (group == 3 || group == 13) return 3;
        // Period 3+ N-group / O-group: expanded octet so σ + π (e.g. SO₃, PO₄) can form after four σ bonds.
        if (atomicNumber > 10 && (group == 15 || group == 16)) return 6;
        return 4;
    }

    public static float[] GetSlotAnglesForCount(int n)
    {
        if (n <= 0) return System.Array.Empty<float>();
        if (n == 1) return new[] { 0f };
        var angles = new float[n];
        for (int i = 0; i < n; i++)
            angles[i] = 360f * i / n;
        return angles;
    }

    public void RegisterBond(CovalentBond bond)
    {
        if (bond != null && !covalentBonds.Contains(bond))
            covalentBonds.Add(bond);
    }

    public void UnregisterBond(CovalentBond bond)
    {
        covalentBonds.Remove(bond);
    }

    public IReadOnlyList<CovalentBond> CovalentBonds => covalentBonds;

    /// <summary>Returns the angle (degrees) to the first bond partner for use as redistribution origin. Null if no bonds.</summary>
    public float? GetPrimaryBondDirectionAngle()
    {
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            var dir = (other.transform.position - transform.position).normalized;
            if (dir.sqrMagnitude >= 0.01f)
                return Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        }
        return null;
    }

    public void OnElectronRemoved()
    {
        RefreshCharge();
    }

    public void OnElectronAdded()
    {
        RefreshCharge();
    }

    /// <summary>Computes oxidation state using electronegativity: bonding electrons assigned to the more electronegative atom; equal EN splits 50-50.</summary>
    public int ComputeCharge()
    {
        int valence = GetValenceFromGroup(GetGroupFromAtomicNumber(atomicNumber));
        if (atomicNumber == 2) valence = 2;

        int electronsOwned = 0;

        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null) continue;
            electronsOwned += orb.ElectronCount;
        }

        foreach (var b in covalentBonds)
        {
            if (b == null || b.Orbital == null) continue;
            electronsOwned += b.GetElectronsOwnedBy(this);
        }

        return valence - electronsOwned;
    }

    public static float GetElectronegativity(int z)
    {
        if (z <= 0 || z > ElectronegativityPauling.Length) return 2f;
        return ElectronegativityPauling[z - 1];
    }

    static readonly float[] ElectronegativityPauling =
    {
        2.20f, 2.0f, 0.98f, 1.57f, 2.04f, 2.55f, 3.04f, 3.44f, 3.98f, 2.0f,
        0.93f, 1.31f, 1.61f, 1.90f, 2.19f, 2.58f, 3.16f, 2.0f,
        0.82f, 1.00f, 1.36f, 1.54f, 1.63f, 1.66f, 1.55f, 1.83f, 1.88f, 1.91f, 1.90f, 1.65f, 1.81f, 2.01f, 2.18f, 2.55f, 2.96f, 3.00f,
        0.82f, 0.95f, 1.22f, 1.33f, 1.60f, 2.16f, 1.90f, 2.20f, 2.28f, 2.20f, 1.93f, 1.69f, 1.78f, 1.96f, 2.05f, 2.10f, 2.66f, 2.60f,
        0.79f, 0.89f,
        1.10f, 1.12f, 1.13f, 1.14f, 1.15f, 1.17f, 1.15f, 1.20f, 1.15f, 1.22f, 1.23f, 1.24f, 1.25f, 1.15f, 1.27f,
        1.30f, 1.50f, 2.36f, 1.90f, 2.20f, 2.20f, 2.28f, 2.54f, 2.00f, 1.62f, 2.33f, 2.02f, 2.00f, 2.20f, 2.20f,
        0.70f, 0.90f,
        1.10f, 1.30f, 1.50f, 1.38f, 1.36f, 1.28f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f,
        1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f
    };

    public void RefreshCharge()
    {
        charge = ComputeCharge();
        RefreshChargeLabel();
    }

    public void RefreshChargeLabel()
    {
        var chargeLabel = transform.Find("ElementLabel/ChargeLabel");
        if (chargeLabel == null) return;
        var tmp = chargeLabel.GetComponent<TMP_Text>();
        if (tmp == null) return;
        int abs = Mathf.Abs(charge);
        tmp.text = charge > 0 ? (abs == 1 ? "+" : charge + "+") : charge < 0 ? (abs == 1 ? "-" : abs + "-") : "";
        chargeLabel.gameObject.SetActive(charge != 0);
    }

    /// <summary>
    /// Bond drag blocked (electrons cannot move to a less electronegative atom): flash charge label red, then fade to its normal color over 1 second.
    /// </summary>
    public void FlashChargeLabelInvalidDragFade()
    {
        if (chargeLabelInvalidDragFlashRoutine != null)
            StopCoroutine(chargeLabelInvalidDragFlashRoutine);
        chargeLabelInvalidDragFlashRoutine = StartCoroutine(ChargeLabelInvalidDragFadeCoroutine());
    }

    IEnumerator ChargeLabelInvalidDragFadeCoroutine()
    {
        var chargeLabel = transform.Find("ElementLabel/ChargeLabel");
        if (chargeLabel == null) yield break;
        var tmp = chargeLabel.GetComponent<TMP_Text>();
        if (tmp == null) yield break;
        Color original = tmp.color;
        bool wasActive = chargeLabel.gameObject.activeSelf;
        if (!wasActive) chargeLabel.gameObject.SetActive(true);
        tmp.color = Color.red;
        const float duration = 1f;
        for (float t = 0; t < duration; t += Time.deltaTime)
        {
            float u = Mathf.Clamp01(t / duration);
            tmp.color = Color.Lerp(Color.red, original, u);
            yield return null;
        }
        tmp.color = original;
        RefreshChargeLabel();
        chargeLabelInvalidDragFlashRoutine = null;
    }

    /// <summary>Block or unblock pointer interaction on this atom and all its orbitals and electrons. Used during bond formation.</summary>
    public void SetInteractionBlocked(bool blocked)
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col != null) col.enabled = !blocked;
        if (col2D != null) col2D.enabled = !blocked;

        foreach (var orb in GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != transform) continue;
            orb.SetPointerBlocked(blocked);
            orb.SetPhysicsEnabled(!blocked);
            foreach (var e in orb.GetComponentsInChildren<ElectronFunction>())
            {
                e.SetPointerBlocked(blocked);
            }
        }
    }

    public void BondOrbital(ElectronOrbitalFunction orbital)
    {
        if (CanAcceptOrbital() && !bondedOrbitals.Contains(orbital))
            bondedOrbitals.Add(orbital);
    }

    public void UnbondOrbital(ElectronOrbitalFunction orbital)
    {
        bondedOrbitals.Remove(orbital);
    }

    /// <summary>Used by bond-break preview: snapshot locals before temporary RedistributeOrbitals passes so we can restore atoms-only snapshots without leaving lone lobes stuck at post-VSEPR poses.</summary>
    public void AppendBondedOrbitalLocalSnapshot(List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> dst)
    {
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.transform.parent != transform) continue;
            dst.Add((orb, orb.transform.localPosition, orb.transform.localRotation));
        }
    }

    public static void RestoreBondedOrbitalLocalSnapshot(List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> snap)
    {
        foreach (var (orb, localPos, localRot) in snap)
        {
            if (orb == null) continue;
            orb.transform.localPosition = localPos;
            orb.transform.localRotation = localRot;
        }
    }

    /// <summary>Before bond-break / bond-formation redistribution: each 0e non-bonded orbital receives 1e from a 2e lone pair on this atom, if available (one electron per transfer).</summary>
    public void TryTransferElectronFromLonePairToEmptyOrbitals()
    {
        var nonBonded = bondedOrbitals.Where(orb => orb != null && orb.Bond == null).ToList();
        foreach (var empty in nonBonded.Where(o => o.ElectronCount == 0).ToList())
        {
            var donor = nonBonded.FirstOrDefault(o => o != empty && o.ElectronCount == 2);
            if (donor == null) break;
            donor.ElectronCount = 1;
            empty.ElectronCount = 1;
        }
    }

    /// <summary>Re-syncs electron sphere/pair placement on every bonded orbital (after VSEPR or bond-break animation).</summary>
    public void RefreshElectronSyncOnBondedOrbitals()
    {
        foreach (var orb in bondedOrbitals)
            orb?.RefreshElectronSyncAfterLayout();
    }

    /// <summary>Returns true if orbitals are not on ideal VSEPR directions (3D) or evenly spaced in the XY plane (2D).</summary>
    public bool HasInconsistentOrbitalAngles()
    {
        int slotCount = GetOrbitalSlotCount();
        if (slotCount <= 1) return false;

        float tolerance = 360f / (2f * slotCount);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return HasInconsistentOrbitalDirections3D(tolerance);

        var angles = CollectUniqueOrbitalAngles(tolerance);
        if (angles.Count <= 1) return false;

        var sorted = angles.Select(NormalizeAngleTo360).OrderBy(a => a).ToList();
        float expectedStep = 360f / sorted.Count;
        for (int i = 0; i < sorted.Count; i++)
        {
            float next = i + 1 < sorted.Count ? sorted[i + 1] : sorted[0] + 360f;
            float diff = next - sorted[i];
            if (diff < 0) diff += 360f;
            if (Mathf.Abs(diff - expectedStep) > tolerance) return true;
        }
        return false;
    }

    bool HasInconsistentOrbitalDirections3D(float toleranceDeg)
    {
        var dirs = CollectUniqueOrbitalDirections(toleranceDeg);
        if (dirs.Count <= 1) return false;

        Vector3 refLocal = ResolveReferenceBondDirectionLocal(null, null);
        var idealRaw = VseprLayout.GetIdealLocalDirections(dirs.Count);
        var idealAligned = VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal);
        foreach (var d in dirs)
        {
            float minAng = 360f;
            foreach (var id in idealAligned)
                minAng = Mathf.Min(minAng, Vector3.Angle(d, id));
            if (minAng > toleranceDeg) return true;
        }
        return false;
    }

    /// <summary>Redistributes orbital directions when pi count or bonding changed. In 3D (perspective), uses VSEPR (linear … octahedral). <paramref name="refBondWorldDirection"/> overrides reference axis when set (e.g. bond break). <paramref name="relaxCoplanarSigmaToTetrahedral"/> (e.g. after σ-only break from a π system) moves coplanar 3σ+1-lone neighbors toward tetrahedral; leave false for normal builds (sp² trigonal FG centers also match 3σ+1 lone before π). <paramref name="skipLoneLobeLayout"/> skips VSEPR repositioning of lone lobes (σ-neighbor relax still runs); use after bond-break animation already placed lone orbitals. <paramref name="pinAtomsForSigmaRelax"/> keeps those atoms fixed during coplanar→tetrahedral σ-neighbor relax (e.g. π break: the two centers still σ-bonded). <paramref name="skipSigmaNeighborRelax"/> skips σ-neighbor motion when substituents are already at post-relax positions (bond-break preview + final lone layout only). <paramref name="bondBreakGuideLoneOrbital"/> when set (bond break), that non-bonded lobe’s direction is not remapped by TryMatch; ideal polyhedron aligns to reference with π &gt; broken σ &gt; guide tip. <paramref name="newSigmaBondPartnerHint"/> + <paramref name="sigmaNeighborCountBeforeHint"/> (instant σ bond): when σ neighbor count increases (e.g. 3→4) and there are no occupied lone lobes, snap substituents to a tetrahedral framework with the new partner pinned (fixes carbocation → sp³ after re-forming C–C).</summary>
    public void RedistributeOrbitals(float? piBondAngleOverride = null, Vector3? refBondWorldDirection = null, bool relaxCoplanarSigmaToTetrahedral = false, bool skipLoneLobeLayout = false, HashSet<AtomFunction> pinAtomsForSigmaRelax = null, bool skipSigmaNeighborRelax = false, ElectronOrbitalFunction bondBreakGuideLoneOrbital = null, AtomFunction newSigmaBondPartnerHint = null, int sigmaNeighborCountBeforeHint = -1)
    {
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            RedistributeOrbitals3D(piBondAngleOverride, refBondWorldDirection, relaxCoplanarSigmaToTetrahedral, skipLoneLobeLayout, pinAtomsForSigmaRelax, skipSigmaNeighborRelax, bondBreakGuideLoneOrbital, newSigmaBondPartnerHint, sigmaNeighborCountBeforeHint);
            return;
        }

        int slotCount = GetOrbitalSlotCount();
        if (slotCount <= 1) return;

        float tolerance = 360f / (2f * slotCount);

        // Step 1: Collect unique orbital angles (lone orbitals + bonds)
        var oldAngles = CollectUniqueOrbitalAngles(tolerance);
        if (oldAngles.Count == 0) return;

        // Step 2: Identify origin angle. Pi bond when present; else 0° (RedistributeOrbitals is used for bond break).
        float? piBondAngle = null;
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (GetBondsTo(other) <= 1) continue;
            var dir = (other.transform.position - transform.position).normalized;
            piBondAngle = OrbitalAngleUtility.DirectionToAngleWorld(dir);
            break;
        }
        float originAngle = piBondAngle.HasValue ? NormalizeAngleTo360(piBondAngle.Value)
            : (piBondAngleOverride.HasValue ? NormalizeAngleTo360(piBondAngleOverride.Value) : 0f);

        // Step 3: Build new angle list starting from origin
        int n = oldAngles.Count;
        float step = 360f / n;
        var newAngles = new List<float>();
        for (int i = 0; i < n; i++)
        {
            float a = originAngle + i * step;
            newAngles.Add(NormalizeAngleTo360(a));
        }

        // Step 4: Remove bond slot from both lists when we have bonds (bond stays fixed). When no bonds, redistribute all.
        List<float> oldNonPi;
        List<float> newNonPi;
        if (covalentBonds.Count > 0)
        {
            float refAngle = piBondAngle.HasValue ? originAngle : (piBondAngleOverride.HasValue ? NormalizeAngleTo360(piBondAngleOverride.Value) : 0f);
            var b = covalentBonds.FirstOrDefault(x => x?.AtomA != null && x?.AtomB != null);
            if (b != null && !piBondAngleOverride.HasValue)
            {
                var other = b.AtomA == this ? b.AtomB : b.AtomA;
                var dir = (other.transform.position - transform.position).normalized;
                if (dir.sqrMagnitude >= 0.01f) refAngle = NormalizeAngleTo360(OrbitalAngleUtility.DirectionToAngleWorld(dir));
            }
            int refIdxOld = FindClosestAngleIndex(oldAngles, refAngle, tolerance);
            int refIdxNew = FindClosestAngleIndex(newAngles, refAngle, tolerance);
            oldNonPi = oldAngles.Where((_, i) => i != refIdxOld).ToList();
            newNonPi = newAngles.Where((_, i) => i != refIdxNew).ToList();
        }
        else
        {
            oldNonPi = oldAngles;
            newNonPi = newAngles;
        }

        if (oldNonPi.Count == 0) return;

        // Step 5: Optimal one-to-one matching (min total angular change)
        var bestMapping = FindBestAngleMapping(oldNonPi, newNonPi);
        if (bestMapping == null) return;

        // Step 6: Apply updates to lone orbitals (one orbital per mapping pair to avoid overlap)
        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null).ToList();
        var moved = new HashSet<ElectronOrbitalFunction>();
        foreach (var (oldAngle, newAngle) in bestMapping)
        {
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPosition(newAngle, bondRadius);
            foreach (var orb in loneOrbitals)
            {
                if (moved.Contains(orb)) continue;
                float orbAngle = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
                if (AnglesWithinTolerance(orbAngle, oldAngle, tolerance))
                {
                    orb.transform.localPosition = pos;
                    orb.transform.localRotation = rot;
                    moved.Add(orb);
                    break; // One orbital per new slot to avoid overlap
                }
            }
        }
    }

    /// <summary>
    /// Internuclear directions toward each bonded neighbor in local space (one per neighbor, not per CovalentBond).
    /// σ+π to the same partner share one framework axis; the π component is not a second σ domain (after π break it becomes a lone lobe counted separately).
    /// </summary>
    void AppendSigmaBondDirectionsLocalDistinctNeighbors(List<Vector3> dest)
    {
        var seen = new HashSet<AtomFunction>();
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (other == null || !seen.Add(other)) continue;
            var w = (other.transform.position - transform.position).normalized;
            if (w.sqrMagnitude < 0.01f) continue;
            dest.Add(transform.InverseTransformDirection(w).normalized);
        }
    }

    /// <summary>σ framework axes toward bonded partners, in local space, merged within tolerance.</summary>
    List<Vector3> CollectSigmaBondAxesLocalMerged(float toleranceDeg)
    {
        var bondAxes = new List<Vector3>();
        AppendSigmaBondDirectionsLocalDistinctNeighbors(bondAxes);
        bondAxes.Sort(CompareVectorsStable);
        return MergeDirectionsWithinTolerance(bondAxes, toleranceDeg);
    }

    /// <summary>
    /// σ bonds claim vertices of the ideal VSEPR polyhedron; lone orbitals are matched only to the remaining directions.
    /// (Mixing σ bond axes into the lone-orbital “old” pool broke stepwise H-auto methane tetrahedral layout.)
    /// </summary>
    /// <param name="loneOrbitalsOccupied">Non-bonded lobes with ElectronCount &gt; 0 (electron domains).</param>
    /// <param name="pinLoneOrbitalForBondBreak">If set and in <paramref name="loneOrbitalsOccupied"/>, that lobe does not participate in permutation; one free ideal vertex is reserved for it.</param>
    /// <param name="pinReservedIdealDirection">When pin is active, the ideal local direction reserved for that lobe (caller applies to the orbital). π-break: tip may still lie along σ; we pick a lone-pair vertex least aligned with σ axes, not the vertex closest to the old π tip.</param>
    bool TryMatchLoneOrbitalsToFreeIdealDirections(
        Vector3 refLocal,
        int slotCount,
        List<Vector3> bondAxesMerged,
        List<ElectronOrbitalFunction> loneOrbitalsOccupied,
        List<Vector3> newDirsAligned,
        out List<(Vector3 oldDir, Vector3 newDir)> mapping,
        out Vector3? pinReservedIdealDirection,
        ElectronOrbitalFunction pinLoneOrbitalForBondBreak = null)
    {
        mapping = null;
        pinReservedIdealDirection = null;

        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        bool[] idealUsed = new bool[slotCount];
        var bondOrder = new List<Vector3>(bondAxesMerged);
        var refN = refLocal.normalized;
        bondOrder.Sort((a, b) =>
        {
            float da = Vector3.Dot(a.normalized, refN);
            float db = Vector3.Dot(b.normalized, refN);
            int c = db.CompareTo(da); // descending dot
            if (c != 0) return c;
            return CompareVectorsStable(a, b);
        });

        foreach (var bd in bondOrder)
        {
            int bestI = -1;
            float bestDot = -2f;
            for (int i = 0; i < slotCount; i++)
            {
                if (idealUsed[i]) continue;
                float d = Vector3.Dot(bd.normalized, newDirsAligned[i].normalized);
                if (bestI < 0 || d > bestDot + 1e-5f || (Mathf.Abs(d - bestDot) <= 1e-5f && i < bestI))
                {
                    bestDot = d;
                    bestI = i;
                }
            }
            if (bestI >= 0) idealUsed[bestI] = true;
        }

        var free = new List<Vector3>();
        for (int i = 0; i < slotCount; i++)
        {
            if (!idealUsed[i])
                free.Add(newDirsAligned[i]);
        }

        bool pinActive = pinLoneOrbitalForBondBreak != null && loneOrbitalsOccupied.Contains(pinLoneOrbitalForBondBreak);
        var loneMatch = pinActive
            ? loneOrbitalsOccupied.Where(o => o != pinLoneOrbitalForBondBreak).ToList()
            : loneOrbitalsOccupied;

        if (pinActive)
        {
            Vector3 pinTip = OrbitalTipLocalDirection(pinLoneOrbitalForBondBreak).normalized;
            int bestFree = -1;
            // π-bond break: former π lobe still points ~along σ; do not reserve the σ-like corner — pick the free vertex least aligned with any σ axis (proper lone-pair site in AX₃E / tetrahedral electron geometry).
            const float pinTipAlongSigmaTol = 0.82f;
            bool pinAlongSigmaFramework = false;
            if (bondAxesMerged != null)
            {
                foreach (var b in bondAxesMerged)
                {
                    if (Mathf.Abs(Vector3.Dot(pinTip, b.normalized)) >= pinTipAlongSigmaTol)
                    {
                        pinAlongSigmaFramework = true;
                        break;
                    }
                }
            }
            if (pinAlongSigmaFramework && bondAxesMerged != null && bondAxesMerged.Count > 0)
            {
                float bestScore = float.MaxValue;
                for (int i = 0; i < free.Count; i++)
                {
                    Vector3 fd = free[i].normalized;
                    float maxAlign = 0f;
                    foreach (var b in bondAxesMerged)
                    {
                        float ad = Mathf.Abs(Vector3.Dot(fd, b.normalized));
                        if (ad > maxAlign) maxAlign = ad;
                    }
                    if (maxAlign < bestScore - 1e-6f || (Mathf.Abs(maxAlign - bestScore) <= 1e-6f && (bestFree < 0 || i < bestFree)))
                    {
                        bestScore = maxAlign;
                        bestFree = i;
                    }
                }
            }
            else
            {
                float bestDot = -2f;
                for (int i = 0; i < free.Count; i++)
                {
                    float d = Vector3.Dot(free[i].normalized, pinTip);
                    if (d > bestDot)
                    {
                        bestDot = d;
                        bestFree = i;
                    }
                }
            }
            if (bestFree >= 0)
            {
                pinReservedIdealDirection = free[bestFree];
                free.RemoveAt(bestFree);
            }
        }

        var oldLone = new List<Vector3>(loneMatch.Count);
        foreach (var o in loneMatch)
            oldLone.Add(OrbitalTipLocalDirection(o));

        if (free.Count != oldLone.Count)
        {
            LogVsepr3D(
                $"TryMatch fail free≠lone: atom={name} Z={atomicNumber} slotCount={slotCount} σAxes={bondAxesMerged.Count} loneOcc={loneOrbitalsOccupied.Count} freeVerts={free.Count} pin={pinActive} (need free==lone to match)");
            return false;
        }

        if (oldLone.Count == 0)
        {
            mapping = new List<(Vector3, Vector3)>();
            return true;
        }

        mapping = FindBestDirectionMapping(oldLone, free);
        if (mapping == null)
            LogVsepr3D($"TryMatch FindBestDirectionMapping null: atom={name} Z={atomicNumber} loneMatch={loneMatch.Count}");
        return mapping != null;
    }

    /// <summary>
    /// Electron-domain count for VSEPR: merged σ axes (one per bonded neighbor) + lone lobes (≥1).
    /// Do not clamp to <see cref="GetOrbitalSlotCount"/> — that cap made <c>slotCount</c> &lt; σ+lone so
    /// <see cref="TryMatchLoneOrbitalsToFreeIdealDirections"/> could never satisfy free.Count == lone count (orbitals unchanged).
    /// </summary>
    int GetVseprSlotCount3D(int mergedSigmaAxisCount, int loneOrbitalCount)
    {
        int domains = mergedSigmaAxisCount + loneOrbitalCount;
        return Mathf.Max(domains, 1);
    }

    /// <summary>One σ bond per distinct neighbor (merged double/triple count as one axis).</summary>
    List<AtomFunction> GetDistinctSigmaNeighborAtoms()
    {
        var seen = new HashSet<AtomFunction>();
        var list = new List<AtomFunction>();
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (other == null || !seen.Add(other)) continue;
            list.Add(other);
        }
        return list;
    }

    /// <summary>How many distinct atoms connected by a σ bond (double/triple to the same neighbor count once).</summary>
    public int GetDistinctSigmaNeighborCount() => GetDistinctSigmaNeighborAtoms().Count;

    /// <summary>
    /// Repositions σ-bonded hydrogens along each C–H bond orbital lobe axis at <paramref name="distance"/>.
    /// Use after VSEPR / tet σ-relax rotates lobes on this center so H nuclei stay on the bond line (edit H-auto, instant σ).
    /// </summary>
    public void SnapHydrogenSigmaNeighborsToBondOrbitalAxes(float distance)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || distance < 1e-4f) return;

        // σ orbital is parented to the bond object: bond.up aligns with the axis, but lobe +X is generally ⊥ the C–H line.
        // Use the geometric vector from this center to each H so CH₄ / H-auto does not shove H along the wrong direction.
        var bondsSnapshot = new List<CovalentBond>(covalentBonds);
        foreach (var b in bondsSnapshot)
        {
            if (b?.Orbital == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (other == null || other.AtomicNumber != 1) continue;

            Vector3 axis = other.transform.position - transform.position;
            if (axis.sqrMagnitude < 1e-10f)
            {
                if (b.AtomA != null && b.AtomB != null)
                    axis = (b.AtomA == this ? b.AtomB.transform.position : b.AtomA.transform.position) - transform.position;
            }
            if (axis.sqrMagnitude < 1e-10f)
            {
                Vector3 tipW = b.Orbital.transform.TransformDirection(Vector3.right);
                if (tipW.sqrMagnitude < 1e-10f) continue;
                axis = tipW.normalized;
            }
            else
                axis.Normalize();

            other.transform.position = transform.position + axis * distance;
            b.UpdateBondTransformToCurrentAtoms();
            b.SnapOrbitalToBondPosition();
        }

        RefreshCharge();
        foreach (var b in bondsSnapshot)
        {
            if (b == null) continue;
            var o = b.AtomA == this ? b.AtomB : b.AtomA;
            o?.RefreshCharge();
        }
        SetupGlobalIgnoreCollisions();
    }

    /// <summary>
    /// —CH₃ on a single C–C (or C–X) σ neighbor: place the three H on the three tetrahedral directions that are ~109.5° from the
    /// heavy bond axis. Bypasses fragile ordering between VSEPR, tet σ-relax, and <see cref="SnapHydrogenSigmaNeighborsToBondOrbitalAxes"/>.
    /// </summary>
    public bool TryPlaceTetrahedralHydrogenSubstituentsAboutSingleHeavyNeighbor(float bondLength)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || bondLength < 1e-4f) return false;

        var sigma = GetDistinctSigmaNeighborAtoms();
        var heavies = sigma.Where(n => n != null && n.AtomicNumber > 1).ToList();
        var hs = sigma.Where(n => n != null && n.AtomicNumber == 1).ToList();
        if (heavies.Count != 1 || hs.Count != 3) return false;

        Vector3 u = heavies[0].transform.position - transform.position;
        if (u.sqrMagnitude < 1e-10f) return false;
        u.Normalize();

        Vector3 refL = transform.InverseTransformDirection(u).normalized;
        var ideal4 = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(4), refL);
        var idealHDirs = new List<Vector3>(3);
        for (int k = 1; k < 4; k++)
            idealHDirs.Add(transform.TransformDirection(ideal4[k]).normalized);

        hs.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        var oldDirs = new List<Vector3>(hs.Count);
        foreach (var h in hs)
        {
            Vector3 d = h.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) return false;
            oldDirs.Add(d.normalized);
        }

        var mapping = FindBestDirectionMapping(oldDirs, idealHDirs);
        if (mapping == null || mapping.Count != 3) return false;

        var bondsSnapshot = new List<CovalentBond>(covalentBonds);
        for (int i = 0; i < hs.Count; i++)
            hs[i].transform.position = transform.position + mapping[i].newDir * bondLength;

        foreach (var b in bondsSnapshot)
        {
            if (b == null) continue;
            b.UpdateBondTransformToCurrentAtoms();
            b.SnapOrbitalToBondPosition();
        }

        RefreshCharge();
        foreach (var h in hs)
            h.RefreshCharge();
        SetupGlobalIgnoreCollisions();
        return true;
    }

    /// <summary>No 2e lone pairs on non-bond lobes — σ-only sp² bond-break frameworks may use 0e and 1e (radical) shells only.</summary>
    bool HasOnlyNonBondRadicalOrEmptyShellForSp2BondBreak()
    {
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null) continue;
            if (o.ElectronCount >= 2) return false;
        }
        return true;
    }

    /// <summary>Carbocation-style empty placement: 3 σ neighbors, exactly one 0e non-bond, no 2e lone pairs on other non-bonds (radicals OK).</summary>
    public bool IsSp2BondBreakEmptyAlongRefCase()
    {
        if (GetDistinctSigmaNeighborCount() != 3) return false;
        if (!HasOnlyNonBondRadicalOrEmptyShellForSp2BondBreak()) return false;
        int empty = bondedOrbitals.Count(o => o != null && o.Bond == null && o.ElectronCount == 0);
        return empty == 1;
    }

    /// <summary>True when exactly one σ neighbor is present (used for lone-lobe spin about the bond axis).</summary>
    public bool TryGetSingleSigmaNeighbor(out AtomFunction neighbor)
    {
        neighbor = null;
        var list = GetDistinctSigmaNeighborAtoms();
        if (list.Count != 1) return false;
        neighbor = list[0];
        return neighbor != null;
    }

    /// <summary>
    /// Atoms reachable from <paramref name="sigmaNeighbor"/> when the σ edge (this, sigmaNeighbor) is removed.
    /// Does not include <c>this</c>. Used to rotate whole substituents with σ-relax (acyclic chains); ring-linked neighbors overlap → fallback.
    /// </summary>
    public List<AtomFunction> GetAtomsOnSideOfSigmaBond(AtomFunction sigmaNeighbor)
    {
        var result = new List<AtomFunction>();
        if (sigmaNeighbor == null) return result;
        var visited = new HashSet<AtomFunction>();
        var q = new Queue<AtomFunction>();
        q.Enqueue(sigmaNeighbor);
        visited.Add(sigmaNeighbor);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            result.Add(u);
            foreach (var b in u.CovalentBonds)
            {
                if (b?.AtomA == null || b?.AtomB == null) continue;
                var v = b.AtomA == u ? b.AtomB : b.AtomA;
                if (v == null) continue;
                if (IsPivotSigmaEdge(u, v, sigmaNeighbor)) continue;
                if (visited.Add(v)) q.Enqueue(v);
            }
        }
        return result;
    }

    bool IsPivotSigmaEdge(AtomFunction u, AtomFunction v, AtomFunction neighborFromPivot)
    {
        return (u == this && v == neighborFromPivot) || (u == neighborFromPivot && v == this);
    }

    /// <summary>
    /// Rigid rotation about <paramref name="pivotWorld"/> maps each σ axis oldDir[i]→newDir[i]. If substituents (fragments beyond each σ bond)
    /// are disjoint, every atom in each fragment rotates; if any fragment shares atoms (rings), only immediate σ neighbors move.
    /// </summary>
    static void BuildSigmaNeighborTargetsWithFragmentRigidRotation(
        Vector3 pivotWorld,
        IReadOnlyList<AtomFunction> sigmaNeighbors,
        IReadOnlyList<Vector3> oldUnitDirs,
        IReadOnlyList<Vector3> newUnitDirs,
        AtomFunction pivot,
        out List<(AtomFunction atom, Vector3 targetWorld)> targets,
        HashSet<AtomFunction> pinWorld = null)
    {
        targets = new List<(AtomFunction, Vector3)>();
        int n = sigmaNeighbors.Count;
        if (n != oldUnitDirs.Count || n != newUnitDirs.Count) return;

        var fragments = new List<List<AtomFunction>>(n);
        for (int i = 0; i < n; i++)
            fragments.Add(pivot.GetAtomsOnSideOfSigmaBond(sigmaNeighbors[i]));

        bool overlap = false;
        var seenAcross = new HashSet<AtomFunction>();
        for (int i = 0; i < n; i++)
        {
            foreach (var a in fragments[i])
            {
                if (!seenAcross.Add(a))
                {
                    overlap = true;
                    break;
                }
            }
            if (overlap) break;
        }

        if (overlap)
        {
            for (int i = 0; i < n; i++)
            {
                var neighbor = sigmaNeighbors[i];
                if (pinWorld != null && pinWorld.Contains(neighbor)) continue;
                float dist = Vector3.Distance(pivotWorld, neighbor.transform.position);
                targets.Add((neighbor, pivotWorld + newUnitDirs[i].normalized * dist));
            }
            return;
        }

        for (int i = 0; i < n; i++)
        {
            Vector3 o = oldUnitDirs[i].normalized;
            Vector3 nd = newUnitDirs[i].normalized;
            if (o.sqrMagnitude < 1e-12f || nd.sqrMagnitude < 1e-12f) continue;
            Quaternion R = Quaternion.FromToRotation(o, nd);
            foreach (var a in fragments[i])
            {
                if (pinWorld != null && pinWorld.Contains(a)) continue;
                Vector3 newPos = pivotWorld + R * (a.transform.position - pivotWorld);
                targets.Add((a, newPos));
            }
        }
    }

    static bool AreThreeDirectionsCoplanar(IReadOnlyList<Vector3> unitDirsFromCenter)
    {
        if (unitDirsFromCenter == null || unitDirsFromCenter.Count != 3) return false;
        var a = unitDirsFromCenter[0];
        var b = unitDirsFromCenter[1];
        var c = unitDirsFromCenter[2];
        float vol = Mathf.Abs(Vector3.Dot(Vector3.Cross(a, b), c));
        return vol < 0.12f;
    }

    /// <summary>Reference axis for VSEPR / σ-relax (π bond toward partner, else override / first σ).</summary>
    public Vector3 GetRedistributeReferenceLocal(float? piBondAngleOverride = null, Vector3? refBondWorldDirection = null) =>
        ResolveReferenceBondDirectionLocal(piBondAngleOverride, refBondWorldDirection);

    /// <summary>
    /// AX₃E with coplanar σ axes (e.g. after π bond break): σ neighbors should move onto three tetrahedral vertices.
    /// Used for animated bond-break; instant apply uses <see cref="TryRelaxCoplanarSigmaNeighborsToTetrahedral3D"/>.
    /// </summary>
    public bool TryComputeCoplanarTetrahedralSigmaNeighborRelaxTargets(Vector3 refLocalNormalized,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets,
        HashSet<AtomFunction> pinWorld = null)
    {
        targets = null;
        var sigmaNeighbors = GetDistinctSigmaNeighborAtoms();
        // AX₃E: exactly one non-bonded lobe toward tetrahedral placement of the three σ neighbors. After a σ bond break,
        // that lobe often has 1e (CovalentBond.BreakBond: orbital.ElectronCount = totalElectrons - 1 for a 2e bond), not 2e.
        // Empty (0e) shells are excluded — they are not the lone domain for this relax.
        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null && orb.ElectronCount > 0).ToList();
        if (sigmaNeighbors.Count != 3 || loneOrbitals.Count != 1) return false;
        if (sigmaNeighbors.Count + loneOrbitals.Count != 4) return false;

        var worldDirs = new List<Vector3>(3);
        foreach (var n in sigmaNeighbors)
        {
            var d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) return false;
            worldDirs.Add(d.normalized);
        }
        if (!AreThreeDirectionsCoplanar(worldDirs)) return false;

        var idealLocal = VseprLayout.GetIdealLocalDirections(4);
        var aligned = VseprLayout.AlignFirstDirectionTo(idealLocal, refLocalNormalized);
        var idealWorld = new Vector3[4];
        for (int i = 0; i < 4; i++)
            idealWorld[i] = transform.TransformDirection(aligned[i]).normalized;

        var lone = loneOrbitals[0];
        Vector3 loneTip = transform.TransformDirection(OrbitalTipLocalDirection(lone)).normalized;
        int loneIdealIdx = 0;
        float bestDot = -2f;
        for (int i = 0; i < 4; i++)
        {
            float dot = Vector3.Dot(idealWorld[i], loneTip);
            if (dot > bestDot)
            {
                bestDot = dot;
                loneIdealIdx = i;
            }
        }

        var remaining = new List<Vector3>(3);
        for (int i = 0; i < 4; i++)
            if (i != loneIdealIdx) remaining.Add(idealWorld[i]);

        var mapping = FindBestDirectionMapping(worldDirs, remaining);
        if (mapping == null || mapping.Count != 3)
            return false;

        var newDirs = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
            newDirs.Add(mapping[i].newDir.normalized);
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld);
        return targets != null && targets.Count > 0;
    }

    /// <summary>
    /// AX₃E with coplanar σ axes (e.g. after π bond break): move σ neighbors onto three vertices of a tetrahedron so lone pair + σ bonds can adopt sp³-like angles. Bond σ orbitals (on CovalentBond) follow atom positions in LateUpdate.
    /// </summary>
    void TryRelaxCoplanarSigmaNeighborsToTetrahedral3D(Vector3 refLocalNormalized, HashSet<AtomFunction> pinWorld = null)
    {
        if (!TryComputeCoplanarTetrahedralSigmaNeighborRelaxTargets(refLocalNormalized, out var targets, pinWorld) || targets == null) return;

        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, targetWorld) in targets)
        {
            atom.transform.position = targetWorld;
            moved.Add(atom);
        }

        RefreshCharge();
        foreach (var a in moved)
            a.RefreshCharge();
        SetupGlobalIgnoreCollisions();
    }

    /// <summary>
    /// After π re-forms: sp² centers with three σ neighbors + π should be trigonal planar; if σ directions are not coplanar,
    /// targets move neighbors onto 120° in-plane. Used for π step-2 animation; instant apply uses <see cref="TryRelaxSigmaNeighborsToTrigonalPlanar3D"/>.
    /// </summary>
    public bool TryComputeTrigonalPlanarSigmaNeighborRelaxTargets(Vector3 refLocalNormalized,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets)
    {
        targets = null;
        if (GetPiBondCount() < 1) return false;
        var sigmaNeighbors = GetDistinctSigmaNeighborAtoms();
        if (sigmaNeighbors.Count != 3) return false;

        var worldDirs = new List<Vector3>(3);
        foreach (var n in sigmaNeighbors)
        {
            var d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) return false;
            worldDirs.Add(d.normalized);
        }
        if (AreThreeDirectionsCoplanar(worldDirs)) return false;

        var idealLocal = VseprLayout.GetIdealLocalDirections(3);
        var aligned = VseprLayout.AlignFirstDirectionTo(idealLocal, refLocalNormalized);
        var idealWorld = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
            idealWorld.Add(transform.TransformDirection(aligned[i]).normalized);

        var mapping = FindBestDirectionMapping(worldDirs, idealWorld);
        if (mapping == null || mapping.Count != 3) return false;

        var newDirs = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
            newDirs.Add(mapping[i].newDir.normalized);
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, null);
        return targets != null && targets.Count > 0;
    }

    /// <summary>
    /// After a π bond re-forms, σ neighbors may still sit at ~tetrahedral positions (from bond-break relax). sp² centers with
    /// three σ neighbors + π should be trigonal planar; if σ directions are not coplanar, snap neighbors onto 120° in-plane.
    /// Skips when already coplanar (FG builds, undistorted sp²).
    /// </summary>
    void TryRelaxSigmaNeighborsToTrigonalPlanar3D(Vector3 refLocalNormalized)
    {
        if (!TryComputeTrigonalPlanarSigmaNeighborRelaxTargets(refLocalNormalized, out var targets) || targets == null) return;

        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, targetWorld) in targets)
        {
            atom.transform.position = targetWorld;
            moved.Add(atom);
        }

        RefreshCharge();
        foreach (var a in moved)
            a.RefreshCharge();
        SetupGlobalIgnoreCollisions();
    }

    /// <summary>
    /// σ-only bond break: 3 σ neighbors, non-bond shell has only 0e/1e (no 2e lone pairs).
    /// <b>Carbocation</b> (<see cref="IsSp2BondBreakEmptyAlongRefCase"/>): three σ in a plane ⊥ the empty p / ex-bond ref (~120° in-plane, ~90° ref–σ).
    /// <b>Radical</b> (·CH₃-style homolysis): tetrahedral framework with ref along the former bond / radical lobe and three σ on the other tetrahedral vertices (~109.5°).
    /// Used for animated bond-break (<see cref="CovalentBond.BuildSigmaRelaxMovesForBreakBond"/>) and instant <see cref="TryApplySp2BondBreakTrigonalPlanarSigmaNeighborRelax3D"/>.
    /// </summary>
    public bool TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets(
        Vector3? refBondWorldDirection,
        HashSet<AtomFunction> pinWorld,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets)
    {
        targets = null;
        if (GetPiBondCount() > 0)
        {
            if (DebugLogCcBondBreakGeometry && GetDistinctSigmaNeighborCount() == 3 && HasOnlyNonBondRadicalOrEmptyShellForSp2BondBreak())
                LogCcBondBreak($"TryComputeSp2 skip atom={name}: piCount={GetPiBondCount()} (need 0 for σ-only relax)");
            return false;
        }
        if (GetDistinctSigmaNeighborCount() != 3)
        {
            if (DebugLogCcBondBreakGeometry && HasOnlyNonBondRadicalOrEmptyShellForSp2BondBreak())
                LogCcBondBreak($"TryComputeSp2 skip atom={name}: sigmaNeighborCount={GetDistinctSigmaNeighborCount()} (need 3)");
            return false;
        }
        if (!HasOnlyNonBondRadicalOrEmptyShellForSp2BondBreak())
        {
            if (DebugLogCcBondBreakGeometry)
                LogCcBondBreak($"TryComputeSp2 skip atom={name}: has 2e lone on non-bond (not σ-only break shell)");
            return false;
        }

        Vector3 refLocal;
        if (refBondWorldDirection.HasValue && refBondWorldDirection.Value.sqrMagnitude >= 0.01f)
        {
            refLocal = transform.InverseTransformDirection(refBondWorldDirection.Value.normalized).normalized;
        }
        else
        {
            if (!IsSp2BondBreakEmptyAlongRefCase())
            {
                if (DebugLogCcBondBreakGeometry)
                    LogCcBondBreak($"TryComputeSp2 skip atom={name}: no world ref and not carbocation 3σ+1empty case");
                return false;
            }
            var emptyOrb = bondedOrbitals.FirstOrDefault(o => o != null && o.Bond == null && o.ElectronCount == 0);
            if (emptyOrb == null) return false;
            Vector3 t = OrbitalTipLocalDirection(emptyOrb);
            if (t.sqrMagnitude < 1e-10f) return false;
            refLocal = t.normalized;
        }

        var sigmaNeighbors = GetDistinctSigmaNeighborAtoms();
        var worldDirs = new List<Vector3>(3);
        foreach (var n in sigmaNeighbors)
        {
            var d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f)
            {
                if (DebugLogCcBondBreakGeometry)
                    LogCcBondBreak($"TryComputeSp2 fail atom={name}: σ neighbor {n?.name} coincident with pivot");
                return false;
            }
            worldDirs.Add(d.normalized);
        }

        Vector3 refN = refLocal.normalized;
        var idealWorld = new List<Vector3>(3);
        if (IsSp2BondBreakEmptyAlongRefCase())
        {
            // sp² carbocation: σ substituents coplanar; empty p along refN — same construction as <see cref="TryComputeCarbocationTrigonalBondBreakSlots"/>.
            var ideal3 = VseprLayout.GetIdealLocalDirections(3);
            Quaternion qPlane = Quaternion.FromToRotation(Vector3.forward, refN);
            for (int i = 0; i < 3; i++)
                idealWorld.Add(transform.TransformDirection((qPlane * ideal3[i]).normalized).normalized);
            if (DebugLogCcBondBreakGeometry)
            {
                float la0 = Vector3.Angle(refN, (qPlane * ideal3[0]).normalized);
                float la1 = Vector3.Angle(refN, (qPlane * ideal3[1]).normalized);
                float la2 = Vector3.Angle(refN, (qPlane * ideal3[2]).normalized);
                LogCcBondBreak($"TryComputeSp2 ideal atom={name}: carbocation σ-trigonal ⟂ ref; ∠(ref,σ-slot0..2)={la0:F1}° {la1:F1}° {la2:F1}° (expect ~90°)");
            }
        }
        else
        {
            var ideal4 = VseprLayout.GetIdealLocalDirections(4);
            var aligned4 = VseprLayout.AlignFirstDirectionTo(ideal4, refN);
            for (int i = 1; i < 4; i++)
                idealWorld.Add(transform.TransformDirection(aligned4[i]).normalized);
            if (DebugLogCcBondBreakGeometry)
            {
                float la1 = Vector3.Angle(refN, aligned4[1]);
                float la2 = Vector3.Angle(refN, aligned4[2]);
                float la3 = Vector3.Angle(refN, aligned4[3]);
                LogCcBondBreak($"TryComputeSp2 ideal atom={name}: radical tetrahedral; local ∠(ref,σ-slot1..3)={la1:F1}° {la2:F1}° {la3:F1}° (tetra ~109.5°)");
            }
        }

        var mapping = FindBestDirectionMapping(worldDirs, idealWorld);
        if (mapping == null || mapping.Count != 3)
        {
            if (DebugLogCcBondBreakGeometry)
                LogCcBondBreak($"TryComputeSp2 fail atom={name}: FindBestDirectionMapping null or count≠3 (world→ideal σ mapping)");
            return false;
        }

        var newDirs = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
            newDirs.Add(mapping[i].newDir.normalized);
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld);
        if (targets == null || targets.Count == 0)
        {
            if (DebugLogCcBondBreakGeometry)
                LogCcBondBreak($"TryComputeSp2 fail atom={name}: BuildSigmaNeighborTargetsWithFragmentRigidRotation produced no moves");
            return false;
        }
        return true;
    }

    /// <summary>Instant apply for redistribute when bond-break animation is not used — same targets as <see cref="TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets"/>.</summary>
    void TryApplySp2BondBreakTrigonalPlanarSigmaNeighborRelax3D(Vector3? refBondWorldDirection, HashSet<AtomFunction> pinWorld = null)
    {
        if (!TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets(refBondWorldDirection, pinWorld, out var t) || t == null) return;

        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, targetWorld) in t)
        {
            atom.transform.position = targetWorld;
            moved.Add(atom);
        }

        RefreshCharge();
        foreach (var a in moved)
            a.RefreshCharge();
        SetupGlobalIgnoreCollisions();
        LogCcBondBreakGeometryDiagnostics("afterTryApplySp2BondBreak");
    }

    /// <summary>
    /// σ bond formation: center gains a fourth distinct σ neighbor (e.g. CH₃⁺ + new C–C) with no occupied non-bond lone lobes.
    /// Move σ substituents onto tetrahedral directions; <paramref name="partnerAlongRef"/> stays fixed (pinned) so the new bond axis is preserved.
    /// </summary>
    public bool TryComputeTetrahedralFourSigmaNeighborRelaxTargets(
        AtomFunction partnerAlongRef,
        HashSet<AtomFunction> pinWorld,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets)
    {
        targets = null;
        if (partnerAlongRef == null) return false;
        var sigmaNeighbors = GetDistinctSigmaNeighborAtoms();
        if (sigmaNeighbors.Count != 4) return false;
        if (bondedOrbitals.Any(o => o != null && o.Bond == null && o.ElectronCount > 0)) return false;

        Vector3 refW = partnerAlongRef.transform.position - transform.position;
        if (refW.sqrMagnitude < 1e-10f) return false;
        refW.Normalize();
        Vector3 refL = transform.InverseTransformDirection(refW).normalized;

        var ideal4 = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(4), refL);
        var idealW = new List<Vector3>(4);
        for (int i = 0; i < 4; i++)
            idealW.Add(transform.TransformDirection(ideal4[i]).normalized);

        var pairs = new List<(AtomFunction n, Vector3 dir)>();
        foreach (var n in sigmaNeighbors)
        {
            var d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) return false;
            pairs.Add((n, d.normalized));
        }
        pairs.Sort((a, b) => a.n.GetInstanceID().CompareTo(b.n.GetInstanceID()));
        var oldDirs = pairs.ConvertAll(p => p.dir);
        var neighOrder = pairs.ConvertAll(p => p.n);

        var mapping = FindBestDirectionMapping(oldDirs, idealW);
        if (mapping == null || mapping.Count != 4) return false;

        var t = new List<(AtomFunction, Vector3)>();
        const float moveEps = 1e-4f;
        for (int i = 0; i < 4; i++)
        {
            var n = neighOrder[i];
            if (pinWorld != null && pinWorld.Contains(n)) continue;
            float dist = Vector3.Distance(transform.position, n.transform.position);
            Vector3 newDir = mapping[i].newDir.normalized;
            Vector3 pos = transform.position + newDir * dist;
            if (Vector3.Distance(n.transform.position, pos) > moveEps)
                t.Add((n, pos));
        }
        if (t.Count == 0) return false;
        targets = t;
        return true;
    }

    /// <summary>σ count increased to 4 with no occupied lone lobes: tetrahedral σ-neighbor relax. Partner hint is usually the new σ bond; if that partner is H and this center has exactly one heavy σ neighbor, align tet to that heavy axis and pin only it (methyl on C–C). Otherwise pin the hint (e.g. CH₄ fourth H). Used for animated bond formation and instant <see cref="RedistributeOrbitals"/>.</summary>
    public bool TryGetTetrahedralFourSigmaNeighborRelaxMovesForBondFormation(
        AtomFunction partnerAlongRef,
        int sigmaNeighborCountBefore,
        out List<(AtomFunction atom, Vector3 startWorld, Vector3 endWorld)> moves)
    {
        moves = null;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return false;
        if (partnerAlongRef == null || sigmaNeighborCountBefore < 0) return false;
        if (GetDistinctSigmaNeighborCount() <= sigmaNeighborCountBefore) return false;
        if (GetDistinctSigmaNeighborCount() != 4) return false;
        if (bondedOrbitals.Any(o => o != null && o.Bond == null && o.ElectronCount > 0)) return false;

        // Methyl / —CH₂— on C–C: partner hint is the new H, but tetrahedron should align its first axis along the C–C bond
        // (heavy neighbor), not along that H. Pinning H + heavy left one H ~90° off the root C–C; pin heavy only and let all H relax.
        var heavies = GetDistinctSigmaNeighborAtoms().Where(n => n != null && n.AtomicNumber > 1).ToList();
        AtomFunction alignAlong = partnerAlongRef;
        var pin = new HashSet<AtomFunction>();
        if (partnerAlongRef.AtomicNumber == 1 && heavies.Count == 1)
        {
            alignAlong = heavies[0];
            pin.Add(heavies[0]);
        }
        else
        {
            pin.Add(partnerAlongRef);
        }

        if (!TryComputeTetrahedralFourSigmaNeighborRelaxTargets(alignAlong, pin, out var t) || t == null || t.Count == 0) return false;
        moves = new List<(AtomFunction, Vector3, Vector3)>();
        foreach (var (n, end) in t)
            moves.Add((n, n.transform.position, end));
        return true;
    }

    void MaybeApplyTetrahedralSigmaRelaxForBondFormation(AtomFunction partnerAlongRef, int sigmaNeighborCountBefore)
    {
        if (!TryGetTetrahedralFourSigmaNeighborRelaxMovesForBondFormation(partnerAlongRef, sigmaNeighborCountBefore, out var moves) || moves == null) return;
        const float moveEps = 1e-4f;
        bool any = false;
        foreach (var (_, s, e) in moves)
            if (Vector3.Distance(s, e) > moveEps) any = true;
        if (!any) return;
        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, _, end) in moves)
        {
            Vector3 from = atom.transform.position;
            atom.transform.position = end;
            LogAttachAnchorCarbonMove("MaybeApplyTetSigmaBondFormation", atom, from, end);
            moved.Add(atom);
        }
        RefreshCharge();
        foreach (var a in moved)
            a.RefreshCharge();
        SetupGlobalIgnoreCollisions();
    }

    /// <summary>Exactly one 0e non-bond and three non-bond orbitals with ≥1 electron (carbocation after C–C cleavage, e.g. C₂).</summary>
    bool TryGetCarbocationOneEmptyThreeElectronsNonbond(out ElectronOrbitalFunction emptyOrb, out List<ElectronOrbitalFunction> occupiedNonBond)
    {
        emptyOrb = null;
        occupiedNonBond = null;
        var nb = bondedOrbitals.Where(o => o != null && o.Bond == null).ToList();
        if (nb.Count != 4) return false;
        var empties = nb.Where(o => o.ElectronCount == 0).ToList();
        var occ = nb.Where(o => o.ElectronCount > 0).ToList();
        if (empties.Count != 1 || occ.Count != 3) return false;
        emptyOrb = empties[0];
        occupiedNonBond = occ;
        return true;
    }

    /// <summary>
    /// sp² carbocation layout: empty p along ex-bond <paramref name="refWorld"/>; three electron lobes in a plane ⊥ that axis (trigonal ~120°).
    /// Not tetrahedral — empty is excluded from the trigonal electron set.
    /// </summary>
    bool TryComputeCarbocationTrigonalBondBreakSlots(Vector3 refWorld, out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots)
    {
        slots = null;
        if (!TryGetCarbocationOneEmptyThreeElectronsNonbond(out var emptyO, out var occ)) return false;
        Vector3 refLocal = transform.InverseTransformDirection(refWorld.normalized).normalized;
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        var ideal3 = VseprLayout.GetIdealLocalDirections(3);
        Quaternion qPlane = Quaternion.FromToRotation(Vector3.forward, refLocal);
        var trigDirs = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
            trigDirs.Add((qPlane * ideal3[i]).normalized);

        occ.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        var oldTips = new List<Vector3>(3);
        foreach (var o in occ)
            oldTips.Add(OrbitalTipLocalDirection(o).normalized);
        var mapping = FindBestDirectionMapping(oldTips, trigDirs);
        if (mapping == null) return false;

        slots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        for (int i = 0; i < 3; i++)
        {
            var newDir = mapping[i].newDir;
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius);
            slots.Add((occ[i], pos, rot));
        }
        {
            var (ePos, eRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(refLocal, bondRadius);
            slots.Add((emptyO, ePos, eRot));
        }
        return true;
    }

    bool TryApplyCarbocationTrigonalPerpendicularToRefLayout(Vector3 refWorld)
    {
        if (!TryComputeCarbocationTrigonalBondBreakSlots(refWorld, out var slots) || slots == null) return false;
        foreach (var (orb, pos, rot) in slots)
        {
            if (orb == null) continue;
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
        }
        return true;
    }

    /// <summary>
    /// Full σ cleavage when non-bond lobes need spreading: bare C₂ (&lt;3 σ neighbors) and carbocation CH₃⁺ (3 σ to H + 1×0e + 3×e⁻ non-bonds).
    /// <see cref="TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets"/> does nothing for C₂, and VSEPR may skip — so lobes would stay stacked without this.
    /// Tetrahedral fallback uses ex-bond as vertex 0. Carbocation (1 empty + 3 e⁻): empty along ref, three lobes trigonal in plane ⊥ ref.
    /// </summary>
    void TrySpreadNonbondOrbitalsForBondBreakSparseSigmaNeighbors(Vector3 refBondWorldDirection, ElectronOrbitalFunction bondBreakGuideOrbital)
    {
        // CH₃⁺ after C–C cleavage has 3 σ neighbors (H,H,H) but still matches carbocation non-bond pattern (1×0e + 3×e⁻).
        // Try carbocation before the σ-count gate — bare C₂ (0 σ) also matches here; tetrahedral fallback is only for other sparse cases.
        if (TryApplyCarbocationTrigonalPerpendicularToRefLayout(refBondWorldDirection))
        {
            if (DebugLogCcBondBreakGeometry)
                LogCcBondBreak(
                    $"TrySpreadSparseSigma atom={name} Z={atomicNumber}: carbocation sp² (1 empty ∥ ex-bond ref, 3 e⁻ lobes trigonal ⊥ ref)");
            return;
        }
        if (GetDistinctSigmaNeighborCount() >= 3) return;
        var nonBond = bondedOrbitals.Where(o => o != null && o.Bond == null).ToList();
        if (nonBond.Count < 2) return;
        nonBond.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        int nMax = Mathf.Min(nonBond.Count, GetOrbitalSlotCount());
        if (nMax < 2) return;

        ElectronOrbitalFunction pin = bondBreakGuideOrbital != null && nonBond.Contains(bondBreakGuideOrbital)
            ? bondBreakGuideOrbital
            : null;
        List<ElectronOrbitalFunction> subset;
        if (pin != null)
        {
            subset = new List<ElectronOrbitalFunction> { pin };
            foreach (var o in nonBond)
            {
                if (o == pin) continue;
                if (subset.Count >= nMax) break;
                subset.Add(o);
            }
        }
        else
            subset = nonBond.Take(nMax).ToList();

        int n = subset.Count;
        if (n < 2) return;

        Vector3 refLocal = transform.InverseTransformDirection(refBondWorldDirection.normalized).normalized;
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        var ideal = VseprLayout.GetIdealLocalDirections(n);
        var aligned = VseprLayout.AlignFirstDirectionTo(ideal, refLocal);
        var newDirList = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
            newDirList.Add(aligned[i].normalized);

        if (pin != null)
        {
            var (pPos, pRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDirList[0], bondRadius);
            pin.transform.localPosition = pPos;
            pin.transform.localRotation = pRot;
            var rest = subset.Where(o => o != pin).ToList();
            var oldR = new List<Vector3>(rest.Count);
            foreach (var o in rest)
                oldR.Add(OrbitalTipLocalDirection(o).normalized);
            var freeR = new List<Vector3>(n - 1);
            for (int i = 1; i < n; i++)
                freeR.Add(newDirList[i]);
            if (oldR.Count != freeR.Count) return;
            var mapping = FindBestDirectionMapping(oldR, freeR);
            if (mapping == null) return;
            for (int i = 0; i < rest.Count; i++)
            {
                var newDir = mapping[i].newDir;
                var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius);
                rest[i].transform.localPosition = pos;
                rest[i].transform.localRotation = rot;
            }
        }
        else
        {
            var oldDirs = new List<Vector3>(n);
            foreach (var o in subset)
                oldDirs.Add(OrbitalTipLocalDirection(o).normalized);
            var mapping = FindBestDirectionMapping(oldDirs, newDirList);
            if (mapping == null) return;
            for (int i = 0; i < n; i++)
            {
                var newDir = mapping[i].newDir;
                var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius);
                subset[i].transform.localPosition = pos;
                subset[i].transform.localRotation = rot;
            }
        }

        if (DebugLogCcBondBreakGeometry)
            LogCcBondBreak(
                $"TrySpreadSparseSigma atom={name} Z={atomicNumber} nonBondCount={nonBond.Count} spreadN={n} σNeighbors={GetDistinctSigmaNeighborCount()} (ex-bond axis = vertex 0)");
    }

    /// <summary>
    /// After a second π forms (triple): AX₂ centers (two σ neighbors, no lone lobes on this atom) should be linear (180°).
    /// Trigonal σ-relax only handles three σ neighbors; coplanar trigonal returns false, so double→triple left sp² σ angles unchanged without this.
    /// </summary>
    public bool TryComputeLinearSigmaNeighborRelaxTargets(Vector3 refLocalNormalized,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets,
        HashSet<AtomFunction> pinWorld = null)
    {
        targets = null;
        if (GetPiBondCount() < 2) return false;
        var sigmaNeighbors = GetDistinctSigmaNeighborAtoms();
        if (sigmaNeighbors.Count != 2) return false;

        var worldDirs = new List<Vector3>(2);
        foreach (var n in sigmaNeighbors)
        {
            var d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) return false;
            worldDirs.Add(d.normalized);
        }
        if (Vector3.Dot(worldDirs[0], worldDirs[1]) <= -0.985f) return false;

        var idealLocal = VseprLayout.GetIdealLocalDirections(2);
        var aligned = VseprLayout.AlignFirstDirectionTo(idealLocal, refLocalNormalized);
        var idealWorld = new List<Vector3>(2);
        for (int i = 0; i < 2; i++)
            idealWorld.Add(transform.TransformDirection(aligned[i]).normalized);

        var mapping = FindBestDirectionMapping(worldDirs, idealWorld);
        if (mapping == null || mapping.Count != 2) return false;

        var newDirs = new List<Vector3>(2);
        for (int i = 0; i < 2; i++)
            newDirs.Add(mapping[i].newDir.normalized);
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld);
        return targets != null && targets.Count > 0;
    }

    /// <summary>
    /// After breaking a π (triple→double etc.): AX₂ centers that were linear (sp) for π≥2 should open σ angles toward trigonal (~120°).
    /// Skips symmetric X–A–X (same heavy element both sides, e.g. CO₂ σ framework) so linear σ is preserved there.
    /// </summary>
    public bool TryComputeOpenedTrigonalSigmaNeighborRelaxTargetsFromLinear(Vector3 refLocalNormalized,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets,
        HashSet<AtomFunction> pinWorld = null)
    {
        targets = null;
        if (GetPiBondCount() != 1) return false;
        var sigmaNeighbors = GetDistinctSigmaNeighborAtoms();
        if (sigmaNeighbors.Count != 2) return false;
        int z0 = sigmaNeighbors[0].AtomicNumber;
        int z1 = sigmaNeighbors[1].AtomicNumber;
        if (z0 == z1 && z0 > 1) return false;

        var worldDirs = new List<Vector3>(2);
        foreach (var n in sigmaNeighbors)
        {
            var d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) return false;
            worldDirs.Add(d.normalized);
        }
        if (Vector3.Dot(worldDirs[0], worldDirs[1]) > -0.985f) return false;

        var idealLocal = VseprLayout.GetIdealLocalDirections(3);
        var aligned = VseprLayout.AlignFirstDirectionTo(idealLocal, refLocalNormalized);
        var idealWorld = new List<Vector3>(2)
        {
            transform.TransformDirection(aligned[1]).normalized,
            transform.TransformDirection(aligned[2]).normalized
        };

        var mapping = FindBestDirectionMapping(worldDirs, idealWorld);
        if (mapping == null || mapping.Count != 2) return false;

        var newDirs = new List<Vector3>(2);
        for (int i = 0; i < 2; i++)
            newDirs.Add(mapping[i].newDir.normalized);
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld);
        return targets != null && targets.Count > 0;
    }

    void TryRelaxSigmaNeighborsToLinear3D(Vector3 refLocalNormalized, HashSet<AtomFunction> pinWorld = null)
    {
        if (!TryComputeLinearSigmaNeighborRelaxTargets(refLocalNormalized, out var targets, pinWorld) || targets == null) return;

        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, targetWorld) in targets)
        {
            atom.transform.position = targetWorld;
            moved.Add(atom);
        }

        RefreshCharge();
        foreach (var a in moved)
            a.RefreshCharge();
        SetupGlobalIgnoreCollisions();
    }

    void TryRelaxSigmaNeighborsOpenedFromLinear3D(Vector3 refLocalNormalized, HashSet<AtomFunction> pinWorld = null)
    {
        if (!TryComputeOpenedTrigonalSigmaNeighborRelaxTargetsFromLinear(refLocalNormalized, out var targets, pinWorld) || targets == null) return;

        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, targetWorld) in targets)
        {
            atom.transform.position = targetWorld;
            moved.Add(atom);
        }

        RefreshCharge();
        foreach (var a in moved)
            a.RefreshCharge();
        SetupGlobalIgnoreCollisions();
    }

    void RedistributeOrbitals3D(float? piBondAngleOverride, Vector3? refBondWorldDirection, bool relaxCoplanarSigmaToTetrahedral = false, bool skipLoneLobeLayout = false, HashSet<AtomFunction> pinAtomsForSigmaRelax = null, bool skipSigmaNeighborRelax = false, ElectronOrbitalFunction bondBreakGuideLoneOrbital = null, AtomFunction newSigmaBondPartnerHint = null, int sigmaNeighborCountBeforeHint = -1)
    {
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1)
        {
            LogVsepr3D($"skip atom={name} Z={atomicNumber}: maxSlots<=1");
            return;
        }

        if (!skipSigmaNeighborRelax)
            MaybeApplyTetrahedralSigmaRelaxForBondFormation(newSigmaBondPartnerHint, sigmaNeighborCountBeforeHint);

        float mergeToleranceDeg = 360f / (2f * maxSlots);
        Vector3 refLocal = ResolveReferenceBondDirectionLocal(piBondAngleOverride, refBondWorldDirection, bondBreakGuideLoneOrbital);
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;
        else refLocal.Normalize();

        // sp² σ framework can stay coplanar after π break while the center should be sp³ (AX₃E). Only from bond-break
        // redistribution — not during FG construction, where trigonal sp² centers also have 3σ + 1 lone lobe before π forms.
        if (!skipSigmaNeighborRelax)
        {
            if (relaxCoplanarSigmaToTetrahedral)
            {
                // π bond break: tet (AX₃E) first, then same σ-relax as normal π formation (bond-break previously skipped linear/trigonal).
                TryRelaxCoplanarSigmaNeighborsToTetrahedral3D(refLocal, pinAtomsForSigmaRelax);
                // Carbocation (3σ + empty p along ref): generic trigonal relax aligns one σ vertex onto refLocal — same axis as the empty p — so skip it; TryApplySp2 places σ neighbors tetrahedrally.
                if (!IsSp2BondBreakEmptyAlongRefCase())
                    TryRelaxSigmaNeighborsToTrigonalPlanar3D(refLocal);
                TryRelaxSigmaNeighborsToLinear3D(refLocal, pinAtomsForSigmaRelax);
                TryRelaxSigmaNeighborsOpenedFromLinear3D(refLocal, pinAtomsForSigmaRelax);
            }
            else
            {
                TryRelaxSigmaNeighborsToTrigonalPlanar3D(refLocal);
                TryRelaxSigmaNeighborsToLinear3D(refLocal);
            }
        }

        // σ-only bond break (3σ + radical/empty shell, no 2e lone pairs on non-bonds): tetrahedral σ neighbor relax (CH₃–CH₃ style).
        // Must run even when σ-neighbor positions were pre-applied during bond-break animation (skipSigmaNeighborRelax=true).
        if (relaxCoplanarSigmaToTetrahedral)
            TryApplySp2BondBreakTrigonalPlanarSigmaNeighborRelax3D(refBondWorldDirection, pinAtomsForSigmaRelax);

        // Bare C₂ (etc.): 0 σ neighbors — TryApplySp2 never runs; VSEPR may skip (no occupied lone lobes). Spread non-bond lobes on tetrahedral/linear frame.
        if (relaxCoplanarSigmaToTetrahedral && refBondWorldDirection.HasValue && refBondWorldDirection.Value.sqrMagnitude >= 0.01f)
            TrySpreadNonbondOrbitalsForBondBreakSparseSigmaNeighbors(refBondWorldDirection.Value, bondBreakGuideLoneOrbital);

        if (skipLoneLobeLayout)
        {
            LogVsepr3D($"skip lone layout atom={name} Z={atomicNumber} skipLoneLobeLayout=true");
            if (DebugLogCcBondBreakGeometry && relaxCoplanarSigmaToTetrahedral && IsSp2BondBreakEmptyAlongRefCase())
                LogCcBondBreak($"Redistribute3D skipLoneLobeLayout=true atom={name} — carbocation uses σ-relax only; OrientEmpty places 0e lobe");
            LogCcBondBreakGeometryDiagnostics("afterRedistribute3D-skipLoneLobeLayout");
            return;
        }

        // Electron domains: σ axes + occupied non-bonded lobes only (0e placeholders are positioned separately).
        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null && orb.ElectronCount > 0).ToList();
        if (loneOrbitals.Count == 0)
        {
            LogVsepr3D($"skip atom={name} Z={atomicNumber}: no occupied lone orbitals");
            if (DebugLogCcBondBreakGeometry && relaxCoplanarSigmaToTetrahedral && IsSp2BondBreakEmptyAlongRefCase())
                LogCcBondBreak($"Redistribute3D no occupied lone lobes atom={name} — VSEPR lone layout skipped; 3σ+empty relies on TryApplySp2 + OrientEmpty (not this block)");
            LogCcBondBreakGeometryDiagnostics("afterRedistribute3D-noOccupiedLone");
            return;
        }

        var bondAxes = CollectSigmaBondAxesLocalMerged(mergeToleranceDeg);
        int domainCount = GetVseprSlotCount3D(bondAxes.Count, loneOrbitals.Count);
        if (domainCount < 1)
            return;

        // Use exactly the electron-domain count (linear / trigonal planar / tetrahedral / …). Do not escalate toward
        // maxSlots when matching is picky — that incorrectly forced tetrahedral for 2–3 domain centers.
        var idealRaw = VseprLayout.GetIdealLocalDirections(domainCount);
        var newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));

        LogVsepr3D(
            $"atom={name} Z={atomicNumber} σAxes={bondAxes.Count} lone={loneOrbitals.Count} domainCount={domainCount} maxValenceSlots={maxSlots} relaxσ→tet={relaxCoplanarSigmaToTetrahedral}");

        ElectronOrbitalFunction pin = bondBreakGuideLoneOrbital != null && loneOrbitals.Contains(bondBreakGuideLoneOrbital) ? bondBreakGuideLoneOrbital : null;
        var loneMatch = pin != null ? loneOrbitals.Where(o => o != pin).ToList() : loneOrbitals;

        bool tryOk = TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, domainCount, bondAxes, loneOrbitals, newDirs, out var bestMapping, out var pinReservedDir, pin);
        if (!tryOk || bestMapping == null || bestMapping.Count != loneMatch.Count)
        {
            LogVsepr3D(
                $"NO APPLY atom={name} Z={atomicNumber}: TryMatch={tryOk} mappingNull={bestMapping == null} mapCount={(bestMapping != null ? bestMapping.Count : -1)} loneMatchCount={loneMatch.Count}");
            return;
        }

        for (int i = 0; i < bestMapping.Count; i++)
        {
            var orb = loneMatch[i];
            if (orb == null) continue;
            var newDir = bestMapping[i].newDir;
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius);
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
            LogVsepr3D(
                $"APPLIED atom={name} Z={atomicNumber} loneIdx={i} orbId={orb.GetInstanceID()} newDir={newDir}");
        }

        if (pin != null && pinReservedDir.HasValue)
        {
            var (pPos, pRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(pinReservedDir.Value, bondRadius);
            pin.transform.localPosition = pPos;
            pin.transform.localRotation = pRot;
            LogVsepr3D(
                $"APPLIED pin atom={name} Z={atomicNumber} orbId={pin.GetInstanceID()} newDir={pinReservedDir.Value}");
        }
    }

    /// <summary>Unit direction (parent local) perpendicular to σ + occupied lone tips — basis for 0e slot placement.</summary>
    static bool TryComputePerpendicularDirectionFromElectronFramework(IReadOnlyList<Vector3> dirs, out Vector3 perpDirLocal)
    {
        perpDirLocal = default;
        if (dirs == null || dirs.Count == 0) return false;
        const float parallelTolDeg = 15f;
        Vector3 perp;
        if (dirs.Count >= 2)
        {
            Vector3 u = dirs[0];
            Vector3 v = dirs[1];
            float ang = Vector3.Angle(u, v);
            int vk = 1;
            while (vk < dirs.Count && (ang < parallelTolDeg || ang > 180f - parallelTolDeg))
            {
                vk++;
                if (vk < dirs.Count)
                {
                    v = dirs[vk];
                    ang = Vector3.Angle(u, v);
                }
            }
            perp = Vector3.Cross(u, v);
            if (perp.sqrMagnitude < 1e-8f) perp = Vector3.Cross(u, Vector3.up);
            if (perp.sqrMagnitude < 1e-8f) perp = Vector3.Cross(u, Vector3.forward);
        }
        else
        {
            Vector3 ax = dirs[0];
            Vector3 aux = Mathf.Abs(Vector3.Dot(ax, Vector3.up)) < 0.95f ? Vector3.up : Vector3.right;
            perp = Vector3.Cross(ax, aux);
            if (perp.sqrMagnitude < 1e-8f) perp = Vector3.Cross(ax, Vector3.forward);
        }

        perp.Normalize();
        perpDirLocal = perp;
        return true;
    }

    /// <summary>Same geometry as <see cref="OrientEmptyNonbondedOrbitalsPerpendicularToFramework"/> — perpendicular slot from framework directions (occupied tips + σ).</summary>
    static bool TryComputePerpendicularEmptySlotFromFrameworkDirs(IReadOnlyList<Vector3> dirs, float bondRadius, out Vector3 localPos, out Quaternion localRot)
    {
        localPos = default;
        localRot = default;
        if (!TryComputePerpendicularDirectionFromElectronFramework(dirs, out Vector3 perp)) return false;
        var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(perp, bondRadius);
        localPos = pos;
        localRot = rot;
        return true;
    }

    static float MinAngleToAnyDirection(Vector3 dir, IReadOnlyList<Vector3> unitDirs)
    {
        if (unitDirs == null || unitDirs.Count == 0) return 180f;
        dir = dir.normalized;
        float worst = 180f;
        foreach (var a in unitDirs)
        {
            if (a.sqrMagnitude < 1e-10f) continue;
            float ang = Vector3.Angle(dir, a.normalized);
            if (ang < worst) worst = ang;
        }
        return worst;
    }

    /// <summary>0e slot perpendicular to electron-containing framework; picks among candidates so direction stays separated from other empty tips.</summary>
    static bool TryComputeSeparatedEmptySlot(
        IReadOnlyList<Vector3> electronFrameworkDirs,
        IReadOnlyList<Vector3> otherEmptyTipsLocal,
        float bondRadius,
        out Vector3 localPos,
        out Quaternion localRot)
    {
        localPos = default;
        localRot = default;
        if (electronFrameworkDirs == null || electronFrameworkDirs.Count == 0) return false;
        if (!TryComputePerpendicularDirectionFromElectronFramework(electronFrameworkDirs, out Vector3 basePerp))
            return false;

        var candidates = new HashSet<Vector3>(new Vector3EqualityComparerApprox());
        void AddCand(Vector3 v)
        {
            if (v.sqrMagnitude < 1e-10f) return;
            candidates.Add(v.normalized);
        }
        AddCand(basePerp);
        AddCand(-basePerp);
        for (int i = 0; i < electronFrameworkDirs.Count; i++)
        {
            Vector3 ei = electronFrameworkDirs[i].normalized;
            for (int j = i + 1; j < electronFrameworkDirs.Count; j++)
            {
                Vector3 ej = electronFrameworkDirs[j].normalized;
                var c = Vector3.Cross(ei, ej);
                AddCand(c);
            }
            var c2 = Vector3.Cross(ei, basePerp);
            AddCand(c2);
        }

        Vector3 best = basePerp;
        float bestScore = -1f;
        foreach (var raw in candidates)
        {
            if (raw.sqrMagnitude < 1e-10f) continue;
            float sepEmpty = MinAngleToAnyDirection(raw, otherEmptyTipsLocal);
            float sepEl = 180f;
            foreach (var e in electronFrameworkDirs)
            {
                if (e.sqrMagnitude < 1e-10f) continue;
                float ang = Vector3.Angle(raw, e.normalized);
                float m = Mathf.Min(ang, 180f - ang);
                if (m < sepEl) sepEl = m;
            }
            float score = Mathf.Min(sepEmpty, sepEl * 1.4f);
            if (score > bestScore)
            {
                bestScore = score;
                best = raw.normalized;
            }
        }

        var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(best, bondRadius);
        localPos = pos;
        localRot = rot;
        return true;
    }

    /// <summary>Equality for hashing candidate orbital directions (~1e-3 rad).</summary>
    sealed class Vector3EqualityComparerApprox : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-6f;

        public int GetHashCode(Vector3 v) =>
            (Mathf.RoundToInt(v.x * 1000f) << 10) ^ (Mathf.RoundToInt(v.y * 1000f) << 5) ^ Mathf.RoundToInt(v.z * 1000f);
    }

    /// <summary>After VSEPR, place each 0e non-bonded lobe. Occupied lone + σ define the framework; 0e lobes sit in directions derived from that (not by dragging the empty p orbital to be “perpendicular to itself”).
    /// σ cleavage (no bond left between the pair): carbocation empty stays along <paramref name="bondBreakRefWorldDirection"/> like CH₃–CH₃; substituents may be H or C.
    /// π component break (σ still between the atoms): remaining σ + other electrons guide layout; empty is placed ⊥ that framework (not along the broken π axis).
    /// Multiple empties: each gets a slot ⊥ electron-containing orbitals and separated from other empties.
    /// <paramref name="placeEmptyAlongBondBreakRef"/> false means π-style placement for the empty even when a ref direction is passed.
    /// <paramref name="skipOrbital"/> excludes a lobe (e.g. bond-break orbital already placed by preview/animation) from being re-oriented here.</summary>
    public void OrientEmptyNonbondedOrbitalsPerpendicularToFramework(Vector3? bondBreakRefWorldDirection = null, ElectronOrbitalFunction skipOrbital = null, bool placeEmptyAlongBondBreakRef = true)
    {
        var empty = bondedOrbitals.Where(o => o != null && o.Bond == null && o.ElectronCount == 0 && (skipOrbital == null || o != skipOrbital)).ToList();
        if (empty.Count == 0) return;

        if (DebugLogCcBondBreakGeometry && !placeEmptyAlongBondBreakRef)
            LogCcBondBreak($"OrientEmpty atom={name}: placeEmptyAlongBondBreakRef=false (σ still between pair) — empty uses ⊥ electron+σ framework if applicable");

        bool refOk = bondBreakRefWorldDirection.HasValue && bondBreakRefWorldDirection.Value.sqrMagnitude >= 0.01f;
        // σ cleavage with ref (ex-σ axis): empty p stays along that direction (same tetrahedral vertex story as CH₃–CH₃).
        if (empty.Count == 1 && refOk && placeEmptyAlongBondBreakRef)
        {
            Vector3 alongLocal = transform.InverseTransformDirection(bondBreakRefWorldDirection.Value.normalized);
            if (alongLocal.sqrMagnitude < 1e-8f) alongLocal = Vector3.right;
            else alongLocal.Normalize();
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(alongLocal, bondRadius);
            foreach (var e in empty)
            {
                e.transform.localPosition = pos;
                e.transform.localRotation = rot;
            }
            LogCcBondBreak($"OrientEmpty branch=alongBondBreakRef atom={name} placeEmptyAlongRef={placeEmptyAlongBondBreakRef}");
            LogCcBondBreakGeometryDiagnostics("afterOrientEmptyAlongRef");
            return;
        }
        if (placeEmptyAlongBondBreakRef && IsSp2BondBreakEmptyAlongRefCase() && empty.Count == 1)
        {
            LogCcBondBreak($"OrientEmpty branch=keepEmptyUnchanged(sp2NoRef) atom={name}");
            return;
        }

        var electronDirs = new List<Vector3>();
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null || o.ElectronCount <= 0) continue;
            electronDirs.Add(OrbitalTipLocalDirection(o).normalized);
        }
        AppendSigmaBondDirectionsLocalDistinctNeighbors(electronDirs);

        if (empty.Count >= 2)
        {
            var placedEmptyTips = new List<Vector3>();
            foreach (var e in empty)
            {
                if (electronDirs.Count == 0)
                    continue;
                if (!TryComputeSeparatedEmptySlot(electronDirs, placedEmptyTips, bondRadius, out var pos, out var rot)) continue;
                e.transform.localPosition = pos;
                e.transform.localRotation = rot;
                placedEmptyTips.Add(OrbitalTipLocalDirection(e).normalized);
            }
            return;
        }

        foreach (var e in empty)
        {
            var dirs = new List<Vector3>();
            foreach (var o in bondedOrbitals)
            {
                if (o == null || o == e || o.Bond != null || o.ElectronCount <= 0) continue;
                dirs.Add(OrbitalTipLocalDirection(o).normalized);
            }
            AppendSigmaBondDirectionsLocalDistinctNeighbors(dirs);
            if (dirs.Count == 0)
                continue;
            if (!TryComputePerpendicularEmptySlotFromFrameworkDirs(dirs, bondRadius, out var pos, out var rot)) continue;
            e.transform.localPosition = pos;
            e.transform.localRotation = rot;
        }
    }

    /// <summary>
    /// Bond-break animation: append 0e lobes whose post-layout pose differs from current. Default: perpendicular to predicted occupied + σ.
    /// σ cleavage: match <see cref="OrientEmptyNonbondedOrbitalsPerpendicularToFramework"/> along-ref when <paramref name="placeEmptyAlongBondBreakRef"/> is true.
    /// Uses <paramref name="occupiedRedistEnds"/> for predicted occupied tips when available (π break / VSEPR).
    /// </summary>
    public void AppendBondBreakEmptyOrbitalAnimTargets(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> occupiedRedistEnds, Vector3? bondBreakRefWorldDirection = null, bool placeEmptyAlongBondBreakRef = true)
    {
        var empties = bondedOrbitals.Where(o => o != null && o.Bond == null && o.ElectronCount == 0).ToList();
        if (empties.Count == 0) return;
        var already = new HashSet<ElectronOrbitalFunction>();
        foreach (var e in occupiedRedistEnds)
            if (e.orb != null) already.Add(e.orb);

        Vector3 TipFromRedistOrCurrent(ElectronOrbitalFunction o)
        {
            for (int i = 0; i < occupiedRedistEnds.Count; i++)
            {
                var e = occupiedRedistEnds[i];
                if (e.orb == o)
                    return (e.rot * Vector3.right).normalized;
            }
            return OrbitalTipLocalDirection(o);
        }

        bool refOk = bondBreakRefWorldDirection.HasValue && bondBreakRefWorldDirection.Value.sqrMagnitude >= 0.01f;
        const float posEps = 0.0004f;
        const float rotEps = 0.35f;

        // Match OrientEmpty: σ cleavage + single empty + ref → animate toward along-ref; π break uses ⊥ framework below.
        if (empties.Count == 1 && refOk && placeEmptyAlongBondBreakRef)
        {
            var emptyOrb = empties[0];
            if (!already.Contains(emptyOrb))
            {
                Vector3 alongLocal = transform.InverseTransformDirection(bondBreakRefWorldDirection.Value.normalized);
                if (alongLocal.sqrMagnitude < 1e-8f) alongLocal = Vector3.right;
                else alongLocal.Normalize();
                var (tPos, tRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(alongLocal, bondRadius);
                if (Vector3.SqrMagnitude(emptyOrb.transform.localPosition - tPos) > posEps * posEps ||
                    Quaternion.Angle(emptyOrb.transform.localRotation, tRot) > rotEps)
                    occupiedRedistEnds.Add((emptyOrb, tPos, tRot));
            }
            return;
        }
        if (placeEmptyAlongBondBreakRef && IsSp2BondBreakEmptyAlongRefCase() && empties.Count == 1)
            return;

        if (empties.Count >= 2)
        {
            var electronDirs = new List<Vector3>();
            foreach (var o in bondedOrbitals)
            {
                if (o == null || o.Bond != null || o.ElectronCount <= 0) continue;
                electronDirs.Add(TipFromRedistOrCurrent(o).normalized);
            }
            AppendSigmaBondDirectionsLocalDistinctNeighbors(electronDirs);

            var placedTips = new List<Vector3>();
            foreach (var emptyOrb in empties)
            {
                if (already.Contains(emptyOrb)) continue;
                if (electronDirs.Count == 0) continue;
                if (!TryComputeSeparatedEmptySlot(electronDirs, placedTips, bondRadius, out var pos, out var rot)) continue;
                if (Vector3.SqrMagnitude(emptyOrb.transform.localPosition - pos) > posEps * posEps ||
                    Quaternion.Angle(emptyOrb.transform.localRotation, rot) > rotEps)
                    occupiedRedistEnds.Add((emptyOrb, pos, rot));
                placedTips.Add((rot * Vector3.right).normalized);
            }
            return;
        }

        foreach (var emptyOrb in empties)
        {
            if (already.Contains(emptyOrb)) continue;
            var dirs = new List<Vector3>();
            foreach (var o in bondedOrbitals)
            {
                if (o == null || o == emptyOrb || o.Bond != null || o.ElectronCount <= 0) continue;
                dirs.Add(TipFromRedistOrCurrent(o));
            }
            AppendSigmaBondDirectionsLocalDistinctNeighbors(dirs);
            if (dirs.Count == 0) continue;
            if (!TryComputePerpendicularEmptySlotFromFrameworkDirs(dirs, bondRadius, out var pos, out var rot)) continue;
            if (Vector3.SqrMagnitude(emptyOrb.transform.localPosition - pos) > posEps * posEps ||
                Quaternion.Angle(emptyOrb.transform.localRotation, rot) > rotEps)
                occupiedRedistEnds.Add((emptyOrb, pos, rot));
        }
    }

    Vector3 ResolveReferenceBondDirectionLocal(float? piBondAngleOverride, Vector3? refBondWorldDirection, ElectronOrbitalFunction bondBreakGuideOrbital = null)
    {
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (GetBondsTo(other) <= 1) continue;
            var w = (other.transform.position - transform.position).normalized;
            if (w.sqrMagnitude >= 0.01f)
                return transform.InverseTransformDirection(w).normalized;
        }
        if (refBondWorldDirection.HasValue)
        {
            var w = refBondWorldDirection.Value;
            if (w.sqrMagnitude >= 0.01f)
                return transform.InverseTransformDirection(w.normalized).normalized;
        }
        if (bondBreakGuideOrbital != null)
        {
            var t = OrbitalTipLocalDirection(bondBreakGuideOrbital);
            if (t.sqrMagnitude > 1e-8f) return t.normalized;
        }
        if (piBondAngleOverride.HasValue)
        {
            float a = NormalizeAngleTo360(piBondAngleOverride.Value) * Mathf.Deg2Rad;
            var worldXy = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            return transform.InverseTransformDirection(worldXy).normalized;
        }
        if (covalentBonds.Count > 0)
        {
            var b = covalentBonds.FirstOrDefault(x => x?.AtomA != null && x?.AtomB != null);
            if (b != null)
            {
                var other = b.AtomA == this ? b.AtomB : b.AtomA;
                var w = (other.transform.position - transform.position).normalized;
                if (w.sqrMagnitude >= 0.01f)
                    return transform.InverseTransformDirection(w).normalized;
            }
        }
        return Vector3.right;
    }

    Vector3 RedistributeReferenceDirectionLocalForTargets(AtomFunction newBondPartner)
    {
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (GetBondsTo(other) <= 1) continue;
            var w = (other.transform.position - transform.position).normalized;
            if (w.sqrMagnitude >= 0.01f)
                return transform.InverseTransformDirection(w).normalized;
        }
        if (newBondPartner != null)
        {
            var w = (newBondPartner.transform.position - transform.position).normalized;
            if (w.sqrMagnitude >= 0.01f)
                return transform.InverseTransformDirection(w).normalized;
        }
        if (covalentBonds.Count > 0)
        {
            var b = covalentBonds.FirstOrDefault(x => x?.AtomA != null && x?.AtomB != null);
            if (b != null)
            {
                var other = b.AtomA == this ? b.AtomB : b.AtomA;
                var w = (other.transform.position - transform.position).normalized;
                if (w.sqrMagnitude >= 0.01f)
                    return transform.InverseTransformDirection(w).normalized;
            }
        }
        return Vector3.right;
    }

    static Vector3 OrbitalTipLocalDirection(ElectronOrbitalFunction orb) =>
        (orb.transform.localRotation * Vector3.right).normalized;

    List<Vector3> CollectUniqueOrbitalDirections(float toleranceDeg)
    {
        var bondDirs = new List<Vector3>();
        AppendSigmaBondDirectionsLocalDistinctNeighbors(bondDirs);
        var mergedBond = MergeDirectionsWithinTolerance(bondDirs, toleranceDeg);

        var loneDirs = new List<Vector3>();
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.transform.parent != transform) continue;
            loneDirs.Add(OrbitalTipLocalDirection(orb));
        }
        var result = new List<Vector3>(mergedBond);
        result.AddRange(loneDirs);
        return result;
    }

    /// <summary>Lexicographic order on normalized components so sorts and ties are reproducible across frames.</summary>
    static int CompareVectorsStable(Vector3 a, Vector3 b)
    {
        a = a.normalized;
        b = b.normalized;
        int c = a.x.CompareTo(b.x);
        if (c != 0) return c;
        c = a.y.CompareTo(b.y);
        if (c != 0) return c;
        return a.z.CompareTo(b.z);
    }

    static int ComparePermutationLex(int[] a, int[] b)
    {
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    static List<Vector3> MergeDirectionsWithinTolerance(List<Vector3> dirs, float toleranceDeg)
    {
        if (dirs.Count == 0) return new List<Vector3>();
        var sorted = new List<Vector3>(dirs);
        sorted.Sort(CompareVectorsStable);
        dirs = sorted;
        var used = new bool[dirs.Count];
        var result = new List<Vector3>();
        for (int i = 0; i < dirs.Count; i++)
        {
            if (used[i]) continue;
            Vector3 acc = dirs[i].normalized;
            int count = 1;
            used[i] = true;
            for (int j = i + 1; j < dirs.Count; j++)
            {
                if (used[j]) continue;
                if (Vector3.Angle(acc, dirs[j]) <= toleranceDeg)
                {
                    acc += dirs[j].normalized;
                    count++;
                    used[j] = true;
                }
            }
            result.Add((acc / count).normalized);
        }
        return result;
    }

    static int FindClosestDirectionIndex(List<Vector3> dirs, Vector3 target, float _)
    {
        target = target.normalized;
        int best = 0;
        float bestAng = 360f;
        for (int i = 0; i < dirs.Count; i++)
        {
            float ang = Vector3.Angle(dirs[i], target);
            if (ang < bestAng)
            {
                bestAng = ang;
                best = i;
            }
        }
        return best;
    }

    static List<(Vector3 oldDir, Vector3 newDir)> FindBestDirectionMapping(List<Vector3> oldDirs, List<Vector3> newDirs)
    {
        if (oldDirs.Count != newDirs.Count || oldDirs.Count == 0) return null;
        var indices = Enumerable.Range(0, newDirs.Count).ToArray();
        List<(Vector3, Vector3)> bestMapping = null;
        float bestTotal = float.MaxValue;
        int[] bestPerm = null;
        const float angleEps = 1e-3f;

        foreach (var perm in Permutations(indices))
        {
            float total = 0f;
            for (int i = 0; i < oldDirs.Count; i++)
                total += Vector3.Angle(oldDirs[i], newDirs[perm[i]]);
            bool better = bestMapping == null
                || total < bestTotal - angleEps
                || (Mathf.Abs(total - bestTotal) <= angleEps && ComparePermutationLex(perm, bestPerm) < 0);

            if (better)
            {
                bestTotal = total;
                bestPerm = (int[])perm.Clone();
                bestMapping = new List<(Vector3, Vector3)>();
                for (int i = 0; i < oldDirs.Count; i++)
                    bestMapping.Add((oldDirs[i], newDirs[perm[i]]));
            }
        }
        return bestMapping;
    }

    static bool DirectionsWithinTolerance(Vector3 a, Vector3 b, float tolDeg) =>
        Vector3.Angle(a, b) <= tolDeg;

    /// <summary>Returns (orbital, targetLocalPos, targetLocalRot) for orbitals that would move during RedistributeOrbitals. For sigma bonds, pass newBondPartner to redistribute when bonding to an orbital that was already rotated. Pass <paramref name="sigmaNeighborCountBefore"/> (σ count before the new bond is created) so a 3→4 σ increase can run tetrahedral σ-relax even when π count is unchanged and there are no occupied lone lobes (carbocation → sp³). Set <paramref name="bondingTopologyChanged"/> true on an atom only when its π count did not change (σ-only bond break) so layout still runs; when π count changes (e.g. π bond break), pass false. When <paramref name="skipLoneRedistTargetsForSigmaOnlyBreak"/> is true (σ-only break: π unchanged on both atoms), skip returning targets only if no bond-break animation context is provided — otherwise <see cref="GetRedistributeTargets3D"/> still runs so non–break lone lobes can animate. For bond-break animation, pass the same π/ref/guide args as the post-break <see cref="RedistributeOrbitals"/> call so targets match <see cref="RedistributeOrbitals3D"/> (pin lobe + reference direction).</summary>
    public List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> GetRedistributeTargets(int piBefore, AtomFunction newBondPartner = null, bool bondingTopologyChanged = false, bool skipLoneRedistTargetsForSigmaOnlyBreak = false, float? piBondAngleOverrideForBreakTargets = null, Vector3? refBondWorldDirectionForBreakTargets = null, ElectronOrbitalFunction bondBreakGuideLoneOrbitalForTargets = null, int sigmaNeighborCountBefore = -1)
    {
        var result = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        if (skipLoneRedistTargetsForSigmaOnlyBreak && bondingTopologyChanged)
        {
            bool haveBreakBondAnimContext = piBondAngleOverrideForBreakTargets.HasValue || refBondWorldDirectionForBreakTargets.HasValue || bondBreakGuideLoneOrbitalForTargets != null;
            if (!haveBreakBondAnimContext)
                return result;
        }
        if (!bondingTopologyChanged)
        {
            bool piCountChanged = GetPiBondCount() != piBefore;
            bool sigmaNeighborCountIncreased = sigmaNeighborCountBefore >= 0 && GetDistinctSigmaNeighborCount() > sigmaNeighborCountBefore;
            if (!piCountChanged && !sigmaNeighborCountIncreased && newBondPartner == null) return result;
        }

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return GetRedistributeTargets3D(piBefore, newBondPartner, piBondAngleOverrideForBreakTargets, refBondWorldDirectionForBreakTargets, bondBreakGuideLoneOrbitalForTargets, sigmaNeighborCountBefore);

        int slotCount = GetOrbitalSlotCount();
        if (slotCount <= 1) return result;

        float tolerance = 360f / (2f * slotCount);
        var oldAngles = CollectUniqueOrbitalAngles(tolerance);
        if (oldAngles.Count == 0) return result;

        float? piBondAngle = null;
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (GetBondsTo(other) <= 1) continue;
            var dir = (other.transform.position - transform.position).normalized;
            piBondAngle = OrbitalAngleUtility.DirectionToAngleWorld(dir);
            break;
        }
        if (!piBondAngle.HasValue && newBondPartner != null)
        {
            var dir = (newBondPartner.transform.position - transform.position).normalized;
            if (dir.sqrMagnitude >= 0.01f) piBondAngle = OrbitalAngleUtility.DirectionToAngleWorld(dir);
        }
        float originAngle = piBondAngle.HasValue ? NormalizeAngleTo360(piBondAngle.Value) : 0f;

        int n = oldAngles.Count;
        float step = 360f / n;
        var newAngles = new List<float>();
        for (int i = 0; i < n; i++)
            newAngles.Add(NormalizeAngleTo360(originAngle + i * step));

        List<float> oldNonPi;
        List<float> newNonPi;
        if (covalentBonds.Count > 0)
        {
            float refAngle = originAngle;
            var b = covalentBonds.FirstOrDefault(x => x?.AtomA != null && x?.AtomB != null);
            if (b != null)
            {
                var other = b.AtomA == this ? b.AtomB : b.AtomA;
                var dir = (other.transform.position - transform.position).normalized;
                if (dir.sqrMagnitude >= 0.01f) refAngle = NormalizeAngleTo360(OrbitalAngleUtility.DirectionToAngleWorld(dir));
            }
            int refIdxOld = FindClosestAngleIndex(oldAngles, refAngle, tolerance);
            int refIdxNew = FindClosestAngleIndex(newAngles, refAngle, tolerance);
            oldNonPi = oldAngles.Where((_, i) => i != refIdxOld).ToList();
            newNonPi = newAngles.Where((_, i) => i != refIdxNew).ToList();
        }
        else
        {
            oldNonPi = oldAngles;
            newNonPi = newAngles;
        }
        if (oldNonPi.Count == 0) return result;

        var bestMapping = FindBestAngleMapping(oldNonPi, newNonPi);
        if (bestMapping == null) return result;

        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null).ToList();
        var moved = new HashSet<ElectronOrbitalFunction>();
        foreach (var (oldAngle, newAngle) in bestMapping)
        {
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPosition(newAngle, bondRadius);
            foreach (var orb in loneOrbitals)
            {
                if (moved.Contains(orb)) continue;
                float orbAngle = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
                if (AnglesWithinTolerance(orbAngle, oldAngle, tolerance))
                {
                    result.Add((orb, pos, rot));
                    moved.Add(orb);
                    break;
                }
            }
        }
        return result;
    }

    List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> GetRedistributeTargets3D(int piBefore, AtomFunction newBondPartner, float? piBondAngleOverrideForBreakTargets, Vector3? refBondWorldDirectionForBreakTargets, ElectronOrbitalFunction bondBreakGuideLoneOrbitalForTargets, int sigmaNeighborCountBefore = -1)
    {
        var result = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1)
            return result;

        float mergeToleranceDeg = 360f / (2f * maxSlots);
        bool useBreakRefs = bondBreakGuideLoneOrbitalForTargets != null || piBondAngleOverrideForBreakTargets.HasValue || refBondWorldDirectionForBreakTargets.HasValue;
        Vector3 refLocal = useBreakRefs
            ? ResolveReferenceBondDirectionLocal(piBondAngleOverrideForBreakTargets, refBondWorldDirectionForBreakTargets, bondBreakGuideLoneOrbitalForTargets)
            : RedistributeReferenceDirectionLocalForTargets(newBondPartner);
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;
        else refLocal.Normalize();

        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null && orb.ElectronCount > 0).ToList();
        if (loneOrbitals.Count == 0)
            return result;

        // Bond-break carbocation: 3 e⁻ lobes + 1 empty — trigonal ⊥ ex-bond (C₂ fragments and CH₃⁺ after C–C cleavage; σ count may be 0 or 3).
        if (useBreakRefs && refBondWorldDirectionForBreakTargets.HasValue && refBondWorldDirectionForBreakTargets.Value.sqrMagnitude >= 0.01f
            && TryComputeCarbocationTrigonalBondBreakSlots(refBondWorldDirectionForBreakTargets.Value, out var carbSlots)
            && carbSlots != null && carbSlots.Count > 0)
            return carbSlots;

        var bondAxes = CollectSigmaBondAxesLocalMerged(mergeToleranceDeg);
        int slotCount = GetVseprSlotCount3D(bondAxes.Count, loneOrbitals.Count);

        var idealRaw = VseprLayout.GetIdealLocalDirections(slotCount);
        var newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));

        ElectronOrbitalFunction pin = bondBreakGuideLoneOrbitalForTargets != null && loneOrbitals.Contains(bondBreakGuideLoneOrbitalForTargets) ? bondBreakGuideLoneOrbitalForTargets : null;
        var loneMatch = pin != null ? loneOrbitals.Where(o => o != pin).ToList() : loneOrbitals;

        if (!TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, slotCount, bondAxes, loneOrbitals, newDirs, out var bestMapping, out var pinReservedDir, pin) ||
            bestMapping == null || bestMapping.Count != loneMatch.Count)
            return result;

        for (int i = 0; i < bestMapping.Count; i++)
        {
            var orb = loneMatch[i];
            if (orb == null) continue;
            var newDir = bestMapping[i].newDir;
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius);
            result.Add((orb, pos, rot));
        }

        if (pin != null && pinReservedDir.HasValue)
        {
            var (pPos, pRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(pinReservedDir.Value, bondRadius);
            result.Add((pin, pPos, pRot));
        }
        return result;
    }

    public void ApplyRedistributeTargets(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets)
    {
        foreach (var (orb, pos, rot) in targets)
        {
            if (orb != null && orb.transform.parent == transform)
            {
                orb.transform.localPosition = pos;
                orb.transform.localRotation = rot;
            }
        }
    }

    static float NormalizeAngleTo360(float deg)
    {
        while (deg >= 360f) deg -= 360f;
        while (deg < 0f) deg += 360f;
        return deg;
    }

    static float AngularDifference(float a, float b)
    {
        float delta = Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(a - b));
        return delta > 180f ? 360f - delta : delta;
    }

    static bool AnglesWithinTolerance(float a, float b, float tol)
    {
        return AngularDifference(a, b) <= tol;
    }

    List<float> CollectUniqueOrbitalAngles(float tolerance)
    {
        // Collect bond angles (merge multiple bonds to same atom - double/triple = 1 angle)
        var bondAngles = new List<float>();
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            var dir = (other.transform.position - transform.position).normalized;
            bondAngles.Add(OrbitalAngleUtility.DirectionToAngleWorld(dir));
        }
        var mergedBondAngles = MergeAnglesWithinTolerance(bondAngles, tolerance);

        // Collect lone orbital angles - each lone orbital gets its own slot (no merging)
        // so orbitals placed at bond dir by BreakBond are correctly redistributed to non-bond slots
        var loneAngles = new List<float>();
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.transform.parent != transform) continue;
            loneAngles.Add(OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform));
        }

        // Combine: bond angles + lone angles. Do NOT merge - each lone orbital needs a slot
        var result = new List<float>(mergedBondAngles);
        result.AddRange(loneAngles);
        return result;
    }

    static List<float> MergeAnglesWithinTolerance(List<float> angles, float tolerance)
    {
        if (angles.Count == 0) return new List<float>();
        var result = new List<float>();
        var used = new bool[angles.Count];
        for (int i = 0; i < angles.Count; i++)
        {
            if (used[i]) continue;
            float representative = NormalizeAngleTo360(angles[i]);
            used[i] = true;
            for (int j = i + 1; j < angles.Count; j++)
            {
                if (used[j]) continue;
                float aj = NormalizeAngleTo360(angles[j]);
                if (AngularDifference(representative, aj) <= tolerance)
                    used[j] = true;
            }
            result.Add(representative);
        }
        return result;
    }

    static int FindClosestAngleIndex(List<float> angles, float target, float tolerance)
    {
        float targetNorm = NormalizeAngleTo360(target);
        int best = 0;
        float bestDelta = 360f;
        for (int i = 0; i < angles.Count; i++)
        {
            float d = AngularDifference(NormalizeAngleTo360(angles[i]), targetNorm);
            if (d < bestDelta)
            {
                bestDelta = d;
                best = i;
            }
        }
        return best;
    }

    static List<(float oldAngle, float newAngle)> FindBestAngleMapping(List<float> oldAngles, List<float> newAngles)
    {
        if (oldAngles.Count != newAngles.Count || oldAngles.Count == 0) return null;
        var indices = Enumerable.Range(0, newAngles.Count).ToArray();
        List<(float, float)> bestMapping = null;
        float bestTotal = float.MaxValue;

        foreach (var perm in Permutations(indices))
        {
            float total = 0f;
            for (int i = 0; i < oldAngles.Count; i++)
                total += AngularDifference(oldAngles[i], newAngles[perm[i]]);
            if (total < bestTotal)
            {
                bestTotal = total;
                bestMapping = new List<(float, float)>();
                for (int i = 0; i < oldAngles.Count; i++)
                    bestMapping.Add((oldAngles[i], newAngles[perm[i]]));
            }
        }
        return bestMapping;
    }

    static IEnumerable<int[]> Permutations(int[] arr)
    {
        if (arr.Length == 0) { yield return arr; yield break; }
        var a = (int[])arr.Clone();
        yield return a;
        while (NextPermutation(a))
            yield return (int[])a.Clone();
    }

    static bool NextPermutation(int[] a)
    {
        int i = a.Length - 2;
        while (i >= 0 && a[i] >= a[i + 1]) i--;
        if (i < 0) return false;
        int j = a.Length - 1;
        while (a[j] <= a[i]) j--;
        (a[i], a[j]) = (a[j], a[i]);
        System.Array.Reverse(a, i + 1, a.Length - i - 1);
        return true;
    }

    public (Vector3 position, Quaternion rotation) GetSlotForNewOrbital(Vector3 preferredDirectionWorld, ElectronOrbitalFunction excludeOrbital = null)
    {
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return GetSlotForNewOrbital3D(preferredDirectionWorld, excludeOrbital);

        Vector3 localDir = transform.InverseTransformDirection(preferredDirectionWorld);
        localDir.z = 0;
        if (localDir.sqrMagnitude < 0.01f) localDir = Vector3.right;
        localDir.Normalize();
        float preferredAngle = Mathf.Atan2(localDir.y, localDir.x) * Mathf.Rad2Deg;

        int slotCount = GetOrbitalSlotCount();
        float[] slotAngles = GetSlotAnglesForCount(slotCount);
        float slotTolerance = 360f / (2f * Mathf.Max(1, slotCount));

        float bestAngle = preferredAngle;
        float bestDelta = 360f;
        foreach (float slotAngle in slotAngles)
        {
            bool occupied = false;
            foreach (var orb in GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb.transform.parent != transform) continue;
                if (orb == excludeOrbital || orb == null) continue;
                float orbAngle = ElectronOrbitalFunction.NormalizeAngle(orb.transform.localEulerAngles.z);
                float delta = Mathf.Abs(ElectronOrbitalFunction.NormalizeAngle(orbAngle - slotAngle));
                if (delta < slotTolerance || delta > 360f - slotTolerance)
                {
                    occupied = true;
                    break;
                }
            }
            if (!occupied)
            {
                float delta = Mathf.Abs(ElectronOrbitalFunction.NormalizeAngle(slotAngle - preferredAngle));
                if (delta > 180f) delta = 360f - delta;
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestAngle = slotAngle;
                }
            }
        }
        return ElectronOrbitalFunction.GetCanonicalSlotPosition(bestAngle, bondRadius);
    }

    (Vector3 position, Quaternion rotation) GetSlotForNewOrbital3D(Vector3 preferredDirectionWorld, ElectronOrbitalFunction excludeOrbital)
    {
        Vector3 prefLocal = transform.InverseTransformDirection(preferredDirectionWorld);
        if (prefLocal.sqrMagnitude < 1e-6f) prefLocal = Vector3.right;
        prefLocal.Normalize();

        int slotCount = GetOrbitalSlotCount();
        float slotToleranceDeg = 360f / (2f * Mathf.Max(1, slotCount));
        var slots = VseprLayout.GetIdealLocalDirections(slotCount);

        float bestDot = -2f;
        Vector3 bestDir = slots[0];
        const float dotEps = 1e-5f;
        foreach (var slot in slots)
        {
            bool occupied = false;
            foreach (var orb in GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb.transform.parent != transform || orb == excludeOrbital || orb == null) continue;
                if (Vector3.Angle(OrbitalTipLocalDirection(orb), slot) < slotToleranceDeg)
                {
                    occupied = true;
                    break;
                }
            }
            if (!occupied)
            {
                float dot = Vector3.Dot(prefLocal, slot);
                if (dot > bestDot + dotEps)
                {
                    bestDot = dot;
                    bestDir = slot;
                }
                else if (Mathf.Abs(dot - bestDot) <= dotEps)
                {
                    // Deterministic tie-break (mirrored C–C endpoints can otherwise pick different slots at equal dot).
                    if (CompareLex3(slot, bestDir) > 0)
                    {
                        bestDot = dot;
                        bestDir = slot;
                    }
                }
            }
        }
        return ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(bestDir, bondRadius);
    }

    /// <summary>True if no other orbital (except <paramref name="excludeOrbital"/>) occupies this ideal local direction (same test as <see cref="GetSlotForNewOrbital3D"/>).</summary>
    public static bool IdealSlotDirectionUnoccupiedFor3D(AtomFunction atom, Vector3 idealDirLocal, float slotToleranceDeg, ElectronOrbitalFunction excludeOrbital)
    {
        if (atom == null) return false;
        idealDirLocal = idealDirLocal.normalized;
        foreach (var orb in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != atom.transform || orb == excludeOrbital || orb == null) continue;
            if (Vector3.Angle(OrbitalTipLocalDirection(orb), idealDirLocal) < slotToleranceDeg)
                return false;
        }
        return true;
    }

    /// <summary>Which VSEPR ideal vertex (index into <see cref="VseprLayout.GetIdealLocalDirections"/>) best matches the given tip direction in the atom's local space.</summary>
    public static int ClosestIdealLocalDirectionIndex(Vector3 tipLocalNormalized, int slotCount)
    {
        var slots = VseprLayout.GetIdealLocalDirections(slotCount);
        float bestDot = -2f;
        int bestIdx = 0;
        const float dotEps = 1e-5f;
        for (int j = 0; j < slots.Length; j++)
        {
            Vector3 slot = slots[j].normalized;
            float dot = Vector3.Dot(tipLocalNormalized, slot);
            if (dot > bestDot + dotEps)
            {
                bestDot = dot;
                bestIdx = j;
            }
            else if (Mathf.Abs(dot - bestDot) <= dotEps)
            {
                if (CompareLex3(slot, slots[bestIdx]) > 0)
                {
                    bestDot = dot;
                    bestIdx = j;
                }
            }
        }
        return bestIdx;
    }

    static int CompareLex3(Vector3 a, Vector3 b)
    {
        if (a.x > b.x + 1e-7f) return 1;
        if (a.x < b.x - 1e-7f) return -1;
        if (a.y > b.y + 1e-7f) return 1;
        if (a.y < b.y - 1e-7f) return -1;
        if (a.z > b.z + 1e-7f) return 1;
        if (a.z < b.z - 1e-7f) return -1;
        return 0;
    }

    public ElectronOrbitalFunction GetEmptyLoneOrbital()
    {
        foreach (var orb in bondedOrbitals)
            if (orb != null && orb.Bond == null && orb.ElectronCount == 0)
                return orb;
        return null;
    }

    public ElectronOrbitalFunction GetAvailableLoneOrbitalForBond(ElectronOrbitalFunction excludeOrbital, int sourceElectronCount, Vector3 hitPosition, Camera viewCamera = null)
    {
        var cam = viewCamera != null ? viewCamera : Camera.main;
        ElectronOrbitalFunction best = null;
        float bestScore = float.MaxValue;

        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb == excludeOrbital) continue;
            if (orb.ElectronCount + sourceElectronCount != ElectronOrbitalFunction.MaxElectrons) continue;

            bool hit3d = orb.ContainsPoint(hitPosition);
            bool hitView = cam != null && excludeOrbital != null &&
                           ElectronOrbitalFunction.OrbitalViewOverlaps(cam, excludeOrbital, orb);
            if (!hit3d && !hitView) continue;

            float score;
            if (cam == null)
                return orb;
            Vector3 sh = cam.WorldToScreenPoint(hitPosition);
            Vector3 so = cam.WorldToScreenPoint(orb.transform.position);
            if (sh.z >= cam.nearClipPlane && so.z >= cam.nearClipPlane)
                score = ((Vector2)sh - (Vector2)so).sqrMagnitude;
            else
                score = hit3d ? 0f : float.MaxValue;

            if (score < bestScore)
            {
                bestScore = score;
                best = orb;
            }
        }

        return best;
    }

    /// <summary>Returns a lone orbital with exactly 1 electron, preferring the one closest to preferredDirection. For programmatic bonding.</summary>
    public ElectronOrbitalFunction GetLoneOrbitalWithOneElectron(Vector3 preferredDirectionWorld)
    {
        ElectronOrbitalFunction best = null;
        float bestDot = -2f;
        Vector3 dirNorm = preferredDirectionWorld.sqrMagnitude >= 0.01f ? preferredDirectionWorld.normalized : Vector3.right;
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount != 1) continue;
            float dot = Vector3.Dot(orb.transform.TransformDirection(Vector3.right), dirNorm);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = orb;
            }
        }
        return best;
    }

    /// <summary>Returns a lone orbital for bond formation. Prefers 1-electron; if only 2-electron (lone pair) available, returns it and sets to 1. Used when replacing H.</summary>
    public ElectronOrbitalFunction GetLoneOrbitalForBondFormation(Vector3 preferredDirectionWorld)
    {
        var one = GetLoneOrbitalWithOneElectron(preferredDirectionWorld);
        if (one != null) return one;
        ElectronOrbitalFunction best = null;
        float bestDot = -2f;
        Vector3 dirNorm = preferredDirectionWorld.sqrMagnitude >= 0.01f ? preferredDirectionWorld.normalized : Vector3.right;
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount != 2) continue;
            float dot = Vector3.Dot(orb.transform.TransformDirection(Vector3.right), dirNorm);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = orb;
            }
        }
        if (best != null)
            best.ElectronCount = 1;
        return best;
    }

    public bool HasEmptyLoneOrbital() => GetEmptyLoneOrbital() != null;

    /// <summary>Returns lone orbitals with 1 electron, sorted by angle (0° to 360°).</summary>
    public List<ElectronOrbitalFunction> GetLoneOrbitalsWithOneElectronSortedByAngle()
    {
        var list = new List<ElectronOrbitalFunction>();
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount != 1) continue;
            list.Add(orb);
        }
        list.Sort((a, b) =>
        {
            float aa = OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.GetOrbitalAngleWorld(a.transform));
            float ab = OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.GetOrbitalAngleWorld(b.transform));
            return aa.CompareTo(ab);
        });
        return list;
    }

    /// <summary>Every orbital on this atom (lone and bond-associated), sorted by world pointing angle for stable keyboard order in 2D and 3D.</summary>
    public List<ElectronOrbitalFunction> GetAllOrbitalsSortedForArrowCycling()
    {
        var list = new List<ElectronOrbitalFunction>();
        foreach (var orb in bondedOrbitals)
        {
            if (orb != null) list.Add(orb);
        }
        list.Sort((a, b) =>
        {
            float aa = OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.GetOrbitalAngleWorld(a.transform));
            float ab = OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.GetOrbitalAngleWorld(b.transform));
            int cmp = aa.CompareTo(ab);
            if (cmp != 0) return cmp;
            return a.GetInstanceID().CompareTo(b.GetInstanceID());
        });
        return list;
    }

    /// <summary>Next/previous in <see cref="GetAllOrbitalsSortedForArrowCycling"/> (wraps). Delta +1 moves forward in that list.</summary>
    public ElectronOrbitalFunction GetAdjacentOrbitalInList(ElectronOrbitalFunction current, int delta)
    {
        var list = GetAllOrbitalsSortedForArrowCycling();
        if (list.Count == 0) return null;
        if (current == null) return list[0];
        int idx = list.IndexOf(current);
        if (idx < 0) return list[0];
        int n = list.Count;
        int next = ((idx + delta) % n + n) % n;
        return list[next];
    }

    /// <summary>Orbital closest to targetAngle (0° = right). For initial selection.</summary>
    public ElectronOrbitalFunction GetOrbitalClosestToAngle(float targetAngleDeg)
    {
        return GetLoneOrbitalWithOneElectron(new Vector3(Mathf.Cos(targetAngleDeg * Mathf.Deg2Rad), Mathf.Sin(targetAngleDeg * Mathf.Deg2Rad), 0));
    }

    GameObject selectionHighlight;
    GameObject selectionHighlight3D;

    /// <summary>Opaque RGB matching the edit-mode selection ring (2D vs 3D path).</summary>
    public static Color GetSelectionHighlightRingColorRgb()
    {
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return new Color(0.25f, 0.98f, 0.42f, 1f);
        return new Color(0.3f, 0.9f, 0.4f, 1f);
    }

    public void SetSelectionHighlight(bool on)
    {
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            SetSelectionHighlight3D(on);
            if (selectionHighlight != null) selectionHighlight.SetActive(false);
            return;
        }

        if (selectionHighlight3D != null) selectionHighlight3D.SetActive(false);

        if (on)
        {
            if (selectionHighlight == null)
            {
                selectionHighlight = new GameObject("SelectionHighlight");
                selectionHighlight.transform.SetParent(transform);
                selectionHighlight.transform.localPosition = Vector3.zero;
                selectionHighlight.transform.localRotation = Quaternion.identity;
                selectionHighlight.transform.localScale = Vector3.one * 1.4f;
                var sr = selectionHighlight.AddComponent<SpriteRenderer>();
                sr.sprite = CreateCircleSprite();
                sr.color = new Color(0.3f, 0.9f, 0.4f, 0.6f);
                sr.sortingOrder = -1;
            }
            if (selectionHighlight != null) selectionHighlight.SetActive(true);
        }
        else if (selectionHighlight != null)
        {
            selectionHighlight.SetActive(false);
        }
    }

    void SetSelectionHighlight3D(bool on)
    {
        if (!on)
        {
            if (selectionHighlight3D != null) selectionHighlight3D.SetActive(false);
            return;
        }

        if (selectionHighlight3D != null && selectionHighlight3D.GetComponent<SpriteRenderer>() == null)
        {
            Destroy(selectionHighlight3D);
            selectionHighlight3D = null;
        }

        if (selectionHighlight3D == null)
        {
            selectionHighlight3D = new GameObject("SelectionHighlight3D");
            selectionHighlight3D.transform.SetParent(transform, false);
            selectionHighlight3D.transform.localPosition = Vector3.zero;
            selectionHighlight3D.transform.localRotation = Quaternion.identity;
            selectionHighlight3D.transform.localScale = Vector3.one;
            var sr = selectionHighlight3D.AddComponent<SpriteRenderer>();
            sr.sprite = CreateCircleSprite();
            sr.color = new Color(0.25f, 0.98f, 0.42f, 0.92f);
            sr.sortingOrder = 80;
        }

        float rWorld = GetAtomBodyRadiusWorld();
        float parentMag = Mathf.Max(
            Mathf.Abs(transform.lossyScale.x),
            Mathf.Abs(transform.lossyScale.y),
            Mathf.Abs(transform.lossyScale.z));
        parentMag = Mathf.Max(parentMag, 1e-4f);
        float diameterWorld = 2f * rWorld * 1.18f;
        float childUniform = diameterWorld / parentMag;
        selectionHighlight3D.transform.localScale = Vector3.one * childUniform;
        selectionHighlight3D.SetActive(true);
        BillboardSelectionRingTowardMainCamera(selectionHighlight3D.transform, transform.position);
    }

    static void BillboardSelectionRingTowardMainCamera(Transform ring, Vector3 worldAnchor)
    {
        var cam = Camera.main;
        if (cam == null || ring == null) return;
        Vector3 toCam = cam.transform.position - worldAnchor;
        if (toCam.sqrMagnitude < 1e-10f) return;
        ring.position = worldAnchor;
        ring.rotation = Quaternion.LookRotation(toCam, cam.transform.up) * Quaternion.Euler(0f, 180f, 0f);
    }

    static bool TryScreenRayIntersectPlane(Camera cam, Vector2 screen, Vector3 planeNormal, Vector3 planePoint, out Vector3 hit)
    {
        hit = default;
        if (cam == null) return false;
        var ray = cam.ScreenPointToRay(screen);
        var plane = new Plane(planeNormal.normalized, planePoint);
        if (!plane.Raycast(ray, out float t)) return false;
        hit = ray.GetPoint(t);
        return true;
    }

    /// <summary>Project <paramref name="point"/> onto the plane (camera-facing “depth” sheet) through <paramref name="planePoint"/>.</summary>
    static Vector3 ProjectPointOntoViewDepthPlane(Vector3 point, Vector3 planePoint, Vector3 viewForward)
    {
        Vector3 n = viewForward.normalized;
        return point - n * Vector3.Dot(point - planePoint, n);
    }

    void SnapMoleculeToViewDepthPlane(Camera cam, Vector3 planeAnchor)
    {
        if (moleculeAtoms == null || cam == null) return;
        Vector3 n = cam.transform.forward;
        foreach (var a in moleculeAtoms)
        {
            if (a == null) continue;
            a.transform.position = ProjectPointOntoViewDepthPlane(a.transform.position, planeAnchor, n);
        }
    }

    static void ProjectMoleculeOntoPlane(HashSet<AtomFunction> atoms, Vector3 planePoint, Vector3 planeNormal)
    {
        if (atoms == null) return;
        Vector3 n = planeNormal.sqrMagnitude > 1e-10f ? planeNormal.normalized : Vector3.forward;
        foreach (var a in atoms)
        {
            if (a == null) continue;
            a.transform.position = ProjectPointOntoViewDepthPlane(a.transform.position, planePoint, n);
        }
    }

    void SnapMoleculeAfterDragStep(Camera cam)
    {
        if (moleculeAtoms == null || cam == null) return;
        var wp = MoleculeWorkPlane.Instance;
        if (wp != null)
            ProjectMoleculeOntoPlane(moleculeAtoms, wp.WorldPlanePoint, wp.WorldPlaneNormal);
        else
            SnapMoleculeToViewDepthPlane(cam, transform.position);
    }

    static Sprite CreateCircleSprite()
    {
        int size = 32;
        var tex = new Texture2D(size, size);
        tex.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - size * 0.5f) / (size * 0.5f);
                float dy = (y - size * 0.5f) / (size * 0.5f);
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                tex.SetPixel(x, y, r >= 0.85f && r <= 1f ? Color.white : Color.clear);
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f);
    }

    public bool HasAvailableLoneOrbitalForBond(ElectronOrbitalFunction excludeOrbital, int sourceElectronCount)
    {
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb == excludeOrbital) continue;
            if (orb.ElectronCount + sourceElectronCount == ElectronOrbitalFunction.MaxElectrons)
                return true;
        }
        return false;
    }

    public static AtomFunction FindBondPartner(AtomFunction sourceAtom, Vector3 tipPosition, ElectronOrbitalFunction sourceOrbital)
    {
        if (sourceAtom == null || sourceOrbital == null) return null;
        var cam = Camera.main;
        var atoms = Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None);
        AtomFunction best = null;
        float bestMetric = float.MaxValue;

        foreach (var a in atoms)
        {
            if (a == sourceAtom) continue;
            if (!a.HasAvailableLoneOrbitalForBond(sourceOrbital, sourceOrbital.ElectronCount)) continue;

            var orb = a.GetAvailableLoneOrbitalForBond(sourceOrbital, sourceOrbital.ElectronCount, tipPosition, cam);
            if (orb == null) continue;

            float metric;
            if (cam != null)
            {
                Vector3 st = cam.WorldToScreenPoint(tipPosition);
                Vector3 so = cam.WorldToScreenPoint(orb.transform.position);
                metric = st.z >= cam.nearClipPlane && so.z >= cam.nearClipPlane
                    ? ((Vector2)st - (Vector2)so).sqrMagnitude
                    : Vector3.SqrMagnitude(a.transform.position - tipPosition);
            }
            else
                metric = Vector3.SqrMagnitude(a.transform.position - tipPosition);

            if (metric < bestMetric)
            {
                bestMetric = metric;
                best = a;
            }
        }

        return best;
    }

    public void SetupIgnoreCollisions()
    {
        SetupGlobalIgnoreCollisions();
    }

    public static void SetupGlobalIgnoreCollisions()
    {
        var atoms = Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None);
        var orbitals = new List<ElectronOrbitalFunction>();
        var allElectrons = new List<ElectronFunction>();
        var atomColliders = new List<(Collider2D c2d, Collider c3d)>();

        foreach (var a in atoms)
        {
            var a2d = a.GetComponent<Collider2D>();
            var a3d = a.GetComponent<Collider>();
            if (a2d != null || a3d != null) atomColliders.Add((a2d, a3d));
            orbitals.AddRange(a.GetComponentsInChildren<ElectronOrbitalFunction>());
            foreach (var b in a.CovalentBonds)
                if (b?.Orbital != null && !orbitals.Contains(b.Orbital))
                    orbitals.Add(b.Orbital);
        }
        foreach (var orb in orbitals)
            allElectrons.AddRange(orb.GetComponentsInChildren<ElectronFunction>());

        foreach (var e in Object.FindObjectsByType<ElectronFunction>(FindObjectsSortMode.None))
        {
            if (e != null && !allElectrons.Contains(e))
                allElectrons.Add(e);
        }

        for (int i = 0; i < atomColliders.Count; i++)
            for (int j = i + 1; j < atomColliders.Count; j++)
                IgnoreCollisionPair(atomColliders[i], atomColliders[j]);
        foreach (var ac in atomColliders)
            foreach (var orb in orbitals)
                IgnoreCollision(ac, orb.GetComponent<Collider2D>(), orb.GetComponent<Collider>());
        foreach (var ac in atomColliders)
            foreach (var e in allElectrons)
                IgnoreCollision(ac, e.GetComponent<Collider2D>(), e.GetComponent<Collider>());
        for (int i = 0; i < orbitals.Count; i++)
        {
            for (int j = i + 1; j < orbitals.Count; j++)
                IgnoreCollision(orbitals[i], orbitals[j]);
            foreach (var e in allElectrons)
                IgnoreCollision(orbitals[i], e);
        }
        for (int i = 0; i < allElectrons.Count; i++)
            for (int j = i + 1; j < allElectrons.Count; j++)
                IgnoreCollision(allElectrons[i], allElectrons[j]);
    }

    static void IgnoreCollisionPair((Collider2D c2d, Collider c3d) a, (Collider2D c2d, Collider c3d) b)
    {
        if (a.c2d != null && b.c2d != null) Physics2D.IgnoreCollision(a.c2d, b.c2d);
        if (a.c3d != null && b.c3d != null) Physics.IgnoreCollision(a.c3d, b.c3d);
    }

    static void IgnoreCollision((Collider2D c2d, Collider c3d) a, Collider2D b2d, Collider b3d)
    {
        if (a.c2d != null && b2d != null) Physics2D.IgnoreCollision(a.c2d, b2d);
        if (a.c3d != null && b3d != null) Physics.IgnoreCollision(a.c3d, b3d);
    }

    static void IgnoreCollision(ElectronOrbitalFunction a, ElectronOrbitalFunction b)
    {
        var a2D = a.GetComponent<Collider2D>();
        var a3D = a.GetComponent<Collider>();
        var b2D = b.GetComponent<Collider2D>();
        var b3D = b.GetComponent<Collider>();
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    static void IgnoreCollision(ElectronOrbitalFunction orb, ElectronFunction e)
    {
        var o2D = orb.GetComponent<Collider2D>();
        var o3D = orb.GetComponent<Collider>();
        var e2D = e.GetComponent<Collider2D>();
        var e3D = e.GetComponent<Collider>();
        if (o2D != null && e2D != null) Physics2D.IgnoreCollision(o2D, e2D);
        if (o3D != null && e3D != null) Physics.IgnoreCollision(o3D, e3D);
    }

    static void IgnoreCollision(ElectronFunction a, ElectronFunction b)
    {
        var a2D = a.GetComponent<Collider2D>();
        var a3D = a.GetComponent<Collider>();
        var b2D = b.GetComponent<Collider2D>();
        var b3D = b.GetComponent<Collider>();
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    void Start()
    {
        EnsureCollider();
        InitializeFromAtomicNumber();
    }

    /// <summary>World-space radius of the atom body for placing labels in front of the mesh (not inside it).</summary>
    float GetAtomBodyRadiusWorld()
    {
        var sph = GetComponent<SphereCollider>();
        if (sph != null)
        {
            Vector3 ls = transform.lossyScale;
            float m = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
            return sph.radius * m;
        }
        var cap = GetComponent<CapsuleCollider>();
        if (cap != null)
        {
            Vector3 ls = transform.lossyScale;
            float rm = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z));
            float hm = Mathf.Abs(ls.y);
            return Mathf.Max(cap.radius * rm, cap.height * 0.5f * hm);
        }
        var sr = GetComponent<SpriteRenderer>();
        if (sr != null && sr.sprite != null && sr.enabled)
            return Mathf.Max(sr.bounds.extents.x, sr.bounds.extents.y);

        var mr = GetComponent<MeshRenderer>();
        if (mr != null)
            return Mathf.Max(mr.bounds.extents.x, mr.bounds.extents.y, mr.bounds.extents.z);
        return 0.42f;
    }

    void LateUpdate()
    {
        if (selectionHighlight3D != null && selectionHighlight3D.activeSelf)
            BillboardSelectionRingTowardMainCamera(selectionHighlight3D.transform, transform.position);

        if (elementLabelTransform == null)
            elementLabelTransform = transform.Find("ElementLabel");
        if (elementLabelTransform == null) return;
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 labelAnchor = transform.position;
        float bodyRworld = GetAtomBodyRadiusWorld();
        float radial = bodyRworld + elementLabelBeyondBodyMargin;

        if (cam.orthographic)
        {
            // Depth offset only along view axis (keeps label centered in XY); rotation matches perspective TMP convention.
            float offset = Mathf.Min(radial, cam.orthographicSize * 0.35f);
            Vector3 f = cam.transform.forward;
            if (f.sqrMagnitude < 1e-10f) return;
            f.Normalize();
            float along = Vector3.Dot(cam.transform.position - labelAnchor, f);
            float sign = Mathf.Abs(along) < 1e-4f ? -1f : Mathf.Sign(along);
            elementLabelTransform.position = labelAnchor + f * (sign * offset);

            Vector3 lookToCam = cam.transform.position - labelAnchor;
            if (lookToCam.sqrMagnitude < 1e-10f)
                lookToCam = -f;
            else
                lookToCam.Normalize();
            elementLabelTransform.rotation = Quaternion.LookRotation(lookToCam, cam.transform.up) * Quaternion.Euler(0f, 180f, 0f);
            return;
        }

        var toCam = cam.transform.position - labelAnchor;
        if (toCam.sqrMagnitude < 1e-10f) return;
        var toCamDir = toCam.normalized;
        float camDist = toCam.magnitude;
        float offsetPersp = Mathf.Min(radial, camDist * 0.42f);
        elementLabelTransform.position = labelAnchor + toCamDir * offsetPersp;
        // TMP world quads are authored with the visible side opposite default LookRotation(toCam).
        elementLabelTransform.rotation = Quaternion.LookRotation(toCam, cam.transform.up) * Quaternion.Euler(0f, 180f, 0f);
    }

    void EnsureCollider()
    {
        if (GetComponent<Collider>() != null || GetComponent<Collider2D>() != null) return;
        var col = gameObject.AddComponent<BoxCollider>();
        col.size = new Vector3(1f, 1f, 0.1f);
        col.isTrigger = true;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        var editMode = FindFirstObjectByType<EditModeManager>();
        if (editMode != null && editMode.EraserMode)
        {
            if (editMode.TryEraseAtomIfChainEnd(this))
                return;
            return;
        }

        isBeingHeld = true;
        moleculeAtoms = GetConnectedMolecule();

        var cam = Camera.main;
        var wp = MoleculeWorkPlane.Instance;
        // Lone atom: project onto sheet / view depth. Bonded: preserve 3D geometry — only translate the whole molecule
        // rigidly so the grabbed atom lies on the work plane (partners are not individually projected).
        if (cam != null && moleculeAtoms != null)
        {
            if (moleculeAtoms.Count == 1)
            {
                if (wp != null)
                    ProjectMoleculeOntoPlane(moleculeAtoms, wp.WorldPlanePoint, wp.WorldPlaneNormal);
                else
                    SnapMoleculeToViewDepthPlane(cam, transform.position);
            }
            else if (wp != null)
            {
                Vector3 n = wp.WorldPlaneNormal.sqrMagnitude > 1e-10f ? wp.WorldPlaneNormal.normalized : Vector3.forward;
                Vector3 projectedLead = ProjectPointOntoViewDepthPlane(transform.position, wp.WorldPlanePoint, n);
                Vector3 rigid = projectedLead - transform.position;
                if (rigid.sqrMagnitude > 1e-12f)
                {
                    foreach (var a in moleculeAtoms)
                        if (a != null) a.transform.position += rigid;
                }
            }
        }

        atomDragPlaneValid = false;
        if (cam != null && wp != null && wp.TryGetWorldPoint(cam, eventData.position, out var workHit))
        {
            atomDragPlaneNormal = wp.WorldPlaneNormal;
            atomDragPlanePoint = wp.WorldPlanePoint;
            atomDragPlaneValid = true;
            dragOffset = transform.position - workHit;
        }
        else if (cam != null && TryScreenRayIntersectPlane(cam, eventData.position, cam.transform.forward, transform.position, out var planeHit))
        {
            atomDragPlaneNormal = cam.transform.forward;
            atomDragPlanePoint = transform.position;
            atomDragPlaneValid = true;
            dragOffset = transform.position - planeHit;
        }
        else
        {
            dragOffset = transform.position - PlanarPointerInteraction.ScreenToWorldPoint(eventData.position);
        }

        if (editMode != null)
            editMode.OnAtomClicked(this);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!isBeingHeld) return;

        Vector3 newPos;
        var cam = Camera.main;
        var wp = MoleculeWorkPlane.Instance;
        Vector3 hit = default;
        bool haveHit = false;
        if (cam != null && wp != null && wp.TryGetWorldPoint(cam, eventData.position, out hit))
            haveHit = true;
        else if (atomDragPlaneValid && cam != null &&
                 TryScreenRayIntersectPlane(cam, eventData.position, atomDragPlaneNormal, atomDragPlanePoint, out hit))
            haveHit = true;

        if (haveHit)
            newPos = hit + dragOffset;
        else
            newPos = PlanarPointerInteraction.ScreenToWorldPoint(eventData.position) + dragOffset;

        var delta = newPos - transform.position;
        // Move grabbed atom first; rest of molecule follows by the same rigid offset (preserves relative layout).
        transform.position = newPos;
        if (moleculeAtoms != null)
        {
            foreach (var a in moleculeAtoms)
            {
                if (a != this && a != null)
                    a.transform.position += delta;
            }
            // Only flatten a single atom onto the plane each step; bonded molecules stay rigid (same translation only).
            if (Camera.main != null && moleculeAtoms.Count == 1)
                SnapMoleculeAfterDragStep(Camera.main);
        }

        if (haveHit)
            dragOffset = transform.position - hit;
    }

    public HashSet<AtomFunction> GetConnectedMolecule()
    {
        var set = new HashSet<AtomFunction>();
        var queue = new Queue<AtomFunction>();
        queue.Enqueue(this);
        set.Add(this);
        while (queue.Count > 0)
        {
            var a = queue.Dequeue();
            foreach (var b in a.CovalentBonds)
            {
                if (b == null) continue;
                var other = b.AtomA == a ? b.AtomB : b.AtomA;
                if (other != null && set.Add(other))
                    queue.Enqueue(other);
            }
        }
        return set;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (isBeingHeld && moleculeAtoms != null)
        {
            var disposal = FindFirstObjectByType<DisposalZone>();
            if (disposal != null && disposal.ContainsScreenPoint(eventData.position))
            {
                disposal.DestroyMolecule(moleculeAtoms);
                moleculeAtoms = null;
            }
        }
        isBeingHeld = false;
        atomDragPlaneValid = false;
    }

    bool initialized;

    /// <summary>Call after setting AtomicNumber when bonds need to be formed immediately (e.g. MoleculeBuilder). Normally Start handles this.</summary>
    public void ForceInitialize() => InitializeFromAtomicNumber();

    void InitializeFromAtomicNumber()
    {
        if (initialized) return;
        initialized = true;
        int valence = GetValenceFromGroup(GetGroupFromAtomicNumber(atomicNumber));
        int valenceElectrons = Mathf.Max(0, valence - charge);
        if (atomicNumber == 2) valenceElectrons = 2; // He: only 1s²
        CreateOrbitalsWithValence(valenceElectrons);
        ApplyAtomBodyTint();
        CreateElementLabel();
        RefreshCharge();
    }

    static readonly int ShaderPropBaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ShaderPropColor = Shader.PropertyToID("_Color");
    static readonly int ShaderPropWhite = Shader.PropertyToID("_White");
    static readonly int ShaderPropRendererColor = Shader.PropertyToID("_RendererColor");

    static void SetMaterialMainTint(Material mat, Color c)
    {
        if (mat == null) return;
        if (mat.HasProperty(ShaderPropBaseColor)) mat.SetColor(ShaderPropBaseColor, c);
        else if (mat.HasProperty(ShaderPropWhite)) mat.SetColor(ShaderPropWhite, c);
        else if (mat.HasProperty(ShaderPropRendererColor)) mat.SetColor(ShaderPropRendererColor, c);
        else if (mat.HasProperty(ShaderPropColor)) mat.SetColor(ShaderPropColor, c);
    }

    static Color ContrastingLabelColor(Color opaqueElementColor)
    {
        float lum = 0.299f * opaqueElementColor.r + 0.587f * opaqueElementColor.g + 0.114f * opaqueElementColor.b;
        return lum > 0.55f ? new Color(0.12f, 0.12f, 0.18f, 1f) : Color.white;
    }

    void ApplyAtomBodyTint()
    {
        Color baseElem = PeriodicTableUI.GetAtomSphereColor(atomicNumber);
        Color body = baseElem;
        body.a = atomBodyAlpha;

        var sr = GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            sr.color = body;
            return;
        }

        var mr = GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var mat = mr.material;
            SetMaterialMainTint(mat, body);
        }
    }

    [SerializeField] Vector2 chargeLabelOffset = new Vector2(0.5f, 0.33f); // Fraction of elementLabelSize (0.5 = right/top edge)
    [SerializeField] Vector2 elementLabelSize = new Vector2(1f, 1f);
    [SerializeField] Vector2 chargeLabelSize = new Vector2(0.5f, 0.5f);
    [Tooltip("World units beyond the atom body radius along the view ray so element + charge TMP sit outside the sphere.")]
    [SerializeField] float elementLabelBeyondBodyMargin = 0.035f;

    public static TMP_FontAsset GetDefaultFont()
    {
        var font = TMP_Settings.defaultFontAsset;
        if (font != null)
            return font;
        font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (font != null)
            return font;
        return Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF - Fallback");
    }

    void CreateElementLabel()
    {
        var labelObj = new GameObject("ElementLabel");
        labelObj.transform.SetParent(transform);
        labelObj.transform.localPosition = Vector3.zero;
        labelObj.transform.localRotation = Quaternion.identity;
        labelObj.transform.localScale = Vector3.one;
        var tmp = labelObj.AddComponent<TextMeshPro>();
        tmp.font = GetDefaultFont();
        tmp.text = GetElementSymbol(atomicNumber);
        tmp.fontSize = 6f;
        tmp.alignment = TextAlignmentOptions.Center;
        if (tmp.rectTransform != null)
        {
            tmp.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            tmp.rectTransform.anchorMin = tmp.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            tmp.rectTransform.sizeDelta = elementLabelSize;
        }
        var labelFg = atomicNumber == 1
            ? Color.white
            : ContrastingLabelColor(PeriodicTableUI.GetAtomSphereColor(atomicNumber));
        tmp.color = labelFg;

        var chargeObj = new GameObject("ChargeLabel");
        chargeObj.transform.SetParent(labelObj.transform);
        chargeObj.transform.localPosition = new Vector3(
            elementLabelSize.x * chargeLabelOffset.x,
            elementLabelSize.y * chargeLabelOffset.y,
            0f);
        chargeObj.transform.localRotation = Quaternion.identity;
        chargeObj.transform.localScale = Vector3.one;
        var chargeTmp = chargeObj.AddComponent<TextMeshPro>();
        chargeTmp.font = GetDefaultFont();
        chargeTmp.fontSize = 4f;
        if (chargeTmp.rectTransform != null)
            chargeTmp.rectTransform.sizeDelta = chargeLabelSize;
        chargeTmp.alignment = TextAlignmentOptions.BottomLeft;
        chargeTmp.color = labelFg;
        RefreshChargeLabel();
        elementLabelTransform = labelObj.transform;
    }

    public static string GetElementSymbol(int z)
    {
        if (z <= 0 || z > ElementSymbols.Length) return "?";
        return ElementSymbols[z - 1];
    }

    static readonly string[] ElementSymbols =
    {
        "H", "He", "Li", "Be", "B", "C", "N", "O", "F", "Ne", "Na", "Mg", "Al", "Si", "P", "S", "Cl", "Ar",
        "K", "Ca", "Sc", "Ti", "V", "Cr", "Mn", "Fe", "Co", "Ni", "Cu", "Zn", "Ga", "Ge", "As", "Se", "Br", "Kr",
        "Rb", "Sr", "Y", "Zr", "Nb", "Mo", "Tc", "Ru", "Rh", "Pd", "Ag", "Cd", "In", "Sn", "Sb", "Te", "I", "Xe",
        "Cs", "Ba", "La", "Ce", "Pr", "Nd", "Pm", "Sm", "Eu", "Gd", "Tb", "Dy", "Ho", "Er", "Tm", "Yb", "Lu",
        "Hf", "Ta", "W", "Re", "Os", "Ir", "Pt", "Au", "Hg", "Tl", "Pb", "Bi", "Po", "At", "Rn",
        "Fr", "Ra", "Ac", "Th", "Pa", "U", "Np", "Pu", "Am", "Cm", "Bk", "Cf", "Es", "Fm", "Md", "No", "Lr",
        "Rf", "Db", "Sg", "Bh", "Hs", "Mt", "Ds", "Rg", "Cn", "Nh", "Fl", "Mc", "Lv", "Ts", "Og"
    };

    // Group 1-18 by atomic number. Lanthanides (57-71) and actinides (89-103) = group 3.
    static readonly int[] GroupByAtomicNumber =
    {
        0, 1, 18, 1, 2, 13, 14, 15, 16, 17, 18, 1, 2, 13, 14, 15, 16, 17, 18,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        1, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        1, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18
    };

    static int GetGroupFromAtomicNumber(int z)
    {
        if (z <= 0 || z > 118) return 1;
        return GroupByAtomicNumber[z];
    }

    static int GetValenceFromGroup(int group) => group switch
    {
        1 => 1, 2 => 2, 13 => 3, 14 => 4, 15 => 5, 16 => 6, 17 => 7, 18 => 8,
        _ => Mathf.Min(group, 8)
    };

    void CreateOrbitalsWithValence(int valence)
    {
        if (orbitalPrefab == null) return;
        int orbitalCount = GetOrbitalSlotCount();
        float[] angles = GetSlotAnglesForCount(orbitalCount);
        float offset = bondRadius * 0.6f;

        // Distribute electrons: prefer 1 per orbital first (for sigma bonding), then fill to 2
        int[] electronsPerOrbital = new int[orbitalCount];
        int remaining = valence;
        for (int i = 0; i < orbitalCount && remaining > 0; i++)
        {
            electronsPerOrbital[i] = Mathf.Min(1, remaining);
            remaining -= electronsPerOrbital[i];
        }
        for (int i = 0; i < orbitalCount && remaining > 0; i++)
        {
            int add = Mathf.Min(ElectronOrbitalFunction.MaxElectrons - electronsPerOrbital[i], remaining);
            electronsPerOrbital[i] += add;
            remaining -= add;
        }

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            var dirs = VseprLayout.GetIdealLocalDirections(orbitalCount);
            for (int i = 0; i < orbitalCount; i++)
            {
                var orbital = Instantiate(orbitalPrefab, transform);
                var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(dirs[i], bondRadius);
                orbital.transform.localPosition = pos;
                orbital.transform.localScale = Vector3.one * 0.6f;
                orbital.transform.localRotation = rot;
                orbital.ElectronCount = electronsPerOrbital[i];
                orbital.SetBondedAtom(this);
                BondOrbital(orbital);
            }
        }
        else
        {
            for (int i = 0; i < orbitalCount; i++)
            {
                var orbital = Instantiate(orbitalPrefab, transform);
                float angleDeg = angles[i];
                float rad = angleDeg * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f);
                orbital.transform.localPosition = dir * offset;
                orbital.transform.localScale = Vector3.one * 0.6f;
                orbital.transform.localRotation = Quaternion.Euler(0, 0, angleDeg);
                orbital.ElectronCount = electronsPerOrbital[i];
                orbital.SetBondedAtom(this);
                BondOrbital(orbital);
            }
        }
        SetupIgnoreCollisions();
    }
}
