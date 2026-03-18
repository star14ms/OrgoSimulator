using UnityEngine;

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

    public void CreateCycloalkane(int ringSize)
    {
        if (ringSize < 3 || ringSize > 6 || atomPrefab == null || Camera.main == null) return;

        float bondLength = GetBondLength();
        float ringRadius = GetRingRadius(ringSize, bondLength);
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
            float angle0 = ringSize == 4 ? 45f * Mathf.Deg2Rad : -90f * Mathf.Deg2Rad;
            Vector3 atom0Pos = anchorAtom.transform.position + dir * bondLength;
            center = atom0Pos - ringRadius * new Vector3(Mathf.Cos(angle0), Mathf.Sin(angle0), 0);
        }
        else
        {
            center = GetViewportCenter();
        }

        var atoms = new AtomFunction[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            float angleDeg = ringSize == 4
                ? 45f + 90f * i  // Square: corners at 45°, 135°, 225°, 315° (horizontal/vertical edges)
                : 360f * i / ringSize - 90f;
            float angle = angleDeg * Mathf.Deg2Rad;
            Vector3 pos = center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * ringRadius;
            var go = Instantiate(atomPrefab, pos, Quaternion.identity);
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
            editMode.SaturateCycloalkaneWithHydrogen(atoms, center);

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

        for (int i = 0; i < 6; i++)
        {
            int next = (i + 1) % 6;
            FormSigmaBondInstant(atoms[i], atoms[next]);
        }

        if (anchorAtom != null && anchorOrbital != null && atoms[0] != null)
        {
            Vector3 dirToAnchor = (anchorAtom.transform.position - atoms[0].transform.position).normalized;
            var orb0 = atoms[0].GetLoneOrbitalWithOneElectron(dirToAnchor);
            if (orb0 != null)
                FormSigmaBondInstant(anchorAtom, atoms[0], anchorOrbital, orb0);
        }

        for (int i = 0; i < 3; i++)
        {
            int a = i * 2;
            int b = (a + 1) % 6;
            FormPiBondInstant(atoms[a], atoms[b]);
        }

        foreach (var a in atoms)
            if (a != null) a.RedistributeOrbitals();
        if (anchorAtom != null) anchorAtom.RedistributeOrbitals();

        if (editMode != null)
        {
            foreach (var a in atoms)
                if (a != null) editMode.SaturateWithHydrogen(a);
        }

        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    void FormSigmaBondInstant(AtomFunction atomA, AtomFunction atomB)
    {
        if (atomA == null || atomB == null) return;
        var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
        var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;

        var orbA = atomA.GetLoneOrbitalWithOneElectron(dirAtoB);
        var orbB = atomB.GetLoneOrbitalWithOneElectron(dirBtoA);
        if (orbA == null || orbB == null) return;

        FormSigmaBondInstant(atomA, atomB, orbA, orbB);
    }

    void FormSigmaBondInstant(AtomFunction atomA, AtomFunction atomB, ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB)
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
            atomA.RedistributeOrbitals();
            atomB.RedistributeOrbitals();
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    void FormPiBondInstant(AtomFunction atomA, AtomFunction atomB)
    {
        if (atomA == null || atomB == null) return;
        if (atomA.GetBondsTo(atomB) != 1) return;

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
            atomA.RedistributeOrbitals();
            atomB.RedistributeOrbitals();
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    Vector3 GetViewportCenter()
    {
        float min = viewportMargin;
        float max = 1f - viewportMargin;
        float mid = (min + max) * 0.5f;
        var world = Camera.main.ViewportToWorldPoint(new Vector3(mid, mid, -Camera.main.transform.position.z));
        return world;
    }
}
