using System.Collections.Generic;
using UnityEngine;

/// <summary>Edit-mode toolbar functional groups: attach to a free orbital or replace selected hydrogen.</summary>
public enum FunctionalGroupKind
{
    AmineNH2,
    Hydroxyl,
    Methoxy,
    Aldehyde,
    Carboxyl,
    Sulfo,
    Nitrile,
    Nitro,
    PhosphateDihydrogen
}

/// <summary>
/// Creates preset molecules: cycloalkanes (C3-C6), benzene. Uses programmatic bond formation.
/// </summary>
public class MoleculeBuilder : MonoBehaviour
{
    [SerializeField] GameObject atomPrefab;
    [SerializeField] float viewportMargin = 0.1f;

    /// <summary>Bond length matches manual bonding: 2 * (bondRadius * 0.6) = 1.2 * bondRadius, from GetCanonicalSlotPosition in ElectronOrbitalFunction.</summary>
    float GetBondLength() => atomPrefab != null && atomPrefab.TryGetComponent<AtomFunction>(out var a) ? 1.2f * a.BondRadius : 0.96f;

    /// <summary>Ring radius for regular n-gon with chord length = bondLength. R = bondLength / (2 * sin(π/n)).</summary>
    static float GetRingRadius(int ringSize, float bondLength) =>
        bondLength / (2f * Mathf.Sin(Mathf.PI / ringSize));

    static void RescaleRingEdges(Vector3[] pts, Vector3 center, float targetEdge)
    {
        if (pts == null || pts.Length < 2) return;
        int n = pts.Length;
        float sum = 0f;
        for (int i = 0; i < n; i++)
            sum += Vector3.Distance(pts[i], pts[(i + 1) % n]);
        float avg = sum / n;
        if (avg < 1e-6f) return;
        float s = targetEdge / avg;
        for (int i = 0; i < n; i++)
            pts[i] = center + (pts[i] - center) * s;
    }

    static Vector3[] CycloalkaneCarbonPositionsPlanar(int ringSize, float bondLength, Vector3 center, float rotOffsetDeg)
    {
        float ringRadius = GetRingRadius(ringSize, bondLength);
        var pts = new Vector3[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            float angleDeg = ringSize == 4
                ? 45f + 90f * i
                : 360f * i / ringSize - 90f;
            angleDeg += rotOffsetDeg;
            float angle = angleDeg * Mathf.Deg2Rad;
            pts[i] = center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * ringRadius;
        }
        return pts;
    }

    /// <summary>Realistic 3D puckering: C3 shallow pyramid, C4 butterfly, C5 envelope, C6 chair.</summary>
    static Vector3[] CycloalkaneCarbonPositions3D(int ringSize, float bondLength, Vector3 center)
    {
        switch (ringSize)
        {
            case 3:
                return PuckeredCyclopropane(bondLength, center);
            case 4:
                return PuckeredCyclobutane(bondLength, center);
            case 5:
                return EnvelopeCyclopentane(bondLength, center);
            case 6:
                return ChairCyclohexane(bondLength, center);
            default:
                return CycloalkaneCarbonPositionsPlanar(ringSize, bondLength, center, 0f);
        }
    }

