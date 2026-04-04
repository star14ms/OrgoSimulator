using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// When switching orthographic (2D) vs perspective (3D), rebuilds all molecule atoms from the matching atom prefab
/// so embedded orbital/electron prefabs stay consistent. Preserves positions, elements, charges, and σ/π bond multiplicities.
/// </summary>
public static class MoleculeViewPrefabSwap
{
    public static void RebuildAllAtomsIfVariantMismatch(GameObject prefab2D, GameObject prefab3D, GameObject fallbackPrefab)
    {
        var cam = Camera.main;
        if (cam == null) return;

        bool want3D = !cam.orthographic;
        GameObject prefab = want3D ? (prefab3D != null ? prefab3D : fallbackPrefab) : (prefab2D != null ? prefab2D : fallbackPrefab);
        if (prefab == null) return;

        var atoms = Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None);
        if (atoms == null || atoms.Length == 0) return;

        bool anyWrong = false;
        foreach (var a in atoms)
        {
            if (a == null) continue;
            if (!AtomVisualMatchesCameraMode(a, want3D))
            {
                anyWrong = true;
                break;
            }
        }

        if (!anyWrong) return;

        var edit = Object.FindFirstObjectByType<EditModeManager>();
        edit?.OnBackgroundClicked();

        var standaloneElectrons = CollectStandaloneElectrons();

        var atomList = new List<AtomFunction>();
        foreach (var a in atoms)
        {
            if (a != null) atomList.Add(a);
        }

        var idToIndex = new Dictionary<int, int>(atomList.Count);
        for (int i = 0; i < atomList.Count; i++)
            idToIndex[atomList[i].GetInstanceID()] = i;

        var bonds = Object.FindObjectsByType<CovalentBond>(FindObjectsSortMode.None);
        var pairMultiplicity = new Dictionary<(int, int), int>();
        foreach (var b in bonds)
        {
            if (b == null || b.AtomA == null || b.AtomB == null) continue;
            if (!idToIndex.TryGetValue(b.AtomA.GetInstanceID(), out int ia)) continue;
            if (!idToIndex.TryGetValue(b.AtomB.GetInstanceID(), out int ib)) continue;
            if (ia > ib) (ia, ib) = (ib, ia);
            var key = (ia, ib);
            pairMultiplicity.TryGetValue(key, out int c);
            pairMultiplicity[key] = c + 1;
        }

        var snapshots = new List<AtomSnapshot>(atomList.Count);
        foreach (var a in atomList)
        {
            var t = a.transform;
            snapshots.Add(new AtomSnapshot
            {
                position = t.position,
                rotation = t.rotation,
                localScale = t.localScale,
                parent = t.parent,
                atomicNumber = a.AtomicNumber,
                charge = a.Charge
            });
        }

        foreach (var b in bonds)
        {
            if (b != null) Object.Destroy(b.gameObject);
        }

        foreach (var a in atomList)
        {
            if (a != null) Object.Destroy(a.gameObject);
        }

        var newAtoms = new AtomFunction[snapshots.Count];
        for (int i = 0; i < snapshots.Count; i++)
        {
            var s = snapshots[i];
            var go = Object.Instantiate(prefab, s.position, s.rotation, s.parent);
            go.transform.localScale = s.localScale;
            if (!go.TryGetComponent<AtomFunction>(out var af)) continue;
            af.AtomicNumber = s.atomicNumber;
            af.Charge = s.charge;
            af.ForceInitialize();
            newAtoms[i] = af;
        }

        foreach (var kvp in pairMultiplicity)
        {
            int ia = kvp.Key.Item1;
            int ib = kvp.Key.Item2;
            int mult = Mathf.Clamp(kvp.Value, 1, 2);
            var a = newAtoms[ia];
            var b = newAtoms[ib];
            if (a == null || b == null) continue;
            if (mult >= 1)
                TryFormSigmaInstant(a, b);
            if (mult >= 2)
                TryFormPiInstant(a, b);
        }

        foreach (var e in standaloneElectrons)
        {
            if (e == null) continue;
            var t = e.transform;
            Vector3 pos = t.position;
            Quaternion rot = t.rotation;
            Transform parent = t.parent;
            Object.Destroy(e.gameObject);
            var ep = ResolveStandaloneElectronPrefab(prefab);
            if (ep != null)
                Object.Instantiate(ep.gameObject, pos, rot, parent);
        }

        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    static ElectronFunction ResolveStandaloneElectronPrefab(GameObject atomPrefabRoot)
    {
        if (atomPrefabRoot == null) return null;
        if (!atomPrefabRoot.TryGetComponent<AtomFunction>(out var af) || af.OrbitalPrefab == null) return null;
        return af.OrbitalPrefab.ElectronPrefab;
    }

    static List<ElectronFunction> CollectStandaloneElectrons()
    {
        var list = new List<ElectronFunction>();
        foreach (var e in Object.FindObjectsByType<ElectronFunction>(FindObjectsSortMode.None))
        {
            if (e == null) continue;
            if (e.GetComponentInParent<AtomFunction>() != null) continue;
            list.Add(e);
        }
        return list;
    }

    struct AtomSnapshot
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 localScale;
        public Transform parent;
        public int atomicNumber;
        public int charge;
    }

    public static bool AtomVisualMatchesCameraMode(AtomFunction atom, bool wantPerspective3D)
    {
        if (atom == null) return true;
        bool spriteBody = atom.GetComponent<SpriteRenderer>() != null;
        if (wantPerspective3D)
            return !spriteBody;
        return spriteBody;
    }

    static void TryFormSigmaInstant(AtomFunction atomA, AtomFunction atomB)
    {
        if (atomA == null || atomB == null) return;
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
            Object.Destroy(orbB.gameObject);
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    static void TryFormPiInstant(AtomFunction atomA, AtomFunction atomB)
    {
        if (atomA == null || atomB == null) return;
        if (atomA.GetBondsTo(atomB) != 1) return;

        var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
        var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;

        var orbA = atomA.GetLoneOrbitalForBondFormation(dirAtoB);
        var orbB = atomB.GetLoneOrbitalForBondFormation(dirBtoA);
        if (orbA == null || orbB == null) return;

        int merged = orbA.ElectronCount + orbB.ElectronCount;
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Object.Destroy(orbB.gameObject);
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }
}
