using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;

/// <summary>How to derive the int shown on <c>ChargeLabel</c>: Lewis formal (octet) vs oxidation state (Pauling EN).</summary>
public enum AtomChargeDisplayMode
{
    OctetFormal,
    OxidationState
}

public class AtomFunction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] float bondRadius = 0.8f;
    [SerializeField] int atomicNumber = 1;
    [SerializeField] int charge;
    [SerializeField] [Range(0.05f, 1f)] float atomBodyAlpha = 0.42f;
    [SerializeField] ElectronOrbitalFunction orbitalPrefab;
    readonly List<ElectronOrbitalFunction> bondedOrbitals = new List<ElectronOrbitalFunction>();
    readonly List<CovalentBond> covalentBonds = new List<CovalentBond>();

    /// <summary>
    /// Guide-group layout: for multiply-bond guide pairs (tiers 1–4), lists <strong>every</strong> hosting orbital on that pair so joint rigid apply skips the whole cluster; ex-bond-only tiers list the single representative. Set in <see cref="TryBuildRedistributeTargets3DGuideGroupPrefix"/>; cleared at <see cref="GetRedistributeTargets3D"/> entry and in <see cref="ApplyRedistributeTargets"/> finally.
    /// </summary>
    HashSet<ElectronOrbitalFunction> orbitalsExcludedFromJointRigidInApplyRedistributeTargets;

    /// <summary>Log π-trigonal guide-group refine, hemisphere flip, and tip-vs-target angles (e.g. O-C-O → O=C). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogPiTrigonalOcoConformation = true;

    /// <summary>NDJSON to <c>.cursor/debug-214323.log</c> for second π on terminal O (O=C=O). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogOcoSecondPiNdjson = true;

    /// <summary>π step 2: oxygen nucleus shell — tips not in redist rows vs operation π (O=C=C off-op lone joint). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogOcoOffOpNucleusLoneAngles = true;

    /// <summary>Logs azimuth template refinement for permutation cost in 3D. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogTemplateTwistPermCost = true;

    /// <summary>Append one NDJSON line per permutation cost snapshot to .cursor/debug-d66405.log for A/B success vs failure runs. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogPermCostInvariantNdjson = true;

    /// <summary>When true, always apply the grid-best azimuth to template dirs (even if improvement is below the usual 0.05 threshold) so rigid copies converge to the same minimum.</summary>
    public static bool DebugPermCostAlwaysApplyAzimuthMinimum = false;

    const string PermCostInvariantNdjsonPath = "/Users/minseo/Documents/Github/_star14ms/OrgoSimulator/.cursor/debug-d66405.log";

    /// <summary>Session id for permutation-cost invariant NDJSON lines (debug ingest).</summary>
    public static string DebugPermCostNdjsonSessionId = "d66405";

    /// <summary>π snap + hybrid: NDJSON world tip vs mesh +X and screen probe. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogOrbitalVisualTipProbe = true;

    /// <summary>σ/π animated formation: NDJSON per-atom electron inventory for all atoms in the union of both fragments. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogMoleculeElectronConfigAroundBond = true;

    /// <summary>Oxygen-only: lone-domain and σ/π-to-carbon counts vs neutral terminal O in O=C=O. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogOxygenOcoLewisDetail = true;

    /// <summary>Mol-ecn snapshot: oxygen lobe +X directions in nucleus space, pairwise angle stats vs 109.5° and 120°. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogOxygenMolEcnOrbitalAngles = true;

    /// <summary>Formation-style VSEPR TryMatch: raw vs predictive lone-domain and σ-axis counts. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogVseprDomainPredict = true;

    static int _molEcnEventSeq;

    /// <summary>Pair before/after <see cref="LogMoleculeElectronConfigurationFromAtomUnion"/> snapshots in ingest logs via data.bondEventId.</summary>
    public static int AllocateMoleculeEcnEventId() => ++_molEcnEventSeq;

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
    public int NonBondEmptyOrbitalCount => bondedOrbitals.Count(o => o != null && o.Bond == null && o.ElectronCount == 0);
    public int AtomicNumber { get => atomicNumber; set => atomicNumber = Mathf.Clamp(value, 1, 118); }
    public int Charge { get => charge; set { charge = value; RefreshChargeLabel(); } }

    /// <summary>Whether charge labels use Lewis formal charge (half bonds) or oxidation state (electronegativity). Default: formal/octet.</summary>
    public static AtomChargeDisplayMode ChargeDisplayMode = AtomChargeDisplayMode.OctetFormal;

    /// <summary>Geometric ideal ∠(domain, domain) for a regular tetrahedron: <c>arccos(-⅓)</c>.</summary>
    public const float TetrahedralInterdomainAngleDeg = 109.47122f;

    public static string FormatAtomBrief(AtomFunction a) =>
        a == null ? "null" : $"id={a.GetInstanceID()} Z={a.AtomicNumber} name={a.name}";

    public static string FormatPinSummary(HashSet<AtomFunction> pin, int maxList = 14)
    {
        if (pin == null) return "pin=null";
        if (pin.Count == 0) return "pin=∅";
        var parts = new List<string>(maxList + 1);
        int n = 0;
        foreach (var a in pin)
        {
            if (a == null) continue;
            parts.Add(FormatAtomBrief(a));
            if (++n >= maxList)
            {
                parts.Add("…");
                break;
            }
        }
        return $"pin[count={pin.Count}] " + string.Join("; ", parts);
    }

    static void LogVsepr3D(string _) { }

    void LogReplaceHRedistributeOrbitalSnapshot(string _) { }

    void LogTetrahedralElectronDomainAngleDiagnostic(string _, List<(Vector3 bondAxisLocal, Vector3 idealLocal)> __) { }

    public static void LogTetrahedralDomainAnglesLine(string _) { }

    public static void LogReplaceHRedistribute(string _) { }

    public static void LogVseprLoneLobeMotionLine(string _) { }

    public static void LogBondBreakRedistributeFlowLine(string _) { }

    public static void LogFrameworkPinSigmaRelax(string _) { }

    /// <summary>Legacy triage toggles; kept for call sites; logs are no-ops above.</summary>
    public static bool DebugLogVseprRedistribute3D = false;
    public static bool DebugLogReplaceHRedistribute = false;
    public static bool DebugLogTetrahedralDomainAngles = false;
    public static bool DebugLogVseprLoneLobeMotion = false;
    public static bool DebugLogBondBreakRedistributeFlow = false;
    public static bool DebugLogFrameworkPinSigmaRelaxTrace = false;
    public static bool DebugLogCcBondBreakGeometry = false;
    public static bool DebugLogCcBondBreakSpreadTrace = false;
    public static bool DebugLogCarbocationTrigonalPlanarityDiag = false;
    public static bool DebugLogCarbocationSigmaRedistributionTrace = false;
    public static bool DebugLogCarbonBreakIdealFrameTrace = false;
    public static bool DebugLogCcBondBreakUnifiedSparseDiag = false;
    public static bool DebugLogBondBreakEmptyTeleport = true;

    /// <summary>When true (default), <see cref="GetRedistributeTargets3D"/> uses only repulsion + guide-plane layout (<see cref="GetRedistributeTargets3DRepulsionLayoutOnly"/>). Set false for VSEPR <c>TryMatch</c> via <see cref="GetRedistributeTargets3DVseprTryMatch"/>.</summary>
    public static bool UseRepulsionLayoutOnlyInGetRedistributeTargets3D = true;

    /// <summary>Summary when <see cref="SnapHydrogenSigmaNeighborsToBondOrbitalAxes"/> moves σ-H (common on CH₄ 4th H: all C–H hybrids refresh). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogHydrogenSigmaSnap = false;
    /// <summary>Trigonal-planar σ-relax diagnostics for π centers (e.g. C(=O)-H, NO3-like N). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogTrigonalPlanarSigmaRelax = true;

    /// <summary>
    /// After <see cref="RedistributeOrbitals3D"/>: log electron-domain directions, pairwise angles, and how each direction is defined (σ axes vs lobe tips). Skips Z=1. Default on for triage; set false for quiet runs.
    /// </summary>
    public static bool DebugLogRedistributeOrbitals3DBondAngles = true;

    /// <summary>
    /// Bond-parented σ targets after <see cref="ApplyRedistributeTargets"/>: pivot-at-caller rigid motion + <see cref="CovalentBond.ApplySigmaOrbitalTipFromRedistribution"/>. Default on for triage; set false for quiet runs.
    /// </summary>
    public static bool DebugLogRedistribute3DApplyBondParented = true;

    /// <summary>
    /// Per-tip alignment vs redistribute tuple (joint input, σ pass before/after bond refresh, optional post-ApplySigma). Default on for triage; set false for quiet runs.
    /// </summary>
    public static bool DebugLogRedistribute3DTipGapTrace = true;

    /// <summary>
    /// σ-substituent rigid-follow during redistribution lerp: start, quarter progress, commit s=1, after Apply. Default on for triage; set false for quiet runs.
    /// </summary>
    public static bool DebugLogJointFragRedistMilestones = true;

    /// <summary>
    /// Log every animation frame for joint fragment motion; default off due to console flood risk.
    /// </summary>
    public static bool DebugLogJointFragRedistEveryFrame = false;

    /// <summary>
    /// Same σ + lone tip directions in world space as <see cref="LogTetrahedralElectronDomainAngleDiagnostic"/> (visual row).
    /// </summary>
    bool TryGetFourElectronDomainDirectionsWorldNormalized(List<Vector3> dirsWorldOut, List<Vector3> loneTipsWorldOnly)
    {
        dirsWorldOut.Clear();
        loneTipsWorldOnly?.Clear();
        var loneTemp = new List<Vector3>(6);
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount <= 0) continue;
            Vector3 w = transform.TransformDirection(OrbitalTipLocalDirection(orb)).normalized;
            loneTemp.Add(w);
            loneTipsWorldOnly?.Add(w);
        }
        var seenN = new HashSet<AtomFunction>();
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null || !b.IsSigmaBondLine()) continue;
            AtomFunction other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (other == null || !seenN.Add(other)) continue;
            if (b.Orbital != null)
            {
                Vector3 local = OrbitalTipDirectionInNucleusLocal(b.Orbital);
                dirsWorldOut.Add(transform.TransformDirection(local).normalized);
            }
            else
            {
                Vector3 w = other.transform.position - transform.position;
                if (w.sqrMagnitude > 1e-12f)
                    dirsWorldOut.Add(w.normalized);
            }
        }
        dirsWorldOut.AddRange(loneTemp);
        return dirsWorldOut.Count == 4;
    }

    /// <summary>True when pairwise spread/mean match tet diagnostic “visual≈tetra” (same vectors as <c>[tetra-domain]</c>).</summary>
    bool FourDomainsVisualElectronGeometryApproximatelyTetrahedral(float spreadTol, float meanTol)
    {
        var dirs = new List<Vector3>(8);
        if (!TryGetFourElectronDomainDirectionsWorldNormalized(dirs, loneTipsWorldOnly: null)) return false;
        TetraDomainPairwiseAngleStats(dirs, out float vmin, out _, out float vmean, out float vspread, out _);
        float vdev = Mathf.Abs(vmean - TetrahedralInterdomainAngleDeg);
        if (vmin < 55f) return false;
        return vspread <= spreadTol && vdev <= meanTol;
    }

    /// <summary>Max angle (°) between σ hybrid tips and internuclear axes; 0 when no σ orbitals to compare.</summary>
    float SigmaTipsVsBondAxesMaxAngleDeg()
    {
        float maxA = 0f;
        var seenN = new HashSet<AtomFunction>();
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null || !b.IsSigmaBondLine()) continue;
            AtomFunction other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (other == null || !seenN.Add(other)) continue;
            Vector3 axisW = other.transform.position - transform.position;
            if (axisW.sqrMagnitude < 1e-12f) continue;
            axisW.Normalize();
            if (b.Orbital != null)
            {
                Vector3 tipW = transform.TransformDirection(OrbitalTipDirectionInNucleusLocal(b.Orbital).normalized);
                maxA = Mathf.Max(maxA, Vector3.Angle(tipW, axisW));
            }
        }
        return maxA;
    }

    /// <summary>
    /// Bond-break triage: nuclear σ-framework vs σ-tip+lone electron-domain tetra stats (matches <see cref="RedistributeOrbitals3DOld"/> visual-tet checks).
    /// Gated by <see cref="CovalentBond.DebugLogBondBreakTetraFramework"/>.
    /// </summary>
    public void LogBondBreakTetraFrameworkSnapshot(string phase)
    {
        if (!CovalentBond.DebugLogBondBreakTetraFramework) return;

        int sigmaN = GetDistinctSigmaNeighborCount();
        int pi = GetPiBondCount();
        int slots = GetOrbitalSlotCount();
        float sigmaTipAxMax = SigmaTipsVsBondAxesMaxAngleDeg();
        bool tipsAligned24 = SigmaTipsAlignedToBondAxes(24f);

        var nuclearAxes = new List<Vector3>(8);
        foreach (var n in GetDistinctSigmaNeighborAtoms())
        {
            if (n == null) continue;
            Vector3 d = n.transform.position - transform.position;
            if (d.sqrMagnitude > 1e-12f) nuclearAxes.Add(d.normalized);
        }

        string nuclearPart;
        if (nuclearAxes.Count >= 2)
        {
            TetraDomainPairwiseAngleStats(nuclearAxes, out float nmin, out float nmax, out float nmean, out float nspread, out float ndev);
            nuclearPart =
                $"nuclearσAxes={nuclearAxes.Count} ∠pair min={nmin:F2} max={nmax:F2} mean={nmean:F2} spread={nspread:F2} |mean-109.5|={ndev:F2}";
        }
        else
            nuclearPart = $"nuclearσAxes={nuclearAxes.Count}";

        var dirs = new List<Vector3>(8);
        bool fourElectronDomains = TryGetFourElectronDomainDirectionsWorldNormalized(dirs, loneTipsWorldOnly: null);

        const float visualSpreadTol = 8f;
        const float visualMeanTol = 6f;
        string electronPart;
        if (fourElectronDomains)
        {
            TetraDomainPairwiseAngleStats(dirs, out float emin, out float emax, out float emean, out float espread, out float edev);
            bool approxTet = FourDomainsVisualElectronGeometryApproximatelyTetrahedral(visualSpreadTol, visualMeanTol);
            electronPart =
                $"electronDomains=4 ∠pair min={emin:F2} max={emax:F2} mean={emean:F2} spread={espread:F2} |mean-109.5|={edev:F2} " +
                $"visualTet({visualSpreadTol},{visualMeanTol})={approxTet} σTipVsAxisMax={sigmaTipAxMax:F2}° tipsAligned≤24°={tipsAligned24}";
        }
        else
            electronPart =
                $"electronDomainDirs={dirs.Count} (need 4 for full tetra stats) σTipVsAxisMax={sigmaTipAxMax:F2}° tipsAligned≤24°={tipsAligned24}";

        string msg =
            "[break-tetra] " + phase + " atom=" + name + "(Z=" + atomicNumber + ") id=" + GetInstanceID() +
            " σN=" + sigmaN + " π=" + pi + " slots=" + slots + " | " + nuclearPart + " || " + electronPart;
        Debug.Log(msg);
        ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
    }

    /// <summary>Hybrid σ tips already track internuclear axes (skip path safe for identity Σ sync).</summary>
    bool SigmaTipsAlignedToBondAxes(float maxDeg)
    {
        var seenN = new HashSet<AtomFunction>();
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null || !b.IsSigmaBondLine()) continue;
            AtomFunction other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (other == null || !seenN.Add(other)) continue;
            Vector3 axisW = (other.transform.position - transform.position);
            if (axisW.sqrMagnitude < 1e-12f) continue;
            axisW.Normalize();
            if (b.Orbital != null)
            {
                Vector3 tipW = transform.TransformDirection(OrbitalTipDirectionInNucleusLocal(b.Orbital).normalized);
                if (Vector3.Angle(tipW, axisW) > maxDeg) return false;
            }
        }
        return true;
    }

    static void TetraDomainPairwiseAngleStats(IReadOnlyList<Vector3> dirs, out float minAng, out float maxAng, out float mean, out float spread, out float meanDevFrom109)
    {
        const float ideal = TetrahedralInterdomainAngleDeg;
        minAng = 360f;
        maxAng = 0f;
        mean = 0f;
        spread = 0f;
        meanDevFrom109 = 0f;
        int n = dirs?.Count ?? 0;
        if (n < 2) return;
        float sum = 0f;
        int pcount = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                float ang = Vector3.Angle(dirs[i], dirs[j]);
                minAng = Mathf.Min(minAng, ang);
                maxAng = Mathf.Max(maxAng, ang);
                sum += ang;
                pcount++;
            }
        }
        mean = pcount > 0 ? sum / pcount : 0f;
        spread = maxAng - minAng;
        meanDevFrom109 = Mathf.Abs(mean - ideal);
    }

    public static void LogCarbocationSigmaRedistributionTrace(string _) { }

    static void LogCarbonBreakIdealFrameTrace(string _) { }

    public static void LogBondBreakEmptyTeleportLine(string _) { }

    public void LogEmptyNonbondSnapshotForBondBreak(string _, Vector3? __, ElectronOrbitalFunction ___) { }

    static void LogCcBondBreak(string _) { }

    static void LogCarbocationPlanarityDiagLine(string _) { }

    static void LogCcBondBreakTrace(string _) { }

    static void LogCcBondBreakUnified(string _) { }

    static void LogCcBondBreakUnifiedDiag(string _) { }

    static string FormatLocalDir3(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

    void LogUnifiedSparseTipSnapshot(string _, List<ElectronOrbitalFunction> __) { }

    void LogUnifiedSparseMinTipSeparation(string _, List<ElectronOrbitalFunction> __) { }

    public void LogCcBondBreakGeometryDiagnostics(string _) { }

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

        // Period 3+ group 15: max five valence / hypervalent slots (e.g. PCl₅, phosphate framework); not six.
        if (atomicNumber > 10 && group == 15)
            return 5;

        // Period 3+ group 16: six slots for SF₆, SO₄²⁻, etc.
        if (atomicNumber > 10 && group == 16)
            return 6;

        // Period 2 N/O stay four slots; lanthanides/actinides use default four unless explicitly extended later.
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

    /// <summary>Displayed charge: <see cref="ChargeDisplayMode"/> selects Lewis formal (octet) vs oxidation state.</summary>
    public int ComputeCharge()
    {
        return ChargeDisplayMode == AtomChargeDisplayMode.OxidationState
            ? ComputeOxidationStateCharge()
            : ComputeOctetFormalCharge();
    }

    /// <summary>Lewis formal charge: FC = V − lone pairs − ½ bonding e⁻ (half of each bond’s electrons).</summary>
    public int ComputeOctetFormalCharge()
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
            electronsOwned += b.GetFormalChargeElectronsOwnedBy(this);
        }

        return valence - electronsOwned;
    }

    /// <summary>Oxidation state: bonding electrons assigned to the more electronegative atom; equal EN splits 50-50 (odd count uses orbital contributor).</summary>
    public int ComputeOxidationStateCharge()
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

    public static void RefreshAllDisplayedCharges()
    {
        foreach (var atom in Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None))
            atom.RefreshCharge();
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

    /// <summary>Append current local targets for all 0e non-bond orbitals on this nucleus.</summary>
    public void AppendCurrentEmptyNonbondOrbitalTargets(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> dst)
    {
        if (dst == null) return;
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null || o.ElectronCount != 0) continue;
            if (o.transform.parent != transform) continue;
            dst.Add((o, o.transform.localPosition, o.transform.localRotation));
        }
    }

    /// <summary>Append current local targets for all non-bond orbitals on this nucleus (occupied + empty).</summary>
    public void AppendCurrentNonbondOrbitalTargets(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> dst)
    {
        if (dst == null) return;
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null) continue;
            if (o.transform.parent != transform) continue;
            dst.Add((o, o.transform.localPosition, o.transform.localRotation));
        }
    }

    /// <summary>Compute the perpendicular empty-slot target for a specific guide orbital using current framework dirs. Other 0e non-bond lobes are passed as <c>avoid</c> so the chosen hemisphere (±perp) does not stack on an existing empty.</summary>
    public bool TryGetPerpendicularEmptyTargetForGuide(ElectronOrbitalFunction guideOrbital, out Vector3 localPos, out Quaternion localRot)
    {
        localPos = Vector3.zero;
        localRot = Quaternion.identity;
        if (guideOrbital == null || guideOrbital.transform.parent != transform) return false;

        var dirs = new List<Vector3>();
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o == guideOrbital || o.Bond != null || o.ElectronCount <= 0) continue;
            dirs.Add(OrbitalTipLocalDirection(o).normalized);
        }
        if (!TryAppendCarbocationFrameworkSigmaLobeTips(dirs, guideOrbital, null))
            AppendSigmaBondDirectionsLocalDistinctNeighbors(dirs);
        if (dirs.Count == 0) return false;

        List<Vector3> avoidOtherEmptyTips = null;
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o == guideOrbital || o.Bond != null || o.ElectronCount != 0) continue;
            Vector3 t = OrbitalTipLocalDirection(o);
            if (t.sqrMagnitude < 1e-14f) continue;
            avoidOtherEmptyTips ??= new List<Vector3>(2);
            avoidOtherEmptyTips.Add(t.normalized);
        }

        if (!TryComputePerpendicularEmptySlotFromFrameworkDirs(
                dirs, bondRadius, out var pos, out var rot, null,
                avoidOtherEmptyTips != null && avoidOtherEmptyTips.Count > 0 ? avoidOtherEmptyTips : null))
            return false;
        localPos = pos;
        localRot = rot;
        return true;
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

    /// <summary>Non-bond orbitals on this nucleus with at least one electron (each counts as a VSEPR lone region in the slot model).</summary>
    public int CountOccupiedNonbondOrbitals()
    {
        int n = 0;
        foreach (var o in bondedOrbitals)
            if (o != null && o.Bond == null && o.ElectronCount > 0) n++;
        return n;
    }

    /// <summary>Re-syncs electron sphere/pair placement on every bonded orbital (after VSEPR or bond-break animation).</summary>
    public void RefreshElectronSyncOnBondedOrbitals()
    {
        foreach (var orb in bondedOrbitals)
        {
            // UnityEngine.Object: do not use ?. — destroyed orbitals must be skipped with an explicit null check before any call.
            if (orb == null) continue;
            orb.RefreshElectronSyncAfterLayout();
        }
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
    /// <param name="freezeSigmaNeighborSubtreeRoot">When set (e.g. functional-group attach), σ-relax emits no nuclear targets for this σ neighbor or its subtree — avoids O(N) over the substrate and keeps the parent framework fixed.</param>
    /// <param name="skipBondBreakSparseNonbondSpread">When true, skips <see cref="TrySpreadNonbondOrbitalsForBondBreakSparseSigmaNeighbors"/> in 3D. Set after animated σ-only bond break (lobes already match <see cref="GetRedistributeTargets"/>); a second TrySpread can remap via <c>FindBestDirectionMapping</c> and visibly pop lobes.</param>
    /// <param name="bondBreakIsSigmaCleavageBetweenFormerPartners">True only when the bond break removed the last edge between the two centers (<see cref="AtomFunction.GetBondsTo"/> to the partner is zero). False for a π-only step (e.g. first break of C=C): σ still holds the pair — do not run σ-only carbocation slot fast path.</param>
    /// <param name="redistributionOperationBond">Bond being formed or broken in this redistribution pass (anchors guide-tier resolution and non–op-pair pinning). Default null preserves legacy behavior.</param>
    public void RedistributeOrbitals(float? piBondAngleOverride = null, Vector3? refBondWorldDirection = null, bool relaxCoplanarSigmaToTetrahedral = false, bool skipLoneLobeLayout = false, HashSet<AtomFunction> pinAtomsForSigmaRelax = null, bool skipSigmaNeighborRelax = false, ElectronOrbitalFunction bondBreakGuideLoneOrbital = null, AtomFunction newSigmaBondPartnerHint = null, int sigmaNeighborCountBeforeHint = -1, bool skipBondBreakSparseNonbondSpread = false, AtomFunction freezeSigmaNeighborSubtreeRoot = null, bool bondBreakIsSigmaCleavageBetweenFormerPartners = false, CovalentBond redistributionOperationBond = null)
    {
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            if (CovalentBond.DebugLogBreakBondMotionSources && (refBondWorldDirection.HasValue || bondBreakGuideLoneOrbital != null))
                Debug.Log(
                    "[break-motion] RedistributeOrbitals→RedistributeOrbitals3D entry atom=" + name + "(Z=" + atomicNumber + ") " +
                    "skipΣNeighRelax=" + skipSigmaNeighborRelax + " skipLoneLobeLayout=" + skipLoneLobeLayout +
                    " refW=" + (refBondWorldDirection.HasValue ? "set" : "null") + " guideOrb=" + (bondBreakGuideLoneOrbital != null) +
                    " σCleavagePartners=" + bondBreakIsSigmaCleavageBetweenFormerPartners);
            RedistributeOrbitals3D(piBondAngleOverride, refBondWorldDirection, relaxCoplanarSigmaToTetrahedral, skipLoneLobeLayout, pinAtomsForSigmaRelax, skipSigmaNeighborRelax, bondBreakGuideLoneOrbital, newSigmaBondPartnerHint, sigmaNeighborCountBeforeHint, skipBondBreakSparseNonbondSpread, freezeSigmaNeighborSubtreeRoot, bondBreakIsSigmaCleavageBetweenFormerPartners, redistributionOperationBond);
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
    /// 3D σ formation step 2: refresh each incident σ bond’s <see cref="CovalentBond.UpdateBondTransformToCurrentAtoms"/> only.
    /// Does not snap σ orbitals — avoids fighting the formation coroutine / <c>LateUpdate</c> (one pose pass per frame).
    /// </summary>
    public static void UpdateSigmaBondLineTransformsOnlyForAtoms(IEnumerable<AtomFunction> atoms)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || atoms == null) return;
        var seenBonds = new HashSet<CovalentBond>();
        foreach (var a in atoms)
        {
            if (a == null) continue;
            foreach (var b in a.CovalentBonds)
            {
                if (b == null || !seenBonds.Add(b)) continue;
                b.UpdateBondTransformToCurrentAtoms();
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

    /// <summary>σ lobe +X in nucleus local space; uses predicted local rot from bond-break targets when present.</summary>
    Vector3 OrbitalHybridTipDirectionInNucleusLocalFromPredictedOrCurrent(
        ElectronOrbitalFunction o,
        IReadOnlyList<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> predictedOccupiedEnds)
    {
        if (o == null) return Vector3.right;
        if (predictedOccupiedEnds != null)
        {
            for (int i = 0; i < predictedOccupiedEnds.Count; i++)
            {
                var e = predictedOccupiedEnds[i];
                if (e.orb != o) continue;
                Vector3 tipInParent = (e.rot * Vector3.right).normalized;
                if (o.transform.parent == transform)
                    return tipInParent;
                Vector3 tipW = o.transform.parent.TransformDirection(tipInParent);
                if (tipW.sqrMagnitude < 1e-10f) return Vector3.forward;
                return transform.InverseTransformDirection(tipW.normalized).normalized;
            }
        }
        return OrbitalTipDirectionInNucleusLocal(o);
    }

    /// <summary>
    /// Carbocation-style σ-cleavage: framework for empty placement must use <b>σ hybrid lobe tips</b> (nucleus-local), not
    /// internuclear axes — substituents may still be tetrahedral while lobes are trigonal after carbocation layout.
    /// </summary>
    bool TryAppendCarbocationFrameworkSigmaLobeTips(
        List<Vector3> dest,
        ElectronOrbitalFunction bondBreakGuideForShell,
        IReadOnlyList<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> predictedOccupiedEnds)
    {
        if (!IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(bondBreakGuideForShell)) return false;
        GetCarbonSigmaCleavageDomains(out var sig, out _);
        int sigmaN = GetDistinctSigmaNeighborCount();
        var tips = new List<Vector3>(4);
        foreach (var o in sig)
        {
            if (o == null || o.ElectronCount <= 0) continue;
            tips.Add(OrbitalHybridTipDirectionInNucleusLocalFromPredictedOrCurrent(o, predictedOccupiedEnds).normalized);
        }
        if (tips.Count < 2) return false;
        if (sigmaN > 0 && tips.Count < sigmaN) return false;
        foreach (var t in tips) dest.Add(t);
        return true;
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
    /// <param name="bondAxisIdealLocks">For each merged σ axis (parent local), the ideal polyhedron vertex it claimed; used to align shared σ orbitals on <see cref="CovalentBond"/> with the same hybrid frame.</param>
    bool TryMatchLoneOrbitalsToFreeIdealDirections(
        Vector3 refLocal,
        int slotCount,
        List<Vector3> bondAxesMerged,
        List<ElectronOrbitalFunction> loneOrbitalsOccupied,
        List<Vector3> newDirsAligned,
        out List<(Vector3 oldDir, Vector3 newDir)> mapping,
        out Vector3? pinReservedIdealDirection,
        out List<(Vector3 bondAxisLocal, Vector3 idealLocal)> bondAxisIdealLocks,
        ElectronOrbitalFunction pinLoneOrbitalForBondBreak = null)
    {
        mapping = null;
        pinReservedIdealDirection = null;
        bondAxisIdealLocks = new List<(Vector3 bondAxisLocal, Vector3 idealLocal)>();

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
            if (bestI >= 0)
            {
                idealUsed[bestI] = true;
                bondAxisIdealLocks.Add((bd.normalized, newDirsAligned[bestI].normalized));
            }
        }

        var free = new List<Vector3>();
        var freeSlotIndices = new List<int>();
        for (int i = 0; i < slotCount; i++)
        {
            if (!idealUsed[i])
            {
                freeSlotIndices.Add(i);
                free.Add(newDirsAligned[i]);
            }
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
                freeSlotIndices.RemoveAt(bestFree);
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

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && loneMatch.Count >= 2 && free.Count == loneMatch.Count && slotCount == 3)
        {
            Vector3 g = refLocal.sqrMagnitude > 1e-14f ? refLocal.normalized : Vector3.right;
            var aligned3 = BuildTrigonalGuideGroupAlignedIdealWithAzimuthMinPermCost(g, loneMatch);
            for (int fi = 0; fi < free.Count; fi++)
                free[fi] = aligned3[freeSlotIndices[fi]];
        }
        else if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && loneMatch.Count >= 2 && free.Count == loneMatch.Count)
            MinimizeTargetDirsAzimuthForPermutationCostInPlace(loneMatch, free, refLocal.normalized, 36, 0.05f);

        var permTm = FindBestOrbitalToTargetDirsPermutation(loneMatch, free, bondRadius);
        if (permTm == null)
        {
            LogVsepr3D($"TryMatch FindBestOrbitalToTargetDirsPermutation null: atom={name} Z={atomicNumber} loneMatch={loneMatch.Count}");
            mapping = null;
            return false;
        }
        if (permTm != null && loneMatch.Count >= 2 && free.Count == loneMatch.Count)
        {
            int aid = GetInstanceID();
            ComputePermutationAssignmentCostBreakdown(loneMatch, free, bondRadius, null, permTm, out float cM, out float qM, out float tM);
            // #region agent log
            AppendPermCostInvariantNdjson(
                "permCostTryMatch",
                "AtomFunction.TryMatchLoneOrbitalsToFreeIdealDirections",
                "after_FindBest loneMatch",
                aid,
                "tryMatch",
                cM,
                qM,
                tM,
                permTm,
                FormatPermCostDirsRounded(free),
                FormatPermCostTipsForOrbs(loneMatch, null),
                0f,
                FormatPermCostDirsRounded(new List<Vector3> { refLocal.normalized }));
            // #endregion
            if (DebugLogPermCostInvariantNdjson)
                Debug.Log("[perm-cost] TryMatch atomId=" + aid + " totalDeg=" + tM.ToString("F2", CultureInfo.InvariantCulture) + " coneOrPlanarDeg=" + cM.ToString("F2", CultureInfo.InvariantCulture) + " quatDeg=" + qM.ToString("F2", CultureInfo.InvariantCulture) + " n=" + loneMatch.Count);
        }
        mapping = new List<(Vector3, Vector3)>();
        for (int i = 0; i < loneMatch.Count; i++)
            mapping.Add((oldLone[i], free[permTm[i]].normalized));
        return true;
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

    /// <summary>σ/π formation partner when set; otherwise infer from <paramref name="redistributionOperationBond"/> or a single σ neighbor (far leg in O=C=O).</summary>
    AtomFunction ResolvePredictiveVseprNewBondPartner(AtomFunction newBondPartner, CovalentBond redistributionOperationBond)
    {
        if (newBondPartner != null) return newBondPartner;
        if (redistributionOperationBond == null) return null;
        if (redistributionOperationBond.AtomA == this) return redistributionOperationBond.AtomB;
        if (redistributionOperationBond.AtomB == this) return redistributionOperationBond.AtomA;
        var sig = GetDistinctSigmaNeighborAtoms();
        return sig.Count == 1 ? sig[0] : null;
    }

    bool ShouldApplyPredictiveVseprDomainModelForTryMatch(
        bool useSigmaCleavageRefForVsepr,
        AtomFunction newBondPartner,
        CovalentBond redistributionOperationBond) =>
        !useSigmaCleavageRefForVsepr
        && redistributionOperationBond != null
        && ResolvePredictiveVseprNewBondPartner(newBondPartner, redistributionOperationBond) != null;

    /// <summary>
    /// For π/σ formation TryMatch: optionally drop lobes merged out of the post-bond domain picture and augment merged σ axes with the internuclear vector (and best-aligned 0e tip). When <paramref name="applyPredictive"/> is false, copies <paramref name="loneOccupiedRaw"/> and raw merged σ axes only.
    /// </summary>
    void BuildPredictiveVseprTryMatchLoneOccupiedAndBondAxes(
        List<ElectronOrbitalFunction> loneOccupiedRaw,
        float mergeToleranceDeg,
        bool applyPredictive,
        AtomFunction newBondPartner,
        CovalentBond redistributionOperationBond,
        ElectronOrbitalFunction vseprDisappearingLoneForPredictiveCount,
        out List<ElectronOrbitalFunction> loneOccupiedForDomains,
        out List<Vector3> bondAxesForFramework,
        out int bondAxesMergedRawCount,
        out int fadeExcludeInstanceId,
        out int explicitExcludeInstanceId)
    {
        fadeExcludeInstanceId = 0;
        explicitExcludeInstanceId = 0;
        var rawMerged = CollectSigmaBondAxesLocalMerged(mergeToleranceDeg);
        bondAxesMergedRawCount = rawMerged.Count;
        bondAxesForFramework = new List<Vector3>(rawMerged);
        loneOccupiedForDomains = new List<ElectronOrbitalFunction>(loneOccupiedRaw);

        if (!applyPredictive)
            return;

        var fade = redistributionOperationBond != null ? redistributionOperationBond.OrbitalBeingFadedForCharge : null;
        if (fade != null)
        {
            int removedFade = loneOccupiedForDomains.RemoveAll(o => o != null && ReferenceEquals(o, fade));
            if (removedFade > 0)
                fadeExcludeInstanceId = fade.GetInstanceID();
        }

        var explicitDisappear = vseprDisappearingLoneForPredictiveCount;
        if (explicitDisappear != null)
        {
            int id = explicitDisappear.GetInstanceID();
            int removedEx = loneOccupiedForDomains.RemoveAll(o => o != null && o.GetInstanceID() == id);
            if (removedEx > 0)
                explicitExcludeInstanceId = id;
        }

        var partnerToward = ResolvePredictiveVseprNewBondPartner(newBondPartner, redistributionOperationBond);
        if (partnerToward == null) return;

        Vector3 w = partnerToward.transform.position - transform.position;
        if (w.sqrMagnitude < 1e-12f)
            return;

        Vector3 towardLocal = transform.InverseTransformDirection(w.normalized);
        if (towardLocal.sqrMagnitude < 1e-12f)
            return;
        towardLocal.Normalize();
        AppendUniqueFrameworkDirection(bondAxesForFramework, towardLocal, mergeToleranceDeg);

        Vector3 towardNorm = towardLocal;
        ElectronOrbitalFunction bestEmpty = null;
        float bestDot = float.NegativeInfinity;
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null || o.ElectronCount != 0 || o.transform.parent != transform) continue;
            Vector3 tip = OrbitalTipLocalDirection(o);
            if (tip.sqrMagnitude < 1e-12f) continue;
            tip = tip.normalized;
            float d = Vector3.Dot(tip, towardNorm);
            if (bestEmpty == null)
            {
                bestEmpty = o;
                bestDot = d;
            }
            else if (d > bestDot)
            {
                bestDot = d;
                bestEmpty = o;
            }
            else if (Mathf.Approximately(d, bestDot) && o.GetInstanceID() < bestEmpty.GetInstanceID())
                bestEmpty = o;
        }

        if (bestEmpty != null)
        {
            Vector3 emptyTip = OrbitalTipLocalDirection(bestEmpty);
            if (emptyTip.sqrMagnitude >= 1e-12f)
                AppendUniqueFrameworkDirection(bondAxesForFramework, emptyTip.normalized, mergeToleranceDeg);
        }
    }

    /// <summary>One σ bond per distinct neighbor (merged double/triple count as one axis).</summary>
    public List<AtomFunction> GetDistinctSigmaNeighborAtoms()
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
    /// Repositions σ-bonded hydrogens along each O–H / C–H σ **hybrid** direction.
    /// Each H uses its **current** internuclear leg length to this center so mixed substituents (e.g. C–O vs C–H) are not all forced to the same distance.
    /// <paramref name="fallbackBondLength"/> applies only when the leg is degenerate (&lt;1e-4).
    /// Calls <see cref="RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/> first so σ locks match post-<see cref="RedistributeOrbitals"/> lone layout before <see cref="CovalentBond.GetOrbitalTargetWorldState"/> reads hybrid tips (avoids σHMoved=0 when H and stale tip stay co-linear).
    /// After moving each H, <see cref="CovalentBond.ApplySigmaOrbitalTipFromRedistribution"/> locks shared σ +X to the actual C→H axis so <see cref="CovalentBond.GetOrbitalTargetWorldState"/> matches nuclei after <see cref="CovalentBond.SnapOrbitalToBondPosition"/>.
    /// Then <see cref="RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/> runs again; σ→H bonds are re-aligned to internuclear again so TryMatch cannot leave the C–H lobe off the bond line.
    /// </summary>
    public void SnapHydrogenSigmaNeighborsToBondOrbitalAxes(float fallbackBondLength)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;

        // Recompute σ hybrid locks from current lone/bond layout before reading tips; otherwise GetOrbitalTargetWorldState can stay
        // aligned with stale H positions (sigmaHMoved=0) while VSEPR directions from RedistributeOrbitals already moved lobes.
        // Z=1: Refresh early-outs (maxSlots<=1); no spurious work when this center is hydrogen.
        RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(null);

        int sigmaHVisited = 0;
        int sigmaHMoved = 0;
        float sigmaHMaxDelta = 0f;
        var bondsSnapshot = new List<CovalentBond>(covalentBonds);
        foreach (var b in bondsSnapshot)
        {
            if (b?.Orbital == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (other == null || other.AtomicNumber != 1) continue;
            sigmaHVisited++;

            b.UpdateBondTransformToCurrentAtoms();
            var (_, worldRot) = b.GetOrbitalTargetWorldState();
            Vector3 tipW = worldRot * Vector3.right;
            if (tipW.sqrMagnitude < 1e-10f)
                tipW = b.Orbital.transform.TransformDirection(Vector3.right);
            Vector3 toH = other.transform.position - transform.position;
            Vector3 axis;
            if (tipW.sqrMagnitude >= 1e-10f)
            {
                tipW.Normalize();
                if (toH.sqrMagnitude >= 1e-10f)
                {
                    toH.Normalize();
                    axis = Vector3.Dot(tipW, toH) < 0f ? -tipW : tipW;
                }
                else
                    axis = tipW;
            }
            else if (toH.sqrMagnitude >= 1e-10f)
                axis = toH.normalized;
            else
                continue;

            float legLen = Vector3.Distance(transform.position, other.transform.position);
            if (legLen < 1e-4f)
            {
                if (fallbackBondLength < 1e-4f) continue;
                legLen = fallbackBondLength;
            }

            Vector3 before = other.transform.position;
            other.transform.position = transform.position + axis * legLen;
            float legDelta = Vector3.Distance(before, other.transform.position);
            if (legDelta > 1e-6f)
            {
                sigmaHMoved++;
                sigmaHMaxDelta = Mathf.Max(sigmaHMaxDelta, legDelta);
            }
            if (DebugLogFrameworkPinSigmaRelaxTrace && legDelta > 1e-4f)
                LogFrameworkPinSigmaRelax(
                    $"SnapHydrogenΣAxes center={FormatAtomBrief(this)} H={FormatAtomBrief(other)} Δ={legDelta:F5}");
            b.UpdateBondTransformToCurrentAtoms();
            b.SnapOrbitalToBondPosition();
            Vector3 alongCh = other.transform.position - transform.position;
            if (alongCh.sqrMagnitude > 1e-10f)
                b.ApplySigmaOrbitalTipFromRedistribution(this, alongCh.normalized);
        }

        if (DebugLogHydrogenSigmaSnap && sigmaHVisited > 0)
            Debug.Log(
                "[H-σ-snap] center=" + name + "(Z=" + atomicNumber + ") σH_visited=" + sigmaHVisited + " reseated=" + sigmaHMoved +
                " maxWorldΔ=" + sigmaHMaxDelta.ToString("F5") + " per-bond leg hybrid tip");

        RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(null);

        foreach (var b in bondsSnapshot)
        {
            if (b?.Orbital == null || !b.IsSigmaBondLine()) continue;
            var oth = b.AtomA == this ? b.AtomB : b.AtomA;
            if (oth == null || oth.AtomicNumber != 1) continue;
            Vector3 g = oth.transform.position - transform.position;
            if (g.sqrMagnitude < 1e-10f) continue;
            b.UpdateBondTransformToCurrentAtoms();
            b.ApplySigmaOrbitalTipFromRedistribution(this, g.normalized);
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
        {
            Vector3 pb = hs[i].transform.position;
            hs[i].transform.position = transform.position + mapping[i].newDir * bondLength;
            if (DebugLogFrameworkPinSigmaRelaxTrace && Vector3.Distance(pb, hs[i].transform.position) > 1e-4f)
                LogFrameworkPinSigmaRelax(
                    $"TryPlaceTetrahedralHAbout1Heavy center={FormatAtomBrief(this)} H={FormatAtomBrief(hs[i])} Δ={Vector3.Distance(pb, hs[i].transform.position):F5}");
        }

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

    /// <summary>
    /// Newman stagger angle ψ (degrees) about an axis from this atom toward <paramref name="partner"/>, using current world geometry.
    /// When <paramref name="requireSigmaBondToPartner"/> is false (e.g. σ bond already removed), the σ-neighbor check is skipped so break animations can still stagger.
    /// Recompute the axis each frame as <c>(partner.position - transform.position).normalized</c> when lerping — ψ is fixed from the preview pose.
    /// </summary>
    public bool TryComputeNewmanStaggerPsi(AtomFunction partner, bool requireSigmaBondToPartner, out float psiDeg)
    {
        psiDeg = 0f;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || partner == null) return false;
        // Allow ψ for π-bearing centers (carbonyl, nitrile, etc.): ApplyNewmanStaggerTwistProgress only reseats σ-H and
        // occupied non-bond lobes; bond formation / break coroutines still gate Newman with their own GetPiBondCount()==0.
        if (requireSigmaBondToPartner && !GetDistinctSigmaNeighborAtoms().Contains(partner)) return false;

        Vector3 axis = partner.transform.position - transform.position;
        if (axis.sqrMagnitude < 1e-10f) return false;
        axis.Normalize();

        Vector3 bondParentToChild = transform.position - partner.transform.position;
        if (bondParentToChild.sqrMagnitude < 1e-10f) return false;
        bondParentToChild.Normalize();

        var parentProj = new List<Vector3>();
        foreach (var n in partner.GetDistinctSigmaNeighborAtoms())
        {
            if (n == null || n == this) continue;
            Vector3 v = n.transform.position - partner.transform.position;
            if (v.sqrMagnitude < 1e-10f) continue;
            v.Normalize();
            Vector3 p = v - Vector3.Dot(v, bondParentToChild) * bondParentToChild;
            if (p.sqrMagnitude < 1e-8f) continue;
            parentProj.Add(p.normalized);
        }
        if (parentProj.Count == 0) return false;

        // Score using σ→H directions and occupied lone/radical lobes only. Empty (0e) non-bond slots share tetrahedral
        // geometry but are not physical stagger targets — including them biases the twist (~30° vs true 60° Newman stagger).
        var childRadialForScore = new List<Vector3>();
        foreach (var n in GetDistinctSigmaNeighborAtoms())
        {
            if (n == null || n == partner || n.AtomicNumber != 1) continue;
            Vector3 d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) continue;
            childRadialForScore.Add(d.normalized);
        }
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.transform.parent != transform || orb.Bond != null) continue;
            if (orb.ElectronCount <= 0) continue;
            Vector3 dW = orb.transform.TransformDirection(Vector3.right);
            if (dW.sqrMagnitude < 1e-10f) continue;
            childRadialForScore.Add(dW.normalized);
        }
        if (childRadialForScore.Count == 0) return false;

        var childHDirWorld = new List<Vector3>();
        foreach (var n in GetDistinctSigmaNeighborAtoms())
        {
            if (n == null || n == partner || n.AtomicNumber != 1) continue;
            Vector3 d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) continue;
            childHDirWorld.Add(d.normalized);
        }

        float bestPsi;
        if (TryTrigonalMethylNewmanStaggerPsiDegrees(parentProj, childHDirWorld, axis, out bestPsi)
            && childRadialForScore.Count == 3
            && childHDirWorld.Count == 3)
        {
            // Closed form: VSEPR often leaves a ~30° (half-stagger) frame; a 30° grid misses the true 60° minimum of cos² eclipse sum.
        }
        else
        {
            const float stepDeg = 10f;
            float bestScore = float.MaxValue;
            float bestMinSep = -1f;
            bestPsi = 0f;
            for (float psi = 0f; psi < 359.99f; psi += stepDeg)
            {
                Quaternion r = Quaternion.AngleAxis(psi, axis);
                float score = NewmanStaggerEclipseScore(parentProj, childRadialForScore, axis, r);
                float minSep = NewmanBottleneckMinAngleToBack(parentProj, childRadialForScore, axis, r);
                bool betterScore = score < bestScore - 1e-4f;
                bool tiePreferStagger = Mathf.Abs(score - bestScore) <= 1e-4f && minSep > bestMinSep + 0.05f;
                if (betterScore || tiePreferStagger)
                {
                    bestScore = score;
                    bestPsi = psi;
                    bestMinSep = minSep;
                }
            }
        }

        // Heteroatom / lone-pair "front" (no σ-H on this center): eclipse-sum minimum is often at ψ=0 even when lone pairs
        // still eclipse the parent substituents (e.g. CH₃→CHR₂—OH replace). Pick ψ by maximizing min angle to back set.
        if (Mathf.Abs(bestPsi) < 0.01f && childHDirWorld.Count == 0 && childRadialForScore.Count >= 1 && parentProj.Count >= 2)
        {
            float b0 = NewmanBottleneckMinAngleToBack(parentProj, childRadialForScore, axis, Quaternion.identity);
            float bestB = b0;
            float psiPick = 0f;
            const float fineStep = 5f;
            for (float psi = fineStep; psi < 359.99f; psi += fineStep)
            {
                Quaternion r = Quaternion.AngleAxis(psi, axis);
                float b = NewmanBottleneckMinAngleToBack(parentProj, childRadialForScore, axis, r);
                if (b > bestB + 0.05f)
                {
                    bestB = b;
                    psiPick = psi;
                }
            }
            if (bestB > b0 + 1.5f)
                bestPsi = psiPick;
        }

        if (Mathf.Abs(bestPsi) < 0.01f) return false;
        psiDeg = bestPsi;
        return true;
    }

    /// <summary>
    /// Twist σ hydrogens (except σ neighbor <paramref name="sigmaPartner"/>, if set) and non-bond lobes on this atom by
    /// <paramref name="psiDeg"/> × <paramref name="twistT01"/> about <paramref name="axisUnitFromChildTowardPartner"/> through this center.
    /// Used inside bond form/break redistribution coroutines.
    /// </summary>
    /// <param name="skipNonbondLobeForTwist">Bond-break guide lobe lerped to Newman-adjusted slot — omit so Newman is not applied twice.</param>
    public void ApplyNewmanStaggerTwistProgress(float psiDeg, float twistT01, Vector3 axisUnitFromChildTowardPartner, AtomFunction sigmaPartner, bool refreshBondTransforms, ElectronOrbitalFunction skipNonbondLobeForTwist = null)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || Mathf.Abs(psiDeg) < 1e-4f) return;
        if (axisUnitFromChildTowardPartner.sqrMagnitude < 1e-10f) return;
        axisUnitFromChildTowardPartner.Normalize();

        Quaternion apply = Quaternion.AngleAxis(psiDeg * Mathf.Clamp01(twistT01), axisUnitFromChildTowardPartner);
        Vector3 c = transform.position;

        foreach (var n in GetDistinctSigmaNeighborAtoms())
        {
            if (n == null || n == sigmaPartner || n.AtomicNumber != 1) continue;
            Vector3 pb = n.transform.position;
            n.transform.position = c + apply * (n.transform.position - c);
            if (DebugLogFrameworkPinSigmaRelaxTrace && Vector3.Distance(pb, n.transform.position) > 1e-4f)
                LogFrameworkPinSigmaRelax(
                    $"NewmanTwistH center={FormatAtomBrief(this)} partner={(sigmaPartner == null ? "null" : FormatAtomBrief(sigmaPartner))} H={FormatAtomBrief(n)} ψ={psiDeg:F2}° Δ={Vector3.Distance(pb, n.transform.position):F5}");
        }

        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb == skipNonbondLobeForTwist || orb.transform.parent != transform || orb.Bond != null) continue;
            // 0e placeholders follow OrientEmpty / final layout; twisting them during Newman makes them drift then snap back.
            if (orb.ElectronCount <= 0) continue;
            Vector3 dW = orb.transform.TransformDirection(Vector3.right);
            if (dW.sqrMagnitude < 1e-10f) continue;
            Vector3 dNewW = (apply * dW).normalized;
            Vector3 localDir = transform.InverseTransformDirection(dNewW);
            if (localDir.sqrMagnitude < 1e-10f) continue;
            localDir.Normalize();
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(localDir, bondRadius);
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
        }

        if (!refreshBondTransforms) return;

        var bondsSnapshot = new List<CovalentBond>(covalentBonds);
        foreach (var b in bondsSnapshot)
        {
            if (b == null) continue;
            b.UpdateBondTransformToCurrentAtoms();
            b.SnapOrbitalToBondPosition();
        }

        RefreshCharge();
        foreach (var n in GetDistinctSigmaNeighborAtoms())
        {
            if (n != null && n != this) n.RefreshCharge();
        }
        SetupGlobalIgnoreCollisions();
    }

    /// <summary>
    /// 3D: For each atom in <paramref name="atoms"/>, updates every incident σ <see cref="CovalentBond"/>'s line transform and bond-orbital world pose from current nuclear positions.
    /// Use on the same frame as non-bond lobe lerps / σ-relax so bonding and non-bonding redistribution share one timestep (bond visuals are not a separate delayed pass).
    /// </summary>
    public static void UpdateSigmaBondVisualsForAtoms(IEnumerable<AtomFunction> atoms)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || atoms == null) return;
        var seenBonds = new HashSet<CovalentBond>();
        foreach (var a in atoms)
        {
            if (a == null) continue;
            foreach (var b in a.CovalentBonds)
            {
                if (b == null || !seenBonds.Add(b)) continue;
                b.UpdateBondTransformToCurrentAtoms();
                b.UpdateSigmaBondVisualsAfterBondTransform();
            }
        }
    }

    /// <summary>σ bond line/orbital alignment after moving nuclei or lobes without another Newman twist.</summary>
    public void RefreshSigmaBondTransformsAndChargesAroundAtom()
    {
        UpdateSigmaBondVisualsForAtoms(new AtomFunction[] { this });
        RefreshCharge();
        foreach (var n in GetDistinctSigmaNeighborAtoms())
        {
            if (n != null && n != this) n.RefreshCharge();
        }
        SetupGlobalIgnoreCollisions();
    }

    /// <summary>
    /// σ bond break: same Newman twist as <see cref="TwistRedistributeTargetsForNewmanStaggerEnd"/> for a single slot local (guide orbital).
    /// Call while preview geometry matches unstaggered σ-relax end.
    /// </summary>
    public static bool TwistSlotLocalForNewmanStaggerEnd(
        AtomFunction child,
        AtomFunction partner,
        float psiDeg,
        Vector3 slotLocalPos,
        Quaternion slotLocalRot,
        out Vector3 twistedPos,
        out Quaternion twistedRot)
    {
        twistedPos = slotLocalPos;
        twistedRot = slotLocalRot;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || child == null || partner == null
            || Mathf.Abs(psiDeg) < 1e-4f)
            return false;
        Vector3 c = child.transform.position;
        Vector3 ax = partner.transform.position - c;
        if (ax.sqrMagnitude < 1e-10f) return false;
        ax.Normalize();
        Quaternion r = Quaternion.AngleAxis(psiDeg, ax);
        Vector3 tipLocal = (slotLocalRot * Vector3.right).normalized;
        Vector3 dW = child.transform.TransformDirection(tipLocal);
        if (dW.sqrMagnitude < 1e-10f) return false;
        dW.Normalize();
        Vector3 dTw = (r * dW).normalized;
        Vector3 localDir = child.transform.InverseTransformDirection(dTw);
        if (localDir.sqrMagnitude < 1e-10f) return false;
        localDir.Normalize();
        var (pos2, rot2) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(localDir, child.BondRadius);
        twistedPos = pos2;
        twistedRot = rot2;
        return true;
    }

    /// <summary>
    /// σ bond formation step 2: rewrite VSEPR redistribute targets on <paramref name="child"/> so final lobe directions equal unstaggered targets rotated by Newman ψ
    /// about world axis child→<paramref name="partner"/> (call while preview geometry matches unstaggered σ-relax end).
    /// </summary>
    public static void TwistRedistributeTargetsForNewmanStaggerEnd(
        AtomFunction child,
        AtomFunction partner,
        float psiDeg,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || child == null || partner == null || targets == null
            || Mathf.Abs(psiDeg) < 1e-4f)
            return;
        Vector3 c = child.transform.position;
        Vector3 ax = partner.transform.position - c;
        if (ax.sqrMagnitude < 1e-10f) return;
        ax.Normalize();
        Quaternion r = Quaternion.AngleAxis(psiDeg, ax);
        float br = child.BondRadius;
        for (int i = 0; i < targets.Count; i++)
        {
            var e = targets[i];
            if (e.orb == null || e.orb.transform.parent != child.transform) continue;
            Vector3 tipLocal = (e.rot * Vector3.right).normalized;
            Vector3 dW = child.transform.TransformDirection(tipLocal);
            if (dW.sqrMagnitude < 1e-10f) continue;
            dW.Normalize();
            Vector3 dTw = (r * dW).normalized;
            Vector3 localDir = child.transform.InverseTransformDirection(dTw);
            if (localDir.sqrMagnitude < 1e-10f) continue;
            localDir.Normalize();
            var (pos2, rot2) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(localDir, br);
            targets[i] = (e.orb, pos2, rot2);
        }
    }

    /// <summary>
    /// Edit attach + H-auto: twist σ hydrogens and non-bond lobes around the bond to <paramref name="partner"/> so Newman projections
    /// are staggered vs the partner's other substituents (avoids arbitrary <see cref="Quaternion.FromToRotation"/> eclipsing from VSEPR).
    /// Only moves hydrogens on this atom (not other heavies). No-op if there is nothing to align against or no rotatable directions.
    /// </summary>
    public bool TryStaggerNewmanRelativeToPartner(AtomFunction partner)
    {
        if (partner == null) return false;
        if (!TryComputeNewmanStaggerPsi(partner, true, out float psi)) return false;
        Vector3 axis = partner.transform.position - transform.position;
        if (axis.sqrMagnitude < 1e-10f) return false;
        axis.Normalize();
        ApplyNewmanStaggerTwistProgress(psi, 1f, axis, partner, true, null);
        return true;
    }

    static void NewmanPlaneBasis(Vector3 axisUnit, out Vector3 e1, out Vector3 e2)
    {
        Vector3 aux = Mathf.Abs(Vector3.Dot(axisUnit, Vector3.up)) < 0.92f ? Vector3.up : Vector3.right;
        e1 = Vector3.Cross(axisUnit, aux);
        if (e1.sqrMagnitude < 1e-8f) e1 = Vector3.Cross(axisUnit, Vector3.forward);
        e1.Normalize();
        e2 = Vector3.Cross(axisUnit, e1).normalized;
    }

    static bool TryUnitProjectPerpendicular(Vector3 axisUnit, Vector3 worldOffset, out Vector3 projectedUnit)
    {
        projectedUnit = default;
        if (worldOffset.sqrMagnitude < 1e-10f) return false;
        worldOffset.Normalize();
        Vector3 p = worldOffset - Vector3.Dot(worldOffset, axisUnit) * axisUnit;
        if (p.sqrMagnitude < 1e-8f) return false;
        projectedUnit = p.normalized;
        return true;
    }

    static float NewmanAzimuthDeg(Vector3 unitInPlane, Vector3 e1, Vector3 e2)
    {
        float x = Vector3.Dot(unitInPlane, e1);
        float y = Vector3.Dot(unitInPlane, e2);
        float deg = Mathf.Atan2(y, x) * Mathf.Rad2Deg;
        if (deg < 0f) deg += 360f;
        return deg;
    }

    /// <summary>
    /// Three in-plane directions at ~120° (Newman projection of —CX₃). <paramref name="sortedAsc"/> = azimuths in [0,360) sorted ascending.
    /// </summary>
    static bool SortedAzimuthsRoughlyTrigonal120(float[] sortedAsc)
    {
        if (sortedAsc == null || sortedAsc.Length != 3) return false;
        float g1 = sortedAsc[1] - sortedAsc[0];
        float g2 = sortedAsc[2] - sortedAsc[1];
        float g3 = 360f - (sortedAsc[2] - sortedAsc[0]);
        float m = Mathf.Min(g1, Mathf.Min(g2, g3));
        float M = Mathf.Max(g1, Mathf.Max(g2, g3));
        return m >= 85f && M <= 155f;
    }

    /// <summary>
    /// Exact stagger twist for two ~trigonal sets in the Newman plane: after rotation by returned ψ, sorted front azimuths sit ~60° from sorted back azimuths.
    /// </summary>
    static bool TryTrigonalMethylNewmanStaggerPsiDegrees(
        List<Vector3> backInPlaneUnit,
        List<Vector3> childHDirWorldUnit,
        Vector3 axisUnit,
        out float psiDeg)
    {
        psiDeg = 0f;
        if (backInPlaneUnit.Count != 3 || childHDirWorldUnit.Count != 3) return false;

        NewmanPlaneBasis(axisUnit, out Vector3 e1, out Vector3 e2);

        var ba = new float[3];
        for (int i = 0; i < 3; i++)
            ba[i] = NewmanAzimuthDeg(backInPlaneUnit[i], e1, e2);
        System.Array.Sort(ba);
        if (!SortedAzimuthsRoughlyTrigonal120(ba)) return false;

        var fa = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!TryUnitProjectPerpendicular(axisUnit, childHDirWorldUnit[i], out Vector3 fp)) return false;
            fa[i] = NewmanAzimuthDeg(fp, e1, e2);
        }
        System.Array.Sort(fa);
        if (!SortedAzimuthsRoughlyTrigonal120(fa)) return false;

        float meanDelta = 0f;
        for (int i = 0; i < 3; i++)
            meanDelta += Mathf.DeltaAngle(fa[i], ba[i]);
        meanDelta /= 3f;

        psiDeg = 60f - meanDelta;
        psiDeg = Mathf.Repeat(psiDeg + 180f, 360f) - 180f;
        return true;
    }

    /// <summary>Smallest (over child directions) of each direction’s minimum angle to any back substituent in the projected plane.</summary>
    static float NewmanBottleneckMinAngleToBack(
        List<Vector3> parentProjUnit,
        List<Vector3> childRadialWorldUnit,
        Vector3 axisUnit,
        Quaternion rotWorld)
    {
        float bottleneck = 180f;
        foreach (Vector3 raw in childRadialWorldUnit)
        {
            Vector3 c = rotWorld * raw;
            c -= Vector3.Dot(c, axisUnit) * axisUnit;
            if (c.sqrMagnitude < 1e-10f) continue;
            c.Normalize();
            float bestAng = 180f;
            foreach (Vector3 pp in parentProjUnit)
            {
                float ang = Vector3.Angle(c, pp);
                if (ang < bestAng) bestAng = ang;
            }
            if (bestAng < bottleneck) bottleneck = bestAng;
        }
        return bottleneck;
    }

    static float NewmanStaggerEclipseScore(
        List<Vector3> parentProjUnit,
        List<Vector3> childRadialWorldUnit,
        Vector3 axisUnit,
        Quaternion rotWorld)
    {
        float score = 0f;
        foreach (Vector3 raw in childRadialWorldUnit)
        {
            Vector3 c = rotWorld * raw;
            c -= Vector3.Dot(c, axisUnit) * axisUnit;
            if (c.sqrMagnitude < 1e-10f) continue;
            c.Normalize();
            float maxCos2 = 0f;
            foreach (Vector3 pp in parentProjUnit)
            {
                float dot = Vector3.Dot(c, pp);
                float cs = dot * dot;
                if (cs > maxCos2) maxCos2 = cs;
            }
            score += maxCos2;
        }
        return score;
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

    /// <summary>
    /// Same σ-relax as homolytic when only 0e/1e non-bonds, or <b>one</b> 2e non-bond (heterolytic ex-bond lone pair) with others ≤1e.
    /// Stricter <see cref="HasOnlyNonBondRadicalOrEmptyShellForSp2BondBreak"/> remains for carbocation <see cref="IsSp2BondBreakEmptyAlongRefCase"/>.
    /// </summary>
    bool HasNonBondShellForSp2BondBreakSigmaRelax()
    {
        int ge2 = 0;
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null) continue;
            if (o.ElectronCount > 2) return false;
            if (o.ElectronCount >= 2) ge2++;
        }
        return ge2 <= 1;
    }

    void GetNonBondShellClassCounts(out int c0, out int c1, out int c2)
    {
        c0 = c1 = c2 = 0;
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null) continue;
            if (o.ElectronCount == 0) c0++;
            else if (o.ElectronCount == 1) c1++;
            else if (o.ElectronCount >= 2) c2++;
        }
    }

    /// <summary>
    /// True only for sp² carbocation layout (CH₃⁺-style): 3 σ neighbors and exactly four non-bond lobes — one 0e and three with electrons.
    /// Must be false for ·CH₂ / ·CH₃ (radical + 0e placeholder): those use tetrahedral σ-relax and empty ⊥ radical+σ, not empty along ex-bond.
    /// </summary>
    public bool IsSp2BondBreakEmptyAlongRefCase()
    {
        if (GetDistinctSigmaNeighborCount() != 3) return false;
        if (!HasOnlyNonBondRadicalOrEmptyShellForSp2BondBreak()) return false;
        return TryGetCarbocationOneEmptyThreeElectronsNonbond(out _, out _);
    }

    /// <summary>
    /// σ-only bond break: use trigonal-planar <b>electron</b> geometry — σ neighbors + each non-bond with e⁻ count as domains; <b>0e empty not counted</b>.
    /// Plane ⊥ ex-bond ref; carbocation-style shells use <see cref="IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase"/> (empty ⊥ σ+occupied framework, not along ref).
    /// <list type="bullet"><item>3 domains (e.g. 3σ; 2σ+one occupied non-bond; 1σ+two occupied) → trigonal.</item>
    /// <item>CH₃⁺ pattern: <see cref="IsSp2BondBreakEmptyAlongRefCase"/>.</item>
    /// <item>2σ + only empties (no e⁻ on non-bonds): extra case (CH₂⁺-style).</item>
    /// <item>2σ + ≥1 empty + 1e non-bond (CH₂⁺ with odd electron on a second lobe): trigonal — empty ⟂ plane.</item>
    /// <item>2σ + 2e lone + 1e (CH₂⁻-style): four e⁻ domains (each occupied non-bond lobe counts) → tetrahedral, not trigonal.</item>
    /// <item>Excludes ·CH₂: 2σ, <b>no</b> 0e non-bond, only 1e non-bonds, no 2e lone → tetrahedral.</item></list>
    /// </summary>
    public bool IsBondBreakTrigonalPlanarFrameworkCase()
    {
        if (GetPiBondCount() > 0) return false;
        int sigmaN = GetDistinctSigmaNeighborCount();
        if (sigmaN < 2 || sigmaN > 3) return false;
        if (!HasNonBondShellForSp2BondBreakSigmaRelax()) return false;
        if (IsSp2BondBreakEmptyAlongRefCase()) return true;

        GetNonBondShellClassCounts(out int c0, out int c1, out int c2);
        int occNonBondOrbitals = 0;
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null) continue;
            if (o.ElectronCount > 0) occNonBondOrbitals++;
        }
        int electronDomains = sigmaN + occNonBondOrbitals;
        // Pure ·CH₂ homolytic: no empty (0e) on nucleus — tetrahedral. With empty (CH₂⁺), allow 1e + empty without forcing tetrahedral.
        if (sigmaN == 2 && c0 == 0 && c1 >= 1 && c2 == 0)
            return false;
        if (electronDomains == 3)
            return true;
        if (sigmaN == 2 && c0 >= 1 && c1 == 0 && c2 <= 1)
            return true;
        return false;
    }

    /// <summary>
    /// σ-only bond break: exactly three occupied electron domains and one 0e non-bond (empty ⊥ σ+occupied framework, not along ex-bond ref). Includes classic four non-bond shell and 3σ + empty p after cleavage.
    /// Pass <paramref name="bondBreakGuideOnThisAtom"/> when known (ex-bond lobe on this nucleus) so the shell matches the fragment <b>after</b> the broken bond is removed.
    /// <see cref="IsSp2BondBreakEmptyAlongRefCase"/> remains the special CH₃⁺ “empty along ref” four non-bond case.
    /// </summary>
    public bool IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(ElectronOrbitalFunction bondBreakGuideOnThisAtom = null)
    {
        if (IsSp2BondBreakEmptyAlongRefCase()) return true;
        if (AtomicNumber != 6) return false;
        GetNonBondShellClassCounts(out int c0, out _, out _);
        if (c0 < 1) return false;
        return TryGetCarbocationOneEmptyAndThreeOccupiedDomains(bondBreakGuideOnThisAtom, out _, out _);
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
    /// When <paramref name="freezeSigmaNeighborSubtreeRoot"/> matches σ neighbor <c>i</c>, that branch emits no targets (substrate frozen for FG attach; avoids O(N) over the parent fragment).
    /// </summary>
    static void BuildSigmaNeighborTargetsWithFragmentRigidRotation(
        Vector3 pivotWorld,
        IReadOnlyList<AtomFunction> sigmaNeighbors,
        IReadOnlyList<Vector3> oldUnitDirs,
        IReadOnlyList<Vector3> newUnitDirs,
        AtomFunction pivot,
        out List<(AtomFunction atom, Vector3 targetWorld)> targets,
        HashSet<AtomFunction> pinWorld = null,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
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
                if (freezeSigmaNeighborSubtreeRoot != null && neighbor == freezeSigmaNeighborSubtreeRoot)
                    continue;
                if (pinWorld != null && pinWorld.Contains(neighbor)) continue;
                float dist = Vector3.Distance(pivotWorld, neighbor.transform.position);
                targets.Add((neighbor, pivotWorld + newUnitDirs[i].normalized * dist));
            }
            return;
        }

        for (int i = 0; i < n; i++)
        {
            if (freezeSigmaNeighborSubtreeRoot != null && sigmaNeighbors[i] == freezeSigmaNeighborSubtreeRoot)
                continue;
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

        if (DebugLogFrameworkPinSigmaRelaxTrace && targets.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"BuildSigmaRigid pivot={FormatAtomBrief(pivot)} overlap={overlap} nΣ={n} ");
            sb.Append(FormatPinSummary(pinWorld)).Append(" targets=").Append(targets.Count);
            const float te = 1e-4f;
            foreach (var (atom, tw) in targets)
            {
                float d = Vector3.Distance(atom.transform.position, tw);
                if (d > te)
                    sb.Append(" | ").Append(FormatAtomBrief(atom)).Append(" Δ=").Append(d.ToString("F5"));
            }
            LogFrameworkPinSigmaRelax(sb.ToString());
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

    /// <summary>
    /// True when σ directions from a pivot already match (or trivially allow) a regular tetrahedral star:
    /// <b>0 σ</b> (empty list) or <b>1 σ</b> — vacuously true; <b>2+</b> iff every pair is ~109.47° apart (subset of tet vertices).
    /// </summary>
    static bool AreSigmaDirectionsAlreadyTetrahedralFramework(IReadOnlyList<Vector3> unitDirsFromCenter, float toleranceDeg)
    {
        if (unitDirsFromCenter == null || unitDirsFromCenter.Count == 0 || unitDirsFromCenter.Count == 1) return true;
        const float tetEdgeDeg = 109.471f;
        int n = unitDirsFromCenter.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (Mathf.Abs(Vector3.Angle(unitDirsFromCenter[i], unitDirsFromCenter[j]) - tetEdgeDeg) > toleranceDeg)
                    return false;
            }
        }
        return true;
    }

    /// <summary>Reference axis for VSEPR / σ-relax (π bond toward partner, else override / first σ).</summary>
    public Vector3 GetRedistributeReferenceLocal(float? piBondAngleOverride = null, Vector3? refBondWorldDirection = null) =>
        ResolveReferenceBondDirectionLocal(piBondAngleOverride, refBondWorldDirection);

    /// <summary>
    /// AX₃E with coplanar σ axes (e.g. after π bond break): σ neighbors should move onto three tetrahedral vertices.
    /// Used for animated bond-break; instant apply uses <see cref="TryRelaxCoplanarSigmaNeighborsToTetrahedral3D"/>.
    /// </summary>
    /// <param name="requireCoplanarBondAxes">When false, still maps three σ directions to a tetrahedral face even if substituents are slightly pyramidal — used for σ bond formation 2→3 (trigonal radical → sp³).</param>
    public bool TryComputeCoplanarTetrahedralSigmaNeighborRelaxTargets(Vector3 refLocalNormalized,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets,
        HashSet<AtomFunction> pinWorld = null,
        bool requireCoplanarBondAxes = true,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
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
        if (requireCoplanarBondAxes && !AreThreeDirectionsCoplanar(worldDirs)) return false;

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
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld, freezeSigmaNeighborSubtreeRoot);
        return targets != null && targets.Count > 0;
    }

    /// <summary>
    /// AX₃E with coplanar σ axes (e.g. after π bond break): move σ neighbors onto three vertices of a tetrahedron so lone pair + σ bonds can adopt sp³-like angles. Bond σ orbitals (on CovalentBond) follow atom positions in LateUpdate.
    /// </summary>
    void TryRelaxCoplanarSigmaNeighborsToTetrahedral3D(Vector3 refLocalNormalized, HashSet<AtomFunction> pinWorld = null, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (!TryComputeCoplanarTetrahedralSigmaNeighborRelaxTargets(refLocalNormalized, out var targets, pinWorld, requireCoplanarBondAxes: true, freezeSigmaNeighborSubtreeRoot) || targets == null) return;

        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, targetWorld) in targets)
        {
            if (DebugLogFrameworkPinSigmaRelaxTrace)
            {
                float d = Vector3.Distance(atom.transform.position, targetWorld);
                if (d > 1e-4f)
                    LogFrameworkPinSigmaRelax(
                        $"TryRelaxCoplanarSigma→Tet center={FormatAtomBrief(this)} APPLY {FormatAtomBrief(atom)} Δ={d:F5}");
            }
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
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets,
        HashSet<AtomFunction> pinWorld = null,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        targets = null;
        int pi = GetPiBondCount();
        if (pi < 1) return false;
        var sigmaNeighbors = GetDistinctSigmaNeighborAtoms();
        int sigmaN = sigmaNeighbors.Count;
        if (sigmaN != 3) return false;

        var worldDirs = new List<Vector3>(3);
        foreach (var n in sigmaNeighbors)
        {
            var d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) return false;
            worldDirs.Add(d.normalized);
        }
        bool coplanar = AreThreeDirectionsCoplanar(worldDirs);
        if ((atomicNumber == 6 || atomicNumber == 7) && DebugLogTrigonalPlanarSigmaRelax)
        {
            int pinCount = pinWorld == null ? 0 : pinWorld.Count;
            Debug.Log(
                "[sp2-relax] compute center=" + name + "(Z=" + atomicNumber + ") pi=" + pi +
                " sigmaN=" + sigmaN + " coplanar=" + coplanar + " pinCount=" + pinCount +
                " freeze=" + (freezeSigmaNeighborSubtreeRoot == null
                    ? "null"
                    : (freezeSigmaNeighborSubtreeRoot.name + "(Z=" + freezeSigmaNeighborSubtreeRoot.AtomicNumber + ")")));
        }
        if (coplanar) return false;

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
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld, freezeSigmaNeighborSubtreeRoot);
        if ((atomicNumber == 6 || atomicNumber == 7) && DebugLogTrigonalPlanarSigmaRelax)
        {
            float maxD = 0f;
            if (targets != null)
            {
                foreach (var (n, tw) in targets)
                    if (n != null) maxD = Mathf.Max(maxD, Vector3.Distance(n.transform.position, tw));
            }
            Debug.Log(
                "[sp2-relax] targets center=" + name + "(Z=" + atomicNumber + ") nTargets=" +
                (targets == null ? 0 : targets.Count) + " maxDelta=" + maxD.ToString("F5"));
        }
        return targets != null && targets.Count > 0;
    }

    /// <summary>
    /// After a π bond re-forms, σ neighbors may still sit at ~tetrahedral positions (from bond-break relax). sp² centers with
    /// three σ neighbors + π should be trigonal planar; if σ directions are not coplanar, snap neighbors onto 120° in-plane.
    /// Skips when already coplanar (FG builds, undistorted sp²).
    /// </summary>
    void TryRelaxSigmaNeighborsToTrigonalPlanar3D(Vector3 refLocalNormalized, HashSet<AtomFunction> pinWorld = null, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (!TryComputeTrigonalPlanarSigmaNeighborRelaxTargets(refLocalNormalized, out var targets, pinWorld, freezeSigmaNeighborSubtreeRoot) || targets == null) return;

        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, targetWorld) in targets)
        {
            if (pinWorld != null && pinWorld.Contains(atom)) continue;
            if (DebugLogFrameworkPinSigmaRelaxTrace)
            {
                float d = Vector3.Distance(atom.transform.position, targetWorld);
                if (d > 1e-4f)
                    LogFrameworkPinSigmaRelax(
                        $"TryRelaxTrigonalPlanar center={FormatAtomBrief(this)} APPLY {FormatAtomBrief(atom)} Δ={d:F5}");
            }
            atom.transform.position = targetWorld;
            moved.Add(atom);
        }

        RefreshCharge();
        foreach (var a in moved)
            a.RefreshCharge();
        SetupGlobalIgnoreCollisions();
    }

    /// <summary>
    /// σ-only bond break: <b>2 or 3</b> σ neighbors, non-bond shell compatible with <see cref="HasNonBondShellForSp2BondBreakSigmaRelax"/>.
    /// <b>Trigonal framework</b> (<see cref="IsBondBreakTrigonalPlanarFrameworkCase"/>): 3 e⁻ domains (σ + occupied non-bonds) in plane ⊥ ref; 0e along ref when present.
    /// <b>Radical</b> (·CH₃ / ·CH₂): tetrahedral framework with ref along ex-bond; σ neighbors map to tetrahedral vertices (two σ → best two of three).
    /// Used for animated bond-break (<see cref="CovalentBond.BuildSigmaRelaxMovesForBreakBond"/>) and instant <see cref="TryApplySp2BondBreakTrigonalPlanarSigmaNeighborRelax3D"/>.
    /// </summary>
    public bool TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets(
        Vector3? refBondWorldDirection,
        HashSet<AtomFunction> pinWorld,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        targets = null;
        if (GetPiBondCount() > 0)
        {
            if (DebugLogCcBondBreakGeometry && GetDistinctSigmaNeighborCount() >= 2 && HasOnlyNonBondRadicalOrEmptyShellForSp2BondBreak())
                LogCcBondBreak($"TryComputeSp2 skip atom={name}: piCount={GetPiBondCount()} (need 0 for σ-only relax)");
            return false;
        }
        int sigmaN = GetDistinctSigmaNeighborCount();
        if (sigmaN != 2 && sigmaN != 3)
        {
            if (DebugLogCcBondBreakGeometry && HasOnlyNonBondRadicalOrEmptyShellForSp2BondBreak())
                LogCcBondBreak($"TryComputeSp2 skip atom={name}: sigmaNeighborCount={sigmaN} (need 2 or 3)");
            return false;
        }
        if (!HasNonBondShellForSp2BondBreakSigmaRelax())
        {
            if (DebugLogCcBondBreakGeometry)
                LogCcBondBreak($"TryComputeSp2 skip atom={name}: non-bond shell not σ-relax (at most one 2e lobe, no lobe >2e)");
            return false;
        }

        Vector3 refLocal;
        if (refBondWorldDirection.HasValue && refBondWorldDirection.Value.sqrMagnitude >= 0.01f)
        {
            refLocal = transform.InverseTransformDirection(refBondWorldDirection.Value.normalized).normalized;
        }
        else
        {
            if (sigmaN == 2)
            {
                if (DebugLogCcBondBreakGeometry)
                    LogCcBondBreak($"TryComputeSp2 skip atom={name}: σN=2 requires bond-break ref world direction");
                return false;
            }
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
        var worldDirs = new List<Vector3>(sigmaN);
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

        // Radical / tet: skip nuclear relax when σ neighbor directions are already a tetrahedral subset (0 σ vacuous; 2+ pairwise ~109.5°).
        bool sp2TrigonalFramework = IsSp2BondBreakEmptyAlongRefCase() || IsBondBreakTrigonalPlanarFrameworkCase();
        const float tetSkipTolDeg = 12f;
        if (!sp2TrigonalFramework && AreSigmaDirectionsAlreadyTetrahedralFramework(worldDirs, tetSkipTolDeg))
        {
            targets = null;
            if (CovalentBond.DebugLogBreakBondSigmaRelaxWhy)
                Debug.Log($"[break-σ-relax] TryComputeSp2 skip rigid relax: σ framework already ~tetrahedral (σN={sigmaN}) center={name}(Z={AtomicNumber})");
            return false;
        }

        Vector3 refN = refLocal.normalized;
        var idealWorld = new List<Vector3>(3);
        if (sp2TrigonalFramework)
        {
            // sp²: σ + e⁻ non-bonds coplanar; ref ⟂ that plane — carbocation-style empties ⊥ framework (<see cref="IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase"/>).
            var ideal3 = VseprLayout.GetIdealLocalDirections(3);
            Quaternion qPlane = Quaternion.FromToRotation(Vector3.forward, refN);
            for (int i = 0; i < 3; i++)
                idealWorld.Add(transform.TransformDirection((qPlane * ideal3[i]).normalized).normalized);
            if (DebugLogCcBondBreakGeometry)
            {
                float la0 = Vector3.Angle(refN, (qPlane * ideal3[0]).normalized);
                float la1 = Vector3.Angle(refN, (qPlane * ideal3[1]).normalized);
                float la2 = Vector3.Angle(refN, (qPlane * ideal3[2]).normalized);
                string tag = IsSp2BondBreakEmptyAlongRefCase() ? "carbocation 3σ" : "trigonal framework (3 e⁻ domains, plane ⊥ ref)";
                LogCcBondBreak($"TryComputeSp2 ideal atom={name}: {tag} σ-trigonal ⟂ ref; ∠(ref,σ-slot0..2)={la0:F1}° {la1:F1}° {la2:F1}° (expect ~90°)");
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
                LogCcBondBreak($"TryComputeSp2 ideal atom={name}: radical tetrahedral σN={sigmaN}; local ∠(ref,σ-slot1..3)={la1:F1}° {la2:F1}° {la3:F1}° (tetra ~109.5°)");
            }
        }

        List<(Vector3 oldDir, Vector3 newDir)> mapping;
        if (worldDirs.Count == 3)
        {
            mapping = FindBestDirectionMapping(worldDirs, idealWorld);
        }
        else
        {
            mapping = null;
            float bestTotal = float.MaxValue;
            for (int skip = 0; skip < 3; skip++)
            {
                var pair = new List<Vector3>(2);
                for (int j = 0; j < 3; j++)
                {
                    if (j == skip) continue;
                    pair.Add(idealWorld[j]);
                }
                var m = FindBestDirectionMapping(worldDirs, pair);
                if (m == null) continue;
                float total = 0f;
                foreach (var x in m)
                    total += Vector3.Angle(x.oldDir, x.newDir);
                if (total < bestTotal - 1e-4f)
                {
                    bestTotal = total;
                    mapping = m;
                }
            }
        }

        if (mapping == null || mapping.Count != sigmaNeighbors.Count)
        {
            if (DebugLogCcBondBreakGeometry)
                LogCcBondBreak($"TryComputeSp2 fail atom={name}: σ world→ideal mapping null or count≠σN (σN={sigmaN})");
            return false;
        }

        var newDirs = new List<Vector3>(sigmaN);
        for (int i = 0; i < sigmaN; i++)
            newDirs.Add(mapping[i].newDir.normalized);
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld, freezeSigmaNeighborSubtreeRoot);
        if (targets == null || targets.Count == 0)
        {
            if (DebugLogCcBondBreakGeometry)
                LogCcBondBreak($"TryComputeSp2 fail atom={name}: BuildSigmaNeighborTargetsWithFragmentRigidRotation produced no moves");
            return false;
        }
        if (CovalentBond.DebugLogBreakBondSigmaRelaxWhy)
        {
            float maxD = 0f;
            foreach (var (a, tw) in targets)
                if (a != null) maxD = Mathf.Max(maxD, Vector3.Distance(a.transform.position, tw));
            float sumMapDeg = 0f;
            foreach (var x in mapping)
                sumMapDeg += Vector3.Angle(x.oldDir, x.newDir);
            string fw = IsSp2BondBreakEmptyAlongRefCase() || IsBondBreakTrigonalPlanarFrameworkCase()
                ? "trigonalPlane⊥ref"
                : "radicalTetra(σ-slot1..3)";
            var nb = new System.Text.StringBuilder();
            for (int i = 0; i < sigmaNeighbors.Count; i++)
            {
                var sn = sigmaNeighbors[i];
                if (sn != null) nb.Append(sn.name).Append("(Z=").Append(sn.AtomicNumber).Append(") ");
            }
            Debug.Log(
                $"[break-σ-relax] TryComputeSp2Detail center={name}(Z={AtomicNumber}) framework={fw} σN={sigmaN} " +
                $"neighbors=[{nb}] sumMap∠={sumMapDeg:F2}° nTargets={targets.Count} maxWorldΔ={maxD:F5}");
        }
        return true;
    }

    /// <summary>Instant apply for redistribute when bond-break animation is not used — same targets as <see cref="TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets"/>.</summary>
    void TryApplySp2BondBreakTrigonalPlanarSigmaNeighborRelax3D(Vector3? refBondWorldDirection, HashSet<AtomFunction> pinWorld = null, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (!TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets(refBondWorldDirection, pinWorld, out var t, freezeSigmaNeighborSubtreeRoot) || t == null) return;

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
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
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

        // Tetrahedral relax has tetrahedral "gauge" freedom: the same shell can match but with a different vertex labeling.
        // AlignFirstDirectionTo always forces vertex 0 onto refL, which can cause spurious tetrahedral rotations when the current shell
        // already matches but with a different equivalent labeling. Try all 4 vertex labelings and pick the one minimizing motion.
        var idealLocal4 = VseprLayout.GetIdealLocalDirections(4);

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

        var isPinned = new bool[4];
        for (int i = 0; i < 4; i++)
        {
            var n = neighOrder[i];
            if (freezeSigmaNeighborSubtreeRoot != null && n == freezeSigmaNeighborSubtreeRoot) isPinned[i] = true;
            if (pinWorld != null && pinWorld.Contains(n)) isPinned[i] = true;
        }

        float bestCost = float.MaxValue;
        List<(Vector3 oldDir, Vector3 newDir)> bestMapping = null;

        for (int k = 0; k < 4; k++)
        {
            var ideal4K = VseprLayout.AlignTetrahedronKthVertexTo(idealLocal4, k, refL);
            var idealWK = new List<Vector3>(4);
            for (int i = 0; i < 4; i++)
                idealWK.Add(transform.TransformDirection(ideal4K[i]).normalized);

            var mappingK = FindBestDirectionMapping(oldDirs, idealWK);
            if (mappingK == null || mappingK.Count != 4) continue;

            float cost = 0f;
            for (int i = 0; i < 4; i++)
            {
                if (isPinned[i]) continue; // only minimize motion for unpinned neighbors
                cost += Vector3.Angle(oldDirs[i], mappingK[i].newDir);
            }

            if (bestMapping == null || cost < bestCost - 1e-4f)
            {
                bestCost = cost;
                bestMapping = mappingK;
            }
        }

        var mapping = bestMapping;
        if (mapping == null || mapping.Count != 4) return false;

        var t = new List<(AtomFunction, Vector3)>();
        const float moveEps = 1e-4f;
        for (int i = 0; i < 4; i++)
        {
            var n = neighOrder[i];
            if (freezeSigmaNeighborSubtreeRoot != null && n == freezeSigmaNeighborSubtreeRoot) continue;
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
        out List<(AtomFunction atom, Vector3 startWorld, Vector3 endWorld)> moves,
        HashSet<AtomFunction> mergePins = null,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
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
        if (mergePins != null)
        {
            foreach (var p in mergePins)
                if (p != null) pin.Add(p);
        }

        if (!TryComputeTetrahedralFourSigmaNeighborRelaxTargets(alignAlong, pin, out var t, freezeSigmaNeighborSubtreeRoot) || t == null || t.Count == 0) return false;
        moves = new List<(AtomFunction, Vector3, Vector3)>();
        foreach (var (n, end) in t)
            moves.Add((n, n.transform.position, end));
        return true;
    }

    /// <summary>
    /// σ count <b>2→3</b> with AX₃E shell (three σ neighbors + exactly one occupied non-bond): electron geometry becomes tetrahedral; move the two older σ substituents (and optionally adjust the third) off the trigonal plane toward ~109.5°.
    /// Pinned <paramref name="partnerAlongRef"/> is the new bond partner. Does not run when the center has π bonds (sp² vinyl-style).
    /// </summary>
    public bool TryGetTetrahedralThreeSigmaAx3ERelaxMovesForBondFormation(
        AtomFunction partnerAlongRef,
        int sigmaNeighborCountBefore,
        out List<(AtomFunction atom, Vector3 startWorld, Vector3 endWorld)> moves,
        HashSet<AtomFunction> mergePins = null,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        moves = null;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return false;
        if (partnerAlongRef == null || sigmaNeighborCountBefore < 0) return false;
        if (GetPiBondCount() != 0) return false;
        if (GetDistinctSigmaNeighborCount() != 3) return false;
        if (GetDistinctSigmaNeighborCount() <= sigmaNeighborCountBefore) return false;
        if (sigmaNeighborCountBefore != 2) return false;

        Vector3 refLocal = RedistributeReferenceDirectionLocalForTargets(partnerAlongRef);
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;
        else refLocal.Normalize();

        var pin = new HashSet<AtomFunction> { partnerAlongRef };
        if (mergePins != null)
        {
            foreach (var p in mergePins)
                if (p != null) pin.Add(p);
        }
        if (!TryComputeCoplanarTetrahedralSigmaNeighborRelaxTargets(refLocal, out var targets, pin, requireCoplanarBondAxes: false, freezeSigmaNeighborSubtreeRoot) || targets == null || targets.Count == 0)
            return false;

        moves = new List<(AtomFunction, Vector3, Vector3)>();
        foreach (var (n, end) in targets)
            moves.Add((n, n.transform.position, end));
        return true;
    }

    void MaybeApplyTetrahedralSigmaRelaxForBondFormation(AtomFunction partnerAlongRef, int sigmaNeighborCountBefore, HashSet<AtomFunction> mergePins = null, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        List<(AtomFunction atom, Vector3 startWorld, Vector3 endWorld)> moves = null;
        string relaxBranch = null;
        if (TryGetTetrahedralFourSigmaNeighborRelaxMovesForBondFormation(partnerAlongRef, sigmaNeighborCountBefore, out var m4, mergePins, freezeSigmaNeighborSubtreeRoot) && m4 != null)
        {
            moves = m4;
            relaxBranch = "fourσ";
        }
        else if (TryGetTetrahedralThreeSigmaAx3ERelaxMovesForBondFormation(partnerAlongRef, sigmaNeighborCountBefore, out var m3, mergePins, freezeSigmaNeighborSubtreeRoot) && m3 != null)
        {
            moves = m3;
            relaxBranch = "threeσAx3E";
        }
        if (moves == null)
        {
            if (DebugLogReplaceHRedistribute)
            {
                int occLone = bondedOrbitals.Count(o => o != null && o.Bond == null && o.ElectronCount > 0);
                int sigma = GetDistinctSigmaNeighborCount();
                LogReplaceHRedistribute(
                    "MaybeApplyTetΣRelax NO branch | center=" + FormatAtomBrief(this)
                    + $" partner={(partnerAlongRef == null ? "null" : FormatAtomBrief(partnerAlongRef))}"
                    + $" σBefore={sigmaNeighborCountBefore} σNow={sigma} π={GetPiBondCount()} occLoneNonbond={occLone}"
                    + " | fourσ sketch: need σ↑ to 4 with **no** occupied lone lobes (CH₄-style)"
                    + " | threeσAx3E sketch: need σ 2→3, π=0, σBefore==2 (trigonal radical→sp³)"
                    + $" | your σ increment ok? {sigma > sigmaNeighborCountBefore}");
            }
            return;
        }
        const float moveEps = 1e-4f;
        bool any = false;
        foreach (var (_, s, e) in moves)
            if (Vector3.Distance(s, e) > moveEps) any = true;
        if (!any)
        {
            if (DebugLogReplaceHRedistribute)
                LogReplaceHRedistribute(
                    "MaybeApplyTetΣRelax branch=" + relaxBranch + " but all move deltas ~0 | center=" + FormatAtomBrief(this));
            return;
        }
        if (DebugLogReplaceHRedistribute)
            LogReplaceHRedistribute(
                "MaybeApplyTetΣRelax APPLY branch=" + relaxBranch + " moves=" + moves.Count + " center=" + FormatAtomBrief(this));
        if (DebugLogFrameworkPinSigmaRelaxTrace)
            LogFrameworkPinSigmaRelax(
                $"MaybeApplyTetrahedralSigmaRelax {relaxBranch} center={FormatAtomBrief(this)} "
                + FormatPinSummary(mergePins) + $" rawMoves={moves.Count}");
        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, start, end) in moves)
        {
            if (mergePins != null && mergePins.Contains(atom))
            {
                if (DebugLogFrameworkPinSigmaRelaxTrace && Vector3.Distance(start, end) > moveEps)
                    LogFrameworkPinSigmaRelax(
                        $"  tetRelax SKIP (pinned) {FormatAtomBrief(atom)} Δ={Vector3.Distance(start, end):F5}");
                continue;
            }
            if (DebugLogFrameworkPinSigmaRelaxTrace && Vector3.Distance(start, end) > moveEps)
                LogFrameworkPinSigmaRelax(
                    $"  tetRelax APPLY {FormatAtomBrief(atom)} Δ={Vector3.Distance(start, end):F5}");
            atom.transform.position = end;
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
    /// True when <paramref name="guide"/> is the ex-bond lobe on <b>this</b> nucleus for a bond that is already cleared there: listed in <see cref="bondedOrbitals"/>, parented to this transform, and not participating in a <see cref="ElectronOrbitalFunction.Bond"/>.
    /// </summary>
    bool IsBondBreakGuideOrbitalWithBondingCleared(ElectronOrbitalFunction guide)
    {
        if (guide == null) return false;
        if (guide.Bond != null) return false;
        if (guide.transform.parent != transform) return false;
        return bondedOrbitals.Contains(guide);
    }

    /// <summary>
    /// Carbocation trigonal shell: three occupied domains + one empty site (0e), or the same topology while the ex-bond guide lobe still has e⁻ (e.g. before redistribution) when σN==3 and the guide is the only non-bond on this nucleus (CH₃–CH₃–style fragment).
    /// When <paramref name="bondBreakGuideOnThisAtom"/> is set, three σ neighbors ⇒ three occupied σ lobes; the guide is the fourth site (not counted among occupied even if <c>ElectronCount&gt;0</c> yet). No extra 0e placeholders besides optional sole future-empty, and no other occupied non-bonds besides the guide when the guide is still paired.
    /// With <paramref name="bondBreakGuideOnThisAtom"/> == <c>null</c>, pre-cleave CH₃ is inferred only when σN==3 and there is ≥1 occupied non-bond but **no** 0e non-bond yet (sole ≥2e ex-bond lobe). If only 0e non-bonds exist, the usual 1×0e + 3σ fallback applies.
    /// </summary>
    /// <remarks>When <see cref="DebugLogCarbocationTrigonalPlanarityDiag"/> is on and Z=6, each <c>return false</c> emits <c>[bond-break-cc] [tryget-carb-shell] FAIL …</c> with σ / non-bond inventory (CH₃–CH₃ triage).</remarks>
    bool TryGetCarbocationOneEmptyAndThreeOccupiedDomains(
        ElectronOrbitalFunction bondBreakGuideOnThisAtom,
        out ElectronOrbitalFunction emptyOrb,
        out List<ElectronOrbitalFunction> occupiedDomains)
    {
        emptyOrb = null;
        occupiedDomains = null;
        GetCarbonSigmaCleavageDomains(out var sigmaOrbs, out var nonBondAll);

        var sigmaOccupied = new List<ElectronOrbitalFunction>();
        foreach (var o in sigmaOrbs)
            if (o != null && o.ElectronCount > 0) sigmaOccupied.Add(o);
        sigmaOccupied.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));

        int sigmaN = GetDistinctSigmaNeighborCount();
        string SigmaInv() =>
            $"σN={sigmaN} σBondOrbs={sigmaOrbs.Count} σOcc={sigmaOccupied.Count} σ[e⁻]=[{string.Join(",", sigmaOrbs.ConvertAll(o => o == null ? "null" : $"{o.GetInstanceID()}:{o.ElectronCount}"))}] "
            + $"nb=[{string.Join(",", nonBondAll.ConvertAll(o => o == null ? "null" : $"{o.GetInstanceID()}:{o.ElectronCount}"))}]";

        void Fail(string reason, string branch)
        {
            if (!DebugLogCarbocationTrigonalPlanarityDiag || AtomicNumber != 6) return;
            string g = bondBreakGuideOnThisAtom == null
                ? "guide=null"
                : $"guideId={bondBreakGuideOnThisAtom.GetInstanceID()} e={bondBreakGuideOnThisAtom.ElectronCount} onNuc={bondBreakGuideOnThisAtom.transform.parent == transform} inBonded={bondedOrbitals.Contains(bondBreakGuideOnThisAtom)}";
            LogCarbocationPlanarityDiagLine($"[tryget-carb-shell] FAIL parentId={GetInstanceID()} branch={branch} reason={reason} {g} {SigmaInv()}");
        }

        if (sigmaOccupied.Count != sigmaN)
        {
            Fail("sigmaOccupied.Count!=sigmaN (some σ bond orbital has e≤0 or count mismatch)", "preGuide");
            return false;
        }

        bool guideOnNucleus = bondBreakGuideOnThisAtom != null
            && bondedOrbitals.Contains(bondBreakGuideOnThisAtom)
            && bondBreakGuideOnThisAtom.transform.parent == transform;

        if (guideOnNucleus && sigmaN == 3)
        {
            if (sigmaOccupied.Count != 3)
            {
                Fail("guidePath sigmaOccupied.Count!=3", "guideOnNucleus");
                return false;
            }

            if (bondBreakGuideOnThisAtom.ElectronCount == 0)
            {
                foreach (var o in nonBondAll)
                {
                    if (o == null || o.ElectronCount != 0) continue;
                    if (o != bondBreakGuideOnThisAtom)
                    {
                        Fail($"extra0eOnNucleus orbId={o.GetInstanceID()}!=guide", "guideOnNucleus");
                        return false;
                    }
                }
            }
            else if (bondBreakGuideOnThisAtom.ElectronCount >= 2)
            {
                // Bond still shows a pair on the ex-bond lobe (e.g. CH₃–CH₃ before e⁻ counts update). Skip e==1 homolytic radicals here.
                foreach (var o in nonBondAll)
                {
                    if (o == null) continue;
                    if (o.ElectronCount > 0 && o != bondBreakGuideOnThisAtom)
                    {
                        Fail($"extraOccupiedNb orbId={o.GetInstanceID()}!=guide (pre-cleave expects sole occupied non-bond = guide)", "guideOnNucleus");
                        return false;
                    }
                    if (o.ElectronCount == 0)
                    {
                        Fail($"extra0eOnNucleus orbId={o.GetInstanceID()} alongside guide e>0", "guideOnNucleus");
                        return false;
                    }
                }
            }
            else
            {
                Fail("guidePath guide has 1e (not 0e and not paired lobe); skip carbocation shell", "guideOnNucleus");
                return false;
            }

            occupiedDomains = new List<ElectronOrbitalFunction>(sigmaOccupied);
            foreach (var o in nonBondAll)
            {
                if (o == null || o == bondBreakGuideOnThisAtom) continue;
                if (o.ElectronCount <= 0) continue;
                occupiedDomains.Add(o);
            }
            occupiedDomains.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            if (occupiedDomains.Count != 3)
            {
                Fail($"guidePath occupiedDomains.Count={occupiedDomains.Count}!=3 (σ plus occupied non-bonds except guide)", "guideOnNucleus");
                return false;
            }
            emptyOrb = bondBreakGuideOnThisAtom;
            return true;
        }

        // guide=null: infer pre-cleave CH₃ only when the ex-bond lobe still has e⁻ (no real 0e non-bond on nucleus yet).
        // If nb is already [-34004:0] (sole empty), skip — classic fallback below handles 1×0e + 3σ.
        if (bondBreakGuideOnThisAtom == null && AtomicNumber == 6 && sigmaN == 3 && sigmaOccupied.Count == 3)
        {
            bool has0eNb = false;
            bool hasOccNb = false;
            foreach (var o in nonBondAll)
            {
                if (o == null) continue;
                if (o.ElectronCount == 0) has0eNb = true;
                else hasOccNb = true;
            }
            if (!has0eNb && hasOccNb)
            {
                ElectronOrbitalFunction soleOccupiedNb = null;
                foreach (var o in nonBondAll)
                {
                    if (o == null) continue;
                    if (o.ElectronCount == 1)
                    {
                        Fail("guide=null shell: 1e non-bond (radical)", "inferredPreCleave");
                        return false;
                    }
                    if (soleOccupiedNb != null)
                    {
                        Fail("guide=null shell: multiple occupied non-bonds", "inferredPreCleave");
                        return false;
                    }
                    soleOccupiedNb = o;
                }
                if (soleOccupiedNb != null && soleOccupiedNb.ElectronCount >= 2
                    && bondedOrbitals.Contains(soleOccupiedNb) && soleOccupiedNb.transform.parent == transform)
                {
                    occupiedDomains = new List<ElectronOrbitalFunction>(sigmaOccupied);
                    occupiedDomains.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
                    emptyOrb = soleOccupiedNb;
                    return true;
                }
            }
        }

        var empties = nonBondAll.Where(o => o != null && o.ElectronCount == 0).ToList();
        if (empties.Count != 1)
        {
            Fail($"fallback empties.Count={empties.Count} (need 1 sole 0e on nucleus)", "fallback");
            return false;
        }
        emptyOrb = empties[0];
        occupiedDomains = new List<ElectronOrbitalFunction>();
        foreach (var o in sigmaOrbs)
            if (o != null && o.ElectronCount > 0) occupiedDomains.Add(o);
        foreach (var o in nonBondAll)
            if (o != null && o.ElectronCount > 0) occupiedDomains.Add(o);
        occupiedDomains.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        if (occupiedDomains.Count != 3)
        {
            Fail($"fallback occupiedDomains.Count={occupiedDomains.Count}!=3", "fallback");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Unit normal to the σ framework plane for carbocation bond-break layout (local space). Uses non-parallel σ axes when possible; otherwise ref × σ.
    /// </summary>
    bool TryComputeCarbocationFrameworkNormalLocal(Vector3 refLocal, IReadOnlyList<Vector3> bondAxes, out Vector3 normalLocal)
    {
        normalLocal = default;
        if (bondAxes != null)
        {
            for (int i = 0; i < bondAxes.Count; i++)
            {
                for (int j = i + 1; j < bondAxes.Count; j++)
                {
                    Vector3 c = Vector3.Cross(bondAxes[i].normalized, bondAxes[j].normalized);
                    if (c.sqrMagnitude > 1e-8f)
                    {
                        normalLocal = c.normalized;
                        return true;
                    }
                }
            }
            if (bondAxes.Count >= 1)
            {
                Vector3 a0 = bondAxes[0].normalized;
                Vector3 c = Vector3.Cross(a0, refLocal.normalized);
                if (c.sqrMagnitude > 1e-8f)
                {
                    normalLocal = c.normalized;
                    return true;
                }
                c = Vector3.Cross(refLocal.normalized, a0);
                if (c.sqrMagnitude > 1e-8f)
                {
                    normalLocal = c.normalized;
                    return true;
                }
            }
        }
        if (refLocal.sqrMagnitude < 1e-8f) return false;
        normalLocal = refLocal.normalized;
        return true;
    }

    static void PreferCarbocationGuideTowardRefLocal(int[] perm, List<ElectronOrbitalFunction> occSorted, Vector3 refLocal, List<Vector3> trigDirs, ElectronOrbitalFunction bondBreakGuideLoneOrbital)
    {
        if (perm == null || occSorted == null || trigDirs == null || bondBreakGuideLoneOrbital == null) return;
        int gi = occSorted.IndexOf(bondBreakGuideLoneOrbital);
        if (gi < 0) return;
        int bestT = 0;
        float bestDot = float.NegativeInfinity;
        for (int t = 0; t < trigDirs.Count; t++)
        {
            float d = Vector3.Dot(trigDirs[t].normalized, refLocal.normalized);
            if (d > bestDot)
            {
                bestDot = d;
                bestT = t;
            }
        }
        if (perm[gi] == bestT) return;
        for (int k = 0; k < occSorted.Count; k++)
        {
            if (k != gi && perm[k] == bestT)
            {
                int tmp = perm[gi];
                perm[gi] = perm[k];
                perm[k] = tmp;
                return;
            }
        }
    }

    /// <summary>
    /// Inventory for unified carbon σ-cleavage layout: one shared σ orbital per σ bond line <b>incident on this nucleus</b> (counts as one domain from this center’s perspective) + every non-bond lobe parented on this nucleus.
    /// <see cref="CovalentBond.AuthoritativeAtomForOrbitalRedistributionPose"/> still picks a single writer for delta resets; domain counting does not use it.
    /// </summary>
    public void GetCarbonSigmaCleavageDomains(out List<ElectronOrbitalFunction> sigmaBondOrbitalsAuthoritative, out List<ElectronOrbitalFunction> nonBondOnNucleus)
    {
        sigmaBondOrbitalsAuthoritative = new List<ElectronOrbitalFunction>();
        nonBondOnNucleus = new List<ElectronOrbitalFunction>();
        foreach (var b in covalentBonds)
        {
            if (b == null || b.Orbital == null || !b.IsSigmaBondLine()) continue;
            if (b.AtomA != this && b.AtomB != this) continue;
            sigmaBondOrbitalsAuthoritative.Add(b.Orbital);
        }
        sigmaBondOrbitalsAuthoritative.Sort((a, c) => a.GetInstanceID().CompareTo(c.GetInstanceID()));
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null || o.transform.parent != transform) continue;
            nonBondOnNucleus.Add(o);
        }
        nonBondOnNucleus.Sort((a, c) => a.GetInstanceID().CompareTo(c.GetInstanceID()));
    }

    /// <param name="lockTipToHybridDirection">When true (ex-bond pin on vertex 0), use <see cref="ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(Vector3,float)"/> without twist minimization so <c>+X</c> stays aligned with <paramref name="idealDirLocalNormalized"/>.</param>
    void ApplyCarbonBreakIdealLocalDirection(ElectronOrbitalFunction orb, Vector3 idealDirLocalNormalized, bool lockTipToHybridDirection = false)
    {
        if (orb == null) return;
        Vector3 d = idealDirLocalNormalized.normalized;
        if (orb.transform.parent == transform)
        {
            var (pos, rot) = lockTipToHybridDirection
                ? ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(d, bondRadius)
                : ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(d, bondRadius, orb.transform.localRotation);
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
            return;
        }
        if (orb.Bond is CovalentBond cb && cb.Orbital == orb && cb.IsSigmaBondLine()
            && (cb.AtomA == this || cb.AtomB == this))
        {
            Vector3 w = transform.TransformDirection(d);
            if (DebugLogCarbocationSigmaRedistributionTrace)
            {
                Vector3 tipBefore = OrbitalTipDirectionInNucleusLocal(orb);
                LogCarbocationSigmaRedistributionTrace(
                    $"ApplyCarbonBreakIdeal bond σ path parentId={GetInstanceID()} orbId={orb.GetInstanceID()} bondId={cb.GetInstanceID()} " +
                    $"idealLocal={FormatLocalDir3(d)} hybridWorldReq={FormatLocalDir3(w)} tipNucLocalBefore={FormatLocalDir3(tipBefore)} " +
                    $"∠(ideal,tipBefore)={Vector3.Angle(d, tipBefore):F2}°");
            }
            cb.ApplySigmaOrbitalTipFromRedistribution(this, w);
            if (DebugLogCarbocationSigmaRedistributionTrace)
            {
                Vector3 tipAfter = OrbitalTipDirectionInNucleusLocal(orb);
                LogCarbocationSigmaRedistributionTrace(
                    $"ApplyCarbonBreakIdeal bond σ after parentId={GetInstanceID()} orbId={orb.GetInstanceID()} tipNucLocalAfter={FormatLocalDir3(tipAfter)} " +
                    $"∠(ideal,tipAfter)={Vector3.Angle(d, tipAfter):F2}°");
            }
        }
    }

    /// <summary>
    /// Diagnostic: trigonal planar σ means three σ lobe tips (nucleus-local) coplanar with pairwise ~120°, each ~90° from ex-bond ref (empty p axis). Triple product magnitude near 0 ⇒ coplanar tips.
    /// Also logs **0e empty** vs σ framework: |empty·n̂| (n̂ ⊥ σ plane) and ∠(emptyTip, each σTip) (~90° when empty is ⊥ to the trigonal plane).
    /// </summary>
    void LogCarbocationTrigonalSigmaPlanarityDiagnostics(string phase, Vector3 refLocalNorm, bool substituentsPlacedOnTrigonalRays = false)
    {
        if (!DebugLogCarbocationTrigonalPlanarityDiag) return;
        GetCarbonSigmaCleavageDomains(out var sig, out _);
        var tips = new List<Vector3>(3);
        foreach (var o in sig)
        {
            if (o == null || o.ElectronCount <= 0) continue;
            tips.Add(OrbitalTipDirectionInNucleusLocal(o));
        }
        if (tips.Count != 3)
        {
            LogCarbocationPlanarityDiagLine($"{phase} carbocation σ trigonal diag: expected 3 σ tips, got {tips.Count} parentId={GetInstanceID()}");
            return;
        }
        float a01 = Vector3.Angle(tips[0], tips[1]);
        float a02 = Vector3.Angle(tips[0], tips[2]);
        float a12 = Vector3.Angle(tips[1], tips[2]);
        float triple = Mathf.Abs(Vector3.Dot(tips[0], Vector3.Cross(tips[1], tips[2])));
        Vector3 r = refLocalNorm.sqrMagnitude < 1e-10f ? Vector3.right : refLocalNorm.normalized;
        float ar0 = Vector3.Angle(tips[0], r);
        float ar1 = Vector3.Angle(tips[1], r);
        float ar2 = Vector3.Angle(tips[2], r);
        LogCarbocationPlanarityDiagLine(
            $"{phase} carbocation σ tips (nucleus-local): pairwise ∠={a01:F1}° {a02:F1}° {a12:F1}° (expect ~120°) |triple|={triple:F5} (0=coplanar) ∠(σTip,exBondRef)={ar0:F1}° {ar1:F1}° {ar2:F1}° (expect ~90°) parentId={GetInstanceID()}");

        ElectronOrbitalFunction emptyOrb =
            TryGetCarbocationOneEmptyAndThreeOccupiedDomains(null, out var eTry, out _)
                ? eTry
                : bondedOrbitals.FirstOrDefault(o => o != null && o.Bond == null && o.ElectronCount == 0);
        if (emptyOrb != null)
        {
            Vector3 eTip = OrbitalTipDirectionInNucleusLocal(emptyOrb).normalized;
            float xe0 = Vector3.Angle(eTip, tips[0]);
            float xe1 = Vector3.Angle(eTip, tips[1]);
            float xe2 = Vector3.Angle(eTip, tips[2]);
            Vector3 nHat = Vector3.Cross(tips[0], tips[1]);
            float nMag = nHat.magnitude;
            float enPlane = nMag > 1e-8f ? Mathf.Abs(Vector3.Dot(eTip, (nHat / nMag).normalized)) : -1f;
            string planePart = enPlane >= 0f
                ? $"|emptyTip·n̂|={enPlane:F4} (n̂≈±σ-plane normal, expect ~1 if empty ⟂ trigonal σ plane)"
                : "|emptyTip·n̂|=n/a (σ tips nearly colinear)";
            LogCarbocationPlanarityDiagLine(
                $"{phase} carbocation **0e empty** vs σ framework: ∠(emptyTip,σTip)={xe0:F1}° {xe1:F1}° {xe2:F1}° (each ~90° if empty is orthogonal to σ plane) {planePart} emptyOrbId={emptyOrb.GetInstanceID()}");
        }
        else
            LogCarbocationPlanarityDiagLine($"{phase} carbocation **0e empty** vs σ: no sole empty on nucleus parentId={GetInstanceID()}");

        if (!substituentsPlacedOnTrigonalRays && GetDistinctSigmaNeighborCount() == 3)
            LogCarbocationPlanarityDiagLine(
                $"{phase} substituent placement: σ neighbors **not** moved to trigonal rays in this pass (or skip) — skeleton angles below may still read ~109.5° while σ tips read ~120° parentId={GetInstanceID()}");

        LogCarbocationSigmaLobeVersusInternuclearAxesDiagnostics(phase, null);
    }

    /// <summary>
    /// Compares σ <b>lobe tips</b> (post-layout) to <b>internuclear axes</b> C→σ-neighbor. Large ∠(tip,axis) with tetrahedral pairwise
    /// internuclear angles ⇒ substituents still ~sp³ in space while lobes read trigonal — typical when <c>applyRigidSubstituentWorldMotion</c> is false.
    /// </summary>
    void LogCarbocationSigmaLobeVersusInternuclearAxesDiagnostics(string phase, ElectronOrbitalFunction bondBreakGuideForShell)
    {
        if (!DebugLogCarbocationTrigonalPlanarityDiag) return;
        if (!IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(bondBreakGuideForShell))
            return;

        var rows = new List<(int orbId, int neighborId, int neighborZ, float angRaw, float angMinToAxis)>();
        var axes = new List<Vector3>();
        foreach (var b in covalentBonds)
        {
            if (b == null || b.Orbital == null || !b.IsSigmaBondLine()) continue;
            if (b.AtomA != this && b.AtomB != this) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (other == null) continue;
            Vector3 w = other.transform.position - transform.position;
            if (w.sqrMagnitude < 1e-12f) continue;
            Vector3 axisL = transform.InverseTransformDirection(w.normalized).normalized;
            axes.Add(axisL);
            var orb = b.Orbital;
            if (orb == null || orb.ElectronCount <= 0) continue;
            Vector3 tip = OrbitalTipDirectionInNucleusLocal(orb);
            float raw = Vector3.Angle(tip, axisL);
            float minAlign = Mathf.Min(raw, Vector3.Angle(tip, -axisL));
            rows.Add((orb.GetInstanceID(), other.GetInstanceID(), other.AtomicNumber, raw, minAlign));
        }
        if (axes.Count < 3 || rows.Count < 3) return;
        rows.Sort((a, b) => a.orbId.CompareTo(b.orbId));
        float x01 = Vector3.Angle(axes[0], axes[1]);
        float x02 = Vector3.Angle(axes[0], axes[2]);
        float x12 = Vector3.Angle(axes[1], axes[2]);
        string perBond = string.Join(" | ", rows.ConvertAll(t =>
            $"σId={t.orbId}→nbId={t.neighborId} Z={t.neighborZ} ∠(tip,axis)={t.angRaw:F1}° minAlign={t.angMinToAxis:F1}°"));
        bool axesTetrahedralLike =
            x01 >= 100f && x01 <= 118f && x02 >= 100f && x02 <= 118f && x12 >= 100f && x12 <= 118f;
        bool lobeMisalignedFromBondAxes = rows.TrueForAll(t => t.angMinToAxis > 15f);
        string warn = axesTetrahedralLike && lobeMisalignedFromBondAxes ? " TETRA_AXES_VS_TRIGONAL_LOBES_WARN" : "";
        LogCarbocationPlanarityDiagLine(
            $"{phase} σ internuclear axes (substituent skeleton): pairwise ∠={x01:F1}° {x02:F1}° {x12:F1}° (trigonal ~120°; tetra ~109.5°) parentId={GetInstanceID()}{warn}");
        LogCarbocationPlanarityDiagLine($"{phase} σ lobe vs C→neighbor axis: {perBond}");
    }

    /// <summary>True when σ substituent fragments (beyond each σ edge from this pivot) are pairwise disjoint — same ring/overlap rule as <see cref="BuildSigmaNeighborTargetsWithFragmentRigidRotation"/>.</summary>
    bool SubstituentFragmentsDisjointForRigidSigmaBreak(IReadOnlyList<AtomFunction> sigmaNeighbors)
    {
        if (sigmaNeighbors == null || sigmaNeighbors.Count == 0) return false;
        var fragments = new List<List<AtomFunction>>(sigmaNeighbors.Count);
        for (int i = 0; i < sigmaNeighbors.Count; i++)
            fragments.Add(GetAtomsOnSideOfSigmaBond(sigmaNeighbors[i]));
        var seenAcross = new HashSet<AtomFunction>();
        for (int i = 0; i < fragments.Count; i++)
        {
            foreach (var a in fragments[i])
            {
                if (!seenAcross.Add(a))
                    return false;
            }
        }
        return true;
    }

    /// <summary>Best rotation about <paramref name="axisWorld"/> (unit) mapping several unit directions simultaneously; validates max angular error.</summary>
    static bool TryComputeRotationAboutAxisMatchingDirections(
        IReadOnlyList<Vector3> fromUnitWorld,
        IReadOnlyList<Vector3> toUnitWorld,
        Vector3 axisWorld,
        out Quaternion rWorld,
        float maxErrorDeg = 10f)
    {
        rWorld = Quaternion.identity;
        if (fromUnitWorld == null || toUnitWorld == null || fromUnitWorld.Count != toUnitWorld.Count || fromUnitWorld.Count == 0)
            return false;
        axisWorld = axisWorld.normalized;
        if (axisWorld.sqrMagnitude < 1e-10f) return false;
        float sumSin = 0f, sumCos = 0f;
        for (int i = 0; i < fromUnitWorld.Count; i++)
        {
            Vector3 o = Vector3.ProjectOnPlane(fromUnitWorld[i], axisWorld);
            Vector3 n = Vector3.ProjectOnPlane(toUnitWorld[i], axisWorld);
            if (o.sqrMagnitude < 1e-10f || n.sqrMagnitude < 1e-10f) return false;
            o.Normalize();
            n.Normalize();
            float angRad = Vector3.SignedAngle(o, n, axisWorld) * Mathf.Deg2Rad;
            sumSin += Mathf.Sin(angRad);
            sumCos += Mathf.Cos(angRad);
        }
        float theta = Mathf.Atan2(sumSin, sumCos);
        rWorld = Quaternion.AngleAxis(theta * Mathf.Rad2Deg, axisWorld);
        for (int i = 0; i < fromUnitWorld.Count; i++)
        {
            if (Vector3.Angle(rWorld * fromUnitWorld[i].normalized, toUnitWorld[i].normalized) > maxErrorDeg)
                return false;
        }
        return true;
    }

    /// <summary>σ cleavage: rigid rotation about ex-bond ref mapping σ substituent directions onto ideal tet vertices 1..n; returns world rotation and aligned local tet (vertex 0 = ref).</summary>
    bool TryComputeRigidSigmaCleavageWorldRotation(
        Vector3 refBondWorldDirectionNormalized,
        out Quaternion rWorld,
        out Vector3[] alignedLocal4)
    {
        rWorld = Quaternion.identity;
        alignedLocal4 = null;
        if (AtomicNumber != 6)
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace("TryComputeRigidSigmaCleavageWorldRotation FAIL Z!=6");
            return false;
        }
        var neighbors = GetDistinctSigmaNeighborAtoms();
        neighbors.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        int n = neighbors.Count;
        if (n < 1 || n > 3)
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryComputeRigidSigmaCleavageWorldRotation FAIL σ neighbor count n={n} parentId={GetInstanceID()}");
            return false;
        }
        if (!SubstituentFragmentsDisjointForRigidSigmaBreak(neighbors))
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryComputeRigidSigmaCleavageWorldRotation FAIL SubstituentFragmentsDisjointForRigidSigmaBreak parentId={GetInstanceID()} σN={n}");
            return false;
        }

        Vector3 refWorld = refBondWorldDirectionNormalized.normalized;
        Vector3 refLocal = transform.InverseTransformDirection(refWorld).normalized;
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        var ideal4 = VseprLayout.GetIdealLocalDirections(4);
        alignedLocal4 = VseprLayout.AlignFirstDirectionTo(ideal4, refLocal).ToArray();
        if (alignedLocal4.Length != 4)
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryComputeRigidSigmaCleavageWorldRotation FAIL alignedLocal4 length!=4 parentId={GetInstanceID()}");
            return false;
        }

        var oldDirs = new List<Vector3>(n);
        Vector3 pivotPos = transform.position;
        foreach (var nb in neighbors)
        {
            if (nb == null)
            {
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace($"TryComputeRigidSigmaCleavageWorldRotation FAIL null σ neighbor parentId={GetInstanceID()}");
                return false;
            }
            Vector3 d = nb.transform.position - pivotPos;
            if (d.sqrMagnitude < 1e-12f)
            {
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace($"TryComputeRigidSigmaCleavageWorldRotation FAIL σ neighbor coincident pivot parentId={GetInstanceID()}");
                return false;
            }
            oldDirs.Add(d.normalized);
        }

        var targetDirsWorld = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 tw = transform.TransformDirection(alignedLocal4[i + 1].normalized);
            if (tw.sqrMagnitude < 1e-10f)
            {
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace($"TryComputeRigidSigmaCleavageWorldRotation FAIL bad tet target dir i={i} parentId={GetInstanceID()}");
                return false;
            }
            targetDirsWorld.Add(tw.normalized);
        }

        var mapping = FindBestDirectionMapping(oldDirs, targetDirsWorld);
        if (mapping == null || mapping.Count != n)
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryComputeRigidSigmaCleavageWorldRotation FAIL FindBestDirectionMapping null or count≠n parentId={GetInstanceID()} σN={n} mapping={(mapping == null ? "null" : mapping.Count.ToString())}");
            return false;
        }

        var fromP = new List<Vector3>(n);
        var toP = new List<Vector3>(n);
        foreach (var (o, t) in mapping)
        {
            fromP.Add(o.normalized);
            toP.Add(t.normalized);
        }

        if (!TryComputeRotationAboutAxisMatchingDirections(fromP, toP, refWorld, out rWorld, 12f))
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryComputeRigidSigmaCleavageWorldRotation FAIL TryComputeRotationAboutAxisMatchingDirections parentId={GetInstanceID()} σN={n}");
            return false;
        }

        if (DebugLogCcBondBreakUnifiedSparseDiag)
            LogCcBondBreakUnifiedDiag(
                "rigidSubstituentFrame parentId=" + GetInstanceID() + " σN=" + n + " refLocal=" + FormatLocalDir3(refLocal) + " tetRAboutRef");
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace(
                "TryComputeRigidSigmaCleavageWorldRotation OK parentId=" + GetInstanceID() + " σN=" + n + " refLocal=" + FormatLocalDir3(refLocal) + " tetRAboutRef");
        return true;
    }

    /// <summary>End world positions for atoms in σ substituent fragments after rigid <paramref name="rWorld"/> about pivot (excludes pivot).</summary>
    void CollectRigidSigmaCleavageSubstituentAtomEndPositions(Quaternion rWorld, List<(AtomFunction atom, Vector3 endWorld)> dst)
    {
        if (dst == null) return;
        Vector3 pivotPos = transform.position;
        var neighbors = GetDistinctSigmaNeighborAtoms();
        var moved = new HashSet<AtomFunction>();
        foreach (var nb in neighbors)
        {
            if (nb == null) continue;
            foreach (var a in GetAtomsOnSideOfSigmaBond(nb))
            {
                if (a == null || a == this || !moved.Add(a)) continue;
                Vector3 p = a.transform.position;
                Vector3 end = pivotPos + rWorld * (p - pivotPos);
                dst.Add((a, end));
            }
        }
    }

    /// <summary>Append (atom → end world) for bond-break σ-relax animation when rigid substituent frame applies.</summary>
    public void TryAppendRigidSigmaCleavageSubstituentAtomMoves(Vector3 refBondWorldDirection, Dictionary<AtomFunction, (Vector3 start, Vector3 end)> moves)
    {
        if (moves == null || AtomicNumber != 6) return;
        if (refBondWorldDirection.sqrMagnitude < 0.01f) return;
        // When Sp2 returns no targets, Collect still falls through here — same rigid spin if σ directions already form a tetrahedral star (σN=0 included).
        var sigmaNb = GetDistinctSigmaNeighborAtoms();
        var dirs = new List<Vector3>(sigmaNb.Count);
        bool okDirs = true;
        foreach (var n in sigmaNb)
        {
            var d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) { okDirs = false; break; }
            dirs.Add(d.normalized);
        }
        const float tetSkipTolRigid = 12f;
        if (okDirs && AreSigmaDirectionsAlreadyTetrahedralFramework(dirs, tetSkipTolRigid))
        {
            if (CovalentBond.DebugLogBreakBondSigmaRelaxWhy)
                Debug.Log($"[break-σ-relax] TryAppendRigidCleavage skip: σ framework already ~tetrahedral (σN={sigmaNb.Count}) center={name}(Z={AtomicNumber})");
            return;
        }
        if (!TryComputeRigidSigmaCleavageWorldRotation(refBondWorldDirection.normalized, out var rWorld, out _))
        {
            if (CovalentBond.DebugLogBreakBondSigmaRelaxWhy)
                Debug.Log($"[break-σ-relax] TryAppendRigidCleavage SKIP center={name}(Z={AtomicNumber}) (no rigid R; σ disjoint / mapping / axis fit)");
            return;
        }
        var ends = new List<(AtomFunction atom, Vector3 endWorld)>();
        CollectRigidSigmaCleavageSubstituentAtomEndPositions(rWorld, ends);
        if (CovalentBond.DebugLogBreakBondSigmaRelaxWhy)
        {
            float maxD = 0f;
            foreach (var (atom, end) in ends)
                if (atom != null) maxD = Mathf.Max(maxD, Vector3.Distance(atom.transform.position, end));
            float rotDeg = Quaternion.Angle(Quaternion.identity, rWorld);
            Debug.Log($"[break-σ-relax] TryAppendRigidCleavage center={name}(Z={AtomicNumber}) |R|={rotDeg:F2}° nEnds={ends.Count} maxWorldΔ={maxD:F5}");
        }
        foreach (var (atom, end) in ends)
        {
            if (atom == null) continue;
            moves[atom] = (atom.transform.position, end);
        }
    }

    void ApplyRigidSigmaCleavagePivotOrbitalTargets(
        Vector3[] alignedLocal4,
        Quaternion rWorld,
        ElectronOrbitalFunction bondBreakGuideOrbital)
    {
        if (alignedLocal4 == null || alignedLocal4.Length != 4) return;
        GetCarbonSigmaCleavageDomains(out var sigmaOrbs, out var nonBondAll);

        ElectronOrbitalFunction pin = bondBreakGuideOrbital != null && nonBondAll.Contains(bondBreakGuideOrbital)
            && bondBreakGuideOrbital.ElectronCount > 0
            ? bondBreakGuideOrbital
            : null;

        if (pin != null)
            ApplyCarbonBreakIdealLocalDirection(pin, alignedLocal4[0].normalized, true);

        Quaternion pivotW = transform.rotation;
        void ApplyRToTipWorld(ElectronOrbitalFunction orb, bool skipBecausePin)
        {
            if (orb == null || skipBecausePin) return;
            Vector3 tipW;
            if (orb.transform.parent == transform)
                tipW = transform.TransformDirection(OrbitalTipLocalDirection(orb)).normalized;
            else if (orb.Bond is CovalentBond cb && cb.Orbital == orb && cb.IsSigmaBondLine()
                     && cb.AuthoritativeAtomForOrbitalRedistributionPose() == this)
                tipW = cb.Orbital.transform.TransformDirection(Vector3.right).normalized;
            else
                return;
            Vector3 newTipW = (rWorld * tipW).normalized;
            Vector3 newLocal = transform.InverseTransformDirection(newTipW).normalized;
            if (newLocal.sqrMagnitude < 1e-10f) return;
            ApplyCarbonBreakIdealLocalDirection(orb, newLocal, false);
        }

        foreach (var o in nonBondAll)
        {
            if (o == null || o.ElectronCount <= 0) continue;
            ApplyRToTipWorld(o, pin != null && o == pin);
        }
        foreach (var o in sigmaOrbs)
        {
            if (o == null || o.ElectronCount <= 0) continue;
            ApplyRToTipWorld(o, false);
        }
    }

    /// <summary>Rigid σ cleavage: rotate disjoint substituent fragments about pivot with one R (⊥ ref), align hybrid lobes to the same R. Unified sparse perm fallback when ineligible.</summary>
    bool TryApplyRigidSigmaBreakSubstituentFrame(
        Vector3 refBondWorldDirection,
        ElectronOrbitalFunction bondBreakGuideOrbital,
        bool applySubstituentWorldMotion)
    {
        if (AtomicNumber != 6 || refBondWorldDirection.sqrMagnitude < 0.01f)
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace(
                    $"TryApplyRigidSigmaBreakSubstituentFrame SKIP Z={AtomicNumber} refSqr={refBondWorldDirection.sqrMagnitude:F6} parentId={GetInstanceID()}");
            return false;
        }
        if (!TryComputeRigidSigmaCleavageWorldRotation(refBondWorldDirection.normalized, out var rWorld, out var aligned4))
            return false;

        if (applySubstituentWorldMotion)
        {
            var moved = new HashSet<AtomFunction> { this };
            var ends = new List<(AtomFunction atom, Vector3 endWorld)>();
            CollectRigidSigmaCleavageSubstituentAtomEndPositions(rWorld, ends);
            foreach (var (atom, end) in ends)
            {
                if (atom == null || !moved.Add(atom)) continue;
                atom.transform.position = end;
            }
            RefreshCharge();
            foreach (var (atom, _) in ends)
                atom?.RefreshCharge();
            SetupGlobalIgnoreCollisions();
            float snapDist = bondRadius * 2f;
            foreach (var nb in GetDistinctSigmaNeighborAtoms())
            {
                if (nb == null) continue;
                float d = Vector3.Distance(transform.position, nb.transform.position);
                if (d > 1e-4f) { snapDist = d; break; }
            }
            SnapHydrogenSigmaNeighborsToBondOrbitalAxes(snapDist);
        }

        ApplyRigidSigmaCleavagePivotOrbitalTargets(aligned4, rWorld, bondBreakGuideOrbital);
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace($"TryApplyRigidSigmaBreakSubstituentFrame OK applied pivot orbital targets parentId={GetInstanceID()} applyMotion={applySubstituentWorldMotion}");
        return true;
    }

    /// <summary>Bond-break animation targets for pivot orbitals when rigid substituent frame applies (matches <see cref="TryApplyRigidSigmaBreakSubstituentFrame"/> with motion).</summary>
    bool TryComputeRigidSigmaCleavageOrbitalRedistributeSlots(
        Vector3 refBondWorldDirection,
        ElectronOrbitalFunction bondBreakGuideOrbital,
        out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots)
    {
        slots = null;
        if (AtomicNumber != 6 || refBondWorldDirection.sqrMagnitude < 0.01f) return false;
        if (!TryComputeRigidSigmaCleavageWorldRotation(refBondWorldDirection.normalized, out var rWorld, out var aligned4))
            return false;

        var list = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        GetCarbonSigmaCleavageDomains(out var sigmaOrbs, out var nonBondAll);
        ElectronOrbitalFunction pin = bondBreakGuideOrbital != null && nonBondAll.Contains(bondBreakGuideOrbital)
            && bondBreakGuideOrbital.ElectronCount > 0
            ? bondBreakGuideOrbital
            : null;

        if (pin != null)
        {
            var d0 = aligned4[0].normalized;
            var (pp, pr) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(d0, bondRadius);
            list.Add((pin, pp, pr));
        }

        void AddSlotForOrb(ElectronOrbitalFunction orb, bool isPinOrb)
        {
            if (orb == null || isPinOrb) return;
            // Bond-break coroutine only applies redistribute targets to nucleus-parented orbitals; σ on CovalentBond updates in final TrySpread (ApplyRigidSigmaCleavagePivotOrbitalTargets).
            if (orb.transform.parent != transform) return;
            Vector3 tipW = transform.TransformDirection(OrbitalTipLocalDirection(orb)).normalized;
            Vector3 newTipW = (rWorld * tipW).normalized;
            Vector3 newLocal = transform.InverseTransformDirection(newTipW).normalized;
            if (newLocal.sqrMagnitude < 1e-10f) return;
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newLocal, bondRadius, orb.transform.localRotation);
            list.Add((orb, pos, rot));
        }

        foreach (var o in nonBondAll)
        {
            if (o == null || o.ElectronCount <= 0) continue;
            AddSlotForOrb(o, pin != null && o == pin);
        }
        foreach (var o in sigmaOrbs)
        {
            if (o == null || o.ElectronCount <= 0) continue;
            AddSlotForOrb(o, false);
        }

        if (list.Count == 0) return false;
        slots = list;
        return true;
    }

    /// <summary>
    /// Carbon σ-cleavage: carbocation / σN=0 pin / 2σ trigonal, then rigid substituent+tet frame when acyclic, else sparse unified tet (σ + e⁻ non-bond, one permutation; 0e via <see cref="OrientEmptyNonbondedOrbitalsPerpendicularToFramework"/>).
    /// </summary>
    bool TryAssignCarbonBreakIdealFrame(
        Vector3 refBondWorldDirection,
        ElectronOrbitalFunction bondBreakGuideOrbital,
        bool applyRigidSubstituentWorldMotion = true)
    {
        if (AtomicNumber != 6) return false;
        string g = bondBreakGuideOrbital == null
            ? "guide=null"
            : $"guideId={bondBreakGuideOrbital.GetInstanceID()} e={bondBreakGuideOrbital.ElectronCount} onNucleus={bondBreakGuideOrbital.transform.parent == transform}";
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace(
                $"TryAssignCarbonBreakIdealFrame ENTER parentId={GetInstanceID()} σN={GetDistinctSigmaNeighborCount()} refSqr={refBondWorldDirection.sqrMagnitude:F6} applyRigidMotion={applyRigidSubstituentWorldMotion} {g}");

        if (TryApplyCarbocationTrigonalPerpendicularToRefLayout(refBondWorldDirection, bondBreakGuideOrbital, applyRigidSubstituentWorldMotion))
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakIdealFrame → OK branch=CarbocationTrigonalPerpendicularToRefLayout parentId={GetInstanceID()}");
            return true;
        }
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakIdealFrame carbocation trigonal failed, try 2σ trigonal parentId={GetInstanceID()}");

        if (TryApplyTwoSigmaTrigonalPlaneNonbondLayout(refBondWorldDirection, bondBreakGuideOrbital))
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakIdealFrame → OK branch=TwoSigmaTrigonalPlaneNonbondLayout parentId={GetInstanceID()}");
            return true;
        }
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakIdealFrame 2σ trigonal failed, try rigid σ+tet frame parentId={GetInstanceID()}");

        if (TryApplyRigidSigmaBreakSubstituentFrame(refBondWorldDirection, bondBreakGuideOrbital, applyRigidSubstituentWorldMotion))
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakIdealFrame → OK branch=RigidSigmaBreakSubstituentFrame (tet vertices 1..n + R about ref) parentId={GetInstanceID()}");
            return true;
        }
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakIdealFrame rigid frame failed, try unified sparse tet parentId={GetInstanceID()}");

        bool unified = TryAssignCarbonBreakUnifiedSparseTetrahedral(refBondWorldDirection, bondBreakGuideOrbital);
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace(
                unified
                    ? $"TryAssignCarbonBreakIdealFrame → OK branch=UnifiedSparseTetrahedral parentId={GetInstanceID()}"
                    : $"TryAssignCarbonBreakIdealFrame → ALL_FAILED parentId={GetInstanceID()} (unified sparse also false)");
        return unified;
    }

    /// <summary>
    /// σN &lt; 3: spread e⁻ σ lobes (authoritative) + e⁻ non-bonds in one <see cref="FindBestOrbitalToTargetDirsPermutation"/>; ex-bond e⁻ guide pins vertex 0 when in non-bond list.
    /// </summary>
    bool TryAssignCarbonBreakUnifiedSparseTetrahedral(Vector3 refBondWorldDirection, ElectronOrbitalFunction bondBreakGuideOrbital)
    {
        if (AtomicNumber != 6) return false;
        if (GetDistinctSigmaNeighborCount() >= 3)
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace(
                    $"TryAssignCarbonBreakUnifiedSparseTetrahedral SKIP σN>=3 (not used for three σ neighbors) parentId={GetInstanceID()} σN={GetDistinctSigmaNeighborCount()}");
            return false;
        }

        if (DebugLogCcBondBreakSpreadTrace)
            LogCcBondBreakTrace($"unifiedSparsePerm enter parentId={GetInstanceID()} σN={GetDistinctSigmaNeighborCount()}");

        GetCarbonSigmaCleavageDomains(out var sigmaOrbs, out var nonBondAll);
        int nb0e = nonBondAll.Count(o => o != null && o.ElectronCount == 0);
        int nbOcc = nonBondAll.Count(o => o != null && o.ElectronCount > 0);
        if (DebugLogCcBondBreakSpreadTrace)
            LogCcBondBreakTrace(
                $"[bond-break-cc-unified] domains parentId={GetInstanceID()} σAuthOrbs={sigmaOrbs.Count} nonBondTotal={nonBondAll.Count} nbEocc={nbOcc} nb0e={nb0e} σN={GetDistinctSigmaNeighborCount()}");

        var nonBond = nonBondAll;
        if (nonBond.Count < 1 && sigmaOrbs.Count < 1)
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakUnifiedSparseTetrahedral FAIL no non-bond and no σ orbs parentId={GetInstanceID()}");
            return false;
        }

        var movers = new List<ElectronOrbitalFunction>();
        foreach (var o in sigmaOrbs)
            if (o != null && o.ElectronCount > 0) movers.Add(o);
        foreach (var o in nonBond)
            if (o != null && o.ElectronCount > 0) movers.Add(o);
        movers.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));

        int nMax = Mathf.Min(Mathf.Max(movers.Count, 1), GetOrbitalSlotCount());
        if (movers.Count >= 2)
            nMax = Mathf.Min(nMax, movers.Count);

        ElectronOrbitalFunction pin = bondBreakGuideOrbital != null && nonBond.Contains(bondBreakGuideOrbital)
            && bondBreakGuideOrbital.ElectronCount > 0
            ? bondBreakGuideOrbital
            : null;

        List<ElectronOrbitalFunction> subset;
        if (pin != null)
        {
            subset = new List<ElectronOrbitalFunction> { pin };
            foreach (var o in movers)
            {
                if (o == pin) continue;
                if (subset.Count >= nMax) break;
                subset.Add(o);
            }
        }
        else
            subset = movers.Take(Mathf.Min(movers.Count, nMax)).ToList();

        int n = subset.Count;
        if (n < 2)
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakUnifiedSparseTetrahedral FAIL subset n<2 parentId={GetInstanceID()} n={n} movers={movers.Count}");
            return false;
        }

        if (DebugLogCcBondBreakUnifiedSparseDiag)
        {
            string pinWhy;
            if (bondBreakGuideOrbital == null)
                pinWhy = "guideOrb=null";
            else if (!nonBond.Contains(bondBreakGuideOrbital))
                pinWhy = $"guideId={bondBreakGuideOrbital.GetInstanceID()} notInNonBondList e={bondBreakGuideOrbital.ElectronCount}";
            else if (bondBreakGuideOrbital.ElectronCount == 0)
                pinWhy = $"guideId={bondBreakGuideOrbital.GetInstanceID()} is0e_pinDisabledForTetPath";
            else
                pinWhy = $"pinActive pinId={bondBreakGuideOrbital.GetInstanceID()}";
            LogCcBondBreakUnifiedDiag(
                $"pinResolve atom={name} parentId={GetInstanceID()} {pinWhy} nMax={nMax} moversCount={movers.Count} pinExcludedFrom3x3PermLater={(pin != null)}");
            string moverSig = string.Join(",", movers.ConvertAll(o => $"{o.GetInstanceID()}:{o.ElectronCount}"));
            string subSig = string.Join(",", subset.ConvertAll(o => $"{o.GetInstanceID()}:{o.ElectronCount}"));
            LogCcBondBreakUnifiedDiag($"movers parentId={GetInstanceID()} [{moverSig}]");
            LogCcBondBreakUnifiedDiag($"subset parentId={GetInstanceID()} n={n} [{subSig}]");
        }

        if (AtomicNumber == 6 && GetDistinctSigmaNeighborCount() == 0 && n == 4)
        {
            var occSubset = subset.Where(o => o.ElectronCount > 0).ToList();
            var empSubset = subset.Where(o => o.ElectronCount == 0).ToList();
            if (occSubset.Count == 3 && empSubset.Count == 1)
            {
                Vector3 refDef = transform.InverseTransformDirection(refBondWorldDirection.normalized).normalized;
                if (refDef.sqrMagnitude < 1e-8f) refDef = Vector3.right;
                var ideal4d = VseprLayout.GetIdealLocalDirections(4);
                var aligned4d = VseprLayout.AlignFirstDirectionTo(ideal4d, refDef);
                var threeDirsDef = new List<Vector3>
                {
                    aligned4d[1].normalized,
                    aligned4d[2].normalized,
                    aligned4d[3].normalized
                };
                occSubset.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
                if (DebugLogCcBondBreakUnifiedSparseDiag)
                {
                    LogCcBondBreakUnifiedDiag(
                        $"subsetRescue enter parentId={GetInstanceID()} emptyPinnedId={empSubset[0].GetInstanceID()} refDef={FormatLocalDir3(refDef)} tetVerts1to3 only perm3x3");
                    LogUnifiedSparseTipSnapshot("subsetRescue/beforeApply", occSubset);
                }
                var permDef = FindBestOrbitalToTargetDirsPermutation(occSubset, threeDirsDef, bondRadius, this);
                if (permDef != null)
                {
                    if (DebugLogCcBondBreakUnifiedSparseDiag)
                        LogCcBondBreakUnifiedDiag(
                            $"subsetRescue perm=[{string.Join(",", permDef)}] occOrbIds=[{string.Join(",", occSubset.ConvertAll(o => o.GetInstanceID().ToString()))}]");
                    for (int i = 0; i < 3; i++)
                        ApplyCarbonBreakIdealLocalDirection(occSubset[i], threeDirsDef[permDef[i]]);
                    if (DebugLogCcBondBreakUnifiedSparseDiag)
                    {
                        LogUnifiedSparseTipSnapshot("subsetRescue/afterApply", occSubset);
                        LogUnifiedSparseMinTipSeparation("subsetRescue/afterApply", occSubset);
                    }
                    if (DebugLogCcBondBreakGeometry)
                        LogCcBondBreakUnified(
                            $"TrySpread unified σN=0 subset rescue atom={name} parentId={GetInstanceID()}: 3 e⁻ on tet vertices 1–3 (skipped spreadN=4)");
                    if (DebugLogCarbonBreakIdealFrameTrace)
                        LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakUnifiedSparseTetrahedral OK σN=0 subsetRescue parentId={GetInstanceID()}");
                    return true;
                }
                if (DebugLogCcBondBreakSpreadTrace)
                    LogCcBondBreakTrace(
                        $"TrySpread unified σN=0 subsetRescue permDef=null parentId={GetInstanceID()} → full spreadN={n}");
            }
        }

        Vector3 refLocal = transform.InverseTransformDirection(refBondWorldDirection.normalized).normalized;
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        var ideal = VseprLayout.GetIdealLocalDirections(n);
        var aligned = VseprLayout.AlignFirstDirectionTo(ideal, refLocal);
        var newDirList = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
            newDirList.Add(aligned[i].normalized);

        if (DebugLogCcBondBreakUnifiedSparseDiag)
        {
            var dirParts = new List<string>(n);
            for (int i = 0; i < n; i++)
                dirParts.Add($"v{i}={FormatLocalDir3(newDirList[i])}");
            LogCcBondBreakUnifiedDiag(
                $"idealDirs parentId={GetInstanceID()} n={n} refLocal={FormatLocalDir3(refLocal)} {string.Join(" ", dirParts)}");
            LogUnifiedSparseTipSnapshot("fullSpread/beforeApply", subset);
        }

        if (pin != null)
        {
            if (DebugLogCcBondBreakUnifiedSparseDiag)
                LogCcBondBreakUnifiedDiag(
                    $"pinSnapToVertex0 parentId={GetInstanceID()} pinId={pin.GetInstanceID()} dir0={FormatLocalDir3(newDirList[0])} (then 3x3 perm on rest only)");
            ApplyCarbonBreakIdealLocalDirection(pin, newDirList[0], lockTipToHybridDirection: true);
            var rest = subset.Where(o => o != pin).ToList();
            var freeR = new List<Vector3>(n - 1);
            for (int i = 1; i < n; i++)
                freeR.Add(newDirList[i]);
            if (rest.Count != freeR.Count)
            {
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakUnifiedSparseTetrahedral FAIL pin path rest.Count!=freeR parentId={GetInstanceID()}");
                return false;
            }
            var permRest = FindBestOrbitalToTargetDirsPermutation(rest, freeR, bondRadius, this);
            if (permRest == null)
            {
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakUnifiedSparseTetrahedral FAIL permRest=null (pin path) parentId={GetInstanceID()}");
                return false;
            }
            if (DebugLogCcBondBreakUnifiedSparseDiag)
            {
                LogCcBondBreakUnifiedDiag($"perm3x3 parentId={GetInstanceID()} permRest=[{string.Join(",", permRest)}]");
                for (int i = 0; i < rest.Count; i++)
                    LogCcBondBreakUnifiedDiag(
                        $"  rest[{i}] orbId={rest[i].GetInstanceID()} -> slot[{permRest[i]}] dir={FormatLocalDir3(freeR[permRest[i]])}");
            }
            for (int i = 0; i < rest.Count; i++)
                ApplyCarbonBreakIdealLocalDirection(rest[i], freeR[permRest[i]]);
        }
        else
        {
            if (DebugLogCcBondBreakUnifiedSparseDiag)
                LogCcBondBreakUnifiedDiag(
                    $"perm4x4 parentId={GetInstanceID()} pinWasNull — all {n} orbitals compete for vertices v0..v{n - 1} (no reserved ex-bond slot)");
            var permSubset = FindBestOrbitalToTargetDirsPermutation(subset, newDirList, bondRadius, this);
            if (permSubset == null)
            {
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakUnifiedSparseTetrahedral FAIL permSubset=null (no pin) parentId={GetInstanceID()}");
                return false;
            }
            if (DebugLogCcBondBreakUnifiedSparseDiag)
            {
                LogCcBondBreakUnifiedDiag($"perm4x4 parentId={GetInstanceID()} perm=[{string.Join(",", permSubset)}]");
                for (int i = 0; i < n; i++)
                    LogCcBondBreakUnifiedDiag(
                        $"  subset[{i}] orbId={subset[i].GetInstanceID()} -> v{permSubset[i]} dir={FormatLocalDir3(newDirList[permSubset[i]])}");
            }
            for (int i = 0; i < n; i++)
                ApplyCarbonBreakIdealLocalDirection(subset[i], newDirList[permSubset[i]]);
        }

        if (DebugLogCcBondBreakUnifiedSparseDiag)
        {
            LogUnifiedSparseTipSnapshot("fullSpread/afterApply", subset);
            LogUnifiedSparseMinTipSeparation("fullSpread/afterApply", subset);
        }

        if (DebugLogCcBondBreakSpreadTrace)
        {
            string pinInfo = pin == null ? "pin=null" : $"pinId={pin.GetInstanceID()} e={pin.ElectronCount}";
            string subSig = string.Join(",", subset.ConvertAll(o => $"{o.GetInstanceID()}:{o.ElectronCount}"));
            LogCcBondBreakTrace(
                $"[bond-break-cc-unified] FULL spread parentId={GetInstanceID()} {pinInfo} spreadN={n} subset=[{subSig}] (σ+e⁻ non-bond, ex-bond vertex 0)");
        }
        if (DebugLogCcBondBreakGeometry)
            LogCcBondBreakUnified(
                $"TrySpread unified atom={name} parentId={GetInstanceID()} Z=6 spreadN={n} σAuth={sigmaOrbs.Count} σN={GetDistinctSigmaNeighborCount()} (σ + e⁻ non-bond perm)");
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace($"TryAssignCarbonBreakUnifiedSparseTetrahedral OK fullSpread parentId={GetInstanceID()} σN={GetDistinctSigmaNeighborCount()} spreadN={n}");
        return true;
    }

    /// <summary>
    /// σN==0 bare center with <b>empty</b> released ex-bond lobe: pin the 0e guide; three occupied non-bonds laid out via <see cref="TryComputeRepulsionSumNonBondLayoutSlots"/> (repulsion from other tips; other 0e lobes perpendicular to occupied span).
    /// When the released lobe has <b>e⁻</b> (e.g. one end of C–C heterolytic break), do <b>not</b> use this path — <see cref="TryAssignCarbonBreakUnifiedSparseTetrahedral"/> places all four domains tetrahedrally with the guide along ref at vertex 0.
    /// </summary>
    bool TryComputeSigmaNZeroExBondGuideTrigonalBondBreakSlots(
        Vector3 refWorld,
        ElectronOrbitalFunction guide,
        out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots,
        out List<Vector3> idealDirNucleusLocalForApply,
        out List<bool> skipApplyCarbonIdealDir)
    {
        slots = null;
        idealDirNucleusLocalForApply = null;
        skipApplyCarbonIdealDir = null;
        if (GetDistinctSigmaNeighborCount() != 0) return false;
        if (guide == null || !bondedOrbitals.Contains(guide) || guide.Bond != null) return false;
        if (guide.transform.parent != transform) return false;
        if (guide.ElectronCount > 0) return false;
        var nb = bondedOrbitals.Where(o => o != null && o.Bond == null).ToList();
        if (nb.Count != 4 || !nb.Contains(guide)) return false;
        var movers = nb.Where(o => o != guide && o.ElectronCount > 0).ToList();
        if (movers.Count != 3) return false;

        movers.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        var empties = new List<ElectronOrbitalFunction>(3);
        foreach (var o in nb)
        {
            if (o != null && o.ElectronCount == 0 && o != guide)
                empties.Add(o);
        }
        empties.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        var emptyList = new List<ElectronOrbitalFunction>(empties.Count + 1) { guide };
        emptyList.AddRange(empties);
        return TryComputeRepulsionSumNonBondLayoutSlots(movers, emptyList, guide, out slots, out idealDirNucleusLocalForApply, out skipApplyCarbonIdealDir);
    }

    /// <summary>Projects a direction into the plane ⊥ unit normal (electron configuration plane). Normal is typically the pinned 0e guide lobe axis in nucleus-local space.</summary>
    static Vector3 ProjectOntoConfigurationPlane(Vector3 direction, Vector3 planeNormalUnit, Vector3 hintWhenDegenerate)
    {
        Vector3 v = direction - Vector3.Dot(direction, planeNormalUnit) * planeNormalUnit;
        if (v.sqrMagnitude > 1e-10f)
            return v.normalized;
        v = hintWhenDegenerate - Vector3.Dot(hintWhenDegenerate, planeNormalUnit) * planeNormalUnit;
        if (v.sqrMagnitude > 1e-10f)
            return v.normalized;
        Vector3 aux = Mathf.Abs(planeNormalUnit.y) < 0.92f ? Vector3.up : Vector3.right;
        return Vector3.Cross(planeNormalUnit, aux).normalized;
    }

    /// <summary>
    /// Picks a unit vector orthogonal to the span of <paramref name="unitDirs"/> (tip directions). Uses the first non-degenerate cross pair; if all are parallel, returns a perpendicular to the first direction.
    /// </summary>
    static bool TryPickUnitNormalOrthogonalToOccupiedTips(IReadOnlyList<Vector3> unitDirs, out Vector3 normal)
    {
        normal = default;
        if (unitDirs == null || unitDirs.Count == 0) return false;
        for (int i = 0; i < unitDirs.Count; i++)
        {
            Vector3 a = unitDirs[i];
            if (a.sqrMagnitude < 1e-10f) continue;
            a.Normalize();
            for (int j = i + 1; j < unitDirs.Count; j++)
            {
                Vector3 b = unitDirs[j];
                if (b.sqrMagnitude < 1e-10f) continue;
                b.Normalize();
                Vector3 c = Vector3.Cross(a, b);
                if (c.sqrMagnitude > 1e-10f)
                {
                    normal = c.normalized;
                    return true;
                }
            }
        }
        Vector3 u = unitDirs[0];
        if (u.sqrMagnitude < 1e-10f) return false;
        u.Normalize();
        Vector3 up = Mathf.Abs(Vector3.Dot(u, Vector3.up)) < 0.92f ? Vector3.up : Vector3.forward;
        normal = Vector3.Cross(u, up);
        if (normal.sqrMagnitude < 1e-10f)
            normal = Vector3.Cross(u, Vector3.right);
        if (normal.sqrMagnitude < 1e-10f) return false;
        normal.Normalize();
        return true;
    }

    /// <summary>One repulsion-layout empty (not pin): skip when tip already ⊥/opposite framework and separated from other empties; otherwise ±planeNormal chosen to avoid overlapping empty directions.</summary>
    void AppendOneRepulsionSumEmptySlot(
        ElectronOrbitalFunction e,
        bool isPin,
        Vector3 planeNormal,
        IReadOnlyList<Vector3> occFrameworkDirsNormalized,
        List<Vector3> placedEmptyTipDirs,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots,
        List<Vector3> idealDirNucleusLocalForApply,
        List<bool> skipApplyCarbonIdealDir,
        System.Func<ElectronOrbitalFunction, Vector3> tipNucleusLocal,
        IReadOnlyList<Vector3> occupiedLobeAxesMustSeparateFrom = null)
    {
        const float alreadyAlongNormalDot = 0.996f;
        const float emptySepDeg = 18f;
        if (isPin)
        {
            slots.Add((e, e.transform.localPosition, e.transform.localRotation));
            idealDirNucleusLocalForApply.Add(Vector3.zero);
            skipApplyCarbonIdealDir.Add(true);
            return;
        }

        Vector3 rawTip = tipNucleusLocal(e);
        Vector3 pn = planeNormal.sqrMagnitude > 1e-12f ? planeNormal.normalized : Vector3.forward;
        Vector3 eTip = rawTip.sqrMagnitude < 1e-10f ? pn : rawTip.normalized;

        if (occFrameworkDirsNormalized != null && occFrameworkDirsNormalized.Count > 0
            && EmptyTipAlreadyIdealVsElectronFramework(eTip, occFrameworkDirsNormalized, occupiedLobeAxesMustSeparateFrom: occupiedLobeAxesMustSeparateFrom)
            && MinAngleToAnyDirection(eTip, placedEmptyTipDirs) > emptySepDeg)
        {
            slots.Add((e, e.transform.localPosition, e.transform.localRotation));
            idealDirNucleusLocalForApply.Add(Vector3.zero);
            skipApplyCarbonIdealDir.Add(true);
            placedEmptyTipDirs.Add(eTip);
            return;
        }

        Vector3 emptyDir = PickEmptyDirOnAxisAvoidingOtherEmpties(pn, eTip, placedEmptyTipDirs);

        if (Mathf.Abs(Vector3.Dot(eTip, emptyDir)) >= alreadyAlongNormalDot
            && MinAngleToAnyDirection(eTip, placedEmptyTipDirs) > emptySepDeg
            && EmptyTipAlreadyIdealVsElectronFramework(
                eTip,
                occFrameworkDirsNormalized,
                occupiedLobeAxesMustSeparateFrom: occupiedLobeAxesMustSeparateFrom))
        {
            slots.Add((e, e.transform.localPosition, e.transform.localRotation));
            idealDirNucleusLocalForApply.Add(Vector3.zero);
            skipApplyCarbonIdealDir.Add(true);
            placedEmptyTipDirs.Add(eTip);
            return;
        }

        var (ePos, eRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(emptyDir, bondRadius, e.transform.localRotation);
        slots.Add((e, ePos, eRot));
        idealDirNucleusLocalForApply.Add(emptyDir);
        skipApplyCarbonIdealDir.Add(false);
        placedEmptyTipDirs.Add(emptyDir.normalized);
    }

    /// <summary>
    /// Element-agnostic non-bond layout for bond-break / sparse shells: <b>n</b> occupied and <b>m</b> empty non-bond orbitals (n≤6, m≤3) on <b>this</b> nucleus.
    /// <list type="bullet">
    /// <item><b>Configuration plane:</b> when <paramref name="pinEmptyGuideOrbital"/> is set, the plane ⊥ its current lobe tip (+X) — occupied repulsion directions are projected into that plane; other empties align ± that same axis.</item>
    /// <item><b>Occupied:</b> repulsion axis = −normalize( Σⱼ tipⱼ for j≠i ), then (with a pin guide) projected onto the configuration plane via <see cref="ProjectOntoConfigurationPlane"/>.</item>
    /// <item><b>Empty (not the pin):</b> without a pin, plane normal from cross pairs of occupied targets; with a pin, normal = guide tip. Place each non-pinned empty along ±that normal when motion is needed.</item>
    /// <item><b>Pin guide:</b> optional <paramref name="pinEmptyGuideOrbital"/> keeps its current local pose (same contract as carbocation <c>skipApplyCarbonIdealDir</c>).</item>
    /// </list>
    /// <para><b>Integration:</b> <see cref="TryComputeSigmaNZeroExBondGuideTrigonalBondBreakSlots"/> delegates here for σN==0 non-bond-only shells. Full σ cleavage with 3 domains + 0e guide uses <see cref="TryComputeRepulsionSigmaCleavageBondBreakSlots"/> first in <see cref="GetRedistributeTargets3D"/>, then legacy <see cref="TryComputeCarbocationBondBreakSlots"/>.</para>
    /// </summary>
    /// <param name="occupiedNonBond">Non-bond orbitals with electrons (&gt;0).</param>
    /// <param name="emptyNonBond">Non-bond 0e orbitals (includes guide if any).</param>
    /// <param name="pinEmptyGuideOrbital">Optional; must appear in <paramref name="emptyNonBond"/> when set. Pinned empty is skipped for perpendicular placement.</param>
    bool TryComputeRepulsionSumNonBondLayoutSlots(
        IReadOnlyList<ElectronOrbitalFunction> occupiedNonBond,
        IReadOnlyList<ElectronOrbitalFunction> emptyNonBond,
        ElectronOrbitalFunction pinEmptyGuideOrbital,
        out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots,
        out List<Vector3> idealDirNucleusLocalForApply,
        out List<bool> skipApplyCarbonIdealDir)
    {
        const float sumEps = 1e-10f;
        slots = null;
        idealDirNucleusLocalForApply = null;
        skipApplyCarbonIdealDir = null;
        if (occupiedNonBond == null || emptyNonBond == null) return false;
        int nOcc = occupiedNonBond.Count;
        int nEmp = emptyNonBond.Count;
        if (nOcc < 1 || nOcc > 6 || nEmp > 3) return false;
        if (pinEmptyGuideOrbital != null && !emptyNonBond.Contains(pinEmptyGuideOrbital)) return false;
        var occSet = new HashSet<ElectronOrbitalFunction>();
        foreach (var o in occupiedNonBond)
        {
            if (o == null || o.Bond != null || o.transform.parent != transform || !bondedOrbitals.Contains(o) || o.ElectronCount <= 0)
                return false;
            if (!occSet.Add(o)) return false;
        }
        foreach (var e in emptyNonBond)
        {
            if (e == null || e.Bond != null || e.transform.parent != transform || !bondedOrbitals.Contains(e) || e.ElectronCount != 0)
                return false;
            if (occSet.Contains(e)) return false;
        }

        var occTips = new Vector3[nOcc];
        for (int i = 0; i < nOcc; i++)
            occTips[i] = OrbitalTipLocalDirection(occupiedNonBond[i]).normalized;

        var occTargetDirs = new Vector3[nOcc];
        for (int i = 0; i < nOcc; i++)
        {
            Vector3 sumOthers = Vector3.zero;
            for (int j = 0; j < nOcc; j++)
            {
                if (j == i) continue;
                sumOthers += occTips[j];
            }
            if (sumOthers.sqrMagnitude > sumEps)
                occTargetDirs[i] = (-sumOthers.normalized);
            else
                occTargetDirs[i] = occTips[i];
        }

        var planeNormal = Vector3.forward;
        bool guideDefinesPlane = pinEmptyGuideOrbital != null;
        if (guideDefinesPlane)
        {
            planeNormal = OrbitalTipLocalDirection(pinEmptyGuideOrbital);
            if (planeNormal.sqrMagnitude < 1e-10f) planeNormal = Vector3.forward;
            planeNormal.Normalize();
            for (int i = 0; i < nOcc; i++)
                occTargetDirs[i] = ProjectOntoConfigurationPlane(occTargetDirs[i], planeNormal, occTips[i]);
        }

        slots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>(nOcc + nEmp);
        idealDirNucleusLocalForApply = new List<Vector3>(nOcc + nEmp);
        skipApplyCarbonIdealDir = new List<bool>(nOcc + nEmp);

        for (int i = 0; i < nOcc; i++)
        {
            var o = occupiedNonBond[i];
            Vector3 d = occTargetDirs[i];
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(d, bondRadius, o.transform.localRotation);
            slots.Add((o, pos, rot));
            idealDirNucleusLocalForApply.Add(d);
            skipApplyCarbonIdealDir.Add(false);
        }

        if (nEmp == 0)
            return true;

        if (!guideDefinesPlane)
        {
            var occDirsForPlane = new List<Vector3>(nOcc);
            for (int i = 0; i < nOcc; i++)
                occDirsForPlane.Add(occTargetDirs[i].normalized);
            if (!TryPickUnitNormalOrthogonalToOccupiedTips(occDirsForPlane, out planeNormal))
                return false;
        }

        var occFw = new List<Vector3>(nOcc);
        for (int i = 0; i < nOcc; i++)
            occFw.Add(occTargetDirs[i].normalized);
        var occLobeTipsNb = new List<Vector3>(nOcc);
        for (int i = 0; i < nOcc; i++)
        {
            var t = OrbitalTipLocalDirection(occupiedNonBond[i]);
            if (t.sqrMagnitude > 1e-12f) occLobeTipsNb.Add(t.normalized);
        }
        var placedEmptyTipsRepulsion = new List<Vector3>();
        for (int k = 0; k < nEmp; k++)
        {
            var e = emptyNonBond[k];
            AppendOneRepulsionSumEmptySlot(
                e,
                pinEmptyGuideOrbital != null && e == pinEmptyGuideOrbital,
                planeNormal,
                occFw,
                placedEmptyTipsRepulsion,
                slots,
                idealDirNucleusLocalForApply,
                skipApplyCarbonIdealDir,
                OrbitalTipLocalDirection,
                occLobeTipsNb);
        }

        return true;
    }

    /// <summary>σ or non-bond lobe incident on this nucleus with &gt;0 e⁻, eligible for σ-cleavage repulsion targets.</summary>
    bool IsSigmaCleavageRepulsionOccupiedDomain(ElectronOrbitalFunction o)
    {
        if (o == null || o.ElectronCount <= 0) return false;
        if (o.Bond is CovalentBond cb && cb.Orbital == o && cb.IsSigmaBondLine() && (cb.AtomA == this || cb.AtomB == this))
            return true;
        if (o.Bond != null) return false;
        return o.transform.parent == transform && bondedOrbitals.Contains(o);
    }

    /// <summary>Occupied electron-domain lobe on this nucleus (σ, π, or non-bond) for π/σ guide-group repulsion layout.</summary>
    bool IsRepulsionOccupiedDomainForGuideGroupLayout(ElectronOrbitalFunction o)
    {
        if (o == null || o.ElectronCount <= 0) return false;
        if (o.Bond is CovalentBond cb && cb.Orbital == o && (cb.AtomA == this || cb.AtomB == this))
            return true;
        return bondedOrbitals.Contains(o) && o.Bond == null && o.transform.parent == transform;
    }

    /// <summary>
    /// Like <see cref="TryComputeRepulsionSumNonBondLayoutSlots"/> but <paramref name="occupiedDomains"/> may include σ bond host orbitals; tips use <see cref="OrbitalTipDirectionInNucleusLocal"/>. Pinned 0e guide defines the configuration plane ⊥ its lobe axis.
    /// </summary>
    /// <remarks>
    /// Repulsion directions <c>d</c> are in <b>this nucleus</b> local space (same as <see cref="OrbitalTipDirectionInNucleusLocal"/>).
    /// <see cref="GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent"/> converts to the orbital parent’s local space before <see cref="ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection"/> so bond-parented σ get <c>rot</c> consistent with that same world hybrid as lone pairs.
    /// </remarks>
    bool TryComputeRepulsionSumElectronDomainLayoutSlots(
        IReadOnlyList<ElectronOrbitalFunction> occupiedDomains,
        IReadOnlyList<ElectronOrbitalFunction> emptyNonBond,
        ElectronOrbitalFunction pinEmptyGuideOrbital,
        out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots,
        out List<Vector3> idealDirNucleusLocalForApply,
        out List<bool> skipApplyCarbonIdealDir)
    {
        const float sumEps = 1e-10f;
        slots = null;
        idealDirNucleusLocalForApply = null;
        skipApplyCarbonIdealDir = null;
        if (occupiedDomains == null || emptyNonBond == null) return false;
        int nOcc = occupiedDomains.Count;
        int nEmp = emptyNonBond.Count;
        if (nOcc < 1 || nOcc > 6 || nEmp > 3) return false;
        if (pinEmptyGuideOrbital != null && !emptyNonBond.Contains(pinEmptyGuideOrbital)) return false;
        var occSet = new HashSet<ElectronOrbitalFunction>();
        foreach (var o in occupiedDomains)
        {
            if (!IsSigmaCleavageRepulsionOccupiedDomain(o)) return false;
            if (!occSet.Add(o)) return false;
        }
        foreach (var e in emptyNonBond)
        {
            if (e == null || e.Bond != null || e.transform.parent != transform || !bondedOrbitals.Contains(e) || e.ElectronCount != 0)
                return false;
            if (occSet.Contains(e)) return false;
        }

        var occTips = new Vector3[nOcc];
        for (int i = 0; i < nOcc; i++)
            occTips[i] = OrbitalTipDirectionInNucleusLocal(occupiedDomains[i]).normalized;

        var occTargetDirs = new Vector3[nOcc];
        for (int i = 0; i < nOcc; i++)
        {
            Vector3 sumOthers = Vector3.zero;
            for (int j = 0; j < nOcc; j++)
            {
                if (j == i) continue;
                sumOthers += occTips[j];
            }
            if (sumOthers.sqrMagnitude > sumEps)
                occTargetDirs[i] = (-sumOthers.normalized);
            else
                occTargetDirs[i] = occTips[i];
        }

        var planeNormal = Vector3.forward;
        bool guideDefinesPlane = pinEmptyGuideOrbital != null;
        if (guideDefinesPlane)
        {
            planeNormal = OrbitalTipDirectionInNucleusLocal(pinEmptyGuideOrbital);
            if (planeNormal.sqrMagnitude < 1e-10f) planeNormal = Vector3.forward;
            planeNormal.Normalize();
            for (int i = 0; i < nOcc; i++)
                occTargetDirs[i] = ProjectOntoConfigurationPlane(occTargetDirs[i], planeNormal, occTips[i]);
        }

        slots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>(nOcc + nEmp);
        idealDirNucleusLocalForApply = new List<Vector3>(nOcc + nEmp);
        skipApplyCarbonIdealDir = new List<bool>(nOcc + nEmp);

        for (int i = 0; i < nOcc; i++)
        {
            var o = occupiedDomains[i];
            Vector3 d = occTargetDirs[i];
            var (pos, rot) = GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(o, d, bondRadius, o.transform.localRotation);
            slots.Add((o, pos, rot));
            idealDirNucleusLocalForApply.Add(d);
            skipApplyCarbonIdealDir.Add(false);
        }

        if (nEmp == 0)
            return true;

        if (!guideDefinesPlane)
        {
            var occDirsForPlane = new List<Vector3>(nOcc);
            for (int i = 0; i < nOcc; i++)
                occDirsForPlane.Add(occTargetDirs[i].normalized);
            if (!TryPickUnitNormalOrthogonalToOccupiedTips(occDirsForPlane, out planeNormal))
                return false;
        }

        var occFwEd = new List<Vector3>(nOcc);
        for (int i = 0; i < nOcc; i++)
            occFwEd.Add(occTargetDirs[i].normalized);
        var occLobeTipsEd = new List<Vector3>(nOcc);
        for (int i = 0; i < nOcc; i++)
            occLobeTipsEd.Add(occTips[i]);
        var placedEmptyTipsEd = new List<Vector3>();
        for (int k = 0; k < nEmp; k++)
        {
            var e = emptyNonBond[k];
            AppendOneRepulsionSumEmptySlot(
                e,
                pinEmptyGuideOrbital != null && e == pinEmptyGuideOrbital,
                planeNormal,
                occFwEd,
                placedEmptyTipsEd,
                slots,
                idealDirNucleusLocalForApply,
                skipApplyCarbonIdealDir,
                OrbitalTipLocalDirection,
                occLobeTipsEd);
        }

        return true;
    }

    /// <summary>
    /// Default σ-cleavage layout (3 occupied domains + 0e guide): repulsion projected into plane ⊥ guide lobe; extra 0e lobes ± that axis. Falls back to <see cref="TryComputeCarbocationBondBreakSlots"/> when this returns false.
    /// </summary>
    bool TryComputeRepulsionSigmaCleavageBondBreakSlots(
        Vector3 refWorld,
        ElectronOrbitalFunction guide,
        out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots,
        out List<Vector3> idealDirNucleusLocalForApply,
        out List<bool> skipApplyCarbonIdealDir)
    {
        slots = null;
        idealDirNucleusLocalForApply = null;
        skipApplyCarbonIdealDir = null;
        _ = refWorld;
        if (guide == null || !IsBondBreakGuideOrbitalWithBondingCleared(guide)) return false;
        if (!TryGetCarbocationOneEmptyAndThreeOccupiedDomains(guide, out var emptyO, out var occ)) return false;
        if (emptyO == null || emptyO.ElectronCount != 0 || emptyO != guide) return false;
        GetCarbonSigmaCleavageDomains(out _, out var nbAll);
        var emptyList = nbAll.Where(o => o != null && o.ElectronCount == 0).ToList();
        emptyList.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        if (emptyList.Count == 0 || !emptyList.Contains(guide)) return false;
        return TryComputeRepulsionSumElectronDomainLayoutSlots(occ, emptyList, guide, out slots, out idealDirNucleusLocalForApply, out skipApplyCarbonIdealDir);
    }

    /// <summary>
    /// Carbocation trigonal: 1×0e + 3 occupied domains (σ and/or occupied nucleus non-bonds). σN==0: <see cref="TryComputeRepulsionSumNonBondLayoutSlots"/> (any element) with pinned 0e guide. σN&gt;0: carbon only — σ framework + trigonal plane; empty ⊥ plane per ex-bond ref / cross products.
    /// When <paramref name="bondBreakGuideLoneOrbital"/> is set, it must be the current break lobe with σ bonding cleared (<c>Bond == null</c> on that orbital); otherwise returns false.
    /// For σN&gt;0 branches, <c>AtomicNumber == 6</c> is required; σN==0 runs before that gate.
    /// </summary>
    bool TryComputeCarbocationBondBreakSlots(
        Vector3 refWorld,
        ElectronOrbitalFunction bondBreakGuideLoneOrbital,
        out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots,
        out List<Vector3> idealDirNucleusLocalForApply,
        out List<bool> skipApplyCarbonIdealDir)
    {
        slots = null;
        idealDirNucleusLocalForApply = null;
        skipApplyCarbonIdealDir = null;
        if (bondBreakGuideLoneOrbital != null && !IsBondBreakGuideOrbitalWithBondingCleared(bondBreakGuideLoneOrbital))
            return false;
        int sigmaN = GetDistinctSigmaNeighborCount();
        if (sigmaN == 0)
        {
            if (TryComputeSigmaNZeroExBondGuideTrigonalBondBreakSlots(
                    refWorld, bondBreakGuideLoneOrbital, out slots, out idealDirNucleusLocalForApply, out skipApplyCarbonIdealDir))
            {
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace($"TryComputeCarbocationBondBreakSlots OK branch=sigmaN0_exBondGuideRepulsion parentId={GetInstanceID()}");
                return true;
            }
        }
        if (AtomicNumber != 6) return false;

        if (!TryGetCarbocationOneEmptyAndThreeOccupiedDomains(bondBreakGuideLoneOrbital, out var emptyO, out var occ))
        {
            if (DebugLogCcBondBreakSpreadTrace)
            {
                GetCarbonSigmaCleavageDomains(out var sigTrace, out var nbAll);
                int nb0 = nbAll.Count(o => o != null && o.ElectronCount == 0);
                int nbOcc = nbAll.Count(o => o != null && o.ElectronCount > 0);
                int sigOcc = sigTrace.Count(o => o != null && o.ElectronCount > 0);
                string nbSig = string.Join(",", nbAll.ConvertAll(o => $"{o.GetInstanceID()}:{o.ElectronCount}"));
                string g = bondBreakGuideLoneOrbital == null ? "guide=null" : $"guideId={bondBreakGuideLoneOrbital.GetInstanceID()} e={bondBreakGuideLoneOrbital.ElectronCount}";
                LogCcBondBreakTrace(
                    $"TryComputeCarbocationBondBreakSlots skip shell parentId={GetInstanceID()} σN={sigmaN} σOcc={sigOcc} nb0e={nb0} nbOcc={nbOcc} {g} nonBond=[{nbSig}] (post-break: 3σ occ + guide 0e, or legacy 1×0e + 3 occ domains)");
            }
            if (DebugLogCarbonBreakIdealFrameTrace)
            {
                GetCarbonSigmaCleavageDomains(out var sigTr, out var nbAll2);
                int sigOcc2 = sigTr.Count(o => o != null && o.ElectronCount > 0);
                string g2 = bondBreakGuideLoneOrbital == null ? "guide=null" : $"guideId={bondBreakGuideLoneOrbital.GetInstanceID()} e={bondBreakGuideLoneOrbital.ElectronCount}";
                LogCarbonBreakIdealFrameTrace(
                    $"TryComputeCarbocationBondBreakSlots FAIL TryGetCarbocationOneEmptyAndThreeOccupiedDomains parentId={GetInstanceID()} σN={sigmaN} σOcc={sigOcc2} {g2}");
            }
            return false;
        }
        Vector3 refLocal = transform.InverseTransformDirection(refWorld.normalized).normalized;
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        float mergeTol = 360f / (2f * Mathf.Max(GetOrbitalSlotCount(), 2));
        var bondAxes = CollectSigmaBondAxesLocalMerged(mergeTol);

        Vector3 planeNormal;
        bool pinEmptyUnmoved;
        if (sigmaN == 0)
        {
            planeNormal = OrbitalTipLocalDirection(emptyO);
            if (planeNormal.sqrMagnitude < 1e-10f) planeNormal = Vector3.forward;
            planeNormal.Normalize();
            pinEmptyUnmoved = true;
        }
        else
        {
            // σN==3 + carbocation empty site (0e or pre-cleave lobe ≥2e): trigonal σ plane is ⊥ ex-bond ref — same as TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets.
            // Using Cross(σ_i,σ_j) from current σ lobe tips can tilt the plane vs ref and breaks coplanar 120° σ vs neighbor positions.
            // TryGetCarbocationOneEmptyAndThreeOccupiedDomains only yields ≥2e emptyO for sole ex-bond non-bond (guide or guide=null infer).
            bool useExBondRefAsTrigonalNormal = sigmaN == 3 && emptyO != null
                && (emptyO.ElectronCount == 0 || emptyO.ElectronCount >= 2);
            if (useExBondRefAsTrigonalNormal)
            {
                planeNormal = refLocal.normalized;
                Vector3 emptyTip = OrbitalTipLocalDirection(emptyO);
                if (emptyTip.sqrMagnitude > 1e-10f && Vector3.Dot(emptyTip.normalized, planeNormal) < 0f)
                    planeNormal = -planeNormal;
                // 0e ex-bond p orbital: keep the lobe at the break pose only when it is the cleared guide lobe for this break.
                // ≥2e on the ex-bond lobe (heterolytic lone still on that site): re-aim along ±n̂ after σ plane settles.
                pinEmptyUnmoved = emptyO.ElectronCount == 0
                    && (bondBreakGuideLoneOrbital == null
                        || (emptyO == bondBreakGuideLoneOrbital && IsBondBreakGuideOrbitalWithBondingCleared(bondBreakGuideLoneOrbital)));
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace(
                        $"TryComputeCarbocationBondBreakSlots planeNormal=exBondRef (σN=3 carbocation) parentId={GetInstanceID()}");
            }
            else if (!TryComputeCarbocationFrameworkNormalLocal(refLocal, bondAxes, out planeNormal))
            {
                if (DebugLogCcBondBreakSpreadTrace)
                    LogCcBondBreakTrace(
                        $"TryComputeCarbocationBondBreakSlots frameworkNormal fail parentId={GetInstanceID()} σN={sigmaN} bondAxes={bondAxes?.Count ?? 0}");
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace(
                        $"TryComputeCarbocationBondBreakSlots FAIL TryComputeCarbocationFrameworkNormalLocal parentId={GetInstanceID()} σN={sigmaN} bondAxesCount={bondAxes?.Count ?? 0}");
                return false;
            }
            else
            {
                pinEmptyUnmoved = false;
                if (DebugLogCarbonBreakIdealFrameTrace)
                    LogCarbonBreakIdealFrameTrace(
                        $"TryComputeCarbocationBondBreakSlots planeNormal=frameworkCross σN={sigmaN} parentId={GetInstanceID()}");
            }
        }

        var ideal3 = VseprLayout.GetIdealLocalDirections(3);
        Quaternion qPlane = Quaternion.FromToRotation(Vector3.forward, planeNormal);
        var trigDirs = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
            trigDirs.Add((qPlane * ideal3[i]).normalized);

        occ.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        var perm = FindBestOrbitalToTargetDirsPermutation(occ, trigDirs, bondRadius, this);
        if (perm == null)
        {
            if (DebugLogCcBondBreakSpreadTrace)
            {
                string occSig = string.Join(",", occ.ConvertAll(o => $"{o.GetInstanceID()}:{o.ElectronCount}"));
                LogCcBondBreakTrace(
                    $"TryComputeCarbocationBondBreakSlots perm=null parentId={GetInstanceID()} σN={sigmaN} occ=[{occSig}] (occupied vs trigonal dirs in plane)");
            }
            if (DebugLogCarbonBreakIdealFrameTrace)
            {
                string occSig2 = string.Join(",", occ.ConvertAll(o => $"{o.GetInstanceID()}:{o.ElectronCount}"));
                LogCarbonBreakIdealFrameTrace(
                    $"TryComputeCarbocationBondBreakSlots FAIL FindBestOrbitalToTargetDirsPermutation=null parentId={GetInstanceID()} σN={sigmaN} occ=[{occSig2}]");
            }
            return false;
        }
        PreferCarbocationGuideTowardRefLocal(perm, occ, refLocal, trigDirs, bondBreakGuideLoneOrbital);

        slots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        idealDirNucleusLocalForApply = new List<Vector3>(4);
        skipApplyCarbonIdealDir = new List<bool>(4);
        for (int i = 0; i < 3; i++)
        {
            var newDir = trigDirs[perm[i]];
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius, occ[i].transform.localRotation);
            slots.Add((occ[i], pos, rot));
            idealDirNucleusLocalForApply.Add(newDir);
            skipApplyCarbonIdealDir.Add(false);
        }
        if (pinEmptyUnmoved)
        {
            slots.Add((emptyO, emptyO.transform.localPosition, emptyO.transform.localRotation));
            idealDirNucleusLocalForApply.Add(Vector3.zero);
            skipApplyCarbonIdealDir.Add(true);
        }
        else
        {
            Vector3 emptyTip = OrbitalTipLocalDirection(emptyO);
            Vector3 emptyDir = Vector3.Dot(emptyTip, planeNormal) >= 0f ? planeNormal : -planeNormal;
            // No twist minimization: 90° cousins about the axis can leave +X not quite along the plane normal (empty drifts into σ plane in diag |empty·n̂|).
            var (ePos, eRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(emptyDir, bondRadius);
            slots.Add((emptyO, ePos, eRot));
            idealDirNucleusLocalForApply.Add(emptyDir);
            skipApplyCarbonIdealDir.Add(false);
        }
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace(
                $"TryComputeCarbocationBondBreakSlots OK parentId={GetInstanceID()} σN={sigmaN} pinEmptyUnmoved={pinEmptyUnmoved} planeNormal={FormatLocalDir3(planeNormal)} occCount={occ.Count}");
        if (DebugLogCarbocationSigmaRedistributionTrace)
        {
            LogCarbocationSigmaRedistributionTrace(
                $"TryComputeCarbocationBondBreakSlots trigDirs parentId={GetInstanceID()} σN={sigmaN} refLocal={FormatLocalDir3(refLocal)} planeN={FormatLocalDir3(planeNormal)} " +
                $"trig[0]={FormatLocalDir3(trigDirs[0])} trig[1]={FormatLocalDir3(trigDirs[1])} trig[2]={FormatLocalDir3(trigDirs[2])} " +
                $"perm={string.Join(",", perm)} occIds=[{string.Join(",", occ.ConvertAll(o => o.GetInstanceID().ToString()))}]");
        }
        return true;
    }

    /// <summary>
    /// After <see cref="TryApplyCarbocationTrigonalPerpendicularToRefLayout"/> rotates σ lobes into a trigonal plane, move the three σ substituents so C→neighbor axes match those lobe directions
    /// (<see cref="BuildSigmaNeighborTargetsWithFragmentRigidRotation"/>), then callers should <b>re-apply</b> bond σ hybrids so <see cref="CovalentBond.ApplySigmaOrbitalTipFromRedistribution"/> sees updated partner positions.
    /// </summary>
    /// <returns>True when σ neighbors were placed on the trigonal rays (or already there).</returns>
    bool TryApplyCarbocationTrigonalSigmaNeighborRigidAlignment(
        IReadOnlyList<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots,
        IReadOnlyList<Vector3> idealDirNucleusLocal,
        IReadOnlyList<bool> skipApplyIdealDir,
        bool applyRigidSubstituentWorldMotion)
    {
        if (!applyRigidSubstituentWorldMotion) return false;
        if (GetDistinctSigmaNeighborCount() != 3) return false;
        if (slots == null || idealDirNucleusLocal == null || skipApplyIdealDir == null) return false;
        if (slots.Count < 3 || idealDirNucleusLocal.Count < 3 || skipApplyIdealDir.Count < 3) return false;

        var neighborsOrdered = new List<AtomFunction>(3);
        var worldDirs = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
        {
            if (skipApplyIdealDir[i]) return false;
            var orb = slots[i].orb;
            if (orb == null) return false;
            if (!(orb.Bond is CovalentBond cb) || !cb.IsSigmaBondLine() || (cb.AtomA != this && cb.AtomB != this)) return false;
            if (cb.Orbital != orb) return false;
            var nb = cb.AtomA == this ? cb.AtomB : cb.AtomA;
            if (nb == null) return false;
            Vector3 d = nb.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-12f) return false;
            neighborsOrdered.Add(nb);
            worldDirs.Add(d.normalized);
        }

        var newDirs = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
        {
            Vector3 nl = idealDirNucleusLocal[i];
            if (nl.sqrMagnitude < 1e-12f) return false;
            newDirs.Add(transform.TransformDirection(nl.normalized).normalized);
        }

        BuildSigmaNeighborTargetsWithFragmentRigidRotation(
            transform.position, neighborsOrdered, worldDirs, newDirs, this, out var targets, null, null);
        if (targets == null || targets.Count == 0)
        {
            for (int i = 0; i < 3; i++)
            {
                float dist = Vector3.Distance(transform.position, neighborsOrdered[i].transform.position);
                neighborsOrdered[i].transform.position = transform.position + newDirs[i] * dist;
            }
            RefreshCharge();
            foreach (var nb in neighborsOrdered)
                nb?.RefreshCharge();
        }
        else
        {
            var moved = new HashSet<AtomFunction>();
            foreach (var (atom, targetWorld) in targets)
            {
                atom.transform.position = targetWorld;
                moved.Add(atom);
            }
            RefreshCharge();
            foreach (var a in moved)
                a.RefreshCharge();
        }

        SetupGlobalIgnoreCollisions();

        float snapDist = bondRadius * 2f;
        foreach (var nb in GetDistinctSigmaNeighborAtoms())
        {
            if (nb == null) continue;
            float d = Vector3.Distance(transform.position, nb.transform.position);
            if (d > 1e-4f)
            {
                snapDist = d;
                break;
            }
        }
        SnapHydrogenSigmaNeighborsToBondOrbitalAxes(snapDist);
        return true;
    }

    /// <summary>
    /// σN=3 carbocation: align the sole 0e lobe along **±n̂** where n̂ is the normal of the plane spanned by the three σ lobe tips (nucleus-local).
    /// Layout first uses ex-bond ref as plane normal; bond-hosted σ updates can leave the true tip-plane slightly skew vs ref, so the empty can sit nearly **in** the σ plane (<c>|empty·n̂|≪1</c>). This pass runs after σ (and optional H) placement.
    /// Uses <see cref="ApplyCarbonBreakIdealLocalDirection(ElectronOrbitalFunction, Vector3, bool)"/> with <c>lockTipToHybridDirection:true</c> so lobe +X is exactly ±n̂ (twist-minimized placement can leave the empty nearly coplanar with σ).
    /// </summary>
    void SnapCarbocationEmptyPerpendicularToActualSigmaTipPlane(ElectronOrbitalFunction bondBreakGuideLoneOrbital)
    {
        if (AtomicNumber != 6 || GetDistinctSigmaNeighborCount() != 3) return;
        if (!TryGetCarbocationOneEmptyAndThreeOccupiedDomains(bondBreakGuideLoneOrbital, out var emptyO, out _)) return;
        if (emptyO == null || emptyO.transform.parent != transform) return;
        if (emptyO.ElectronCount != 0 && (bondBreakGuideLoneOrbital == null || bondBreakGuideLoneOrbital != emptyO)) return;

        GetCarbonSigmaCleavageDomains(out var sig, out _);
        var tips = new List<Vector3>(3);
        foreach (var o in sig)
        {
            if (o == null || o.ElectronCount <= 0) continue;
            tips.Add(OrbitalTipDirectionInNucleusLocal(o).normalized);
        }
        if (tips.Count != 3) return;

        float tripleVol = Mathf.Abs(Vector3.Dot(tips[0], Vector3.Cross(tips[1], tips[2])));
        if (tripleVol > 0.12f) return;

        Vector3 n = Vector3.Cross(tips[0], tips[1]);
        if (n.sqrMagnitude < 1e-10f) n = Vector3.Cross(tips[0], tips[2]);
        if (n.sqrMagnitude < 1e-10f) n = Vector3.Cross(tips[1], tips[2]);
        if (n.sqrMagnitude < 1e-10f) return;
        n.Normalize();

        Vector3 eTip = OrbitalTipLocalDirection(emptyO);
        if (eTip.sqrMagnitude < 1e-10f) eTip = n;
        else eTip.Normalize();
        Vector3 emptyDir = Vector3.Dot(eTip, n) >= 0f ? n : -n;
        ApplyCarbonBreakIdealLocalDirection(emptyO, emptyDir, lockTipToHybridDirection: true);
    }

    bool TryApplyCarbocationTrigonalPerpendicularToRefLayout(
        Vector3 refWorld,
        ElectronOrbitalFunction bondBreakGuideLoneOrbital = null,
        bool applyRigidSubstituentWorldMotion = true)
    {
        bool okSlots = TryComputeRepulsionSigmaCleavageBondBreakSlots(
            refWorld, bondBreakGuideLoneOrbital, out var slots, out var idealDirs, out var skipIdeal);
        if (!okSlots)
            okSlots = TryComputeCarbocationBondBreakSlots(
                refWorld, bondBreakGuideLoneOrbital, out slots, out idealDirs, out skipIdeal);
        if (!okSlots
            || slots == null || idealDirs == null || skipIdeal == null
            || idealDirs.Count != slots.Count || skipIdeal.Count != slots.Count)
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace(
                    $"TryApplyCarbocationTrigonalPerpendicularToRefLayout FAIL slots={slots != null} idealDirs={idealDirs != null} skipIdeal={skipIdeal != null} counts slots={slots?.Count ?? -1} ideal={idealDirs?.Count ?? -1} skip={skipIdeal?.Count ?? -1} parentId={GetInstanceID()}");
            return false;
        }
        const float posEps = 1e-3f;
        const float rotEpsDeg = 1.2f;
        for (int i = 0; i < slots.Count; i++)
        {
            var (orb, pos, rot) = slots[i];
            if (orb == null) continue;
            if (!skipIdeal[i])
            {
                if (DebugLogCarbocationSigmaRedistributionTrace)
                {
                    bool bondSig = orb.Bond is CovalentBond cbb && cbb.Orbital == orb && cbb.IsSigmaBondLine()
                        && (cbb.AtomA == this || cbb.AtomB == this);
                    Vector3 idL = idealDirs[i];
                    Vector3 idW = transform.TransformDirection(idL);
                    LogCarbocationSigmaRedistributionTrace(
                        $"TryApplyCarbocationTrigonal slot i={i} parentId={GetInstanceID()} orbId={orb.GetInstanceID()} bondHostedσ={bondSig} " +
                        $"idealLocal={FormatLocalDir3(idL)} idealWorld={FormatLocalDir3(idW)}");
                }
                bool lockEmptyTip = orb.Bond == null && orb.ElectronCount == 0;
                ApplyCarbonBreakIdealLocalDirection(orb, idealDirs[i], lockEmptyTip);
                continue;
            }
            if (Vector3.Distance(orb.transform.localPosition, pos) < posEps && Quaternion.Angle(orb.transform.localRotation, rot) < rotEpsDeg)
                continue;
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
        }
        bool substPlaced = TryApplyCarbocationTrigonalSigmaNeighborRigidAlignment(slots, idealDirs, skipIdeal, applyRigidSubstituentWorldMotion);
        // Bond σ hybrids were aimed while H were still on old sites; re-apply so ApplySigmaOrbitalTipFromRedistribution uses new C→neighbor geometry.
        if (substPlaced)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                if (skipIdeal[i]) continue;
                var orb = slots[i].orb;
                if (orb == null) continue;
                bool lockEmptyTip = orb.Bond == null && orb.ElectronCount == 0;
                ApplyCarbonBreakIdealLocalDirection(orb, idealDirs[i], lockEmptyTip);
            }
        }
        SnapCarbocationEmptyPerpendicularToActualSigmaTipPlane(bondBreakGuideLoneOrbital);
        if (DebugLogCarbonBreakIdealFrameTrace)
            LogCarbonBreakIdealFrameTrace($"TryApplyCarbocationTrigonalPerpendicularToRefLayout OK applied parentId={GetInstanceID()} slotCount={slots.Count}");
        if (DebugLogCarbocationTrigonalPlanarityDiag)
        {
            Vector3 refL = transform.InverseTransformDirection(refWorld.normalized).normalized;
            if (refL.sqrMagnitude < 1e-8f) refL = Vector3.right;
            if (!applyRigidSubstituentWorldMotion && GetDistinctSigmaNeighborCount() == 3)
                LogCarbocationPlanarityDiagLine(
                    $"TryApplyCarbocationTrigonal/after CARBOC_SUBST_RIGID_SKIPPED applyRigidSubstituentWorldMotion=false parentId={GetInstanceID()} (σ/H may stay tetrahedral until anim ends)");
            LogCarbocationTrigonalSigmaPlanarityDiagnostics("TryApplyCarbocationTrigonal/after", refL, substPlaced);
        }
        return true;
    }

    static ElectronOrbitalFunction ResolveTwoSigmaTrigonalPrimaryEmptyOrbital(AtomFunction atom, ElectronOrbitalFunction bondBreakGuideOrbital)
    {
        if (atom == null) return null;
        if (bondBreakGuideOrbital != null && bondBreakGuideOrbital.transform.parent == atom.transform
            && bondBreakGuideOrbital.Bond == null && bondBreakGuideOrbital.ElectronCount == 0)
            return bondBreakGuideOrbital;
        return atom.bondedOrbitals.FirstOrDefault(o => o != null && o.Bond == null && o.ElectronCount == 0);
    }

    /// <summary>2σ trigonal electron geometry: σ + occupied non-bonds in plane ⊥ ref; 0e lobes along ref (primary = guide when 0e). Preview / <see cref="GetRedistributeTargets3D"/>.</summary>
    bool TryComputeTwoSigmaTrigonalBondBreakSlots(Vector3 refWorld, out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots, ElectronOrbitalFunction bondBreakGuideOrbital = null)
    {
        slots = null;
        if (GetDistinctSigmaNeighborCount() != 2 || !IsBondBreakTrigonalPlanarFrameworkCase()) return false;
        Vector3 refLocal = transform.InverseTransformDirection(refWorld.normalized).normalized;
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        GetNonBondShellClassCounts(out int c0, out _, out _);
        bool hasEmpty = c0 >= 1;
        var emptyOrb = hasEmpty ? ResolveTwoSigmaTrigonalPrimaryEmptyOrbital(this, bondBreakGuideOrbital) : null;
        if (hasEmpty && emptyOrb == null) return false;

        var ideal3 = VseprLayout.GetIdealLocalDirections(3);
        Quaternion qPlane = Quaternion.FromToRotation(Vector3.forward, refLocal);
        var trigDirs = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
            trigDirs.Add((qPlane * ideal3[i]).normalized);

        slots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        if (hasEmpty && emptyOrb != null)
        {
            var (ePos, eRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(refLocal, bondRadius, emptyOrb.transform.localRotation);
            slots.Add((emptyOrb, ePos, eRot));
        }

        var occupiedNb = new List<ElectronOrbitalFunction>();
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null) continue;
            if (o == emptyOrb) continue;
            if (o.ElectronCount > 0) occupiedNb.Add(o);
        }
        occupiedNb.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));

        if (occupiedNb.Count == 0)
            return true;

        var sigmaNeighbors = GetDistinctSigmaNeighborAtoms();
        if (sigmaNeighbors.Count != 2)
        {
            slots = null;
            return false;
        }

        var sigmaInPlane = new List<Vector3>(2);
        foreach (var n in sigmaNeighbors)
        {
            Vector3 dw = n.transform.position - transform.position;
            if (dw.sqrMagnitude < 1e-10f)
            {
                slots = null;
                return false;
            }
            Vector3 dl = transform.InverseTransformDirection(dw.normalized).normalized;
            float along = Vector3.Dot(dl, refLocal);
            Vector3 inPlane = dl - along * refLocal;
            if (inPlane.sqrMagnitude < 1e-8f)
            {
                slots = null;
                return false;
            }
            sigmaInPlane.Add(inPlane.normalized);
        }

        float bestSum = float.MaxValue;
        int bi = -1, bj = -1;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (j == i) continue;
                float sum = Vector3.Angle(sigmaInPlane[0], trigDirs[i]) + Vector3.Angle(sigmaInPlane[1], trigDirs[j]);
                if (sum < bestSum - 1e-4f)
                {
                    bestSum = sum;
                    bi = i;
                    bj = j;
                }
            }
        }
        if (bi < 0)
        {
            slots = null;
            return false;
        }
        int freeK = -1;
        for (int k = 0; k < 3; k++)
        {
            if (k != bi && k != bj)
            {
                freeK = k;
                break;
            }
        }
        if (freeK < 0)
        {
            slots = null;
            return false;
        }

        var freeTrig = new List<Vector3> { trigDirs[freeK] };
        if (occupiedNb.Count > freeTrig.Count)
        {
            slots = null;
            return false;
        }

        var permOcc = FindBestOrbitalToTargetDirsPermutation(occupiedNb, freeTrig, bondRadius);
        if (permOcc == null)
        {
            slots = null;
            return false;
        }
        for (int i = 0; i < occupiedNb.Count; i++)
        {
            var newDir = freeTrig[permOcc[i]];
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius, occupiedNb[i].transform.localRotation);
            slots.Add((occupiedNb[i], pos, rot));
        }
        return true;
    }

    /// <summary>
    /// 2σ trigonal: plane ⊥ ref contains σ axes + lone; 0e along ref; occupied non-bonds on the free in-plane vertex vs σ mapping.
    /// </summary>
    bool TryApplyTwoSigmaTrigonalPlaneNonbondLayout(Vector3 refWorld, ElectronOrbitalFunction bondBreakGuideOrbital)
    {
        if (!TryComputeTwoSigmaTrigonalBondBreakSlots(refWorld, out var slots, bondBreakGuideOrbital) || slots == null) return false;
        const float posEps = 1e-3f;
        const float rotEpsDeg = 1.2f;
        foreach (var (orb, pos, rot) in slots)
        {
            if (orb == null) continue;
            if (Vector3.Distance(orb.transform.localPosition, pos) < posEps && Quaternion.Angle(orb.transform.localRotation, rot) < rotEpsDeg)
                continue;
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
        }
        if (DebugLogCcBondBreakGeometry)
            LogCcBondBreak($"TrySpreadSparseSigma atom={name} parentId={GetInstanceID()}: 2σ trigonal sp² — electron plane ⊥ ref, 0e along ref when present");
        return true;
    }

    /// <summary>
    /// Full σ cleavage when lobes need spreading. <b>Carbon (Z=6):</b> <see cref="TryAssignCarbonBreakIdealFrame"/> — carbocation / σN=0 ex-bond pin / 2σ trigonal / unified sparse tet with <b>one</b> permutation over σ (authoritative) + e⁻ non-bonds (<see cref="TryAssignCarbonBreakUnifiedSparseTetrahedral"/>). Non-carbon: legacy non-bond-only tet after the same carbocation/two-σ attempts.
    /// <see cref="TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets"/> does nothing for C₂; TrySpread + VSEPR avoid stacked lobes. Ex-bond axis = tet vertex 0 when applicable; 0e via <see cref="OrientEmptyNonbondedOrbitalsPerpendicularToFramework"/>.
    /// <paramref name="applyRigidSubstituentWorldMotion"/>: when false (σ relax already applied same atom ends), rigid frame only updates pivot hybrid lobes.
    /// </summary>
    void TrySpreadNonbondOrbitalsForBondBreakSparseSigmaNeighbors(
        Vector3 refBondWorldDirection,
        ElectronOrbitalFunction bondBreakGuideOrbital,
        bool applyRigidSubstituentWorldMotion = true)
    {
        if (DebugLogCcBondBreakSpreadTrace)
        {
            var nbTrace = bondedOrbitals.Where(o => o != null && o.Bond == null).ToList();
            nbTrace.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
            string nbSig = string.Join(",", nbTrace.ConvertAll(o => $"{o.GetInstanceID()}:{o.ElectronCount}"));
            string g = "guide=null";
            if (bondBreakGuideOrbital != null)
            {
                Transform gp = bondBreakGuideOrbital.transform.parent;
                string pKind = gp == null ? "noParent" : gp == transform ? "onAtom" : gp.GetComponent<CovalentBond>() != null ? "onBond" : "other";
                g = $"guideId={bondBreakGuideOrbital.GetInstanceID()} e={bondBreakGuideOrbital.ElectronCount} parent={pKind} inNbList={nbTrace.Contains(bondBreakGuideOrbital)}";
            }
            bool carbOk = TryGetCarbocationOneEmptyAndThreeOccupiedDomains(bondBreakGuideOrbital, out _, out _);
            LogCcBondBreakTrace(
                $"TrySpread ENTER parentId={GetInstanceID()} Z={atomicNumber} σN={GetDistinctSigmaNeighborCount()} carb3dom1empty={carbOk} {g} nonBond=[{nbSig}]");
        }

        // Carbon: one pipeline (carbocation / σN=0 pin / 2σ trigonal / rigid substituent+tet / unified σ+e⁻ tet). Non-carbon: legacy two-σ + non-bond-only tet.
        if (TryAssignCarbonBreakIdealFrame(refBondWorldDirection, bondBreakGuideOrbital, applyRigidSubstituentWorldMotion))
        {
            if (DebugLogCcBondBreakGeometry && AtomicNumber == 6)
                LogCcBondBreakUnified(
                    $"TrySpread atom={name} parentId={GetInstanceID()} σN={GetDistinctSigmaNeighborCount()} — TryAssignCarbonBreakIdealFrame applied");
            return;
        }
        if (GetDistinctSigmaNeighborCount() >= 3) return;

        if (AtomicNumber != 6)
        {
            if (TryApplyCarbocationTrigonalPerpendicularToRefLayout(refBondWorldDirection, bondBreakGuideOrbital, applyRigidSubstituentWorldMotion))
                return;
            if (TryApplyTwoSigmaTrigonalPlaneNonbondLayout(refBondWorldDirection, bondBreakGuideOrbital))
                return;
        }
        var nonBond = bondedOrbitals.Where(o => o != null && o.Bond == null).ToList();
        if (nonBond.Count < 2) return;
        nonBond.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        int nMax = Mathf.Min(nonBond.Count, GetOrbitalSlotCount());
        if (nMax < 2) return;

        ElectronOrbitalFunction pin = bondBreakGuideOrbital != null && nonBond.Contains(bondBreakGuideOrbital)
            && bondBreakGuideOrbital.ElectronCount > 0
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

        // If pin was null, avoid spreadN=4 permuting a 1×0e+3×e⁻ σN==0 carbon shell (same shell as carbocation / TryApply above).
        if (AtomicNumber == 6 && GetDistinctSigmaNeighborCount() == 0 && n == 4)
        {
            var occSubset = subset.Where(o => o.ElectronCount > 0).ToList();
            var empSubset = subset.Where(o => o.ElectronCount == 0).ToList();
            if (occSubset.Count == 3 && empSubset.Count == 1)
            {
                Vector3 refDef = transform.InverseTransformDirection(refBondWorldDirection.normalized).normalized;
                if (refDef.sqrMagnitude < 1e-8f) refDef = Vector3.right;
                var ideal4d = VseprLayout.GetIdealLocalDirections(4);
                var aligned4d = VseprLayout.AlignFirstDirectionTo(ideal4d, refDef);
                var threeDirsDef = new List<Vector3>
                {
                    aligned4d[1].normalized,
                    aligned4d[2].normalized,
                    aligned4d[3].normalized
                };
                occSubset.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
                var permDef = FindBestOrbitalToTargetDirsPermutation(occSubset, threeDirsDef, bondRadius);
                if (permDef != null)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var newDir = threeDirsDef[permDef[i]];
                        var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius, occSubset[i].transform.localRotation);
                        occSubset[i].transform.localPosition = pos;
                        occSubset[i].transform.localRotation = rot;
                    }
                    if (DebugLogCcBondBreakGeometry)
                        LogCcBondBreak(
                            $"TrySpreadSparseSigma atom={name} parentId={GetInstanceID()} Z=6 σN=0: subset rescue — 3 e⁻ on tet vertices 1–3, skipped spreadN=4");
                    return;
                }
                if (DebugLogCcBondBreakSpreadTrace)
                    LogCcBondBreakTrace(
                        $"TrySpread σN=0 subsetRescue permDef=null parentId={GetInstanceID()} → will try full spreadN=4 perm");
            }
        }

        Vector3 refLocal = transform.InverseTransformDirection(refBondWorldDirection.normalized).normalized;
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        var ideal = VseprLayout.GetIdealLocalDirections(n);
        var aligned = VseprLayout.AlignFirstDirectionTo(ideal, refLocal);
        var newDirList = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
            newDirList.Add(aligned[i].normalized);

        if (pin != null)
        {
            var (pPos, pRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDirList[0], bondRadius, pin.transform.localRotation);
            pin.transform.localPosition = pPos;
            pin.transform.localRotation = pRot;
            var rest = subset.Where(o => o != pin).ToList();
            var freeR = new List<Vector3>(n - 1);
            for (int i = 1; i < n; i++)
                freeR.Add(newDirList[i]);
            if (rest.Count != freeR.Count) return;
            var permRest = FindBestOrbitalToTargetDirsPermutation(rest, freeR, bondRadius);
            if (permRest == null) return;
            for (int i = 0; i < rest.Count; i++)
            {
                var newDir = freeR[permRest[i]];
                var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius, rest[i].transform.localRotation);
                rest[i].transform.localPosition = pos;
                rest[i].transform.localRotation = rot;
            }
        }
        else
        {
            var permSubset = FindBestOrbitalToTargetDirsPermutation(subset, newDirList, bondRadius);
            if (permSubset == null) return;
            for (int i = 0; i < n; i++)
            {
                var newDir = newDirList[permSubset[i]];
                var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius, subset[i].transform.localRotation);
                subset[i].transform.localPosition = pos;
                subset[i].transform.localRotation = rot;
            }
        }

        if (DebugLogCcBondBreakSpreadTrace)
        {
            string pinInfo = pin == null ? "pin=null" : $"pinId={pin.GetInstanceID()} e={pin.ElectronCount}";
            string subSig = string.Join(",", subset.ConvertAll(o => $"{o.GetInstanceID()}:{o.ElectronCount}"));
            LogCcBondBreakTrace(
                $"TrySpread FULL_TET parentId={GetInstanceID()} {pinInfo} spreadN={n} subset=[{subSig}] (ex-bond vertex 0)");
        }
        if (DebugLogCcBondBreakGeometry)
            LogCcBondBreak(
                $"TrySpreadSparseSigma atom={name} parentId={GetInstanceID()} Z={atomicNumber} nonBondCount={nonBond.Count} spreadN={n} σNeighbors={GetDistinctSigmaNeighborCount()} (ex-bond axis = vertex 0)");
    }

    /// <summary>
    /// After a second π forms (triple): AX₂ centers (two σ neighbors, no lone lobes on this atom) should be linear (180°).
    /// Trigonal σ-relax only handles three σ neighbors; coplanar trigonal returns false, so double→triple left sp² σ angles unchanged without this.
    /// </summary>
    public bool TryComputeLinearSigmaNeighborRelaxTargets(Vector3 refLocalNormalized,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets,
        HashSet<AtomFunction> pinWorld = null,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
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
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld, freezeSigmaNeighborSubtreeRoot);
        return targets != null && targets.Count > 0;
    }

    /// <summary>
    /// After breaking a π (triple→double etc.): AX₂ centers that were linear (sp) for π≥2 should open σ angles toward trigonal (~120°).
    /// Skips symmetric X–A–X (same heavy element both sides, e.g. CO₂ σ framework) so linear σ is preserved there.
    /// </summary>
    public bool TryComputeOpenedTrigonalSigmaNeighborRelaxTargetsFromLinear(Vector3 refLocalNormalized,
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets,
        HashSet<AtomFunction> pinWorld = null,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
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
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld, freezeSigmaNeighborSubtreeRoot);
        return targets != null && targets.Count > 0;
    }

    void TryRelaxSigmaNeighborsToLinear3D(Vector3 refLocalNormalized, HashSet<AtomFunction> pinWorld = null, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (!TryComputeLinearSigmaNeighborRelaxTargets(refLocalNormalized, out var targets, pinWorld, freezeSigmaNeighborSubtreeRoot) || targets == null) return;

        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, targetWorld) in targets)
        {
            if (DebugLogFrameworkPinSigmaRelaxTrace)
            {
                float d = Vector3.Distance(atom.transform.position, targetWorld);
                if (d > 1e-4f)
                    LogFrameworkPinSigmaRelax(
                        $"TryRelaxLinearΣ center={FormatAtomBrief(this)} APPLY {FormatAtomBrief(atom)} Δ={d:F5}");
            }
            atom.transform.position = targetWorld;
            moved.Add(atom);
        }

        RefreshCharge();
        foreach (var a in moved)
            a.RefreshCharge();
        SetupGlobalIgnoreCollisions();
    }

    void TryRelaxSigmaNeighborsOpenedFromLinear3D(Vector3 refLocalNormalized, HashSet<AtomFunction> pinWorld = null, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (!TryComputeOpenedTrigonalSigmaNeighborRelaxTargetsFromLinear(refLocalNormalized, out var targets, pinWorld, freezeSigmaNeighborSubtreeRoot) || targets == null) return;

        var moved = new HashSet<AtomFunction>();
        foreach (var (atom, targetWorld) in targets)
        {
            if (DebugLogFrameworkPinSigmaRelaxTrace)
            {
                float d = Vector3.Distance(atom.transform.position, targetWorld);
                if (d > 1e-4f)
                    LogFrameworkPinSigmaRelax(
                        $"TryRelaxOpenedFromLinear center={FormatAtomBrief(this)} APPLY {FormatAtomBrief(atom)} Δ={d:F5}");
            }
            atom.transform.position = targetWorld;
            moved.Add(atom);
        }

        RefreshCharge();
        foreach (var a in moved)
            a.RefreshCharge();
        SetupGlobalIgnoreCollisions();
    }

    void RedistributeOrbitals3DOld(float? piBondAngleOverride, Vector3? refBondWorldDirection, bool relaxCoplanarSigmaToTetrahedral = false, bool skipLoneLobeLayout = false, HashSet<AtomFunction> pinAtomsForSigmaRelax = null, bool skipSigmaNeighborRelax = false, ElectronOrbitalFunction bondBreakGuideLoneOrbital = null, AtomFunction newSigmaBondPartnerHint = null, int sigmaNeighborCountBeforeHint = -1, bool skipBondBreakSparseNonbondSpread = false, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1)
        {
            LogVsepr3D($"skip atom={name} Z={atomicNumber}: maxSlots<=1");
            if (DebugLogReplaceHRedistribute)
                LogReplaceHRedistribute("Redistribute3D early exit maxSlots<=1 | " + FormatAtomBrief(this) + $" Z={atomicNumber}");
            return;
        }

        LogReplaceHRedistributeOrbitalSnapshot("Redistribute3D enter");

        if (DebugLogFrameworkPinSigmaRelaxTrace)
        {
            LogFrameworkPinSigmaRelax(
                "RedistributeOrbitals3D enter center=" + FormatAtomBrief(this)
                + $" σN={GetDistinctSigmaNeighborCount()} π={GetPiBondCount()}"
                + $" skipΣRelax={skipSigmaNeighborRelax} coplanarTet={relaxCoplanarSigmaToTetrahedral} skipLone={skipLoneLobeLayout}"
                + " " + FormatPinSummary(pinAtomsForSigmaRelax)
                + $" partnerHint={(newSigmaBondPartnerHint == null ? "null" : FormatAtomBrief(newSigmaBondPartnerHint))}"
                + $" σBeforeHint={sigmaNeighborCountBeforeHint}");
        }

        if (!skipSigmaNeighborRelax)
            MaybeApplyTetrahedralSigmaRelaxForBondFormation(newSigmaBondPartnerHint, sigmaNeighborCountBeforeHint, pinAtomsForSigmaRelax, freezeSigmaNeighborSubtreeRoot);

        LogReplaceHRedistributeOrbitalSnapshot("Redistribute3D after MaybeApplyTetΣRelax");

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
                TryRelaxCoplanarSigmaNeighborsToTetrahedral3D(refLocal, pinAtomsForSigmaRelax, freezeSigmaNeighborSubtreeRoot);
                // Carbocation (3σ or 2σ + empty along ref): generic trigonal relax aligns one σ vertex onto refLocal — same axis as the empty p — so skip it; TryApplySp2 places σ neighbors in the trigonal plane.
                if (!IsSp2BondBreakEmptyAlongRefCase() && !IsBondBreakTrigonalPlanarFrameworkCase())
                    TryRelaxSigmaNeighborsToTrigonalPlanar3D(refLocal, null, freezeSigmaNeighborSubtreeRoot);
                TryRelaxSigmaNeighborsToLinear3D(refLocal, pinAtomsForSigmaRelax, freezeSigmaNeighborSubtreeRoot);
                TryRelaxSigmaNeighborsOpenedFromLinear3D(refLocal, pinAtomsForSigmaRelax, freezeSigmaNeighborSubtreeRoot);
            }
            else
            {
                TryRelaxSigmaNeighborsToTrigonalPlanar3D(refLocal, pinAtomsForSigmaRelax, freezeSigmaNeighborSubtreeRoot);
                TryRelaxSigmaNeighborsToLinear3D(refLocal, pinAtomsForSigmaRelax, freezeSigmaNeighborSubtreeRoot);
            }
        }

        // σ-only bond break (3σ + radical/empty shell, no 2e lone pairs on non-bonds): tetrahedral / trigonal-carbocation σ relax (CH₃–CH₃ style).
        // When skipSigmaNeighborRelax=true, animation or instant break already applied the same targets via BuildSigmaRelaxMoves — running
        // TryApplySp2 again remaps σ→ideal (FindBestDirectionMapping) and teleports H while guide lobes stay put (looks like the ex-bond lobe flipped vs σ).
        if (relaxCoplanarSigmaToTetrahedral && !skipSigmaNeighborRelax)
            TryApplySp2BondBreakTrigonalPlanarSigmaNeighborRelax3D(refBondWorldDirection, pinAtomsForSigmaRelax, freezeSigmaNeighborSubtreeRoot);

        // Bare C₂ (etc.): 0 σ neighbors — TryApplySp2 never runs; VSEPR may skip (no occupied lone lobes). Carbon: TrySpread uses TryAssignCarbonBreakIdealFrame (σ + e⁻ non-bond unified perm when sparse).
        // ·CH₂ radical (2 σ): σ targets from TryApplySp2 — skip tetrahedral TrySpread (teleports guide/empty vs animation). CH₂⁺ (2 σ carbocation) still runs TrySpread (trigonal / unified).
        // After bond-break coroutine lerps, skip TrySpread — FindBestDirectionMapping can pick a different equivalent vertex than GetRedistributeTargets (visible teleport).
        if (relaxCoplanarSigmaToTetrahedral && refBondWorldDirection.HasValue && refBondWorldDirection.Value.sqrMagnitude >= 0.01f && !skipBondBreakSparseNonbondSpread)
        {
            bool skipSparseForTwoSigmaBreak = GetDistinctSigmaNeighborCount() == 2
                && HasNonBondShellForSp2BondBreakSigmaRelax()
                && !(GetDistinctSigmaNeighborCount() == 2 && IsBondBreakTrigonalPlanarFrameworkCase());
            if (DebugLogCcBondBreakSpreadTrace)
            {
                string g = bondBreakGuideLoneOrbital == null
                    ? "guide=null"
                    : $"guideId={bondBreakGuideLoneOrbital.GetInstanceID()}";
                LogCcBondBreakTrace(
                    $"Redistribute3D→TrySpread parentId={GetInstanceID()} skipBondBreakSparseNonbondSpread={skipBondBreakSparseNonbondSpread} skipSparseFor2σBreak={skipSparseForTwoSigmaBreak} skipLoneLobeLayout={skipLoneLobeLayout} {g}");
            }
            if (!skipSparseForTwoSigmaBreak)
                TrySpreadNonbondOrbitalsForBondBreakSparseSigmaNeighbors(
                    refBondWorldDirection.Value,
                    bondBreakGuideLoneOrbital,
                    applyRigidSubstituentWorldMotion: !skipSigmaNeighborRelax);
        }

        if (skipLoneLobeLayout)
        {
            LogVsepr3D($"skip lone layout atom={name} Z={atomicNumber} skipLoneLobeLayout=true");
            if (DebugLogCcBondBreakGeometry && relaxCoplanarSigmaToTetrahedral && IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(bondBreakGuideLoneOrbital))
                LogCcBondBreak($"Redistribute3D skipLoneLobeLayout=true atom={name} — carbocation uses σ-relax + TrySpread; OrientEmpty places 0e lobe");
            LogCcBondBreakGeometryDiagnostics("afterRedistribute3D-skipLoneLobeLayout");
            return;
        }

        // Electron domains: σ axes + occupied non-bonded lobes only (0e placeholders are positioned separately).
        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null && orb.ElectronCount > 0).ToList();
        if (loneOrbitals.Count == 0)
        {
            if (DebugLogReplaceHRedistribute)
            {
                int nLone0e = bondedOrbitals.Count(o => o != null && o.Bond == null && o.ElectronCount == 0);
                LogReplaceHRedistribute(
                    "VSEPR lone layout SKIPPED (no occupied lone lobes) | " + FormatAtomBrief(this)
                    + $" Z={atomicNumber} nonbondLone0eCount={nLone0e} — occupied-lone-only path below may still place σ tips / OrientEmpty");
            }
            LogVsepr3D($"skip atom={name} Z={atomicNumber}: no occupied lone orbitals");
            if (DebugLogCcBondBreakGeometry && relaxCoplanarSigmaToTetrahedral && IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(bondBreakGuideLoneOrbital))
                LogCcBondBreak($"Redistribute3D no occupied lone lobes atom={name} — VSEPR lone layout skipped; carbocation σ-break uses TryApplySp2 + TrySpread + OrientEmpty (not this block)");
            LogCcBondBreakGeometryDiagnostics("afterRedistribute3D-noOccupiedLone");
            if (!skipLoneLobeLayout)
            {
                var bondAxesOnly = CollectSigmaBondAxesLocalMerged(mergeToleranceDeg);
                if (bondAxesOnly.Count >= 2)
                {
                    ClearSigmaBondOrbitalRedistributionDeltaWhereAuthoritative();
                    int dc = bondAxesOnly.Count;
                    var idealAxes = VseprLayout.GetIdealLocalDirections(dc);
                    var ndAxes = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealAxes, refLocal));
                    var noOcc = new List<ElectronOrbitalFunction>();
                    if (TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, dc, bondAxesOnly, noOcc, ndAxes, out _, out _, out var locksAxes, null) && locksAxes != null && locksAxes.Count > 0)
                        SyncSigmaBondOrbitalTipsFromLocks(locksAxes);
                }
                if (bondedOrbitals.Any(o => o != null && o.Bond == null && o.ElectronCount == 0))
                    OrientEmptyNonbondedOrbitalsPerpendicularToFramework(null, null, placeEmptyAlongBondBreakRef: false);
            }
            return;
        }

        ClearSigmaBondOrbitalRedistributionDeltaWhereAuthoritative();

        var bondAxes = CollectSigmaBondAxesLocalMerged(mergeToleranceDeg);
        int domainCount = GetVseprSlotCount3D(bondAxes.Count, loneOrbitals.Count);
        if (domainCount < 1)
            return;

        // Use exactly the electron-domain count (linear / trigonal planar / tetrahedral / …). Do not escalate toward
        // maxSlots when matching is picky — that incorrectly forced tetrahedral for 2–3 domain centers.
        var idealRaw = VseprLayout.GetIdealLocalDirections(domainCount);

        ElectronOrbitalFunction pin = bondBreakGuideLoneOrbital != null && loneOrbitals.Contains(bondBreakGuideLoneOrbital) ? bondBreakGuideLoneOrbital : null;
        var loneMatch = pin != null ? loneOrbitals.Where(o => o != pin).ToList() : loneOrbitals;

        // Already tetrahedral: same test as [tetra-domain] “visual” row (σ hybrid tips + lone tips in world space).
        // Merged internuclear + local lone tips often failed while hybrids + loners were already ~109.5° — TryMatch then
        // relabeled vertices and spun lone pairs. Require hybrids ≈ bond axes so identity Σ sync does not pop shells.
        const float visualTetSpreadTol = 8f;
        const float visualTetMeanTol = 6f;
        if (domainCount == 4
            && loneOrbitals.TrueForAll(o => o != null && o.ElectronCount == 2)
            && bondAxes.Count + loneOrbitals.Count == 4
            && SigmaTipsAlignedToBondAxes(24f)
            && FourDomainsVisualElectronGeometryApproximatelyTetrahedral(visualTetSpreadTol, visualTetMeanTol))
        {
                var bondIdealLocksSkip = new List<(Vector3 bondAxisLocal, Vector3 idealLocal)>();
                var bondOrderSkip = new List<Vector3>(bondAxes);
                Vector3 refNskip = refLocal.normalized;
                bondOrderSkip.Sort((a, b) =>
                {
                    float da = Vector3.Dot(a.normalized, refNskip);
                    float db = Vector3.Dot(b.normalized, refNskip);
                    int cmp = db.CompareTo(da);
                    if (cmp != 0) return cmp;
                    return CompareVectorsStable(a, b);
                });
                foreach (Vector3 bd in bondOrderSkip)
                    bondIdealLocksSkip.Add((bd.normalized, bd.normalized));

                if (DebugLogReplaceHRedistribute)
                    LogReplaceHRedistribute(
                        "VSEPR lone layout SKIPPED ([tetra-domain] visual spread/mean OK, Σ tips≈axes) | " + FormatAtomBrief(this));
                SyncSigmaBondOrbitalTipsFromLocks(bondIdealLocksSkip);
                LogTetrahedralElectronDomainAngleDiagnostic("Redistribute3D DONE", bondIdealLocksSkip);
                if (DebugLogReplaceHRedistribute)
                    LogReplaceHRedistribute(
                        "Redistribute3D DONE Σ sync only (no lone snap) | " + FormatAtomBrief(this) + $" domainCount={domainCount}");
                return;
        }

        // Tetrahedral electron geometry (4 domains): any of the four ideal vertices can be rotated onto refLocal
        // (broken-σ reference). AlignFirstDirectionTo (vertex 0 only) relabels the frame and forces large lone motion
        // for one of two symmetric H₂O breaks while the shell was already ~tetrahedral (see [lone-vsepr-apply] logs).
        List<Vector3> newDirs;
        if (domainCount == 4 && loneOrbitals.Count > 0)
        {
            var tetRaw = VseprLayout.GetIdealLocalDirections(4);
            float bestMaxTipDeg = float.MaxValue;
            float bestSumDeg = float.MaxValue;
            int bestK = -1;
            List<Vector3> bestDirs = null;
            for (int k = 0; k < 4; k++)
            {
                var alignedArr = VseprLayout.AlignTetrahedronKthVertexTo(tetRaw, k, refLocal);
                var candidate = new List<Vector3>(4);
                for (int i = 0; i < 4; i++)
                    candidate.Add(alignedArr[i]);
                if (!TryMatchLoneOrbitalsToFreeIdealDirections(
                        refLocal, domainCount, bondAxes, loneOrbitals, candidate,
                        out var probeMap, out var probePinRes, out _, pin))
                    continue;
                if (probeMap == null || probeMap.Count != loneMatch.Count)
                    continue;
                float sumDeg = 0f;
                float maxTipDeg = 0f;
                for (int i = 0; i < probeMap.Count; i++)
                {
                    float a = Vector3.Angle(probeMap[i].oldDir.normalized, probeMap[i].newDir.normalized);
                    sumDeg += a;
                    maxTipDeg = Mathf.Max(maxTipDeg, a);
                }
                if (pin != null && probePinRes.HasValue)
                {
                    float a = Vector3.Angle(OrbitalTipLocalDirection(pin).normalized, probePinRes.Value.normalized);
                    sumDeg += a;
                    maxTipDeg = Mathf.Max(maxTipDeg, a);
                }
                // Minimizing sum alone picks orientations where one lobe pays ~68° and others ~0 (asymmetric H₂O breaks);
                // prefer min **max** tip motion, then sum, then k (ties).
                if (maxTipDeg < bestMaxTipDeg - 1e-4f
                    || (Mathf.Abs(maxTipDeg - bestMaxTipDeg) <= 1e-4f && sumDeg < bestSumDeg - 1e-4f)
                    || (Mathf.Abs(maxTipDeg - bestMaxTipDeg) <= 1e-4f && Mathf.Abs(sumDeg - bestSumDeg) <= 1e-4f && (bestK < 0 || k < bestK)))
                {
                    bestMaxTipDeg = maxTipDeg;
                    bestSumDeg = sumDeg;
                    bestK = k;
                    bestDirs = candidate;
                }
            }
            newDirs = bestDirs ?? new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));
            if (DebugLogReplaceHRedistribute && bestK >= 0)
                LogReplaceHRedistribute(
                    "VSEPR tet orient | " + FormatAtomBrief(this)
                    + $" pickedVertexK={bestK} minMax∠≈{bestMaxTipDeg:F2}° total∠≈{bestSumDeg:F2}° (4 σ||refLocal frames)");
        }
        else
            newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));

        LogVsepr3D(
            $"atom={name} Z={atomicNumber} σAxes={bondAxes.Count} lone={loneOrbitals.Count} domainCount={domainCount} maxValenceSlots={maxSlots} relaxσ→tet={relaxCoplanarSigmaToTetrahedral}");
        if (DebugLogReplaceHRedistribute)
            LogReplaceHRedistribute(
                "VSEPR lone layout | " + FormatAtomBrief(this)
                + $" σAxes={bondAxes.Count} occLone={loneOrbitals.Count} domainCount={domainCount} maxSlots={maxSlots}"
                + $" refLocal={refLocal} skipLoneLobe={skipLoneLobeLayout}");

        bool tryOk = TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, domainCount, bondAxes, loneOrbitals, newDirs, out var bestMapping, out var pinReservedDir, out var bondIdealLocks, pin);
        if (!tryOk || bestMapping == null || bestMapping.Count != loneMatch.Count)
        {
            LogVsepr3D(
                $"NO APPLY atom={name} Z={atomicNumber}: TryMatch={tryOk} mappingNull={bestMapping == null} mapCount={(bestMapping != null ? bestMapping.Count : -1)} loneMatchCount={loneMatch.Count}");
            if (DebugLogReplaceHRedistribute)
                LogReplaceHRedistribute(
                    "TryMatchLoneOrbitals FAILED → lone lobes not snapped to tetrahedral vertices | " + FormatAtomBrief(this)
                    + $" TryMatch={tryOk} mapCount={(bestMapping == null ? -1 : bestMapping.Count)} loneMatch={loneMatch.Count} domainCount={domainCount}");
            return;
        }

        var applyDirs = new List<Vector3>(bestMapping.Count);
        for (int i = 0; i < bestMapping.Count; i++)
            applyDirs.Add(bestMapping[i].newDir);
        if ((bondBreakGuideLoneOrbital != null || refBondWorldDirection.HasValue) && loneMatch.Count > 1)
            RematchLoneOrbitalTargetDirectionsMinAngularMotion(loneMatch, applyDirs);

        float maxTipDegPreApply = 0f;
        for (int j = 0; j < loneMatch.Count; j++)
        {
            if (loneMatch[j] == null) continue;
            maxTipDegPreApply = Mathf.Max(
                maxTipDegPreApply,
                Vector3.Angle(OrbitalTipLocalDirection(loneMatch[j]).normalized, applyDirs[j].normalized));
        }
        float pinTipDegPreApply = 0f;
        if (pin != null && pinReservedDir.HasValue)
            pinTipDegPreApply = Vector3.Angle(OrbitalTipLocalDirection(pin).normalized, pinReservedDir.Value.normalized);

        // Only bypass when TryMatch per-lobe motion is **modest** (same tet, different vertex labels). ~109° is a full
        // step to another tet vertex — skipping APPLY leaves lobes inconsistent with σ; identity Σ sync then yields
        // bond/lone overlap (log: min∠=0°, visual≠tetra). H-auto O + 2 H: second redistribute had max∠≈109°.
        const float bypassRelabelMaxTipDeg = 72f;
        // Do **not** pass TryMatch's bondIdealLocks when bypassing lone APPLY (locks assume lobes on matched ideals).
        // Use identity locks (axis, axis) — align σ to internuclear only.
        if (domainCount == 4
            && loneOrbitals.TrueForAll(o => o != null && o.ElectronCount == 2)
            && FourDomainsVisualElectronGeometryApproximatelyTetrahedral(8f, 6f)
            && maxTipDegPreApply <= bypassRelabelMaxTipDeg
            && pinTipDegPreApply <= bypassRelabelMaxTipDeg
            && (maxTipDegPreApply > 4f || pinTipDegPreApply > 4f))
        {
            var bondIdealLocksBypass = new List<(Vector3 bondAxisLocal, Vector3 idealLocal)>();
            var bondOrderBypass = new List<Vector3>(bondAxes);
            Vector3 refNb = refLocal.normalized;
            bondOrderBypass.Sort((a, b) =>
            {
                float da = Vector3.Dot(a.normalized, refNb);
                float db = Vector3.Dot(b.normalized, refNb);
                int cmp = db.CompareTo(da);
                if (cmp != 0) return cmp;
                return CompareVectorsStable(a, b);
            });
            foreach (Vector3 bd in bondOrderBypass)
                bondIdealLocksBypass.Add((bd.normalized, bd.normalized));

            if (DebugLogReplaceHRedistribute)
                LogReplaceHRedistribute(
                    "VSEPR lone APPLY bypassed (visual tet; TryMatch relabel only) | " + FormatAtomBrief(this)
                    + $" max∠(tip,target)={maxTipDegPreApply:F1}° pin∠={pinTipDegPreApply:F1}° → Σ sync identity locks");
            SyncSigmaBondOrbitalTipsFromLocks(bondIdealLocksBypass);
            LogTetrahedralElectronDomainAngleDiagnostic("Redistribute3D DONE", bondIdealLocksBypass);
            if (DebugLogReplaceHRedistribute)
                LogReplaceHRedistribute(
                    "Redistribute3D DONE Σ sync only (no lone snap) | " + FormatAtomBrief(this) + $" domainCount={domainCount}");
            return;
        }

        if (DebugLogVseprLoneLobeMotion)
        {
            float maxTipDeg = maxTipDegPreApply;
            LogVseprLoneLobeMotionLine(
                "SUMMARY pre-apply | " + FormatAtomBrief(this)
                + $" max∠(currTip,targetDir)={maxTipDeg:F3}° nLone={bestMapping.Count} domainCount={domainCount}"
                + $" refLocal={refLocal} relaxσ→tet={relaxCoplanarSigmaToTetrahedral} skipLoneLayout={skipLoneLobeLayout}"
                + $" skipΣRelax={skipSigmaNeighborRelax} refBondW={(refBondWorldDirection.HasValue ? $"{refBondWorldDirection.Value.x:F3},{refBondWorldDirection.Value.y:F3},{refBondWorldDirection.Value.z:F3}" : "null")}"
                + $" guideOrbId={(bondBreakGuideLoneOrbital == null ? "null" : bondBreakGuideLoneOrbital.GetInstanceID().ToString())}");
        }

        for (int i = 0; i < bestMapping.Count; i++)
        {
            var orb = loneMatch[i];
            if (orb == null) continue;
            var newDir = applyDirs[i];
            Vector3 oldTip = OrbitalTipLocalDirection(orb).normalized;
            Vector3 oldLp = orb.transform.localPosition;
            Quaternion oldLr = orb.transform.localRotation;
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius, orb.transform.localRotation);
            if (DebugLogVseprLoneLobeMotion)
            {
                float angTip = Vector3.Angle(oldTip, newDir.normalized);
                float dPos = Vector3.Distance(oldLp, pos);
                float dRot = Quaternion.Angle(oldLr, rot);
                LogVseprLoneLobeMotionLine(
                    "APPLY lone | " + FormatAtomBrief(this)
                    + $" idx={i} orbId={orb.GetInstanceID()} ∠(tip,target)={angTip:F3}° |ΔlocalPos|={dPos:F5} ∠localRot={dRot:F3}°"
                    + (angTip < 0.05f && dPos < 1e-4f && dRot < 0.05f ? " (near-no-op Quaternion/pos still rewritten)" : ""));
            }
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
            LogVsepr3D(
                $"APPLIED atom={name} Z={atomicNumber} loneIdx={i} orbId={orb.GetInstanceID()} newDir={newDir}");
        }

        if (pin != null && pinReservedDir.HasValue)
        {
            Vector3 oldPinTip = OrbitalTipLocalDirection(pin).normalized;
            Vector3 oldPp = pin.transform.localPosition;
            Quaternion oldPr = pin.transform.localRotation;
            var (pPos, pRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(pinReservedDir.Value, bondRadius, pin.transform.localRotation);
            if (DebugLogVseprLoneLobeMotion)
            {
                float angTip = Vector3.Angle(oldPinTip, pinReservedDir.Value.normalized);
                float dPos = Vector3.Distance(oldPp, pPos);
                float dRot = Quaternion.Angle(oldPr, pRot);
                LogVseprLoneLobeMotionLine(
                    "APPLY pin lone | " + FormatAtomBrief(this)
                    + $" orbId={pin.GetInstanceID()} ∠(tip,target)={angTip:F3}° |ΔlocalPos|={dPos:F5} ∠localRot={dRot:F3}°");
            }
            pin.transform.localPosition = pPos;
            pin.transform.localRotation = pRot;
            LogVsepr3D(
                $"APPLIED pin atom={name} Z={atomicNumber} orbId={pin.GetInstanceID()} newDir={pinReservedDir.Value}");
        }

        SyncSigmaBondOrbitalTipsFromLocks(bondIdealLocks);
        LogTetrahedralElectronDomainAngleDiagnostic("Redistribute3D DONE", bondIdealLocks);
        if (DebugLogReplaceHRedistribute)
            LogReplaceHRedistribute("Redistribute3D DONE lone+VSEPR applied | " + FormatAtomBrief(this) + $" domainCount={domainCount}");
    }

    /// <summary>σ domain: internuclear unit vector in nucleus local space. Lone/empty on nucleus: <see cref="OrbitalTipLocalDirection"/> equals localRotation times Vector3.right.</summary>
    List<(string label, string source, Vector3 dir)> CollectRedistribute3DLocalDomainDirsForBondAngleDiag(ElectronOrbitalFunction bondBreakGuideLoneOrbital)
    {
        var list = new List<(string label, string source, Vector3 dir)>();
        foreach (var neighbor in GetDistinctSigmaNeighborAtoms())
        {
            if (neighbor == null) continue;
            Vector3 w = neighbor.transform.position - transform.position;
            if (w.sqrMagnitude < 1e-16f) continue;
            Vector3 dirL = transform.InverseTransformDirection(w.normalized).normalized;
            list.Add((
                "σ→" + neighbor.name,
                "internuclear axis in nucleus local: InverseTransformDirection(normalize(neighbor.worldPos minus atom.worldPos)); σ hybrid pose may update via ApplyRedistributeTargets bond σ pass on this atom or partner",
                dirL));
        }
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null) continue;
            string lbl = o.ElectronCount > 0 ? "lone(" + o.ElectronCount + "e)" : "empty(0e)";
            if (o == bondBreakGuideLoneOrbital) lbl += " guide";
            list.Add((
                lbl + " id=" + o.GetInstanceID(),
                "lobe plusX in nucleus local when parent is nucleus: normalize(localRotation times Vector3.right), same as OrbitalTipLocalDirection",
                OrbitalTipLocalDirection(o).normalized));
        }
        return list;
    }

    void LogRedist3DBondAngleLine(string message)
    {
        Debug.Log(message);
        ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(message);
    }

    void LogRedistribute3DBondAngleDiagnosticsPairwise(string phase, List<(string label, string source, Vector3 dir)> entries)
    {
        if (entries == null || entries.Count == 0) return;
        for (int i = 0; i < entries.Count; i++)
        {
            for (int j = i + 1; j < entries.Count; j++)
            {
                float deg = Vector3.Angle(entries[i].dir, entries[j].dir);
                LogRedist3DBondAngleLine(
                    "[redist3d-angle] " + phase + " pair angleDeg=" + deg.ToString("F2") +
                    " a=" + entries[i].label + " b=" + entries[j].label +
                    " formula=Vector3.Angle between unit vectors in nucleus local space");
            }
        }
    }

    void LogRedistribute3DBondAngleDiagnosticsFull(
        List<(string label, string source, Vector3 dir)> pre,
        List<(string label, string source, Vector3 dir)> post,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets)
    {
        LogRedist3DBondAngleLine(
            "[redist3d-angle] RedistributeOrbitals3D summary atom=" + name + " id=" + GetInstanceID() + " Z=" + atomicNumber +
            " targetsCount=" + targets.Count + " repulsionOnly=" + UseRepulsionLayoutOnlyInGetRedistributeTargets3D +
            " sigmaN=" + GetDistinctSigmaNeighborCount() + " pi=" + GetPiBondCount());
        LogRedist3DBondAngleLine(
            "[redist3d-angle] angleHowCreated sigmaDomainsUseInternuclearAxes loneAndEmptyUseOrbitalTipLocalDirection equals localRotationTimesPlusX on nucleus-parent orbitals");
        if (targets != null)
        {
            for (int t = 0; t < targets.Count; t++)
            {
                var (orb, pos, rot) = targets[t];
                if (orb == null) continue;
                Vector3 tipFromRot = (rot * Vector3.right).normalized;
                bool parentNuc = orb.transform.parent == transform;
                LogRedist3DBondAngleLine(
                    "[redist3d-angle] getRedistributeTargets3D target index=" + t + " orb=" + orb.name + " id=" + orb.GetInstanceID() +
                    " parentIsNucleus=" + parentNuc + " tipLocalFromTargetRot x=" + tipFromRot.x.ToString("F4") + " y=" + tipFromRot.y.ToString("F4") + " z=" + tipFromRot.z.ToString("F4") +
                    " posLocal x=" + pos.x.ToString("F4") + " y=" + pos.y.ToString("F4") + " z=" + pos.z.ToString("F4") +
                    " applyRedistributeTargetsWritesLocalPoseWhenParentIsNucleus=" + parentNuc);
            }
        }
        void LogEntries(string phase, List<(string label, string source, Vector3 dir)> entries)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                LogRedist3DBondAngleLine(
                    "[redist3d-angle] " + phase + " domain i=" + i + " label=" + e.label +
                    " source=" + e.source +
                    " dirLocal x=" + e.dir.x.ToString("F4") + " y=" + e.dir.y.ToString("F4") + " z=" + e.dir.z.ToString("F4"));
            }
        }
        LogEntries("preApply", pre);
        LogRedistribute3DBondAngleDiagnosticsPairwise("preApply", pre);
        LogEntries("postApply", post);
        LogRedistribute3DBondAngleDiagnosticsPairwise("postApply", post);
    }

    /// <summary>
    /// 3D redistribution entry — applies <see cref="GetRedistributeTargets3D"/> (repulsion-only when <see cref="UseRepulsionLayoutOnlyInGetRedistributeTargets3D"/> is true, else VSEPR <c>TryMatch</c>).
    /// Full σ cleavage (<paramref name="bondBreakIsSigmaCleavageBetweenFormerPartners"/>) is framed for guide/ref — not π-only steps of a multi-bond.
    /// Other 0e guides without ex-bond ref may still be placed ⊥ the occupied framework here.
    /// </summary>
    void RedistributeOrbitals3D(float? piBondAngleOverride, Vector3? refBondWorldDirection, bool relaxCoplanarSigmaToTetrahedral = false, bool skipLoneLobeLayout = false, HashSet<AtomFunction> pinAtomsForSigmaRelax = null, bool skipSigmaNeighborRelax = false, ElectronOrbitalFunction bondBreakGuideLoneOrbital = null, AtomFunction newSigmaBondPartnerHint = null, int sigmaNeighborCountBeforeHint = -1, bool skipBondBreakSparseNonbondSpread = false, AtomFunction freezeSigmaNeighborSubtreeRoot = null, bool bondBreakIsSigmaCleavageBetweenFormerPartners = false, CovalentBond redistributionOperationBond = null)
    {
        var targets = GetRedistributeTargets3D(
            GetPiBondCount(),
            newSigmaBondPartnerHint,
            piBondAngleOverride,
            refBondWorldDirection,
            bondBreakGuideLoneOrbital,
            sigmaNeighborCountBeforeHint,
            bondBreakIsSigmaCleavageBetweenFormerPartners,
            redistributionOperationBond);
        List<(string label, string source, Vector3 dir)> preAngleDiag = null;
        if (DebugLogRedistributeOrbitals3DBondAngles)
            preAngleDiag = CollectRedistribute3DLocalDomainDirsForBondAngleDiag(bondBreakGuideLoneOrbital);

        ApplyRedistributeTargets(targets);

        // Without ex-bond ref: optional placement of a 0e guide ⊥ occupied+σ framework.
        // When ref is set (bond break), the empty stays at the break axis — see GetRedistributeTargets3D carbocation path / OrientEmpty skipOrbital.
        if (bondBreakGuideLoneOrbital != null && bondBreakGuideLoneOrbital.ElectronCount == 0
            && bondBreakGuideLoneOrbital.Bond == null && bondBreakGuideLoneOrbital.transform.parent == transform
            && !refBondWorldDirection.HasValue)
        {
            var frameworkDirs = new List<Vector3>();
            foreach (var o in bondedOrbitals)
            {
                if (o == null || o == bondBreakGuideLoneOrbital || o.Bond != null || o.ElectronCount <= 0) continue;
                frameworkDirs.Add(OrbitalTipLocalDirection(o).normalized);
            }
            AppendSigmaBondDirectionsLocalDistinctNeighbors(frameworkDirs);
            if (frameworkDirs.Count > 0 && TryComputePerpendicularEmptySlotFromFrameworkDirs(frameworkDirs, bondRadius, out var pos, out var rot))
            {
                bondBreakGuideLoneOrbital.transform.localPosition = pos;
                bondBreakGuideLoneOrbital.transform.localRotation = rot;
            }
        }

        if (DebugLogRedistributeOrbitals3DBondAngles)
        {
            var postAngleDiag = CollectRedistribute3DLocalDomainDirsForBondAngleDiag(bondBreakGuideLoneOrbital);
            LogRedistribute3DBondAngleDiagnosticsFull(preAngleDiag, postAngleDiag, targets);
        }
    }

    /// <summary>Same target list as <see cref="RedistributeOrbitals3D"/> — for bond-break lerp before <see cref="ApplyRedistributeTargets"/>.</summary>
    public List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> PeekRedistributeTargetsSameAsRedistributeOrbitals3D(
        float? piBondAngleOverride,
        Vector3? refBondWorldDirection,
        ElectronOrbitalFunction bondBreakGuideLoneOrbital,
        int sigmaNeighborCountBeforeHint,
        bool bondBreakIsSigmaCleavageBetweenFormerPartners,
        CovalentBond redistributionOperationBond = null)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return GetRedistributeTargets(
                GetPiBondCount(),
                null,
                bondingTopologyChanged: true,
                skipLoneRedistTargetsForSigmaOnlyBreak: false,
                piBondAngleOverrideForBreakTargets: piBondAngleOverride,
                refBondWorldDirectionForBreakTargets: refBondWorldDirection,
                bondBreakGuideLoneOrbitalForTargets: bondBreakGuideLoneOrbital,
                sigmaNeighborCountBefore: sigmaNeighborCountBeforeHint,
                bondBreakIsSigmaCleavageBetweenFormerPartners: bondBreakIsSigmaCleavageBetweenFormerPartners,
                redistributionOperationBond: redistributionOperationBond);
        return GetRedistributeTargets3D(
            GetPiBondCount(),
            null,
            piBondAngleOverride,
            refBondWorldDirection,
            bondBreakGuideLoneOrbital,
            sigmaNeighborCountBeforeHint,
            bondBreakIsSigmaCleavageBetweenFormerPartners,
            redistributionOperationBond);
    }

    /// <summary>
    /// π cleavage: freeze non-guide domains to current pose (see <see cref="CovalentBond.FreezeRedistTargetsExceptGuideToCurrentLocals"/>); optionally set only a 0e guide along the former π axis (toward partner). <see cref="CovalentBond.BreakBond"/> often splits a 2e π into 1e+1e, so the freeze step applies even when the guide is not empty.
    /// </summary>
    bool TryOverridePiBreakEmptyGuideTargetAlongRef(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets,
        ElectronOrbitalFunction emptyGuide,
        Vector3 refWorldFromThisTowardPartner)
    {
        if (targets == null || emptyGuide == null) return false;
        if (refWorldFromThisTowardPartner.sqrMagnitude < 1e-16f) return false;
        Vector3 refLocal = transform.InverseTransformDirection(refWorldFromThisTowardPartner.normalized);
        if (refLocal.sqrMagnitude < 1e-16f) return false;
        var (pos, rot) = GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(
            emptyGuide, refLocal, bondRadius, emptyGuide.transform.localRotation);
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].orb != emptyGuide) continue;
            targets[i] = (emptyGuide, pos, rot);
            return true;
        }
        return false;
    }

    /// <summary>
    /// σ cleavage 0e ex-bond guide: <see cref="TryOverridePiBreakEmptyGuideTargetAlongRef"/> aligns along internuclear ref and can overlap the remaining σ / lone framework. Prefer the same perpendicular slot as <see cref="TryGetPerpendicularEmptyTargetForGuide"/> (also when no π rows remain — final σ on O=C=O fragment).
    /// </summary>
    bool TryOverrideSigmaOpPiEmptyGuidePerpendicularToFramework(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets,
        ElectronOrbitalFunction emptyGuide)
    {
        if (targets == null || emptyGuide == null) return false;
        if (!TryGetPerpendicularEmptyTargetForGuide(emptyGuide, out var pos, out var rot)) return false;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].orb != emptyGuide) continue;
            targets[i] = (emptyGuide, pos, rot);
            return true;
        }
        return false;
    }

    /// <summary>Lerps nucleus-parented redistribute targets after <see cref="CovalentBond.BreakBond"/>, then applies targets and hybrid refresh. Electron sync and bond GO destroy run in <paramref name="onComplete"/>.</summary>
    /// <param name="redistributionOperationBond">Cleaved bond (e.g. <c>this</c> from <see cref="CovalentBond.BreakBond"/>); π breaks pass it so guide resolution prefers each endpoint's ex-bond lobe over a remaining π.</param>
    public IEnumerator CoLerpBondBreakRedistribution(
        AtomFunction partnerAtom,
        Vector3 refWorldThis,
        Vector3 refWorldPartner,
        ElectronOrbitalFunction guideThis,
        ElectronOrbitalFunction guidePartner,
        int sigmaBeforeThis,
        int sigmaBeforePartner,
        bool sigmaCleavage,
        System.Action onComplete,
        CovalentBond redistributionOperationBond = null)
    {
        const float duration = 0.65f;
        if (partnerAtom == null)
        {
            onComplete?.Invoke();
            yield break;
        }

        var targetsThis = PeekRedistributeTargetsSameAsRedistributeOrbitals3D(null, refWorldThis, guideThis, sigmaBeforeThis, sigmaCleavage, redistributionOperationBond);
        var targetsPartner = partnerAtom.PeekRedistributeTargetsSameAsRedistributeOrbitals3D(null, refWorldPartner, guidePartner, sigmaBeforePartner, sigmaCleavage, redistributionOperationBond);

        bool piBreak = redistributionOperationBond != null && !redistributionOperationBond.IsSigmaBondLine();
        bool opIsSigmaLine = redistributionOperationBond != null && redistributionOperationBond.IsSigmaBondLine();
        bool hasPiRowThis = CovalentBond.TargetsListContainsPiBondLineRow(targetsThis);
        bool hasPiRowPartner = CovalentBond.TargetsListContainsPiBondLineRow(targetsPartner);
        // σ bond break: Peek still includes remaining π bond rows → same joint fight as π cleavage; freeze non-guide rows per side when that side's targets include a π line.
        bool freezeThis = piBreak || (opIsSigmaLine && hasPiRowThis);
        bool freezePartner = piBreak || (opIsSigmaLine && hasPiRowPartner);
        if (freezeThis && guideThis != null)
            CovalentBond.FreezeRedistTargetsExceptGuideToCurrentLocals(targetsThis, guideThis, this);
        if (freezePartner && guidePartner != null)
            CovalentBond.FreezeRedistTargetsExceptGuideToCurrentLocals(targetsPartner, guidePartner, partnerAtom);

        // 0e ex-bond guide: must run for σ cleavage even when no π rows remain (e.g. second σ break on O=C=O fragment)
        // — previously gated inside freezeThis, so last σ break skipped override and layout looked random/overlapping.
        bool perpAppliedThis = false;
        bool override0eThis = false;
        if (guideThis != null && guideThis.ElectronCount == 0 && redistributionOperationBond != null)
        {
            if (piBreak)
                override0eThis = TryOverridePiBreakEmptyGuideTargetAlongRef(targetsThis, guideThis, refWorldThis);
            else if (opIsSigmaLine)
            {
                perpAppliedThis = TryOverrideSigmaOpPiEmptyGuidePerpendicularToFramework(targetsThis, guideThis);
                // Sigma-only cleavage with 0 occ / 4 empty can have no meaningful overlap right after break.
                // If perpendicular target cannot be resolved, keep the precomputed target set unchanged.
                override0eThis = perpAppliedThis;
            }
        }
        string causeThis = piBreak ? "piOp" : (opIsSigmaLine && hasPiRowThis ? "sigmaOpPiTargets" : (opIsSigmaLine ? "sigmaOnly" : "none"));
        bool sigma0eThis = opIsSigmaLine && !piBreak && guideThis != null && guideThis.ElectronCount == 0;
        // #region agent log
        if (guideThis != null && redistributionOperationBond != null && (freezeThis || guideThis.ElectronCount == 0))
        {
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H5",
                "AtomFunction.CoLerpBondBreakRedistribution",
                "empty0e_override_this",
                "{\"atomId\":" + GetInstanceID()
                + ",\"targetsN\":" + targetsThis.Count
                + ",\"guideId\":" + guideThis.GetInstanceID()
                + ",\"guideEC\":" + guideThis.ElectronCount
                + ",\"freezeThis\":" + (freezeThis ? "true" : "false")
                + ",\"hasPiRowThis\":" + (hasPiRowThis ? "true" : "false")
                + ",\"override0e\":" + (override0eThis ? "true" : "false")
                + ",\"sigmaCleavage0eTryPerp\":" + (sigma0eThis ? "true" : "false")
                + ",\"perpApplied\":" + (perpAppliedThis ? "true" : "false")
                + ",\"cause\":\"" + causeThis + "\"}",
                "perp-empty");
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H3",
                "AtomFunction.CoLerpBondBreakRedistribution",
                "pi_break_freeze_except_guide_this",
                "{\"atomId\":" + GetInstanceID()
                + ",\"targetsN\":" + targetsThis.Count
                + ",\"guideId\":" + guideThis.GetInstanceID()
                + ",\"guideEC\":" + guideThis.ElectronCount
                + ",\"override0e\":" + (override0eThis ? "true" : "false")
                + ",\"cause\":\"" + causeThis + "\"}",
                "post-fix");
            Debug.Log("[pi-break-stub] this atomId=" + GetInstanceID() + " targetsN=" + targetsThis.Count + " guideEC=" + guideThis.ElectronCount + " override0e=" + override0eThis + " perpApplied=" + perpAppliedThis + " freeze=" + freezeThis + " cause=" + causeThis);
        }
        // #endregion

        bool perpAppliedPartner = false;
        bool override0ePartner = false;
        if (guidePartner != null && guidePartner.ElectronCount == 0 && redistributionOperationBond != null)
        {
            if (piBreak)
                override0ePartner = partnerAtom.TryOverridePiBreakEmptyGuideTargetAlongRef(targetsPartner, guidePartner, refWorldPartner);
            else if (opIsSigmaLine)
            {
                perpAppliedPartner = partnerAtom.TryOverrideSigmaOpPiEmptyGuidePerpendicularToFramework(targetsPartner, guidePartner);
                // Keep sigma-only behavior symmetric on partner side.
                override0ePartner = perpAppliedPartner;
            }
        }
        string causePartner = piBreak ? "piOp" : (opIsSigmaLine && hasPiRowPartner ? "sigmaOpPiTargets" : (opIsSigmaLine ? "sigmaOnly" : "none"));
        bool sigma0ePartner = opIsSigmaLine && !piBreak && guidePartner != null && guidePartner.ElectronCount == 0;
        // #region agent log
        if (guidePartner != null && redistributionOperationBond != null && (freezePartner || guidePartner.ElectronCount == 0))
        {
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H5",
                "AtomFunction.CoLerpBondBreakRedistribution",
                "empty0e_override_partner",
                "{\"atomId\":" + partnerAtom.GetInstanceID()
                + ",\"targetsN\":" + targetsPartner.Count
                + ",\"guideId\":" + guidePartner.GetInstanceID()
                + ",\"guideEC\":" + guidePartner.ElectronCount
                + ",\"freezePartner\":" + (freezePartner ? "true" : "false")
                + ",\"hasPiRowPartner\":" + (hasPiRowPartner ? "true" : "false")
                + ",\"override0e\":" + (override0ePartner ? "true" : "false")
                + ",\"sigmaCleavage0eTryPerp\":" + (sigma0ePartner ? "true" : "false")
                + ",\"perpApplied\":" + (perpAppliedPartner ? "true" : "false")
                + ",\"cause\":\"" + causePartner + "\"}",
                "perp-empty");
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H3",
                "AtomFunction.CoLerpBondBreakRedistribution",
                "pi_break_freeze_except_guide_partner",
                "{\"atomId\":" + partnerAtom.GetInstanceID()
                + ",\"targetsN\":" + targetsPartner.Count
                + ",\"guideId\":" + guidePartner.GetInstanceID()
                + ",\"guideEC\":" + guidePartner.ElectronCount
                + ",\"override0e\":" + (override0ePartner ? "true" : "false")
                + ",\"cause\":\"" + causePartner + "\"}",
                "post-fix");
            Debug.Log("[pi-break-stub] partner atomId=" + partnerAtom.GetInstanceID() + " targetsN=" + targetsPartner.Count + " guideEC=" + guidePartner.ElectronCount + " override0e=" + override0ePartner + " perpApplied=" + perpAppliedPartner + " freeze=" + freezePartner + " cause=" + causePartner);
        }
        // #endregion

        // #region agent log
        if (atomicNumber == 6 || partnerAtom.atomicNumber == 6)
        {
            float OcoAngleWorldDeg(AtomFunction c)
            {
                if (c == null || c.atomicNumber != 6) return -1f;
                var sig = new List<AtomFunction>();
                foreach (var b in c.CovalentBonds)
                {
                    if (b == null || !b.IsSigmaBondLine()) continue;
                    var o = b.AtomA == c ? b.AtomB : b.AtomA;
                    if (o != null) sig.Add(o);
                }
                if (sig.Count < 2) return -1f;
                Vector3 a = (sig[0].transform.position - c.transform.position).normalized;
                Vector3 d = (sig[1].transform.position - c.transform.position).normalized;
                if (a.sqrMagnitude < 1e-10f || d.sqrMagnitude < 1e-10f) return -1f;
                return Vector3.Angle(a, d);
            }
            float angThis = OcoAngleWorldDeg(this);
            float angP = OcoAngleWorldDeg(partnerAtom);
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H2",
                "AtomFunction.CoLerpBondBreakRedistribution",
                "peek_targets_sigma_framework_angle_deg",
                "{\"thisAtomId\":" + GetInstanceID()
                + ",\"thisZ\":" + atomicNumber
                + ",\"partnerId\":" + partnerAtom.GetInstanceID()
                + ",\"partnerZ\":" + partnerAtom.atomicNumber
                + ",\"sigmaAngleThisDeg\":" + angThis.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"sigmaAnglePartnerDeg\":" + angP.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"opBondParamNull\":" + (redistributionOperationBond == null ? "true" : "false")
                + ",\"opBondId\":" + (redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID().ToString() : "0")
                + ",\"opIsSigmaLine\":" + (redistributionOperationBond == null ? "null" : (redistributionOperationBond.IsSigmaBondLine() ? "true" : "false"))
                + ",\"piBreak\":" + (redistributionOperationBond != null && !redistributionOperationBond.IsSigmaBondLine() ? "true" : "false")
                + ",\"hasPiRowThis\":" + (hasPiRowThis ? "true" : "false")
                + ",\"hasPiRowPartner\":" + (hasPiRowPartner ? "true" : "false")
                + ",\"freezeThis\":" + (freezeThis ? "true" : "false")
                + ",\"freezePartner\":" + (freezePartner ? "true" : "false") + "}");
        }
        // #endregion

        var startsThis = new List<(Vector3 p, Quaternion r)>(targetsThis.Count);
        foreach (var e in targetsThis)
        {
            var o = e.orb;
            // UnityEngine.Object: explicit destroyed check before .transform (same as orb == null for fake-null).
            startsThis.Add(o != null ? (o.transform.localPosition, o.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        }
        var startsPartner = new List<(Vector3 p, Quaternion r)>(targetsPartner.Count);
        foreach (var e in targetsPartner)
        {
            var o = e.orb;
            startsPartner.Add(o != null ? (o.transform.localPosition, o.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        }

        var fragWorldThis = new Dictionary<AtomFunction, (Vector3 worldPos, Quaternion worldRot)>();
        var fragWorldPartner = new Dictionary<AtomFunction, (Vector3 worldPos, Quaternion worldRot)>();
        Quaternion deltaJointThis = Quaternion.identity;
        Quaternion deltaJointPartner = Quaternion.identity;
        Vector3 pivotStartThis = transform.position;
        Vector3 pivotStartPartner = partnerAtom.transform.position;
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            SnapshotJointFragmentWorldPositionsForTargets(targetsThis, fragWorldThis);
            partnerAtom.SnapshotJointFragmentWorldPositionsForTargets(targetsPartner, fragWorldPartner);
            deltaJointThis = ComputeJointRedistributeRotationWorldFromTargetsAndStarts(targetsThis, startsThis);
            deltaJointPartner = partnerAtom.ComputeJointRedistributeRotationWorldFromTargetsAndStarts(targetsPartner, startsPartner);
        }

        string partnerSummaryThis = BuildJointFragSigmaPartnerIdSummary(this, targetsThis);
        string partnerSummaryPartner = BuildJointFragSigmaPartnerIdSummary(partnerAtom, targetsPartner);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            LogJointFragRedistLine("start", "breakLerpThis", fragWorldThis, pivotStartThis, transform.position, -1f, deltaJointThis, partnerSummaryThis);
            partnerAtom.LogJointFragRedistLine("start", "breakLerpPartner", fragWorldPartner, pivotStartPartner, partnerAtom.transform.position, -1f, deltaJointPartner, partnerSummaryPartner);
        }

        int lastJointFragProgressBucket = -1;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float s = Mathf.Clamp01(elapsed / duration);
            s = s * s * (3f - 2f * s);
            float rotT = 1f - (1f - s) * (1f - s);

            for (int i = 0; i < targetsThis.Count; i++)
            {
                var e = targetsThis[i];
                if (e.orb == null || e.orb.transform.parent != transform) continue;
                var (sp, sr) = startsThis[i];
                e.orb.transform.localPosition = Vector3.Lerp(sp, e.pos, s);
                e.orb.transform.localRotation = Quaternion.Slerp(sr, e.rot, rotT);
            }
            for (int i = 0; i < targetsPartner.Count; i++)
            {
                var e = targetsPartner[i];
                if (e.orb == null || e.orb.transform.parent != partnerAtom.transform) continue;
                var (sp, sr) = startsPartner[i];
                e.orb.transform.localPosition = Vector3.Lerp(sp, e.pos, s);
                e.orb.transform.localRotation = Quaternion.Slerp(sr, e.rot, rotT);
            }

            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                ApplyJointRedistributeFragmentMotionFraction(
                    targetsThis, deltaJointThis, s, pivotStartThis, transform.position, fragWorldThis);
                partnerAtom.ApplyJointRedistributeFragmentMotionFraction(
                    targetsPartner, deltaJointPartner, s, pivotStartPartner, partnerAtom.transform.position, fragWorldPartner);
            }

            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                if (DebugLogJointFragRedistEveryFrame)
                {
                    LogJointFragRedistLine("frame", "breakLerpThis", fragWorldThis, pivotStartThis, transform.position, s, deltaJointThis, partnerSummaryThis);
                    partnerAtom.LogJointFragRedistLine("frame", "breakLerpPartner", fragWorldPartner, pivotStartPartner, partnerAtom.transform.position, s, deltaJointPartner, partnerSummaryPartner);
                }
                else if (DebugLogJointFragRedistMilestones)
                {
                    int bucket = Mathf.Clamp(Mathf.FloorToInt(s * 4f), 0, 3);
                    if (bucket != lastJointFragProgressBucket)
                    {
                        lastJointFragProgressBucket = bucket;
                        LogJointFragRedistLine("progress", "breakLerpThis", fragWorldThis, pivotStartThis, transform.position, s, deltaJointThis, partnerSummaryThis);
                        partnerAtom.LogJointFragRedistLine("progress", "breakLerpPartner", fragWorldPartner, pivotStartPartner, partnerAtom.transform.position, s, deltaJointPartner, partnerSummaryPartner);
                    }
                }
            }

            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(new HashSet<AtomFunction> { this, partnerAtom });
            yield return null;
        }

        // for/while exits as soon as elapsed >= duration, so the last iteration used s < 1. Commit s=1 so the last rendered frame matches Apply.
        {
            const float sEnd = 1f;
            const float rotTEnd = 1f;
            for (int i = 0; i < targetsThis.Count; i++)
            {
                var e = targetsThis[i];
                if (e.orb == null || e.orb.transform.parent != transform) continue;
                var (sp, sr) = startsThis[i];
                e.orb.transform.localPosition = Vector3.Lerp(sp, e.pos, sEnd);
                e.orb.transform.localRotation = Quaternion.Slerp(sr, e.rot, rotTEnd);
            }
            for (int i = 0; i < targetsPartner.Count; i++)
            {
                var e = targetsPartner[i];
                if (e.orb == null || e.orb.transform.parent != partnerAtom.transform) continue;
                var (sp, sr) = startsPartner[i];
                e.orb.transform.localPosition = Vector3.Lerp(sp, e.pos, sEnd);
                e.orb.transform.localRotation = Quaternion.Slerp(sr, e.rot, rotTEnd);
            }
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                ApplyJointRedistributeFragmentMotionFraction(
                    targetsThis, deltaJointThis, 1f, pivotStartThis, transform.position, fragWorldThis);
                partnerAtom.ApplyJointRedistributeFragmentMotionFraction(
                    targetsPartner, deltaJointPartner, 1f, pivotStartPartner, partnerAtom.transform.position, fragWorldPartner);
            }
            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(new HashSet<AtomFunction> { this, partnerAtom });
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                LogJointFragRedistLine("commit", "breakLerpThis", fragWorldThis, pivotStartThis, transform.position, 1f, deltaJointThis, partnerSummaryThis);
                partnerAtom.LogJointFragRedistLine("commit", "breakLerpPartner", fragWorldPartner, pivotStartPartner, partnerAtom.transform.position, 1f, deltaJointPartner, partnerSummaryPartner);
            }
        }

        ApplyRedistributeTargets(targetsThis, skipJointRigidFragmentMotion: OrbitalAngleUtility.UseFull3DOrbitalGeometry);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            LogJointFragRedistLine("afterApply", "breakLerpThis", fragWorldThis, pivotStartThis, transform.position, 1f, deltaJointThis, partnerSummaryThis);
        partnerAtom.ApplyRedistributeTargets(targetsPartner, skipJointRigidFragmentMotion: OrbitalAngleUtility.UseFull3DOrbitalGeometry);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            partnerAtom.LogJointFragRedistLine("afterApply", "breakLerpPartner", fragWorldPartner, pivotStartPartner, partnerAtom.transform.position, 1f, deltaJointPartner, partnerSummaryPartner);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(partnerAtom);
            partnerAtom.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(this);
            LogJointFragRedistLine("afterHybrid", "breakLerpThis", fragWorldThis, pivotStartThis, transform.position, 1f, deltaJointThis, partnerSummaryThis);
            partnerAtom.LogJointFragRedistLine("afterHybrid", "breakLerpPartner", fragWorldPartner, pivotStartPartner, partnerAtom.transform.position, 1f, deltaJointPartner, partnerSummaryPartner);
        }
        onComplete?.Invoke();
    }

    void ClearSigmaBondOrbitalRedistributionDeltaWhereAuthoritative()
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;
        foreach (var b in covalentBonds)
        {
            if (b == null || !b.IsSigmaBondLine()) continue;
            b.ResetOrbitalRedistributionDeltaIfAuthoritative(this);
        }
    }

    /// <summary>
    /// Shared σ orbital on <see cref="CovalentBond"/> uses one world rotation; after lone lobes snap to VSEPR vertices,
    /// rotate that orbital so +X tracks the same hybrid direction (authoritative nucleus = lower InstanceID).
    /// </summary>
    void SyncSigmaBondOrbitalTipsFromLocks(List<(Vector3 bondAxisLocal, Vector3 idealLocal)> locks)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || locks == null || locks.Count == 0) return;
        foreach (var bond in covalentBonds)
        {
            if (bond == null || bond.Orbital == null || !bond.IsSigmaBondLine()) continue;
            if (bond.AuthoritativeAtomForOrbitalRedistributionPose() != this) continue;
            var other = bond.AtomA == this ? bond.AtomB : bond.AtomA;
            if (other == null) continue;
            Vector3 toOther = other.transform.position - transform.position;
            if (toOther.sqrMagnitude < 1e-12f) continue;
            Vector3 axisLocal = transform.InverseTransformDirection(toOther.normalized);
            if (axisLocal.sqrMagnitude < 1e-12f) continue;
            axisLocal.Normalize();

            int bestK = -1;
            float bestDot = -2f;
            for (int k = 0; k < locks.Count; k++)
            {
                float d = Vector3.Dot(axisLocal, locks[k].bondAxisLocal);
                if (d > bestDot)
                {
                    bestDot = d;
                    bestK = k;
                }
            }
            if (bestK < 0 || bestDot < 0.35f) continue;

            Vector3 hybridWorld = transform.TransformDirection(locks[bestK].idealLocal);
            if (hybridWorld.sqrMagnitude < 1e-12f) continue;
            hybridWorld.Normalize();
            bond.ApplySigmaOrbitalTipFromRedistribution(this, hybridWorld);
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

    /// <summary>Same geometry as <see cref="OrientEmptyNonbondedOrbitalsPerpendicularToFramework"/> — perpendicular slot from framework directions (occupied tips + σ).
    /// If there is exactly one framework group, prefer the opposite side (180°) for the empty slot.
    /// <paramref name="avoidTipLocalDirections"/> (other 0e lobes): pick ±perp to maximize minimum angular separation from those tips so released empties do not stack.</summary>
    static bool TryComputePerpendicularEmptySlotFromFrameworkDirs(
        IReadOnlyList<Vector3> dirs,
        float bondRadius,
        out Vector3 localPos,
        out Quaternion localRot,
        Vector3? preferredLocalDirection = null,
        IReadOnlyList<Vector3> avoidTipLocalDirections = null)
    {
        localPos = default;
        localRot = default;
        if (dirs != null && dirs.Count == 1 && dirs[0].sqrMagnitude > 1e-10f)
        {
            Vector3 ax = dirs[0].normalized;
            Vector3 opposite = -ax;
            if (avoidTipLocalDirections != null && avoidTipLocalDirections.Count > 0)
            {
                float so = MinAngleToAnyDirection(opposite, avoidTipLocalDirections);
                float sa = MinAngleToAnyDirection(ax, avoidTipLocalDirections);
                const float sepEps = 1.5f;
                if (sa > so + sepEps)
                    opposite = ax;
            }
            var (posOpp, rotOpp) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(opposite, bondRadius);
            localPos = posOpp;
            localRot = rotOpp;
            return true;
        }
        if (!TryComputePerpendicularDirectionFromElectronFramework(dirs, out Vector3 perpRaw)) return false;
        Vector3 c0 = perpRaw.normalized;
        Vector3 c1 = -c0;
        Vector3 chosen;
        bool avoidActive = avoidTipLocalDirections != null && avoidTipLocalDirections.Count > 0;
        if (avoidActive)
        {
            float s0 = MinAngleToAnyDirection(c0, avoidTipLocalDirections);
            float s1 = MinAngleToAnyDirection(c1, avoidTipLocalDirections);
            const float sepEps = 1.5f;
            if (s1 > s0 + sepEps)
                chosen = c1;
            else if (s0 > s1 + sepEps)
                chosen = c0;
            else if (preferredLocalDirection.HasValue && preferredLocalDirection.Value.sqrMagnitude > 1e-10f)
            {
                Vector3 pref = preferredLocalDirection.Value.normalized;
                float d0 = Vector3.Dot(pref, c0);
                float d1 = Vector3.Dot(pref, c1);
                const float dotEps = 1e-5f;
                if (d1 > d0 + dotEps)
                    chosen = c1;
                else if (Mathf.Abs(d1 - d0) <= dotEps && CompareLex3(c1, c0) > 0)
                    chosen = c1;
                else
                    chosen = c0;
            }
            else
                chosen = c0;
        }
        else if (preferredLocalDirection.HasValue && preferredLocalDirection.Value.sqrMagnitude > 1e-10f)
        {
            Vector3 pref = preferredLocalDirection.Value.normalized;
            float d0 = Vector3.Dot(pref, c0);
            float d1 = Vector3.Dot(pref, c1);
            const float dotEps = 1e-5f;
            if (d1 > d0 + dotEps)
                chosen = c1;
            else if (Mathf.Abs(d1 - d0) <= dotEps && CompareLex3(c1, c0) > 0)
                chosen = c1;
            else
                chosen = c0;
        }
        else
            chosen = c0;

        var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(chosen, bondRadius);
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

    /// <summary>Undirected separation between two directions in 0…90°: min(∠(a,b), 180°−∠(a,b)). Parallel or anti-parallel → 0° (same bond axis).</summary>
    static float AcuteAngleBetweenDirections(Vector3 a, Vector3 b)
    {
        if (a.sqrMagnitude < 1e-12f || b.sqrMagnitude < 1e-12f) return 0f;
        float ang = Vector3.Angle(a.normalized, b.normalized);
        return Mathf.Min(ang, 180f - ang);
    }

    static bool IsPerpendicularToDirections(Vector3 dir, IReadOnlyList<Vector3> unitDirs, float toleranceDeg = 12f)
    {
        if (unitDirs == null || unitDirs.Count == 0) return false;
        if (dir.sqrMagnitude < 1e-10f) return false;
        dir = dir.normalized;
        foreach (var d in unitDirs)
        {
            if (d.sqrMagnitude < 1e-10f) continue;
            float ang = Vector3.Angle(dir, d.normalized);
            if (Mathf.Abs(ang - 90f) > toleranceDeg) return false;
        }
        return true;
    }

    /// <summary>
    /// Whether the empty lobe tip is already in an acceptable pose: one framework direction = ⊥ that axis (undirected: <see cref="AcuteAngleBetweenDirections"/> ≈ 90°), not collinear with the remaining σ/π axis; two or more = ⊥ each (VSEPR plane normal family).
    /// <paramref name="occupiedLobeAxesMustSeparateFrom"/> (optional): σ/π or lone axes that still occupy space but may be omitted from <paramref name="electronFrameworkUnitDirs"/> (e.g. co-bond merge, or repulsion targets vs current tips). Rejects tips within <paramref name="minUndirectedSeparationDegFromOccupied"/> of any such axis using <see cref="AcuteAngleBetweenDirections"/>.
    /// If <paramref name="electronFrameworkUnitDirs"/> is null or empty, only the separation test applies (returns true when there is nothing to separate from, or when all axes are clear).
    /// </summary>
    static bool EmptyTipAlreadyIdealVsElectronFramework(
        Vector3 unitTip,
        IReadOnlyList<Vector3> electronFrameworkUnitDirs,
        float perpToleranceDeg = 14f,
        IReadOnlyList<Vector3> occupiedLobeAxesMustSeparateFrom = null,
        float minUndirectedSeparationDegFromOccupied = 36f)
    {
        if (unitTip.sqrMagnitude < 1e-12f) return false;
        unitTip = unitTip.normalized;

        bool hasFw = electronFrameworkUnitDirs != null && electronFrameworkUnitDirs.Count > 0;
        bool hasSep = occupiedLobeAxesMustSeparateFrom != null && occupiedLobeAxesMustSeparateFrom.Count > 0;
        if (!hasFw && !hasSep) return false;

        if (occupiedLobeAxesMustSeparateFrom != null)
        {
            foreach (var occ in occupiedLobeAxesMustSeparateFrom)
            {
                if (occ.sqrMagnitude < 1e-10f) continue;
                if (AcuteAngleBetweenDirections(unitTip, occ.normalized) < minUndirectedSeparationDegFromOccupied)
                    return false;
            }
        }

        if (!hasFw) return true;

        if (electronFrameworkUnitDirs.Count == 1)
        {
            var f = electronFrameworkUnitDirs[0];
            if (f.sqrMagnitude < 1e-10f) return false;
            float acuteToBondAxis = AcuteAngleBetweenDirections(unitTip, f.normalized);
            return Mathf.Abs(acuteToBondAxis - 90f) <= perpToleranceDeg;
        }
        return IsPerpendicularToDirections(unitTip, electronFrameworkUnitDirs, perpToleranceDeg);
    }

    /// <summary>Choose ±planeNormal so the empty axis is as far as possible from other empty lobe directions.</summary>
    static Vector3 PickEmptyDirOnAxisAvoidingOtherEmpties(Vector3 planeNormalUnit, Vector3 eTipUnit, IReadOnlyList<Vector3> placedEmptyTips)
    {
        Vector3 a = Vector3.Dot(eTipUnit, planeNormalUnit) >= 0f ? planeNormalUnit : -planeNormalUnit;
        Vector3 b = -a;
        float sa = MinAngleToAnyDirection(a, placedEmptyTips);
        float sb = MinAngleToAnyDirection(b, placedEmptyTips);
        return sb > sa + 0.5f ? b : a;
    }

    /// <summary>0e slot perpendicular to electron-containing framework; picks among candidates so direction stays separated from other empty tips.
    /// If there is exactly one framework group, prefer the opposite side (180°) when that side is not already occupied by another empty.</summary>
    static bool TryComputeSeparatedEmptySlot(
        IReadOnlyList<Vector3> electronFrameworkDirs,
        IReadOnlyList<Vector3> otherEmptyTipsLocal,
        float bondRadius,
        out Vector3 localPos,
        out Quaternion localRot,
        Vector3? preferredLocalDirection = null)
    {
        localPos = default;
        localRot = default;
        if (electronFrameworkDirs == null || electronFrameworkDirs.Count == 0) return false;

        // One-group case: prioritize the 180° opposite side unless another empty already occupies that direction.
        if (electronFrameworkDirs.Count == 1 && electronFrameworkDirs[0].sqrMagnitude > 1e-10f)
        {
            Vector3 opposite = -electronFrameworkDirs[0].normalized;
            const float emptyOccupancyTolDeg = 18f;
            if (MinAngleToAnyDirection(opposite, otherEmptyTipsLocal) > emptyOccupancyTolDeg)
            {
                var (posOpp, rotOpp) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(opposite, bondRadius);
                localPos = posOpp;
                localRot = rotOpp;
                return true;
            }
        }

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

        const float minSepFromOtherEmptyDeg = 20f;
        Vector3 pref = preferredLocalDirection.HasValue && preferredLocalDirection.Value.sqrMagnitude > 1e-10f
            ? preferredLocalDirection.Value.normalized
            : Vector3.zero;
        bool hasPref = pref.sqrMagnitude > 1e-10f;

        void ScoreCandidate(Vector3 rawN, out float sepEmpty, out float score)
        {
            sepEmpty = MinAngleToAnyDirection(rawN, otherEmptyTipsLocal);
            float sepEl = 180f;
            foreach (var e in electronFrameworkDirs)
            {
                if (e.sqrMagnitude < 1e-10f) continue;
                float ang = Vector3.Angle(rawN, e.normalized);
                float m = Mathf.Min(ang, 180f - ang);
                if (m < sepEl) sepEl = m;
            }
            score = Mathf.Min(sepEmpty * 1.3f, sepEl * 1.4f);
        }

        void TieBreakPrefer(ref Vector3 best, Vector3 rawN)
        {
            if (hasPref)
            {
                float dRaw = Vector3.Dot(pref, rawN);
                float dBest = Vector3.Dot(pref, best);
                const float dotEps = 1e-5f;
                if (dRaw > dBest + dotEps || (Mathf.Abs(dRaw - dBest) <= dotEps && CompareLex3(rawN, best) > 0))
                    best = rawN;
            }
            else if (CompareLex3(rawN, best) > 0)
                best = rawN;
        }

        Vector3 bestStrict = Vector3.zero;
        float bestStrictScore = -1f;
        Vector3 bestLoose = basePerp.normalized;
        float bestLooseScore = -1f;
        foreach (var raw in candidates)
        {
            if (raw.sqrMagnitude < 1e-10f) continue;
            Vector3 rawN = raw.normalized;
            ScoreCandidate(rawN, out var sepEmpty, out float score);
            if (sepEmpty >= minSepFromOtherEmptyDeg)
            {
                if (score > bestStrictScore + 1e-4f)
                {
                    bestStrictScore = score;
                    bestStrict = rawN;
                }
                else if (Mathf.Abs(score - bestStrictScore) <= 1e-4f && bestStrictScore >= 0f)
                    TieBreakPrefer(ref bestStrict, rawN);
            }
            if (score > bestLooseScore + 1e-4f)
            {
                bestLooseScore = score;
                bestLoose = rawN;
            }
            else if (Mathf.Abs(score - bestLooseScore) <= 1e-4f)
                TieBreakPrefer(ref bestLoose, rawN);
        }

        Vector3 best = bestStrictScore >= 0f ? bestStrict : bestLoose;

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

    /// <summary>After VSEPR, place each 0e non-bonded lobe. Occupied lone + σ define the framework; 0e lobes sit ⊥ that framework.
    /// σ cleavage: carbocation-style shells (<see cref="IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase"/>) still use ⊥ framework, not along ex-bond ref.
    /// π component break (σ still between the atoms): remaining σ + other electrons guide layout; empty is placed ⊥ that framework.
    /// Multiple empties: each gets a slot ⊥ electron-containing orbitals and separated from other empties (bond-break guide-only adjustment when applicable).
    /// <paramref name="placeEmptyAlongBondBreakRef"/> is the boolean passed from bond-break code as <c>sigmaCleavageBetweenPartners</c> (<see cref="CovalentBond.BreakBond"/> — full σ cleavage when no σ edge remains between the two centers). It matches <c>bondBreakIsSigmaCleavageBetweenFormerPartners</c> on <see cref="RedistributeOrbitals"/>.
    /// When <c>true</c>: newly formed 0e guide stays at the break/slot; other e⁻ domains are laid out elsewhere ⊥ that axis. When <c>false</c> (π-only step, σ still links the pair): only the new 0e guide is snapped ⊥ the post-layout σ + occupied non-bond framework.
    /// With two 0e on the nucleus, only the bond-break guide moves (separated from the other empty, with π-only ⊥ fallback if needed).</summary>
    public void OrientEmptyNonbondedOrbitalsPerpendicularToFramework(Vector3? bondBreakRefWorldDirection = null, ElectronOrbitalFunction skipOrbital = null, bool placeEmptyAlongBondBreakRef = true)
    {
        var empty = bondedOrbitals.Where(o => o != null && o.Bond == null && o.ElectronCount == 0 && (skipOrbital == null || o != skipOrbital)).ToList();
        bool breakGuideIsZeroEmpty = skipOrbital != null
            && skipOrbital.ElectronCount == 0
            && IsBondBreakGuideOrbitalWithBondingCleared(skipOrbital);

        bool refOk = bondBreakRefWorldDirection.HasValue && bondBreakRefWorldDirection.Value.sqrMagnitude >= 0.01f;
        Vector3? preferredEmptyDirLocal = refOk
            ? transform.InverseTransformDirection(bondBreakRefWorldDirection.Value.normalized)
            : (Vector3?)null;

        if (empty.Count == 0)
        {
            if (!breakGuideIsZeroEmpty)
                return;

            if (CovalentBond.DebugLogBreakBondMotionSources && bondBreakRefWorldDirection.HasValue)
                Debug.Log(
                    "[break-motion] OrientEmptyNonbondedOrbitalsPerpendicularToFramework atom=" + name + "(Z=" + atomicNumber + ") emptyCount=0(soleGuide0e) σN=" +
                    GetDistinctSigmaNeighborCount() + " π=" + GetPiBondCount() + " placeAlongRef=" + placeEmptyAlongBondBreakRef + " skipOrbital=True");

            if (DebugLogCcBondBreakGeometry && !placeEmptyAlongBondBreakRef)
                LogCcBondBreak($"OrientEmpty atom={name}: sole 0e guide, placeEmptyAlongBondBreakRef=false — ⊥ snap to electron+σ framework");

            if (DebugLogBondBreakEmptyTeleport)
            {
                LogBondBreakEmptyTeleportLine(
                    $"[break-empty-teleport] OrientEmpty enter: atom={name} soleGuide0e refOk={refOk} placeAlongRef={placeEmptyAlongBondBreakRef} σN={GetDistinctSigmaNeighborCount()}");
                LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/before", bondBreakRefWorldDirection, skipOrbital);
            }

            if (placeEmptyAlongBondBreakRef)
            {
                if (DebugLogBondBreakEmptyTeleport)
                    LogBondBreakEmptyTeleportLine($"[break-empty-teleport] OrientEmpty branch=soleGuide0eSigmaCleavePin atom={name}");
                return;
            }

            var edSolePi = new List<Vector3>();
            foreach (var o in bondedOrbitals)
            {
                if (o == null || o.Bond != null || o.ElectronCount <= 0) continue;
                edSolePi.Add(OrbitalTipLocalDirection(o).normalized);
            }
            if (!TryAppendCarbocationFrameworkSigmaLobeTips(edSolePi, skipOrbital, null))
                AppendSigmaBondDirectionsLocalDistinctNeighbors(edSolePi);
            if (edSolePi.Count > 0
                && TryComputePerpendicularEmptySlotFromFrameworkDirs(edSolePi, bondRadius, out var posPi, out var rotPi, preferredEmptyDirLocal))
            {
                skipOrbital.transform.localPosition = posPi;
                skipOrbital.transform.localRotation = rotPi;
            }
            if (DebugLogBondBreakEmptyTeleport)
            {
                LogBondBreakEmptyTeleportLine($"[break-empty-teleport] OrientEmpty branch=soleGuide0ePiPerpFramework atom={name}");
                LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/after-soleGuide0ePiPerpFramework", bondBreakRefWorldDirection, skipOrbital);
            }
            goto OrientEmptyFinishCarbSnap;
        }

        if (CovalentBond.DebugLogBreakBondMotionSources && bondBreakRefWorldDirection.HasValue)
            Debug.Log(
                "[break-motion] OrientEmptyNonbondedOrbitalsPerpendicularToFramework atom=" + name + "(Z=" + atomicNumber + ") emptyCount=" + empty.Count +
                " σN=" + GetDistinctSigmaNeighborCount() + " π=" + GetPiBondCount() + " placeAlongRef=" + placeEmptyAlongBondBreakRef +
                " skipOrbital=" + (skipOrbital != null));

        if (DebugLogCcBondBreakGeometry && !placeEmptyAlongBondBreakRef)
            LogCcBondBreak($"OrientEmpty atom={name}: placeEmptyAlongBondBreakRef=false (σ still between pair) — empty uses ⊥ electron+σ framework if applicable");

        if (DebugLogBondBreakEmptyTeleport)
        {
            LogBondBreakEmptyTeleportLine(
                $"[break-empty-teleport] OrientEmpty enter atom={name} id={GetInstanceID()} Z={atomicNumber} emptyCount={empty.Count} refOk={refOk} placeAlongRef={placeEmptyAlongBondBreakRef} " +
                $"carbStyleEmpty={IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(skipOrbital)} σN={GetDistinctSigmaNeighborCount()}");
            LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/before", bondBreakRefWorldDirection, skipOrbital);
        }

        var electronDirs = new List<Vector3>();
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null || o.ElectronCount <= 0) continue;
            electronDirs.Add(OrbitalTipLocalDirection(o).normalized);
        }
        if (!TryAppendCarbocationFrameworkSigmaLobeTips(electronDirs, skipOrbital, null))
            AppendSigmaBondDirectionsLocalDistinctNeighbors(electronDirs);

        // Ex-bond 0e guide + another 0e on nucleus: move only the cleaved (guide) empty; keep pre-existing empties at their post-redistribute pose.
        if (breakGuideIsZeroEmpty && empty.Count >= 1)
        {
            if (placeEmptyAlongBondBreakRef)
            {
                // Full σ cleavage: guide 0e stays at break/slot. Removed: reposition of additional 0e (2-occ+2-empty / dual-empty pairing)
                // pending rewrite — see former TryComputeSigmaNZeroExBondGuideLinearBondBreakSlots + 2/2 loop here.

                if (DebugLogBondBreakEmptyTeleport)
                {
                    LogBondBreakEmptyTeleportLine(
                        $"[break-empty-teleport] OrientEmpty branch=breakGuide0eSigmaCleavePin atom={name} fixedOther0e={empty.Count}");
                    LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/after-breakGuide0eSigmaCleavePin", bondBreakRefWorldDirection, skipOrbital);
                }
                goto OrientEmptyFinishCarbSnap;
            }

            var otherEmptyTips = new List<Vector3>(empty.Count);
            foreach (var o in empty)
            {
                Vector3 t = OrbitalTipLocalDirection(o);
                if (t.sqrMagnitude > 1e-10f) otherEmptyTips.Add(t.normalized);
            }
            if (otherEmptyTips.Count > 0 && electronDirs.Count > 0
                && TryComputeSeparatedEmptySlot(electronDirs, otherEmptyTips, bondRadius, out var posSep, out var rotSep, preferredEmptyDirLocal))
            {
                skipOrbital.transform.localPosition = posSep;
                skipOrbital.transform.localRotation = rotSep;
            }
            else if (!placeEmptyAlongBondBreakRef && electronDirs.Count > 0
                && TryComputePerpendicularEmptySlotFromFrameworkDirs(electronDirs, bondRadius, out var posPi2, out var rotPi2, preferredEmptyDirLocal))
            {
                skipOrbital.transform.localPosition = posPi2;
                skipOrbital.transform.localRotation = rotPi2;
            }
            if (DebugLogBondBreakEmptyTeleport)
            {
                LogBondBreakEmptyTeleportLine(
                    $"[break-empty-teleport] OrientEmpty branch=breakGuide0eVsFixedExisting atom={name} fixedOther0eTips={otherEmptyTips.Count}");
                LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/after-breakGuide0eVsFixedExisting", bondBreakRefWorldDirection, skipOrbital);
            }
        }
        else
        {
            // Bond-break 0e guide should have been handled above; if not (unexpected), do not multi-move other empties — would drag pre-existing 0e lobes.
            if (empty.Count >= 2 && !(skipOrbital != null && IsBondBreakGuideOrbitalWithBondingCleared(skipOrbital)))
            {
                var placedEmptyTips = new List<Vector3>();
                foreach (var e in empty)
                {
                    if (electronDirs.Count == 0)
                        continue;
                    if (!TryComputeSeparatedEmptySlot(electronDirs, placedEmptyTips, bondRadius, out var pos, out var rot, preferredEmptyDirLocal)) continue;
                    e.transform.localPosition = pos;
                    e.transform.localRotation = rot;
                    placedEmptyTips.Add(OrbitalTipLocalDirection(e).normalized);
                }
                if (DebugLogBondBreakEmptyTeleport)
                {
                    LogBondBreakEmptyTeleportLine($"[break-empty-teleport] OrientEmpty branch=separatedMultiEmpty atom={name} count={empty.Count}");
                    LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/after-separatedMultiEmpty", bondBreakRefWorldDirection, skipOrbital);
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
                if (!TryAppendCarbocationFrameworkSigmaLobeTips(dirs, skipOrbital, null))
                    AppendSigmaBondDirectionsLocalDistinctNeighbors(dirs);
                if (dirs.Count == 0)
                    continue;
                if (!TryComputePerpendicularEmptySlotFromFrameworkDirs(dirs, bondRadius, out var pos, out var rot, preferredEmptyDirLocal)) continue;
                e.transform.localPosition = pos;
                e.transform.localRotation = rot;
            }
            if (DebugLogBondBreakEmptyTeleport)
            {
                LogBondBreakEmptyTeleportLine($"[break-empty-teleport] OrientEmpty branch=perpFramework atom={name}");
                LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/after-perpFramework", bondBreakRefWorldDirection, skipOrbital);
            }
        }

    OrientEmptyFinishCarbSnap:
        // CH₃⁺-style empty along ex-bond ref: do not pull the 0e lobe to the σ-tip plane normal (would fight fixed break pose).
        // placeEmptyAlongBondBreakRef mirrors BreakBond σCleavageBetweenPartners — false on π-only break (σ still links centers).
        bool skipEmptySnapToSigmaTipPlane = refOk && placeEmptyAlongBondBreakRef && skipOrbital != null && skipOrbital.ElectronCount == 0 && IsSp2BondBreakEmptyAlongRefCase()
            && IsBondBreakGuideOrbitalWithBondingCleared(skipOrbital);
        if (GetDistinctSigmaNeighborCount() == 3
            && IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(skipOrbital)
            && !skipEmptySnapToSigmaTipPlane)
            SnapCarbocationEmptyPerpendicularToActualSigmaTipPlane(skipOrbital);
        LogCarbocationSigmaLobeVersusInternuclearAxesDiagnostics("OrientEmpty/end", skipOrbital);
    }

    /// <summary>
    /// Bond-break animation: append 0e lobes whose post-layout pose differs from current. Matches <see cref="OrientEmptyNonbondedOrbitalsPerpendicularToFramework"/> (⊥ σ + occupied framework).
    /// Uses <paramref name="occupiedRedistEnds"/> for predicted occupied tips when available (π break / VSEPR).
    /// </summary>
    public void AppendBondBreakEmptyOrbitalAnimTargets(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> occupiedRedistEnds, Vector3? bondBreakRefWorldDirection = null, bool placeEmptyAlongBondBreakRef = true, ElectronOrbitalFunction bondBreakGuideOnThisAtom = null)
    {
        var empties = bondedOrbitals.Where(o => o != null && o.Bond == null && o.ElectronCount == 0).ToList();
        if (empties.Count == 0) return;
        bool refOkAnim = bondBreakRefWorldDirection.HasValue && bondBreakRefWorldDirection.Value.sqrMagnitude >= 0.01f;
        Vector3? preferredAnimEmptyDirLocal = refOkAnim
            ? transform.InverseTransformDirection(bondBreakRefWorldDirection.Value.normalized)
            : (Vector3?)null;
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

        const float posEps = 0.0004f;
        const float rotEps = 0.35f;

        if (empties.Count >= 2)
        {
            var electronDirs = new List<Vector3>();
            foreach (var o in bondedOrbitals)
            {
                if (o == null || o.Bond != null || o.ElectronCount <= 0) continue;
                electronDirs.Add(TipFromRedistOrCurrent(o).normalized);
            }
            if (!TryAppendCarbocationFrameworkSigmaLobeTips(electronDirs, bondBreakGuideOnThisAtom, occupiedRedistEnds))
                AppendSigmaBondDirectionsLocalDistinctNeighbors(electronDirs);

            var guide = bondBreakGuideOnThisAtom;
            bool guideIsZeroEmpty = guide != null && guide.ElectronCount == 0 && IsBondBreakGuideOrbitalWithBondingCleared(guide);
            if (guideIsZeroEmpty)
            {
                if (placeEmptyAlongBondBreakRef)
                    return;

                var otherTips = new List<Vector3>();
                foreach (var o in empties)
                {
                    if (o == guide) continue;
                    Vector3 t = TipFromRedistOrCurrent(o);
                    if (t.sqrMagnitude > 1e-10f) otherTips.Add(t.normalized);
                }
                if (!already.Contains(guide) && electronDirs.Count > 0 && otherTips.Count > 0
                    && TryComputeSeparatedEmptySlot(electronDirs, otherTips, bondRadius, out var posS, out var rotS, preferredAnimEmptyDirLocal))
                {
                    if (Vector3.SqrMagnitude(guide.transform.localPosition - posS) > posEps * posEps ||
                        Quaternion.Angle(guide.transform.localRotation, rotS) > rotEps)
                        occupiedRedistEnds.Add((guide, posS, rotS));
                }
                else if (!already.Contains(guide) && !placeEmptyAlongBondBreakRef && electronDirs.Count > 0
                    && TryComputePerpendicularEmptySlotFromFrameworkDirs(electronDirs, bondRadius, out var posF, out var rotF, preferredAnimEmptyDirLocal))
                {
                    if (Vector3.SqrMagnitude(guide.transform.localPosition - posF) > posEps * posEps ||
                        Quaternion.Angle(guide.transform.localRotation, rotF) > rotEps)
                        occupiedRedistEnds.Add((guide, posF, rotF));
                }
                return;
            }

            var placedTips = new List<Vector3>();
            foreach (var emptyOrb in empties)
            {
                if (already.Contains(emptyOrb)) continue;
                if (electronDirs.Count == 0) continue;
                if (!TryComputeSeparatedEmptySlot(electronDirs, placedTips, bondRadius, out var pos, out var rot, preferredAnimEmptyDirLocal)) continue;
                if (Vector3.SqrMagnitude(emptyOrb.transform.localPosition - pos) > posEps * posEps ||
                    Quaternion.Angle(emptyOrb.transform.localRotation, rot) > rotEps)
                    occupiedRedistEnds.Add((emptyOrb, pos, rot));
                placedTips.Add((rot * Vector3.right).normalized);
            }
            return;
        }

        if (refOkAnim && GetPiBondCount() == 0 && empties.Count == 1
            && TryGetCarbocationOneEmptyAndThreeOccupiedDomains(bondBreakGuideOnThisAtom, out var animGuideEmpty, out _)
            && empties[0] == animGuideEmpty)
            return;

        if (empties.Count == 1 && bondBreakGuideOnThisAtom != null && empties[0] == bondBreakGuideOnThisAtom
            && bondBreakGuideOnThisAtom.ElectronCount == 0 && IsBondBreakGuideOrbitalWithBondingCleared(bondBreakGuideOnThisAtom)
            && placeEmptyAlongBondBreakRef)
            return;

        foreach (var emptyOrb in empties)
        {
            if (already.Contains(emptyOrb)) continue;
            var dirs = new List<Vector3>();
            foreach (var o in bondedOrbitals)
            {
                if (o == null || o == emptyOrb || o.Bond != null || o.ElectronCount <= 0) continue;
                dirs.Add(TipFromRedistOrCurrent(o));
            }
            if (!TryAppendCarbocationFrameworkSigmaLobeTips(dirs, bondBreakGuideOnThisAtom, occupiedRedistEnds))
                AppendSigmaBondDirectionsLocalDistinctNeighbors(dirs);
            if (dirs.Count == 0) continue;
            if (!TryComputePerpendicularEmptySlotFromFrameworkDirs(dirs, bondRadius, out var pos, out var rot)) continue;
            if (Vector3.SqrMagnitude(emptyOrb.transform.localPosition - pos) > posEps * posEps ||
                Quaternion.Angle(emptyOrb.transform.localRotation, rot) > rotEps)
                occupiedRedistEnds.Add((emptyOrb, pos, rot));
        }
    }

    /// <summary>
    /// σ bond formation / σ-count increase: 0e slots need targets ⊥ σ + occupied framework (predicted <paramref name="occupiedPredictedEnds"/> when set, else current tips).
    /// Multiple empties: same separation strategy as <see cref="OrientEmptyNonbondedOrbitalsPerpendicularToFramework"/>.
    /// Bond break with cleared 0e guide: <paramref name="pinSoleBreakGuide0eForFullSigmaCleavage"/> is <see cref="CovalentBond.BreakBond"/> <c>sigmaCleavageBetweenPartners</c>. When true, sole guide appends no target (slot pin); when false (π break), sole guide gets ⊥ framework target.
    /// With two 0e, only the guide gets a target; full σ-cleavage pins that guide (no movement), while π break may use separation or ⊥ fallback.
    /// </summary>
    void AppendEmptyNonbondRedistributeTargetsForSigmaFramework(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> result,
        IReadOnlyList<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> occupiedPredictedEnds = null,
        ElectronOrbitalFunction bondBreakGuideLoneOrbitalForEmptyTargets = null,
        bool pinSoleBreakGuide0eForFullSigmaCleavage = false)
    {
        if (result == null) return;
        var empties = bondedOrbitals.Where(o => o != null && o.Bond == null && o.ElectronCount == 0).OrderBy(o => o.GetInstanceID()).ToList();
        if (empties.Count == 0) return;

        const float posEps = 0.0004f;
        const float rotEps = 0.35f;

        bool onlyBreakGuide0e = bondBreakGuideLoneOrbitalForEmptyTargets != null
            && bondBreakGuideLoneOrbitalForEmptyTargets.ElectronCount == 0
            && IsBondBreakGuideOrbitalWithBondingCleared(bondBreakGuideLoneOrbitalForEmptyTargets)
            && empties.Contains(bondBreakGuideLoneOrbitalForEmptyTargets);

        var electronDirs = new List<Vector3>();
        if (occupiedPredictedEnds != null)
        {
            foreach (var (ob, _, pr) in occupiedPredictedEnds)
            {
                if (ob == null || ob.transform.parent != transform || ob.Bond != null) continue;
                if (ob.ElectronCount <= 0) continue;
                electronDirs.Add((pr * Vector3.right).normalized);
            }
        }
        if (electronDirs.Count == 0)
        {
            foreach (var o in bondedOrbitals)
            {
                if (o == null || o.Bond != null || o.ElectronCount <= 0) continue;
                electronDirs.Add(OrbitalTipLocalDirection(o).normalized);
            }
        }
        AppendSigmaBondDirectionsLocalDistinctNeighbors(electronDirs);

        if (onlyBreakGuide0e)
        {
            var guide = bondBreakGuideLoneOrbitalForEmptyTargets;
            Vector3 guideTip = OrbitalTipLocalDirection(guide);
            Vector3? preferredGuideDirLocal = guideTip.sqrMagnitude > 1e-10f ? guideTip.normalized : (Vector3?)null;
            if (pinSoleBreakGuide0eForFullSigmaCleavage)
                return;

            if (empties.Count <= 1)
            {
                if (electronDirs.Count > 0
                    && TryComputePerpendicularEmptySlotFromFrameworkDirs(electronDirs, bondRadius, out var posP, out var rotP, preferredGuideDirLocal))
                {
                    if (Vector3.SqrMagnitude(guide.transform.localPosition - posP) > posEps * posEps ||
                        Quaternion.Angle(guide.transform.localRotation, rotP) > rotEps)
                        result.Add((guide, posP, rotP));
                }
                return;
            }

            var otherTips = new List<Vector3>();
            foreach (var o in empties)
            {
                if (o == guide) continue;
                Vector3 t = OrbitalTipLocalDirection(o);
                if (t.sqrMagnitude > 1e-10f) otherTips.Add(t.normalized);
            }
            if (electronDirs.Count > 0 && otherTips.Count > 0
                && TryComputeSeparatedEmptySlot(electronDirs, otherTips, bondRadius, out var posS, out var rotS, preferredGuideDirLocal))
            {
                if (Vector3.SqrMagnitude(guide.transform.localPosition - posS) > posEps * posEps ||
                    Quaternion.Angle(guide.transform.localRotation, rotS) > rotEps)
                    result.Add((guide, posS, rotS));
            }
            else if (electronDirs.Count > 0
                && TryComputePerpendicularEmptySlotFromFrameworkDirs(electronDirs, bondRadius, out var posF, out var rotF, preferredGuideDirLocal))
            {
                if (Vector3.SqrMagnitude(guide.transform.localPosition - posF) > posEps * posEps ||
                    Quaternion.Angle(guide.transform.localRotation, rotF) > rotEps)
                    result.Add((guide, posF, rotF));
            }
            return;
        }

        if (empties.Count >= 2)
        {
            var placedTips = new List<Vector3>();
            foreach (var e in empties)
            {
                if (electronDirs.Count == 0) continue;
                if (!TryComputeSeparatedEmptySlot(electronDirs, placedTips, bondRadius, out var pos, out var rot)) continue;
                if (Vector3.SqrMagnitude(e.transform.localPosition - pos) > posEps * posEps ||
                    Quaternion.Angle(e.transform.localRotation, rot) > rotEps)
                    result.Add((e, pos, rot));
                placedTips.Add((rot * Vector3.right).normalized);
            }
            return;
        }

        var e0 = empties[0];
        if (electronDirs.Count == 0) return;
        if (!TryComputePerpendicularEmptySlotFromFrameworkDirs(electronDirs, bondRadius, out var p0, out var r0)) return;
        if (Vector3.SqrMagnitude(e0.transform.localPosition - p0) > posEps * posEps ||
            Quaternion.Angle(e0.transform.localRotation, r0) > rotEps)
            result.Add((e0, p0, r0));
    }

    /// <summary>First covalent partner direction in nucleus local (single-bond path). Used to express legacy π plane angles without world-fixed XY in 3D.</summary>
    Vector3 TryGetFirstCovalentPartnerDirectionLocalForPiAngleOverride()
    {
        if (covalentBonds.Count <= 0) return Vector3.zero;
        var b = covalentBonds.FirstOrDefault(x => x?.AtomA != null && x?.AtomB != null);
        if (b == null) return Vector3.zero;
        var other = b.AtomA == this ? b.AtomB : b.AtomA;
        if (other == null) return Vector3.zero;
        var w = (other.transform.position - transform.position).normalized;
        if (w.sqrMagnitude < 0.01f) return Vector3.zero;
        return transform.InverseTransformDirection(w).normalized;
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
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                Vector3 axisLocal = TryGetFirstCovalentPartnerDirectionLocalForPiAngleOverride();
                if (axisLocal.sqrMagnitude >= 0.01f)
                {
                    axisLocal.Normalize();
                    Vector3 refUp = Mathf.Abs(Vector3.Dot(axisLocal, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
                    Vector3 u = Vector3.Cross(axisLocal, refUp);
                    if (u.sqrMagnitude < 1e-10f)
                    {
                        refUp = Vector3.forward;
                        u = Vector3.Cross(axisLocal, refUp);
                    }
                    u.Normalize();
                    Vector3 v = Vector3.Cross(axisLocal, u).normalized;
                    float angRad = NormalizeAngleTo360(piBondAngleOverride.Value) * Mathf.Deg2Rad;
                    return (Mathf.Cos(angRad) * u + Mathf.Sin(angRad) * v).normalized;
                }
            }
            float a2 = NormalizeAngleTo360(piBondAngleOverride.Value) * Mathf.Deg2Rad;
            var worldXy = new Vector3(Mathf.Cos(a2), Mathf.Sin(a2), 0f);
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

    /// <summary>Lobe +X as a unit vector in <b>this nucleus</b> local space (non-bond: parent is nucleus; σ on bond: world tip → nucleus local).</summary>
    Vector3 OrbitalTipDirectionInNucleusLocal(ElectronOrbitalFunction orb)
    {
        if (orb == null) return Vector3.right;
        if (orb.transform.parent == transform)
            return OrbitalTipLocalDirection(orb).normalized;
        Vector3 tipW = orb.transform.TransformDirection(Vector3.right);
        if (tipW.sqrMagnitude < 1e-10f) return Vector3.forward;
        return transform.InverseTransformDirection(tipW.normalized).normalized;
    }

    /// <summary>
    /// VSEPR vertex directions are in <b>this nucleus</b> local space (same as lone <see cref="OrbitalTipLocalDirection"/>).
    /// <see cref="ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection"/> expects <paramref name="idealDirNucleusLocal"/> in the orbital's <b>parent</b> local space (nucleus or <see cref="CovalentBond"/>).
    /// When the parent is a bond, converts nucleus → bond so hybrid +X, joint rotation, and <see cref="GetRedistributeTargetHybridTipWorldFromTuple"/> agree with nonbonding domains.
    /// </summary>
    (Vector3 pos, Quaternion rot) GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(
        ElectronOrbitalFunction orb, Vector3 idealDirNucleusLocal, float bondRadiusForSlot, Quaternion preferClosestLocalRotation)
    {
        Vector3 dirLocal = idealDirNucleusLocal.sqrMagnitude < 1e-14f ? Vector3.right : idealDirNucleusLocal.normalized;
        if (orb != null)
        {
            Transform p = orb.transform.parent;
            if (p != null && p != transform)
            {
                Vector3 w = transform.TransformDirection(dirLocal);
                dirLocal = p.InverseTransformDirection(w).normalized;
            }
        }
        return ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(dirLocal, bondRadiusForSlot, preferClosestLocalRotation);
    }

    /// <summary>
    /// π-step trigonal: canonical slot rotation can place +X on the opposite hemisphere of the pre-redist lobe in parent space (FromToRotation + 90° roll tie-break). Apply 180° about orbital-local Y so the lobe stays in the same hemisphere; matches user expectation when the only error is a 180° flip vs =O / neighbor framework.
    /// </summary>
    /// <param name="nucleusIdealDir">When set, skip the flip if it would worsen +X alignment to this VSEPR target in nucleus space (runtime: O lone movers had preTipToIdealDeg=0 and post 180 after flip).</param>
    void MaybeFlipPiTrigonalCanonicalSlotRotForParentHemisphereContinuity(
        ElectronOrbitalFunction orb, ref Quaternion slotLocalRot, Vector3? nucleusIdealDir = null)
    {
        // DISABLED (user request): optional 180° about slot +Y — can misread as off-plane / misleading rotation vs trigonal template.
        _ = orb;
        _ = slotLocalRot;
        _ = nucleusIdealDir;
    }

    /// <summary>
    /// +X tip direction of a slot pose (<paramref name="slotLocalRot"/>) in **this nucleus** local space (canonical lobe axis).
    /// </summary>
    Vector3 OrbitalSlotPlusXInNucleusLocal(ElectronOrbitalFunction orb, Quaternion slotLocalRot)
    {
        if (orb == null || orb.transform.parent == null) return Vector3.zero;
        Vector3 tipInParent = (slotLocalRot * Vector3.right).normalized;
        Vector3 tipWorld = orb.transform.parent.TransformDirection(tipInParent);
        if (tipWorld.sqrMagnitude < 1e-12f) return Vector3.zero;
        return transform.InverseTransformDirection(tipWorld.normalized).normalized;
    }

    /// <summary>
    /// First VSEPR vertex direction for <see cref="TryBuildRedistributeTargets3DGuideGroupPrefix"/>: bond guides use the internuclear axis only (not lobe +X, which can remain anti-parallel to the partner after bond formation/break). Ex-bond lone/empty guides use the measured vector from this nucleus pivot to the orbital transform (then lobe tip fallback), not saved lobe quaternion alone.
    /// </summary>
    Vector3 GuideGroupFirstVertexDirectionNucleusLocal(ElectronOrbitalFunction guide, CovalentBond guideBond)
    {
        if (guide == null) return Vector3.right;
        if (guideBond != null && guideBond.AtomA != null && guideBond.AtomB != null)
        {
            var partner = guideBond.AtomA == this ? guideBond.AtomB : guideBond.AtomA;
            if (partner != null)
            {
                Vector3 w = partner.transform.position - transform.position;
                if (w.sqrMagnitude > 1e-14f)
                    return transform.InverseTransformDirection(w.normalized).normalized;
            }
        }
        Vector3 offsetW = guide.transform.position - transform.position;
        if (offsetW.sqrMagnitude > 1e-14f)
            return transform.InverseTransformDirection(offsetW.normalized).normalized;
        Vector3 tipWorld = guide.transform.position + guide.transform.TransformDirection(Vector3.right) * bondRadius;
        Vector3 towardTipW = tipWorld - transform.position;
        if (towardTipW.sqrMagnitude > 1e-14f)
            return transform.InverseTransformDirection(towardTipW.normalized).normalized;
        Vector3 tipW = guide.transform.TransformDirection(Vector3.right);
        if (tipW.sqrMagnitude < 1e-10f) return Vector3.forward;
        return transform.InverseTransformDirection(tipW.normalized).normalized;
    }

    /// <summary>
    /// Unit internuclear axis from this nucleus toward the σ bond partner, in **this nucleus** local space.
    /// Falls back to orbital-tip direction when endpoints are missing or degenerate (same convention as trigonal perm resolver).
    /// </summary>
    Vector3 InternuclearSigmaAxisNucleusLocalForBond(CovalentBond cb)
    {
        if (cb == null) return Vector3.forward;
        if (cb.AtomA == null || cb.AtomB == null)
            return OrbitalTipDirectionInNucleusLocal(cb.Orbital).normalized;
        var partner = cb.AtomA == this ? cb.AtomB : cb.AtomA;
        if (partner == null)
            return OrbitalTipDirectionInNucleusLocal(cb.Orbital).normalized;
        Vector3 w = partner.transform.position - transform.position;
        if (w.sqrMagnitude < 1e-14f)
            return OrbitalTipDirectionInNucleusLocal(cb.Orbital).normalized;
        return transform.InverseTransformDirection(w.normalized).normalized;
    }

    /// <summary>
    /// π trigonal guide: σ-line occupied movers first (partner Z, then InstanceID), then π/lone — stable mover index vs perm-sync lead.
    /// </summary>
    int CompareGuideGroupOccPiTrigonalFor2(ElectronOrbitalFunction a, ElectronOrbitalFunction b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return 1;
        if (b == null) return -1;
        bool sa = a.Bond is CovalentBond cba && cba.IsSigmaBondLine();
        bool sb = b.Bond is CovalentBond cbb && cbb.IsSigmaBondLine();
        if (sa != sb)
            return sa ? -1 : 1;
        if (sa && a.Bond is CovalentBond ca && b.Bond is CovalentBond cb)
        {
            var pa = ca.AtomA == this ? ca.AtomB : ca.AtomA;
            var pb = cb.AtomA == this ? cb.AtomB : cb.AtomA;
            int za = pa != null ? pa.AtomicNumber : 0;
            int zb = pb != null ? pb.AtomicNumber : 0;
            int c = za.CompareTo(zb);
            if (c != 0) return c;
            int ia = pa != null ? pa.GetInstanceID() : 0;
            int ib = pb != null ? pb.GetInstanceID() : 0;
            c = ia.CompareTo(ib);
            if (c != 0) return c;
        }
        return a.GetInstanceID().CompareTo(b.GetInstanceID());
    }

    void LogPiTrigonalOcoLine(string detailNoTag)
    {
        string msg = "[pi-trigonal-oco] " + detailNoTag;
        Debug.Log(msg);
        ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson("pi-trigonal-oco", "AtomFunction", msg, "{}");
    }

    /// <summary>
    /// π endpoints use the **same** handedness; per-nucleus <paramref name="guideAxisVertex0NucleusLocal"/> points along the bond
    /// but from opposite ends, so local <c>Dot(g, Cross(...))</c> alone can flip one endpoint’s perm vs the partner.
    /// </summary>
    bool TryResolveTrigonalTwoSigmaMoverPermUsingInternuclearAxes(
        List<ElectronOrbitalFunction> movers,
        List<Vector3> targetDirsNucleusLocal,
        Vector3 guideAxisVertex0NucleusLocal,
        CovalentBond piOpBondForUnifiedCrossWorld,
        out int[] perm,
        out bool usedUnifiedBondWorldCross)
    {
        perm = null;
        usedUnifiedBondWorldCross = false;
        if (movers == null || targetDirsNucleusLocal == null || movers.Count != 2 || targetDirsNucleusLocal.Count != 2)
            return false;
        if (movers[0] == null || movers[1] == null) return false;
        var b0 = movers[0].Bond as CovalentBond;
        var b1 = movers[1].Bond as CovalentBond;
        if (b0 == null || b1 == null || !b0.IsSigmaBondLine() || !b1.IsSigmaBondLine()) return false;

        Vector3 g = guideAxisVertex0NucleusLocal;
        if (g.sqrMagnitude < 1e-14f) return false;
        g.Normalize();

        int PartnerInst(CovalentBond cb)
        {
            var partner = cb.AtomA == this ? cb.AtomB : cb.AtomA;
            return partner != null ? partner.GetInstanceID() : int.MinValue;
        }

        Vector3 RejectFromUnit(Vector3 v, Vector3 unitAxis)
        {
            return v - unitAxis * Vector3.Dot(v, unitAxis);
        }

        Vector3 RejectFromG(Vector3 unitOrDir) => RejectFromUnit(unitOrDir, g);

        bool TryUnifiedBondDirWorldForPiPerm(CovalentBond opBond, out Vector3 dirW)
        {
            dirW = default;
            if (opBond == null || opBond.AtomA == null || opBond.AtomB == null) return false;
            var pa = opBond.AtomA;
            var pb = opBond.AtomB;
            bool tailIsA = pa.AtomicNumber < pb.AtomicNumber
                || (pa.AtomicNumber == pb.AtomicNumber && pa.GetInstanceID() <= pb.GetInstanceID());
            var tail = tailIsA ? pa : pb;
            var head = tailIsA ? pb : pa;
            Vector3 w = head.transform.position - tail.transform.position;
            if (w.sqrMagnitude < 1e-16f) return false;
            dirW = w.normalized;
            return true;
        }

        Vector3 a0 = InternuclearSigmaAxisNucleusLocalForBond(b0);
        Vector3 a1 = InternuclearSigmaAxisNucleusLocalForBond(b1);
        Vector3 t0 = targetDirsNucleusLocal[0].sqrMagnitude < 1e-14f ? Vector3.forward : targetDirsNucleusLocal[0].normalized;
        Vector3 t1 = targetDirsNucleusLocal[1].sqrMagnitude < 1e-14f ? Vector3.forward : targetDirsNucleusLocal[1].normalized;

        int p0 = PartnerInst(b0);
        int p1 = PartnerInst(b1);
        const float crossEps = 1e-5f;
        const float perpEpsSq = 1e-12f;

        Vector3 uWorld = default;
        bool haveBondW = piOpBondForUnifiedCrossWorld != null
            && TryUnifiedBondDirWorldForPiPerm(piOpBondForUnifiedCrossWorld, out uWorld);

        bool useWorldPlaneCross = false;
        if (haveBondW)
        {
            Vector3 a0w = transform.TransformDirection(a0);
            Vector3 a1w = transform.TransformDirection(a1);
            Vector3 t0w = transform.TransformDirection(t0);
            Vector3 t1w = transform.TransformDirection(t1);
            Vector3 pA0w = RejectFromUnit(a0w, uWorld);
            Vector3 pA1w = RejectFromUnit(a1w, uWorld);
            Vector3 pT0w = RejectFromUnit(t0w, uWorld);
            Vector3 pT1w = RejectFromUnit(t1w, uWorld);
            useWorldPlaneCross = pA0w.sqrMagnitude >= perpEpsSq && pA1w.sqrMagnitude >= perpEpsSq
                && pT0w.sqrMagnitude >= perpEpsSq && pT1w.sqrMagnitude >= perpEpsSq;
        }

        int[] mOrd;
        int[] tOrd;
        if (useWorldPlaneCross)
        {
            usedUnifiedBondWorldCross = true;
            Vector3 a0w = transform.TransformDirection(a0);
            Vector3 a1w = transform.TransformDirection(a1);
            Vector3 t0w = transform.TransformDirection(t0);
            Vector3 t1w = transform.TransformDirection(t1);
            Vector3 pA0w = RejectFromUnit(a0w, uWorld);
            Vector3 pA1w = RejectFromUnit(a1w, uWorld);
            Vector3 pT0w = RejectFromUnit(t0w, uWorld);
            Vector3 pT1w = RejectFromUnit(t1w, uWorld);
            float sM = Vector3.Dot(uWorld, Vector3.Cross(pA0w.normalized, pA1w.normalized));
            if (sM > crossEps) mOrd = new[] { 0, 1 };
            else if (sM < -crossEps) mOrd = new[] { 1, 0 };
            else
                mOrd = p0 <= p1 ? new[] { 0, 1 } : new[] { 1, 0 };

            float sT = Vector3.Dot(uWorld, Vector3.Cross(pT0w.normalized, pT1w.normalized));
            if (sT > crossEps) tOrd = new[] { 0, 1 };
            else if (sT < -crossEps) tOrd = new[] { 1, 0 };
            else
                tOrd = new[] { 0, 1 };
        }
        else
        {
            usedUnifiedBondWorldCross = false;
            Vector3 pA0 = RejectFromG(a0);
            Vector3 pA1 = RejectFromG(a1);
            if (pA0.sqrMagnitude < perpEpsSq || pA1.sqrMagnitude < perpEpsSq)
                mOrd = p0 <= p1 ? new[] { 0, 1 } : new[] { 1, 0 };
            else
            {
                float s = Vector3.Dot(g, Vector3.Cross(pA0.normalized, pA1.normalized));
                if (s > crossEps) mOrd = new[] { 0, 1 };
                else if (s < -crossEps) mOrd = new[] { 1, 0 };
                else
                    mOrd = p0 <= p1 ? new[] { 0, 1 } : new[] { 1, 0 };
            }

            Vector3 pT0 = RejectFromG(t0);
            Vector3 pT1 = RejectFromG(t1);
            if (pT0.sqrMagnitude < perpEpsSq || pT1.sqrMagnitude < perpEpsSq)
                tOrd = new[] { 0, 1 };
            else
            {
                float sTloc = Vector3.Dot(g, Vector3.Cross(pT0.normalized, pT1.normalized));
                if (sTloc > crossEps) tOrd = new[] { 0, 1 };
                else if (sTloc < -crossEps) tOrd = new[] { 1, 0 };
                else
                    tOrd = new[] { 0, 1 };
            }
        }

        perm = new int[2];
        perm[mOrd[0]] = tOrd[0];
        perm[mOrd[1]] = tOrd[1];

        return true;
    }

    /// <summary>σ bond-break pin lobe tip in <b>this nucleus</b> local space (+X along lobe), even if the orbital is still parented under a bond.</summary>
    Vector3 ExBondPinOrbitalTipLocalOnNucleus(ElectronOrbitalFunction pinOrb)
    {
        if (pinOrb == null) return Vector3.forward;
        if (pinOrb.transform.parent == transform)
            return OrbitalTipLocalDirection(pinOrb).normalized;
        Vector3 tipW = pinOrb.transform.TransformDirection(Vector3.right);
        if (tipW.sqrMagnitude < 1e-10f) return Vector3.forward;
        return transform.InverseTransformDirection(tipW.normalized).normalized;
    }

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

    /// <summary>Work-plane angles in [0,360): min of |o−t|, |o−(t−360)|, |o−(t+360)| — same idea as comparing raw separation vs shifting the target by one turn before summing over a permutation.</summary>
    static float PlanarAbsDeltaMinRawMinus360Plus360PairDeg(float oDeg, float tDeg)
    {
        float o = NormalizeAngleTo360(oDeg);
        float t = NormalizeAngleTo360(tDeg);
        return Mathf.Min(
            Mathf.Abs(o - t),
            Mathf.Abs(o - (t - 360f)),
            Mathf.Abs(o - (t + 360f)));
    }

    /// <summary>For one assignment permutation: Σ|o−t|, Σ|o−(t−360)|, Σ|o−(t+360)| with o,t∈[0,360); take the smallest total (then compare across permutations).</summary>
    static float PlanarAnglePermutationCostMinOfThreeSums(IReadOnlyList<float> oldDeg, IReadOnlyList<float> targetDeg, int[] perm)
    {
        int n = oldDeg.Count;
        float s0 = 0f, s1 = 0f, s2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float o = NormalizeAngleTo360(oldDeg[i]);
            float t = NormalizeAngleTo360(targetDeg[perm[i]]);
            s0 += Mathf.Abs(o - t);
            s1 += Mathf.Abs(o - (t - 360f));
            s2 += Mathf.Abs(o - (t + 360f));
        }
        return Mathf.Min(s0, s1, s2);
    }

    static float Atan2DegXY(Vector3 v) => Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;

    static float QuaternionSlotCostOnly(ElectronOrbitalFunction orb, Vector3 targetDirLocal, float bondRadius)
    {
        if (orb == null) return 0f;
        var (_, r) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(targetDirLocal.normalized, bondRadius, orb.transform.localRotation);
        return Quaternion.Angle(orb.transform.localRotation, r);
    }

    static float QuaternionSlotCostOnlyFromSlotRot(ElectronOrbitalFunction orb, Quaternion slotLocalRot, float bondRadius)
    {
        if (orb == null) return 0f;
        Vector3 d = (slotLocalRot * Vector3.right).normalized;
        return QuaternionSlotCostOnly(orb, d, bondRadius);
    }

    /// <summary>Matches 2D formation: planar wrap via ±360° shifts; full 3D uses cone angle.</summary>
    static float FormationStyleTipToTipCost(Vector3 oldTipLocal, Vector3 newTipLocal)
    {
        oldTipLocal.Normalize();
        newTipLocal.Normalize();
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return Vector3.Angle(oldTipLocal, newTipLocal);
        return PlanarAbsDeltaMinRawMinus360Plus360PairDeg(Atan2DegXY(oldTipLocal), Atan2DegXY(newTipLocal));
    }

    /// <summary>
    /// Full-3D permutation cone cost: σ-line movers use directed +X, <see cref="Vector3.Angle"/> in [0,180].
    /// π / lone movers use an undirected lobe axis: min(θ, 180°−θ) so a 138° tip–target separation costs 42° (either lobe can face the domain).
    /// </summary>
    static float PiPermutationConeAngleTipToTargetDeg(ElectronOrbitalFunction orb, Vector3 tipNucleusUnit, Vector3 targetNucleusUnit)
    {
        if (tipNucleusUnit.sqrMagnitude < 1e-14f || targetNucleusUnit.sqrMagnitude < 1e-14f) return 0f;
        float a = Vector3.Angle(tipNucleusUnit.normalized, targetNucleusUnit.normalized);
        if (orb != null && orb.Bond is CovalentBond cb && cb.IsSigmaBondLine())
            return a;
        return Mathf.Min(a, 180f - a);
    }

    /// <summary>σ redistribution / bond break: cost for assigning a physical lobe to a target hybrid direction — same ingredients as formation (shortest planar angle when 2D) plus in-plane quaternion move after canonical slot.</summary>
    static float FormationStyleOrbitalToDirSlotCost(ElectronOrbitalFunction orb, Vector3 targetDirLocal, float bondRadius)
    {
        if (orb == null) return 0f;
        targetDirLocal.Normalize();
        Vector3 ot = OrbitalTipLocalDirection(orb).normalized;
        float dirCost = FormationStyleTipToTipCost(ot, targetDirLocal);
        var (_, r) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(targetDirLocal, bondRadius, orb.transform.localRotation);
        return dirCost + Quaternion.Angle(orb.transform.localRotation, r);
    }

    static float FormationStyleOrbitalToSlotPoseCost(ElectronOrbitalFunction orb, Quaternion slotLocalRot)
    {
        if (orb == null) return 0f;
        Vector3 nt = (slotLocalRot * Vector3.right).normalized;
        Vector3 ot = OrbitalTipLocalDirection(orb).normalized;
        float dirCost = FormationStyleTipToTipCost(ot, nt);
        return dirCost + Quaternion.Angle(orb.transform.localRotation, slotLocalRot);
    }

    static string JsonEscapeForNdjson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    static string FormatPermCostDirsRounded(List<Vector3> dirs)
    {
        if (dirs == null || dirs.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < dirs.Count; i++)
        {
            if (i > 0) sb.Append(';');
            Vector3 d = dirs[i];
            sb.Append(d.x.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(d.y.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(d.z.ToString("F4", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    static string FormatPermCostTipsForOrbs(List<ElectronOrbitalFunction> orbs, AtomFunction tipSpaceNucleus)
    {
        if (orbs == null || orbs.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < orbs.Count; i++)
        {
            if (i > 0) sb.Append(';');
            Vector3 ot = tipSpaceNucleus != null
                ? tipSpaceNucleus.OrbitalTipDirectionInNucleusLocal(orbs[i])
                : OrbitalTipLocalDirection(orbs[i]);
            if (ot.sqrMagnitude > 1e-14f) ot.Normalize();
            sb.Append(ot.x.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(ot.y.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                .Append(ot.z.ToString("F4", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // #region agent log
    static void AppendPermCostInvariantNdjson(
        string hypothesisId,
        string location,
        string message,
        int atomInstanceId,
        string phase,
        float coneOrPlanarDeg,
        float quatDeg,
        float totalDeg,
        int[] perm,
        string targetDirsRounded,
        string tipDirsRounded,
        float thetaDeg,
        string twistAxisRounded)
    {
        if (!DebugLogPermCostInvariantNdjson) return;
        try
        {
            string permJson = "null";
            if (perm != null && perm.Length > 0)
            {
                var pb = new StringBuilder();
                for (int i = 0; i < perm.Length; i++)
                {
                    if (i > 0) pb.Append(',');
                    pb.Append(perm[i].ToString(CultureInfo.InvariantCulture));
                }
                permJson = "[" + pb + "]";
            }
            long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string sid = DebugPermCostNdjsonSessionId ?? "";
            var sb = new StringBuilder(512);
            sb.Append("{\"sessionId\":\"").Append(JsonEscapeForNdjson(sid)).Append("\",\"hypothesisId\":\"").Append(JsonEscapeForNdjson(hypothesisId))
                .Append("\",\"location\":\"").Append(JsonEscapeForNdjson(location)).Append("\",\"message\":\"").Append(JsonEscapeForNdjson(message))
                .Append("\",\"timestamp\":").Append(ts.ToString(CultureInfo.InvariantCulture))
                .Append(",\"data\":{\"atomInstanceId\":").Append(atomInstanceId).Append(",\"phase\":\"").Append(JsonEscapeForNdjson(phase))
                .Append("\",\"coneOrPlanarDeg\":").Append(coneOrPlanarDeg.ToString("F4", CultureInfo.InvariantCulture))
                .Append(",\"quatDeg\":").Append(quatDeg.ToString("F4", CultureInfo.InvariantCulture))
                .Append(",\"totalDeg\":").Append(totalDeg.ToString("F4", CultureInfo.InvariantCulture))
                .Append(",\"perm\":").Append(permJson)
                .Append(",\"targetDirsNuc\":\"").Append(JsonEscapeForNdjson(targetDirsRounded)).Append("\",\"tipDirsNuc\":\"").Append(JsonEscapeForNdjson(tipDirsRounded))
                .Append("\",\"thetaDeg\":").Append(thetaDeg.ToString("F4", CultureInfo.InvariantCulture))
                .Append(",\"twistAxis\":\"").Append(JsonEscapeForNdjson(twistAxisRounded)).Append("\"}}");
            File.AppendAllText(PermCostInvariantNdjsonPath, sb.ToString() + "\n");
        }
        catch
        {
            /* ingest file may be missing or locked */
        }
    }

    // #endregion

    /// <summary>Cone (or planar wrap) sum and quaternion slot sum; <paramref name="totalDeg"/> = sum of both.</summary>
    static void ComputePermutationAssignmentCostBreakdown(
        List<ElectronOrbitalFunction> orbs,
        List<Vector3> targetDirs,
        float bondRadius,
        AtomFunction tipSpaceNucleus,
        int[] perm,
        out float coneOrPlanarDeg,
        out float quatSumDeg,
        out float totalDeg)
    {
        coneOrPlanarDeg = 0f;
        quatSumDeg = 0f;
        totalDeg = float.MaxValue;
        int n = orbs != null ? orbs.Count : 0;
        if (perm == null || n == 0 || targetDirs == null || targetDirs.Count != n || perm.Length != n)
            return;
        bool full3D = OrbitalAngleUtility.UseFull3DOrbitalGeometry;
        for (int i = 0; i < n; i++)
        {
            if (tipSpaceNucleus != null && orbs[i] != null && orbs[i].Bond != null)
                continue;
            quatSumDeg += QuaternionSlotCostOnly(orbs[i], targetDirs[perm[i]], bondRadius);
        }
        if (full3D)
        {
            for (int i = 0; i < n; i++)
            {
                Vector3 td = targetDirs[perm[i]].normalized;
                Vector3 ot = tipSpaceNucleus != null
                    ? tipSpaceNucleus.OrbitalTipDirectionInNucleusLocal(orbs[i])
                    : OrbitalTipLocalDirection(orbs[i]).normalized;
                coneOrPlanarDeg += PiPermutationConeAngleTipToTargetDeg(orbs[i], ot, td);
            }
            totalDeg = coneOrPlanarDeg + quatSumDeg;
            return;
        }
        var oldDeg = new List<float>(n);
        var tgtDeg = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 ot = tipSpaceNucleus != null
                ? tipSpaceNucleus.OrbitalTipDirectionInNucleusLocal(orbs[i])
                : OrbitalTipLocalDirection(orbs[i]).normalized;
            oldDeg.Add(Atan2DegXY(ot.normalized));
        }
        for (int i = 0; i < n; i++)
            tgtDeg.Add(Atan2DegXY(targetDirs[i].normalized));
        coneOrPlanarDeg = PlanarAnglePermutationCostMinOfThreeSums(oldDeg, tgtDeg, perm);
        totalDeg = coneOrPlanarDeg + quatSumDeg;
    }

    /// <summary>Same total as minimized by <see cref="FindBestOrbitalToTargetDirsPermutation"/> for a fixed assignment <paramref name="perm"/> (<c>orb i</c> → <c>targetDirs[perm[i]]</c>).</summary>
    static float ComputePermutationAssignmentTotalCost(
        List<ElectronOrbitalFunction> orbs,
        List<Vector3> targetDirs,
        float bondRadius,
        AtomFunction tipSpaceNucleus,
        int[] perm)
    {
        ComputePermutationAssignmentCostBreakdown(orbs, targetDirs, bondRadius, tipSpaceNucleus, perm, out _, out _, out float total);
        return total;
    }

    /// <summary>
    /// Spins ideal/template target directions about <paramref name="twistAxisNucleusLocal"/> (nucleus-local) to minimize
    /// <see cref="ComputePermutationAssignmentTotalCost"/> after an optimal permutation — reduces world-orientation / azimuth
    /// dependence for the same rigid electron geometry.
    /// </summary>
    void MinimizeTargetDirsAzimuthForPermutationCostInPlace(
        List<ElectronOrbitalFunction> orbs,
        List<Vector3> targetDirs,
        Vector3 twistAxisNucleusLocal,
        int gridSteps = 36,
        float minMeaningfulImprove = 0.05f)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;
        if (orbs == null || targetDirs == null || orbs.Count < 2 || orbs.Count != targetDirs.Count) return;
        if (orbs.Count > 5) return;

        Vector3 a = twistAxisNucleusLocal.normalized;
        if (a.sqrMagnitude < 1e-10f) return;

        var baseDirs = new List<Vector3>(targetDirs.Count);
        foreach (var d in targetDirs) baseDirs.Add(d.normalized);

        var work = new List<Vector3>(targetDirs.Count);
        int[] permBaseline = FindBestOrbitalToTargetDirsPermutation(orbs, baseDirs, bondRadius, this);
        if (permBaseline == null) return;
        ComputePermutationAssignmentCostBreakdown(orbs, baseDirs, bondRadius, this, permBaseline, out float cone0, out float quat0, out float baseline);
        string axisStr = FormatPermCostDirsRounded(new List<Vector3> { a });
        // #region agent log
        AppendPermCostInvariantNdjson(
            "permCostMinimize",
            "AtomFunction.MinimizeTargetDirsAzimuthForPermutationCostInPlace",
            "baseline_before_azimuth_grid",
            GetInstanceID(),
            "baseline",
            cone0,
            quat0,
            baseline,
            permBaseline,
            FormatPermCostDirsRounded(baseDirs),
            FormatPermCostTipsForOrbs(orbs, this),
            0f,
            axisStr);
        // #endregion

        // DISABLED (user request): θ grid twist of template targetDirs — misleading off-template rotation; baseline dirs unchanged.
        _ = gridSteps;
        _ = minMeaningfulImprove;
        _ = work;
        return;
    }

    /// <returns>perm where orb <c>i</c> is assigned target direction <c>targetDirs[perm[i]]</c>.</returns>
    /// <param name="tipSpaceNucleus">When set, old tip directions for cost are expressed in this nucleus's local space (σ orbitals on bonds). Quaternion slot cost is skipped for bond-parented orbitals (targets are nucleus-local).</param>
    static int[] FindBestOrbitalToTargetDirsPermutation(List<ElectronOrbitalFunction> orbs, List<Vector3> targetDirs, float bondRadius, AtomFunction tipSpaceNucleus = null)
    {
        int n = orbs.Count;
        if (n != targetDirs.Count || n == 0) return null;
        int[] idx = Enumerable.Range(0, n).ToArray();
        int[] bestPerm = null;
        float bestTotal = float.MaxValue;
        const float eps = 1e-3f;

        foreach (var perm in Permutations(idx))
        {
            float total = ComputePermutationAssignmentTotalCost(orbs, targetDirs, bondRadius, tipSpaceNucleus, perm);
            bool better = bestPerm == null
                || total < bestTotal - eps
                || (Mathf.Abs(total - bestTotal) <= eps && ComparePermutationLex(perm, bestPerm) < 0);
            if (better)
            {
                bestTotal = total;
                bestPerm = (int[])perm.Clone();
            }
        }
        return bestPerm;
    }

#if UNITY_EDITOR
    /// <summary>Editor sanity: rotation about axis preserves unit lengths. See Tools/OrgoSimulator menu.</summary>
    public static bool EditorSelfCheck_TemplateTwistRotationPreservesUnitDirs()
    {
        Vector3 axis = Vector3.up;
        var dirs = new List<Vector3> { Vector3.forward.normalized, Vector3.right.normalized };
        Quaternion R = Quaternion.AngleAxis(47f, axis);
        foreach (var d in dirs)
        {
            Vector3 r = (R * d).normalized;
            if (Mathf.Abs(r.magnitude - 1f) > 1e-4f) return false;
        }
        return true;
    }
#endif

    /// <summary>Bond-break / break-ref redistribution: permute which physical lobe gets which computed slot so Σ∠(old tip, new tip) is minimal (pin lobe unchanged).</summary>
    void RematchRedistributeTargetSlotsMinAngularMotion(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list, ElectronOrbitalFunction pinOrbital)
    {
        if (list == null || list.Count <= 1) return;
        var movableIdx = new List<int>();
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i].orb;
            if (o == null || o.transform.parent != transform) continue;
            if (pinOrbital != null && o == pinOrbital) continue;
            movableIdx.Add(i);
        }
        if (movableIdx.Count <= 1) return;

        int n = movableIdx.Count;
        var slotPos = new Vector3[n];
        var slotRot = new Quaternion[n];
        for (int k = 0; k < n; k++)
        {
            int i = movableIdx[k];
            slotPos[k] = list[i].pos;
            slotRot[k] = list[i].rot;
        }

        int[] idx = Enumerable.Range(0, n).ToArray();
        float bestTotal = float.MaxValue;
        int[] bestPerm = null;
        const float angleEps = 1e-3f;
        bool full3D = OrbitalAngleUtility.UseFull3DOrbitalGeometry;
        var oldDeg = new List<float>(n);
        var slotDeg = new List<float>(n);
        for (int k = 0; k < n; k++)
            oldDeg.Add(Atan2DegXY(OrbitalTipLocalDirection(list[movableIdx[k]].orb).normalized));
        for (int k = 0; k < n; k++)
            slotDeg.Add(Atan2DegXY((slotRot[k] * Vector3.right).normalized));

        foreach (var perm in Permutations(idx))
        {
            float total;
            float quatSum = 0f;
            for (int a = 0; a < n; a++)
                quatSum += QuaternionSlotCostOnlyFromSlotRot(list[movableIdx[a]].orb, slotRot[perm[a]], bondRadius);
            if (full3D)
            {
                float coneSum = 0f;
                for (int a = 0; a < n; a++)
                {
                    var o = list[movableIdx[a]].orb;
                    Vector3 nt = (slotRot[perm[a]] * Vector3.right).normalized;
                    Vector3 ot = OrbitalTipLocalDirection(o).normalized;
                    coneSum += PiPermutationConeAngleTipToTargetDeg(o, ot, nt);
                }
                total = coneSum + quatSum;
            }
            else
                total = PlanarAnglePermutationCostMinOfThreeSums(oldDeg, slotDeg, perm) + quatSum;
            bool better = bestPerm == null
                || total < bestTotal - angleEps
                || (Mathf.Abs(total - bestTotal) <= angleEps && ComparePermutationLex(perm, bestPerm) < 0);
            if (better)
            {
                bestTotal = total;
                bestPerm = (int[])perm.Clone();
            }
        }
        if (bestPerm == null) return;

        for (int k = 0; k < n; k++)
        {
            int li = movableIdx[k];
            int s = bestPerm[k];
            var o = list[li].orb;
            list[li] = (o, slotPos[s], slotRot[s]);
        }
    }

    void RematchLoneOrbitalTargetDirectionsMinAngularMotion(IReadOnlyList<ElectronOrbitalFunction> orbs, IList<Vector3> targetDirsLocal)
    {
        if (orbs == null || targetDirsLocal == null || orbs.Count != targetDirsLocal.Count || orbs.Count <= 1) return;
        int n = orbs.Count;
        var copy = new Vector3[n];
        for (int i = 0; i < n; i++)
            copy[i] = targetDirsLocal[i];

        int[] idx = Enumerable.Range(0, n).ToArray();
        float bestTotal = float.MaxValue;
        int[] bestPerm = null;
        const float angleEps = 1e-3f;
        bool full3D = OrbitalAngleUtility.UseFull3DOrbitalGeometry;
        var oldDeg = new List<float>(n);
        var tgtDeg = new List<float>(n);
        for (int i = 0; i < n; i++)
            oldDeg.Add(Atan2DegXY(OrbitalTipLocalDirection(orbs[i]).normalized));
        for (int i = 0; i < n; i++)
            tgtDeg.Add(Atan2DegXY(copy[i].normalized));

        foreach (var perm in Permutations(idx))
        {
            float total;
            float quatSum = 0f;
            for (int a = 0; a < n; a++)
                quatSum += QuaternionSlotCostOnly(orbs[a], copy[perm[a]], bondRadius);
            if (full3D)
            {
                float coneSum = 0f;
                for (int a = 0; a < n; a++)
                {
                    Vector3 td = copy[perm[a]].normalized;
                    Vector3 ot = OrbitalTipLocalDirection(orbs[a]).normalized;
                    coneSum += Vector3.Angle(ot, td);
                }
                total = coneSum + quatSum;
            }
            else
                total = PlanarAnglePermutationCostMinOfThreeSums(oldDeg, tgtDeg, perm) + quatSum;
            bool better = bestPerm == null
                || total < bestTotal - angleEps
                || (Mathf.Abs(total - bestTotal) <= angleEps && ComparePermutationLex(perm, bestPerm) < 0);
            if (better)
            {
                bestTotal = total;
                bestPerm = (int[])perm.Clone();
            }
        }
        if (bestPerm == null) return;
        for (int k = 0; k < n; k++)
            targetDirsLocal[k] = copy[bestPerm[k]];
    }

    static bool DirectionsWithinTolerance(Vector3 a, Vector3 b, float tolDeg) =>
        Vector3.Angle(a, b) <= tolDeg;

    /// <summary>Returns (orbital, targetLocalPos, targetLocalRot) for orbitals that would move during RedistributeOrbitals. For sigma bonds, pass newBondPartner to redistribute when bonding to an orbital that was already rotated. Pass <paramref name="sigmaNeighborCountBefore"/> (σ count before the new bond is created) so a 3→4 σ increase can run tetrahedral σ-relax even when π count is unchanged and there are no occupied lone lobes (carbocation → sp³). Set <paramref name="bondingTopologyChanged"/> true on an atom only when its π count did not change (σ-only bond break) so layout still runs; when π count changes (e.g. π bond break), pass false. When <paramref name="skipLoneRedistTargetsForSigmaOnlyBreak"/> is true (σ-only break: π unchanged on both atoms), skip returning targets only if no bond-break animation context is provided — otherwise <see cref="GetRedistributeTargets3D"/> still runs so non–break lone lobes can animate. For bond-break animation, pass the same π/ref/guide args as the post-break <see cref="RedistributeOrbitals"/> call so targets match <see cref="RedistributeOrbitals3D"/> (pin lobe + reference direction). <paramref name="bondBreakIsSigmaCleavageBetweenFormerPartners"/> must match <c>RedistributeOrbitals</c> for carbocation fast-path parity. Optional <paramref name="redistributionOperationBond"/> excludes that bond when choosing the π/σ guide group in repulsion-only layout. Optional <paramref name="vseprDisappearingLoneForPredictiveCount"/> omits that occupied lobe from formation-style 3D TryMatch lone-domain inventory when <see cref="CovalentBond.OrbitalBeingFadedForCharge"/> is not enough (e.g. π donor after unbond).</summary>
    public List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> GetRedistributeTargets(int piBefore, AtomFunction newBondPartner = null, bool bondingTopologyChanged = false, bool skipLoneRedistTargetsForSigmaOnlyBreak = false, float? piBondAngleOverrideForBreakTargets = null, Vector3? refBondWorldDirectionForBreakTargets = null, ElectronOrbitalFunction bondBreakGuideLoneOrbitalForTargets = null, int sigmaNeighborCountBefore = -1, bool bondBreakIsSigmaCleavageBetweenFormerPartners = false, CovalentBond redistributionOperationBond = null, ElectronOrbitalFunction vseprDisappearingLoneForPredictiveCount = null)
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
            return GetRedistributeTargets3D(piBefore, newBondPartner, piBondAngleOverrideForBreakTargets, refBondWorldDirectionForBreakTargets, bondBreakGuideLoneOrbitalForTargets, sigmaNeighborCountBefore, bondBreakIsSigmaCleavageBetweenFormerPartners, redistributionOperationBond, vseprDisappearingLoneForPredictiveCount);

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

    /// <summary>
    /// Tetrahedral σ formation: <see cref="VseprLayout.AlignFirstDirectionTo"/> always places <paramref name="refLocal"/> on ideal vertex 0, which may not match the shell already on screen.
    /// Try all four <see cref="VseprLayout.AlignTetrahedronKthVertexTo"/> labelings and keep the one that minimizes Σ∠(lone tip, assigned ideal direction) after <see cref="TryMatchLoneOrbitalsToFreeIdealDirections"/>.
    /// </summary>
    List<Vector3> ChooseTetrahedralNewDirsForFormationMinLoneMotion(
        Vector3 refLocal,
        List<Vector3> bondAxesMerged,
        List<ElectronOrbitalFunction> loneOrbitalsOccupied,
        ElectronOrbitalFunction pinLoneOrbitalForBondBreak,
        int slotCount)
    {
        var ideal4 = VseprLayout.GetIdealLocalDirections(4);
        float bestScore = float.MaxValue;
        List<Vector3> bestDirs = null;
        int bestK = -1;
        const float scoreEps = 1e-3f;
        for (int k = 0; k < 4; k++)
        {
            var candidate = new List<Vector3>(VseprLayout.AlignTetrahedronKthVertexTo(ideal4, k, refLocal));
            if (!TryMatchLoneOrbitalsToFreeIdealDirections(
                    refLocal,
                    slotCount,
                    bondAxesMerged,
                    loneOrbitalsOccupied,
                    candidate,
                    out var mapping,
                    out _,
                    out _,
                    pinLoneOrbitalForBondBreak)
                || mapping == null)
                continue;

            float score = 0f;
            for (int i = 0; i < mapping.Count; i++)
                score += Vector3.Angle(mapping[i].oldDir.normalized, mapping[i].newDir.normalized);

            if (bestDirs == null
                || score < bestScore - scoreEps
                || (Mathf.Abs(score - bestScore) <= scoreEps && (bestK < 0 || k < bestK)))
            {
                bestScore = score;
                bestDirs = candidate;
                bestK = k;
            }
        }

        if (bestDirs != null) return bestDirs;
        return new List<Vector3>(VseprLayout.AlignFirstDirectionTo(ideal4, refLocal));
    }

    /// <summary>Which tier produced the redistribution guide orbital (<see cref="TryResolveRedistributionGuideGroupForLayout"/>); evaluation order matches enum 1–6.</summary>
    enum RedistributionGuideSource
    {
        PiBondNotInOperation = 1,
        PiBondInOperation = 2,
        SigmaBondNotInOperation = 3,
        SigmaBondInOperation = 4,
        LonePairFromOperation = 5,
        EmptyOrbitalFromOperation = 6,
    }

    /// <summary>
    /// π / operation-bond redistribution (e.g. step 2): run <see cref="GetRedistributeTargets"/> on the atom whose
    /// <see cref="TryResolveRedistributionGuideGroupForLayout"/> tier is numerically <strong>smaller</strong> first (stronger anchor);
    /// if both resolve to the same tier or neither resolves, tie-break with <strong>larger</strong> <see cref="Object.GetInstanceID"/>.
    /// Per-atom break guides are optional (π formation passes null).
    /// </summary>
    public static void OrderPairAtomsForRedistributionGuideTier(
        AtomFunction atomA,
        AtomFunction atomB,
        CovalentBond redistributionOperationBond,
        ElectronOrbitalFunction bondBreakGuideLoneOrbitalForA,
        ElectronOrbitalFunction bondBreakGuideLoneOrbitalForB,
        out AtomFunction first,
        out AtomFunction second)
    {
        if (atomA == null || atomB == null)
        {
            first = atomA ?? atomB;
            second = null;
            return;
        }
        if (atomA == atomB)
        {
            first = atomA;
            second = atomB;
            return;
        }

        bool okA = atomA.TryResolveRedistributionGuideGroupForLayout(
            redistributionOperationBond,
            bondBreakGuideLoneOrbitalForA,
            out _,
            out _,
            out RedistributionGuideSource srcA,
            out bool abortA);
        if (abortA)
            okA = false;

        bool okB = atomB.TryResolveRedistributionGuideGroupForLayout(
            redistributionOperationBond,
            bondBreakGuideLoneOrbitalForB,
            out _,
            out _,
            out RedistributionGuideSource srcB,
            out bool abortB);
        if (abortB)
            okB = false;

        const int unresolvedRank = 100;
        int rankA = okA ? (int)srcA : unresolvedRank;
        int rankB = okB ? (int)srcB : unresolvedRank;

        if (rankA != rankB)
        {
            if (rankA < rankB)
            {
                first = atomA;
                second = atomB;
            }
            else
            {
                first = atomB;
                second = atomA;
            }
            return;
        }

        int idA = atomA.GetInstanceID();
        int idB = atomB.GetInstanceID();
        if (idA > idB)
        {
            first = atomA;
            second = atomB;
        }
        else
        {
            first = atomB;
            second = atomA;
        }
    }

    /// <summary>
    /// Single guide group in strict priority: **π cleavage with operation bond** (ex-bond lobe on this nucleus along the
    /// broken edge, 0e or &gt;0e) runs before π/σ tiers so CO₂-style centers use the break axis as VSEPR vertex 0, not a
    /// remaining π. Then (1) π not in the **operation atom pair** (all multiply-bond edges to that pair count as “in op”),
    /// (2) any π edge on the operation pair (including when <c>redistributionOperationBond</c> is the σ line to that pair),
    /// (3) σ not in the operation pair, (4) σ in the operation pair; (5) ex-bond lobe with e⁻; (6) ex-bond 0e when no
    /// operation bond marks cleavage.
    /// </summary>
    /// <param name="redistributionAbortedDueToEmptyMovers">
    /// True only when Tier 1 (<see cref="RedistributionGuideSource.PiBondNotInOperation"/>) would be chosen but
    /// <see cref="CollectRedistributionGuideGroupMoversExcludingGuide"/> finds no movers — redistribution must cancel; do not fall through to Tier 2.
    /// </param>
    bool TryResolveRedistributionGuideGroupForLayout(
        CovalentBond redistributionOperationBond,
        ElectronOrbitalFunction bondBreakGuideLoneOrbitalForTargets,
        out ElectronOrbitalFunction guideOrbital,
        out CovalentBond guideBond,
        out RedistributionGuideSource guideSource,
        out bool redistributionAbortedDueToEmptyMovers)
    {
        guideOrbital = null;
        guideBond = null;
        guideSource = default;
        redistributionAbortedDueToEmptyMovers = false;

        // π bond break (3D CoLerp passes redistributionOperationBond): anchor layout to this nucleus's ex-bond lobe along
        // the cleaved edge. Carbon often receives a 0e stub (Tier 6 would run too late vs PiBondNotInOperation); use main
        // VSEPR path via LonePairFromOperation source for both 0e and &gt;0e here.
        // Host check: BreakBond sets <see cref="ElectronOrbitalFunction.SetBondedAtom"/> on both ex-bond lobes — prefer that over
        // transform ancestry (GetComponentInParent / IsChildOf) when the lobe is not under a transform that has AtomFunction.
        // Do not require <see cref="IsBondBreakGuideOrbitalWithBondingCleared"/> — stub may be absent from bondedOrbitals when slots are full.
        if (redistributionOperationBond != null
            && !redistributionOperationBond.IsSigmaBondLine()
            && (redistributionOperationBond.AtomA == this || redistributionOperationBond.AtomB == this)
            && bondBreakGuideLoneOrbitalForTargets != null
            && (bondBreakGuideLoneOrbitalForTargets.BondedAtom == this
                || bondBreakGuideLoneOrbitalForTargets.GetComponentInParent<AtomFunction>() == this))
        {
            guideOrbital = bondBreakGuideLoneOrbitalForTargets;
            guideBond = null;
            guideSource = RedistributionGuideSource.LonePairFromOperation;
            // #region agent log
            if (atomicNumber == 6)
            {
                int opId = redistributionOperationBond.GetInstanceID();
                ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                    "H1fix",
                    "AtomFunction.TryResolveRedistributionGuideGroupForLayout",
                    "pi_cleavage_ex_bond_guide",
                    "{\"atomId\":" + GetInstanceID()
                    + ",\"opBondId\":" + opId
                    + ",\"breakGuideId\":" + bondBreakGuideLoneOrbitalForTargets.GetInstanceID()
                    + ",\"breakGuideE\":" + bondBreakGuideLoneOrbitalForTargets.ElectronCount
                    + ",\"sigmaN\":" + GetDistinctSigmaNeighborCount()
                    + ",\"piN\":" + GetPiBondCount() + "}");
            }
            // #endregion
            return true;
        }

        // Tier 1 — PiBondNotInOperation: π edge on this center whose atom pair is not the operation pair.
        var piNotOp = new List<CovalentBond>();
        foreach (var b in covalentBonds)
        {
            if (b == null || b.AtomA == null || b.AtomB == null) continue;
            if (b.AtomA != this && b.AtomB != this) continue;
            if (b.IsSigmaBondLine()) continue;
            if (redistributionOperationBond != null && CovalentBondConnectsSameAtomPair(b, redistributionOperationBond)) continue;
            piNotOp.Add(b);
        }
        piNotOp.Sort((a, c) => a.GetInstanceID().CompareTo(c.GetInstanceID()));
        if (piNotOp.Count > 0 && piNotOp[0].Orbital != null)
        {
            var tier1Bond = piNotOp[0];
            var tier1Orb = tier1Bond.Orbital;
            var tier1MoversProbe = new List<ElectronOrbitalFunction>();
            CollectRedistributionGuideGroupMoversExcludingGuide(tier1Orb, tier1Bond, tier1MoversProbe, redistributionOperationBond);
            if (tier1MoversProbe.Count == 0)
            {
                redistributionAbortedDueToEmptyMovers = true;
                LogGuideGroupTrace(
                    "TryResolve ABORT tier=PiBondNotInOperation reason=no_movers opBondId="
                    + (redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID().ToString() : "null")
                    + " tier1GuideBondId=" + tier1Bond.GetInstanceID());
                return false;
            }
            guideBond = tier1Bond;
            guideOrbital = tier1Orb;
            guideSource = RedistributionGuideSource.PiBondNotInOperation;
            return true;
        }

        // Tier 2 — PiBondInOperation: any π edge on the **operation atom pair** (multiply-bond group).
        if (redistributionOperationBond != null
            && (redistributionOperationBond.AtomA == this || redistributionOperationBond.AtomB == this))
        {
            var piOnOpPair = new List<CovalentBond>();
            foreach (var b in covalentBonds)
            {
                if (b == null || b.AtomA == null || b.AtomB == null) continue;
                if (b.AtomA != this && b.AtomB != this) continue;
                if (b.IsSigmaBondLine() || b.Orbital == null) continue;
                if (!CovalentBondConnectsSameAtomPair(b, redistributionOperationBond)) continue;
                piOnOpPair.Add(b);
            }
            piOnOpPair.Sort((a, c) => a.GetInstanceID().CompareTo(c.GetInstanceID()));
            if (piOnOpPair.Count > 0)
            {
                guideBond = piOnOpPair[0];
                guideOrbital = guideBond.Orbital;
                guideSource = RedistributionGuideSource.PiBondInOperation;
                return true;
            }
        }

        // Tier 3 — SigmaBondNotInOperation: σ line on this center whose atom pair is not the operation pair.
        var sigNotOp = new List<CovalentBond>();
        foreach (var b in covalentBonds)
        {
            if (b == null || b.AtomA == null || b.AtomB == null) continue;
            if (b.AtomA != this && b.AtomB != this) continue;
            if (!b.IsSigmaBondLine()) continue;
            if (redistributionOperationBond != null && CovalentBondConnectsSameAtomPair(b, redistributionOperationBond)) continue;
            sigNotOp.Add(b);
        }
        sigNotOp.Sort((a, c) => a.GetInstanceID().CompareTo(c.GetInstanceID()));
        if (sigNotOp.Count > 0 && sigNotOp[0].Orbital != null)
        {
            guideBond = sigNotOp[0];
            guideOrbital = guideBond.Orbital;
            guideSource = RedistributionGuideSource.SigmaBondNotInOperation;
            // #region agent log
            if (redistributionOperationBond != null
                && redistributionOperationBond.IsSigmaBondLine()
                && (redistributionOperationBond.AtomA == this || redistributionOperationBond.AtomB == this))
            {
                ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                    "H8",
                    "AtomFunction.TryResolveRedistributionGuideGroupForLayout",
                    "sigma_guide_selected_not_operation",
                    "{\"atomId\":" + GetInstanceID()
                    + ",\"selectedGuideBondId\":" + guideBond.GetInstanceID()
                    + ",\"opBondId\":" + redistributionOperationBond.GetInstanceID()
                    + ",\"sigNotOpCount\":" + sigNotOp.Count
                    + ",\"sigNotOpFirstId\":" + sigNotOp[0].GetInstanceID()
                    + ",\"source\":\"SigmaBondNotInOperation\"}");
            }
            // #endregion
            return true;
        }

        // Tier 4 — SigmaBondInOperation: σ line equals redistributionOperationBond.
        if (redistributionOperationBond != null
            && redistributionOperationBond.IsSigmaBondLine()
            && (redistributionOperationBond.AtomA == this || redistributionOperationBond.AtomB == this)
            && redistributionOperationBond.Orbital != null)
        {
            guideBond = redistributionOperationBond;
            guideOrbital = guideBond.Orbital;
            guideSource = RedistributionGuideSource.SigmaBondInOperation;
            // #region agent log
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H8",
                "AtomFunction.TryResolveRedistributionGuideGroupForLayout",
                "sigma_guide_selected_operation",
                "{\"atomId\":" + GetInstanceID()
                + ",\"selectedGuideBondId\":" + guideBond.GetInstanceID()
                + ",\"opBondId\":" + redistributionOperationBond.GetInstanceID()
                + ",\"source\":\"SigmaBondInOperation\"}");
            // #endregion
            return true;
        }

        // Tier 5 — LonePairFromOperation: ex-bond lobe with e⁻ after single-bond break (no CovalentBond reference required).
        if (bondBreakGuideLoneOrbitalForTargets != null
            && bondBreakGuideLoneOrbitalForTargets.ElectronCount > 0
            && IsBondBreakGuideOrbitalWithBondingCleared(bondBreakGuideLoneOrbitalForTargets))
        {
            guideOrbital = bondBreakGuideLoneOrbitalForTargets;
            guideSource = RedistributionGuideSource.LonePairFromOperation;
            return true;
        }

        // Tier 6 — EmptyOrbitalFromOperation: ex-bond 0e lobe after break (same bondBreakGuide parameter as tier 5).
        if (bondBreakGuideLoneOrbitalForTargets != null
            && bondBreakGuideLoneOrbitalForTargets.ElectronCount == 0
            && IsBondBreakGuideOrbitalWithBondingCleared(bondBreakGuideLoneOrbitalForTargets))
        {
            guideOrbital = bondBreakGuideLoneOrbitalForTargets;
            guideSource = RedistributionGuideSource.EmptyOrbitalFromOperation;
            return true;
        }

        // No tier 1–6 match; caller falls back to non–guide-group repulsion / VSEPR paths.
        LogGuideGroupTrace(
            "TryResolve FAIL piNotOpN=" + piNotOp.Count + " sigNotOpN=" + sigNotOp.Count
            + " opBondId=" + (redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID().ToString() : "null")
            + " breakGuideId=" + (bondBreakGuideLoneOrbitalForTargets != null ? bondBreakGuideLoneOrbitalForTargets.GetInstanceID().ToString() : "null")
            + " breakGuideE=" + (bondBreakGuideLoneOrbitalForTargets != null ? bondBreakGuideLoneOrbitalForTargets.ElectronCount.ToString() : "na")
            + " breakGuideBondNull=" + (bondBreakGuideLoneOrbitalForTargets != null ? (bondBreakGuideLoneOrbitalForTargets.Bond == null).ToString() : "na")
            + " breakGuideParentNuc=" + (bondBreakGuideLoneOrbitalForTargets != null ? (bondBreakGuideLoneOrbitalForTargets.transform.parent == transform).ToString() : "na")
            + " breakGuideInBondedList=" + (bondBreakGuideLoneOrbitalForTargets != null ? bondedOrbitals.Contains(bondBreakGuideLoneOrbitalForTargets).ToString() : "na")
            + " breakGuideCleared=" + (bondBreakGuideLoneOrbitalForTargets != null && IsBondBreakGuideOrbitalWithBondingCleared(bondBreakGuideLoneOrbitalForTargets)).ToString());
        return false;
    }

    static bool CovalentBondConnectsSameAtomPair(CovalentBond a, CovalentBond b)
    {
        if (a?.AtomA == null || a.AtomB == null || b?.AtomA == null || b.AtomB == null) return false;
        return (a.AtomA == b.AtomA && a.AtomB == b.AtomB) || (a.AtomA == b.AtomB && a.AtomB == b.AtomA);
    }

    /// <summary>All bond hosting orbitals on <c>this</c> nucleus that share the same atom pair as <paramref name="anchorBond"/> (multiply-bond cluster).</summary>
    void CollectMultiplyBondGroupOrbitalsOnThisNucleus(CovalentBond anchorBond, List<ElectronOrbitalFunction> dst)
    {
        dst.Clear();
        if (anchorBond == null) return;
        foreach (var b in covalentBonds)
        {
            if (b?.Orbital == null) continue;
            if (b.AtomA != this && b.AtomB != this) continue;
            if (CovalentBondConnectsSameAtomPair(b, anchorBond))
                dst.Add(b.Orbital);
        }
        dst.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
    }

    /// <summary>
    /// Hosting orbitals on every multiply-bonded neighbor pair on this nucleus <b>except</b> <paramref name="operationBond"/>'s atom pair.
    /// Pre-existing =O legs stay pinned when tier logic uses the operation pair as guide (<see cref="RedistributionGuideSource.PiBondInOperation"/>).
    /// </summary>
    void CollectNonOperationMultiplyBondGroupOrbitalsOnThisNucleus(CovalentBond operationBond, List<ElectronOrbitalFunction> dst)
    {
        dst.Clear();
        if (operationBond == null) return;
        var seen = new HashSet<ElectronOrbitalFunction>();
        var tmp = new List<ElectronOrbitalFunction>();
        foreach (var b in covalentBonds)
        {
            if (b?.Orbital == null) continue;
            if (b.AtomA != this && b.AtomB != this) continue;
            if (CovalentBondConnectsSameAtomPair(b, operationBond)) continue;
            AtomFunction partner = b.AtomA == this ? b.AtomB : b.AtomA;
            if (partner == null || GetBondsTo(partner) <= 1) continue;
            CollectMultiplyBondGroupOrbitalsOnThisNucleus(b, tmp);
            foreach (var o in tmp)
                if (o != null && seen.Add(o)) dst.Add(o);
        }
        dst.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
    }

    static void AppendUniqueFrameworkDirection(List<Vector3> list, Vector3 unitDir, float mergeAngleDeg = 8f)
    {
        if (unitDir.sqrMagnitude < 1e-12f) return;
        unitDir = unitDir.normalized;
        foreach (var e in list)
        {
            float ang = Vector3.Angle(e, unitDir);
            if (ang < mergeAngleDeg || ang > 180f - mergeAngleDeg) return;
        }
        list.Add(unitDir);
    }

    /// <summary>Assigns three movers to three tripod bases: <c>perm[i]</c> = base index for mover <c>i</c>.</summary>
    static readonly int[][] TetraAzimuthPermutations3 =
    {
        new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
        new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
    };

    /// <summary>
    /// Trigonal guide-group (n=3, two movers): search in-plane rotation of the canonical ideal before <see cref="VseprLayout.AlignFirstDirectionTo"/>,
    /// minimizing <see cref="ComputePermutationAssignmentTotalCost"/>; preserves 120°/120°/120°.
    /// </summary>
    Vector3[] BuildTrigonalGuideGroupAlignedIdealWithAzimuthMinPermCost(Vector3 guideTip, List<ElectronOrbitalFunction> movers2)
    {
        // DISABLED (user request): in-plane θ grid + perm cost — use pure trigonal template (120°) aligned to guide only.
        _ = movers2;
        Vector3 g = guideTip.sqrMagnitude > 1e-14f ? guideTip.normalized : Vector3.right;
        return VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(3), g);
    }

    /// <summary>
    /// Minimum summed <see cref="Vector3.Angle"/> with optimal assignment of three mover tips to three tripod bases rotated by <paramref name="thetaDeg"/> about <paramref name="axisNorm"/>.
    /// </summary>
    static float TetraAzimuthMinCostForThetaDeg(
        float thetaDeg,
        Vector3 axisNorm,
        Vector3 b0,
        Vector3 b1,
        Vector3 b2,
        Vector3 r0,
        Vector3 r1,
        Vector3 r2,
        out int bestPermIndex)
    {
        Quaternion R = Quaternion.AngleAxis(thetaDeg, axisNorm);
        var rb0 = (R * b0).normalized;
        var rb1 = (R * b1).normalized;
        var rb2 = (R * b2).normalized;
        bestPermIndex = 0;
        float best = float.MaxValue;
        Vector3[] rb = { rb0, rb1, rb2 };
        for (int p = 0; p < TetraAzimuthPermutations3.Length; p++)
        {
            var perm = TetraAzimuthPermutations3[p];
            float c = Vector3.Angle(rb[perm[0]], r0) + Vector3.Angle(rb[perm[1]], r1) + Vector3.Angle(rb[perm[2]], r2);
            if (c < best)
            {
                best = c;
                bestPermIndex = p;
            }
        }
        return best;
    }

    /// <summary>
    /// Tetrahedron vertex 0 fixed along <paramref name="guideAxis"/>; search one azimuth θ (rotation about that axis) minimizing the same cone+quaternion total as <see cref="FindBestOrbitalToTargetDirsPermutation"/> for the three occupied movers vs tripod vertices 1–3.
    /// Mutates <paramref name="alignedIdealTet4"/> indices 1…3 only; length must be 4.
    /// </summary>
    void ApplyTetrahedralGuideAzimuthLockAboutVertex0InPlace(Vector3[] alignedIdealTet4, Vector3 guideAxis, List<ElectronOrbitalFunction> occupiedMoversExcludingGuide)
    {
        if (alignedIdealTet4 == null || alignedIdealTet4.Length < 4 || occupiedMoversExcludingGuide == null) return;
        // DISABLED (user request): θ search + tripod perm cost mutates vertices 1–3 off pure AlignFirstDirectionTo tetra template — see git for full implementation.
        _ = guideAxis;
        _ = occupiedMoversExcludingGuide;
    }

    void CollectGuideGroupCoBondOrbitalTipsNucleusLocal(
        CovalentBond guideBond,
        CovalentBond redistributionOperationBond,
        ElectronOrbitalFunction guideOrbital,
        List<Vector3> outTips)
    {
        outTips.Clear();
        CovalentBond pairBond = guideBond ?? redistributionOperationBond;
        if (pairBond == null) return;
        var seen = new HashSet<ElectronOrbitalFunction>();
        foreach (var b in covalentBonds)
        {
            if (b == null || b.Orbital == null || b.Orbital == guideOrbital) continue;
            if (!CovalentBondConnectsSameAtomPair(b, pairBond)) continue;
            if (!seen.Add(b.Orbital)) continue;
            var t = OrbitalTipDirectionInNucleusLocal(b.Orbital);
            if (t.sqrMagnitude > 1e-12f) outTips.Add(t.normalized);
        }
    }

    /// <summary>
    /// One VSEPR electron group can represent single/double/triple/quadruple bond order to a neighbor (multiple <see cref="CovalentBond"/> edges, same atom pair). Removes hosting orbitals of every <b>other</b> edge on that pair from <paramref name="occ"/> so <c>nVseprGroups = 1 + occ.Count</c> counts groups, not bond lines.
    /// </summary>
    int RemoveGuideGroupCoBondOccupiedSamePair(CovalentBond guideBond, List<ElectronOrbitalFunction> occ)
    {
        if (guideBond == null || occ == null || occ.Count == 0) return 0;
        var drop = new HashSet<ElectronOrbitalFunction>();
        foreach (var b in covalentBonds)
        {
            if (b == null || b == guideBond || b.Orbital == null) continue;
            if (!CovalentBondConnectsSameAtomPair(b, guideBond)) continue;
            drop.Add(b.Orbital);
        }
        int before = occ.Count;
        occ.RemoveAll(o => o != null && drop.Contains(o));
        return before - occ.Count;
    }

    /// <param name="guideBond">When set, every multiply-bond hosting orbital on that atom pair is excluded with the guide (not only <paramref name="guide"/>).</param>
    /// <param name="redistributionOperationBond">When set, multiply-bond orbitals on <b>other</b> pairs are excluded from movers (pinned legs). Operation-pair orbitals are not blanket-excluded; only the guide multiply-bond cluster is.</param>
    void CollectRedistributionGuideGroupMoversExcludingGuide(
        ElectronOrbitalFunction guide,
        CovalentBond guideBond,
        List<ElectronOrbitalFunction> dst,
        CovalentBond redistributionOperationBond = null)
    {
        dst.Clear();
        if (guide == null) return;
        var guideGroup = new HashSet<ElectronOrbitalFunction>();
        if (guideBond != null)
        {
            foreach (var b in covalentBonds)
            {
                if (b?.Orbital == null) continue;
                if (b.AtomA != this && b.AtomB != this) continue;
                if (CovalentBondConnectsSameAtomPair(b, guideBond))
                    guideGroup.Add(b.Orbital);
            }
        }
        else
            guideGroup.Add(guide);

        HashSet<ElectronOrbitalFunction> nonOpMultiplyPinned = null;
        if (redistributionOperationBond != null)
        {
            var tmpNonOp = new List<ElectronOrbitalFunction>();
            CollectNonOperationMultiplyBondGroupOrbitalsOnThisNucleus(redistributionOperationBond, tmpNonOp);
            nonOpMultiplyPinned = new HashSet<ElectronOrbitalFunction>();
            foreach (var o in tmpNonOp)
                if (o != null) nonOpMultiplyPinned.Add(o);
        }

        var seen = new HashSet<ElectronOrbitalFunction>();
        foreach (var b in covalentBonds)
        {
            if (b == null || b.Orbital == null) continue;
            if (b.AtomA != this && b.AtomB != this) continue;
            if (guideGroup.Contains(b.Orbital)) continue;
            if (nonOpMultiplyPinned != null && nonOpMultiplyPinned.Contains(b.Orbital)) continue;
            if (seen.Add(b.Orbital)) dst.Add(b.Orbital);
        }
        foreach (var o in bondedOrbitals)
        {
            if (o == null || guideGroup.Contains(o)) continue;
            if (nonOpMultiplyPinned != null && nonOpMultiplyPinned.Contains(o)) continue;
            if (!seen.Add(o)) continue;
            dst.Add(o);
        }
    }

    bool IsRepulsionEmptyNonBondMoverForGuideGroup(ElectronOrbitalFunction o) =>
        o != null && bondedOrbitals.Contains(o) && o.Bond == null && o.ElectronCount == 0 && o.transform.parent == transform;

    /// <summary>
    /// Guide fixed by <see cref="TryResolveRedistributionGuideGroupForLayout"/>; movers = incident bond orbitals + nucleus <see cref="bondedOrbitals"/> minus guide cluster, minus multiply-bond orbitals on non-operation pairs when <c>redistributionOperationBond</c> is set (operation-pair orbitals may be movers).
    /// When <c>nOcc &gt; 0</c> and the resolved tier has a bond anchor (<c>guideBond != null</c>, tiers 1–4), <paramref name="outSlots"/> begins with every hosting orbital on the guide multiply-bond cluster: <b>π-in-operation trigonal</b> uses the same canonical VSEPR slots as the non-anchor path (vertex 0 / σ internuclear); otherwise rows use current locals. Then permuted occupied movers; <see cref="orbitalsExcludedFromJointRigidInApplyRedistributeTargets"/> lists that whole cluster. Ex-bond-only tiers 5–6 (<c>guideBond == null</c>) keep the legacy single-orbital canonical vertex-0 slot for the representative orbital.
    /// Empty guide (tier 6): perpendicular-to-guide (pinned 0e) repulsion via <see cref="TryComputeRepulsionSumElectronDomainLayoutSlots"/> + permutation on non-pinned slots.
    /// Bonding or lone-pair guide (tiers 1–5): same <c>nVseprGroups = 1 + nOcc</c> as σ/π tiers (3–4); only the fixed guide differs (bond orbital vs ex-bond lone pair). <see cref="VseprLayout.GetIdealLocalDirections"/> for guide + <b>occupied</b> movers; 0e nonbond is excluded from ideal vertices and placed ⊥ that framework.
    /// All bond orders (single through quadruple) between the same two centers are <b>one</b> electron-domain group: hosting orbitals on the other edges of <see cref="guideBond"/>’s atom pair are stripped from <c>occ</c> via <see cref="RemoveGuideGroupCoBondOccupiedSamePair"/> and are not permuted as separate substituent vertices.
    /// For <c>nVseprGroups==4</c>, after aligning vertex 0 to the guide, <see cref="ApplyTetrahedralGuideAzimuthLockAboutVertex0InPlace"/> searches rotation about that axis (grid × 3! assignment) so tripod vertices 1–3 best match current occupied mover tips in nucleus local (preserves prior tetrahedral azimuth when adding the fourth substituent).
    /// When the guide is a π bond, <see cref="VseprLayout.AlignFirstDirectionTo"/> still uses <b>three</b> coplanar trigonal vertices; vertex 0 is aligned to the <b>internuclear axis</b> (σ-plane domain toward the partner), not <see cref="OrbitalTipDirectionInNucleusLocal"/> of the π lobe (which lies ⊥ that plane).
    /// If co-bond merge removes every occupied mover (<c>nOcc==0</c>, e.g. quadruple bond to a sole neighbor and no other domains), we return only empty nonbond targets (or none)—never fall through to repulsion electron-domains (which would re-aim σ and move the partner).
    /// Caller gates σ-cleavage break framing.
    /// </summary>
    bool TryBuildRedistributeTargets3DGuideGroupPrefix(
        CovalentBond redistributionOperationBond,
        ElectronOrbitalFunction bondBreakGuideLoneOrbitalForTargets,
        out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> outSlots,
        out bool cancelRedistributionDueToEmptyMovers)
    {
        outSlots = null;
        cancelRedistributionDueToEmptyMovers = false;
        if (!TryResolveRedistributionGuideGroupForLayout(
                redistributionOperationBond,
                bondBreakGuideLoneOrbitalForTargets,
                out var guide,
                out var guideBond,
                out var source,
                out bool resolveAbortedEmptyMovers))
        {
            cancelRedistributionDueToEmptyMovers = resolveAbortedEmptyMovers;
            if (!resolveAbortedEmptyMovers)
                LogGuideGroupTrace("TryBuild SKIP reason=TryResolve_no_tier");
            return false;
        }

        if (source == RedistributionGuideSource.EmptyOrbitalFromOperation)
        {
            GetCarbonSigmaCleavageDomains(out var sigAll, out var nbAll);
            var occDom = new List<ElectronOrbitalFunction>();
            foreach (var o in sigAll)
                if (o != null && o.ElectronCount > 0) occDom.Add(o);
            foreach (var o in nbAll)
                if (o != null && o.ElectronCount > 0) occDom.Add(o);
            occDom = occDom.Distinct().OrderBy(o => o.GetInstanceID()).ToList();
            var empDom = nbAll.Where(o => o != null && o.ElectronCount == 0).OrderBy(o => o.GetInstanceID()).ToList();
            if (!empDom.Contains(guide))
            {
                LogGuideGroupTrace("TryBuild FAIL tier=EmptyOrbitalFromOperation reason=empDom_missing_guide empDomN=" + empDom.Count + " occDomN=" + occDom.Count + " guideId=" + guide.GetInstanceID());
                return false;
            }
            if (occDom.Count < 1 || occDom.Count > 6 || empDom.Count > 3)
            {
                LogGuideGroupTrace("TryBuild FAIL tier=EmptyOrbitalFromOperation reason=occ_emp_bounds occDomN=" + occDom.Count + " empDomN=" + empDom.Count);
                return false;
            }
            if (!TryComputeRepulsionSumElectronDomainLayoutSlots(occDom, empDom, guide, out var rawSlots, out var ideals, out var skipApply)
                || rawSlots == null || ideals == null || skipApply == null
                || rawSlots.Count != ideals.Count || rawSlots.Count != skipApply.Count)
            {
                LogGuideGroupTrace("TryBuild FAIL tier=EmptyOrbitalFromOperation reason=TryComputeRepulsionSumElectronDomainLayoutSlots occDomN=" + occDom.Count + " empDomN=" + empDom.Count);
                return false;
            }

            var movableIdx = new List<int>();
            for (int i = 0; i < rawSlots.Count; i++)
                if (!skipApply[i]) movableIdx.Add(i);
            if (movableIdx.Count == 0)
            {
                outSlots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>(rawSlots);
                int guideE = guide.ElectronCount;
                bool guideOnNucleus = guide.transform.parent == transform;
                Vector3 guideTipLocal = OrbitalTipLocalDirection(guide);
                LogGetRedistributeTargets3DLine(
                    "EXIT_repulsionOnly_guideGroup",
                    "targets=" + outSlots.Count + " source=" + source
                    + " movers=0 guideOrbId=" + guide.GetInstanceID()
                    + " guideOrbE=" + guideE
                    + " guideOnNucleus=" + (guideOnNucleus ? "true" : "false")
                    + " guideTipLocal={"
                    + guideTipLocal.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
                    + guideTipLocal.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
                    + guideTipLocal.z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "}"
                    + " guideBondId=null opBondId=" + (redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID().ToString() : "null"));
                return true;
            }

            var moversOrdered = new List<ElectronOrbitalFunction>(movableIdx.Count);
            foreach (int i in movableIdx) moversOrdered.Add(rawSlots[i].orb);

            Vector3 guideTipEmpty = GuideGroupFirstVertexDirectionNucleusLocal(guide, redistributionOperationBond);
            if (guideTipEmpty.sqrMagnitude < 1e-10f)
            {
                int gix = -1;
                for (int ii = 0; ii < rawSlots.Count; ii++)
                {
                    if (rawSlots[ii].orb == guide)
                    {
                        gix = ii;
                        break;
                    }
                }
                if (gix >= 0 && ideals[gix].sqrMagnitude > 1e-12f)
                    guideTipEmpty = ideals[gix];
                else
                    guideTipEmpty = OrbitalTipLocalDirection(guide);
            }
            guideTipEmpty.Normalize();

            var permutationTargetDirs = new List<Vector3>(movableIdx.Count);
            if (rawSlots.Count == 3 && moversOrdered.Count == 2 && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                var aligned3 = BuildTrigonalGuideGroupAlignedIdealWithAzimuthMinPermCost(guideTipEmpty, moversOrdered);
                permutationTargetDirs.Add(aligned3[1].normalized);
                permutationTargetDirs.Add(aligned3[2].normalized);
            }
            else if (rawSlots.Count == 4 && moversOrdered.Count == 3 && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                var ideal4 = VseprLayout.GetIdealLocalDirections(4);
                var aligned4 = VseprLayout.AlignFirstDirectionTo(ideal4, guideTipEmpty);
                ApplyTetrahedralGuideAzimuthLockAboutVertex0InPlace(aligned4, guideTipEmpty, moversOrdered);
                permutationTargetDirs.Add(aligned4[1].normalized);
                permutationTargetDirs.Add(aligned4[2].normalized);
                permutationTargetDirs.Add(aligned4[3].normalized);
            }
            else
            {
                foreach (int i in movableIdx)
                {
                    var id = ideals[i];
                    if (id.sqrMagnitude < 1e-12f)
                        permutationTargetDirs.Add(OrbitalTipDirectionInNucleusLocal(rawSlots[i].orb).normalized);
                    else
                        permutationTargetDirs.Add(id.normalized);
                }

                if (moversOrdered.Count >= 2 && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                {
                    Vector3 twistAx = OrbitalTipLocalDirection(guide);
                    if (twistAx.sqrMagnitude < 1e-10f) twistAx = Vector3.right;
                    MinimizeTargetDirsAzimuthForPermutationCostInPlace(moversOrdered, permutationTargetDirs, twistAx.normalized, 36, 0.05f);
                }
            }

            var perm = FindBestOrbitalToTargetDirsPermutation(moversOrdered, permutationTargetDirs, bondRadius, this);
            if (perm == null)
            {
                LogGetRedistributeTargets3DLine("guideGroup_permutationFallback", "source=" + source + " reason=perm_null");
                perm = Enumerable.Range(0, moversOrdered.Count).ToArray();
            }

            outSlots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>(rawSlots.Count);
            var movableOrder = new Dictionary<int, int>();
            for (int k = 0; k < movableIdx.Count; k++)
                movableOrder[movableIdx[k]] = k;

            for (int i = 0; i < rawSlots.Count; i++)
            {
                if (skipApply[i])
                {
                    outSlots.Add(rawSlots[i]);
                    continue;
                }
                int k = movableOrder[i];
                int t = perm[k];
                Vector3 d = permutationTargetDirs[t];
                var o = rawSlots[i].orb;
                if (d.sqrMagnitude < 1e-14f)
                    outSlots.Add((o, o.transform.localPosition, o.transform.localRotation));
                else
                {
                    var (pos, rot) = GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(o, d.normalized, bondRadius, o.transform.localRotation);
                    outSlots.Add((o, pos, rot));
                }
            }

            LogGetRedistributeTargets3DLine(
                "EXIT_repulsionOnly_guideGroup",
                "targets=" + outSlots.Count + " source=" + source
                + " movers=" + moversOrdered.Count
                + " guideOrbId=" + guide.GetInstanceID()
                + " guideOrbE=" + guide.ElectronCount
                + " guideOnNucleus=" + (guide.transform.parent == transform ? "true" : "false")
                + " guideTipLocal={"
                + OrbitalTipLocalDirection(guide).x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
                + OrbitalTipLocalDirection(guide).y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
                + OrbitalTipLocalDirection(guide).z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "}"
                + " guideBondId=null opBondId=" + (redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID().ToString() : "null"));
            return true;
        }

        var movers = new List<ElectronOrbitalFunction>();
        CollectRedistributionGuideGroupMoversExcludingGuide(guide, guideBond, movers, redistributionOperationBond);
        if (movers.Count == 0)
        {
            cancelRedistributionDueToEmptyMovers = true;
            LogGuideGroupTrace("TryBuild FAIL reason=movers_empty source=" + source + " guideId=" + guide.GetInstanceID());
            return false;
        }

        var occ = new List<ElectronOrbitalFunction>();
        var emp = new List<ElectronOrbitalFunction>();
        foreach (var o in movers)
        {
            if (IsRepulsionOccupiedDomainForGuideGroupLayout(o))
                occ.Add(o);
            else if (IsRepulsionEmptyNonBondMoverForGuideGroup(o))
                emp.Add(o);
            else
            {
                LogGuideGroupTrace(
                    "TryBuild FAIL reason=mover_neither_occ_nor_empty source=" + source + " moverId=" + o.GetInstanceID() + " e=" + o.ElectronCount + " bondNull=" + (o.Bond == null));
                return false;
            }
        }

        GetCarbonSigmaCleavageDomains(out _, out var nbInventory);
        var occSeen = new HashSet<ElectronOrbitalFunction>(occ);
        foreach (var o in nbInventory)
        {
            if (o == null || o == guide || o.ElectronCount <= 0) continue;
            if (!occSeen.Add(o)) continue;
            occ.Add(o);
        }

        occ.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        emp.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        // Ex-bond guide (guideBond null) still needs the cleaved edge for σ+π merge on that pair (e.g. CO₂ π break → linear C).
        CovalentBond pairBondForOccMergeAndGuideAxis = guideBond ?? redistributionOperationBond;
        int mergedCoBondOccRemoved = RemoveGuideGroupCoBondOccupiedSamePair(pairBondForOccMergeAndGuideAxis, occ);
        // Stub guide on π cleavage: first merge uses cleaved bond pair; other multiply-bonded pairs (e.g. remaining C=O) still
        // have σ+π both in occ — merge each π edge so nOcc matches electron domains (linear 2, not trigonal 3).
        if (source == RedistributionGuideSource.LonePairFromOperation
            && guideBond == null
            && redistributionOperationBond != null
            && !redistributionOperationBond.IsSigmaBondLine())
        {
            foreach (var b in covalentBonds)
            {
                if (b == null || b.IsSigmaBondLine() || b.Orbital == null) continue;
                if (b.AtomA != this && b.AtomB != this) continue;
                mergedCoBondOccRemoved += RemoveGuideGroupCoBondOccupiedSamePair(b, occ);
            }
        }
        if (source == RedistributionGuideSource.PiBondInOperation && occ.Count == 2)
            occ.Sort(CompareGuideGroupOccPiTrigonalFor2);
        else
            occ.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));

        // Far terminal O in a π step: σ guide is Tier 3 (not on the π op pair) while π count on this atom can still be 0.
        // Three occupied nucleus lone lobes were counted as three VSEPR substituents → nVseprGroups=4 and tetrahedral tripod;
        // electron geometry is trigonal planar (σ + two lone domains). Reuse the same 3→2 collapse as
        // CollapseNucleusLoneDomainsForTerminalSp2OxoHybridIfNeeded so guide-group layout matches hybrid TryMatch / tri120.
        if (source == RedistributionGuideSource.SigmaBondNotInOperation
            && redistributionOperationBond != null
            && !redistributionOperationBond.IsSigmaBondLine()
            && occ.Count == 3)
        {
            bool allLone = true;
            for (int oi = 0; oi < occ.Count; oi++)
            {
                var ox = occ[oi];
                if (ox == null || ox.Bond != null || ox.ElectronCount <= 0) { allLone = false; break; }
            }
            if (allLone)
            {
                Vector3 sigmaAxisForCollapse = GuideGroupFirstVertexDirectionNucleusLocal(guide, pairBondForOccMergeAndGuideAxis);
                if (sigmaAxisForCollapse.sqrMagnitude < 1e-10f)
                {
                    var gtipC = OrbitalTipLocalDirection(guide);
                    sigmaAxisForCollapse = gtipC.sqrMagnitude > 1e-10f ? gtipC.normalized : Vector3.right;
                }
                else
                    sigmaAxisForCollapse.Normalize();
                var collapsedOcc = CollapseNucleusLoneDomainsForTerminalSp2OxoHybridIfNeeded(
                    occ,
                    new List<Vector3> { sigmaAxisForCollapse },
                    redistributionOperationBond);
                if (collapsedOcc != null && collapsedOcc.Count == 2)
                    occ = collapsedOcc;
            }
        }

        int nOcc = occ.Count;
        // #region agent log
        if (redistributionOperationBond != null
            && redistributionOperationBond.IsSigmaBondLine()
            && (source == RedistributionGuideSource.SigmaBondInOperation || source == RedistributionGuideSource.SigmaBondNotInOperation))
        {
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H9",
                "AtomFunction.TryBuildRedistributeTargets3DGuideGroupPrefix",
                "sigma_guide_group_occ_summary",
                "{\"atomId\":" + GetInstanceID()
                + ",\"source\":\"" + source + "\""
                + ",\"nOcc\":" + nOcc
                + ",\"nEmp\":" + emp.Count
                + ",\"nVseprGroups\":" + (1 + nOcc)
                + ",\"guideBondId\":" + (guideBond != null ? guideBond.GetInstanceID().ToString() : "0")
                + ",\"opBondId\":" + redistributionOperationBond.GetInstanceID()
                + ",\"guideEqualsOp\":" + ((guideBond != null && guideBond == redistributionOperationBond) ? "true" : "false")
                + ",\"mergedCoBondOccRemoved\":" + mergedCoBondOccRemoved + "}");
        }
        // #endregion
        if (nOcc > 6)
        {
            LogGuideGroupTrace("TryBuild FAIL reason=nOcc_gt_6 nOcc=" + nOcc + " source=" + source);
            return false;
        }

        // First VSEPR direction for AlignFirstDirectionTo: bond guides use internuclear axis only (σ/π domain toward partner). Ex-bond lone/empty: measured pivot→orbital (then pivot→lobe tip) so post–bond-break lobe quaternion sign does not invert the frame.
        Vector3 guideTip = GuideGroupFirstVertexDirectionNucleusLocal(guide, pairBondForOccMergeAndGuideAxis);

        if (guideTip.sqrMagnitude < 1e-10f)
        {
            LogGuideGroupTrace("TryBuild FAIL reason=guideTip_zero source=" + source + " guideId=" + guide.GetInstanceID());
            return false;
        }
        guideTip.Normalize();

        // All σ/π on the guide pair merged out of occ (e.g. quadruple to sole neighbor): no substituent orbitals to permute.
        // Emit at most empty nonbond slots; do not fall through to electronDomains (would re-target σ and move the partner).
        if (nOcc == 0)
        {
            outSlots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>(emp.Count);
            // Sigma-only break with no occupied movers: keep all empty rows at their current locals.
            // Right after cleavage there is no occupied overlap to resolve, so re-slotting empties here causes artificial swings.
            if (source == RedistributionGuideSource.SigmaBondInOperation)
            {
                for (int i = 0; i < emp.Count; i++)
                {
                    var e = emp[i];
                    if (e == null) continue;
                    outSlots.Add((e, e.transform.localPosition, e.transform.localRotation));
                }
                LogGetRedistributeTargets3DLine(
                    "EXIT_repulsionOnly_guideGroup",
                    "targets=" + outSlots.Count + " source=" + source
                    + " nVseprGroups=1 occMovers=0 empSlots=" + emp.Count
                    + " mergedCoBondOccRemoved=" + mergedCoBondOccRemoved
                    + " onlyMultiplyBondedPairNoOcc=true guideOrbId=" + guide.GetInstanceID()
                    + " guideOrbE=" + guide.ElectronCount
                    + " guideOnNucleus=" + (guide.transform.parent == transform ? "true" : "false")
                    + " guideTipLocal={"
                    + OrbitalTipLocalDirection(guide).x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
                    + OrbitalTipLocalDirection(guide).y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
                    + OrbitalTipLocalDirection(guide).z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "}"
                    + " guideFixedNotInTargets=True guideBondId=" + (guideBond != null ? guideBond.GetInstanceID().ToString() : "null")
                    + " opBondId=" + (redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID().ToString() : "null"));
                return true;
            }
            var coBondTipsForEmpty = new List<Vector3>();
            CollectGuideGroupCoBondOrbitalTipsNucleusLocal(guideBond, redistributionOperationBond, guide, coBondTipsForEmpty);
            var electronFrameworkForEmpty = new List<Vector3>();
            AppendUniqueFrameworkDirection(electronFrameworkForEmpty, guideTip);
            foreach (var t in coBondTipsForEmpty)
                AppendUniqueFrameworkDirection(electronFrameworkForEmpty, t);

            const float emptySepDegGuide = 18f;
            var placedEmptyTipsLocal0 = new List<Vector3>();
            foreach (var e in emp)
            {
                Vector3 tipPref = OrbitalTipLocalDirection(e);
                Vector3? prefDir = tipPref.sqrMagnitude >= 1e-10f ? tipPref.normalized : (Vector3?)null;
                if (prefDir.HasValue
                    && EmptyTipAlreadyIdealVsElectronFramework(
                        prefDir.Value,
                        electronFrameworkForEmpty,
                        occupiedLobeAxesMustSeparateFrom: coBondTipsForEmpty)
                    && MinAngleToAnyDirection(prefDir.Value, placedEmptyTipsLocal0) > emptySepDegGuide)
                {
                    outSlots.Add((e, e.transform.localPosition, e.transform.localRotation));
                    placedEmptyTipsLocal0.Add(prefDir.Value);
                    continue;
                }
                if (!TryComputeSeparatedEmptySlot(electronFrameworkForEmpty, placedEmptyTipsLocal0, bondRadius, out var ePos, out var eRot, prefDir))
                {
                    outSlots.Add((e, e.transform.localPosition, e.transform.localRotation));
                    continue;
                }
                outSlots.Add((e, ePos, eRot));
                placedEmptyTipsLocal0.Add((eRot * Vector3.right).normalized);
            }

            LogGetRedistributeTargets3DLine(
                "EXIT_repulsionOnly_guideGroup",
                "targets=" + outSlots.Count + " source=" + source
                + " nVseprGroups=1 occMovers=0 empSlots=" + emp.Count
                + " mergedCoBondOccRemoved=" + mergedCoBondOccRemoved
                + " onlyMultiplyBondedPairNoOcc=true guideOrbId=" + guide.GetInstanceID()
                + " guideOrbE=" + guide.ElectronCount
                + " guideOnNucleus=" + (guide.transform.parent == transform ? "true" : "false")
                + " guideTipLocal={"
                + OrbitalTipLocalDirection(guide).x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
                + OrbitalTipLocalDirection(guide).y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
                + OrbitalTipLocalDirection(guide).z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "}"
                + " guideFixedNotInTargets=True guideBondId=" + (guideBond != null ? guideBond.GetInstanceID().ToString() : "null")
                + " opBondId=" + (redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID().ToString() : "null"));
            return true;
        }

        int nVseprGroups = 1 + nOcc;
        if (nVseprGroups < 2)
        {
            LogGuideGroupTrace("TryBuild FAIL reason=nVsepr_lt_2 nVseprGroups=" + nVseprGroups + " nOcc=" + nOcc + " source=" + source);
            return false;
        }

        // #region agent log
        if (DebugLogOcoSecondPiNdjson && source == RedistributionGuideSource.PiBondInOperation && nVseprGroups == 3)
        {
            int opId = redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID() : 0;
            int fadeId = redistributionOperationBond != null && redistributionOperationBond.OrbitalBeingFadedForCharge != null
                ? redistributionOperationBond.OrbitalBeingFadedForCharge.GetInstanceID()
                : 0;
            int gId = guide != null ? guide.GetInstanceID() : 0;
            var sb = new System.Text.StringBuilder(320);
            sb.Append("{\"mergedCoBondOccRemoved\":").Append(mergedCoBondOccRemoved);
            sb.Append(",\"opBondId\":").Append(opId);
            sb.Append(",\"fadeOutOrbId\":").Append(fadeId);
            sb.Append(",\"guideOrbId\":").Append(gId);
            sb.Append(",\"atomId\":").Append(GetInstanceID());
            sb.Append(",\"Z\":").Append(atomicNumber);
            sb.Append(",\"nOcc\":").Append(nOcc);
            sb.Append(",\"occ\":[");
            for (int oi = 0; oi < occ.Count; oi++)
            {
                if (oi > 0) sb.Append(',');
                var o = occ[oi];
                if (o == null)
                {
                    sb.Append("null");
                    continue;
                }
                bool isSig = o.Bond is CovalentBond cob && cob.IsSigmaBondLine();
                bool isPiEdge = o.Bond is CovalentBond cob2 && !cob2.IsSigmaBondLine();
                sb.Append("{\"id\":").Append(o.GetInstanceID());
                sb.Append(",\"e\":").Append(o.ElectronCount);
                sb.Append(",\"sigma\":").Append(isSig ? "true" : "false");
                sb.Append(",\"piEdge\":").Append(isPiEdge ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("]}");
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "A_B_E",
                "AtomFunction.TryBuildRedistributeTargets3DGuideGroupPrefix",
                "pi_in_op_trigonal_occ_inventory",
                sb.ToString());
        }
        // #endregion

        var moversOrdered2 = new List<ElectronOrbitalFunction>(occ);
        Vector3[] alignedIdeal;
        if (nVseprGroups == 3 && moversOrdered2.Count == 2 && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            alignedIdeal = BuildTrigonalGuideGroupAlignedIdealWithAzimuthMinPermCost(guideTip, moversOrdered2);
        else
        {
            var idealFrame = VseprLayout.GetIdealLocalDirections(nVseprGroups);
            alignedIdeal = VseprLayout.AlignFirstDirectionTo(idealFrame, guideTip);
            if (nVseprGroups == 4)
                ApplyTetrahedralGuideAzimuthLockAboutVertex0InPlace(alignedIdeal, guideTip, occ);
        }
        var permutationTargetDirs2 = new List<Vector3>(nVseprGroups - 1);
        for (int vi = 1; vi < nVseprGroups; vi++)
        {
            var id = alignedIdeal[vi];
            var idxHint = vi - 1;
            permutationTargetDirs2.Add(
                id.sqrMagnitude < 1e-12f
                    ? OrbitalTipDirectionInNucleusLocal(moversOrdered2[idxHint]).normalized
                    : id.normalized);
        }

        if (moversOrdered2.Count != permutationTargetDirs2.Count)
        {
            LogGuideGroupTrace(
                "TryBuild FAIL reason=perm_list_count_mismatch moversN=" + moversOrdered2.Count + " permDirsN=" + permutationTargetDirs2.Count + " nVseprGroups=" + nVseprGroups + " source=" + source);
            return false;
        }

        // n=3 trigonal: step-2 template azimuth is the in-plane θ grid on the canonical ideal before AlignFirstDirectionTo (see BuildTrigonalGuideGroupAlignedIdealWithAzimuthMinPermCost).
        // Post-align MinimizeTargetDirsAzimuth on mover-only lists still breaks 120° geometry.

        Vector3 dVertex0 = alignedIdeal[0];
        if (dVertex0.sqrMagnitude < 1e-14f) dVertex0 = guideTip;
        else dVertex0.Normalize();

        // #region agent log
        void LogTrigonalGroupAngles(string phase, Vector3 a, Vector3 b, Vector3 c, string groupLabel)
        {
            if (!DebugLogPiTrigonalOcoConformation) return;
            if (a.sqrMagnitude < 1e-14f || b.sqrMagnitude < 1e-14f || c.sqrMagnitude < 1e-14f) return;
            a.Normalize(); b.Normalize(); c.Normalize();
            float angAB = Vector3.Angle(a, b);
            float angAC = Vector3.Angle(a, c);
            float angBC = Vector3.Angle(b, c);
            LogPiTrigonalOcoLine("tri120 phase=" + phase
                + " group=" + groupLabel
                + " angGuideToM0Deg=" + angAB.ToString("F2", CultureInfo.InvariantCulture)
                + " angGuideToM1Deg=" + angAC.ToString("F2", CultureInfo.InvariantCulture)
                + " angM0ToM1Deg=" + angBC.ToString("F2", CultureInfo.InvariantCulture));
        }
        // #endregion

        // #region agent log
        if (DebugLogPiTrigonalOcoConformation
            && source == RedistributionGuideSource.PiBondInOperation
            && nVseprGroups == 3
            && moversOrdered2.Count == 2
            && permutationTargetDirs2.Count == 2)
        {
            int sigmaIdxPre = -1;
            for (int i = 0; i < 2; i++)
            {
                if (moversOrdered2[i] != null && moversOrdered2[i].Bond is CovalentBond cbPre && cbPre.IsSigmaBondLine())
                {
                    sigmaIdxPre = i;
                    break;
                }
            }
            if (sigmaIdxPre >= 0 && moversOrdered2[sigmaIdxPre] != null)
            {
                var sigmaOrbPre = moversOrdered2[sigmaIdxPre];
                Vector3 sigmaTipPre = OrbitalTipDirectionInNucleusLocal(sigmaOrbPre).normalized;
                Vector3 t0Pre = permutationTargetDirs2[0].normalized;
                Vector3 t1Pre = permutationTargetDirs2[1].normalized;
                Vector3 sigmaAxisPre = Vector3.zero;
                if (sigmaOrbPre.Bond is CovalentBond sigmaCbPre)
                    sigmaAxisPre = InternuclearSigmaAxisNucleusLocalForBond(sigmaCbPre).normalized;
                float angTipToT0Pre = Vector3.Angle(sigmaTipPre, t0Pre);
                float angTipToT1Pre = Vector3.Angle(sigmaTipPre, t1Pre);
                float angTipToAxisPre = sigmaAxisPre.sqrMagnitude > 1e-14f ? Vector3.Angle(sigmaTipPre, sigmaAxisPre) : -1f;
                float angGuideToAxisPre = sigmaAxisPre.sqrMagnitude > 1e-14f ? Vector3.Angle(guideTip.normalized, sigmaAxisPre) : -1f;
                LogPiTrigonalOcoLine("prePerm sigmaState atomId=" + GetInstanceID()
                    + " sigmaIdx=" + sigmaIdxPre
                    + " sigmaOrbId=" + sigmaOrbPre.GetInstanceID()
                    + " parentIsNucleus=" + (sigmaOrbPre.transform.parent == transform)
                    + " angTipToT0Deg=" + angTipToT0Pre.ToString("F2", CultureInfo.InvariantCulture)
                    + " angTipToT1Deg=" + angTipToT1Pre.ToString("F2", CultureInfo.InvariantCulture)
                    + " angTipToSigmaAxisDeg=" + angTipToAxisPre.ToString("F2", CultureInfo.InvariantCulture)
                    + " angGuideToSigmaAxisDeg=" + angGuideToAxisPre.ToString("F2", CultureInfo.InvariantCulture));
            }
            if (moversOrdered2[0] != null && moversOrdered2[1] != null)
            {
                Vector3 t0 = permutationTargetDirs2[0].normalized;
                Vector3 t1 = permutationTargetDirs2[1].normalized;
                Vector3 m0Tip = OrbitalTipDirectionInNucleusLocal(moversOrdered2[0]).normalized;
                Vector3 m1Tip = OrbitalTipDirectionInNucleusLocal(moversOrdered2[1]).normalized;
                for (int mi = 0; mi < 2; mi++)
                {
                    var mo = moversOrdered2[mi];
                    if (mo == null) continue;
                    Vector3 mt = OrbitalTipDirectionInNucleusLocal(mo).normalized;
                    Quaternion lr = mo.transform.localRotation;
                    int bondId = mo.Bond != null ? mo.Bond.GetInstanceID() : 0;
                    bool sigmaLine = mo.Bond is CovalentBond cbM && cbM.IsSigmaBondLine();
                    LogPiTrigonalOcoLine("prePerm moverState atomId=" + GetInstanceID()
                        + " mi=" + mi
                        + " orbId=" + mo.GetInstanceID()
                        + " bondId=" + bondId
                        + " sigmaLine=" + sigmaLine
                        + " parentIsNucleus=" + (mo.transform.parent == transform)
                        + " tipN={" + mt.x.ToString("F4", CultureInfo.InvariantCulture) + "," + mt.y.ToString("F4", CultureInfo.InvariantCulture) + "," + mt.z.ToString("F4", CultureInfo.InvariantCulture) + "}"
                        + " localRot={" + lr.x.ToString("F4", CultureInfo.InvariantCulture) + "," + lr.y.ToString("F4", CultureInfo.InvariantCulture) + "," + lr.z.ToString("F4", CultureInfo.InvariantCulture) + "," + lr.w.ToString("F4", CultureInfo.InvariantCulture) + "}");
                }
                float m0t0 = PiPermutationConeAngleTipToTargetDeg(moversOrdered2[0], m0Tip, t0);
                float m0t1 = PiPermutationConeAngleTipToTargetDeg(moversOrdered2[0], m0Tip, t1);
                float m1t0 = PiPermutationConeAngleTipToTargetDeg(moversOrdered2[1], m1Tip, t0);
                float m1t1 = PiPermutationConeAngleTipToTargetDeg(moversOrdered2[1], m1Tip, t1);
                float cost01 = m0t0 + m1t1;
                float cost10 = m0t1 + m1t0;
                LogPiTrigonalOcoLine("prePerm matrix atomId=" + GetInstanceID()
                    + " m0Id=" + moversOrdered2[0].GetInstanceID()
                    + " m1Id=" + moversOrdered2[1].GetInstanceID()
                    + " m0_t0=" + m0t0.ToString("F2", CultureInfo.InvariantCulture)
                    + " m0_t1=" + m0t1.ToString("F2", CultureInfo.InvariantCulture)
                    + " m1_t0=" + m1t0.ToString("F2", CultureInfo.InvariantCulture)
                    + " m1_t1=" + m1t1.ToString("F2", CultureInfo.InvariantCulture)
                    + " costPerm01=" + cost01.ToString("F2", CultureInfo.InvariantCulture)
                    + " costPerm10=" + cost10.ToString("F2", CultureInfo.InvariantCulture));
                // #region agent log
                int sigmaIdxParentDiag = -1;
                for (int i = 0; i < 2; i++)
                {
                    if (moversOrdered2[i] != null && moversOrdered2[i].Bond is CovalentBond cbPd && cbPd.IsSigmaBondLine())
                    {
                        sigmaIdxParentDiag = i;
                        break;
                    }
                }
                if (sigmaIdxParentDiag >= 0 && moversOrdered2[sigmaIdxParentDiag] != null)
                {
                    var so = moversOrdered2[sigmaIdxParentDiag];
                    Vector3 sigmaCurParent = (so.transform.localRotation * Vector3.right).normalized;
                    float ParentDirectedCost(int sigmaTargetIdx)
                    {
                        Vector3 tdN = permutationTargetDirs2[sigmaTargetIdx].normalized;
                        Vector3 tdP = tdN;
                        if (so.transform.parent != null && so.transform.parent != transform)
                        {
                            Vector3 tdW = transform.TransformDirection(tdN);
                            tdP = so.transform.parent.InverseTransformDirection(tdW).normalized;
                        }
                        return Vector3.Angle(sigmaCurParent, tdP);
                    }
                    float parentSigmaCostPerm01 = ParentDirectedCost(0);
                    float parentSigmaCostPerm10 = ParentDirectedCost(1);
                    LogPiTrigonalOcoLine("prePerm sigmaParentSpace atomId=" + GetInstanceID()
                        + " sigmaIdx=" + sigmaIdxParentDiag
                        + " sigmaOrbId=" + so.GetInstanceID()
                        + " parentIsNucleus=" + (so.transform.parent == transform)
                        + " parentCostPerm01Deg=" + parentSigmaCostPerm01.ToString("F2", CultureInfo.InvariantCulture)
                        + " parentCostPerm10Deg=" + parentSigmaCostPerm10.ToString("F2", CultureInfo.InvariantCulture));
                }
                // #endregion

                int sigmaIdxEval = -1;
                for (int i = 0; i < 2; i++)
                {
                    if (moversOrdered2[i] != null && moversOrdered2[i].Bond is CovalentBond cbEval && cbEval.IsSigmaBondLine())
                    {
                        sigmaIdxEval = i;
                        break;
                    }
                }
                if (sigmaIdxEval >= 0)
                {
                    float EvalPostPermScore(int a0, int a1, out float sigmaToAxisDeg, out float nonSigmaToTargetDeg)
                    {
                        sigmaToAxisDeg = -1f;
                        nonSigmaToTargetDeg = -1f;
                        int[] a = { a0, a1 };
                        float sum = 0f;
                        for (int mi = 0; mi < 2; mi++)
                        {
                            var o = moversOrdered2[mi];
                            if (o == null) return float.MaxValue;
                            Vector3 tdEval = permutationTargetDirs2[a[mi]].normalized;
                            var (_, rotEval) = GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(o, tdEval, bondRadius, o.transform.localRotation);
                            Vector3 sigmaAxisEval = Vector3.zero;
                            if (o.Bond is CovalentBond cbSigmaEval && cbSigmaEval.IsSigmaBondLine())
                            {
                                sigmaAxisEval = InternuclearSigmaAxisNucleusLocalForBond(cbSigmaEval);
                                if (sigmaAxisEval.sqrMagnitude > 1e-14f)
                                    sigmaAxisEval.Normalize();
                            }
                            MaybeFlipPiTrigonalCanonicalSlotRotForParentHemisphereContinuity(o, ref rotEval, tdEval);
                            Vector3 tipEval = OrbitalSlotPlusXInNucleusLocal(o, rotEval);
                            if (tipEval.sqrMagnitude < 1e-14f) return float.MaxValue;
                            if (mi == sigmaIdxEval && sigmaAxisEval.sqrMagnitude > 1e-14f)
                            {
                                sigmaToAxisDeg = Vector3.Angle(tipEval, sigmaAxisEval);
                                sum += sigmaToAxisDeg;
                            }
                            else
                            {
                                float ang = PiPermutationConeAngleTipToTargetDeg(o, tipEval, tdEval);
                                nonSigmaToTargetDeg = ang;
                                sum += ang;
                            }
                        }
                        return sum;
                    }

                    float s01Post = EvalPostPermScore(0, 1, out float sigma01, out float non01);
                    float s10Post = EvalPostPermScore(1, 0, out float sigma10, out float non10);
                    LogPiTrigonalOcoLine("prePerm postEval atomId=" + GetInstanceID()
                        + " sigmaIdx=" + sigmaIdxEval
                        + " score01=" + s01Post.ToString("F2", CultureInfo.InvariantCulture)
                        + " score10=" + s10Post.ToString("F2", CultureInfo.InvariantCulture)
                        + " sigma01Deg=" + sigma01.ToString("F2", CultureInfo.InvariantCulture)
                        + " sigma10Deg=" + sigma10.ToString("F2", CultureInfo.InvariantCulture)
                        + " non01Deg=" + non01.ToString("F2", CultureInfo.InvariantCulture)
                        + " non10Deg=" + non10.ToString("F2", CultureInfo.InvariantCulture));
                }

                LogTrigonalGroupAngles(
                    "prePermCurrentTips",
                    guideTip.normalized,
                    m0Tip,
                    m1Tip,
                    "guide+movers");
            }
            LogPiTrigonalOcoLine("prePerm frame atomId=" + GetInstanceID()
                + " guideTip={" + guideTip.x.ToString("F4", CultureInfo.InvariantCulture) + "," + guideTip.y.ToString("F4", CultureInfo.InvariantCulture) + "," + guideTip.z.ToString("F4", CultureInfo.InvariantCulture) + "}"
                + " dV0={" + dVertex0.x.ToString("F4", CultureInfo.InvariantCulture) + "," + dVertex0.y.ToString("F4", CultureInfo.InvariantCulture) + "," + dVertex0.z.ToString("F4", CultureInfo.InvariantCulture) + "}"
                + " t0={" + permutationTargetDirs2[0].x.ToString("F4", CultureInfo.InvariantCulture) + "," + permutationTargetDirs2[0].y.ToString("F4", CultureInfo.InvariantCulture) + "," + permutationTargetDirs2[0].z.ToString("F4", CultureInfo.InvariantCulture) + "}"
                + " t1={" + permutationTargetDirs2[1].x.ToString("F4", CultureInfo.InvariantCulture) + "," + permutationTargetDirs2[1].y.ToString("F4", CultureInfo.InvariantCulture) + "," + permutationTargetDirs2[1].z.ToString("F4", CultureInfo.InvariantCulture) + "}");
        }
        // #endregion

        int[] perm2;
        int[] permAxis = null;
        bool usedUnifiedBondWorldCross = false;
        CovalentBond piUnifiedCrossBond = source == RedistributionGuideSource.PiBondInOperation ? redistributionOperationBond : null;
        bool usedInternuclearAxisPerm = nVseprGroups == 3
            && moversOrdered2.Count == 2
            && TryResolveTrigonalTwoSigmaMoverPermUsingInternuclearAxes(
                moversOrdered2,
                permutationTargetDirs2,
                guideTip,
                piUnifiedCrossBond,
                out permAxis,
                out usedUnifiedBondWorldCross);
        if (usedInternuclearAxisPerm && permAxis != null)
            perm2 = permAxis;
        else
        {
            perm2 = FindBestOrbitalToTargetDirsPermutation(moversOrdered2, permutationTargetDirs2, bondRadius, this);
            if (perm2 == null)
            {
                LogGetRedistributeTargets3DLine("guideGroup_permutationFallback", "source=" + source + " reason=perm_null");
                perm2 = Enumerable.Range(0, moversOrdered2.Count).ToArray();
            }
        }

        if (perm2 != null && moversOrdered2.Count >= 2 && moversOrdered2.Count == permutationTargetDirs2.Count)
        {
            ComputePermutationAssignmentCostBreakdown(moversOrdered2, permutationTargetDirs2, bondRadius, this, perm2, out float coneG, out float quatG, out float totalG);
            // #region agent log
            AppendPermCostInvariantNdjson(
                "permCostGuideGroup",
                "AtomFunction.TryBuildRedistributeTargets3DGuideGroupPrefix",
                "perm2_usedInternuclear=" + (usedInternuclearAxisPerm ? "1" : "0") + " source=" + source,
                GetInstanceID(),
                "guideGroup_perm2",
                coneG,
                quatG,
                totalG,
                perm2,
                FormatPermCostDirsRounded(permutationTargetDirs2),
                FormatPermCostTipsForOrbs(moversOrdered2, this),
                0f,
                FormatPermCostDirsRounded(new List<Vector3> { guideTip.normalized }));
            // #endregion
        }

        if (DebugLogPiTrigonalOcoConformation
            && source == RedistributionGuideSource.PiBondInOperation
            && nVseprGroups == 3
            && moversOrdered2.Count == 2
            && perm2 != null
            && perm2.Length == 2)
        {
            int opId = redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID() : 0;
            LogPiTrigonalOcoLine("tryBuild atomId=" + GetInstanceID() + " frame=" + Time.frameCount + " opBondId=" + opId
                + " perm=" + perm2[0] + "," + perm2[1]
                + " usedInternuclearPerm=" + usedInternuclearAxisPerm
                + " unifiedPiPlaneCrossWorld=" + usedUnifiedBondWorldCross);
            // #region agent log
            if (source == RedistributionGuideSource.PiBondInOperation && nVseprGroups == 3)
            {
                float PermTotalWithFindBestRule(int a0, int a1, out float coneSum, out float quatSum)
                {
                    int[] a = { a0, a1 };
                    coneSum = 0f;
                    quatSum = 0f;
                    for (int i = 0; i < 2; i++)
                    {
                        var o = moversOrdered2[i];
                        if (o == null) continue;
                        Vector3 td = permutationTargetDirs2[a[i]].normalized;
                        Vector3 ot = OrbitalTipDirectionInNucleusLocal(o).normalized;
                        coneSum += PiPermutationConeAngleTipToTargetDeg(o, ot, td);
                        if (o.Bond == null)
                            quatSum += QuaternionSlotCostOnly(o, td, bondRadius);
                    }
                    return coneSum + quatSum;
                }
                float total01 = PermTotalWithFindBestRule(0, 1, out float cone01, out float quat01);
                float total10 = PermTotalWithFindBestRule(1, 0, out float cone10, out float quat10);
                LogPiTrigonalOcoLine("permFindBestCost atomId=" + GetInstanceID()
                    + " total01=" + total01.ToString("F2", CultureInfo.InvariantCulture)
                    + " total10=" + total10.ToString("F2", CultureInfo.InvariantCulture)
                    + " cone01=" + cone01.ToString("F2", CultureInfo.InvariantCulture)
                    + " cone10=" + cone10.ToString("F2", CultureInfo.InvariantCulture)
                    + " quat01=" + quat01.ToString("F2", CultureInfo.InvariantCulture)
                    + " quat10=" + quat10.ToString("F2", CultureInfo.InvariantCulture));
                int sigmaIdxDiag = -1;
                for (int di = 0; di < 2; di++)
                {
                    if (moversOrdered2[di] != null && moversOrdered2[di].Bond is CovalentBond cbDiag && cbDiag.IsSigmaBondLine())
                    {
                        sigmaIdxDiag = di;
                        break;
                    }
                }
                if (sigmaIdxDiag >= 0)
                {
                    var sigmaCb = moversOrdered2[sigmaIdxDiag].Bond as CovalentBond;
                    Vector3 sigmaAxisDiag = InternuclearSigmaAxisNucleusLocalForBond(sigmaCb).normalized;
                    Vector3 d0Diag = permutationTargetDirs2[0].normalized;
                    Vector3 d1Diag = permutationTargetDirs2[1].normalized;
                    float angAxisToD0 = Vector3.Angle(sigmaAxisDiag, d0Diag);
                    float angAxisToD1 = Vector3.Angle(sigmaAxisDiag, d1Diag);
                    LogPiTrigonalOcoLine("permDiag atomId=" + GetInstanceID()
                        + " sigmaIdx=" + sigmaIdxDiag
                        + " angSigmaAxisToT0Deg=" + angAxisToD0.ToString("F2", CultureInfo.InvariantCulture)
                        + " angSigmaAxisToT1Deg=" + angAxisToD1.ToString("F2", CultureInfo.InvariantCulture)
                        + " d0DotGuide=" + Vector3.Dot(d0Diag, dVertex0).ToString("F3", CultureInfo.InvariantCulture)
                        + " d1DotGuide=" + Vector3.Dot(d1Diag, dVertex0).ToString("F3", CultureInfo.InvariantCulture));
                }
            }
            // #endregion
            for (int mi = 0; mi < 2; mi++)
            {
                var om = moversOrdered2[mi];
                if (om == null) continue;
                int tix = perm2[mi];
                Vector3 td = permutationTargetDirs2[tix];
                if (td.sqrMagnitude > 1e-14f) td.Normalize();
                Vector3 tip = OrbitalTipDirectionInNucleusLocal(om);
                if (tip.sqrMagnitude > 1e-14f) tip.Normalize();
                float ang = Vector3.Angle(tip, td);
                bool sigmaLine = om.Bond is CovalentBond cm && cm.IsSigmaBondLine();
                LogPiTrigonalOcoLine("tryBuild mover mi=" + mi + " orbId=" + om.GetInstanceID() + " sigmaLine=" + sigmaLine
                    + " parentIsNucleus=" + (om.transform.parent == transform)
                    + " assignTix=" + tix + " angTipToTargetDeg=" + ang.ToString("F2", CultureInfo.InvariantCulture));
                // #region agent log
                if (sigmaLine && source == RedistributionGuideSource.PiBondInOperation && nVseprGroups == 3)
                {
                    Vector3 sigmaAxisDiag = Vector3.zero;
                    if (om.Bond is CovalentBond cbSigmaDiag)
                    {
                        sigmaAxisDiag = InternuclearSigmaAxisNucleusLocalForBond(cbSigmaDiag);
                        if (sigmaAxisDiag.sqrMagnitude > 1e-14f) sigmaAxisDiag.Normalize();
                    }
                    var (_, rotPrefCurrent) = GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(om, td, bondRadius, om.transform.localRotation);
                    var (_, rotPrefIdentity) = GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(om, td, bondRadius, Quaternion.identity);
                    Vector3 tipPrefCurrent = OrbitalSlotPlusXInNucleusLocal(om, rotPrefCurrent);
                    Vector3 tipPrefIdentity = OrbitalSlotPlusXInNucleusLocal(om, rotPrefIdentity);
                    float angCurToTarget = tipPrefCurrent.sqrMagnitude > 1e-14f ? Vector3.Angle(tipPrefCurrent, td) : -1f;
                    float angIdToTarget = tipPrefIdentity.sqrMagnitude > 1e-14f ? Vector3.Angle(tipPrefIdentity, td) : -1f;
                    float angCurToSigma = (tipPrefCurrent.sqrMagnitude > 1e-14f && sigmaAxisDiag.sqrMagnitude > 1e-14f) ? Vector3.Angle(tipPrefCurrent, sigmaAxisDiag) : -1f;
                    float angIdToSigma = (tipPrefIdentity.sqrMagnitude > 1e-14f && sigmaAxisDiag.sqrMagnitude > 1e-14f) ? Vector3.Angle(tipPrefIdentity, sigmaAxisDiag) : -1f;
                    Vector3 tdParent = td;
                    Vector3 curTipParent = (om.transform.localRotation * Vector3.right).normalized;
                    if (om.transform.parent != null && om.transform.parent != transform)
                    {
                        Vector3 tdWorld = transform.TransformDirection(td);
                        tdParent = om.transform.parent.InverseTransformDirection(tdWorld).normalized;
                    }
                    float angCurParentToTdParent = Vector3.Angle(curTipParent, tdParent);
                    float angOppCurParentToTdParent = Vector3.Angle(-curTipParent, tdParent);
                    LogPiTrigonalOcoLine("canonSeedDiag orbId=" + om.GetInstanceID()
                        + " curToTargetDeg=" + angCurToTarget.ToString("F2", CultureInfo.InvariantCulture)
                        + " idToTargetDeg=" + angIdToTarget.ToString("F2", CultureInfo.InvariantCulture)
                        + " curToSigmaDeg=" + angCurToSigma.ToString("F2", CultureInfo.InvariantCulture)
                        + " idToSigmaDeg=" + angIdToSigma.ToString("F2", CultureInfo.InvariantCulture)
                        + " curParentToTargetParentDeg=" + angCurParentToTdParent.ToString("F2", CultureInfo.InvariantCulture)
                        + " oppCurParentToTargetParentDeg=" + angOppCurParentToTdParent.ToString("F2", CultureInfo.InvariantCulture));
                }
                // #endregion
            }
        }

        var nonOpMultiplyPinned = new List<ElectronOrbitalFunction>();
        if (redistributionOperationBond != null)
            CollectNonOperationMultiplyBondGroupOrbitalsOnThisNucleus(redistributionOperationBond, nonOpMultiplyPinned);

        var guideFrozenOrbitals = new List<ElectronOrbitalFunction>();
        if (guideBond != null)
            CollectMultiplyBondGroupOrbitalsOnThisNucleus(guideBond, guideFrozenOrbitals);
        else
            guideFrozenOrbitals.Add(guide);

        bool freezeMultiplyBondGuideCluster = guideBond != null;
        Vector3 postGuideTipN;

        outSlots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>(guideFrozenOrbitals.Count + nonOpMultiplyPinned.Count + moversOrdered2.Count + emp.Count);

        if (freezeMultiplyBondGuideCluster)
        {
            bool canonSlotsFromFramework = nVseprGroups == 3 && dVertex0.sqrMagnitude > 1e-14f;
            Quaternion? guideCanonRotForPostTip = null;
            foreach (var go in guideFrozenOrbitals)
            {
                if (go == null) continue;
                if (canonSlotsFromFramework)
                {
                    Vector3 targetDir;
                    if (go == guide)
                        targetDir = dVertex0.normalized;
                    else if (go.Bond is CovalentBond cbCl && cbCl.IsSigmaBondLine())
                    {
                        Vector3 ax = InternuclearSigmaAxisNucleusLocalForBond(cbCl);
                        targetDir = ax.sqrMagnitude > 1e-14f ? ax.normalized : OrbitalTipDirectionInNucleusLocal(go).normalized;
                    }
                    else
                    {
                        Vector3 tipF = OrbitalTipDirectionInNucleusLocal(go);
                        targetDir = tipF.sqrMagnitude > 1e-14f ? tipF.normalized : dVertex0.normalized;
                    }
                    var (gPos, gRot) = GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(go, targetDir, bondRadius, go.transform.localRotation);
                    MaybeFlipPiTrigonalCanonicalSlotRotForParentHemisphereContinuity(go, ref gRot, targetDir);
                    if (go == guide)
                        guideCanonRotForPostTip = gRot;
                    outSlots.Add((go, gPos, gRot));
                }
                else
                    outSlots.Add((go, go.transform.localPosition, go.transform.localRotation));
            }
            var guideFrozenSetForNonOp = new HashSet<ElectronOrbitalFunction>(guideFrozenOrbitals);
            foreach (var no in nonOpMultiplyPinned)
            {
                if (no == null || guideFrozenSetForNonOp.Contains(no)) continue;
                outSlots.Add((no, no.transform.localPosition, no.transform.localRotation));
            }
            if (guideCanonRotForPostTip.HasValue)
                postGuideTipN = OrbitalSlotPlusXInNucleusLocal(guide, guideCanonRotForPostTip.Value);
            else
            {
                postGuideTipN = OrbitalTipDirectionInNucleusLocal(guide);
                if (postGuideTipN.sqrMagnitude > 1e-14f)
                    postGuideTipN.Normalize();
                else
                    postGuideTipN = guideTip.normalized;
            }
            // #region agent log
            if (DebugLogPiTrigonalOcoConformation
                && source == RedistributionGuideSource.PiBondInOperation
                && nVseprGroups == 3)
            {
                float angG = postGuideTipN.sqrMagnitude > 1e-14f ? Vector3.Angle(postGuideTipN, dVertex0.normalized) : -1f;
                LogPiTrigonalOcoLine("postSlot guide orbId=" + guide.GetInstanceID()
                    + " angTipToVertex0Deg=" + angG.ToString("F2", CultureInfo.InvariantCulture)
                    + " multiplyBondClusterPinned=true canonSlotsFromFramework=" + (canonSlotsFromFramework ? "true" : "false"));
            }
            // #endregion
            orbitalsExcludedFromJointRigidInApplyRedistributeTargets = new HashSet<ElectronOrbitalFunction>(guideFrozenOrbitals);
            foreach (var no in nonOpMultiplyPinned)
                if (no != null) orbitalsExcludedFromJointRigidInApplyRedistributeTargets.Add(no);
            // π formation on (epA,epB): joint σ-fragment motion would drag off-endpoint partners that do not get GetRedistributeTargets in π step 2. Pin only edges that are already multiply bonded on this nucleus (e.g. O=C=O: remote C=O leg). Do not pin σ-only neighbors (e.g. O=C–O from O–C–O: the single-bonded O must still move with redistribution); see GetBondsTo.
            if (redistributionOperationBond != null
                && !redistributionOperationBond.IsSigmaBondLine()
                && redistributionOperationBond.AtomA != null
                && redistributionOperationBond.AtomB != null)
            {
                AtomFunction ep0 = redistributionOperationBond.AtomA;
                AtomFunction ep1 = redistributionOperationBond.AtomB;
                foreach (var b in covalentBonds)
                {
                    if (b == null || b.Orbital == null || !b.IsSigmaBondLine()) continue;
                    if (b.AtomA != this && b.AtomB != this) continue;
                    AtomFunction partner = b.AtomA == this ? b.AtomB : b.AtomA;
                    if (partner == null) continue;
                    if (partner != ep0 && partner != ep1 && GetBondsTo(partner) > 1)
                        orbitalsExcludedFromJointRigidInApplyRedistributeTargets.Add(b.Orbital);
                }
            }
        }
        else
        {
            var (guidePos, guideRot) = GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(guide, dVertex0, bondRadius, guide.transform.localRotation);
            if (source == RedistributionGuideSource.PiBondInOperation && nVseprGroups == 3)
                MaybeFlipPiTrigonalCanonicalSlotRotForParentHemisphereContinuity(guide, ref guideRot, dVertex0.sqrMagnitude > 1e-14f ? dVertex0.normalized : (Vector3?)null);
            // #region agent log
            if (DebugLogPiTrigonalOcoConformation
                && source == RedistributionGuideSource.PiBondInOperation
                && nVseprGroups == 3)
            {
                Vector3 gTip = OrbitalSlotPlusXInNucleusLocal(guide, guideRot);
                float angG = gTip.sqrMagnitude > 1e-14f ? Vector3.Angle(gTip, dVertex0.normalized) : -1f;
                LogPiTrigonalOcoLine("postSlot guide orbId=" + guide.GetInstanceID()
                    + " angTipToVertex0Deg=" + angG.ToString("F2", CultureInfo.InvariantCulture));
            }
            // #endregion
            outSlots.Add((guide, guidePos, guideRot));
            foreach (var no in nonOpMultiplyPinned)
            {
                if (no == null || no == guide) continue;
                outSlots.Add((no, no.transform.localPosition, no.transform.localRotation));
            }
            postGuideTipN = OrbitalSlotPlusXInNucleusLocal(guide, guideRot);
            orbitalsExcludedFromJointRigidInApplyRedistributeTargets = new HashSet<ElectronOrbitalFunction> { guide };
            foreach (var no in nonOpMultiplyPinned)
                if (no != null && no != guide) orbitalsExcludedFromJointRigidInApplyRedistributeTargets.Add(no);
        }

        var postMoverTipsN = new Vector3[moversOrdered2.Count];
        for (int i = 0; i < moversOrdered2.Count; i++)
        {
            int t = perm2[i];
            Vector3 d = permutationTargetDirs2[t];
            if (d.sqrMagnitude < 1e-14f)
            {
                var o = moversOrdered2[i];
                outSlots.Add((o, o.transform.localPosition, o.transform.localRotation));
            }
            else
            {
                var o = moversOrdered2[i];
                var (pos, rot) = GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent(o, d.normalized, bondRadius, o.transform.localRotation);
                Quaternion rotBeforeHemi = rot;
                if (nVseprGroups == 3)
                    MaybeFlipPiTrigonalCanonicalSlotRotForParentHemisphereContinuity(o, ref rot, d.normalized);
                // #region agent log
                if (DebugLogPiTrigonalOcoConformation
                    && source == RedistributionGuideSource.PiBondInOperation
                    && nVseprGroups == 3)
                {
                    bool hemiApplied = Quaternion.Angle(rotBeforeHemi, rot) > 179.9f;
                    Vector3 tipN = OrbitalSlotPlusXInNucleusLocal(o, rot);
                    Vector3 tipNPre = OrbitalSlotPlusXInNucleusLocal(o, rotBeforeHemi);
                    postMoverTipsN[i] = tipN;
                    Vector3 ideal = d.normalized;
                    float angPost = tipN.sqrMagnitude > 1e-14f ? Vector3.Angle(tipN, ideal) : -1f;
                    float angPostOpp = tipN.sqrMagnitude > 1e-14f ? Vector3.Angle(-tipN, ideal) : -1f;
                    float angPre = tipNPre.sqrMagnitude > 1e-14f ? Vector3.Angle(tipNPre, ideal) : -1f;
                    float angPreOpp = tipNPre.sqrMagnitude > 1e-14f ? Vector3.Angle(-tipNPre, ideal) : -1f;
                    string sigmaAxisPart = "";
                    if (o.Bond is CovalentBond cbLog && cbLog.IsSigmaBondLine())
                    {
                        Vector3 axL = InternuclearSigmaAxisNucleusLocalForBond(cbLog);
                        if (axL.sqrMagnitude > 1e-14f)
                            sigmaAxisPart = " angTipToSigmaAxisDeg=" + (tipN.sqrMagnitude > 1e-14f
                                ? Vector3.Angle(tipN, axL.normalized).ToString("F2", CultureInfo.InvariantCulture)
                                : "na");
                    }
                    LogPiTrigonalOcoLine("postSlot mover orbId=" + o.GetInstanceID()
                        + " angTipToAssignIdealDeg=" + angPost.ToString("F2", CultureInfo.InvariantCulture)
                        + " angOppTipToAssignIdealDeg=" + angPostOpp.ToString("F2", CultureInfo.InvariantCulture)
                        + sigmaAxisPart);
                    if (atomicNumber == 8 && o.Bond == null && o.transform.parent == transform)
                    {
                        LogPiTrigonalOcoLine("hemiEval hypothesisId=H1 loneMover orbId=" + o.GetInstanceID()
                            + " hemiApplied=" + (hemiApplied ? "true" : "false")
                            + " preTipToIdealDeg=" + angPre.ToString("F2", CultureInfo.InvariantCulture)
                            + " preOppToIdealDeg=" + angPreOpp.ToString("F2", CultureInfo.InvariantCulture)
                            + " postTipToIdealDeg=" + angPost.ToString("F2", CultureInfo.InvariantCulture)
                            + " postOppToIdealDeg=" + angPostOpp.ToString("F2", CultureInfo.InvariantCulture));
                    }
                }
                // #endregion
                outSlots.Add((o, pos, rot));
            }
        }

        // #region agent log
        if (DebugLogPiTrigonalOcoConformation
            && source == RedistributionGuideSource.PiBondInOperation
            && nVseprGroups == 3
            && moversOrdered2.Count == 2)
        {
            LogTrigonalGroupAngles(
                "postSlotTips",
                postGuideTipN,
                postMoverTipsN[0],
                postMoverTipsN[1],
                "guide+movers");
        }
        // #endregion

        var electronFrameworkLocal = new List<Vector3>(nVseprGroups);
        electronFrameworkLocal.Add(guideTip);
        for (int vi = 1; vi < nVseprGroups; vi++)
            electronFrameworkLocal.Add(alignedIdeal[vi].normalized);

        var fwForEmptySkip = new List<Vector3>(electronFrameworkLocal.Count);
        foreach (var fd in electronFrameworkLocal)
        {
            if (fd.sqrMagnitude > 1e-10f)
                fwForEmptySkip.Add(fd.normalized);
        }

        var coBondTipsForOverlap = new List<Vector3>();
        CollectGuideGroupCoBondOrbitalTipsNucleusLocal(guideBond, redistributionOperationBond, guide, coBondTipsForOverlap);
        var occupiedAxesForEmptyIdeal = new List<Vector3>(1 + moversOrdered2.Count + coBondTipsForOverlap.Count);
        AppendUniqueFrameworkDirection(occupiedAxesForEmptyIdeal, dVertex0);
        foreach (var o in moversOrdered2)
        {
            var t = OrbitalTipDirectionInNucleusLocal(o);
            if (t.sqrMagnitude > 1e-12f) occupiedAxesForEmptyIdeal.Add(t.normalized);
        }
        foreach (var t in coBondTipsForOverlap)
            AppendUniqueFrameworkDirection(occupiedAxesForEmptyIdeal, t);

        const float emptySepDegGuideMulti = 18f;
        var placedEmptyTipsLocal = new List<Vector3>();
        foreach (var e in emp)
        {
            Vector3 tipPref = OrbitalTipLocalDirection(e);
            Vector3? prefDir = tipPref.sqrMagnitude >= 1e-10f ? tipPref.normalized : (Vector3?)null;
            if (prefDir.HasValue
                && EmptyTipAlreadyIdealVsElectronFramework(
                    prefDir.Value,
                    fwForEmptySkip,
                    occupiedLobeAxesMustSeparateFrom: occupiedAxesForEmptyIdeal)
                && MinAngleToAnyDirection(prefDir.Value, placedEmptyTipsLocal) > emptySepDegGuideMulti)
            {
                outSlots.Add((e, e.transform.localPosition, e.transform.localRotation));
                placedEmptyTipsLocal.Add(prefDir.Value);
                continue;
            }
            if (!TryComputeSeparatedEmptySlot(electronFrameworkLocal, placedEmptyTipsLocal, bondRadius, out var ePos, out var eRot, prefDir))
            {
                outSlots.Add((e, e.transform.localPosition, e.transform.localRotation));
                continue;
            }
            outSlots.Add((e, ePos, eRot));
            placedEmptyTipsLocal.Add((eRot * Vector3.right).normalized);
        }

        LogGetRedistributeTargets3DLine(
            "EXIT_repulsionOnly_guideGroup",
            "targets=" + outSlots.Count + " source=" + source
            + " nVseprGroups=" + nVseprGroups
            + " occMovers=" + moversOrdered2.Count
            + " empSlots=" + emp.Count
            + " mergedCoBondOccRemoved=" + mergedCoBondOccRemoved
            + " guideOrbId=" + guide.GetInstanceID()
            + " guideOrbE=" + guide.ElectronCount
            + " guideOnNucleus=" + (guide.transform.parent == transform ? "true" : "false")
            + " guideTipLocal={"
            + OrbitalTipLocalDirection(guide).x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
            + OrbitalTipLocalDirection(guide).y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ","
            + OrbitalTipLocalDirection(guide).z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "}"
            + " guideVertex0InTargets=True guideExcludedFromJointRigid=True guideBondId=" + (guideBond != null ? guideBond.GetInstanceID().ToString() : "null")
            + " opBondId=" + (redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID().ToString() : "null"));
        // #region agent log
        if (DebugLogOcoSecondPiNdjson && atomicNumber == 8)
        {
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H1",
                "AtomFunction.TryBuildRedistributeTargets3DGuideGroupPrefix",
                "[oco-remote-freeze] guide_group_exit",
                "{\"atomId\":" + GetInstanceID()
                + ",\"source\":\"" + source + "\""
                + ",\"freezeCluster\":" + (freezeMultiplyBondGuideCluster ? "true" : "false")
                + ",\"guideClusterN\":" + guideFrozenOrbitals.Count
                + ",\"nonOpPinN\":" + nonOpMultiplyPinned.Count
                + ",\"occMovers\":" + moversOrdered2.Count
                + ",\"emp\":" + emp.Count
                + ",\"outSlots\":" + outSlots.Count
                + ",\"jointRigidN\":" + (orbitalsExcludedFromJointRigidInApplyRedistributeTargets != null ? orbitalsExcludedFromJointRigidInApplyRedistributeTargets.Count.ToString() : "0")
                + ",\"opBondId\":" + (redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID().ToString() : "0")
                + ",\"guideBondId\":" + (guideBond != null ? guideBond.GetInstanceID().ToString() : "0") + "}");
        }
        // #endregion
        return true;
    }

    void LogGetRedistributeTargets3DLine(string branch, string detail = null)
    {
        if (!CovalentBond.DebugLogBondBreakTetraFramework) return;
        string msg =
            "[break-tetra] GetRedistributeTargets3D " + branch + " atom=" + name + "(Z=" + atomicNumber + ") id=" + GetInstanceID() +
            (string.IsNullOrEmpty(detail) ? "" : " | " + detail);
        Debug.Log(msg);
        ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
    }

    /// <summary>Guide-group resolution and TryBuild failure reasons; gated by <see cref="CovalentBond.DebugLogBondBreakTetraFramework"/>.</summary>
    void LogGuideGroupTrace(string detail)
    {
        if (!CovalentBond.DebugLogBondBreakTetraFramework) return;
        string msg = "[guide-group-trace] atom=" + name + "(Z=" + atomicNumber + ") id=" + GetInstanceID() + " " + detail;
        Debug.Log(msg);
        ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
    }

    /// <summary>
    /// Repulsion-only 3D targets: σ-cleavage 3+1 shell, σN==0 four-non-bond, then combined σ+occupied non-bond domains, then non-bond-only, then empty-only legacy append. No VSEPR ideal polyhedra or <see cref="TryMatchLoneOrbitalsToFreeIdealDirections"/>.
    /// Electron-domain σ rows use nucleus-local ideal directions but parent-local <c>(pos,rot)</c>; see <see cref="TryComputeRepulsionSumElectronDomainLayoutSlots"/> remarks.
    /// </summary>
    List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> GetRedistributeTargets3DRepulsionLayoutOnly(
        int piBefore,
        AtomFunction newBondPartner,
        float? piBondAngleOverrideForBreakTargets,
        Vector3? refBondWorldDirectionForBreakTargets,
        ElectronOrbitalFunction bondBreakGuideLoneOrbitalForTargets,
        int sigmaNeighborCountBefore,
        bool bondBreakIsSigmaCleavageBetweenFormerPartners,
        CovalentBond redistributionOperationBond = null)
    {
        _ = piBefore;
        var result = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1)
        {
            LogGetRedistributeTargets3DLine("EXIT_maxSlots<=1_repulsionOnly", "maxSlots=" + maxSlots);
            return result;
        }

        bool useBreakRefs = bondBreakGuideLoneOrbitalForTargets != null || piBondAngleOverrideForBreakTargets.HasValue || refBondWorldDirectionForBreakTargets.HasValue;
        bool useSigmaCleavageRefForVsepr = useBreakRefs && bondBreakIsSigmaCleavageBetweenFormerPartners;
        ElectronOrbitalFunction sigmaCleavageGuideForTargets = bondBreakGuideLoneOrbitalForTargets;
        if (useSigmaCleavageRefForVsepr
            && (sigmaCleavageGuideForTargets == null
                || sigmaCleavageGuideForTargets.Bond != null
                || sigmaCleavageGuideForTargets.transform.parent != transform
                || sigmaCleavageGuideForTargets.ElectronCount != 0))
        {
            var localEmpties = bondedOrbitals
                .Where(o => o != null && o.Bond == null && o.transform.parent == transform && o.ElectronCount == 0)
                .OrderByDescending(o => o.GetInstanceID())
                .ToList();
            if (localEmpties.Count > 0)
                sigmaCleavageGuideForTargets = localEmpties[0];
        }

        LogGetRedistributeTargets3DLine(
            "ENTER_repulsionOnly",
            "maxSlots=" + maxSlots + " useBreakRefs=" + useBreakRefs + " σCleavageRefVsepr=" + useSigmaCleavageRefForVsepr + " σN=" + GetDistinctSigmaNeighborCount() + " π=" + GetPiBondCount() +
            " guideOrb=" + (bondBreakGuideLoneOrbitalForTargets != null) + " refW=" + refBondWorldDirectionForBreakTargets.HasValue);

        // Guide group (tiers 1–6): tier 5 (occupied ex-bond lone) follows tiers 1–4 VSEPR + mover permutation; do not skip when σ-cleavage framing is on — that path only ran TryComputeRepulsionSigmaCleavageBondBreakSlots below and skipped tier 5.
        if (TryBuildRedistributeTargets3DGuideGroupPrefix(redistributionOperationBond, bondBreakGuideLoneOrbitalForTargets, out var guideGrpTargets, out bool cancelRedistEmptyMovers)
            && guideGrpTargets != null)
        {
            result.AddRange(guideGrpTargets);
            return result;
        }
        if (cancelRedistEmptyMovers)
        {
            Debug.LogError("[redist-guide-group] Redistribution cancelled: no movers for guide-group layout after resolving guide. atomId=" + GetInstanceID() + " Z=" + atomicNumber);
            LogGetRedistributeTargets3DLine("ABORT_redistribution_movers_empty", "repulsionOnly=true");
            return result;
        }

        Vector3 refWorldFallback = refBondWorldDirectionForBreakTargets.HasValue
            ? refBondWorldDirectionForBreakTargets.Value
            : transform.TransformDirection(Vector3.right);

        if (useSigmaCleavageRefForVsepr && refBondWorldDirectionForBreakTargets.HasValue && sigmaCleavageGuideForTargets != null
            && IsBondBreakGuideOrbitalWithBondingCleared(sigmaCleavageGuideForTargets)
            && TryComputeRepulsionSigmaCleavageBondBreakSlots(
                refBondWorldDirectionForBreakTargets.Value,
                sigmaCleavageGuideForTargets,
                out var repulsionShell,
                out _, out _)
            && repulsionShell != null && repulsionShell.Count > 0)
        {
            result.AddRange(repulsionShell);
            LogGetRedistributeTargets3DLine("EXIT_repulsionOnly_sigmaCleavageShell", "targets=" + repulsionShell.Count);
            return result;
        }

        if (bondBreakGuideLoneOrbitalForTargets != null
            && TryComputeSigmaNZeroExBondGuideTrigonalBondBreakSlots(
                refWorldFallback,
                bondBreakGuideLoneOrbitalForTargets,
                out var sigmaNZeroSlots,
                out _, out _)
            && sigmaNZeroSlots != null && sigmaNZeroSlots.Count > 0)
        {
            result.AddRange(sigmaNZeroSlots);
            LogGetRedistributeTargets3DLine("EXIT_repulsionOnly_sigmaN0FourNonbond", "targets=" + sigmaNZeroSlots.Count);
            return result;
        }

        GetCarbonSigmaCleavageDomains(out var sigAll, out var nbAll);
        var occDom = new List<ElectronOrbitalFunction>();
        foreach (var o in sigAll)
            if (o != null && o.ElectronCount > 0) occDom.Add(o);
        foreach (var o in nbAll)
            if (o != null && o.ElectronCount > 0) occDom.Add(o);
        occDom = occDom.Distinct().OrderBy(o => o.GetInstanceID()).ToList();
        var empDom = nbAll.Where(o => o != null && o.ElectronCount == 0).OrderBy(o => o.GetInstanceID()).ToList();
        ElectronOrbitalFunction pinDom = null;
        if (sigmaCleavageGuideForTargets != null && sigmaCleavageGuideForTargets.ElectronCount == 0 && IsBondBreakGuideOrbitalWithBondingCleared(sigmaCleavageGuideForTargets) && empDom.Contains(sigmaCleavageGuideForTargets))
            pinDom = sigmaCleavageGuideForTargets;
        else if (bondBreakGuideLoneOrbitalForTargets != null && bondBreakGuideLoneOrbitalForTargets.ElectronCount == 0 && IsBondBreakGuideOrbitalWithBondingCleared(bondBreakGuideLoneOrbitalForTargets) && empDom.Contains(bondBreakGuideLoneOrbitalForTargets))
            pinDom = bondBreakGuideLoneOrbitalForTargets;

        if (occDom.Count >= 1 && occDom.Count <= 6 && empDom.Count <= 3
            && TryComputeRepulsionSumElectronDomainLayoutSlots(occDom, empDom, pinDom, out var electronDomainSlots, out _, out _)
            && electronDomainSlots != null && electronDomainSlots.Count > 0)
        {
            result.AddRange(electronDomainSlots);
            LogGetRedistributeTargets3DLine("EXIT_repulsionOnly_electronDomains", "targets=" + electronDomainSlots.Count + " occDom=" + occDom.Count + " empDom=" + empDom.Count);
            return result;
        }

        var occNb = bondedOrbitals.Where(o => o != null && o.Bond == null && o.transform.parent == transform && o.ElectronCount > 0).OrderBy(o => o.GetInstanceID()).ToList();
        var empNb = bondedOrbitals.Where(o => o != null && o.Bond == null && o.transform.parent == transform && o.ElectronCount == 0).OrderBy(o => o.GetInstanceID()).ToList();
        ElectronOrbitalFunction pinNb = pinDom;
        if (occNb.Count >= 1 && occNb.Count <= 6 && empNb.Count <= 3
            && TryComputeRepulsionSumNonBondLayoutSlots(occNb, empNb, pinNb, out var nonbondSlots, out _, out _)
            && nonbondSlots != null && nonbondSlots.Count > 0)
        {
            result.AddRange(nonbondSlots);
            LogGetRedistributeTargets3DLine("EXIT_repulsionOnly_nonbondOnly", "targets=" + nonbondSlots.Count);
            return result;
        }

        if (occNb.Count == 0)
        {
            LogGetRedistributeTargets3DLine("branch_zeroOccupiedLone_repulsionOnly", "σCleavageRefVsepr=" + useSigmaCleavageRefForVsepr);
            if (!useSigmaCleavageRefForVsepr)
            {
                bool sigmaIncreased = sigmaNeighborCountBefore >= 0 && GetDistinctSigmaNeighborCount() > sigmaNeighborCountBefore;
                if (newBondPartner != null || sigmaIncreased || useBreakRefs)
                    AppendEmptyNonbondRedistributeTargetsForSigmaFramework(result, null, sigmaCleavageGuideForTargets, bondBreakIsSigmaCleavageBetweenFormerPartners);
            }
            else if (sigmaCleavageGuideForTargets != null
                && sigmaCleavageGuideForTargets.ElectronCount == 0
                && IsBondBreakGuideOrbitalWithBondingCleared(sigmaCleavageGuideForTargets))
            {
                AppendEmptyNonbondRedistributeTargetsForSigmaFramework(result, null, sigmaCleavageGuideForTargets, bondBreakIsSigmaCleavageBetweenFormerPartners);
            }
            LogGetRedistributeTargets3DLine("EXIT_zeroLone_repulsionOnly", "resultCount=" + result.Count);
            return result;
        }

        LogGetRedistributeTargets3DLine("EXIT_repulsionOnly_noMatch", "occNb=" + occNb.Count + " empNb=" + empNb.Count + " occDom=" + occDom.Count);
        return result;
    }

    List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> GetRedistributeTargets3D(int piBefore, AtomFunction newBondPartner, float? piBondAngleOverrideForBreakTargets, Vector3? refBondWorldDirectionForBreakTargets, ElectronOrbitalFunction bondBreakGuideLoneOrbitalForTargets, int sigmaNeighborCountBefore = -1, bool bondBreakIsSigmaCleavageBetweenFormerPartners = false, CovalentBond redistributionOperationBond = null, ElectronOrbitalFunction vseprDisappearingLoneForPredictiveCount = null)
    {
        orbitalsExcludedFromJointRigidInApplyRedistributeTargets = null;
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets3d;
        if (UseRepulsionLayoutOnlyInGetRedistributeTargets3D)
            targets3d = GetRedistributeTargets3DRepulsionLayoutOnly(
                piBefore,
                newBondPartner,
                piBondAngleOverrideForBreakTargets,
                refBondWorldDirectionForBreakTargets,
                bondBreakGuideLoneOrbitalForTargets,
                sigmaNeighborCountBefore,
                bondBreakIsSigmaCleavageBetweenFormerPartners,
                redistributionOperationBond);
        else
            targets3d = GetRedistributeTargets3DVseprTryMatch(
                piBefore,
                newBondPartner,
                piBondAngleOverrideForBreakTargets,
                refBondWorldDirectionForBreakTargets,
                bondBreakGuideLoneOrbitalForTargets,
                sigmaNeighborCountBefore,
                bondBreakIsSigmaCleavageBetweenFormerPartners,
                redistributionOperationBond,
                vseprDisappearingLoneForPredictiveCount);
        // #region agent log
        if (DebugLogOcoSecondPiNdjson && (AtomicNumber == 6 || AtomicNumber == 8))
        {
            int frozenN = orbitalsExcludedFromJointRigidInApplyRedistributeTargets?.Count ?? 0;
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H6-build",
                "AtomFunction.GetRedistributeTargets3D",
                "joint_rigid_exclusions_after_build",
                "{\"atomId\":" + GetInstanceID()
                + ",\"Z\":" + AtomicNumber
                + ",\"targetN\":" + (targets3d != null ? targets3d.Count : 0).ToString(CultureInfo.InvariantCulture)
                + ",\"jointRigidFrozenN\":" + frozenN.ToString(CultureInfo.InvariantCulture)
                + ",\"repulsionOnly\":" + (UseRepulsionLayoutOnlyInGetRedistributeTargets3D ? "true" : "false") + "}");
        }
        // #endregion
        return targets3d;
    }

    /// <summary>Legacy VSEPR + <c>TryMatch</c> redistribution; may take guide-group prefix (tiers 1–6) before σ-cleavage / carbocation fallbacks.</summary>
    List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> GetRedistributeTargets3DVseprTryMatch(int piBefore, AtomFunction newBondPartner, float? piBondAngleOverrideForBreakTargets, Vector3? refBondWorldDirectionForBreakTargets, ElectronOrbitalFunction bondBreakGuideLoneOrbitalForTargets, int sigmaNeighborCountBefore = -1, bool bondBreakIsSigmaCleavageBetweenFormerPartners = false, CovalentBond redistributionOperationBond = null, ElectronOrbitalFunction vseprDisappearingLoneForPredictiveCount = null)
    {
        var result = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1)
        {
            LogGetRedistributeTargets3DLine("EXIT_maxSlots<=1", "maxSlots=" + maxSlots);
            return result;
        }

        float mergeToleranceDeg = 360f / (2f * maxSlots);
        bool useBreakRefs = bondBreakGuideLoneOrbitalForTargets != null || piBondAngleOverrideForBreakTargets.HasValue || refBondWorldDirectionForBreakTargets.HasValue;
        // π-only break still passes ref+guide for animation/OrientEmpty — but VSEPR must follow the surviving σ framework, not σ-cleavage framing.
        bool useSigmaCleavageRefForVsepr = useBreakRefs && bondBreakIsSigmaCleavageBetweenFormerPartners;
        ElectronOrbitalFunction sigmaCleavageGuideForTargets = bondBreakGuideLoneOrbitalForTargets;
        if (useSigmaCleavageRefForVsepr
            && (sigmaCleavageGuideForTargets == null
                || sigmaCleavageGuideForTargets.Bond != null
                || sigmaCleavageGuideForTargets.transform.parent != transform
                || sigmaCleavageGuideForTargets.ElectronCount != 0))
        {
            // In some break phases, the caller-provided guide can already hold electrons while a different local 0e is the
            // actual ex-bond empty guide for this center. Resolve to local empty first to keep σ-cleavage branch stable.
            var localEmpties = bondedOrbitals
                .Where(o => o != null && o.Bond == null && o.transform.parent == transform && o.ElectronCount == 0)
                .OrderByDescending(o => o.GetInstanceID())
                .ToList();
            if (localEmpties.Count > 0)
                sigmaCleavageGuideForTargets = localEmpties[0];
        }
        Vector3 refLocal = useSigmaCleavageRefForVsepr
            ? ResolveReferenceBondDirectionLocal(piBondAngleOverrideForBreakTargets, refBondWorldDirectionForBreakTargets, sigmaCleavageGuideForTargets)
            : RedistributeReferenceDirectionLocalForTargets(newBondPartner);
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;
        else refLocal.Normalize();

        LogGetRedistributeTargets3DLine(
            "ENTER",
            "maxSlots=" + maxSlots + " useBreakRefs=" + useBreakRefs + " σCleavageRefVsepr=" + useSigmaCleavageRefForVsepr + " σN=" + GetDistinctSigmaNeighborCount() + " π=" + GetPiBondCount() +
            " mergeTolDeg=" + mergeToleranceDeg.ToString("F2") + " σTipVsAxisMax=" + SigmaTipsVsBondAxesMaxAngleDeg().ToString("F2") + "° guideOrb=" +
            (bondBreakGuideLoneOrbitalForTargets != null) + " refW=" + refBondWorldDirectionForBreakTargets.HasValue);

        // #region agent log
        if (DebugLogOcoSecondPiNdjson && redistributionOperationBond != null && !useBreakRefs)
        {
            var sbC = new System.Text.StringBuilder(192);
            sbC.Append("{\"atomId\":").Append(GetInstanceID());
            sbC.Append(",\"Z\":").Append(atomicNumber);
            sbC.Append(",\"piBefore\":").Append(piBefore);
            sbC.Append(",\"piNow\":").Append(GetPiBondCount());
            sbC.Append(",\"partnerId\":").Append(newBondPartner != null ? newBondPartner.GetInstanceID() : 0);
            sbC.Append(",\"sigmaNBefore\":").Append(sigmaNeighborCountBefore);
            sbC.Append(",\"sigmaNNow\":").Append(GetDistinctSigmaNeighborCount());
            sbC.Append(",\"opBondId\":").Append(redistributionOperationBond.GetInstanceID());
            sbC.Append('}');
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "C",
                "AtomFunction.GetRedistributeTargets3DVseprTryMatch",
                "enter_with_op_bond",
                sbC.ToString());
        }
        // #endregion

        if (TryBuildRedistributeTargets3DGuideGroupPrefix(redistributionOperationBond, bondBreakGuideLoneOrbitalForTargets, out var guideGrpTargetsVsepr, out bool cancelRedistEmptyMoversVsepr)
            && guideGrpTargetsVsepr != null)
        {
            result.AddRange(guideGrpTargetsVsepr);
            return result;
        }
        if (cancelRedistEmptyMoversVsepr)
        {
            Debug.LogError("[redist-guide-group] Redistribution cancelled: no movers for guide-group layout after resolving guide. atomId=" + GetInstanceID() + " Z=" + atomicNumber);
            LogGetRedistributeTargets3DLine("ABORT_redistribution_movers_empty", "repulsionOnly=false");
            return result;
        }

        // Full σ cleavage (no bond edge left between former partners): default = repulsion among 3 occupied domains + 0e guide; legacy carbocation ex-bond-ref trigonal as fallback.
        // σN==0 bare centers still use repulsion via TryComputeCarbocationBondBreakSlots → TryComputeSigmaNZeroExBondGuideTrigonalBondBreakSlots when repulsion shell fails.
        // Guide must be the cleaved ex-bond lobe (no Bond on that orbital). Do **not** use π count on this center: π-only break of C=C yields π=0 while σ still links the atoms → must match <see cref="CovalentBond.BreakBond"/> flag σCleavageBetweenPartners only.
        if (useSigmaCleavageRefForVsepr && refBondWorldDirectionForBreakTargets.HasValue && sigmaCleavageGuideForTargets != null
            && IsBondBreakGuideOrbitalWithBondingCleared(sigmaCleavageGuideForTargets)
            && TryComputeRepulsionSigmaCleavageBondBreakSlots(
                refBondWorldDirectionForBreakTargets.Value,
                sigmaCleavageGuideForTargets,
                out var repulsionSlots,
                out _,
                out _)
            && repulsionSlots != null && repulsionSlots.Count > 0)
        {
            result.AddRange(repulsionSlots);
            LogGetRedistributeTargets3DLine("EXIT_repulsionSigmaCleavageBondBreakSlots", "targets=" + repulsionSlots.Count);
            return result;
        }
        if (useSigmaCleavageRefForVsepr && refBondWorldDirectionForBreakTargets.HasValue && sigmaCleavageGuideForTargets != null
            && IsBondBreakGuideOrbitalWithBondingCleared(sigmaCleavageGuideForTargets)
            && TryComputeCarbocationBondBreakSlots(
                refBondWorldDirectionForBreakTargets.Value,
                sigmaCleavageGuideForTargets,
                out var carbSlots,
                out _,
                out _)
            && carbSlots != null && carbSlots.Count > 0)
        {
            result.AddRange(carbSlots);
            LogGetRedistributeTargets3DLine("EXIT_carbocationBondBreakSlots", "targets=" + carbSlots.Count);
            return result;
        }

        var loneOrbitalsRaw = bondedOrbitals.Where(orb => orb != null && orb.Bond == null && orb.ElectronCount > 0).ToList();
        bool applyPredictiveVseprDomains = ShouldApplyPredictiveVseprDomainModelForTryMatch(useSigmaCleavageRefForVsepr, newBondPartner, redistributionOperationBond);
        BuildPredictiveVseprTryMatchLoneOccupiedAndBondAxes(
            loneOrbitalsRaw,
            mergeToleranceDeg,
            applyPredictiveVseprDomains,
            newBondPartner,
            redistributionOperationBond,
            vseprDisappearingLoneForPredictiveCount,
            out var loneOrbitals,
            out var bondAxes,
            out int bondAxesMergedRawCount,
            out int vseprFadeExcludeId,
            out int vseprExplicitExcludeId);

        if (DebugLogVseprDomainPredict && applyPredictiveVseprDomains)
        {
            int opId = redistributionOperationBond != null ? redistributionOperationBond.GetInstanceID() : 0;
            Debug.Log(
                "[vsepr-domain-predict] atomId=" + GetInstanceID() + " Z=" + atomicNumber +
                " occLoneRaw=" + loneOrbitalsRaw.Count + " occLoneFiltered=" + loneOrbitals.Count +
                " bondAxesRaw=" + bondAxesMergedRawCount + " bondAxesAugmented=" + bondAxes.Count +
                " opBondId=" + opId + " fadeExclId=" + vseprFadeExcludeId + " explicitExclId=" + vseprExplicitExcludeId);
        }

        if (loneOrbitals.Count == 0)
        {
            LogGetRedistributeTargets3DLine("branch_zeroOccupiedLone", "σCleavageRefVsepr=" + useSigmaCleavageRefForVsepr + " useBreakRefs=" + useBreakRefs);
            // π-only break sets useBreakRefs (ref+guide for CoAnimate) but not σCleavageRefVsepr — do not gate empty slots on `!useBreakRefs`
            // or 0e lobes get no targets (log targets=0) and π-cleave layout falls through wrong paths.
            if (!useSigmaCleavageRefForVsepr)
            {
                bool sigmaIncreased = sigmaNeighborCountBefore >= 0 && GetDistinctSigmaNeighborCount() > sigmaNeighborCountBefore;
                if (newBondPartner != null || sigmaIncreased || useBreakRefs)
                    AppendEmptyNonbondRedistributeTargetsForSigmaFramework(result, null, sigmaCleavageGuideForTargets, bondBreakIsSigmaCleavageBetweenFormerPartners);
            }
            else if (sigmaCleavageGuideForTargets != null
                && sigmaCleavageGuideForTargets.ElectronCount == 0
                && IsBondBreakGuideOrbitalWithBondingCleared(sigmaCleavageGuideForTargets))
                AppendEmptyNonbondRedistributeTargetsForSigmaFramework(result, null, sigmaCleavageGuideForTargets, bondBreakIsSigmaCleavageBetweenFormerPartners);

            LogGetRedistributeTargets3DLine("EXIT_zeroLone_noRigid", "resultCount=" + result.Count + " (TryMatch+VSEPR path skipped)");
            return result;
        }

        // Pin the bond-break guide only for full σ cleavage (ex-bond lone as reserved domain). π break: same guide orb may exist but σ still links centers — do not σ-cleave pin.
        ElectronOrbitalFunction pin = useSigmaCleavageRefForVsepr && sigmaCleavageGuideForTargets != null && loneOrbitals.Contains(sigmaCleavageGuideForTargets)
            ? sigmaCleavageGuideForTargets
            : null;
        int slotCount = GetVseprSlotCount3D(bondAxes.Count, loneOrbitals.Count);

        List<Vector3> newDirs;
        if (slotCount == 4 && !useSigmaCleavageRefForVsepr && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            newDirs = ChooseTetrahedralNewDirsForFormationMinLoneMotion(refLocal, bondAxes, loneOrbitals, pin, slotCount);
        else
        {
            var idealRaw = VseprLayout.GetIdealLocalDirections(slotCount);
            newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));
        }

        var loneMatch = pin != null ? loneOrbitals.Where(o => o != pin).ToList() : loneOrbitals;

        LogGetRedistributeTargets3DLine(
            "TryMatch_inputs",
            "bondAxesMerged=" + bondAxes.Count + " slotCount=" + slotCount + " occupiedLone=" + loneOrbitals.Count + " loneMatch=" + loneMatch.Count + " pin=" + (pin != null));

        bool tryOk = TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, slotCount, bondAxes, loneOrbitals, newDirs, out var bestMapping, out var pinReservedDir, out _, pin);
        if (!tryOk || bestMapping == null || bestMapping.Count != loneMatch.Count)
        {
            LogGetRedistributeTargets3DLine(
                "EXIT_TryMatch_failed",
                "tryOk=" + tryOk + " mappingN=" + (bestMapping == null ? -1 : bestMapping.Count) + " loneMatchN=" + loneMatch.Count +
                " bondAxesN=" + bondAxes.Count + " slotCount=" + slotCount);
            return result;
        }

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

        int afterLonePin = result.Count;

        // Bond-break always; 3D σ formation too: permute slots to minimize Σ∆(tip) then pick roll closest to each lobe (was break-only → formation got huge spurious lerp).
        bool rematched = false;
        bool recanonicalized = false;
        bool postProcessOccupiedOnly = useSigmaCleavageRefForVsepr || OrbitalAngleUtility.UseFull3DOrbitalGeometry;
        if (postProcessOccupiedOnly && result.Count > 1)
        {
            RematchRedistributeTargetSlotsMinAngularMotion(result, pin);
            rematched = true;
        }
        if (postProcessOccupiedOnly && result.Count > 0)
        {
            recanonicalized = true;
            for (int i = 0; i < result.Count; i++)
            {
                var (o, _, r) = result[i];
                if (o == null || o.transform.parent != transform) continue;
                Vector3 d = (r * Vector3.right).normalized;
                var (p2, r2) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(d, bondRadius, o.transform.localRotation);
                result[i] = (o, p2, r2);
            }
        }

        if (!useSigmaCleavageRefForVsepr && (newBondPartner != null || (sigmaNeighborCountBefore >= 0 && GetDistinctSigmaNeighborCount() > sigmaNeighborCountBefore)))
            AppendEmptyNonbondRedistributeTargetsForSigmaFramework(result, result, sigmaCleavageGuideForTargets, bondBreakIsSigmaCleavageBetweenFormerPartners);

        // Full σ cleavage: TryMatch never maps 0e lobes — still give the ex-bond 0e guide a lone target (pre-existing empties stay fixed).
        if (useSigmaCleavageRefForVsepr
            && sigmaCleavageGuideForTargets != null
            && sigmaCleavageGuideForTargets.ElectronCount == 0
            && IsBondBreakGuideOrbitalWithBondingCleared(sigmaCleavageGuideForTargets))
            AppendEmptyNonbondRedistributeTargetsForSigmaFramework(result, result, sigmaCleavageGuideForTargets, bondBreakIsSigmaCleavageBetweenFormerPartners);

        LogGetRedistributeTargets3DLine(
            "EXIT_TryMatch_pipeline",
            "finalTargets=" + result.Count + " afterLonePin=" + afterLonePin + " appendedEmptyStep=" + (!useSigmaCleavageRefForVsepr && (newBondPartner != null || (sigmaNeighborCountBefore >= 0 && GetDistinctSigmaNeighborCount() > sigmaNeighborCountBefore))) +
            " Rematch=" + rematched + " recanonicalizeLocalRot=" + recanonicalized);
        return result;
    }

    static void OrthonormalizeDirectionPairForJointRedist(Vector3 in0, Vector3 in1, out Vector3 u, out Vector3 v, out Vector3 w)
    {
        u = in0.normalized;
        v = in1 - Vector3.Dot(in1, u) * u;
        if (v.sqrMagnitude < 1e-12f)
        {
            v = Vector3.Cross(u, Vector3.up);
            if (v.sqrMagnitude < 1e-12f) v = Vector3.Cross(u, Vector3.right);
        }
        v = v.normalized;
        w = Vector3.Cross(u, v);
    }

    /// <summary>
    /// Snapshot world pose (position + rotation) of σ-substituent atoms that <see cref="ApplyRedistributeTargets"/> would rigidly move (same fragment / ring-fallback rules).
    /// Rotation is required so nucleus-parented lone lobes (local poses) track the fragment in world space during joint lerp.
    /// </summary>
    public void SnapshotJointFragmentWorldPositionsForTargets(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets,
        Dictionary<AtomFunction, (Vector3 worldPos, Quaternion worldRot)> outInitialWorld)
    {
        if (targets == null || outInitialWorld == null || !OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;
        var seenBondsMove = new HashSet<CovalentBond>();
        foreach (var (orb, pos, rot) in targets)
        {
            if (orb == null || orb.Bond == null) continue;
            if (IsOrbitalExcludedFromJointRigidRedistribute(orb)) continue;
            var cb = orb.Bond;
            if (!cb.IsSigmaBondLine()) continue;
            if (orb.transform.parent != cb.transform) continue;
            if (cb.AtomA != this && cb.AtomB != this) continue;
            if (!seenBondsMove.Add(cb)) continue;

            var partner = cb.AtomA == this ? cb.AtomB : cb.AtomA;
            if (partner == null) continue;

            var fragment = GetAtomsOnSideOfSigmaBond(partner);
            bool ringFallback = PartnerSigmaFragmentOverlapsOtherEdges(partner, fragment);

            if (ringFallback)
            {
                if (!outInitialWorld.ContainsKey(partner))
                    outInitialWorld[partner] = (partner.transform.position, partner.transform.rotation);
            }
            else
            {
                for (int i = 0; i < fragment.Count; i++)
                {
                    var a = fragment[i];
                    if (a != null && !outInitialWorld.ContainsKey(a))
                        outInitialWorld[a] = (a.transform.position, a.transform.rotation);
                }
            }
        }
    }

    /// <summary>
    /// Joint rotation (world) from start lobe tips to target tuple tips — same basis as the first phase of <see cref="ApplyRedistributeTargets"/>.
    /// <paramref name="starts"/> must align with <paramref name="targets"/>; missing entries use zero local pose.
    /// Guide orbitals excluded from fragment motion are still included here so the quaternion fits all VSEPR domains together.
    /// </summary>
    public Quaternion ComputeJointRedistributeRotationWorldFromTargetsAndStarts(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets,
        List<(Vector3 localPos, Quaternion localRot)> starts)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || targets == null || targets.Count == 0)
            return Quaternion.identity;

        var currentTips = new List<Vector3>();
        var desiredTips = new List<Vector3>();

        for (int i = 0; i < targets.Count; i++)
        {
            var (orb, pos, rot) = targets[i];
            var (sp, sr) = (starts != null && i < starts.Count) ? starts[i] : (Vector3.zero, Quaternion.identity);
            if (orb == null) continue;

            if (orb.Bond != null)
            {
                var cb = orb.Bond;
                // σ and π lines both represent bond domains; joint rotation must see π guides (vertex 0) or trigonal fits use only two tips.
                if (orb.transform.parent != cb.transform) continue;
                if (cb.AtomA != this && cb.AtomB != this) continue;
                Transform parent = orb.transform.parent;
                if (parent == null) continue;
                Vector3 cur = parent.TransformDirection((sr * Vector3.right).normalized);
                if (cur.sqrMagnitude < 1e-16f) continue;
                cur.Normalize();
                Vector3 des = GetRedistributeTargetHybridTipWorldFromTuple(orb, rot);
                if (des.sqrMagnitude < 1e-16f) continue;
                des.Normalize();
                bool sigmaLine = cb is CovalentBond cbSigmaRow && cbSigmaRow.IsSigmaBondLine();
                // σ hybrid direction is an axis; +X vs −X are equivalent for undirected lobe alignment. Joint solve used +X only,
                // which can over-rotate fragments when −X is much closer to des (runtime R5: angOppCurToDesDeg ≪ angCurToDesDeg).
                bool sigmaJointUsedOppHemisphere = false;
                if (sigmaLine)
                {
                    float angPlus = Vector3.Angle(cur, des);
                    float angMinus = Vector3.Angle(-cur, des);
                    const float tieEpsJointSigmaHemDeg = 0.05f;
                    // R5: always picking the closer hemisphere flips perm=1,0 (gap~48°: 114 vs 66) and misrotates -O, while
                    // perm=0,1 needs the flip (gap~96°: 138 vs 42). Only use opposite +X for joint fit when hemispheres disagree strongly.
                    const float minDirectedGapDegForSigmaJointOppHem = 70f;
                    if (angMinus < angPlus - tieEpsJointSigmaHemDeg
                        && (angPlus - angMinus) > minDirectedGapDegForSigmaJointOppHem)
                    {
                        cur = -cur;
                        sigmaJointUsedOppHemisphere = true;
                    }
                }
                currentTips.Add(cur);
                desiredTips.Add(des);
                // #region agent log
                if (DebugLogOcoSecondPiNdjson && targets.Count <= 6)
                {
                    float ang = Vector3.Angle(cur, des);
                    var sb = new System.Text.StringBuilder(320);
                    sb.Append("{\"row\":").Append(i);
                    sb.Append(",\"pivotId\":").Append(GetInstanceID());
                    sb.Append(",\"orbId\":").Append(orb.GetInstanceID());
                    sb.Append(",\"bondId\":").Append(orb.Bond.GetInstanceID());
                    sb.Append(",\"bondParent\":true");
                    sb.Append(",\"sigmaLine\":").Append(sigmaLine ? "true" : "false");
                    sb.Append(",\"angCurToDesDeg\":").Append(ang.ToString("F2", CultureInfo.InvariantCulture));
                    if (sigmaLine)
                    {
                        sb.Append(",\"sigmaJointOppHem\":").Append(sigmaJointUsedOppHemisphere ? "true" : "false");
                        float angOppPost = Vector3.Angle(-cur, des);
                        sb.Append(",\"angOppCurToDesDeg\":").Append(angOppPost.ToString("F2", CultureInfo.InvariantCulture));
                    }
                    sb.Append('}');
                    ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                        "R5",
                        "AtomFunction.ComputeJointRedistributeRotationWorldFromTargetsAndStarts",
                        "joint_row_input",
                        sb.ToString());
                }
                // #endregion
            }
            else if (orb.transform.parent == transform)
            {
                Vector3 cur = transform.TransformDirection((sr * Vector3.right).normalized);
                if (cur.sqrMagnitude < 1e-16f) continue;
                cur.Normalize();
                Vector3 des = GetRedistributeTargetHybridTipWorldFromTuple(orb, rot);
                if (des.sqrMagnitude < 1e-16f) continue;
                des.Normalize();
                currentTips.Add(cur);
                desiredTips.Add(des);
                // #region agent log
                if (DebugLogOcoSecondPiNdjson && targets.Count <= 6)
                {
                    float ang = Vector3.Angle(cur, des);
                    var sb = new System.Text.StringBuilder(260);
                    sb.Append("{\"row\":").Append(i);
                    sb.Append(",\"pivotId\":").Append(GetInstanceID());
                    sb.Append(",\"orbId\":").Append(orb.GetInstanceID());
                    sb.Append(",\"bondId\":0");
                    sb.Append(",\"bondParent\":false");
                    sb.Append(",\"sigmaLine\":false");
                    sb.Append(",\"angCurToDesDeg\":").Append(ang.ToString("F2", CultureInfo.InvariantCulture));
                    sb.Append('}');
                    ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                        "R5",
                        "AtomFunction.ComputeJointRedistributeRotationWorldFromTargetsAndStarts",
                        "joint_row_input",
                        sb.ToString());
                }
                // #endregion
            }
        }
        Quaternion q = ComputeJointRedistributeRotationWorld(currentTips, desiredTips);
        // #region agent log
        if (DebugLogOcoSecondPiNdjson && targets.Count <= 6)
        {
            var sb = new System.Text.StringBuilder(180);
            sb.Append("{\"pivotId\":").Append(GetInstanceID());
            sb.Append(",\"rowsUsed\":").Append(currentTips.Count);
            sb.Append(",\"jointDeg\":").Append(Quaternion.Angle(Quaternion.identity, q).ToString("F2", CultureInfo.InvariantCulture));
            sb.Append('}');
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "R5",
                "AtomFunction.ComputeJointRedistributeRotationWorldFromTargetsAndStarts",
                "joint_solution",
                sb.ToString());
        }
        // #endregion
        return q;
    }

    /// <summary>
    /// Rigid motion of σ-substituent fragments for a fraction of the full joint rotation (about <paramref name="pivotStartWorld"/>),
    /// with pivot translation <c>pivotWorldNow minus pivotStartWorld</c> folded in. Applies the same world quaternion to each atom's
    /// rotation so nucleus-child lone lobes stay rigid with the fragment (not just translation).
    /// </summary>
    public void ApplyJointRedistributeFragmentMotionFraction(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets,
        Quaternion deltaWorldFull,
        float fraction,
        Vector3 pivotStartWorld,
        Vector3 pivotWorldNow,
        Dictionary<AtomFunction, (Vector3 worldPos, Quaternion worldRot)> fragmentInitialWorld)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || targets == null || fragmentInitialWorld == null || fragmentInitialWorld.Count == 0)
            return;
        fraction = Mathf.Clamp01(fraction);
        Quaternion r = Quaternion.Slerp(Quaternion.identity, deltaWorldFull, fraction);

        var movedAtoms = new HashSet<AtomFunction>();
        var seenBondsMove = new HashSet<CovalentBond>();
        foreach (var (orb, pos, rot) in targets)
        {
            if (orb == null || orb.Bond == null) continue;
            if (IsOrbitalExcludedFromJointRigidRedistribute(orb)) continue;
            var cb = orb.Bond;
            if (!cb.IsSigmaBondLine()) continue;
            if (orb.transform.parent != cb.transform) continue;
            if (cb.AtomA != this && cb.AtomB != this) continue;
            if (!seenBondsMove.Add(cb)) continue;

            var partner = cb.AtomA == this ? cb.AtomB : cb.AtomA;
            if (partner == null) continue;

            var fragment = GetAtomsOnSideOfSigmaBond(partner);
            bool ringFallback = PartnerSigmaFragmentOverlapsOtherEdges(partner, fragment);

            if (ringFallback)
            {
                if (!movedAtoms.Add(partner)) continue;
                if (fragmentInitialWorld.TryGetValue(partner, out var snap))
                    partner.transform.SetPositionAndRotation(
                        pivotWorldNow + r * (snap.worldPos - pivotStartWorld),
                        r * snap.worldRot);
            }
            else
            {
                for (int fi = 0; fi < fragment.Count; fi++)
                {
                    var a = fragment[fi];
                    if (a == null) continue;
                    if (!movedAtoms.Add(a)) continue;
                    if (fragmentInitialWorld.TryGetValue(a, out var snap))
                        a.transform.SetPositionAndRotation(
                            pivotWorldNow + r * (snap.worldPos - pivotStartWorld),
                            r * snap.worldRot);
                }
            }
        }

        foreach (var atom in movedAtoms)
        {
            if (atom?.CovalentBonds == null) continue;
            for (int bi = 0; bi < atom.CovalentBonds.Count; bi++)
                atom.CovalentBonds[bi]?.UpdateBondTransformToCurrentAtoms();
        }
    }

    /// <summary>
    /// Nucleus-parented orbitals not listed in formation/redistribution <paramref name="targets"/> (e.g. lone empties omitted
    /// from guide-group rows). Snapshot world pose so <see cref="ApplyNucleusSiblingOrbitalsJointRotationFraction"/> can apply
    /// the same joint quaternion as <see cref="ApplyJointRedistributeFragmentMotionFraction"/> to keep the electron shell rigid.
    /// </summary>
    public void SnapshotNucleusParentedSiblingsExcludedFromRedistTargets(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets,
        Dictionary<ElectronOrbitalFunction, (Vector3 worldPos, Quaternion worldRot)> outSnapshot)
    {
        if (outSnapshot == null || !OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;
        outSnapshot.Clear();
        var inTargets = new HashSet<ElectronOrbitalFunction>();
        if (targets != null)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var o = targets[i].orb;
                if (o != null) inTargets.Add(o);
            }
        }
        for (int i = 0; i < bondedOrbitals.Count; i++)
        {
            var orb = bondedOrbitals[i];
            if (orb == null || inTargets.Contains(orb)) continue;
            if (orb.transform.parent != transform) continue;
            outSnapshot[orb] = (orb.transform.position, orb.transform.rotation);
        }
    }

    /// <summary>
    /// Same world-space rigid motion as substituent fragments: pivot translation + <see cref="Quaternion.Slerp"/> of full joint delta.
    /// Only orbitals snapped in <see cref="SnapshotNucleusParentedSiblingsExcludedFromRedistTargets"/> are moved — not redistribution target rows.
    /// </summary>
    public void ApplyNucleusSiblingOrbitalsJointRotationFraction(
        Dictionary<ElectronOrbitalFunction, (Vector3 worldPos, Quaternion worldRot)> siblingSnapshot,
        Quaternion deltaWorldFull,
        float fraction,
        Vector3 pivotStartWorld,
        Vector3 pivotWorldNow)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || siblingSnapshot == null || siblingSnapshot.Count == 0)
            return;
        fraction = Mathf.Clamp01(fraction);
        Quaternion r = Quaternion.Slerp(Quaternion.identity, deltaWorldFull, fraction);
        foreach (var kv in siblingSnapshot)
        {
            var orb = kv.Key;
            if (orb == null || orb.transform.parent != transform) continue;
            var snap = kv.Value;
            orb.transform.SetPositionAndRotation(
                pivotWorldNow + r * (snap.worldPos - pivotStartWorld),
                r * snap.worldRot);
        }
    }

    public static bool TryFindLegacyCarbonylOxygenForCPiBond(
        CovalentBond opPi,
        out AtomFunction centralCarbon,
        out AtomFunction operationOxygen,
        out AtomFunction legacyCarbonylOxygen)
    {
        centralCarbon = null;
        operationOxygen = null;
        legacyCarbonylOxygen = null;
        if (opPi == null || opPi.IsSigmaBondLine()) return false;
        var a = opPi.AtomA;
        var b = opPi.AtomB;
        if (a == null || b == null) return false;
        if (a.AtomicNumber == 6 && b.AtomicNumber == 8)
        {
            centralCarbon = a;
            operationOxygen = b;
        }
        else if (a.AtomicNumber == 8 && b.AtomicNumber == 6)
        {
            centralCarbon = b;
            operationOxygen = a;
        }
        else
            return false;
        AtomFunction found = null;
        var bonds = centralCarbon.CovalentBonds;
        for (int i = 0; i < bonds.Count; i++)
        {
            var cb = bonds[i];
            if (cb == null) continue;
            var other = cb.AtomA == centralCarbon ? cb.AtomB : cb.AtomA;
            if (other == null || other.AtomicNumber != 8) continue;
            if (other == operationOxygen) continue;
            if (found != null && found != other)
                return false;
            found = other;
        }
        if (found == null) return false;
        legacyCarbonylOxygen = found;
        return true;
    }

    /// <summary>
    /// After π snap + hybrid: logs mesh +X in world vs redistribute row local target (meaningful for solver mismatch),
    /// and vs <see cref="OrbitalTipDirectionInNucleusLocal"/> (for nucleus children this matches mesh +X by construction).
    /// Also logs π lobes vs bond <see cref="CovalentBond.GetOrbitalTargetWorldState"/> when parented on the bond.
    /// </summary>
    public static void LogPiOrbitalVisualTipProbeNdjson(
        CovalentBond opPi,
        AtomFunction endpointA,
        AtomFunction endpointB,
        string phase,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redistRowsA = null,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redistRowsB = null,
        ElectronOrbitalFunction sourcePiOrb = null,
        ElectronOrbitalFunction targetPiOrb = null,
        string runId = "run1")
    {
        if (!DebugLogOrbitalVisualTipProbe || opPi == null || opPi.IsSigmaBondLine()) return;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;

        var inv = CultureInfo.InvariantCulture;
        Camera cam = Camera.main;
        AtomFunction legacyO = null;
        if (TryFindLegacyCarbonylOxygenForCPiBond(opPi, out _, out _, out var leg) && leg != null)
            legacyO = leg;

        var rowByOrb = new Dictionary<ElectronOrbitalFunction, (Vector3 pos, Quaternion rot)>();
        void IndexRows(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> rows)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count; i++)
            {
                var o = rows[i].orb;
                if (o != null) rowByOrb[o] = (rows[i].pos, rows[i].rot);
            }
        }
        IndexRows(redistRowsA);
        IndexRows(redistRowsB);

        void AppendProbeLine(
            AtomFunction nucleus,
            ElectronOrbitalFunction orb,
            string nucleusRole,
            string domainRole)
        {
            if (nucleus == null || orb == null) return;
            if (orb.transform.parent != nucleus.transform) return;
            Vector3 tipN = nucleus.OrbitalTipDirectionInNucleusLocal(orb);
            if (tipN.sqrMagnitude < 1e-14f) return;
            tipN.Normalize();
            Vector3 expectW = nucleus.transform.TransformDirection(tipN);
            if (expectW.sqrMagnitude < 1e-14f) return;
            expectW.Normalize();
            Vector3 meshXw = orb.transform.TransformDirection(Vector3.right);
            if (meshXw.sqrMagnitude < 1e-14f) return;
            meshXw.Normalize();
            float angErrDeg = Vector3.Angle(expectW, meshXw);
            bool hasRow = false;
            float angRowToMesh = -1f;
            if (rowByOrb.TryGetValue(orb, out var tup))
            {
                Vector3 rowTipW = nucleus.transform.TransformDirection((tup.rot * Vector3.right).normalized);
                if (rowTipW.sqrMagnitude > 1e-14f)
                {
                    rowTipW.Normalize();
                    hasRow = true;
                    angRowToMesh = Vector3.Angle(rowTipW, meshXw);
                }
            }
            Vector3 originW = orb.transform.position;
            var sb = new System.Text.StringBuilder(520);
            sb.Append("{\"phase\":\"").Append(phase).Append('"');
            sb.Append(",\"nucleusRole\":\"").Append(nucleusRole).Append('"');
            sb.Append(",\"domainRole\":\"").Append(domainRole).Append('"');
            sb.Append(",\"probeKind\":\"nucleusShell\"");
            sb.Append(",\"pivotId\":").Append(nucleus.GetInstanceID());
            sb.Append(",\"orbId\":").Append(orb.GetInstanceID());
            sb.Append(",\"bondId\":").Append(orb.Bond != null ? orb.Bond.GetInstanceID() : 0);
            sb.Append(",\"sigmaLine\":")
                .Append(orb.Bond is CovalentBond cb0 && cb0.IsSigmaBondLine() ? "true" : "false");
            sb.Append(",\"expectWx\":").Append(expectW.x.ToString(inv));
            sb.Append(",\"expectWy\":").Append(expectW.y.ToString(inv));
            sb.Append(",\"expectWz\":").Append(expectW.z.ToString(inv));
            sb.Append(",\"meshXwx\":").Append(meshXw.x.ToString(inv));
            sb.Append(",\"meshXwy\":").Append(meshXw.y.ToString(inv));
            sb.Append(",\"meshXwz\":").Append(meshXw.z.ToString(inv));
            sb.Append(",\"angExpectToMeshXDeg\":").Append(angErrDeg.ToString("F2", inv));
            sb.Append(",\"hasRedistRow\":").Append(hasRow ? "true" : "false");
            sb.Append(",\"angRedistRowToMeshXDeg\":").Append(hasRow ? angRowToMesh.ToString("F2", inv) : "-1");
            if (cam != null)
            {
                var p0 = cam.WorldToScreenPoint(originW);
                var pE = cam.WorldToScreenPoint(originW + expectW * 0.35f);
                var pM = cam.WorldToScreenPoint(originW + meshXw * 0.35f);
                sb.Append(",\"hasCam\":true");
                sb.Append(",\"scrOx\":").Append(p0.x.ToString("F1", inv));
                sb.Append(",\"scrOy\":").Append(p0.y.ToString("F1", inv));
                sb.Append(",\"scrEx\":").Append(pE.x.ToString("F1", inv));
                sb.Append(",\"scrEy\":").Append(pE.y.ToString("F1", inv));
                sb.Append(",\"scrMx\":").Append(pM.x.ToString("F1", inv));
                sb.Append(",\"scrMy\":").Append(pM.y.ToString("F1", inv));
            }
            else
                sb.Append(",\"hasCam\":false");
            sb.Append('}');
            string msg = string.Concat(
                "[orbital-visual-probe] phase=", phase,
                " nucleusRole=", nucleusRole,
                " domainRole=", domainRole,
                " probeKind=nucleusShell",
                " pivotId=", nucleus.GetInstanceID().ToString(inv),
                " orbId=", orb.GetInstanceID().ToString(inv),
                " angRedistRowToMeshXDeg=", hasRow ? angRowToMesh.ToString("F2", inv) : "na",
                " angExpectToMeshXDeg=", angErrDeg.ToString("F2", inv));
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H-visual-tip",
                "AtomFunction.LogPiOrbitalVisualTipProbeNdjson",
                msg,
                sb.ToString(),
                runId);

#if UNITY_EDITOR
            if (DebugLogOrbitalVisualTipProbe)
            {
                const float rayLen = 0.35f;
                Debug.DrawRay(originW, expectW * rayLen, Color.green, 8f, depthTest: false);
                Debug.DrawRay(originW, meshXw * rayLen, Color.magenta, 8f, depthTest: false);
                if (hasRow && angRowToMesh > 0.25f)
                {
                    Vector3 rowTipW = nucleus.transform.TransformDirection((rowByOrb[orb].rot * Vector3.right).normalized);
                    Debug.DrawRay(originW, rowTipW * rayLen, Color.cyan, 8f, depthTest: false);
                }
            }
#endif
        }

        void AppendPiLobeProbe(ElectronOrbitalFunction orb, string lobeRole)
        {
            if (orb == null || orb.Bond != opPi) return;
            Transform p = orb.transform.parent;
            if (p == null) return;
            Vector3 meshXw = orb.transform.TransformDirection(Vector3.right);
            if (meshXw.sqrMagnitude < 1e-14f) return;
            meshXw.Normalize();
            Vector3 originW = orb.transform.position;
            float angBondTargetToMesh = -1f;
            float angRowToMesh = -1f;
            bool hasRow = false;
            bool hasBondTarget = false;
            Vector3 bondWantW = Vector3.zero;

            if (p == opPi.transform)
            {
                var (_, wr) = opPi.GetOrbitalTargetWorldState();
                bondWantW = wr * Vector3.right;
                if (bondWantW.sqrMagnitude > 1e-14f)
                {
                    bondWantW.Normalize();
                    hasBondTarget = true;
                    angBondTargetToMesh = Vector3.Angle(bondWantW, meshXw);
                }
            }

            var nuc = p.GetComponent<AtomFunction>();
            if (nuc != null && rowByOrb.TryGetValue(orb, out var tup))
            {
                Vector3 rowTipW = nuc.transform.TransformDirection((tup.rot * Vector3.right).normalized);
                if (rowTipW.sqrMagnitude > 1e-14f)
                {
                    rowTipW.Normalize();
                    hasRow = true;
                    angRowToMesh = Vector3.Angle(rowTipW, meshXw);
                }
            }

            string parentKind = p == opPi.transform ? "bond" : "nucleus";
            var sb = new System.Text.StringBuilder(520);
            sb.Append("{\"phase\":\"").Append(phase).Append('"');
            sb.Append(",\"probeKind\":\"piLobe\"");
            sb.Append(",\"lobeRole\":\"").Append(lobeRole).Append('"');
            sb.Append(",\"parentKind\":\"").Append(parentKind).Append('"');
            sb.Append(",\"orbId\":").Append(orb.GetInstanceID());
            sb.Append(",\"opPiBondId\":").Append(opPi.GetInstanceID());
            sb.Append(",\"meshXwx\":").Append(meshXw.x.ToString(inv));
            sb.Append(",\"meshXwy\":").Append(meshXw.y.ToString(inv));
            sb.Append(",\"meshXwz\":").Append(meshXw.z.ToString(inv));
            sb.Append(",\"hasBondTarget\":").Append(hasBondTarget ? "true" : "false");
            if (hasBondTarget)
            {
                sb.Append(",\"bondTargetWx\":").Append(bondWantW.x.ToString(inv));
                sb.Append(",\"bondTargetWy\":").Append(bondWantW.y.ToString(inv));
                sb.Append(",\"bondTargetWz\":").Append(bondWantW.z.ToString(inv));
            }
            sb.Append(",\"angBondTargetToMeshXDeg\":").Append(hasBondTarget ? angBondTargetToMesh.ToString("F2", inv) : "-1");
            sb.Append(",\"hasRedistRow\":").Append(hasRow ? "true" : "false");
            sb.Append(",\"angRedistRowToMeshXDeg\":").Append(hasRow ? angRowToMesh.ToString("F2", inv) : "-1");
            if (cam != null)
            {
                var p0 = cam.WorldToScreenPoint(originW);
                var pM = cam.WorldToScreenPoint(originW + meshXw * 0.35f);
                sb.Append(",\"hasCam\":true");
                sb.Append(",\"scrOx\":").Append(p0.x.ToString("F1", inv));
                sb.Append(",\"scrOy\":").Append(p0.y.ToString("F1", inv));
                sb.Append(",\"scrMx\":").Append(pM.x.ToString("F1", inv));
                sb.Append(",\"scrMy\":").Append(pM.y.ToString("F1", inv));
            }
            else
                sb.Append(",\"hasCam\":false");
            sb.Append('}');
            string msg = string.Concat(
                "[orbital-visual-probe] phase=", phase,
                " probeKind=piLobe lobeRole=", lobeRole,
                " parentKind=", parentKind,
                " orbId=", orb.GetInstanceID().ToString(inv),
                " angBondTargetToMeshXDeg=",
                hasBondTarget ? angBondTargetToMesh.ToString("F2", inv) : "na",
                " angRedistRowToMeshXDeg=", hasRow ? angRowToMesh.ToString("F2", inv) : "na");
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H-visual-tip",
                "AtomFunction.LogPiOrbitalVisualTipProbeNdjson",
                msg,
                sb.ToString(),
                runId);

#if UNITY_EDITOR
            if (DebugLogOrbitalVisualTipProbe && hasBondTarget && angBondTargetToMesh > 0.25f)
            {
                Debug.DrawRay(originW, bondWantW * 0.35f, Color.cyan, 8f, depthTest: false);
                Debug.DrawRay(originW, meshXw * 0.35f, Color.magenta, 8f, depthTest: false);
            }
#endif
        }

        void ProbeNucleusEndpoints(AtomFunction nucleus, string nucleusRole)
        {
            if (nucleus == null) return;
            for (int i = 0; i < nucleus.bondedOrbitals.Count; i++)
            {
                var orb = nucleus.bondedOrbitals[i];
                if (orb == null || orb.transform.parent != nucleus.transform) continue;
                string domainRole;
                if (orb.Bond == null) domainRole = "lone";
                else if (orb.Bond == opPi) domainRole = "piOp";
                else if (orb.Bond is CovalentBond cb && cb.IsSigmaBondLine()) domainRole = "sigma";
                else domainRole = "piOther";
                AppendProbeLine(nucleus, orb, nucleusRole, domainRole);
            }
        }

        ProbeNucleusEndpoints(endpointA, "endpointA");
        ProbeNucleusEndpoints(endpointB, "endpointB");
        if (legacyO != null && legacyO != endpointA && legacyO != endpointB)
            ProbeNucleusEndpoints(legacyO, "legacyEqO");

        AppendPiLobeProbe(sourcePiOrb, "sourcePi");
        AppendPiLobeProbe(targetPiOrb, "targetPi");
    }

    /// <summary>
    /// One NDJSON line per atom in the merged connected components of <paramref name="atomA"/> and <paramref name="atomB"/>.
    /// </summary>
    public static void LogMoleculeElectronConfigurationFromAtomUnion(
        AtomFunction atomA,
        AtomFunction atomB,
        string phase,
        int bondEventId,
        CovalentBond opBond,
        string bondKind,
        string runId = "run1")
    {
        if (!DebugLogMoleculeElectronConfigAroundBond) return;
        var atoms = new HashSet<AtomFunction>();
        if (atomA != null) foreach (var x in atomA.GetConnectedMolecule()) atoms.Add(x);
        if (atomB != null) foreach (var x in atomB.GetConnectedMolecule()) atoms.Add(x);
        var list = new List<AtomFunction>(atoms);
        list.Sort((x, y) => x.GetInstanceID().CompareTo(y.GetInstanceID()));
        foreach (var a in list)
            a.EmitMoleculeElectronConfigOneAtomNdjson(phase, bondEventId, opBond, bondKind, runId);
        // #region agent log
        if (DebugLogOxygenOcoLewisDetail)
        {
            foreach (var a in list)
                a.EmitOxygenOcoTerminalNdjson(phase, bondEventId, opBond, bondKind, runId);
        }
        if (DebugLogOxygenMolEcnOrbitalAngles)
        {
            foreach (var a in list)
                a.EmitOxygenMolEcnOrbitalPairwiseAnglesNdjson(phase, bondEventId, opBond, bondKind, runId);
        }
        // #endregion
        EmitMoleculeEcnSnapshotSummaryNdjson(list, phase, bondEventId, opBond, bondKind, runId);
    }

    static void EmitMoleculeEcnSnapshotSummaryNdjson(
        List<AtomFunction> list,
        string phase,
        int bondEventId,
        CovalentBond opBond,
        string bondKind,
        string runId)
    {
        var inv = CultureInfo.InvariantCulture;
        int molNonBond = 0;
        int molBondOwned = 0;
        foreach (var a in list)
        {
            if (a == null) continue;
            a.GetEcnNonBondAndBondOwnedSums(out int nb, out int bo);
            molNonBond += nb;
            molBondOwned += bo;
        }
        int molTotal = molNonBond + molBondOwned;
        var sb = new StringBuilder(220);
        sb.Append("{\"rowKind\":\"molSnapshot\"");
        sb.Append(",\"phase\":\"").Append(phase).Append('"');
        sb.Append(",\"bondEventId\":").Append(bondEventId);
        sb.Append(",\"bondKind\":\"").Append(bondKind).Append('"');
        sb.Append(",\"atomCount\":").Append(list.Count);
        sb.Append(",\"molNonBondE\":").Append(molNonBond);
        sb.Append(",\"molBondElectronsOwnedSum\":").Append(molBondOwned);
        sb.Append(",\"molTotalValenceElectronsAttributed\":").Append(molTotal);
        if (opBond != null)
        {
            sb.Append(",\"opBondId\":").Append(opBond.GetInstanceID());
            sb.Append(",\"opBondIsSigma\":").Append(opBond.IsSigmaBondLine() ? "true" : "false");
        }
        sb.Append('}');
        string msg = string.Concat(
            "[mol-ecn] rowKind=molSnapshot phase=", phase,
            " bondEventId=", bondEventId.ToString(inv),
            " bondKind=", bondKind,
            " molNonBondE=", molNonBond.ToString(inv),
            " molBondOwnedE=", molBondOwned.ToString(inv),
            " molTotalValenceE=", molTotal.ToString(inv));
        ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
            "H-mol-ecn-sum",
            "AtomFunction.EmitMoleculeEcnSnapshotSummaryNdjson",
            msg,
            sb.ToString(),
            runId);
    }

    void GetEcnNonBondAndBondOwnedSums(out int nonBondEOnAtomList, out int electronsOwnedFromIncidentBonds)
    {
        nonBondEOnAtomList = 0;
        for (int i = 0; i < bondedOrbitals.Count; i++)
        {
            var o = bondedOrbitals[i];
            if (o == null || o.Bond != null) continue;
            nonBondEOnAtomList += o.ElectronCount;
        }
        electronsOwnedFromIncidentBonds = 0;
        foreach (var cb in covalentBonds)
        {
            if (cb == null) continue;
            electronsOwnedFromIncidentBonds += cb.GetElectronsOwnedBy(this);
        }
    }

    void EmitMoleculeElectronConfigOneAtomNdjson(string phase, int bondEventId, CovalentBond opBond, string bondKind, string runId)
    {
        var inv = CultureInfo.InvariantCulture;
        int sumE = 0;
        int sumNonBond = 0;
        var sbOrbs = new StringBuilder(128);
        sbOrbs.Append('[');
        bool firstOrb = true;
        for (int i = 0; i < bondedOrbitals.Count; i++)
        {
            var o = bondedOrbitals[i];
            if (o == null) continue;
            sumE += o.ElectronCount;
            bool lone = o.Bond == null;
            if (lone) sumNonBond += o.ElectronCount;
            if (!firstOrb) sbOrbs.Append(',');
            firstOrb = false;
            int bid = o.Bond != null ? o.Bond.GetInstanceID() : 0;
            bool sigma = o.Bond is CovalentBond cb && cb.IsSigmaBondLine();
            bool piB = o.Bond is CovalentBond cb2 && !cb2.IsSigmaBondLine();
            int parentId = o.transform.parent != null ? o.transform.parent.GetInstanceID() : 0;
            sbOrbs.Append("{\"id\":").Append(o.GetInstanceID());
            sbOrbs.Append(",\"e\":").Append(o.ElectronCount);
            sbOrbs.Append(",\"lone\":").Append(lone ? "true" : "false");
            sbOrbs.Append(",\"bondId\":").Append(bid);
            sbOrbs.Append(",\"sigma\":").Append(sigma ? "true" : "false");
            sbOrbs.Append(",\"pi\":").Append(piB ? "true" : "false");
            sbOrbs.Append(",\"parentId\":").Append(parentId);
            sbOrbs.Append('}');
        }
        sbOrbs.Append(']');

        var sbBonds = new StringBuilder(96);
        sbBonds.Append('[');
        bool firstB = true;
        int eOwnedFromBonds = 0;
        foreach (var cb in covalentBonds)
        {
            if (cb == null) continue;
            int ownedThis = cb.GetElectronsOwnedBy(this);
            eOwnedFromBonds += ownedThis;
            var other = cb.AtomA == this ? cb.AtomB : cb.AtomA;
            if (!firstB) sbBonds.Append(',');
            firstB = false;
            int orbE = cb.Orbital != null ? cb.Orbital.ElectronCount : 0;
            int fadeE = cb.OrbitalBeingFadedForCharge != null ? cb.OrbitalBeingFadedForCharge.ElectronCount : 0;
            sbBonds.Append("{\"bondId\":").Append(cb.GetInstanceID());
            sbBonds.Append(",\"partnerId\":").Append(other != null ? other.GetInstanceID() : 0);
            sbBonds.Append(",\"sigma\":").Append(cb.IsSigmaBondLine() ? "true" : "false");
            sbBonds.Append(",\"orbitalE\":").Append(orbE);
            sbBonds.Append(",\"fadedOrbE\":").Append(fadeE);
            sbBonds.Append(",\"eOwnedByThisAtom\":").Append(ownedThis);
            sbBonds.Append('}');
        }
        sbBonds.Append(']');

        int totalValenceAttributed = sumNonBond + eOwnedFromBonds;

        var sb = new StringBuilder(520);
        sb.Append("{\"rowKind\":\"atom\"");
        sb.Append(",\"phase\":\"").Append(phase).Append('"');
        sb.Append(",\"bondEventId\":").Append(bondEventId);
        sb.Append(",\"bondKind\":\"").Append(bondKind).Append('"');
        sb.Append(",\"atomId\":").Append(GetInstanceID());
        sb.Append(",\"Z\":").Append(AtomicNumber);
        sb.Append(",\"charge\":").Append(Charge);
        sb.Append(",\"sigmaN\":").Append(GetDistinctSigmaNeighborCount());
        sb.Append(",\"piN\":").Append(GetPiBondCount());
        sb.Append(",\"bondedOrbitalSlots\":").Append(bondedOrbitals.Count);
        sb.Append(",\"sumOrbitalEOnAtomList\":").Append(sumE);
        sb.Append(",\"nonBondEOnAtomList\":").Append(sumNonBond);
        sb.Append(",\"eOwnedFromIncidentBonds\":").Append(eOwnedFromBonds);
        sb.Append(",\"totalValenceElectronsAttributed\":").Append(totalValenceAttributed);
        sb.Append(",\"orbitals\":").Append(sbOrbs.ToString());
        sb.Append(",\"incidentBonds\":").Append(sbBonds.ToString());
        if (opBond != null)
        {
            sb.Append(",\"opBondId\":").Append(opBond.GetInstanceID());
            sb.Append(",\"opBondIsSigma\":").Append(opBond.IsSigmaBondLine() ? "true" : "false");
        }
        sb.Append('}');

        string msg = string.Concat(
            "[mol-ecn] rowKind=atom phase=", phase,
            " bondEventId=", bondEventId.ToString(inv),
            " bondKind=", bondKind,
            " atomId=", GetInstanceID().ToString(inv),
            " Z=", AtomicNumber.ToString(inv),
            " charge=", Charge.ToString(inv),
            " nonBondE=", sumNonBond.ToString(inv),
            " eOwnedBonds=", eOwnedFromBonds.ToString(inv),
            " totalValenceE=", totalValenceAttributed.ToString(inv),
            " sigmaN=", GetDistinctSigmaNeighborCount().ToString(inv),
            " piN=", GetPiBondCount().ToString(inv));
        ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
            "H-mol-ecn",
            "AtomFunction.EmitMoleculeElectronConfigOneAtomNdjson",
            msg,
            sb.ToString(),
            runId);
    }

    void EmitOxygenOcoTerminalNdjson(string phase, int bondEventId, CovalentBond opBond, string bondKind, string runId)
    {
        if (!DebugLogOxygenOcoLewisDetail || AtomicNumber != 8) return;
        var inv = CultureInfo.InvariantCulture;
        int nLone2 = 0, nLone1 = 0, nLone0 = 0;
        for (int i = 0; i < bondedOrbitals.Count; i++)
        {
            var o = bondedOrbitals[i];
            if (o == null || o.Bond != null) continue;
            int e = o.ElectronCount;
            if (e >= 2) nLone2++;
            else if (e == 1) nLone1++;
            else nLone0++;
        }
        int nSigmaToC = 0, nPiToC = 0;
        int centralCId = 0;
        var carbonIds = new HashSet<int>();
        foreach (var cb in covalentBonds)
        {
            if (cb == null) continue;
            var other = cb.AtomA == this ? cb.AtomB : cb.AtomA;
            if (other == null || other.AtomicNumber != 6) continue;
            carbonIds.Add(other.GetInstanceID());
            if (cb.IsSigmaBondLine()) nSigmaToC++;
            else nPiToC++;
            centralCId = other.GetInstanceID();
        }
        int nCarbonNeighborsDistinct = carbonIds.Count;
        bool opPiTouchesThis = opBond != null && !opBond.IsSigmaBondLine() &&
            (opBond.AtomA == this || opBond.AtomB == this);
        bool lewisOcoNeutralTerminal = nCarbonNeighborsDistinct == 1 && nSigmaToC == 1 && nPiToC == 1 &&
            nLone2 == 2 && nLone1 == 0;
        var sb = new StringBuilder(360);
        sb.Append("{\"rowKind\":\"ocoOTerminal\"");
        sb.Append(",\"phase\":\"").Append(phase).Append('"');
        sb.Append(",\"bondEventId\":").Append(bondEventId);
        sb.Append(",\"bondKind\":\"").Append(bondKind).Append('"');
        sb.Append(",\"atomId\":").Append(GetInstanceID());
        sb.Append(",\"nLone2e\":").Append(nLone2);
        sb.Append(",\"nLone1e\":").Append(nLone1);
        sb.Append(",\"nLone0e\":").Append(nLone0);
        sb.Append(",\"nSigmaToC\":").Append(nSigmaToC);
        sb.Append(",\"nPiToC\":").Append(nPiToC);
        sb.Append(",\"nCarbonNeighborsDistinct\":").Append(nCarbonNeighborsDistinct);
        sb.Append(",\"centralCId\":").Append(nCarbonNeighborsDistinct == 1 ? centralCId : 0);
        sb.Append(",\"opPiTouchesThis\":").Append(opPiTouchesThis ? "true" : "false");
        sb.Append(",\"lewisOcoNeutralTerminal\":").Append(lewisOcoNeutralTerminal ? "true" : "false");
        sb.Append('}');
        string msg = string.Concat(
            "[oco-O-lewis] phase=", phase,
            " bondEventId=", bondEventId.ToString(inv),
            " atomId=", GetInstanceID().ToString(inv),
            " nLone2e=", nLone2.ToString(inv),
            " nLone1e=", nLone1.ToString(inv),
            " sigmaToC=", nSigmaToC.ToString(inv),
            " piToC=", nPiToC.ToString(inv),
            " lewisOco=", lewisOcoNeutralTerminal ? "ok" : "no");
        // #region agent log
        ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
            "H-oco-O-lewis",
            "AtomFunction.EmitOxygenOcoTerminalNdjson",
            msg,
            sb.ToString(),
            runId);
        // #endregion
    }

    void EmitOxygenMolEcnOrbitalPairwiseAnglesNdjson(string phase, int bondEventId, CovalentBond opBond, string bondKind, string runId)
    {
        if (!DebugLogOxygenMolEcnOrbitalAngles || AtomicNumber != 8) return;
        var inv = CultureInfo.InvariantCulture;
        var tips = new List<Vector3>(8);
        var sbTips = new StringBuilder(256);
        sbTips.Append('[');
        bool firstT = true;
        int nSigma = 0, nPi = 0, nLone = 0;
        var seen = new HashSet<ElectronOrbitalFunction>();
        void AppendTip(ElectronOrbitalFunction o)
        {
            if (o == null || !seen.Add(o)) return;
            Vector3 tip = OrbitalTipDirectionInNucleusLocal(o);
            if (tip.sqrMagnitude < 1e-14f) return;
            tip.Normalize();
            tips.Add(tip);
            bool lone = o.Bond == null;
            bool sigma = o.Bond is CovalentBond cbS && cbS.IsSigmaBondLine();
            bool piB = o.Bond is CovalentBond cbP && !cbP.IsSigmaBondLine();
            char kind = lone ? 'L' : (sigma ? 'S' : (piB ? 'P' : '?'));
            if (lone) nLone++;
            else if (sigma) nSigma++;
            else if (piB) nPi++;
            if (!firstT) sbTips.Append(',');
            firstT = false;
            sbTips.Append("{\"id\":").Append(o.GetInstanceID());
            sbTips.Append(",\"kind\":\"").Append(kind).Append('"');
            sbTips.Append(",\"e\":").Append(o.ElectronCount);
            sbTips.Append(",\"parentIsNuc\":").Append(o.transform.parent == transform ? "true" : "false");
            sbTips.Append(",\"tx\":").Append(tip.x.ToString("F4", inv));
            sbTips.Append(",\"ty\":").Append(tip.y.ToString("F4", inv));
            sbTips.Append(",\"tz\":").Append(tip.z.ToString("F4", inv));
            sbTips.Append('}');
        }
        for (int i = 0; i < bondedOrbitals.Count; i++)
            AppendTip(bondedOrbitals[i]);
        foreach (var cb in covalentBonds)
        {
            if (cb == null) continue;
            if (cb.AtomA != this && cb.AtomB != this) continue;
            AppendTip(cb.Orbital);
        }
        sbTips.Append(']');
        int nTips = tips.Count;
        if (nTips < 2) return;
        TetraDomainPairwiseAngleStats(tips, out float pMin, out float pMax, out float pMean, out float pSpread, out float meanDev109);
        float maxDev109 = 0f, maxDev120 = 0f;
        for (int a = 0; a < nTips; a++)
        {
            for (int b = a + 1; b < nTips; b++)
            {
                float ang = Vector3.Angle(tips[a], tips[b]);
                maxDev109 = Mathf.Max(maxDev109, Mathf.Abs(ang - TetrahedralInterdomainAngleDeg));
                maxDev120 = Mathf.Max(maxDev120, Mathf.Abs(ang - 120f));
            }
        }
        var sbPairs = new StringBuilder(120);
        sbPairs.Append('[');
        bool firstP = true;
        for (int a = 0; a < nTips; a++)
        {
            for (int b = a + 1; b < nTips; b++)
            {
                if (!firstP) sbPairs.Append(',');
                firstP = false;
                float ang = Vector3.Angle(tips[a], tips[b]);
                sbPairs.Append("{\"i\":").Append(a).Append(",\"j\":").Append(b);
                sbPairs.Append(",\"deg\":").Append(ang.ToString("F2", inv)).Append('}');
            }
        }
        sbPairs.Append(']');
        var sb = new StringBuilder(420);
        sb.Append("{\"rowKind\":\"ocoOAngleSnap\"");
        sb.Append(",\"phase\":\"").Append(phase).Append('"');
        sb.Append(",\"bondEventId\":").Append(bondEventId);
        sb.Append(",\"bondKind\":\"").Append(bondKind).Append('"');
        sb.Append(",\"atomId\":").Append(GetInstanceID());
        sb.Append(",\"nTips\":").Append(nTips);
        sb.Append(",\"nLoneTips\":").Append(nLone);
        sb.Append(",\"nSigmaTips\":").Append(nSigma);
        sb.Append(",\"nPiTips\":").Append(nPi);
        sb.Append(",\"pairMinDeg\":").Append(pMin.ToString("F2", inv));
        sb.Append(",\"pairMaxDeg\":").Append(pMax.ToString("F2", inv));
        sb.Append(",\"pairMeanDeg\":").Append(pMean.ToString("F2", inv));
        sb.Append(",\"pairSpreadDeg\":").Append(pSpread.ToString("F2", inv));
        sb.Append(",\"meanPairDevFrom109p5Deg\":").Append(meanDev109.ToString("F2", inv));
        sb.Append(",\"maxPairDevFrom109p5Deg\":").Append(maxDev109.ToString("F2", inv));
        sb.Append(",\"maxPairDevFrom120Deg\":").Append(maxDev120.ToString("F2", inv));
        sb.Append(",\"tips\":").Append(sbTips.ToString());
        sb.Append(",\"pairwiseDeg\":").Append(sbPairs.ToString());
        sb.Append(",\"full3D\":").Append(OrbitalAngleUtility.UseFull3DOrbitalGeometry ? "true" : "false");
        sb.Append('}');
        string msg = string.Concat(
            "[OCO-O-angle] phase=", phase,
            " bondEventId=", bondEventId.ToString(inv),
            " atomId=", GetInstanceID().ToString(inv),
            " nTips=", nTips.ToString(inv),
            " pairMinDeg=", pMin.ToString("F2", inv),
            " pairMaxDeg=", pMax.ToString("F2", inv),
            " maxDev109p5=", maxDev109.ToString("F2", inv),
            " maxDev120=", maxDev120.ToString("F2", inv));
        // #region agent log
        ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
            "H-O-angle-snap",
            "AtomFunction.EmitOxygenMolEcnOrbitalPairwiseAnglesNdjson",
            msg,
            sb.ToString(),
            runId);
        // #endregion
    }

    /// <summary>
    /// Debug: for oxygen, log all nucleus-parented lobe +X directions (nucleus local) and pairwise angles.
    /// Highlights orbitals <b>not</b> in <paramref name="redistRows"/> and not on the operation π bond (off-op lone pairs that still get sibling joint motion).
    /// </summary>
    public void LogOxygenOffRedistShellDiagnostics(
        string phase,
        string hypothesisId,
        CovalentBond operationPiBond,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> redistRows,
        float smoothS,
        Quaternion deltaJointFull,
        int siblingSnapshotCount)
    {
        if (!DebugLogOcoOffOpNucleusLoneAngles || !OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;
        if (atomicNumber != 8) return;
        var inv = CultureInfo.InvariantCulture;
        int opId = operationPiBond != null ? operationPiBond.GetInstanceID() : 0;
        var inRedist = new HashSet<ElectronOrbitalFunction>();
        if (redistRows != null)
        {
            for (int i = 0; i < redistRows.Count; i++)
            {
                var o = redistRows[i].orb;
                if (o != null && o.transform.parent == transform) inRedist.Add(o);
            }
        }
        var entries = new List<(int id, bool inRedist, bool offOp, bool isLone, bool isSigma, int bondId, Vector3 tipN)>();
        for (int i = 0; i < bondedOrbitals.Count; i++)
        {
            var o = bondedOrbitals[i];
            if (o == null || o.transform.parent != transform) continue;
            Vector3 tip = OrbitalTipDirectionInNucleusLocal(o);
            if (tip.sqrMagnitude < 1e-14f) continue;
            tip.Normalize();
            bool ir = inRedist.Contains(o);
            bool isSig = o.Bond is CovalentBond cb0 && cb0.IsSigmaBondLine();
            bool onOpPi = operationPiBond != null && !operationPiBond.IsSigmaBondLine() && o.Bond == operationPiBond;
            bool offOp = !onOpPi;
            int bid = o.Bond != null ? o.Bond.GetInstanceID() : 0;
            bool isLone = o.Bond == null;
            entries.Add((o.GetInstanceID(), ir, offOp, isLone, isSig, bid, tip));
        }
        if (entries.Count < 2) return;
        float pairMin = 999f, pairMax = -1f, maxDev120 = -1f;
        for (int a = 0; a < entries.Count; a++)
        {
            for (int b = a + 1; b < entries.Count; b++)
            {
                float ang = Vector3.Angle(entries[a].tipN, entries[b].tipN);
                if (ang < pairMin) pairMin = ang;
                if (ang > pairMax) pairMax = ang;
                float dev = Mathf.Abs(ang - 120f);
                if (dev > maxDev120) maxDev120 = dev;
            }
        }
        float offOpLoneToRedistMinDeg = 999f;
        for (int i = 0; i < entries.Count; i++)
        {
            if (!entries[i].offOp || !entries[i].isLone) continue;
            for (int j = 0; j < entries.Count; j++)
            {
                if (!entries[j].inRedist) continue;
                float ang = Vector3.Angle(entries[i].tipN, entries[j].tipN);
                if (ang < offOpLoneToRedistMinDeg) offOpLoneToRedistMinDeg = ang;
            }
        }
        if (offOpLoneToRedistMinDeg > 998f) offOpLoneToRedistMinDeg = -1f;
        int opPiOrbIdForTriad = 0;
        Vector3 opPiTipNucleus = default;
        bool haveOpPiTipNucleus = false;
        float triadMinDeg = -1f;
        float triadMaxDeg = -1f;
        float triadMaxDevFrom120Deg = -1f;
        if (operationPiBond != null && !operationPiBond.IsSigmaBondLine()
            && operationPiBond.Orbital != null
            && (operationPiBond.AtomA == this || operationPiBond.AtomB == this))
        {
            Vector3 tPi = OrbitalTipDirectionInNucleusLocal(operationPiBond.Orbital);
            if (tPi.sqrMagnitude > 1e-14f)
            {
                tPi.Normalize();
                opPiTipNucleus = tPi;
                haveOpPiTipNucleus = true;
                opPiOrbIdForTriad = operationPiBond.Orbital.GetInstanceID();
            }
        }
        Vector3 loneTipA = default, loneTipB = default;
        int nLoneTipsForTriad = 0;
        for (int li = 0; li < entries.Count && nLoneTipsForTriad < 2; li++)
        {
            if (!entries[li].isLone) continue;
            if (nLoneTipsForTriad == 0) loneTipA = entries[li].tipN;
            else loneTipB = entries[li].tipN;
            nLoneTipsForTriad++;
        }
        if (haveOpPiTipNucleus && nLoneTipsForTriad >= 2)
        {
            triadMinDeg = 999f;
            triadMaxDeg = -1f;
            triadMaxDevFrom120Deg = -1f;
            var tri = new[] { loneTipA, loneTipB, opPiTipNucleus };
            for (int ta = 0; ta < 3; ta++)
            {
                for (int tb = ta + 1; tb < 3; tb++)
                {
                    float ang = Vector3.Angle(tri[ta], tri[tb]);
                    if (ang < triadMinDeg) triadMinDeg = ang;
                    if (ang > triadMaxDeg) triadMaxDeg = ang;
                    float dev = Mathf.Abs(ang - 120f);
                    if (dev > triadMaxDevFrom120Deg) triadMaxDevFrom120Deg = dev;
                }
            }
        }
        var angVsRedistTargetByOrbId = new Dictionary<int, float>();
        if (redistRows != null)
        {
            for (int ri = 0; ri < redistRows.Count; ri++)
            {
                var (oR, _, rotT) = redistRows[ri];
                if (oR == null || oR.transform.parent != transform) continue;
                Vector3 tipNow = OrbitalTipDirectionInNucleusLocal(oR);
                if (tipNow.sqrMagnitude < 1e-14f) continue;
                tipNow.Normalize();
                Vector3 tipTgt = OrbitalSlotPlusXInNucleusLocal(oR, rotT);
                if (tipTgt.sqrMagnitude < 1e-14f) continue;
                tipTgt.Normalize();
                angVsRedistTargetByOrbId[oR.GetInstanceID()] = Vector3.Angle(tipNow, tipTgt);
            }
        }
        float jointDeg = Quaternion.Angle(Quaternion.identity, deltaJointFull);
        var sbData = new System.Text.StringBuilder(400);
        sbData.Append("{\"hypothesisId\":\"").Append(hypothesisId).Append('"');
        sbData.Append(",\"phase\":\"").Append(phase).Append('"');
        sbData.Append(",\"pivotId\":").Append(GetInstanceID());
        sbData.Append(",\"frame\":").Append(Time.frameCount);
        sbData.Append(",\"opBondId\":").Append(opId);
        sbData.Append(",\"smoothS\":").Append(smoothS.ToString("F3", inv));
        sbData.Append(",\"jointDeg\":").Append(jointDeg.ToString("F2", inv));
        sbData.Append(",\"siblingSnapN\":").Append(siblingSnapshotCount);
        sbData.Append(",\"pairMinDeg\":").Append(pairMin.ToString("F2", inv));
        sbData.Append(",\"pairMaxDeg\":").Append(pairMax.ToString("F2", inv));
        sbData.Append(",\"maxDevFrom120Deg\":").Append(maxDev120.ToString("F2", inv));
        sbData.Append(",\"offOpLoneToRedistMinDeg\":").Append(offOpLoneToRedistMinDeg.ToString("F2", inv));
        sbData.Append(",\"opPiOrbId\":").Append(opPiOrbIdForTriad);
        if (haveOpPiTipNucleus && nLoneTipsForTriad >= 2)
        {
            sbData.Append(",\"triadMinDeg\":").Append(triadMinDeg.ToString("F2", inv));
            sbData.Append(",\"triadMaxDeg\":").Append(triadMaxDeg.ToString("F2", inv));
            sbData.Append(",\"triadMaxDevFrom120Deg\":").Append(triadMaxDevFrom120Deg.ToString("F2", inv));
        }
        else
        {
            sbData.Append(",\"triadMinDeg\":null,\"triadMaxDeg\":null,\"triadMaxDevFrom120Deg\":null");
        }
        sbData.Append(",\"tips\":[");
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sbData.Append(',');
            var e = entries[i];
            sbData.Append("{\"id\":").Append(e.id);
            sbData.Append(",\"inRedist\":").Append(e.inRedist ? "true" : "false");
            sbData.Append(",\"offOp\":").Append(e.offOp ? "true" : "false");
            sbData.Append(",\"lone\":").Append(e.isLone ? "true" : "false");
            sbData.Append(",\"sigma\":").Append(e.isSigma ? "true" : "false");
            sbData.Append(",\"bondId\":").Append(e.bondId);
            sbData.Append(",\"tx\":").Append(e.tipN.x.ToString("F4", inv));
            sbData.Append(",\"ty\":").Append(e.tipN.y.ToString("F4", inv));
            sbData.Append(",\"tz\":").Append(e.tipN.z.ToString("F4", inv));
            if (angVsRedistTargetByOrbId.TryGetValue(e.id, out float angV))
                sbData.Append(",\"angVsRedistTargetDeg\":").Append(angV.ToString("F2", inv));
            else
                sbData.Append(",\"angVsRedistTargetDeg\":null");
            sbData.Append('}');
        }
        sbData.Append("]}");
        string triadSuffix = "";
        if (haveOpPiTipNucleus && nLoneTipsForTriad >= 2)
            triadSuffix = " opPiOrbId=" + opPiOrbIdForTriad
                + " triadMinDeg=" + triadMinDeg.ToString("F2", inv) + " triadMaxDeg=" + triadMaxDeg.ToString("F2", inv)
                + " triadMaxDevFrom120Deg=" + triadMaxDevFrom120Deg.ToString("F2", inv);
        string oneLine = "[oco-offop-lone] phase=" + phase + " hypothesisId=" + hypothesisId + " pivotId=" + GetInstanceID()
            + " opBondId=" + opId + " frame=" + Time.frameCount + " smoothS=" + smoothS.ToString("F3", inv)
            + " jointDeg=" + jointDeg.ToString("F2", inv) + " siblingSnapN=" + siblingSnapshotCount
            + " nTips=" + entries.Count + " pairMinDeg=" + pairMin.ToString("F2", inv) + " pairMaxDeg=" + pairMax.ToString("F2", inv)
            + " maxDevFrom120Deg=" + maxDev120.ToString("F2", inv)
            + " offOpLoneToRedistMinDeg=" + (offOpLoneToRedistMinDeg >= 0f ? offOpLoneToRedistMinDeg.ToString("F2", inv) : "na")
            + triadSuffix;
        Debug.Log(oneLine);
        if (DebugLogOcoSecondPiNdjson)
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                hypothesisId,
                "AtomFunction.LogOxygenOffRedistShellDiagnostics",
                oneLine,
                sbData.ToString());
    }

    public static string BuildJointFragSigmaPartnerIdSummary(
        AtomFunction pivot,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets)
    {
        if (pivot == null || targets == null || targets.Count == 0) return "partnerIds=none";
        var ids = new List<int>(6);
        var seenBonds = new HashSet<CovalentBond>();
        for (int i = 0; i < targets.Count; i++)
        {
            var orb = targets[i].orb;
            if (orb?.Bond == null) continue;
            if (pivot.IsOrbitalExcludedFromJointRigidRedistribute(orb)) continue;
            var cb = orb.Bond;
            if (!cb.IsSigmaBondLine() || orb.transform.parent != cb.transform) continue;
            if (cb.AtomA != pivot && cb.AtomB != pivot) continue;
            if (!seenBonds.Add(cb)) continue;
            var partner = cb.AtomA == pivot ? cb.AtomB : cb.AtomA;
            if (partner != null) ids.Add(partner.GetInstanceID());
        }
        return ids.Count == 0 ? "partnerIds=none" : "partnerIds=" + string.Join(",", ids);
    }

    /// <summary>
    /// One line: fragment centroid shift and max per-atom displacement vs snapshot, pivot motion, joint angle. phase=frame gated by <see cref="DebugLogJointFragRedistEveryFrame"/>; other phases by <see cref="DebugLogJointFragRedistMilestones"/>.
    /// </summary>
    public void LogJointFragRedistLine(
        string phase,
        string context,
        IReadOnlyDictionary<AtomFunction, (Vector3 worldPos, Quaternion worldRot)> initialWorld,
        Vector3 pivotStartWorld,
        Vector3 pivotNowWorld,
        float smoothSOrNegOne,
        Quaternion deltaFull,
        string partnerIdSummary)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;
        if (phase == "frame")
        {
            if (!DebugLogJointFragRedistEveryFrame) return;
        }
        else if (!DebugLogJointFragRedistMilestones) return;

        int fragN = initialWorld != null ? initialWorld.Count : 0;
        float centroidShift = 0f;
        float maxDispFromSnap = 0f;
        if (fragN > 0 && initialWorld != null)
        {
            Vector3 sum0 = Vector3.zero;
            Vector3 sumNow = Vector3.zero;
            foreach (var kv in initialWorld)
            {
                if (kv.Key == null) continue;
                sum0 += kv.Value.worldPos;
                sumNow += kv.Key.transform.position;
                maxDispFromSnap = Mathf.Max(maxDispFromSnap, Vector3.Distance(kv.Key.transform.position, kv.Value.worldPos));
            }
            centroidShift = Vector3.Distance(sum0 / fragN, sumNow / fragN);
        }

        float jointDeg = Quaternion.Angle(Quaternion.identity, deltaFull);
        string sStr = smoothSOrNegOne < 0f ? "na" : smoothSOrNegOne.ToString("F3");
        string msg =
            "[joint-frag-redist] phase=" + phase + " context=" + context + " pivotId=" + GetInstanceID() +
            " smoothS=" + sStr + " jointFullDeg=" + jointDeg.ToString("F2") + " fragN=" + fragN +
            " centroidShiftFromSnap=" + centroidShift.ToString("F4") + " maxDispFromSnap=" + maxDispFromSnap.ToString("F4") +
            " pivotDrift=" + Vector3.Distance(pivotStartWorld, pivotNowWorld).ToString("F5") + " " + partnerIdSummary;
        Debug.Log(msg);
        ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
    }

    /// <summary>
    /// World rotation from paired hybrid tips: first two non-parallel directions give orthonormal “from”/“to” bases; falls back to <see cref="Quaternion.FromToRotation"/> when degenerate.
    /// </summary>
    static Quaternion ComputeJointRedistributeRotationWorld(List<Vector3> fromDirs, List<Vector3> toDirs)
    {
        int n = fromDirs != null ? fromDirs.Count : 0;
        if (n == 0 || toDirs == null || toDirs.Count != n) return Quaternion.identity;
        if (n == 1)
            return Quaternion.FromToRotation(fromDirs[0].normalized, toDirs[0].normalized);

        Vector3 f0 = fromDirs[0].normalized;
        Vector3 t0 = toDirs[0].normalized;
        Vector3 f1 = fromDirs[1].normalized;
        Vector3 t1 = toDirs[1].normalized;
        int fi = 1;
        while (fi < n && Mathf.Abs(Vector3.Dot(f0, f1)) > 0.99f)
        {
            fi++;
            if (fi >= n)
                break;
            f1 = fromDirs[fi].normalized;
            t1 = toDirs[fi].normalized;
        }
        if (fi >= n || Mathf.Abs(Vector3.Dot(f0, f1)) > 0.99f)
            return Quaternion.FromToRotation(f0, t0);

        OrthonormalizeDirectionPairForJointRedist(f0, f1, out var cu, out var cv, out var cw);
        OrthonormalizeDirectionPairForJointRedist(t0, t1, out var tu, out var tv, out var tw);

        var c = Matrix4x4.identity;
        c.SetColumn(0, new Vector4(cu.x, cu.y, cu.z, 0f));
        c.SetColumn(1, new Vector4(cv.x, cv.y, cv.z, 0f));
        c.SetColumn(2, new Vector4(cw.x, cw.y, cw.z, 0f));
        var t = Matrix4x4.identity;
        t.SetColumn(0, new Vector4(tu.x, tu.y, tu.z, 0f));
        t.SetColumn(1, new Vector4(tv.x, tv.y, tv.z, 0f));
        t.SetColumn(2, new Vector4(tw.x, tw.y, tw.z, 0f));
        var rm = t * c.transpose;
        return rm.rotation;
    }


    /// <summary>
    /// Applies redistribute target tuples from <see cref="GetRedistributeTargets3D"/>: nucleus-parented orbitals get <c>localPosition</c>/<c>localRotation</c>; σ orbitals on <see cref="CovalentBond"/> (3D) use pivot = this nucleus, one joint rigid rotation for all partner-side fragments (<see cref="GetAtomsOnSideOfSigmaBond"/> / ring-fallback unchanged), bond updates, and <see cref="CovalentBond.ApplySigmaOrbitalTipFromRedistribution"/>.
    /// Joint rotation uses current-to-desired hybrid tips for every nucleus- and bond-parented domain in <paramref name="targets"/> (including guide σ excluded only from fragment motion) so substituents are not moved one σ at a time. Target <c>rot</c> for bond σ is parent-local; world hybrid tip = <c>orb.transform.parent.TransformDirection(rot * Vector3.right)</c>.
    /// Each atom that runs <see cref="RedistributeOrbitals"/> applies σ targets for bonds incident on this center; <see cref="CovalentBond.AuthoritativeAtomForOrbitalRedistributionPose"/> is not used to skip this pass (shared σ pose still converges via <see cref="CovalentBond.ApplySigmaOrbitalTipFromRedistribution"/> when both endpoints run).
    /// <param name="skipJointRigidFragmentMotion">When true, skips the σ-substituent rigid pivot rotation (orbitals still snap; σ hybrid sync still runs). Use after a lerp already applied the same joint motion to fragments.</param>
    /// </summary>
    public void ApplyRedistributeTargets(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets, bool skipJointRigidFragmentMotion = false)
    {
        if (targets == null || targets.Count == 0) return;

        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            foreach (var (orb, pos, rot) in targets)
            {
                if (orb != null && orb.transform.parent == transform)
                {
                    orb.transform.localPosition = pos;
                    orb.transform.localRotation = rot;
                }
            }
            return;
        }

        try
        {
        Quaternion jointDeltaRigidDiag = Quaternion.identity;
        if (!skipJointRigidFragmentMotion)
        {
            var currentTips = new List<Vector3>();
            var desiredTips = new List<Vector3>();
            Vector3 pivotWorld = transform.position;
            int jointSigmaKept = 0, jointSigmaExcl = 0, jointNucleusOrbs = 0;

            foreach (var (orb, pos, rot) in targets)
            {
                if (orb == null) continue;
                if (orb.Bond != null)
                {
                    var cb = orb.Bond;
                    if (orb.transform.parent != cb.transform) continue;
                    if (cb.AtomA != this && cb.AtomB != this) continue;
                    Vector3 cur = transform.TransformDirection(OrbitalTipDirectionInNucleusLocal(orb));
                    if (cur.sqrMagnitude < 1e-16f) continue;
                    cur.Normalize();
                    Vector3 des = GetRedistributeTargetHybridTipWorldFromTuple(orb, rot);
                    if (des.sqrMagnitude < 1e-16f) continue;
                    des.Normalize();
                    bool exclSigma = IsOrbitalExcludedFromJointRigidRedistribute(orb);
                    if (DebugLogRedistribute3DTipGapTrace)
                    {
                        string msg =
                            "[redist3d-tip-trace] phase=jointInput pivotId=" + GetInstanceID() + " kind=sigma" +
                            " bondId=" + cb.GetInstanceID() + " orbId=" + orb.GetInstanceID() +
                            " excludedFromJointRigid=" + exclSigma +
                            " angleDeg=" + Vector3.Angle(cur, des).ToString("F2");
                        Debug.Log(msg);
                        ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
                    }
                    // σ orbitals excluded from fragment rigid motion must not drive the joint quaternion:
                    // their partner subtree is not rotated; pose is reconciled later via ApplySigma only.
                    if (exclSigma)
                    {
                        jointSigmaExcl++;
                        continue;
                    }
                    jointSigmaKept++;
                    currentTips.Add(cur);
                    desiredTips.Add(des);
                }
                else if (orb.transform.parent == transform)
                {
                    Vector3 cur = transform.TransformDirection(OrbitalTipLocalDirection(orb));
                    if (cur.sqrMagnitude < 1e-16f) continue;
                    cur.Normalize();
                    Vector3 des = GetRedistributeTargetHybridTipWorldFromTuple(orb, rot);
                    if (des.sqrMagnitude < 1e-16f) continue;
                    des.Normalize();
                    jointNucleusOrbs++;
                    if (DebugLogRedistribute3DTipGapTrace)
                    {
                        string msg =
                            "[redist3d-tip-trace] phase=jointInput pivotId=" + GetInstanceID() + " kind=nucleusOrb" +
                            " orbId=" + orb.GetInstanceID() +
                            " angleDeg=" + Vector3.Angle(cur, des).ToString("F2");
                        Debug.Log(msg);
                        ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
                    }
                    currentTips.Add(cur);
                    desiredTips.Add(des);
                }
            }

            Quaternion deltaWorld = ComputeJointRedistributeRotationWorld(currentTips, desiredTips);
            jointDeltaRigidDiag = deltaWorld;
            // #region agent log
            int frozenNApply = orbitalsExcludedFromJointRigidInApplyRedistributeTargets?.Count ?? 0;
            if (DebugLogOcoSecondPiNdjson
                && (AtomicNumber == 6 || AtomicNumber == 8)
                && (frozenNApply > 0 || jointSigmaExcl > 0))
            {
                float jAng = Quaternion.Angle(Quaternion.identity, deltaWorld);
                float f01Par = -1f;
                if (currentTips.Count >= 2)
                    f01Par = Mathf.Abs(Vector3.Dot(currentTips[0].normalized, currentTips[1].normalized));
                ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                    "H6",
                    "AtomFunction.ApplyRedistributeTargets",
                    "joint_tip_inventory_pi_redist",
                    "{\"pivotId\":" + GetInstanceID()
                    + ",\"Z\":" + AtomicNumber
                    + ",\"tipN\":" + currentTips.Count
                    + ",\"sigmaKept\":" + jointSigmaKept.ToString(CultureInfo.InvariantCulture)
                    + ",\"sigmaExcl\":" + jointSigmaExcl.ToString(CultureInfo.InvariantCulture)
                    + ",\"nucleusOrbs\":" + jointNucleusOrbs.ToString(CultureInfo.InvariantCulture)
                    + ",\"jointRigidFrozenN\":" + frozenNApply.ToString(CultureInfo.InvariantCulture)
                    + ",\"jointDeg\":" + jAng.ToString("F2", CultureInfo.InvariantCulture)
                    + ",\"absDotTip01\":" + f01Par.ToString("F4", CultureInfo.InvariantCulture) + "}");
            }
            // #endregion
            if (DebugLogRedistribute3DTipGapTrace)
            {
                string msg =
                    "[redist3d-tip-trace] phase=jointMotionComputed pivotId=" + GetInstanceID() +
                    " tipPairCount=" + currentTips.Count +
                    " jointAngleDeg=" + Quaternion.Angle(Quaternion.identity, deltaWorld).ToString("F2");
                Debug.Log(msg);
                ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
            }

            var movedAtoms = new HashSet<AtomFunction>();
            var seenBondsMove = new HashSet<CovalentBond>();
            foreach (var (orb, pos, rot) in targets)
            {
                if (orb == null || orb.Bond == null) continue;
                if (IsOrbitalExcludedFromJointRigidRedistribute(orb)) continue;
                var cb = orb.Bond;
                if (!cb.IsSigmaBondLine()) continue;
                if (orb.transform.parent != cb.transform) continue;
                if (cb.AtomA != this && cb.AtomB != this) continue;
                if (!seenBondsMove.Add(cb)) continue;

                var partner = cb.AtomA == this ? cb.AtomB : cb.AtomA;
                if (partner == null) continue;

                var fragment = GetAtomsOnSideOfSigmaBond(partner);
                bool ringFallback = PartnerSigmaFragmentOverlapsOtherEdges(partner, fragment);

                if (ringFallback)
                {
                    if (movedAtoms.Add(partner))
                    {
                        Vector3 p0 = partner.transform.position;
                        Quaternion q0 = partner.transform.rotation;
                        partner.transform.SetPositionAndRotation(
                            pivotWorld + deltaWorld * (p0 - pivotWorld),
                            deltaWorld * q0);
                    }
                }
                else
                {
                    for (int i = 0; i < fragment.Count; i++)
                    {
                        var a = fragment[i];
                        if (a == null) continue;
                        if (movedAtoms.Add(a))
                        {
                            Vector3 p0 = a.transform.position;
                            Quaternion q0 = a.transform.rotation;
                            a.transform.SetPositionAndRotation(
                                pivotWorld + deltaWorld * (p0 - pivotWorld),
                                deltaWorld * q0);
                        }
                    }
                }
            }

            foreach (var atom in movedAtoms)
            {
                if (atom?.CovalentBonds == null) continue;
                for (int bi = 0; bi < atom.CovalentBonds.Count; bi++)
                    atom.CovalentBonds[bi]?.UpdateBondTransformToCurrentAtoms();
            }
        }

        foreach (var (orb, pos, rot) in targets)
        {
            if (orb == null) continue;
            bool onThisNucleus = orb.transform.parent == transform;
            bool piSharedOnPartnerNucleus = false;
            if (!onThisNucleus && orb.Bond is CovalentBond cbPiApply && !cbPiApply.IsSigmaBondLine()
                && (cbPiApply.AtomA == this || cbPiApply.AtomB == this))
            {
                var partnerApply = cbPiApply.AtomA == this ? cbPiApply.AtomB : cbPiApply.AtomA;
                if (partnerApply != null && orb.transform.parent == partnerApply.transform)
                    piSharedOnPartnerNucleus = true;
            }
            if (onThisNucleus || piSharedOnPartnerNucleus)
            {
                orb.transform.localPosition = pos;
                orb.transform.localRotation = rot;
            }
        }

        // #region agent log
        // Runtime proof: terminal sp² O can have 3 occupied nucleus lone lobes but occ collapsed to 2 → third lobe never in targets → visual shell not coplanar 120°.
        if (DebugLogOcoSecondPiNdjson && AtomicNumber == 8 && GetDistinctSigmaNeighborCount() == 1)
        {
            var inTargetIds = new HashSet<int>();
            foreach (var t in targets)
                if (t.orb != null) inTargetIds.Add(t.orb.GetInstanceID());
            int loneOccNuc = 0;
            var orphanIds = new List<int>(3);
            foreach (var o in bondedOrbitals)
            {
                if (o == null || o.Bond != null || o.ElectronCount <= 0 || o.transform.parent != transform) continue;
                loneOccNuc++;
                if (!inTargetIds.Contains(o.GetInstanceID())) orphanIds.Add(o.GetInstanceID());
            }
            if (orphanIds.Count > 0)
            {
                var sb = new System.Text.StringBuilder(160);
                sb.Append("{\"pivotId\":").Append(GetInstanceID());
                sb.Append(",\"targetN\":").Append(targets.Count);
                sb.Append(",\"loneOccOnNucleus\":").Append(loneOccNuc);
                sb.Append(",\"orphanN\":").Append(orphanIds.Count);
                sb.Append(",\"orphanIds\":[");
                for (int oi = 0; oi < orphanIds.Count; oi++)
                {
                    if (oi > 0) sb.Append(',');
                    sb.Append(orphanIds[oi]);
                }
                sb.Append("]}");
                ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                    "H_orphanLone",
                    "AtomFunction.ApplyRedistributeTargets",
                    "terminal_O_nucleus_lone_not_in_redist_targets",
                    sb.ToString());
            }
        }
        // #endregion

        var seenBondsSigma = new HashSet<CovalentBond>();
        foreach (var (orb, pos, rot) in targets)
        {
            if (orb == null || orb.Bond == null) continue;
            var cb = orb.Bond;
            if (!cb.IsSigmaBondLine()) continue;
            if (orb.transform.parent != cb.transform) continue;
            if (cb.AtomA != this && cb.AtomB != this) continue;
            if (!seenBondsSigma.Add(cb)) continue;

            var partner = cb.AtomA == this ? cb.AtomB : cb.AtomA;
            Vector3 desiredTipWorld = GetRedistributeTargetHybridTipWorldFromTuple(orb, rot);
            if (desiredTipWorld.sqrMagnitude < 1e-16f) continue;
            desiredTipWorld.Normalize();

            Vector3 currentTipWorld = transform.TransformDirection(OrbitalTipDirectionInNucleusLocal(orb));
            if (currentTipWorld.sqrMagnitude < 1e-16f) continue;
            currentTipWorld.Normalize();

            float alignGap = Vector3.Angle(currentTipWorld, desiredTipWorld);
            float alignUndir = Mathf.Min(alignGap, Vector3.Angle(currentTipWorld, -desiredTipWorld));
            float dotDesVsAxis = 0f;
            float dotCurVsAxis = 0f;
            float dotCurVsDes = Vector3.Dot(currentTipWorld, desiredTipWorld);
            Vector3 toPartnerUnit = Vector3.zero;
            if (partner != null)
            {
                Vector3 toPartner = partner.transform.position - transform.position;
                if (toPartner.sqrMagnitude > 1e-10f)
                {
                    toPartner.Normalize();
                    toPartnerUnit = toPartner;
                    dotDesVsAxis = Vector3.Dot(desiredTipWorld, toPartner);
                    dotCurVsAxis = Vector3.Dot(currentTipWorld, toPartner);
                }
            }

            if (DebugLogRedistribute3DTipGapTrace)
            {
                string msg =
                    "[redist3d-tip-trace] phase=sigmaBeforeBondUpdate pivotId=" + GetInstanceID() +
                    " bondId=" + cb.GetInstanceID() + " partnerId=" + (partner != null ? partner.GetInstanceID().ToString() : "null") +
                    " alignGapDeg=" + alignGap.ToString("F2") + " alignUndirDeg=" + alignUndir.ToString("F2") +
                    " dotDesVsCPivotToPartner=" + dotDesVsAxis.ToString("F4") +
                    " dotCurVsCPivotToPartner=" + dotCurVsAxis.ToString("F4") +
                    " dotCurVsDesiredTip=" + dotCurVsDes.ToString("F4");
                Debug.Log(msg);
                ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
            }

            cb.UpdateBondTransformToCurrentAtoms();
            Vector3 curAfterBond = orb.transform.TransformDirection(Vector3.right);
            if (curAfterBond.sqrMagnitude > 1e-16f)
            {
                curAfterBond.Normalize();
                float gapAfterBond = Vector3.Angle(curAfterBond, desiredTipWorld);
                float gapAfterUndir = Mathf.Min(gapAfterBond, Vector3.Angle(curAfterBond, -desiredTipWorld));
                float dotAfterVsAxis = toPartnerUnit.sqrMagnitude > 1e-10f ? Vector3.Dot(curAfterBond, toPartnerUnit) : 0f;
                float dotAfterVsDesired = Vector3.Dot(curAfterBond, desiredTipWorld);
                if (DebugLogRedistribute3DTipGapTrace)
                {
                    string msg =
                        "[redist3d-tip-trace] phase=sigmaAfterBondUpdate pivotId=" + GetInstanceID() +
                        " bondId=" + cb.GetInstanceID() +
                        " alignGapDeg=" + gapAfterBond.ToString("F2") + " alignUndirDeg=" + gapAfterUndir.ToString("F2") +
                        " dotAfterVsCPivotToPartner=" + dotAfterVsAxis.ToString("F4") +
                        " dotAfterVsDesiredTip=" + dotAfterVsDesired.ToString("F4");
                    Debug.Log(msg);
                    ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
                }
            }

            cb.ApplySigmaOrbitalTipFromRedistribution(this, desiredTipWorld);

            if (DebugLogRedistribute3DTipGapTrace)
            {
                Vector3 curPost = orb.transform.TransformDirection(Vector3.right);
                float gapPostVsTuple = 0f;
                float gapPostUndir = 0f;
                if (curPost.sqrMagnitude > 1e-16f)
                {
                    curPost.Normalize();
                    gapPostVsTuple = Vector3.Angle(curPost, desiredTipWorld);
                    gapPostUndir = Mathf.Min(gapPostVsTuple, Vector3.Angle(curPost, -desiredTipWorld));
                }
                float dotPostVsAxis = toPartnerUnit.sqrMagnitude > 1e-10f ? Vector3.Dot(curPost, toPartnerUnit) : 0f;
                float dotPostVsDesired = curPost.sqrMagnitude > 1e-16f ? Vector3.Dot(curPost.normalized, desiredTipWorld) : 0f;
                string msg =
                    "[redist3d-tip-trace] phase=sigmaPostApply pivotId=" + GetInstanceID() +
                    " bondId=" + cb.GetInstanceID() +
                    " alignGapVsTupleTipDeg=" + gapPostVsTuple.ToString("F2") + " alignUndirDeg=" + gapPostUndir.ToString("F2") +
                    " dotPostVsCPivotToPartner=" + dotPostVsAxis.ToString("F4") +
                    " dotPostVsDesiredTip=" + dotPostVsDesired.ToString("F4");
                Debug.Log(msg);
                ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
            }

            if (DebugLogRedistribute3DApplyBondParented)
            {
                var fragment = partner != null ? GetAtomsOnSideOfSigmaBond(partner) : null;
                bool ringFallback = partner != null && fragment != null && PartnerSigmaFragmentOverlapsOtherEdges(partner, fragment);
                string msg =
                    "[redist3d-apply-bond] joint pivot id=" + GetInstanceID() + " bondId=" + cb.GetInstanceID() +
                    " partner id=" + (partner != null ? partner.GetInstanceID().ToString() : "null") +
                    " fragCount=" + (fragment != null ? fragment.Count : 0) + " ringFallback=" + ringFallback + " alignGapDeg=" + alignGap.ToString("F2") +
                    " alignUndirDeg=" + alignUndir.ToString("F2") +
                    " jointAngleDeg=" + Quaternion.Angle(Quaternion.identity, jointDeltaRigidDiag).ToString("F2") +
                    " skipRigidFrag=" + skipJointRigidFragmentMotion;
                Debug.Log(msg);
                ProjectAgentDebugLog.AppendOrbitalRedistMirrorLine(msg);
            }
        }
        }
        finally
        {
            orbitalsExcludedFromJointRigidInApplyRedistributeTargets = null;
        }
    }

    bool IsOrbitalExcludedFromJointRigidRedistribute(ElectronOrbitalFunction orb) =>
        orb != null
        && orbitalsExcludedFromJointRigidInApplyRedistributeTargets != null
        && orbitalsExcludedFromJointRigidInApplyRedistributeTargets.Contains(orb);

    /// <summary>World +X hybrid tip implied by target tuple; <paramref name="targetOrbitalLocalRotation"/> is under <paramref name="orb"/>.transform.parent (bond-local for σ on <see cref="CovalentBond"/>). For σ lines on this nucleus, tip is forced into the same hemisphere as pivot→partner (matches <see cref="CovalentBond.ApplySigmaOrbitalTipFromRedistribution"/> so joint rotation and lone domains stay consistent; avoids O–C–O π formation with dot(tuple,axis)&lt;0).</summary>
    Vector3 GetRedistributeTargetHybridTipWorldFromTuple(ElectronOrbitalFunction orb, Quaternion targetOrbitalLocalRotation)
    {
        if (orb == null) return Vector3.zero;
        Transform parent = orb.transform.parent;
        Vector3 tip;
        if (parent == null)
            tip = (targetOrbitalLocalRotation * Vector3.right).normalized;
        else
            tip = parent.TransformDirection((targetOrbitalLocalRotation * Vector3.right).normalized).normalized;

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry
            && orb.Bond is CovalentBond cbSigma
            && cbSigma.Orbital == orb
            && cbSigma.IsSigmaBondLine()
            && (cbSigma.AtomA == this || cbSigma.AtomB == this))
        {
            AtomFunction partner = cbSigma.AtomA == this ? cbSigma.AtomB : cbSigma.AtomA;
            if (partner != null)
            {
                Vector3 geom = partner.transform.position - transform.position;
                if (geom.sqrMagnitude > 1e-10f)
                {
                    geom.Normalize();
                    float dotTipGeom = Vector3.Dot(tip, geom);
                    if (dotTipGeom < 0f)
                        tip = -tip;
                }
            }
        }

        return tip;
    }

    /// <summary>True if substituent atoms for <paramref name="partner"/> share any atom with another σ neighbor’s substituent (ring / shared branch); same idea as overlap in <see cref="BuildSigmaNeighborTargetsWithFragmentRigidRotation"/>.</summary>
    bool PartnerSigmaFragmentOverlapsOtherEdges(AtomFunction partner, List<AtomFunction> fragment)
    {
        if (partner == null || fragment == null || fragment.Count == 0) return false;
        var others = GetDistinctSigmaNeighborAtoms();
        for (int oi = 0; oi < others.Count; oi++)
        {
            var other = others[oi];
            if (other == null || other == partner) continue;
            var otherFrag = GetAtomsOnSideOfSigmaBond(other);
            for (int fi = 0; fi < fragment.Count; fi++)
            {
                var a = fragment[fi];
                if (a != null && otherFrag.Contains(a))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Terminal =O (one σ neighbor): trigonal planar VSEPR is 2 lone pairs + bond domain. The shell can still list three occupied nucleus lobes (e.g. 2×2e + 1×1e before π count updates on the non–π-operation leg). Keep the pair with the widest mutual tip angle (~120° lone–lone), dropping the outlier that mis-escalates TryMatch to tetrahedral + azimuth twist.
    /// <paramref name="redistributionOperationBondForPredictive"/> when non-null (π step hybrid refresh): allow collapse even when <see cref="GetPiBondCount"/> is still 0 on this atom (far O in first CO₂ π).
    /// </summary>
    List<ElectronOrbitalFunction> CollapseNucleusLoneDomainsForTerminalSp2OxoHybridIfNeeded(
        List<ElectronOrbitalFunction> loneOccupied,
        List<Vector3> bondAxesMerged,
        CovalentBond redistributionOperationBondForPredictive)
    {
        if (loneOccupied == null || loneOccupied.Count != 3) return loneOccupied;
        if (AtomicNumber != 8) return loneOccupied;
        if (bondAxesMerged == null || bondAxesMerged.Count != 1) return loneOccupied;
        if (GetDistinctSigmaNeighborCount() != 1) return loneOccupied;
        bool piStepHybridContext = redistributionOperationBondForPredictive != null && !redistributionOperationBondForPredictive.IsSigmaBondLine();
        if (GetPiBondCount() < 1 && !piStepHybridContext) return loneOccupied;

        int bestI = 0, bestJ = 1;
        float bestSep = -1f;
        for (int i = 0; i < 3; i++)
        {
            for (int j = i + 1; j < 3; j++)
            {
                Vector3 ti = OrbitalTipLocalDirection(loneOccupied[i]);
                Vector3 tj = OrbitalTipLocalDirection(loneOccupied[j]);
                if (ti.sqrMagnitude < 1e-14f || tj.sqrMagnitude < 1e-14f) continue;
                float sep = Vector3.Angle(ti.normalized, tj.normalized);
                if (sep > bestSep)
                {
                    bestSep = sep;
                    bestI = i;
                    bestJ = j;
                }
            }
        }
        return new List<ElectronOrbitalFunction> { loneOccupied[bestI], loneOccupied[bestJ] };
    }

    /// <summary>
    /// During σ bond formation, align shared σ lobes on <see cref="CovalentBond"/> and (for trigonal electron geometry) nucleus lone lobes to the same <see cref="TryMatchLoneOrbitalsToFreeIdealDirections"/> frame as full <see cref="GetRedistributeTargets3D"/>.
    /// Previously only σ received <see cref="SyncSigmaBondOrbitalTipsFromLocks"/>; lone pairs kept pre-hybrid directions so lone–σ angles could deviate from 120° on terminal O (e.g. O=C=O).</summary>
    /// <param name="redistributionOperationBondForPredictive">When set (e.g. π step), use the same predictive lone/axes model as <see cref="GetRedistributeTargets3DVseprTryMatch"/> so full-molecule hybrid refresh does not re-run tetrahedral TryMatch on a leg that already resolved trigonal.</param>
    public void RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(
        AtomFunction partnerAlongNewSigmaBond,
        CovalentBond redistributionOperationBondForPredictive = null,
        ElectronOrbitalFunction vseprDisappearingLoneForPredictive = null)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1)
        {
            if (ElectronOrbitalFunction.DebugLogSigmaFormationHeavyOrbRotationWhy
                && ElectronOrbitalFunction.ConsumeSigmaFormationHeavyRotDiag())
                Debug.Log(
                    "[σ-form-rot-hybrid] skip maxSlots<=1 atom=" + name + "(Z=" + AtomicNumber + ") partner=" +
                    (partnerAlongNewSigmaBond != null ? partnerAlongNewSigmaBond.name : "null"));
            return;
        }

        float mergeToleranceDeg = 360f / (2f * maxSlots);
        var partnerForRef = partnerAlongNewSigmaBond ?? ResolvePredictiveVseprNewBondPartner(null, redistributionOperationBondForPredictive);
        Vector3 refLocal = RedistributeReferenceDirectionLocalForTargets(partnerForRef);
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;
        else refLocal.Normalize();

        ClearSigmaBondOrbitalRedistributionDeltaWhereAuthoritative();

        var loneRaw = bondedOrbitals.Where(orb => orb != null && orb.Bond == null && orb.ElectronCount > 0).ToList();
        bool applyPredictive = ShouldApplyPredictiveVseprDomainModelForTryMatch(false, partnerAlongNewSigmaBond, redistributionOperationBondForPredictive);
        var partnerForBuild = ResolvePredictiveVseprNewBondPartner(partnerAlongNewSigmaBond, redistributionOperationBondForPredictive);
        BuildPredictiveVseprTryMatchLoneOccupiedAndBondAxes(
            loneRaw,
            mergeToleranceDeg,
            applyPredictive,
            partnerForBuild,
            redistributionOperationBondForPredictive,
            vseprDisappearingLoneForPredictive,
            out var loneOrbitals,
            out var bondAxes,
            out _,
            out _,
            out _);

        loneOrbitals = CollapseNucleusLoneDomainsForTerminalSp2OxoHybridIfNeeded(loneOrbitals, bondAxes, redistributionOperationBondForPredictive);

        int domainCount = GetVseprSlotCount3D(bondAxes.Count, loneOrbitals.Count);
        if (domainCount < 1)
        {
            if (ElectronOrbitalFunction.DebugLogSigmaFormationHeavyOrbRotationWhy
                && ElectronOrbitalFunction.ConsumeSigmaFormationHeavyRotDiag())
                Debug.Log(
                    "[σ-form-rot-hybrid] skip domainCount<1 atom=" + name + "(Z=" + AtomicNumber + ") σAxes=" + bondAxes.Count +
                    " loneOcc=" + loneOrbitals.Count + " partner=" + (partnerAlongNewSigmaBond != null ? partnerAlongNewSigmaBond.name : "null"));
            return;
        }

        var idealRaw = VseprLayout.GetIdealLocalDirections(domainCount);
        var newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));

        if (!TryMatchLoneOrbitalsToFreeIdealDirections(
                refLocal, domainCount, bondAxes, loneOrbitals, newDirs, out var bestMapping, out var pinReservedDir, out var bondIdealLocks, null))
        {
            if (ElectronOrbitalFunction.DebugLogSigmaFormationHeavyOrbRotationWhy
                && ElectronOrbitalFunction.ConsumeSigmaFormationHeavyRotDiag())
                Debug.Log(
                    "[σ-form-rot-hybrid] TryMatch FAIL (no bondIdealLocks) atom=" + name + "(Z=" + AtomicNumber + ") domainCount=" + domainCount +
                    " σAxesMerged=" + bondAxes.Count + " loneOcc=" + loneOrbitals.Count + " refLocal=" + refLocal.ToString("F2"));
            return;
        }

        if (ElectronOrbitalFunction.DebugLogSigmaFormationHeavyOrbRotationWhy
            && ElectronOrbitalFunction.ConsumeSigmaFormationHeavyRotDiag())
            Debug.Log(
                "[σ-form-rot-hybrid] TryMatch OK → lone+SyncSigma atom=" + name + "(Z=" + AtomicNumber + ") partner=" +
                (partnerAlongNewSigmaBond != null ? partnerAlongNewSigmaBond.name : "null") + " domainCount=" + domainCount +
                " σAxesMerged=" + bondAxes.Count + " loneOcc=" + loneOrbitals.Count + " lockPairs=" + bondIdealLocks.Count);

        // Trigonal domains (e.g. one merged σ axis + two lone pairs on terminal =O): apply lone slots like Redistribute3D
        // so lone–σ pairwise angles match ~120°; σ-only sync left lobes stale (H-O-angle-snap legacy O lone–σ ~84° / ~145°).
        if (domainCount == 3 && bestMapping != null && bestMapping.Count > 0 && bestMapping.Count == loneOrbitals.Count)
        {
            for (int i = 0; i < bestMapping.Count; i++)
            {
                var orb = loneOrbitals[i];
                if (orb == null) continue;
                Vector3 newDir = bestMapping[i].newDir;
                var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius, orb.transform.localRotation);
                orb.transform.localPosition = pos;
                orb.transform.localRotation = rot;
            }
        }

        SyncSigmaBondOrbitalTipsFromLocks(bondIdealLocks);
    }

    /// <summary>
    /// π formation only calls <see cref="RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/> on the two π endpoints.
    /// σ-neighbor atoms (e.g. the other terminal O in O=C=O) keep stale lone-vs-σ hybrid frames until refreshed; see NDJSON H-O-angle-snap on "far" O.
    /// When <paramref name="redistributionOperationBondForPredictive"/> is a π bond, atoms <b>not</b> on that bond get <see cref="GetRedistributeTargets"/> with <c>bondingTopologyChanged: true</c> and <see cref="ApplyRedistributeTargets"/> first: their π/σ counts often unchanged so the usual <see cref="GetRedistributeTargets"/> early-out skipped full layout, while <see cref="RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/> alone only TryMatch-reposes a collapsed subset of nucleus lobes and left the third occupied lobe in ~109.5° tetrahedral geometry (failure.log perm-cost on far O, tri120 only on π endpoints).
    /// </summary>
    /// <param name="skipHybridAlignmentForAtom">When non-null, that atom is omitted so terminal multiply-bond guide shells (e.g. pre-existing =O in O=C=O second π) are not re-laid by TryMatch while the op leg animates — avoids success/failure splits driven only by which oxygen picked up full-molecule hybrid motion (runtime: success.log pivotDrift on op O vs failure.log legacy lobe jump with fragN=0).</param>
    /// <param name="redistributionOperationBondForPredictive">π bond under formation: pass through to <see cref="RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/> so predictive VSEPR matches <see cref="GetRedistributeTargets3DVseprTryMatch"/> on all centers (e.g. far terminal O).</param>
    public static void RefreshSigmaBondOrbitalHybridAlignmentForConnectedMolecule(AtomFunction anyInMolecule, AtomFunction skipHybridAlignmentForAtom = null, CovalentBond redistributionOperationBondForPredictive = null)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || anyInMolecule == null) return;
        var mol = anyInMolecule.GetConnectedMolecule();
        if (mol == null || mol.Count == 0) return;
        UpdateSigmaBondLineTransformsOnlyForAtoms(mol);
        CovalentBond op = redistributionOperationBondForPredictive;
        bool piStepPeripheralFullRedist = op != null && !op.IsSigmaBondLine() && op.AtomA != null && op.AtomB != null;
        foreach (var a in mol)
        {
            if (a == null) continue;
            if (skipHybridAlignmentForAtom != null && a == skipHybridAlignmentForAtom) continue;
            if (piStepPeripheralFullRedist)
            {
                bool onOpPiEndpoints = a == op.AtomA || a == op.AtomB;
                if (!onOpPiEndpoints)
                {
                    int piNow = a.GetPiBondCount();
                    var peripheralTargets = a.GetRedistributeTargets(
                        piNow,
                        newBondPartner: null,
                        bondingTopologyChanged: true,
                        redistributionOperationBond: op);
                    if (peripheralTargets != null && peripheralTargets.Count > 0)
                        a.ApplyRedistributeTargets(peripheralTargets, skipJointRigidFragmentMotion: true);
                }
            }
            a.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(null, redistributionOperationBondForPredictive, null);
        }
    }

    /// <summary>After π snap: full-molecule hybrid refresh, but skip the legacy carbonyl oxygen when it is already multiply bonded to the central C (second π to CO₂) so the guide =O shell stays fixed.</summary>
    public static void RefreshSigmaBondOrbitalHybridAlignmentForConnectedMoleculeAfterPiStep(CovalentBond opPi, AtomFunction anyInMolecule)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || anyInMolecule == null) return;
        AtomFunction skip = null;
        AtomFunction cCentForLog = null;
        AtomFunction opOxygenForLog = null;
        int bondsToLegacy = 0;
        if (opPi != null
            && !opPi.IsSigmaBondLine()
            && TryFindLegacyCarbonylOxygenForCPiBond(opPi, out var cC, out var opO, out var legO)
            && legO != null
            && cC != null)
        {
            opOxygenForLog = opO;
            bondsToLegacy = cC.GetBondsTo(legO);
            if (bondsToLegacy > 1)
            {
                skip = legO;
                cCentForLog = cC;
            }
        }
        // #region agent log
        if (DebugLogOcoSecondPiNdjson && opPi != null && !opPi.IsSigmaBondLine())
        {
            ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
                "H3",
                "AtomFunction.RefreshSigmaBondOrbitalHybridAlignmentForConnectedMoleculeAfterPiStep",
                "afterPiStep connected-molecule hybrid refresh",
                "{\"skipLegacyHybrid\":" + (skip != null ? "true" : "false")
                + ",\"legId\":" + (skip != null ? skip.GetInstanceID().ToString() : "0")
                + ",\"opOId\":" + (opOxygenForLog != null ? opOxygenForLog.GetInstanceID().ToString() : "0")
                + ",\"cId\":" + (cCentForLog != null ? cCentForLog.GetInstanceID().ToString() : "0")
                + ",\"bondsToLegacy\":" + bondsToLegacy.ToString() + "}");
        }
        // #endregion
        RefreshSigmaBondOrbitalHybridAlignmentForConnectedMolecule(anyInMolecule, skip, opPi);
    }

    /// <summary>World poses + bond π δ for π step-2 bake: lerp ends where post-hybrid finals land (no pop after animation).</summary>
    public sealed class PiStep2VisualBakeState
    {
        public List<(AtomFunction atom, Vector3 wPos, Quaternion wRot)> Atoms;
        public List<(CovalentBond bond, Vector3 wPos, Quaternion wRot, Quaternion orbDelta, bool orbFlipped)> Bonds;
        public List<(ElectronOrbitalFunction orb, Vector3 wPos, Quaternion wRot)> Orbitals;

        /// <param name="extraOrbitalsWorldSpace">
        /// π step-2: orbitals detached from atom hierarchies (e.g. source lobe after <c>SetParent(null)</c>) must be listed so pre- and post-bake captures include the same orbital set.
        /// </param>
        public static PiStep2VisualBakeState Capture(AtomFunction anyInMolecule, params ElectronOrbitalFunction[] extraOrbitalsWorldSpace)
        {
            var state = new PiStep2VisualBakeState();
            if (anyInMolecule == null)
            {
                state.Atoms = new List<(AtomFunction, Vector3, Quaternion)>();
                state.Bonds = new List<(CovalentBond, Vector3, Quaternion, Quaternion, bool)>();
                state.Orbitals = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
                return state;
            }
            var mol = anyInMolecule.GetConnectedMolecule();
            state.Atoms = new List<(AtomFunction, Vector3, Quaternion)>(mol.Count);
            foreach (var a in mol)
            {
                if (a == null) continue;
                var t = a.transform;
                state.Atoms.Add((a, t.position, t.rotation));
            }
            state.Atoms.Sort((x, y) =>
            {
                int ix = x.atom != null ? x.atom.GetInstanceID() : int.MaxValue;
                int iy = y.atom != null ? y.atom.GetInstanceID() : int.MaxValue;
                return ix.CompareTo(iy);
            });
            var seenBonds = new HashSet<CovalentBond>();
            state.Bonds = new List<(CovalentBond, Vector3, Quaternion, Quaternion, bool)>();
            foreach (var a in mol)
            {
                if (a?.CovalentBonds == null) continue;
                foreach (var b in a.CovalentBonds)
                {
                    if (b == null || !seenBonds.Add(b)) continue;
                    var bt = b.transform;
                    var (d, f) = b.CapturePiStep2RedistributionForBake();
                    state.Bonds.Add((b, bt.position, bt.rotation, d, f));
                }
            }
            state.Bonds.Sort((x, y) =>
            {
                int ix = x.bond != null ? x.bond.GetInstanceID() : int.MaxValue;
                int iy = y.bond != null ? y.bond.GetInstanceID() : int.MaxValue;
                return ix.CompareTo(iy);
            });
            var orbsSeen = new HashSet<ElectronOrbitalFunction>();
            state.Orbitals = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
            foreach (var a in mol)
            {
                if (a == null) continue;
                var childOrbs = a.GetComponentsInChildren<ElectronOrbitalFunction>(true);
                for (int i = 0; i < childOrbs.Length; i++)
                {
                    var o = childOrbs[i];
                    if (o == null || !orbsSeen.Add(o)) continue;
                    var ot = o.transform;
                    state.Orbitals.Add((o, ot.position, ot.rotation));
                }
                if (a.CovalentBonds == null) continue;
                foreach (var cb in a.CovalentBonds)
                {
                    if (cb?.Orbital == null || !orbsSeen.Add(cb.Orbital)) continue;
                    var ot = cb.Orbital.transform;
                    state.Orbitals.Add((cb.Orbital, ot.position, ot.rotation));
                }
            }
            if (extraOrbitalsWorldSpace != null)
            {
                for (int ei = 0; ei < extraOrbitalsWorldSpace.Length; ei++)
                {
                    var o = extraOrbitalsWorldSpace[ei];
                    if (o == null || !orbsSeen.Add(o)) continue;
                    var ot = o.transform;
                    state.Orbitals.Add((o, ot.position, ot.rotation));
                }
            }
            state.Orbitals.Sort((x, y) =>
            {
                int ix = x.orb != null ? x.orb.GetInstanceID() : int.MaxValue;
                int iy = y.orb != null ? y.orb.GetInstanceID() : int.MaxValue;
                return ix.CompareTo(iy);
            });
            return state;
        }

        public static void Restore(PiStep2VisualBakeState s)
        {
            if (s == null) return;
            if (s.Atoms != null)
                for (int i = 0; i < s.Atoms.Count; i++)
                {
                    var (a, p, r) = s.Atoms[i];
                    if (a != null) a.transform.SetPositionAndRotation(p, r);
                }
            if (s.Bonds != null)
                for (int i = 0; i < s.Bonds.Count; i++)
                {
                    var (b, p, r, d, f) = s.Bonds[i];
                    if (b == null) continue;
                    b.transform.SetPositionAndRotation(p, r);
                    b.RestorePiStep2RedistributionForBake(d, f);
                }
            if (s.Orbitals != null)
                for (int i = 0; i < s.Orbitals.Count; i++)
                {
                    var (o, wp, wr) = s.Orbitals[i];
                    if (o != null) o.transform.SetPositionAndRotation(wp, wr);
                }
        }

        public static void Lerp(PiStep2VisualBakeState a, PiStep2VisualBakeState b, float s)
        {
            if (a == null || b == null) return;
            s = Mathf.Clamp01(s);
            if (a.Atoms != null && b.Atoms != null && a.Atoms.Count == b.Atoms.Count)
                for (int i = 0; i < a.Atoms.Count; i++)
                {
                    var (atom, ap, ar) = a.Atoms[i];
                    var (_, bp, br) = b.Atoms[i];
                    if (atom != null) atom.transform.SetPositionAndRotation(Vector3.Lerp(ap, bp, s), Quaternion.Slerp(ar, br, s));
                }
            if (a.Bonds != null && b.Bonds != null && a.Bonds.Count == b.Bonds.Count)
                for (int i = 0; i < a.Bonds.Count; i++)
                {
                    var (bond, ap, ar, ad, af) = a.Bonds[i];
                    var (_, bp, br, bd, bf) = b.Bonds[i];
                    if (bond == null) continue;
                    bond.transform.SetPositionAndRotation(Vector3.Lerp(ap, bp, s), Quaternion.Slerp(ar, br, s));
                    bond.RestorePiStep2RedistributionForBake(Quaternion.Slerp(ad, bd, s), s >= 0.999f ? bf : af);
                }
            if (a.Orbitals != null && b.Orbitals != null && a.Orbitals.Count == b.Orbitals.Count)
                for (int i = 0; i < a.Orbitals.Count; i++)
                {
                    var (o, aw, arot) = a.Orbitals[i];
                    var (_, bw, brot) = b.Orbitals[i];
                    if (o != null) o.transform.SetPositionAndRotation(Vector3.Lerp(aw, bw, s), Quaternion.Slerp(arot, brot, s));
                }
        }

        /// <summary>π step-2 motion: endpoint nuclei stay pinned — skip them so their child lobes are not dragged before world lerp reapplies tips.</summary>
        public static void LerpAtomsExceptEndpoints(PiStep2VisualBakeState a, PiStep2VisualBakeState b, float s, AtomFunction endpointA, AtomFunction endpointB)
        {
            if (a == null || b == null) return;
            s = Mathf.Clamp01(s);
            if (a.Atoms == null || b.Atoms == null || a.Atoms.Count != b.Atoms.Count) return;
            for (int i = 0; i < a.Atoms.Count; i++)
            {
                var (atom, ap, ar) = a.Atoms[i];
                var (_, bp, br) = b.Atoms[i];
                if (atom == null) continue;
                if (atom == endpointA || atom == endpointB) continue;
                atom.transform.SetPositionAndRotation(Vector3.Lerp(ap, bp, s), Quaternion.Slerp(ar, br, s));
            }
        }

        public static void LerpBondsAndOrbitalsOnly(PiStep2VisualBakeState a, PiStep2VisualBakeState b, float s)
        {
            if (a == null || b == null) return;
            s = Mathf.Clamp01(s);
            if (a.Bonds != null && b.Bonds != null && a.Bonds.Count == b.Bonds.Count)
                for (int i = 0; i < a.Bonds.Count; i++)
                {
                    var (bond, ap, ar, ad, af) = a.Bonds[i];
                    var (_, bp, br, bd, bf) = b.Bonds[i];
                    if (bond == null) continue;
                    bond.transform.SetPositionAndRotation(Vector3.Lerp(ap, bp, s), Quaternion.Slerp(ar, br, s));
                    bond.RestorePiStep2RedistributionForBake(Quaternion.Slerp(ad, bd, s), s >= 0.999f ? bf : af);
                }
            if (a.Orbitals != null && b.Orbitals != null && a.Orbitals.Count == b.Orbitals.Count)
                for (int i = 0; i < a.Orbitals.Count; i++)
                {
                    var (o, aw, arot) = a.Orbitals[i];
                    var (_, bw, brot) = b.Orbitals[i];
                    if (o != null) o.transform.SetPositionAndRotation(Vector3.Lerp(aw, bw, s), Quaternion.Slerp(arot, brot, s));
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

    /// <summary>Returns a lone orbital with exactly 1 electron, preferring the one closest to preferredDirection. For programmatic bonding.
    /// After choosing the orbital, place the partner along <see cref="OrbitalAngleUtility.GetOrbitalDirectionWorld"/> for that lobe when
    /// preferredDirection comes from σ-only VSEPR (lone-pair domains are otherwise ignored).</summary>
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

    /// <summary>When &gt; 0, <see cref="SetupIgnoreCollisions"/> skips work (batch FG / multi-bond builds call <see cref="SetupIgnoreCollisionsInvolvingAtoms"/> or <see cref="SetupGlobalIgnoreCollisions"/> once at the end).</summary>
    public static int SuppressAutoGlobalIgnoreCollisions;

    public void SetupIgnoreCollisions()
    {
        if (SuppressAutoGlobalIgnoreCollisions > 0) return;
        SetupGlobalIgnoreCollisions();
    }

    static void BuildCollisionUniverse(
        AtomFunction[] sceneAtoms,
        out List<(Collider2D c2d, Collider c3d)> atomColliders,
        out List<ElectronOrbitalFunction> orbitals,
        out List<ElectronFunction> allElectrons)
    {
        atomColliders = new List<(Collider2D c2d, Collider c3d)>();
        orbitals = new List<ElectronOrbitalFunction>();
        allElectrons = new List<ElectronFunction>();
        foreach (var a in sceneAtoms)
        {
            if (a == null) continue;
            var a2d = a.GetComponent<Collider2D>();
            var a3d = a.GetComponent<Collider>();
            if (a2d != null || a3d != null) atomColliders.Add((a2d, a3d));
            orbitals.AddRange(a.GetComponentsInChildren<ElectronOrbitalFunction>());
            foreach (var b in a.CovalentBonds)
            {
                if (b == null) continue;
                var o = b.Orbital;
                if (o == null) continue;
                if (!orbitals.Contains(o)) orbitals.Add(o);
            }
        }
        orbitals.RemoveAll(static o => o == null);
        foreach (var orb in orbitals)
        {
            if (orb == null) continue;
            allElectrons.AddRange(orb.GetComponentsInChildren<ElectronFunction>());
        }
        foreach (var e in Object.FindObjectsByType<ElectronFunction>(FindObjectsSortMode.None))
        {
            if (e != null && !allElectrons.Contains(e))
                allElectrons.Add(e);
        }
    }

    public static void SetupGlobalIgnoreCollisions()
    {
        var atoms = Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None);
        BuildCollisionUniverse(atoms, out var atomColliders, out var orbitals, out var allElectrons);

        for (int i = 0; i < atomColliders.Count; i++)
            for (int j = i + 1; j < atomColliders.Count; j++)
                IgnoreCollisionPair(atomColliders[i], atomColliders[j]);
        foreach (var ac in atomColliders)
            foreach (var orb in orbitals)
            {
                if (orb == null) continue;
                IgnoreCollision(ac, orb.GetComponent<Collider2D>(), orb.GetComponent<Collider>());
            }
        foreach (var ac in atomColliders)
            foreach (var e in allElectrons)
            {
                if (e == null) continue;
                IgnoreCollision(ac, e.GetComponent<Collider2D>(), e.GetComponent<Collider>());
            }
        for (int i = 0; i < orbitals.Count; i++)
        {
            if (orbitals[i] == null) continue;
            for (int j = i + 1; j < orbitals.Count; j++)
            {
                if (orbitals[j] == null) continue;
                IgnoreCollision(orbitals[i], orbitals[j]);
            }
            foreach (var e in allElectrons)
            {
                if (e == null) continue;
                IgnoreCollision(orbitals[i], e);
            }
        }
        for (int i = 0; i < allElectrons.Count; i++)
        {
            if (allElectrons[i] == null) continue;
            for (int j = i + 1; j < allElectrons.Count; j++)
            {
                if (allElectrons[j] == null) continue;
                IgnoreCollision(allElectrons[i], allElectrons[j]);
            }
        }
    }

    /// <summary>
    /// Sets ignore flags only for pairs that touch <paramref name="involvedAtoms"/> (expanded to orbitals/electrons on those centers).
    /// Use after adding a small fragment so cost is O(|involved|·scene) instead of O(scene²).
    /// </summary>
    public static void SetupIgnoreCollisionsInvolvingAtoms(IReadOnlyCollection<AtomFunction> involvedAtoms)
    {
        if (involvedAtoms == null || involvedAtoms.Count == 0)
        {
            SetupGlobalIgnoreCollisions();
            return;
        }

        var involved = new HashSet<AtomFunction>();
        foreach (var a in involvedAtoms)
            if (a != null) involved.Add(a);

        var sceneAtoms = Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None);
        BuildCollisionUniverse(sceneAtoms, out var atomColliders, out var orbitals, out var allElectrons);

        var invAtomColliders = new List<(Collider2D c2d, Collider c3d)>();
        foreach (var a in sceneAtoms)
        {
            if (a == null || !involved.Contains(a)) continue;
            var a2d = a.GetComponent<Collider2D>();
            var a3d = a.GetComponent<Collider>();
            if (a2d != null || a3d != null) invAtomColliders.Add((a2d, a3d));
        }

        var invOrbitals = new List<ElectronOrbitalFunction>();
        foreach (var a in sceneAtoms)
        {
            if (a == null || !involved.Contains(a)) continue;
            invOrbitals.AddRange(a.GetComponentsInChildren<ElectronOrbitalFunction>());
            foreach (var b in a.CovalentBonds)
            {
                if (b == null) continue;
                var o = b.Orbital;
                if (o == null) continue;
                if (!invOrbitals.Contains(o)) invOrbitals.Add(o);
            }
        }
        invOrbitals.RemoveAll(static o => o == null);
        var invElectrons = new List<ElectronFunction>();
        foreach (var orb in invOrbitals)
        {
            if (orb == null) continue;
            invElectrons.AddRange(orb.GetComponentsInChildren<ElectronFunction>());
        }

        foreach (var iac in invAtomColliders)
            foreach (var ac in atomColliders)
                IgnoreCollisionPair(iac, ac);
        foreach (var io in invOrbitals)
            foreach (var o in orbitals)
            {
                if (io == null || o == null) continue;
                IgnoreCollision(io, o);
            }
        foreach (var ie in invElectrons)
            foreach (var e in allElectrons)
            {
                if (ie == null || e == null) continue;
                IgnoreCollision(ie, e);
            }

        foreach (var iac in invAtomColliders)
            foreach (var orb in orbitals)
            {
                if (orb == null) continue;
                IgnoreCollision(iac, orb.GetComponent<Collider2D>(), orb.GetComponent<Collider>());
            }
        foreach (var ac in atomColliders)
            foreach (var io in invOrbitals)
            {
                if (io == null) continue;
                IgnoreCollision(ac, io.GetComponent<Collider2D>(), io.GetComponent<Collider>());
            }

        foreach (var iac in invAtomColliders)
            foreach (var e in allElectrons)
            {
                if (e == null) continue;
                IgnoreCollision(iac, e.GetComponent<Collider2D>(), e.GetComponent<Collider>());
            }
        foreach (var ac in atomColliders)
            foreach (var ie in invElectrons)
            {
                if (ie == null) continue;
                IgnoreCollision(ac, ie.GetComponent<Collider2D>(), ie.GetComponent<Collider>());
            }

        foreach (var io in invOrbitals)
            foreach (var e in allElectrons)
            {
                if (io == null || e == null) continue;
                IgnoreCollision(io, e);
            }
        foreach (var o in orbitals)
            foreach (var ie in invElectrons)
            {
                if (o == null || ie == null) continue;
                IgnoreCollision(o, ie);
            }
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
        if (a == null || b == null) return;
        var a2D = a.GetComponent<Collider2D>();
        var a3D = a.GetComponent<Collider>();
        var b2D = b.GetComponent<Collider2D>();
        var b3D = b.GetComponent<Collider>();
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    static void IgnoreCollision(ElectronOrbitalFunction orb, ElectronFunction e)
    {
        if (orb == null || e == null) return;
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