    /// <summary>Slight non-planarity so each C—C—C plane has a distinct normal (improves out-of-plane H).</summary>
    static Vector3[] PuckeredCyclopropane(float bondLength, Vector3 center)
    {
        float r = GetRingRadius(3, bondLength);
        float lift = bondLength * 0.14f;
        var pts = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            float ang = (360f * i / 3f - 90f) * Mathf.Deg2Rad;
            float z = i == 0 ? lift : (i == 1 ? -lift * 0.45f : -lift * 0.55f);
            pts[i] = center + new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, z);
        }
        RescaleRingEdges(pts, center, bondLength);
        return pts;
    }

    static Vector3[] PuckeredCyclobutane(float bondLength, Vector3 center)
    {
        float r = bondLength * 0.74f;
        float d = bondLength * 0.22f;
        var pts = new Vector3[4];
        float[] zs = { d, -d, d, -d };
        for (int i = 0; i < 4; i++)
        {
            float ang = (45f + 90f * i) * Mathf.Deg2Rad;
            pts[i] = center + new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, zs[i]);
        }
        RescaleRingEdges(pts, center, bondLength);
        return pts;
    }

    static Vector3[] EnvelopeCyclopentane(float bondLength, Vector3 center)
    {
        int n = 5;
        float r = bondLength / (2f * Mathf.Sin(Mathf.PI / n));
        float flap = bondLength * 0.35f;
        float ripple = bondLength * 0.08f;
        var pts = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float ang = (360f * i / n - 90f) * Mathf.Deg2Rad;
            float z = (i == 2 ? flap : 0f) + Mathf.Sin(i * 2f * Mathf.PI / n) * ripple;
            pts[i] = center + new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, z);
        }
        RescaleRingEdges(pts, center, bondLength);
        return pts;
    }

    static Vector3[] ChairCyclohexane(float bondLength, Vector3 center)
    {
        // Alternating ±h on a regular hexagon (radius r): all ring edges have length √(r²+4h²), and
        // cos(C–C–C) = (−½r²+4h²)/(r²+4h²). Tetrahedral 109.47° (cos = −⅓) ⇔ (h/r)² = 1/32.
        float r = bondLength * 0.72f;
        float h = r * Mathf.Sqrt(1f / 32f);
        var pts = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float ang = (60f * i - 90f) * Mathf.Deg2Rad;
            float z = i % 2 == 0 ? h : -h;
            pts[i] = center + new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, z);
        }
        RescaleRingEdges(pts, center, bondLength);
        return pts;
    }

    void Awake()
    {
        if (atomPrefab == null)
        {
            var quickAdd = GetComponent<AtomQuickAddUI>();
            if (quickAdd != null)
                atomPrefab = quickAdd.GetAtomPrefab();
        }
    }

    public void SetAtomPrefab(GameObject prefab) => atomPrefab = prefab;

    /// <param name="attachToSelectedOrbital">
    /// Perspective (3D): bond ring to edit selection only when true (e.g. Shift+click). Orthographic (2D): restores legacy behavior — attach whenever selection has a free 1e orbital, ignoring this flag.
    /// </param>
    public void CreateCycloalkane(int ringSize, bool attachToSelectedOrbital = false)
    {
        if (ringSize < 3 || ringSize > 6 || atomPrefab == null || Camera.main == null) return;

        float bondLength = GetBondLength();
        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        AtomFunction anchorAtom = null;
        ElectronOrbitalFunction anchorOrbital = null;

        bool use3DRing = OrbitalAngleUtility.UseFull3DOrbitalGeometry;
        bool canAnchor = editMode != null && editMode.SelectedAtom != null && editMode.SelectedOrbital != null
            && editMode.SelectedOrbital.Bond == null && editMode.SelectedOrbital.ElectronCount == 1;
        // 2D: always fuse to selection when possible (previous toolbar behavior). 3D: only with explicit attach (Shift).
        bool anchoredToSelection = canAnchor && (!use3DRing || attachToSelectedOrbital);
        Vector3 center = GetViewportCenter();
        Vector3[] carbonPos = use3DRing
            ? CycloalkaneCarbonPositions3D(ringSize, bondLength, center)
            : CycloalkaneCarbonPositionsPlanar(ringSize, bondLength, center, 0f);

        if (anchoredToSelection)
        {
            anchorAtom = editMode.SelectedAtom;
            anchorOrbital = editMode.SelectedOrbital;
            Vector3 dirOrb = anchorOrbital.transform.TransformDirection(Vector3.right).normalized;
            if (dirOrb.sqrMagnitude < 1e-6f) dirOrb = Vector3.right;
            if (!use3DRing)
            {
                dirOrb.z = 0f;
                if (dirOrb.sqrMagnitude < 1e-8f) dirOrb = Vector3.right;
                else dirOrb.Normalize();
            }

            carbonPos = use3DRing
                ? CycloalkaneCarbonPositions3D(ringSize, bondLength, Vector3.zero)
                : CycloalkaneCarbonPositionsPlanar(ringSize, bondLength, Vector3.zero, 0f);
            Vector3 p0 = carbonPos[0];
            Vector3 edge01 = (carbonPos[1] - p0).normalized;
            if (edge01.sqrMagnitude > 1e-8f)
            {
                Quaternion align = Quaternion.FromToRotation(edge01, dirOrb);
                for (int i = 0; i < ringSize; i++)
                    carbonPos[i] = align * (carbonPos[i] - p0) + p0;
            }
            Vector3 targetFirstC = anchorAtom.transform.position + dirOrb * bondLength;
            Vector3 delta = targetFirstC - carbonPos[0];
            for (int i = 0; i < ringSize; i++)
                carbonPos[i] += delta;
        }

        var atoms = new AtomFunction[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            var go = Instantiate(atomPrefab, carbonPos[i], Quaternion.identity);
            if (go.TryGetComponent<AtomFunction>(out var atom))
            {
                atom.AtomicNumber = 6;
                atom.ForceInitialize();
                atoms[i] = atom;
            }
        }

        for (int i = 0; i < ringSize; i++)
        {
            int next = (i + 1) % ringSize;
            FormSigmaBondInstant(atoms[i], atoms[next]);
        }

        if (anchorAtom != null && anchorOrbital != null && atoms[0] != null)
        {
            Vector3 dirToAnchor = (anchorAtom.transform.position - atoms[0].transform.position).normalized;
            var orb0 = atoms[0].GetLoneOrbitalWithOneElectron(dirToAnchor);
            if (orb0 != null)
                FormSigmaBondInstant(anchorAtom, atoms[0], anchorOrbital, orb0);
        }

        foreach (var a in atoms)
            if (a != null) a.RedistributeOrbitals();
        if (anchorAtom != null) anchorAtom.RedistributeOrbitals();

        if (editMode != null)
        {
            editMode.SaturateCycloalkaneWithHydrogen(atoms);
            editMode.RefreshSelectedMoleculeAfterBondChange();
        }

        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    public void CreateBenzene()
    {
        if (atomPrefab == null || Camera.main == null) return;

        float bondLength = GetBondLength();
        float ringRadius = GetRingRadius(6, bondLength);
        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        AtomFunction anchorAtom = null;
        ElectronOrbitalFunction anchorOrbital = null;
        Vector3 center;

        if (editMode != null && editMode.SelectedAtom != null && editMode.SelectedOrbital != null
            && editMode.SelectedOrbital.Bond == null && editMode.SelectedOrbital.ElectronCount == 1)
        {
            anchorAtom = editMode.SelectedAtom;
            anchorOrbital = editMode.SelectedOrbital;
            Vector3 dir = anchorOrbital.transform.TransformDirection(Vector3.right);
            float angle0 = -90f * Mathf.Deg2Rad; // atoms[0] at top
            Vector3 atom0Pos = anchorAtom.transform.position + dir * bondLength;
            center = atom0Pos - ringRadius * new Vector3(Mathf.Cos(angle0), Mathf.Sin(angle0), 0);
        }
        else
        {
            center = GetViewportCenter();
        }

        var atoms = new AtomFunction[6];
        for (int i = 0; i < 6; i++)
        {
            float angle = (60f * i - 90f) * Mathf.Deg2Rad;
            Vector3 pos = center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * ringRadius;
            var go = Instantiate(atomPrefab, pos, Quaternion.identity);
            if (go.TryGetComponent<AtomFunction>(out var atom))
            {
                atom.AtomicNumber = 6;
                atom.ForceInitialize();
                atoms[i] = atom;
            }
        }

        // Full σ framework, then all π (Kekulé doubles on 0–1, 2–3, 4–5), then one redistribute.
        // Deferring per-bond redistribution keeps the first VSEPR pass from treating ring C as sp³;
        // with π present, orbitals settle as trigonal planar so H-auto stays in the ring plane.
        for (int i = 0; i < 6; i++)
        {
            int next = (i + 1) % 6;
            FormSigmaBondInstant(atoms[i], atoms[next], redistributeEndpoints: false);
        }

        if (anchorAtom != null && anchorOrbital != null && atoms[0] != null)
        {
            Vector3 dirToAnchor = (anchorAtom.transform.position - atoms[0].transform.position).normalized;
            var orb0 = atoms[0].GetLoneOrbitalWithOneElectron(dirToAnchor);
            if (orb0 != null)
                FormSigmaBondInstant(anchorAtom, atoms[0], anchorOrbital, orb0, redistributeEndpoints: false);
        }

        for (int i = 0; i < 3; i++)
        {
            int a = i * 2;
            int b = (a + 1) % 6;
            FormPiBondInstant(atoms[a], atoms[b], redistributeEndpoints: false);
        }

        foreach (var a in atoms)
            if (a != null) a.RedistributeOrbitals();
        if (anchorAtom != null) anchorAtom.RedistributeOrbitals();

        ElectronOrbitalFunction.LogPiRedistDebug($"CreateBenzene: batch RedistributeOrbitals on ring + anchor (π formed with redistributeEndpoints=false)");

        if (editMode != null)
        {
            foreach (var a in atoms)
                if (a != null) editMode.SaturateWithHydrogen(a);
            editMode.RefreshSelectedMoleculeAfterBondChange();
        }

        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    /// <summary>Spawns only an electron at the work-plane (same prefab as orbital lone pairs), for 3D electron tests.</summary>
    public void CreateFreeElectronAtViewport()
    {
        if (atomPrefab == null || Camera.main == null) return;
        if (!atomPrefab.TryGetComponent<AtomFunction>(out var atomFn) || atomFn.OrbitalPrefab == null) return;
        var ep = atomFn.OrbitalPrefab.ElectronPrefab;
        if (ep == null) return;

        Vector3 pos = GetViewportCenter() + new Vector3(0.18f, 0.1f, 0f);
        Instantiate(ep, pos, Quaternion.identity);
        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    void FormSigmaBondInstant(AtomFunction atomA, AtomFunction atomB, bool redistributeEndpoints = true)
    {
        if (atomA == null || atomB == null) return;
        var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
        var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;

        var orbA = atomA.GetLoneOrbitalWithOneElectron(dirAtoB);
        var orbB = atomB.GetLoneOrbitalWithOneElectron(dirBtoA);
        if (orbA == null || orbB == null) return;

        FormSigmaBondInstant(atomA, atomB, orbA, orbB, redistributeEndpoints);
    }

    void FormSigmaBondInstant(AtomFunction atomA, AtomFunction atomB, ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB, bool redistributeEndpoints = true)
    {
        if (atomA == null || atomB == null || orbA == null || orbB == null) return;

        int merged = orbA.ElectronCount + orbB.ElectronCount;
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            if (redistributeEndpoints)
            {
                atomA.RedistributeOrbitals();
                atomB.RedistributeOrbitals();
            }
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    void FormPiBondInstant(AtomFunction atomA, AtomFunction atomB, bool redistributeEndpoints = true)
    {
        if (atomA == null || atomB == null) return;
        if (atomA.GetBondsTo(atomB) != 1) return;

        int piA0 = atomA.GetPiBondCount();
        int piB0 = atomB.GetPiBondCount();
        ElectronOrbitalFunction.LogPiRedistDebug($"FormPiBondInstant: Z={atomA.AtomicNumber}↔Z={atomB.AtomicNumber} π before {piA0}/{piB0}, redistributeEndpoints={redistributeEndpoints}");

        var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
        var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;

        var orbA = atomA.GetLoneOrbitalWithOneElectron(dirAtoB);
        var orbB = atomB.GetLoneOrbitalWithOneElectron(dirBtoA);
        if (orbA == null || orbB == null) return;

        int merged = orbA.ElectronCount + orbB.ElectronCount;
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            if (redistributeEndpoints)
            {
                atomA.RedistributeOrbitals();
                atomB.RedistributeOrbitals();
            }
            atomA.RefreshCharge();
            atomB.RefreshCharge();
            ElectronOrbitalFunction.LogPiRedistDebug($"FormPiBondInstant done: Z={atomA.AtomicNumber} π={atomA.GetPiBondCount()} Z={atomB.AtomicNumber} π={atomB.GetPiBondCount()} (redistributed={(redistributeEndpoints ? "per-atom" : "deferred")})");
        }
    }

    /// <summary>Second π between the same pair (σ + π already present) for approximate triple bonds.</summary>
    void FormSecondPiBondInstant(AtomFunction atomA, AtomFunction atomB, bool redistributeEndpoints = true)
    {
        if (atomA == null || atomB == null) return;
        if (atomA.GetBondsTo(atomB) != 2) return;

        var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
        var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;

        var orbA = atomA.GetLoneOrbitalWithOneElectron(dirAtoB);
        var orbB = atomB.GetLoneOrbitalWithOneElectron(dirBtoA);
        if (orbA == null || orbB == null) return;

        int merged = orbA.ElectronCount + orbB.ElectronCount;
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            if (redistributeEndpoints)
            {
                atomA.RedistributeOrbitals();
                atomB.RedistributeOrbitals();
            }
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    AtomFunction SpawnAtomElement(int z, Vector3 pos)
    {
        var go = Instantiate(atomPrefab, pos, Quaternion.identity);
        if (!go.TryGetComponent<AtomFunction>(out var a))
        {
            Destroy(go);
            return null;
        }
        a.AtomicNumber = z;
        a.ForceInitialize();
        return a;
    }

    bool BondSigmaNoRedist(AtomFunction a, AtomFunction b)
    {
        Vector3 dAB = (b.transform.position - a.transform.position).normalized;
        Vector3 dBA = -dAB;
        var oa = a.GetLoneOrbitalForBondFormation(dAB);
        var ob = b.GetLoneOrbitalWithOneElectron(dBA);
        if (oa == null || ob == null) return false;
        FormSigmaBondInstant(a, b, oa, ob, false);
        return true;
    }

    /// <param name="refBondWorldForRedist">If set, orients lone orbitals after σ bond (matches <see cref="EditModeManager.AddHydrogenAtDirection"/>).</param>
    bool TryBondHydrogen(AtomFunction heavy, Vector3 dirHeavyToH, Vector3? refBondWorldForRedist = null)
    {
        if (heavy == null || dirHeavyToH.sqrMagnitude < 1e-10f) return false;
        dirHeavyToH.Normalize();
        float L = GetBondLength();
        var ho = heavy.GetLoneOrbitalWithOneElectron(dirHeavyToH);
        if (ho == null) return false;
        var h = SpawnAtomElement(1, heavy.transform.position + dirHeavyToH * L);
        if (h == null) return false;
        var hOrb = h.GetLoneOrbitalWithOneElectron(-dirHeavyToH);
        if (hOrb == null)
        {
            Destroy(h.gameObject);
            return false;
        }
        FormSigmaBondInstant(heavy, h, ho, hOrb, false);
        if (refBondWorldForRedist.HasValue)
        {
            Vector3 refd = refBondWorldForRedist.Value.normalized;
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                heavy.RedistributeOrbitals(refBondWorldDirection: refd);
            else
                heavy.RedistributeOrbitals(piBondAngleOverride: Mathf.Atan2(refd.y, refd.x) * Mathf.Rad2Deg);
        }
        return true;
    }

    /// <summary>One H on a center that already has one σ to a heavy neighbor (e.g. alcohol O): bent ~109°, not colinear with that bond.</summary>
    bool TryBondHydrogenBent(AtomFunction center, Vector3 dirCenterToHeavyNeighbor)
    {
        if (center == null || dirCenterToHeavyNeighbor.sqrMagnitude < 1e-10f) return false;
        Vector3 u1 = dirCenterToHeavyNeighbor.normalized;
        GetAttachBasis(u1, out var perp, out _);
        Vector3 hd1, hd2;
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            VseprLayout.TwoHydrogenDirectionsFromBonds(u1, perp, out hd1, out hd2);
        else
            VseprLayout.TwoHydrogenDirectionsFromBondsPlanarXY(u1, perp, out hd1, out hd2);
        return TryBondHydrogen(center, hd1, refBondWorldForRedist: hd1);
    }

    static void GetAttachBasis(Vector3 dirParentToAnchor, out Vector3 right, out Vector3 up)
    {
        Vector3 f = dirParentToAnchor.normalized;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            f.z = 0f;
            if (f.sqrMagnitude < 1e-8f) f = Vector3.right;
            else f.Normalize();
            right = new Vector3(-f.y, f.x, 0f);
            if (right.sqrMagnitude < 1e-8f) right = Vector3.up;
            else right.Normalize();
            up = Vector3.forward;
            return;
        }
        Vector3 refAxis = Mathf.Abs(Vector3.Dot(f, Vector3.up)) < 0.95f ? Vector3.up : Vector3.right;
        right = Vector3.Cross(refAxis, f).normalized;
        up = Vector3.Cross(f, right).normalized;
    }

    /// <summary>First atom of the group is placed at <paramref name="anchorWorldPos"/> and σ-bonded to <paramref name="parent"/>.</summary>
    public bool BuildFunctionalGroup(FunctionalGroupKind kind, AtomFunction parent, Vector3 anchorWorldPos, EditModeManager edit, out AtomFunction selectionAnchor)
    {
        selectionAnchor = null;
        if (parent == null || atomPrefab == null) return false;

        Vector3 dirOut = anchorWorldPos - parent.transform.position;
        if (dirOut.sqrMagnitude < 1e-10f) return false;
        dirOut.Normalize();

        int z0 = kind switch
        {
            FunctionalGroupKind.AmineNH2 => 7,
            FunctionalGroupKind.Hydroxyl => 8,
            FunctionalGroupKind.Methoxy => 8,
            FunctionalGroupKind.Aldehyde => 6,
            FunctionalGroupKind.Carboxyl => 6,
            FunctionalGroupKind.Sulfo => 16,
            FunctionalGroupKind.Nitrile => 6,
            FunctionalGroupKind.Nitro => 7,
            FunctionalGroupKind.PhosphateDihydrogen => 15,
            _ => 6
        };

        var anchor = SpawnAtomElement(z0, anchorWorldPos);
        if (anchor == null) return false;
        if (!BondSigmaNoRedist(parent, anchor))
        {
            Destroy(anchor.gameObject);
            return false;
        }

        selectionAnchor = anchor;
        float L = GetBondLength();
        var touched = new List<AtomFunction> { parent, anchor };
        GetAttachBasis(dirOut, out var right, out var up);

        bool ok = true;
        switch (kind)
        {
            case FunctionalGroupKind.AmineNH2:
            {
                Vector3 u1 = (parent.transform.position - anchor.transform.position).normalized;
                Vector3 hd1, hd2;
                if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                    VseprLayout.TwoHydrogenDirectionsFromBonds(u1, right, out hd1, out hd2);
                else
                    VseprLayout.TwoHydrogenDirectionsFromBondsPlanarXY(u1, right, out hd1, out hd2);
                ok = TryBondHydrogen(anchor, hd1) && TryBondHydrogen(anchor, hd2);
                break;
            }
            case FunctionalGroupKind.Hydroxyl:
                ok = TryBondHydrogenBent(anchor, parent.transform.position - anchor.transform.position);
                break;
            case FunctionalGroupKind.Methoxy:
            {
                Vector3 oc = anchor.transform.position + right * L;
                var c = SpawnAtomElement(6, oc);
                if (c == null) { ok = false; break; }
                touched.Add(c);
                ok = BondSigmaNoRedist(anchor, c);
                if (ok && edit != null) edit.SaturateWithHydrogen(c);
                break;
            }
            case FunctionalGroupKind.Aldehyde:
            {
                Vector3 cPos = anchor.transform.position;
                var o = SpawnAtomElement(8, cPos + right * L);
                if (o == null) { ok = false; break; }
                touched.Add(o);
                ok = BondSigmaNoRedist(anchor, o);
                if (ok)
                    FormPiBondInstant(anchor, o, false);
                if (ok)
                {
                    Vector3 uR = (parent.transform.position - anchor.transform.position).normalized;
                    Vector3 uO = (o.transform.position - anchor.transform.position).normalized;
                    Vector3 hd1, hd2;
                    if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                        VseprLayout.TwoHydrogenDirectionsFromBonds(uR, uO, out hd1, out hd2);
                    else
                        VseprLayout.TwoHydrogenDirectionsFromBondsPlanarXY(uR, uO, out hd1, out hd2);
                    ok = TryBondHydrogen(anchor, hd1, refBondWorldForRedist: hd1);
                }
                break;
            }
            case FunctionalGroupKind.Carboxyl:
            {
                Vector3 cPos = anchor.transform.position;
                ComputeTrigonalDirsTowardParentForFunctionalGroup(cPos, parent.transform.position, out Vector3 dirCarbonylO, out Vector3 dirHydroxylO);

                var oC = SpawnAtomElement(8, cPos + dirCarbonylO * L);
                var oH = SpawnAtomElement(8, cPos + dirHydroxylO * L);
                if (oC == null || oH == null) { ok = false; break; }
                touched.Add(oC);
                touched.Add(oH);
                ok = BondSigmaNoRedist(anchor, oC) && BondSigmaNoRedist(anchor, oH);
                if (ok)
                {
                    FormPiBondInstant(anchor, oC, false);
                    ok = TryBondHydrogenBent(oH, anchor.transform.position - oH.transform.position);
                }
                break;
            }
            case FunctionalGroupKind.Sulfo:
            {
                Vector3 sPos = anchor.transform.position;
                ComputeTetraThreeOxyDirsTowardParentForSulfo(sPos, parent.transform.position, out Vector3 d1, out Vector3 d2, out Vector3 d3);
                var o1 = SpawnAtomElement(8, sPos + d1 * L);
                var o2 = SpawnAtomElement(8, sPos + d2 * L);
                var o3 = SpawnAtomElement(8, sPos + d3 * L);
                if (o1 == null || o2 == null || o3 == null) { ok = false; break; }
                touched.Add(o1);
                touched.Add(o2);
                touched.Add(o3);
                ok = BondSigmaNoRedist(anchor, o1) && BondSigmaNoRedist(anchor, o2) && BondSigmaNoRedist(anchor, o3);
                if (ok)
                    ok = TryBondHydrogenBent(o3, anchor.transform.position - o3.transform.position);
                break;
            }
            case FunctionalGroupKind.Nitrile:
            {
                var n = SpawnAtomElement(7, anchor.transform.position + dirOut * L);
                if (n == null) { ok = false; break; }
                touched.Add(n);
                ok = BondSigmaNoRedist(anchor, n);
                if (ok)
                {
                    FormPiBondInstant(anchor, n, false);
                    FormSecondPiBondInstant(anchor, n, false);
                }
                break;
            }
            case FunctionalGroupKind.Nitro:
            {
                Vector3 nPos = anchor.transform.position;
                ComputeTrigonalDirsTowardParentForFunctionalGroup(nPos, parent.transform.position, out Vector3 dirO1, out Vector3 dirO2);
                var o1 = SpawnAtomElement(8, nPos + dirO1 * L);
                var o2 = SpawnAtomElement(8, nPos + dirO2 * L);
                if (o1 == null || o2 == null) { ok = false; break; }
                touched.Add(o1);
                touched.Add(o2);
                ok = BondSigmaNoRedist(anchor, o1) && BondSigmaNoRedist(anchor, o2);
                break;
            }
            case FunctionalGroupKind.PhosphateDihydrogen:
            {
                // R—P(=O)(OH)₂: tetrahedral at P (same σ layout as sulfo); two hydroxyl O + one P=O.
                Vector3 pPos = anchor.transform.position;
                ComputeTetraThreeOxyDirsTowardParentForSulfo(pPos, parent.transform.position, out Vector3 d1, out Vector3 d2, out Vector3 d3);
                var o1 = SpawnAtomElement(8, pPos + d1 * L);
                var o2 = SpawnAtomElement(8, pPos + d2 * L);
                var o3 = SpawnAtomElement(8, pPos + d3 * L);
                if (o1 == null || o2 == null || o3 == null) { ok = false; break; }
                touched.Add(o1);
                touched.Add(o2);
                touched.Add(o3);
                ok = BondSigmaNoRedist(anchor, o1) && BondSigmaNoRedist(anchor, o2) && BondSigmaNoRedist(anchor, o3);
                if (ok)
                    FormPiBondInstant(anchor, o3, false);
                if (ok)
                    ok = TryBondHydrogenBent(o1, anchor.transform.position - o1.transform.position)
                        && TryBondHydrogenBent(o2, anchor.transform.position - o2.transform.position);
                break;
            }
        }

        if (!ok)
        {
            selectionAnchor = null;
            return false;
        }

        foreach (var a in touched)
            if (a != null) a.RedistributeOrbitals();
        parent.RedistributeOrbitals();

        AtomFunction.SetupGlobalIgnoreCollisions();
        return true;
    }

    /// <summary>~120° σ directions for two substituents (e.g. nitro O—N—O) given center→parent bond.</summary>
    static void ComputeTrigonalDirsTowardParentForFunctionalGroup(Vector3 centerWorld, Vector3 parentWorld, out Vector3 dirSubA, out Vector3 dirSubB)
    {
        Vector3 uR = parentWorld - centerWorld;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            uR.z = 0f;
            if (uR.sqrMagnitude < 1e-10f) uR = Vector3.right;
            else uR.Normalize();
        }
        else if (uR.sqrMagnitude < 1e-10f)
            uR = Vector3.right;
        else
            uR.Normalize();

        var tri = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(3), uR);
        dirSubA = tri[1].normalized;
        dirSubB = tri[2].normalized;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            ProjectFunctionalGroupDirToXY(ref dirSubA, uR, null);
            ProjectFunctionalGroupDirToXY(ref dirSubB, uR, dirSubA);
        }
    }

    /// <summary>Three tetrahedral σ directions for sulfonate / phosphonate oxygens (avoids colinear R—S—O / R—P—O).</summary>
    static void ComputeTetraThreeOxyDirsTowardParentForSulfo(Vector3 centerWorld, Vector3 parentWorld, out Vector3 d1, out Vector3 d2, out Vector3 d3)
    {
        Vector3 uR = parentWorld - centerWorld;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            uR.z = 0f;
            if (uR.sqrMagnitude < 1e-10f) uR = Vector3.right;
            else uR.Normalize();
        }
        else if (uR.sqrMagnitude < 1e-10f)
            uR = Vector3.right;
        else
            uR.Normalize();

        var tet = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(4), uR);
        d1 = tet[1].normalized;
        d2 = tet[2].normalized;
        d3 = tet[3].normalized;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            ProjectFunctionalGroupDirToXY(ref d1, uR, null);
            ProjectFunctionalGroupDirToXY(ref d2, uR, d1);
            ProjectFunctionalGroupDirToXY(ref d3, uR, d1 + d2);
        }
    }

    static void ProjectFunctionalGroupDirToXY(ref Vector3 v, Vector3 uR, Vector3? sumPrior)
    {
        v.z = 0f;
        if (v.sqrMagnitude > 1e-10f)
        {
            v.Normalize();
            return;
        }
        if (!sumPrior.HasValue)
            v = new Vector3(-uR.y, uR.x, 0f);
        else
        {
            Vector3 s = sumPrior.Value;
            s.z = 0f;
            v = (-uR - s);
        }
        if (v.sqrMagnitude > 1e-10f)
            v.Normalize();
        else
            v = new Vector3(-uR.y, uR.x, 0f).normalized;
    }

    Vector3 GetViewportCenter() => PlanarPointerInteraction.ViewportCenterOnWorkPlane(viewportMargin);
}
