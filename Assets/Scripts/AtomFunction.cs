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
    /// <summary>Reused list for σ pre-bond unified shell (bonded + bond GO orbitals + explicit op). Not a persistent cache of atom state.</summary>
    readonly List<ElectronOrbitalFunction> scratchSigmaPrebondRigidShell = new List<ElectronOrbitalFunction>();

    /// <summary>Log π-trigonal guide-group refine, hemisphere flip, and tip-vs-target angles (e.g. O-C-O → O=C). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogPiTrigonalOcoConformation = true;

    /// <summary>π step 2: oxygen nucleus shell — tips not in redist rows vs operation π (O=C=C off-op lone joint). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogOcoOffOpNucleusLoneAngles = true;

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

    /// <summary>Joint quaternion build: one NDJSON line per redistribute target row with skip reason or added branch. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogJointTipPairRowNdjson = true;

    /// <summary>ApplyRedistributeTargets: NDJSON snapshots of π internuclear vertex-0 vs hybrid tip in world space (entry / after joint fragments / after local snap). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogApplyRedistJointVertex0Ndjson = true;

    /// <summary>TryBuild multiply-cluster: NDJSON π slot targetDir vs internuclear v0 (explains J8 angV0Tip residual when target uses lobe tip). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogMultiPiV0ResidualNdjson = true;

    /// <summary>π step 2: NDJSON internuclear vertex-0 world + σ hybrid tips on op bond pair (tracks rotation with guide σ group). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogVertex0SigmaGroupWorldNdjson = true;

    /// <summary>d66405: entry snapshot of op π orbital mesh vertex 0 world position for VP1 driftMm. Cleared when overwriting pi_step2_entry per pivot+opBond key.</summary>
    static readonly Dictionary<long, Vector3> PiStep2MeshVertex0EntryWorldD66405 = new Dictionary<long, Vector3>();

    static int _molEcnEventSeq;

    /// <summary>Pair before/after <see cref="LogMoleculeElectronConfigurationFromAtomUnion"/> snapshots in ingest logs via data.bondEventId.</summary>
    public static int AllocateMoleculeEcnEventId() => ++_molEcnEventSeq;

    bool isBeingHeld;
    /// <summary>True while σ/π formation coroutine has called <see cref="SetInteractionBlocked"/> with blocked=true.</summary>
    bool interactionBlockedByBondFormation;

    /// <summary>Used by orbitals to avoid re-enabling physics on pointer up while formation still holds the molecule.</summary>
    public bool IsInteractionBlockedByBondFormation => interactionBlockedByBondFormation;
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

    /// <summary>NDJSON line for O=C=O-style case debug; forwards to <see cref="ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson"/>.</summary>
    public static void AppendOcoCaseDebugNdjson(string hypothesisId, string location, string message, string dataJsonObject)
    {
        if (string.IsNullOrEmpty(dataJsonObject)) dataJsonObject = "{}";
        ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(hypothesisId, location, message, dataJsonObject);
    }

    /// <summary>π step 2: optional vertex-0 / σ-group world NDJSON. Gated by <see cref="DebugLogVertex0SigmaGroupWorldNdjson"/>.</summary>
    public void AppendVertex0SigmaGroupWorldTraceNdjson(string phase, CovalentBond opBond, float jointDeg)
    {
        if (!DebugLogVertex0SigmaGroupWorldNdjson) return;
        try
        {
            int bid = opBond != null ? opBond.GetInstanceID() : 0;
            string data =
                "{\"phase\":\"" + phase + "\",\"opBondId\":" + bid + ",\"jointDeg\":" + jointDeg.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"pivotId\":" + GetInstanceID() + "}";
            AppendOcoCaseDebugNdjson("V0SG", "AppendVertex0SigmaGroupWorldTraceNdjson", phase, data);
        }
        catch
        {
            /* optional */
        }
    }

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

    /// <summary>
    /// Partner atom across <paramref name="bond"/> when this instance is an endpoint; otherwise null.
    /// Use this (not ad-hoc indexing) so directed graph walks do not confuse A/B with “backward” self-links.
    /// </summary>
    public AtomFunction GetPartnerAtomOnBond(CovalentBond bond)
    {
        if (bond == null) return null;
        if (bond.AtomA == this) return bond.AtomB;
        if (bond.AtomB == this) return bond.AtomA;
        return null;
    }

    /// <summary>
    /// Every σ bond-line lobe (<see cref="CovalentBond.Orbital"/>) incident to this atom. Substituent rigid motion beyond the bond
    /// should continue to use <see cref="GetAtomsOnSideOfSigmaBond"/> (BFS that does not re-cross the pivot σ edge).
    /// </summary>
    public void AppendIncidentSigmaBondLineOrbitals(List<ElectronOrbitalFunction> dst)
    {
        if (dst == null) return;
        foreach (var cb in covalentBonds)
        {
            if (cb == null || !cb.IsSigmaBondLine() || cb.Orbital == null) continue;
            if (cb.AtomA != this && cb.AtomB != this) continue;
            if (!dst.Contains(cb.Orbital)) dst.Add(cb.Orbital);
        }
    }

    /// <summary>
    /// σ formation: after a hybrid preview, append (orb, local pos, local rot) for each incident σ line except <paramref name="formingSigmaBondOrNull"/>.
    /// Temporarily runs <see cref="RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/> then restores nucleus + bond orbital locals.
    /// </summary>
    public bool TryAppendIncidentSigmaBondLineRedistributeTargetsFromHybridPreview(
        AtomFunction partnerAlongSigmaBond,
        CovalentBond formingSigmaBondOrNull,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> dst)
    {
        if (dst == null || partnerAlongSigmaBond == null) return false;
        var nucleusSnap = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
        SnapshotNucleusParentedOrbitalLocalTransforms(nucleusSnap);
        var bondLocals = new List<(CovalentBond cb, Vector3 lp, Quaternion lr)>();
        foreach (var cb in covalentBonds)
        {
            if (cb == null || cb == formingSigmaBondOrNull || !cb.IsSigmaBondLine() || cb.Orbital == null) continue;
            if (cb.AtomA != this && cb.AtomB != this) continue;
            bondLocals.Add((cb, cb.Orbital.transform.localPosition, cb.Orbital.transform.localRotation));
        }

        RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(partnerAlongSigmaBond, formingSigmaBondOrNull, null);

        int added = 0;
        foreach (var cb in covalentBonds)
        {
            if (cb == null || cb == formingSigmaBondOrNull || !cb.IsSigmaBondLine() || cb.Orbital == null) continue;
            if (cb.AtomA != this && cb.AtomB != this) continue;
            dst.Add((cb.Orbital, cb.Orbital.transform.localPosition, cb.Orbital.transform.localRotation));
            added++;
        }

        RestoreNucleusParentedOrbitalLocalTransforms(nucleusSnap);
        for (int i = 0; i < bondLocals.Count; i++)
        {
            var (cb, lp, lr) = bondLocals[i];
            if (cb?.Orbital == null) continue;
            cb.Orbital.transform.localPosition = lp;
            cb.Orbital.transform.localRotation = lr;
        }

        return added > 0;
    }

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

    static readonly HashSet<AtomFunction> BondFormationInteractionBlockedAtoms = new HashSet<AtomFunction>();

    /// <summary>Call when stepped-debug phase wait starts/ends so colliders can turn on during <see cref="BondFormationDebugController.IsWaitingForPhase"/>.</summary>
    public static void RefreshBondFormationInteractionAfterPhaseWaitChange()
    {
        foreach (var a in BondFormationInteractionBlockedAtoms)
            if (a != null) a.ApplyBondFormationInteractionBlock();
    }

    /// <summary>Block or unblock pointer interaction on this atom and all its orbitals and electrons. Used during bond formation.</summary>
    public void SetInteractionBlocked(bool blocked)
    {
        interactionBlockedByBondFormation = blocked;
        if (blocked) BondFormationInteractionBlockedAtoms.Add(this);
        else BondFormationInteractionBlockedAtoms.Remove(this);
        ApplyBondFormationInteractionBlock();
    }

    void ApplyBondFormationInteractionBlock()
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (!interactionBlockedByBondFormation)
        {
            if (col != null) col.enabled = true;
            if (col2D != null) col2D.enabled = true;
            foreach (var orb in GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb.transform.parent != transform) continue;
                orb.SetPointerBlocked(false);
                orb.SetPhysicsEnabled(true);
                foreach (var e in orb.GetComponentsInChildren<ElectronFunction>())
                    e.SetPointerBlocked(false);
            }
            return;
        }

        // During stepped-debug phase waits: keep atom colliders on for selection; orbitals stay pointer-blocked (no drag/peel on lobes).
        bool suppressAtomOrbitalColliders = !BondFormationDebugController.IsWaitingForPhase;

        if (col != null) col.enabled = !suppressAtomOrbitalColliders;
        if (col2D != null) col2D.enabled = !suppressAtomOrbitalColliders;

        foreach (var orb in GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != transform) continue;
            orb.SetPointerBlocked(true);
            orb.SetPhysicsEnabled(false);
            foreach (var e in orb.GetComponentsInChildren<ElectronFunction>())
                e.SetPointerBlocked(true);
        }
    }

    void OnDestroy()
    {
        BondFormationInteractionBlockedAtoms.Remove(this);
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

    /// <summary>Snapshot locals for every orbital parented to this nucleus (σ + non-bond) — used before predictive hybrid refresh previews.</summary>
    public void SnapshotNucleusParentedOrbitalLocalTransforms(List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> dst)
    {
        if (dst == null) return;
        dst.Clear();
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.transform.parent != transform) continue;
            dst.Add((o, o.transform.localPosition, o.transform.localRotation));
        }
    }

    /// <summary>Restore a snapshot from <see cref="SnapshotNucleusParentedOrbitalLocalTransforms"/>.</summary>
    public static void RestoreNucleusParentedOrbitalLocalTransforms(List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> snap)
    {
        if (snap == null) return;
        foreach (var (orb, localPos, localRot) in snap)
        {
            if (orb == null) continue;
            orb.transform.localPosition = localPos;
            orb.transform.localRotation = localRot;
        }
    }

    /// <summary>
    /// Rigid rotation about <see cref="transform.position"/> for one nucleus-child orbital (σ pre-bond op lobe only).
    /// </summary>
    public void ApplyRigidWorldRotationToSingleNucleusParentedOrbital(ElectronOrbitalFunction orb, Quaternion worldDelta)
    {
        if (orb == null || orb.transform.parent != transform) return;
        Vector3 pivot = transform.position;
        orb.transform.position = pivot + worldDelta * (orb.transform.position - pivot);
        orb.transform.rotation = worldDelta * orb.transform.rotation;
    }

    /// <summary>
    /// σ pre-bond: <see cref="bondedOrbitals"/> ∪ orbitals under each <see cref="CovalentBond"/> (σ+π) ∪ optional forming op; deduped.
    /// </summary>
    public void CollectSigmaPrebondRigidShellOrbitals(ElectronOrbitalFunction explicitNonGuideOp, List<ElectronOrbitalFunction> dst)
    {
        if (dst == null) return;
        dst.Clear();
        var seen = new HashSet<int>();
        void Add(ElectronOrbitalFunction o)
        {
            if (o == null) return;
            int id = o.GetInstanceID();
            if (seen.Add(id))
                dst.Add(o);
        }
        foreach (var o in bondedOrbitals)
            Add(o);
        foreach (var cb in covalentBonds)
        {
            if (cb == null) continue;
            var under = cb.GetComponentsInChildren<ElectronOrbitalFunction>(true);
            if (under == null) continue;
            for (int i = 0; i < under.Length; i++)
                Add(under[i]);
        }
        Add(explicitNonGuideOp);
    }

    /// <summary>
    /// Same world-space rotation about this <b>nucleus</b> for the σ pre-bond <b>unified shell</b> (bonded list + bond GO orbitals + explicit op).
    /// </summary>
    /// <param name="explicitNonGuideOp">Non-guide gesture orbital for this atom so it stays in the set if list membership drifts.</param>
    public void ApplyRigidWorldRotationToNucleusParentedOrbitals(Quaternion worldDelta, ElectronOrbitalFunction explicitNonGuideOp = null)
    {
        Vector3 pivot = transform.position;
        CollectSigmaPrebondRigidShellOrbitals(explicitNonGuideOp, scratchSigmaPrebondRigidShell);
        var copy = new List<ElectronOrbitalFunction>(scratchSigmaPrebondRigidShell);
        foreach (var orb in copy)
        {
            if (orb == null)
                continue;
            orb.transform.position = pivot + worldDelta * (orb.transform.position - pivot);
            orb.transform.rotation = worldDelta * orb.transform.rotation;
        }
    }

    /// <summary>World pose of each nucleus-parented orbital (for σ pre-bond animation about a fixed pivot).</summary>
    public void CaptureNucleusParentedOrbitalWorldTransforms(List<(ElectronOrbitalFunction orb, Vector3 worldPos, Quaternion worldRot)> dst)
    {
        if (dst == null) return;
        dst.Clear();
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.transform.parent != transform) continue;
            dst.Add((o, o.transform.position, o.transform.rotation));
        }
    }

    /// <summary>World pose of every orbital in the σ pre-bond unified shell (see <see cref="CollectSigmaPrebondRigidShellOrbitals"/>).</summary>
    public void CaptureAllBondedOrbitalWorldTransforms(
        List<(ElectronOrbitalFunction orb, Vector3 worldPos, Quaternion worldRot)> dst,
        ElectronOrbitalFunction explicitNonGuideOp = null)
    {
        if (dst == null) return;
        dst.Clear();
        CollectSigmaPrebondRigidShellOrbitals(explicitNonGuideOp, scratchSigmaPrebondRigidShell);
        foreach (var o in scratchSigmaPrebondRigidShell)
        {
            if (o == null) continue;
            dst.Add((o, o.transform.position, o.transform.rotation));
        }
    }

    /// <summary>Local pose snapshot for the σ pre-bond unified shell (bond GO–parented σ/π included).</summary>
    public void SnapshotAllBondedOrbitalLocalTransforms(
        List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> dst,
        ElectronOrbitalFunction explicitNonGuideOp = null)
    {
        if (dst == null) return;
        dst.Clear();
        CollectSigmaPrebondRigidShellOrbitals(explicitNonGuideOp, scratchSigmaPrebondRigidShell);
        foreach (var o in scratchSigmaPrebondRigidShell)
        {
            if (o == null) continue;
            dst.Add((o, o.transform.localPosition, o.transform.localRotation));
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

    /// <summary>Returns true if orbitals are not on ideal VSEPR directions (3D).</summary>
    public bool HasInconsistentOrbitalAngles()
    {
        int slotCount = GetOrbitalSlotCount();
        if (slotCount <= 1) return false;

        float tolerance = 360f / (2f * slotCount);
        return HasInconsistentOrbitalDirections3D(tolerance);
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


    /// <summary>
    /// 3D σ post-Create formation: refresh each incident σ bond’s <see cref="CovalentBond.UpdateBondTransformToCurrentAtoms"/> only.
    /// Does not snap σ orbitals — avoids fighting the formation coroutine / <c>LateUpdate</c> (one pose pass per frame).
    /// </summary>
    public static void UpdateSigmaBondLineTransformsOnlyForAtoms(IEnumerable<AtomFunction> atoms)
    {
        if (atoms == null) return;
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

    /// <summary>Adds a unit direction to <paramref name="list"/> if not within <paramref name="mergeAngleDeg"/> of an existing entry (predictive VSEPR σ-axis merge).</summary>
    void AppendUniqueFrameworkDirection(List<Vector3> list, Vector3 unitDir, float mergeAngleDeg = 8f)
    {
        if (list == null || unitDir.sqrMagnitude < 1e-12f) return;
        unitDir = unitDir.normalized;
        foreach (var e in list)
        {
            float ang = Vector3.Angle(e, unitDir);
            if (ang < mergeAngleDeg || ang > 180f - mergeAngleDeg) return;
        }
        list.Add(unitDir);
    }

    /// <summary>Trigonal ideal directions (120°) with first vertex along <paramref name="guideTip"/>; <paramref name="movers2"/> unused (legacy perm-cost hook).</summary>
    Vector3[] BuildTrigonalGuideGroupAlignedIdealWithAzimuthMinPermCost(Vector3 guideTip, List<ElectronOrbitalFunction> movers2)
    {
        _ = movers2;
        Vector3 g = guideTip.sqrMagnitude > 1e-14f ? guideTip.normalized : Vector3.right;
        return VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(3), g);
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

        if (loneMatch.Count >= 2 && free.Count == loneMatch.Count && slotCount == 3)
        {
            Vector3 g = refLocal.sqrMagnitude > 1e-14f ? refLocal.normalized : Vector3.right;
            var aligned3 = BuildTrigonalGuideGroupAlignedIdealWithAzimuthMinPermCost(g, loneMatch);
            for (int fi = 0; fi < free.Count; fi++)
                free[fi] = aligned3[freeSlotIndices[fi]];
        }
        else if (loneMatch.Count >= 2 && free.Count == loneMatch.Count)
            MinimizeTargetDirsAzimuthForPermutationCostInPlace(loneMatch, free, refLocal.normalized, 36, 0.05f);

        var permTm = FindBestOrbitalToTargetDirsPermutation(loneMatch, free, bondRadius);
        if (permTm == null)
        {
            LogVsepr3D($"TryMatch FindBestOrbitalToTargetDirsPermutation null: atom={name} Z={atomicNumber} loneMatch={loneMatch.Count}");
            mapping = null;
            return false;
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
        if (bondLength < 1e-4f) return false;

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
        if (partner == null) return false;
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
        if (Mathf.Abs(psiDeg) < 1e-4f) return;
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
        if (atoms == null) return;
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
        if (child == null || partner == null
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
    /// σ post-Create formation: rewrite VSEPR redistribute targets on <paramref name="child"/> so final lobe directions equal unstaggered targets rotated by Newman ψ
    /// about world axis child→<paramref name="partner"/> (call while preview geometry matches unstaggered σ-relax end).
    /// </summary>
    public static void TwistRedistributeTargetsForNewmanStaggerEnd(
        AtomFunction child,
        AtomFunction partner,
        float psiDeg,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets)
    {
        if (child == null || partner == null || targets == null
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
    public Vector3 GetReferenceDirectionLocalForVsepr(float? piBondAngleOverride = null, Vector3? refBondWorldDirection = null) =>
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
        if (partnerAlongRef == null || sigmaNeighborCountBefore < 0) return false;
        if (GetPiBondCount() != 0) return false;
        if (GetDistinctSigmaNeighborCount() != 3) return false;
        if (GetDistinctSigmaNeighborCount() <= sigmaNeighborCountBefore) return false;
        if (sigmaNeighborCountBefore != 2) return false;

        Vector3 refLocal = FormationReferenceDirectionLocalForPartner(partnerAlongRef);
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
    /// When <paramref name="pinOccupiedCleavedExBond"/> is set, those occupied domains keep their current local pose (e.g. released lone pair while tier-6 guide is the empty lobe) but still contribute tips to repulsion for other domains.
    /// </remarks>
    bool TryComputeRepulsionSumElectronDomainLayoutSlots(
        IReadOnlyList<ElectronOrbitalFunction> occupiedDomains,
        IReadOnlyList<ElectronOrbitalFunction> emptyNonBond,
        ElectronOrbitalFunction pinEmptyGuideOrbital,
        out List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> slots,
        out List<Vector3> idealDirNucleusLocalForApply,
        out List<bool> skipApplyCarbonIdealDir,
        HashSet<ElectronOrbitalFunction> pinOccupiedCleavedExBond = null)
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
            if (pinOccupiedCleavedExBond != null && pinOccupiedCleavedExBond.Contains(o))
            {
                slots.Add((o, o.transform.localPosition, o.transform.localRotation));
                idealDirNucleusLocalForApply.Add(Vector3.zero);
                skipApplyCarbonIdealDir.Add(true);
                continue;
            }
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
            bool pinEmpty = pinEmptyGuideOrbital != null
                && (e == pinEmptyGuideOrbital || IsBondBreakGuideOrbitalWithBondingCleared(e));
            AppendOneRepulsionSumEmptySlot(
                e,
                pinEmpty,
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


    /// <summary>
    /// Bond-break: σ-line tracking delay + simultaneous pure redistribution on atoms that receive 0e anti-guide (see <see cref="SigmaBreakPureRedistribution"/>).
    /// <paramref name="onComplete"/> runs electron sync and bond GO teardown.
    /// </summary>
    public IEnumerator CoLerpBondBreakRedistribution(
        AtomFunction partnerAtom,
        ElectronOrbitalFunction antiGuideOnThis,
        ElectronOrbitalFunction antiGuideOnPartner,
        System.Action onComplete)
    {
        const float duration = 0.65f;
        if (partnerAtom == null)
        {
            onComplete?.Invoke();
            yield break;
        }

        var planThis = SigmaBreakPureRedistribution.TryBuildMotionPlan(this, antiGuideOnThis);
        var planPartner = SigmaBreakPureRedistribution.TryBuildMotionPlan(partnerAtom, antiGuideOnPartner);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float s = duration > 1e-6f ? Mathf.Clamp01(elapsed / duration) : 1f;
            float smooth = s * s * (3f - 2f * s);
            ApplySigmaBreakPureMotionLerp(planThis, smooth);
            ApplySigmaBreakPureMotionLerp(planPartner, smooth);
            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(new HashSet<AtomFunction> { this, partnerAtom });
            yield return null;
        }

        ApplySigmaBreakPureMotionLerp(planThis, 1f);
        ApplySigmaBreakPureMotionLerp(planPartner, 1f);
        AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(new HashSet<AtomFunction> { this, partnerAtom });

        RefreshChargesAfterSigmaBreakPurePlan(planThis);
        RefreshChargesAfterSigmaBreakPurePlan(planPartner);
        RefreshCharge();
        partnerAtom.RefreshCharge();
        SetupGlobalIgnoreCollisions();

        onComplete?.Invoke();
    }

    static void RefreshChargesAfterSigmaBreakPurePlan(SigmaBreakPureRedistribution.MotionPlan plan)
    {
        if (plan == null) return;
        foreach (var kv in plan.AtomWorldLerp)
            kv.Key?.RefreshCharge();
    }

    static void ApplySigmaBreakPureMotionLerp(SigmaBreakPureRedistribution.MotionPlan plan, float s)
    {
        if (plan == null) return;
        for (int i = 0; i < plan.NonBondLerp.Count; i++)
        {
            var (orb, lp0, lr0, lp1, lr1) = plan.NonBondLerp[i];
            if (orb == null) continue;
            orb.transform.localPosition = Vector3.Lerp(lp0, lp1, s);
            orb.transform.localRotation = Quaternion.Slerp(lr0, lr1, s);
        }
        foreach (var kv in plan.AtomWorldLerp)
        {
            var a = kv.Key;
            if (a == null) continue;
            var (w0, w1) = kv.Value;
            a.transform.position = Vector3.Lerp(w0, w1, s);
        }
    }

    void ClearSigmaBondOrbitalRedistributionDeltaWhereAuthoritative()
    {
        foreach (var b in covalentBonds)
        {
            if (b == null || !b.IsSigmaBondLine()) continue;
            b.ResetOrbitalRedistributionDeltaIfAuthoritative(this);
        }
    }

    /// <summary>
    /// Shared σ orbital on <see cref="CovalentBond"/> uses one world rotation; after lone lobes snap to VSEPR vertices,
    /// rotate that orbital so +X tracks this nucleus's hybrid direction from <see cref="TryMatchLoneOrbitalsToFreeIdealDirections"/>.
    /// Runs once per atom that called <see cref="RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/>; each end applies for its
    /// own incident bonds with <paramref name="locks"/> in that nucleus's local space (do not gate on
    /// <see cref="CovalentBond.AuthoritativeAtomForOrbitalRedistributionPose"/>, or lower-InstanceID partners like H never receive the heavy atom's frame).
    /// </summary>
    void SyncSigmaBondOrbitalTipsFromLocks(List<(Vector3 bondAxisLocal, Vector3 idealLocal)> locks)
    {
        if (locks == null || locks.Count == 0)
            return;
        foreach (var bond in covalentBonds)
        {
            if (bond == null || bond.Orbital == null || !bond.IsSigmaBondLine()) continue;
            var other = bond.AtomA == this ? bond.AtomB : bond.AtomA;
            if (other == null)
                continue;
            Vector3 toOther = other.transform.position - transform.position;
            if (toOther.sqrMagnitude < 1e-12f)
                continue;
            Vector3 axisLocal = transform.InverseTransformDirection(toOther.normalized);
            if (axisLocal.sqrMagnitude < 1e-12f)
                continue;
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
            if (bestK < 0 || bestDot < 0.35f)
                continue;

            Vector3 hybridWorld = transform.TransformDirection(locks[bestK].idealLocal);
            if (hybridWorld.sqrMagnitude < 1e-12f)
                continue;
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

    Vector3 FormationReferenceDirectionLocalForPartner(AtomFunction newBondPartner)
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

    /// <summary>
    /// Unit direction in <b>this</b> reference nucleus’s local space from that nucleus’s pivot toward the orbital transform’s world position (“orbital center” used for placement).
    /// The vector is <b>not</b> absolute: it depends on which atom’s <see cref="AtomFunction"/> you call this on — the reference nucleus is always <c>this.transform</c>.
    /// For redistribution of a <b>bonding</b> orbital, use the pivot atom that is <b>undergoing redistribution</b> as <c>this</c> so directions stay in that atom’s VSEPR frame.
    /// When pivot→orbital offset is degenerate, falls back to the canonical hybrid +X lobe axis (same as historical behavior).
    /// </summary>
    Vector3 OrbitalTipDirectionInNucleusLocal(ElectronOrbitalFunction orb)
    {
        if (orb == null) return Vector3.right;
        Vector3 deltaWorld = orb.transform.position - transform.position;
        if (deltaWorld.sqrMagnitude > 1e-14f)
            return transform.InverseTransformVector(deltaWorld).normalized;
        if (orb.transform.parent == transform)
            return OrbitalTipLocalDirection(orb).normalized;
        Vector3 tipW = orb.transform.TransformDirection(Vector3.right);
        if (tipW.sqrMagnitude < 1e-10f) return Vector3.forward;
        return transform.InverseTransformDirection(tipW.normalized).normalized;
    }

    /// <summary>
    /// VSEPR vertex directions are in <b>this nucleus</b> local space (same as lone <see cref="OrbitalTipLocalDirection"/>).
    /// <see cref="ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection"/> expects <paramref name="idealDirNucleusLocal"/> in the orbital's <b>parent</b> local space (nucleus or <see cref="CovalentBond"/>).
    /// When the parent is a bond, converts nucleus → bond so hybrid +X, joint rotation, and <see cref="GetOrbitalHybridTipWorldFromLocalRotation"/> agree with nonbonding domains.
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
        Debug.Log("[pi-trigonal-oco] " + detailNoTag);
    }

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

    /// <summary>Cone angle between tip directions (3D).</summary>
    static float FormationStyleTipToTipCost(Vector3 oldTipLocal, Vector3 newTipLocal)
    {
        oldTipLocal.Normalize();
        newTipLocal.Normalize();
        return Vector3.Angle(oldTipLocal, newTipLocal);
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
        for (int i = 0; i < n; i++)
        {
            if (tipSpaceNucleus != null && orbs[i] != null && orbs[i].Bond != null)
                continue;
            quatSumDeg += QuaternionSlotCostOnly(orbs[i], targetDirs[perm[i]], bondRadius);
        }
        for (int i = 0; i < n; i++)
        {
            Vector3 td = targetDirs[perm[i]].normalized;
            Vector3 ot = tipSpaceNucleus != null
                ? tipSpaceNucleus.OrbitalTipDirectionInNucleusLocal(orbs[i])
                : OrbitalTipLocalDirection(orbs[i]).normalized;
            coneOrPlanarDeg += PiPermutationConeAngleTipToTargetDeg(orbs[i], ot, td);
        }
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
        if (orbs == null || targetDirs == null || orbs.Count < 2 || orbs.Count != targetDirs.Count) return;
        if (orbs.Count > 5) return;

        Vector3 a = twistAxisNucleusLocal.normalized;
        if (a.sqrMagnitude < 1e-10f) return;

        // Azimuth grid disabled: keep caller's template targetDirs unchanged.
        _ = gridSteps;
        _ = minMeaningfulImprove;
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

        foreach (var perm in Permutations(idx))
        {
            float total;
            float quatSum = 0f;
            for (int a = 0; a < n; a++)
                quatSum += QuaternionSlotCostOnlyFromSlotRot(list[movableIdx[a]].orb, slotRot[perm[a]], bondRadius);
            float coneSum = 0f;
            for (int a = 0; a < n; a++)
            {
                var o = list[movableIdx[a]].orb;
                Vector3 nt = (slotRot[perm[a]] * Vector3.right).normalized;
                Vector3 ot = OrbitalTipLocalDirection(o).normalized;
                coneSum += PiPermutationConeAngleTipToTargetDeg(o, ot, nt);
            }
            total = coneSum + quatSum;
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

        foreach (var perm in Permutations(idx))
        {
            float total;
            float quatSum = 0f;
            for (int a = 0; a < n; a++)
                quatSum += QuaternionSlotCostOnly(orbs[a], copy[perm[a]], bondRadius);
            float coneSum = 0f;
            for (int a = 0; a < n; a++)
            {
                Vector3 td = copy[perm[a]].normalized;
                Vector3 ot = OrbitalTipLocalDirection(orbs[a]).normalized;
                coneSum += Vector3.Angle(ot, td);
            }
            total = coneSum + quatSum;
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

    /// <summary>Stable atom ordering for σ/π bond-formation steps (replaces former redistribution guide-tier ordering).</summary>
    public static void OrderPairAtomsForBondFormationStep(
        AtomFunction atomA,
        AtomFunction atomB,
        CovalentBond redistributionOperationBond,
        ElectronOrbitalFunction bondBreakGuideLoneOrbitalForA,
        ElectronOrbitalFunction bondBreakGuideLoneOrbitalForB,
        out AtomFunction first,
        out AtomFunction second)
    {
        _ = redistributionOperationBond;
        _ = bondBreakGuideLoneOrbitalForA;
        _ = bondBreakGuideLoneOrbitalForB;
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
        int idA = atomA.GetInstanceID();
        int idB = atomB.GetInstanceID();
        if (idA <= idB)
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

    /// <summary>Stepped bond-formation debug: resolve multiply-bond anchor for guide-line tint. Redistribution removed — always falls back to the operation bond.</summary>
    public bool TryGetRedistributionGuideBondAnchorForSteppedDebug(
        CovalentBond redistributionOperationBond,
        ElectronOrbitalFunction bondBreakGuideLoneOrbitalForTargets,
        out CovalentBond guideBond)
    {
        _ = bondBreakGuideLoneOrbitalForTargets;
        guideBond = null;
        return false;
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
        if (targets == null || outInitialWorld == null) return;
        var seenBondsMove = new HashSet<CovalentBond>();
        foreach (var (orb, pos, rot) in targets)
        {
            if (orb == null || orb.Bond == null) continue;
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
    /// Joint rotation (world) from start lobe tips to target tuple tips — nucleus non-bond rows plus σ bond-line rows parented on <see cref="CovalentBond"/> (π bond lines are still skipped).
    /// <paramref name="starts"/> must align with <paramref name="targets"/>; missing entries use zero local pose.
    /// When <paramref name="jointTipsSigmaBondLineRowsOnly"/> is true (σ post-Create formation), nucleus-parented non-bond rows are omitted from the tip fit so those lobes are driven only by redistribute lerp — avoids pairing the same nonbond DOF with fragment joint motion.
    /// </summary>
    public Quaternion ComputeJointRotationWorldFromOrbitalTuples(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets,
        List<(Vector3 localPos, Quaternion localRot)> starts,
        bool jointTipsSigmaBondLineRowsOnly = false)
    {
        if (targets == null || targets.Count == 0)
            return Quaternion.identity;

        var currentTips = new List<Vector3>();
        var desiredTips = new List<Vector3>();

        for (int i = 0; i < targets.Count; i++)
        {
            var (orb, pos, rot) = targets[i];
            var (sp, sr) = (starts != null && i < starts.Count) ? starts[i] : (Vector3.zero, Quaternion.identity);
            int orbIdRow = 0;
            int bondIdRow = 0;
            int parentIdRow = 0;
            int bondTransIdRow = 0;
            bool hasBondRow = false;
            bool sigmaLineRow = false;
            bool piLineRow = false;
            string h6Outcome = "skip_unknown";

            if (orb == null)
                h6Outcome = "skip_null_orb";
            else
            {
                orbIdRow = orb.GetInstanceID();
                if (orb.Bond is CovalentBond cb)
                {
                    hasBondRow = true;
                    bondIdRow = cb.GetInstanceID();
                    bondTransIdRow = cb.transform != null ? cb.transform.GetInstanceID() : 0;
                    parentIdRow = orb.transform.parent != null ? orb.transform.parent.GetInstanceID() : 0;
                    sigmaLineRow = cb.IsSigmaBondLine();
                    piLineRow = !sigmaLineRow;
                    if (sigmaLineRow
                        && orb.transform.parent == cb.transform
                        && (cb.AtomA == this || cb.AtomB == this))
                    {
                        Vector3 cur = orb.transform.parent.TransformDirection((sr * Vector3.right).normalized);
                        if (cur.sqrMagnitude < 1e-16f)
                            h6Outcome = "skip_sigma_bond_cur_tip_near_zero";
                        else
                        {
                            cur.Normalize();
                            Vector3 des = GetOrbitalHybridTipWorldFromLocalRotation(orb, rot);
                            if (des.sqrMagnitude < 1e-16f)
                                h6Outcome = "skip_sigma_bond_des_tip_near_zero";
                            else
                            {
                                des.Normalize();
                                currentTips.Add(cur);
                                desiredTips.Add(des);
                                h6Outcome = "added_sigma_bond_line";
                            }
                        }
                    }
                    else if (piLineRow)
                        h6Outcome = "skip_pi_bond_joint_excluded";
                    else
                        h6Outcome = "skip_bonding_orb_joint_excluded";
                }
                else if (orb.Bond != null)
                {
                    hasBondRow = true;
                    bondIdRow = orb.Bond.GetInstanceID();
                    h6Outcome = "skip_bonding_orb_joint_excluded";
                }
                else if (orb.transform.parent == transform)
                {
                    parentIdRow = transform.GetInstanceID();
                    if (jointTipsSigmaBondLineRowsOnly)
                        h6Outcome = "skip_nucleus_nonbond_joint_sigma_formation_step2";
                    else
                    {
                        Vector3 cur = transform.TransformDirection((sr * Vector3.right).normalized);
                        if (cur.sqrMagnitude < 1e-16f)
                            h6Outcome = "skip_nucleus_cur_tip_near_zero";
                        else
                        {
                            cur.Normalize();
                            Vector3 des = GetOrbitalHybridTipWorldFromLocalRotation(orb, rot);
                            if (des.sqrMagnitude < 1e-16f)
                                h6Outcome = "skip_nucleus_des_tip_near_zero";
                            else
                            {
                                des.Normalize();
                                currentTips.Add(cur);
                                desiredTips.Add(des);
                                h6Outcome = "added_nucleus_child_nonbond";
                            }
                        }
                    }
                }
                else
                {
                    h6Outcome = "skip_has_bond_false_parent_not_this_nucleus";
                    parentIdRow = orb.transform.parent != null ? orb.transform.parent.GetInstanceID() : 0;
                }
            }

            // #region agent log
            if (DebugLogJointTipPairRowNdjson)
            {
                try
                {
                    string d6 = "{\"pivotId\":" + GetInstanceID() + ",\"Z\":" + atomicNumber + ",\"row\":" + i
                        + ",\"orbId\":" + orbIdRow + ",\"hasBond\":" + (hasBondRow ? "true" : "false") + ",\"bondId\":" + bondIdRow
                        + ",\"sigmaLine\":" + (sigmaLineRow ? "true" : "false") + ",\"piLine\":" + (piLineRow ? "true" : "false")
                        + ",\"parentId\":" + parentIdRow + ",\"bondTransId\":" + bondTransIdRow
                        + ",\"parentEqBondTrans\":" + (hasBondRow && orb != null && orb.Bond != null && orb.transform.parent == orb.Bond.transform ? "true" : "false")
                        + ",\"outcome\":\"" + h6Outcome + "\",\"frame\":" + Time.frameCount + "}";
                    AppendOcoCaseDebugNdjson("H6", "ComputeJointRotationWorldFromOrbitalTuples", "joint_tip_row", d6);
                }
                catch
                {
                    /* optional */
                }
            }
            // #endregion
        }
        // #region agent log
        try
        {
            string h5 = "{\"pivotId\":" + GetInstanceID() + ",\"Z\":" + atomicNumber + ",\"targetRows\":" + targets.Count
                + ",\"startRows\":" + (starts != null ? starts.Count : 0) + ",\"jointTipPairs\":" + currentTips.Count
                + ",\"sigmaBondLineRowsOnlyJoint\":" + (jointTipsSigmaBondLineRowsOnly ? "true" : "false")
                + ",\"frame\":" + Time.frameCount + "}";
            AppendOcoCaseDebugNdjson("H5", "ComputeJointRotationWorldFromOrbitalTuples", "tip_pairs_built", h5);
        }
        catch
        {
            /* optional */
        }
        // #endregion
        return ComputeJointRotationWorldFromTipDirections(currentTips, desiredTips);
    }

    /// <summary>
    /// Rigid motion of σ-substituent fragments for a fraction of the full joint rotation (about <paramref name="pivotStartWorld"/>),
    /// with pivot translation <c>pivotWorldNow minus pivotStartWorld</c> folded in. Applies the same world quaternion to each atom's
    /// rotation so nucleus-child lone lobes stay rigid with the fragment (not just translation).
    /// </summary>
    public void ApplyJointFragmentMotionForOrbitalTargets(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets,
        Quaternion deltaWorldFull,
        float fraction,
        Vector3 pivotStartWorld,
        Vector3 pivotWorldNow,
        Dictionary<AtomFunction, (Vector3 worldPos, Quaternion worldRot)> fragmentInitialWorld)
    {
        if (targets == null || fragmentInitialWorld == null || fragmentInitialWorld.Count == 0)
            return;
        fraction = Mathf.Clamp01(fraction);
        Quaternion r = Quaternion.Slerp(Quaternion.identity, deltaWorldFull, fraction);

        var movedAtoms = new HashSet<AtomFunction>();
        var seenBondsMove = new HashSet<CovalentBond>();
        foreach (var (orb, pos, rot) in targets)
        {
            if (orb == null || orb.Bond == null) continue;
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
    /// the same joint quaternion as <see cref="ApplyJointFragmentMotionForOrbitalTargets"/> to keep the electron shell rigid.
    /// Occupied lone pairs (<c>Bond==null</c>, <see cref="ElectronOrbitalFunction.ElectronCount"/>&gt;0) are omitted: σ post-Create formation joint
    /// Δ aligns substituent σ lines, not the pivot’s lone-pair directions — applying joint to those lobes read as nonbond rotation.
    /// </summary>
    public void SnapshotNucleusParentedSiblingsExcludedFromRedistTargets(
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets,
        Dictionary<ElectronOrbitalFunction, (Vector3 worldPos, Quaternion worldRot)> outSnapshot)
    {
        if (outSnapshot == null) return;
        outSnapshot.Clear();
        var inTargets = new HashSet<int>();
        if (targets != null)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var o = targets[i].orb;
                if (o != null) inTargets.Add(o.GetInstanceID());
            }
        }
        for (int i = 0; i < bondedOrbitals.Count; i++)
        {
            var orb = bondedOrbitals[i];
            if (orb == null || inTargets.Contains(orb.GetInstanceID())) continue;
            if (orb.transform.parent != transform) continue;
            if (orb.Bond == null && orb.ElectronCount > 0) continue;
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
        if (siblingSnapshot == null || siblingSnapshot.Count == 0)
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
    /// and vs <see cref="OrbitalTipDirectionInNucleusLocal"/> (pivot→orbital center in reference nucleus space; degenerate case uses +X lobe).
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
        ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
            "H-oco-O-lewis",
            "AtomFunction.EmitOxygenOcoTerminalNdjson",
            msg,
            sb.ToString(),
            runId);
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
        sb.Append(",\"full3D\":true");
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
        ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson(
            "H-O-angle-snap",
            "AtomFunction.EmitOxygenMolEcnOrbitalPairwiseAnglesNdjson",
            msg,
            sb.ToString(),
            runId);
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
        if (!DebugLogOcoOffOpNucleusLoneAngles) return;
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
        float jointDeg = Quaternion.Angle(Quaternion.identity, deltaJointFull);
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
    static Quaternion ComputeJointRotationWorldFromTipDirections(List<Vector3> fromDirs, List<Vector3> toDirs)
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


    /// <summary>World +X hybrid tip implied by target tuple; <paramref name="targetOrbitalLocalRotation"/> is under <paramref name="orb"/>.transform.parent (bond-local for σ on <see cref="CovalentBond"/>). For σ lines on this nucleus, tip is forced into the same hemisphere as pivot→partner (undirected σ axis; avoids O–C–O π formation with dot(tuple,axis)&lt;0).</summary>
    public Vector3 GetOrbitalHybridTipWorldFromLocalRotation(ElectronOrbitalFunction orb, Quaternion targetOrbitalLocalRotation)
    {
        if (orb == null) return Vector3.zero;
        Transform parent = orb.transform.parent;
        Vector3 tip;
        if (parent == null)
            tip = (targetOrbitalLocalRotation * Vector3.right).normalized;
        else
            tip = parent.TransformDirection((targetOrbitalLocalRotation * Vector3.right).normalized).normalized;

        if (orb.Bond is CovalentBond cbSigma
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
    /// Carbonyl-type terminal oxygen (one σ neighbor, <see cref="GetPiBondCount"/> ≥ 1 on this atom): trigonal planar VSEPR is often modeled as σ + two lone domains; the shell may still list three occupied nucleus lobes — keep the pair with the widest mutual tip angle (~120° lone–lone) and drop the outlier.
    /// Single-bonded terminal O (no π on this atom, e.g. far –O in O=C–O during a π step): <b>no</b> collapse — four electron domains (1 bonding + 3 lone pairs) stay as four VSEPR groups (tetrahedral electron geometry).
    /// </summary>
    List<ElectronOrbitalFunction> CollapseNucleusLoneDomainsForTerminalSp2OxoHybridIfNeeded(
        List<ElectronOrbitalFunction> loneOccupied,
        List<Vector3> bondAxesMerged,
        CovalentBond redistributionOperationBondForPredictive)
    {
        _ = redistributionOperationBondForPredictive;
        if (loneOccupied == null || loneOccupied.Count != 3) return loneOccupied;
        if (AtomicNumber != 8) return loneOccupied;
        if (bondAxesMerged == null || bondAxesMerged.Count != 1) return loneOccupied;
        if (GetDistinctSigmaNeighborCount() != 1) return loneOccupied;
        if (GetPiBondCount() < 1) return loneOccupied;

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
        Vector3 refLocal = FormationReferenceDirectionLocalForPartner(partnerForRef);
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

        // Trigonal (3) and tetrahedral (4) electron domains: apply lone slots from TryMatch so nucleus lobes rotate
        // (domainCount 4 was σ-only via SyncSigma before; prebonding-only path then showed maxRotDeg=0 in NDJSON).
        // For post-bond σ phase-3 predictive refresh (operation bond is sigma), preserve existing guide non-op lone orientation:
        // keep σ tip sync from bondIdealLocks, but do not remap lone slots (prevents guide lone teleport after step 2).
        bool skipLoneApplyForPredictiveSigmaPostbond =
            redistributionOperationBondForPredictive != null && redistributionOperationBondForPredictive.IsSigmaBondLine();
        if (!skipLoneApplyForPredictiveSigmaPostbond
            && (domainCount == 3 || domainCount == 4)
            && bestMapping != null && bestMapping.Count > 0 && bestMapping.Count == loneOrbitals.Count)
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
    /// Full-molecule σ hybrid refresh after π/σ formation steps. Non-endpoint atoms (e.g. terminal O in O=C=O) need the same predictive VSEPR pass as the forming bond pair.
    /// </summary>
    /// <param name="skipHybridAlignmentForAtom">When non-null, that atom is omitted (e.g. legacy carbonyl oxygen held fixed during second π).</param>
    /// <param name="redistributionOperationBondForPredictive">π bond under formation: passed to <see cref="RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/> for consistent lone/σ-axis modeling.</param>
    public static void RefreshSigmaBondOrbitalHybridAlignmentForConnectedMolecule(AtomFunction anyInMolecule, AtomFunction skipHybridAlignmentForAtom = null, CovalentBond redistributionOperationBondForPredictive = null)
    {
        if (anyInMolecule == null) return;
        var mol = anyInMolecule.GetConnectedMolecule();
        if (mol == null || mol.Count == 0) return;
        UpdateSigmaBondLineTransformsOnlyForAtoms(mol);
        foreach (var a in mol)
        {
            if (a == null) continue;
            if (skipHybridAlignmentForAtom != null && a == skipHybridAlignmentForAtom) continue;
            a.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(null, redistributionOperationBondForPredictive, null);
        }
    }

    /// <summary>After π snap: full-molecule hybrid refresh, but skip the legacy carbonyl oxygen when it is already multiply bonded to the central C (second π to CO₂) so the guide =O shell stays fixed.</summary>
    public static void RefreshSigmaBondOrbitalHybridAlignmentForConnectedMoleculeAfterPiStep(CovalentBond opPi, AtomFunction anyInMolecule)
    {
        if (anyInMolecule == null) return;
        AtomFunction skip = null;
        if (opPi != null
            && !opPi.IsSigmaBondLine()
            && TryFindLegacyCarbonylOxygenForCPiBond(opPi, out var cC, out _, out var legO)
            && legO != null
            && cC != null
            && cC.GetBondsTo(legO) > 1)
            skip = legO;
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
        return GetSlotForNewOrbital3D(preferredDirectionWorld, excludeOrbital);
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

    /// <summary>
    /// Rotate this atom about its nucleus so <paramref name="orbital"/>'s lobe axis (local +X) matches <paramref name="worldDirection"/>.
    /// Rigidly carries all orbitals and electrons (whole VSEPR shell); used when spawning a partner so the σ lobe faces the anchor orbital.
    /// When <paramref name="staggerVsPartner"/> is set, applies a Newman ψ twist about the child→partner axis (~60° vs the partner’s σ
    /// projections when both sides are trigonal) so the three non-bonding groups are staggered relative to the existing center’s conformation.
    /// Pass <paramref name="partnerOrbitalTowardThis"/> (anchor lobe used for the new σ) when the partner has no σ neighbors yet so stagger can use the partner’s other lobes as the “back” reference.
    /// </summary>
    public void RotateAboutCenterSoOrbitalLobePointsWorld(
        ElectronOrbitalFunction orbital,
        Vector3 worldDirection,
        AtomFunction staggerVsPartner = null,
        ElectronOrbitalFunction partnerOrbitalTowardThis = null)
    {
        if (orbital == null || worldDirection.sqrMagnitude < 1e-14f) return;
        Vector3 d0 = OrbitalAngleUtility.GetOrbitalDirectionWorld(orbital.transform);
        Vector3 d1 = worldDirection.normalized;
        if (d0.sqrMagnitude < 1e-14f) return;
        transform.rotation = Quaternion.FromToRotation(d0, d1) * transform.rotation;

        if (staggerVsPartner == null) return;
        if (!TryComputeNewmanStaggerPsiForEditAttach(staggerVsPartner, orbital, partnerOrbitalTowardThis, out float psiDeg)
            || Mathf.Abs(psiDeg) < 0.01f)
            return;

        Vector3 ax = staggerVsPartner.transform.position - transform.position;
        if (ax.sqrMagnitude < 1e-14f) return;
        ax.Normalize();
        // Bond lobe is along this axis; twist preserves its direction.
        transform.rotation = Quaternion.AngleAxis(psiDeg, ax) * transform.rotation;
    }

    /// <summary>σ-neighbor list can be empty before the new bond exists; use partner lobe directions (excluding the bond lobe) as Newman “back” references.</summary>
    static void AppendParentNewmanProjectionsFromPartnerLobes(
        AtomFunction partner,
        Vector3 bondPartnerToChildUnit,
        ElectronOrbitalFunction excludeOrbitalTowardChild,
        List<Vector3> parentProj)
    {
        if (partner == null) return;
        foreach (var o in partner.bondedOrbitals)
        {
            if (o == null || o == excludeOrbitalTowardChild) continue;
            if (o.ElectronCount <= 0) continue;
            Vector3 v = OrbitalAngleUtility.GetOrbitalDirectionWorld(o.transform);
            if (v.sqrMagnitude < 1e-10f) continue;
            Vector3 p = v - Vector3.Dot(v, bondPartnerToChildUnit) * bondPartnerToChildUnit;
            if (p.sqrMagnitude < 1e-8f) continue;
            parentProj.Add(p.normalized);
        }
    }

    /// <summary>
    /// Newman ψ (degrees) about axis this→<paramref name="partner"/> to stagger occupied non-bond lobes vs partner’s σ substituents.
    /// Bond to partner need not exist yet (edit spawn). Uses the same scoring as <see cref="TryComputeNewmanStaggerPsi"/> where applicable.
    /// When the partner has no σ-bonded atoms yet, <paramref name="partnerOrbitalTowardThis"/> supplies the “back” Newman reference via other occupied lobes.
    /// </summary>
    bool TryComputeNewmanStaggerPsiForEditAttach(
        AtomFunction partner,
        ElectronOrbitalFunction bondingOrbitalOnChild,
        ElectronOrbitalFunction partnerOrbitalTowardThis,
        out float psiDeg)
    {
        psiDeg = 0f;
        if (partner == null || bondingOrbitalOnChild == null) return false;

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

        if (parentProj.Count == 0 && partnerOrbitalTowardThis != null)
            AppendParentNewmanProjectionsFromPartnerLobes(partner, bondParentToChild, partnerOrbitalTowardThis, parentProj);

        if (parentProj.Count == 0)
            return false;

        var childRadial = new List<Vector3>();
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb == bondingOrbitalOnChild || orb.Bond != null) continue;
            if (orb.ElectronCount <= 0) continue;
            Vector3 dW = OrbitalAngleUtility.GetOrbitalDirectionWorld(orb.transform);
            if (dW.sqrMagnitude < 1e-10f) continue;
            childRadial.Add(dW.normalized);
        }
        if (childRadial.Count == 0)
            return false;

        var childHDirForTrigonal = new List<Vector3>();
        foreach (var n in GetDistinctSigmaNeighborAtoms())
        {
            if (n == null || n == partner || n.AtomicNumber != 1) continue;
            Vector3 d = n.transform.position - transform.position;
            if (d.sqrMagnitude < 1e-10f) continue;
            childHDirForTrigonal.Add(d.normalized);
        }

        float bestPsi = 0f;
        if (childHDirForTrigonal.Count == 3 && childRadial.Count == 3
            && TryTrigonalMethylNewmanStaggerPsiDegrees(parentProj, childHDirForTrigonal, axis, out bestPsi))
        {
            // Rare at spawn: three σ-H on the new center (same closed form as TryComputeNewmanStaggerPsi).
        }
        else if (parentProj.Count == 3 && childRadial.Count == 3
                 && TryTrigonalMethylNewmanStaggerPsiDegrees(parentProj, childRadial, axis, out bestPsi))
        {
            // Typical edit add: tetrahedral new center — three occupied non-bond lobes vs partner’s three σ projections (~60° stagger).
        }
        else
        {
            const float stepDeg = 10f;
            float bestScore = float.MaxValue;
            float bestMinSep = -1f;
            for (float psi = 0f; psi < 359.99f; psi += stepDeg)
            {
                Quaternion r = Quaternion.AngleAxis(psi, axis);
                float score = NewmanStaggerEclipseScore(parentProj, childRadial, axis, r);
                float minSep = NewmanBottleneckMinAngleToBack(parentProj, childRadial, axis, r);
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

        if (Mathf.Abs(bestPsi) < 0.01f && childHDirForTrigonal.Count == 0 && childRadial.Count >= 1 && parentProj.Count >= 2)
        {
            float b0 = NewmanBottleneckMinAngleToBack(parentProj, childRadial, axis, Quaternion.identity);
            float bestB = b0;
            float psiPick = 0f;
            const float fineStep = 5f;
            for (float psi = fineStep; psi < 359.99f; psi += fineStep)
            {
                Quaternion r = Quaternion.AngleAxis(psi, axis);
                float b = NewmanBottleneckMinAngleToBack(parentProj, childRadial, axis, r);
                if (b > bestB + 0.05f)
                {
                    bestB = b;
                    psiPick = psi;
                }
            }
            if (bestB > b0 + 1.5f)
                bestPsi = psiPick;
        }

        if (Mathf.Abs(bestPsi) < 0.01f)
            return false;
        psiDeg = bestPsi;
        return true;
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

    /// <summary>Opaque RGB matching the edit-mode selection ring (3D).</summary>
    public static Color GetSelectionHighlightRingColorRgb()
    {
        return new Color(0.25f, 0.98f, 0.42f, 1f);
    }

    public void SetSelectionHighlight(bool on)
    {
        SetSelectionHighlight3D(on);
        if (selectionHighlight != null) selectionHighlight.SetActive(false);
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

        // Stepped bond debug: allow selection only; do not start molecule drag.
        if (BondFormationDebugController.IsWaitingForPhase)
        {
            if (editMode != null)
                editMode.OnAtomClicked(this);
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
        SetupIgnoreCollisions();
    }
}
