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

    /// <summary>Console + optional project <c>.log</c>: <c>[vsepr3d]</c> trace. Default off; enable for VSEPR redistribution triage.</summary>
    public static bool DebugLogVseprRedistribute3D = false;

    /// <summary>Console + project <c>.log</c>: <c>[fw-pin]</c> — which code paths move nuclei during redistribute / σ-relax / H snap (framework drag triage). Set true in inspector via script or temporarily in code.</summary>
    public static bool DebugLogFrameworkPinSigmaRelaxTrace = false;

    /// <summary>Writes <c>[fw-pin]</c> lines when <see cref="DebugLogFrameworkPinSigmaRelaxTrace"/> is enabled.</summary>
    public static void LogFrameworkPinSigmaRelax(string message)
    {
        if (!DebugLogFrameworkPinSigmaRelaxTrace) return;
        string line = "[fw-pin] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

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

    static void LogVsepr3D(string message)
    {
        if (!DebugLogVseprRedistribute3D) return;
        string line = "[vsepr3d] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    /// <summary>Console + project <c>.log</c>: <c>[bond-break-cc]</c> C–C σ-cleavage geometry branches. Default off; use <see cref="DebugLogCarbocationTrigonalPlanarityDiag"/> for carbocation planarity.</summary>
    public static bool DebugLogCcBondBreakGeometry = false;

    /// <summary>Console + project <c>.log</c>: <c>[bond-break-cc:trace]</c> — per-call TrySpread / carbocation diagnostics (shell e⁻ counts, guide parent, perm null, redistribute gates). Default off.</summary>
    public static bool DebugLogCcBondBreakSpreadTrace = false;

    /// <summary>Console + project <c>.log</c>: <c>[bond-break-cc]</c> via <see cref="LogCarbocationPlanarityDiagLine"/> — carbocation σ lobe tips (pairwise ~120°, |triple|, ∠to ref) plus **internuclear σ-axis** pairwise angles vs **∠(lobe tip, C→neighbor)**; <c>TETRA_AXES_VS_TRIGONAL_LOBES_WARN</c> when skeleton ~tetrahedral but lobes don’t align to bond axes. Also at <c>OrientEmpty/end</c>. Independent of <see cref="DebugLogCcBondBreakGeometry"/>. **Default on**.</summary>
    public static bool DebugLogCarbocationTrigonalPlanarityDiag = true;

    /// <summary>Console + project <c>.log</c>: <c>[bond-break-cc:sigma-redist]</c> — trigonal slot ideal dirs, bond-hosted <see cref="ApplyCarbonBreakIdealLocalDirection"/>, and <see cref="CovalentBond.ApplySigmaOrbitalTipFromRedistribution"/> (world hybrid, tip0→want, δ rotation). Default off (verbose).</summary>
    public static bool DebugLogCarbocationSigmaRedistributionTrace = false;

    /// <summary>Writes <c>[bond-break-cc:sigma-redist]</c> when <see cref="DebugLogCarbocationSigmaRedistributionTrace"/> is enabled.</summary>
    public static void LogCarbocationSigmaRedistributionTrace(string message)
    {
        if (!DebugLogCarbocationSigmaRedistributionTrace) return;
        string line = "[bond-break-cc:sigma-redist] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    /// <summary>Console + project <c>.log</c>: <c>[bond-break-cc:ideal-frame]</c> — branch order in <see cref="TryAssignCarbonBreakIdealFrame"/> (carbocation trigonal → 2σ trigonal → rigid tet+R → unified sparse). Default off.</summary>
    public static bool DebugLogCarbonBreakIdealFrameTrace = false;

    /// <summary>Writes <c>[bond-break-cc:ideal-frame]</c> lines when <see cref="DebugLogCarbonBreakIdealFrameTrace"/> is enabled.</summary>
    static void LogCarbonBreakIdealFrameTrace(string message)
    {
        if (!DebugLogCarbonBreakIdealFrameTrace) return;
        string line = "[bond-break-cc:ideal-frame] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    /// <summary>Console + project <c>.log</c>: <c>[bond-break-cc:unified-diag]</c> — <see cref="TryAssignCarbonBreakUnifiedSparseTetrahedral"/> pin vs permutation, ideal dirs, perm indices, tips before/after, min inter-tip angle (overlap hint). Default off; enable for unified-tet triage.</summary>
    public static bool DebugLogCcBondBreakUnifiedSparseDiag = false;

    /// <summary>Unity + project <c>.log</c>: <c>[break-empty-teleport]</c> — 0e non-bond world tip through bond-break (finds which step flips ~180°). Default off.</summary>
    public static bool DebugLogBondBreakEmptyTeleport = false;

    public static void LogBondBreakEmptyTeleportLine(string line)
    {
        if (!DebugLogBondBreakEmptyTeleport) return;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    /// <summary>Each 0e non-bond on this atom: local tip, dot/∠ vs ex-bond <paramref name="refWorldTowardPartner"/> and vs guide lobe tip.</summary>
    public void LogEmptyNonbondSnapshotForBondBreak(string phase, Vector3? refWorldTowardPartner, ElectronOrbitalFunction bondBreakGuideOrbital)
    {
        if (!DebugLogBondBreakEmptyTeleport) return;
        Vector3? guideTipW = null;
        if (bondBreakGuideOrbital != null && bondBreakGuideOrbital.transform.parent == transform)
        {
            Vector3 gl = (bondBreakGuideOrbital.transform.localRotation * Vector3.right).normalized;
            Vector3 gw = transform.TransformDirection(gl);
            if (gw.sqrMagnitude > 1e-8f) guideTipW = gw.normalized;
        }

        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null || o.ElectronCount != 0) continue;
            Vector3 tipL = (o.transform.localRotation * Vector3.right).normalized;
            Vector3 tipW = transform.TransformDirection(tipL);
            if (tipW.sqrMagnitude > 1e-8f) tipW.Normalize();
            string refPart = "";
            if (refWorldTowardPartner.HasValue && refWorldTowardPartner.Value.sqrMagnitude > 1e-6f)
            {
                Vector3 rw = refWorldTowardPartner.Value.normalized;
                refPart = $" ∠ref={Vector3.Angle(tipW, rw):F2}° dotRef={Vector3.Dot(tipW, rw):F4}";
            }

            string guidePart = "";
            if (guideTipW.HasValue)
                guidePart = $" ∠guide={Vector3.Angle(tipW, guideTipW.Value):F2}° dotGuide={Vector3.Dot(tipW, guideTipW.Value):F4}";

            string line =
                $"[break-empty-teleport] {phase} parent={name} Z={atomicNumber} parentId={GetInstanceID()} emptyOrbId={o.GetInstanceID()} " +
                $"localP=({o.transform.localPosition.x:F4},{o.transform.localPosition.y:F4},{o.transform.localPosition.z:F4}) " +
                $"tipW=({tipW.x:F4},{tipW.y:F4},{tipW.z:F4}){refPart}{guidePart} " +
                $"carbStyleEmpty={IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(bondBreakGuideOrbital)} σN={GetDistinctSigmaNeighborCount()}";
            LogBondBreakEmptyTeleportLine(line);
        }
    }

    static void LogCcBondBreak(string message)
    {
        if (!DebugLogCcBondBreakGeometry) return;
        string line = "[bond-break-cc] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    /// <summary><c>[bond-break-cc]</c> lines for <see cref="DebugLogCarbocationTrigonalPlanarityDiag"/> only — not gated by <see cref="DebugLogCcBondBreakGeometry"/>.</summary>
    static void LogCarbocationPlanarityDiagLine(string message)
    {
        if (!DebugLogCarbocationTrigonalPlanarityDiag) return;
        string line = "[bond-break-cc] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    static void LogCcBondBreakTrace(string message)
    {
        if (!DebugLogCcBondBreakSpreadTrace) return;
        string line = "[bond-break-cc:trace] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    static void LogCcBondBreakUnified(string message)
    {
        if (!DebugLogCcBondBreakGeometry) return;
        string line = "[bond-break-cc-unified] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    static void LogCcBondBreakUnifiedDiag(string message)
    {
        if (!DebugLogCcBondBreakUnifiedSparseDiag) return;
        string line = "[bond-break-cc:unified-diag] " + message;
        Debug.Log(line);
        ProjectAgentDebugLog.MirrorToProjectDotLog(line);
    }

    static string FormatLocalDir3(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

    void LogUnifiedSparseTipSnapshot(string phase, List<ElectronOrbitalFunction> orbs)
    {
        if (!DebugLogCcBondBreakUnifiedSparseDiag || orbs == null) return;
        for (int i = 0; i < orbs.Count; i++)
        {
            var o = orbs[i];
            if (o == null) continue;
            Vector3 t = OrbitalTipDirectionInNucleusLocal(o);
            string parentKind = o.transform.parent == transform ? "nucleus" : o.Bond != null ? "bond" : "other";
            LogCcBondBreakUnifiedDiag(
                $"{phase} atom={name} parentId={GetInstanceID()} i={i} orbId={o.GetInstanceID()} e={o.ElectronCount} parent={parentKind} tipLocal={FormatLocalDir3(t)}");
        }
    }

    void LogUnifiedSparseMinTipSeparation(string phase, List<ElectronOrbitalFunction> orbs)
    {
        if (!DebugLogCcBondBreakUnifiedSparseDiag || orbs == null || orbs.Count < 2) return;
        float minAng = 180f;
        int ia = -1, ib = -1;
        for (int i = 0; i < orbs.Count; i++)
        {
            if (orbs[i] == null) continue;
            Vector3 ti = OrbitalTipDirectionInNucleusLocal(orbs[i]);
            for (int j = i + 1; j < orbs.Count; j++)
            {
                if (orbs[j] == null) continue;
                Vector3 tj = OrbitalTipDirectionInNucleusLocal(orbs[j]);
                float ang = Vector3.Angle(ti, tj);
                if (ang < minAng)
                {
                    minAng = ang;
                    ia = i;
                    ib = j;
                }
            }
        }
        string warn = minAng < 25f ? " LOW_SEP_WARN" : "";
        LogCcBondBreakUnifiedDiag(
            $"{phase} atom={name} parentId={GetInstanceID()} min∠tips={minAng:F2}° between subsetIdx {ia},{ib}{warn}");
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
    /// <param name="skipBondBreakSparseNonbondSpread">When true, skips <see cref="TrySpreadNonbondOrbitalsForBondBreakSparseSigmaNeighbors"/> in 3D. Set after animated σ-only bond break (lobes already match <see cref="GetRedistributeTargets"/>); a second TrySpread can remap via <c>FindBestDirectionMapping</c> and visibly pop lobes.</param>
    public void RedistributeOrbitals(float? piBondAngleOverride = null, Vector3? refBondWorldDirection = null, bool relaxCoplanarSigmaToTetrahedral = false, bool skipLoneLobeLayout = false, HashSet<AtomFunction> pinAtomsForSigmaRelax = null, bool skipSigmaNeighborRelax = false, ElectronOrbitalFunction bondBreakGuideLoneOrbital = null, AtomFunction newSigmaBondPartnerHint = null, int sigmaNeighborCountBeforeHint = -1, bool skipBondBreakSparseNonbondSpread = false)
    {
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            RedistributeOrbitals3D(piBondAngleOverride, refBondWorldDirection, relaxCoplanarSigmaToTetrahedral, skipLoneLobeLayout, pinAtomsForSigmaRelax, skipSigmaNeighborRelax, bondBreakGuideLoneOrbital, newSigmaBondPartnerHint, sigmaNeighborCountBeforeHint, skipBondBreakSparseNonbondSpread);
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

            Vector3 before = other.transform.position;
            other.transform.position = transform.position + axis * distance;
            if (DebugLogFrameworkPinSigmaRelaxTrace && Vector3.Distance(before, other.transform.position) > 1e-4f)
                LogFrameworkPinSigmaRelax(
                    $"SnapHydrogenΣAxes center={FormatAtomBrief(this)} H={FormatAtomBrief(other)} Δ={Vector3.Distance(before, other.transform.position):F5}");
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
        if (GetPiBondCount() > 0) return false;
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
                b.SnapOrbitalToBondPosition();
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
        bool requireCoplanarBondAxes = true)
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
        HashSet<AtomFunction> pinWorld = null)
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
        BuildSigmaNeighborTargetsWithFragmentRigidRotation(transform.position, sigmaNeighbors, worldDirs, newDirs, this, out targets, pinWorld);
        return targets != null && targets.Count > 0;
    }

    /// <summary>
    /// After a π bond re-forms, σ neighbors may still sit at ~tetrahedral positions (from bond-break relax). sp² centers with
    /// three σ neighbors + π should be trigonal planar; if σ directions are not coplanar, snap neighbors onto 120° in-plane.
    /// Skips when already coplanar (FG builds, undistorted sp²).
    /// </summary>
    void TryRelaxSigmaNeighborsToTrigonalPlanar3D(Vector3 refLocalNormalized, HashSet<AtomFunction> pinWorld = null)
    {
        if (!TryComputeTrigonalPlanarSigmaNeighborRelaxTargets(refLocalNormalized, out var targets, pinWorld) || targets == null) return;

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
        out List<(AtomFunction neighbor, Vector3 targetWorld)> targets)
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

        Vector3 refN = refLocal.normalized;
        var idealWorld = new List<Vector3>(3);
        if (IsSp2BondBreakEmptyAlongRefCase() || IsBondBreakTrigonalPlanarFrameworkCase())
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
        out List<(AtomFunction atom, Vector3 startWorld, Vector3 endWorld)> moves,
        HashSet<AtomFunction> mergePins = null)
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

        if (!TryComputeTetrahedralFourSigmaNeighborRelaxTargets(alignAlong, pin, out var t) || t == null || t.Count == 0) return false;
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
        HashSet<AtomFunction> mergePins = null)
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
        if (!TryComputeCoplanarTetrahedralSigmaNeighborRelaxTargets(refLocal, out var targets, pin, requireCoplanarBondAxes: false) || targets == null || targets.Count == 0)
            return false;

        moves = new List<(AtomFunction, Vector3, Vector3)>();
        foreach (var (n, end) in targets)
            moves.Add((n, n.transform.position, end));
        return true;
    }

    void MaybeApplyTetrahedralSigmaRelaxForBondFormation(AtomFunction partnerAlongRef, int sigmaNeighborCountBefore, HashSet<AtomFunction> mergePins = null)
    {
        List<(AtomFunction atom, Vector3 startWorld, Vector3 endWorld)> moves = null;
        string relaxBranch = null;
        if (TryGetTetrahedralFourSigmaNeighborRelaxMovesForBondFormation(partnerAlongRef, sigmaNeighborCountBefore, out var m4, mergePins) && m4 != null)
        {
            moves = m4;
            relaxBranch = "fourσ";
        }
        else if (TryGetTetrahedralThreeSigmaAx3ERelaxMovesForBondFormation(partnerAlongRef, sigmaNeighborCountBefore, out var m3, mergePins) && m3 != null)
        {
            moves = m3;
            relaxBranch = "threeσAx3E";
        }
        if (moves == null) return;
        const float moveEps = 1e-4f;
        bool any = false;
        foreach (var (_, s, e) in moves)
            if (Vector3.Distance(s, e) > moveEps) any = true;
        if (!any) return;
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
        if (!TryComputeRigidSigmaCleavageWorldRotation(refBondWorldDirection.normalized, out var rWorld, out _))
            return;
        var ends = new List<(AtomFunction atom, Vector3 endWorld)>();
        CollectRigidSigmaCleavageSubstituentAtomEndPositions(rWorld, ends);
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
    /// σN==0 bare C with <b>empty</b> released ex-bond lobe: pin the 0e guide; three e⁻ non-bonds in the plane ⊥ pin (trigonal electron geometry, carbocation-style fragment).
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
        if (AtomicNumber != 6 || GetDistinctSigmaNeighborCount() != 0) return false;
        if (guide == null || !bondedOrbitals.Contains(guide) || guide.Bond != null) return false;
        if (guide.transform.parent != transform) return false;
        if (guide.ElectronCount > 0) return false;
        var nb = bondedOrbitals.Where(o => o != null && o.Bond == null).ToList();
        if (nb.Count != 4 || !nb.Contains(guide)) return false;
        var movers = nb.Where(o => o != guide && o.ElectronCount > 0).ToList();
        if (movers.Count != 3) return false;

        Vector3 refLocal = transform.InverseTransformDirection(refWorld.normalized).normalized;
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        Vector3 planeNormal = ExBondPinOrbitalTipLocalOnNucleus(guide);
        if (planeNormal.sqrMagnitude < 1e-10f) planeNormal = Vector3.forward;
        planeNormal.Normalize();

        var ideal3 = VseprLayout.GetIdealLocalDirections(3);
        Quaternion qPlane = Quaternion.FromToRotation(Vector3.forward, planeNormal);
        var trigDirs = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
            trigDirs.Add((qPlane * ideal3[i]).normalized);

        movers.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
        var perm = FindBestOrbitalToTargetDirsPermutation(movers, trigDirs, bondRadius, this);
        if (perm == null) return false;
        PreferCarbocationGuideTowardRefLocal(perm, movers, refLocal, trigDirs, guide);

        slots = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        idealDirNucleusLocalForApply = new List<Vector3>(4);
        skipApplyCarbonIdealDir = new List<bool>(4);
        for (int i = 0; i < 3; i++)
        {
            var newDir = trigDirs[perm[i]];
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius, movers[i].transform.localRotation);
            slots.Add((movers[i], pos, rot));
            idealDirNucleusLocalForApply.Add(newDir);
            skipApplyCarbonIdealDir.Add(false);
        }
        slots.Add((guide, guide.transform.localPosition, guide.transform.localRotation));
        idealDirNucleusLocalForApply.Add(Vector3.zero);
        skipApplyCarbonIdealDir.Add(true);
        return true;
    }

    /// <summary>
    /// Carbocation trigonal: 1×0e + 3 occupied domains (σ and/or occupied nucleus non-bonds). σN==0 pins the empty; three occupied in plane ⊥ empty axis.
    /// σN&gt;0: σ+occupied framework defines a plane; three occupied match trigonal directions in that plane; empty ⊥ plane (sign from current empty tip), not along ex-bond ref.
    /// Carbon only.
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
        if (AtomicNumber != 6) return false;
        int sigmaN = GetDistinctSigmaNeighborCount();
        if (sigmaN == 0 && TryComputeSigmaNZeroExBondGuideTrigonalBondBreakSlots(
                refWorld, bondBreakGuideLoneOrbital, out slots, out idealDirNucleusLocalForApply, out skipApplyCarbonIdealDir))
        {
            if (DebugLogCarbonBreakIdealFrameTrace)
                LogCarbonBreakIdealFrameTrace($"TryComputeCarbocationBondBreakSlots OK branch=sigmaN0_exBondGuideTrigonal parentId={GetInstanceID()}");
            return true;
        }

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
                pinEmptyUnmoved = false;
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
            transform.position, neighborsOrdered, worldDirs, newDirs, this, out var targets, null);
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
        if (!TryComputeCarbocationBondBreakSlots(
                refWorld, bondBreakGuideLoneOrbital, out var slots, out var idealDirs, out var skipIdeal)
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

    void TryRelaxSigmaNeighborsOpenedFromLinear3D(Vector3 refLocalNormalized, HashSet<AtomFunction> pinWorld = null)
    {
        if (!TryComputeOpenedTrigonalSigmaNeighborRelaxTargetsFromLinear(refLocalNormalized, out var targets, pinWorld) || targets == null) return;

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

    void RedistributeOrbitals3D(float? piBondAngleOverride, Vector3? refBondWorldDirection, bool relaxCoplanarSigmaToTetrahedral = false, bool skipLoneLobeLayout = false, HashSet<AtomFunction> pinAtomsForSigmaRelax = null, bool skipSigmaNeighborRelax = false, ElectronOrbitalFunction bondBreakGuideLoneOrbital = null, AtomFunction newSigmaBondPartnerHint = null, int sigmaNeighborCountBeforeHint = -1, bool skipBondBreakSparseNonbondSpread = false)
    {
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1)
        {
            LogVsepr3D($"skip atom={name} Z={atomicNumber}: maxSlots<=1");
            return;
        }

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
            MaybeApplyTetrahedralSigmaRelaxForBondFormation(newSigmaBondPartnerHint, sigmaNeighborCountBeforeHint, pinAtomsForSigmaRelax);

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
                // Carbocation (3σ or 2σ + empty along ref): generic trigonal relax aligns one σ vertex onto refLocal — same axis as the empty p — so skip it; TryApplySp2 places σ neighbors in the trigonal plane.
                if (!IsSp2BondBreakEmptyAlongRefCase() && !IsBondBreakTrigonalPlanarFrameworkCase())
                    TryRelaxSigmaNeighborsToTrigonalPlanar3D(refLocal);
                TryRelaxSigmaNeighborsToLinear3D(refLocal, pinAtomsForSigmaRelax);
                TryRelaxSigmaNeighborsOpenedFromLinear3D(refLocal, pinAtomsForSigmaRelax);
            }
            else
            {
                TryRelaxSigmaNeighborsToTrigonalPlanar3D(refLocal, pinAtomsForSigmaRelax);
                TryRelaxSigmaNeighborsToLinear3D(refLocal, pinAtomsForSigmaRelax);
            }
        }

        // σ-only bond break (3σ + radical/empty shell, no 2e lone pairs on non-bonds): tetrahedral / trigonal-carbocation σ relax (CH₃–CH₃ style).
        // When skipSigmaNeighborRelax=true, animation or instant break already applied the same targets via BuildSigmaRelaxMoves — running
        // TryApplySp2 again remaps σ→ideal (FindBestDirectionMapping) and teleports H while guide lobes stay put (looks like the ex-bond lobe flipped vs σ).
        if (relaxCoplanarSigmaToTetrahedral && !skipSigmaNeighborRelax)
            TryApplySp2BondBreakTrigonalPlanarSigmaNeighborRelax3D(refBondWorldDirection, pinAtomsForSigmaRelax);

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
        var newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));

        LogVsepr3D(
            $"atom={name} Z={atomicNumber} σAxes={bondAxes.Count} lone={loneOrbitals.Count} domainCount={domainCount} maxValenceSlots={maxSlots} relaxσ→tet={relaxCoplanarSigmaToTetrahedral}");

        ElectronOrbitalFunction pin = bondBreakGuideLoneOrbital != null && loneOrbitals.Contains(bondBreakGuideLoneOrbital) ? bondBreakGuideLoneOrbital : null;
        var loneMatch = pin != null ? loneOrbitals.Where(o => o != pin).ToList() : loneOrbitals;

        bool tryOk = TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, domainCount, bondAxes, loneOrbitals, newDirs, out var bestMapping, out var pinReservedDir, out var bondIdealLocks, pin);
        if (!tryOk || bestMapping == null || bestMapping.Count != loneMatch.Count)
        {
            LogVsepr3D(
                $"NO APPLY atom={name} Z={atomicNumber}: TryMatch={tryOk} mappingNull={bestMapping == null} mapCount={(bestMapping != null ? bestMapping.Count : -1)} loneMatchCount={loneMatch.Count}");
            return;
        }

        var applyDirs = new List<Vector3>(bestMapping.Count);
        for (int i = 0; i < bestMapping.Count; i++)
            applyDirs.Add(bestMapping[i].newDir);
        if ((bondBreakGuideLoneOrbital != null || refBondWorldDirection.HasValue) && loneMatch.Count > 1)
            RematchLoneOrbitalTargetDirectionsMinAngularMotion(loneMatch, applyDirs);

        for (int i = 0; i < bestMapping.Count; i++)
        {
            var orb = loneMatch[i];
            if (orb == null) continue;
            var newDir = applyDirs[i];
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius, orb.transform.localRotation);
            orb.transform.localPosition = pos;
            orb.transform.localRotation = rot;
            LogVsepr3D(
                $"APPLIED atom={name} Z={atomicNumber} loneIdx={i} orbId={orb.GetInstanceID()} newDir={newDir}");
        }

        if (pin != null && pinReservedDir.HasValue)
        {
            var (pPos, pRot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(pinReservedDir.Value, bondRadius, pin.transform.localRotation);
            pin.transform.localPosition = pPos;
            pin.transform.localRotation = pRot;
            LogVsepr3D(
                $"APPLIED pin atom={name} Z={atomicNumber} orbId={pin.GetInstanceID()} newDir={pinReservedDir.Value}");
        }

        SyncSigmaBondOrbitalTipsFromLocks(bondIdealLocks);
    }

    void ClearSigmaBondOrbitalRedistributionDeltaWhereAuthoritative()
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;
        foreach (var b in covalentBonds)
            b?.ResetOrbitalRedistributionDeltaIfAuthoritative(this);
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

    /// <summary>After VSEPR, place each 0e non-bonded lobe. Occupied lone + σ define the framework; 0e lobes sit ⊥ that framework.
    /// σ cleavage: carbocation-style shells (<see cref="IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase"/>) still use ⊥ framework, not along ex-bond ref.
    /// π component break (σ still between the atoms): remaining σ + other electrons guide layout; empty is placed ⊥ that framework.
    /// Multiple empties: each gets a slot ⊥ electron-containing orbitals and separated from other empties.
    /// <paramref name="placeEmptyAlongBondBreakRef"/> is legacy for callers; along-ref snapping for carbocation-style shells is not used.
    /// <paramref name="skipOrbital"/> excludes a lobe (e.g. bond-break orbital already placed by preview/animation) from being re-oriented here.</summary>
    public void OrientEmptyNonbondedOrbitalsPerpendicularToFramework(Vector3? bondBreakRefWorldDirection = null, ElectronOrbitalFunction skipOrbital = null, bool placeEmptyAlongBondBreakRef = true)
    {
        var empty = bondedOrbitals.Where(o => o != null && o.Bond == null && o.ElectronCount == 0 && (skipOrbital == null || o != skipOrbital)).ToList();
        if (empty.Count == 0)
            return;

        if (DebugLogCcBondBreakGeometry && !placeEmptyAlongBondBreakRef)
            LogCcBondBreak($"OrientEmpty atom={name}: placeEmptyAlongBondBreakRef=false (σ still between pair) — empty uses ⊥ electron+σ framework if applicable");

        bool refOk = bondBreakRefWorldDirection.HasValue && bondBreakRefWorldDirection.Value.sqrMagnitude >= 0.01f;
        if (DebugLogBondBreakEmptyTeleport)
        {
            LogBondBreakEmptyTeleportLine(
                $"[break-empty-teleport] OrientEmpty enter atom={name} id={GetInstanceID()} Z={atomicNumber} emptyCount={empty.Count} refOk={refOk} placeAlongRef={placeEmptyAlongBondBreakRef} " +
                $"carbStyleEmpty={IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(skipOrbital)} σN={GetDistinctSigmaNeighborCount()}");
            LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/before", bondBreakRefWorldDirection, skipOrbital);
        }

        // No π bonds on this center (ex-bond ref): released σ lobe may be pinned — do not re-aim that empty via ⊥-framework math when it matches the guide (TrySpread already placed other e⁻ ⊥ pin).
        if (refOk && GetPiBondCount() == 0 && empty.Count == 1)
        {
            var onlyEmpty = empty[0];
            bool skipPinnedReleased =
                (skipOrbital != null && onlyEmpty == skipOrbital)
                || (TryGetCarbocationOneEmptyAndThreeOccupiedDomains(skipOrbital, out var guideEmpty, out _) && onlyEmpty == guideEmpty);
            if (skipPinnedReleased)
            {
                if (DebugLogCcBondBreakGeometry)
                    LogCcBondBreak($"OrientEmpty atom={name}: π=0 σ-break — skip moving pinned released 0e ex-bond lobe");
                if (DebugLogBondBreakEmptyTeleport)
                {
                    LogBondBreakEmptyTeleportLine($"[break-empty-teleport] OrientEmpty branch=skipPinnedSigmaZeroExBondEmpty atom={name}");
                    LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/after-skipPinnedSigmaZeroExBondEmpty", bondBreakRefWorldDirection, skipOrbital);
                }
                return;
            }
        }

        var electronDirs = new List<Vector3>();
        foreach (var o in bondedOrbitals)
        {
            if (o == null || o.Bond != null || o.ElectronCount <= 0) continue;
            electronDirs.Add(OrbitalTipLocalDirection(o).normalized);
        }
        if (!TryAppendCarbocationFrameworkSigmaLobeTips(electronDirs, skipOrbital, null))
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
            if (!TryComputePerpendicularEmptySlotFromFrameworkDirs(dirs, bondRadius, out var pos, out var rot)) continue;
            e.transform.localPosition = pos;
            e.transform.localRotation = rot;
        }
        if (DebugLogBondBreakEmptyTeleport)
        {
            LogBondBreakEmptyTeleportLine($"[break-empty-teleport] OrientEmpty branch=perpFramework atom={name}");
            LogEmptyNonbondSnapshotForBondBreak("OrientEmpty/after-perpFramework", bondBreakRefWorldDirection, skipOrbital);
        }
        if (GetDistinctSigmaNeighborCount() == 3
            && IsCarbocationStyleBondBreakThreeDomainsWithEmptyCase(skipOrbital))
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

        bool refOkAnim = bondBreakRefWorldDirection.HasValue && bondBreakRefWorldDirection.Value.sqrMagnitude >= 0.01f;
        if (refOkAnim && GetPiBondCount() == 0 && empties.Count == 1
            && TryGetCarbocationOneEmptyAndThreeOccupiedDomains(bondBreakGuideOnThisAtom, out var animGuideEmpty, out _)
            && empties[0] == animGuideEmpty)
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
    /// </summary>
    void AppendEmptyNonbondRedistributeTargetsForSigmaFramework(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> result,
        IReadOnlyList<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> occupiedPredictedEnds = null)
    {
        if (result == null) return;
        var empties = bondedOrbitals.Where(o => o != null && o.Bond == null && o.ElectronCount == 0).OrderBy(o => o.GetInstanceID()).ToList();
        if (empties.Count == 0) return;

        const float posEps = 0.0004f;
        const float rotEps = 0.35f;

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

    /// <returns>perm where orb <c>i</c> is assigned target direction <c>targetDirs[perm[i]]</c>.</returns>
    /// <param name="tipSpaceNucleus">When set, old tip directions for cost are expressed in this nucleus's local space (σ orbitals on bonds). Quaternion slot cost is skipped for bond-parented orbitals (targets are nucleus-local).</param>
    static int[] FindBestOrbitalToTargetDirsPermutation(List<ElectronOrbitalFunction> orbs, List<Vector3> targetDirs, float bondRadius, AtomFunction tipSpaceNucleus = null)
    {
        int n = orbs.Count;
        if (n != targetDirs.Count || n == 0) return null;
        int[] idx = Enumerable.Range(0, n).ToArray();
        float bestTotal = float.MaxValue;
        int[] bestPerm = null;
        const float eps = 1e-3f;
        bool full3D = OrbitalAngleUtility.UseFull3DOrbitalGeometry;
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

        foreach (var perm in Permutations(idx))
        {
            float total;
            float quatSum = 0f;
            for (int i = 0; i < n; i++)
            {
                if (tipSpaceNucleus != null && orbs[i] != null && orbs[i].Bond != null)
                    continue;
                quatSum += QuaternionSlotCostOnly(orbs[i], targetDirs[perm[i]], bondRadius);
            }
            if (full3D)
            {
                float coneSum = 0f;
                for (int i = 0; i < n; i++)
                {
                    Vector3 td = targetDirs[perm[i]].normalized;
                    Vector3 ot = tipSpaceNucleus != null
                        ? tipSpaceNucleus.OrbitalTipDirectionInNucleusLocal(orbs[i])
                        : OrbitalTipLocalDirection(orbs[i]).normalized;
                    coneSum += Vector3.Angle(ot, td);
                }
                float planarSum = PlanarAnglePermutationCostMinOfThreeSums(oldDeg, tgtDeg, perm);
                total = Mathf.Min(coneSum, planarSum) + quatSum;
            }
            else
                total = PlanarAnglePermutationCostMinOfThreeSums(oldDeg, tgtDeg, perm) + quatSum;
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
                    coneSum += Vector3.Angle(ot, nt);
                }
                float planarSum = PlanarAnglePermutationCostMinOfThreeSums(oldDeg, slotDeg, perm);
                total = Mathf.Min(coneSum, planarSum) + quatSum;
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
                float planarSum = PlanarAnglePermutationCostMinOfThreeSums(oldDeg, tgtDeg, perm);
                total = Mathf.Min(coneSum, planarSum) + quatSum;
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

        // Bond-break carbocation slots before the no–lone-early-return: CH₃⁺ (4 non-bonds) and CH₂⁺ (2 σ, 1 empty).
        if (useBreakRefs && refBondWorldDirectionForBreakTargets.HasValue && refBondWorldDirectionForBreakTargets.Value.sqrMagnitude >= 0.01f
            && TryComputeCarbocationBondBreakSlots(
                refBondWorldDirectionForBreakTargets.Value, bondBreakGuideLoneOrbitalForTargets, out var carbSlots, out _, out _)
            && carbSlots != null && carbSlots.Count > 0)
            return carbSlots;
        if (useBreakRefs && refBondWorldDirectionForBreakTargets.HasValue && refBondWorldDirectionForBreakTargets.Value.sqrMagnitude >= 0.01f
            && TryComputeTwoSigmaTrigonalBondBreakSlots(refBondWorldDirectionForBreakTargets.Value, out var ch2Slots, bondBreakGuideLoneOrbitalForTargets)
            && ch2Slots != null && ch2Slots.Count > 0)
            return ch2Slots;

        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null && orb.ElectronCount > 0).ToList();
        if (loneOrbitals.Count == 0)
        {
            if (useBreakRefs && refBondWorldDirectionForBreakTargets.HasValue
                && refBondWorldDirectionForBreakTargets.Value.sqrMagnitude >= 0.01f
                && TryComputeRigidSigmaCleavageOrbitalRedistributeSlots(
                    refBondWorldDirectionForBreakTargets.Value,
                    bondBreakGuideLoneOrbitalForTargets,
                    out var rigidBreakSlots)
                && rigidBreakSlots != null && rigidBreakSlots.Count > 0)
                return rigidBreakSlots;
            // CH₃⁺-style → 4th σ: no occupied lone for TryMatch, but 0e lobes must animate ⊥ new tetrahedral σ framework.
            if (!useBreakRefs)
            {
                bool sigmaIncreased = sigmaNeighborCountBefore >= 0 && GetDistinctSigmaNeighborCount() > sigmaNeighborCountBefore;
                if (newBondPartner != null || sigmaIncreased)
                    AppendEmptyNonbondRedistributeTargetsForSigmaFramework(result);
            }
            return result;
        }

        var bondAxes = CollectSigmaBondAxesLocalMerged(mergeToleranceDeg);
        int slotCount = GetVseprSlotCount3D(bondAxes.Count, loneOrbitals.Count);

        var idealRaw = VseprLayout.GetIdealLocalDirections(slotCount);
        var newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));

        ElectronOrbitalFunction pin = bondBreakGuideLoneOrbitalForTargets != null && loneOrbitals.Contains(bondBreakGuideLoneOrbitalForTargets) ? bondBreakGuideLoneOrbitalForTargets : null;
        var loneMatch = pin != null ? loneOrbitals.Where(o => o != pin).ToList() : loneOrbitals;

        if (!TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, slotCount, bondAxes, loneOrbitals, newDirs, out var bestMapping, out var pinReservedDir, out _, pin) ||
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

        if (!useBreakRefs && (newBondPartner != null || (sigmaNeighborCountBefore >= 0 && GetDistinctSigmaNeighborCount() > sigmaNeighborCountBefore)))
            AppendEmptyNonbondRedistributeTargetsForSigmaFramework(result, result);

        if (useBreakRefs && result.Count > 1)
            RematchRedistributeTargetSlotsMinAngularMotion(result, pin);

        if (useBreakRefs && result.Count > 0)
        {
            for (int i = 0; i < result.Count; i++)
            {
                var (o, _, r) = result[i];
                if (o == null || o.transform.parent != transform) continue;
                Vector3 d = (r * Vector3.right).normalized;
                var (p2, r2) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(d, bondRadius, o.transform.localRotation);
                result[i] = (o, p2, r2);
            }
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

    /// <summary>
    /// During σ bond formation, rotate shared σ lobes on <see cref="CovalentBond"/> to match ideal σ-vertices from <see cref="TryMatchLoneOrbitalsToFreeIdealDirections"/> (same locks as <see cref="GetRedistributeTargets3D"/>).
    /// Call after lone lobes reflect the desired layout — once after <see cref="ApplyRedistributeTargets"/>, or each frame while step 2 lerps so <see cref="CovalentBond.GetOrbitalTargetWorldState"/> picks up <c>orbitalRedistributionWorldDelta</c>.
    /// </summary>
    public void RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(AtomFunction partnerAlongNewSigmaBond)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) return;
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1) return;

        float mergeToleranceDeg = 360f / (2f * maxSlots);
        Vector3 refLocal = RedistributeReferenceDirectionLocalForTargets(partnerAlongNewSigmaBond);
        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;
        else refLocal.Normalize();

        ClearSigmaBondOrbitalRedistributionDeltaWhereAuthoritative();

        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null && orb.ElectronCount > 0).ToList();
        var bondAxes = CollectSigmaBondAxesLocalMerged(mergeToleranceDeg);
        int domainCount = GetVseprSlotCount3D(bondAxes.Count, loneOrbitals.Count);
        if (domainCount < 1) return;

        var idealRaw = VseprLayout.GetIdealLocalDirections(domainCount);
        var newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));

        if (!TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, domainCount, bondAxes, loneOrbitals, newDirs, out _, out _, out var bondIdealLocks, null))
            return;

        SyncSigmaBondOrbitalTipsFromLocks(bondIdealLocks);
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
