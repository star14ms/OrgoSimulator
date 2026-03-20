using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Manages edit mode: selection (click atom to select, background to deselect), add-atom-to-selected,
/// H-auto mode (saturate new atoms with hydrogen). Toggle edit mode with E.
/// </summary>
public class EditModeManager : MonoBehaviour
{
    [SerializeField] GameObject atomPrefab;

    bool editModeActive;
    bool hAutoMode;
    bool eraserMode;
    AtomFunction selectedAtom;
    ElectronOrbitalFunction selectedOrbital;
    HashSet<AtomFunction> selectedMolecule;
    bool orbitalExplicitlySelected;

    public bool EditModeActive => editModeActive;
    public bool HAutoMode => hAutoMode;
    public bool EraserMode => eraserMode;
    public AtomFunction SelectedAtom => selectedAtom;
    public ElectronOrbitalFunction SelectedOrbital => selectedOrbital;

    MoleculeBuilder moleculeBuilder;

    void Start()
    {
        moleculeBuilder = FindFirstObjectByType<MoleculeBuilder>();
        CreateBackgroundForDeselect();
    }

    void CreateBackgroundForDeselect()
    {
        var cam = Camera.main;
        if (cam == null) return;

        var go = new GameObject("EditModeBackground");
        go.transform.position = cam.transform.position + cam.transform.forward * 15f;
        go.transform.rotation = cam.transform.rotation;
        go.transform.localScale = new Vector3(100f, 100f, 0.01f);

        var collider = go.AddComponent<BoxCollider>();
        collider.size = Vector3.one;
        collider.isTrigger = true;

        var handler = go.AddComponent<BackgroundClickHandler>();
        handler.editModeManager = this;
    }

    void Update()
    {
        if (Keyboard.current?.eKey.wasPressedThisFrame == true)
            SetEditMode(!editModeActive);
        if (Keyboard.current?.dKey.wasPressedThisFrame == true)
            eraserMode = !eraserMode;

        if (editModeActive && selectedAtom != null)
        {
            var k = Keyboard.current;
            float? arrowAngle = null;
            if (k?.rightArrowKey.wasPressedThisFrame == true) arrowAngle = 0f;
            else if (k?.upArrowKey.wasPressedThisFrame == true) arrowAngle = 90f;
            else if (k?.leftArrowKey.wasPressedThisFrame == true) arrowAngle = 180f;
            else if (k?.downArrowKey.wasPressedThisFrame == true) arrowAngle = 270f;

            if (arrowAngle.HasValue && selectedOrbital != null)
            {
                var next = selectedAtom.GetNextOrbitalForArrow(selectedOrbital, arrowAngle.Value);
                if (next != null)
                {
                    selectedOrbital.SetHighlighted(false);
                    selectedOrbital = next;
                    selectedOrbital.SetHighlighted(true);
                }
            }
        }
    }

    public void SetEditMode(bool on)
    {
        if (editModeActive == on) return;
        editModeActive = on;
        if (!on)
        {
            if (selectedOrbital != null) selectedOrbital.SetHighlighted(false);
            selectedOrbital = null;
            orbitalExplicitlySelected = false;
            if (selectedAtom != null) selectedAtom.SetSelectionHighlight(true);
            return;
        }

        if (selectedAtom != null)
        {
            selectedOrbital = selectedAtom.GetOrbitalClosestToAngle(0f);
            orbitalExplicitlySelected = false;
            ApplySelectionHighlights();
        }
    }
    public void SetHAutoMode(bool on) => hAutoMode = on;
    public void SetEraserMode(bool on) => eraserMode = on;

    public void DestroyMolecule(HashSet<AtomFunction> atoms)
    {
        if (atoms == null) return;
        if (selectedAtom != null && atoms.Contains(selectedAtom))
        {
            ClearSelectionHighlights();
            selectedAtom = null;
            selectedOrbital = null;
            selectedMolecule = null;
        }
        foreach (var a in atoms)
        {
            if (a == null) continue;
            foreach (var b in a.CovalentBonds)
            {
                if (b != null && b.gameObject != null)
                    Destroy(b.gameObject);
            }
        }
        foreach (var a in atoms)
        {
            if (a != null && a.gameObject != null)
                Destroy(a.gameObject);
        }
    }

    /// <summary>Remove every atom and bond in the scene (all molecules).</summary>
    public void ClearAllMolecules()
    {
        ClearSelectionHighlights();
        selectedAtom = null;
        selectedOrbital = null;
        selectedMolecule = null;

        var atoms = Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None);
        var seen = new HashSet<AtomFunction>();
        foreach (var a in atoms)
        {
            if (a == null || !seen.Add(a)) continue;
            var mol = a.GetConnectedMolecule();
            foreach (var x in mol)
                seen.Add(x);
            DestroyMolecule(mol);
        }
    }

    public void OnAtomClicked(AtomFunction atom)
    {
        if (atom == null) return;

        if (!editModeActive)
        {
            if (selectedAtom == atom) return;
            ClearSelectionHighlights();
            selectedAtom = atom;
            selectedMolecule = atom.GetConnectedMolecule();
            selectedOrbital = null;
            orbitalExplicitlySelected = false;
            selectedAtom.SetSelectionHighlight(true);
            return;
        }

        orbitalExplicitlySelected = false;
        var newOrb = atom.GetOrbitalClosestToAngle(0f);
        if (selectedAtom == atom && selectedOrbital == newOrb) return;
        ClearSelectionHighlights();
        selectedAtom = atom;
        selectedMolecule = atom.GetConnectedMolecule();
        selectedOrbital = newOrb;
        ApplySelectionHighlights();
    }

    public void OnOrbitalClicked(AtomFunction atom, ElectronOrbitalFunction orb)
    {
        if (!editModeActive || atom == null || orb == null) return;
        if (orb.Bond != null || orb.ElectronCount != 1) return;
        orbitalExplicitlySelected = true;
        if (selectedAtom == atom && selectedOrbital == orb) return;
        ClearSelectionHighlights();
        selectedAtom = atom;
        selectedMolecule = atom.GetConnectedMolecule();
        selectedOrbital = orb;
        ApplySelectionHighlights();
    }

    public void OnBackgroundClicked()
    {
        orbitalExplicitlySelected = false;
        ClearSelectionHighlights();
        selectedAtom = null;
        selectedOrbital = null;
        selectedMolecule = null;
    }

    void ClearSelectionHighlights()
    {
        if (selectedAtom != null) selectedAtom.SetSelectionHighlight(false);
        if (selectedOrbital != null) selectedOrbital.SetHighlighted(false);
    }

    void ApplySelectionHighlights()
    {
        if (selectedAtom != null) selectedAtom.SetSelectionHighlight(true);
        if (selectedOrbital != null) selectedOrbital.SetHighlighted(true);
    }

    public void RepositionDeselectBackground()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var bg = GameObject.Find("EditModeBackground");
        if (bg == null) return;
        bg.transform.position = cam.transform.position + cam.transform.forward * 15f;
        bg.transform.rotation = cam.transform.rotation;
    }

    public void RefreshEditSelectionHighlights()
    {
        if (!editModeActive)
        {
            if (selectedAtom != null) selectedAtom.SetSelectionHighlight(true);
            return;
        }
        ClearSelectionHighlights();
        ApplySelectionHighlights();
    }

    public bool TryAddAtomToSelected(int atomicNumber)
    {
        if (selectedAtom == null || atomPrefab == null || Camera.main == null) return false;

        if (selectedAtom.AtomicNumber == 1 && !orbitalExplicitlySelected)
            return TryReplaceHydrogenWithAtom(atomicNumber);

        var orb = selectedOrbital ?? selectedAtom.GetOrbitalClosestToAngle(0f);
        if (orb == null) return false;

        Vector3 dir = orb.transform.TransformDirection(Vector3.right);
        Vector3 newPos = selectedAtom.transform.position + dir * GetBondLength();

        var newAtomGo = Instantiate(atomPrefab, newPos, Quaternion.identity);
        if (!newAtomGo.TryGetComponent<AtomFunction>(out var newAtom))
        {
            Destroy(newAtomGo);
            return false;
        }
        newAtom.AtomicNumber = atomicNumber;
        newAtom.ForceInitialize();

        Vector3 dirToSelected = (selectedAtom.transform.position - newAtom.transform.position).normalized;
        var newOrb = newAtom.GetLoneOrbitalWithOneElectron(dirToSelected);
        if (newOrb == null)
        {
            Destroy(newAtomGo);
            return false;
        }

        FormSigmaBondInstant(selectedAtom, newAtom, orb, newOrb);

        selectedOrbital?.SetHighlighted(false);
        selectedOrbital = selectedAtom.GetOrbitalClosestToAngle(0f);
        if (selectedOrbital != null) selectedOrbital.SetHighlighted(true);

        if (hAutoMode)
            SaturateWithHydrogen(newAtom);

        AtomFunction.SetupGlobalIgnoreCollisions();
        return true;
    }

    bool TryReplaceHydrogenWithAtom(int atomicNumber)
    {
        var hydrogen = selectedAtom;
        AtomFunction parentAtom = null;
        CovalentBond bondToBreak = null;
        foreach (var b in hydrogen.CovalentBonds)
        {
            if (b == null) continue;
            var other = b.AtomA == hydrogen ? b.AtomB : b.AtomA;
            if (other != null) { parentAtom = other; bondToBreak = b; break; }
        }
        if (parentAtom == null || bondToBreak == null) return false;

        Vector3 hPos = hydrogen.transform.position;
        bondToBreak.BreakBond(parentAtom);
        selectedAtom?.SetSelectionHighlight(false);
        selectedOrbital?.SetHighlighted(false);
        Destroy(hydrogen.gameObject);

        var newAtomGo = Instantiate(atomPrefab, hPos, Quaternion.identity);
        if (!newAtomGo.TryGetComponent<AtomFunction>(out var newAtom))
        {
            Destroy(newAtomGo);
            return false;
        }
        newAtom.AtomicNumber = atomicNumber;
        newAtom.ForceInitialize();

        Vector3 dirToNewAtom = (newAtom.transform.position - parentAtom.transform.position).normalized;
        var parentOrb = parentAtom.GetLoneOrbitalForBondFormation(dirToNewAtom);
        if (parentOrb == null)
        {
            Destroy(newAtomGo);
            return false;
        }

        Vector3 dirToParent = (parentAtom.transform.position - newAtom.transform.position).normalized;
        var newOrb = newAtom.GetLoneOrbitalWithOneElectron(dirToParent);
        if (newOrb == null)
        {
            Destroy(newAtomGo);
            return false;
        }

        FormSigmaBondInstant(parentAtom, newAtom, parentOrb, newOrb);

        selectedAtom = newAtom;
        selectedOrbital = newAtom.GetOrbitalClosestToAngle(0f);
        selectedAtom.SetSelectionHighlight(true);
        if (selectedOrbital != null) selectedOrbital.SetHighlighted(true);

        if (hAutoMode)
            SaturateWithHydrogen(newAtom);

        AtomFunction.SetupGlobalIgnoreCollisions();
        return true;
    }

    void FormSigmaBondInstant(AtomFunction atomA, AtomFunction atomB, ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB, bool redistributeAtomA = true, bool redistributeAtomB = true)
    {
        int merged = orbA.ElectronCount + orbB.ElectronCount;
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            if (redistributeAtomA)
                atomA.RedistributeOrbitals();
            if (redistributeAtomB)
            {
                Vector3 dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;
                float bondAngleFromB = Mathf.Atan2(dirBtoA.y, dirBtoA.x) * Mathf.Rad2Deg;
                atomB.RedistributeOrbitals(piBondAngleOverride: bondAngleFromB, refBondWorldDirection: dirBtoA);
            }
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    static bool TryPickTwoLoneOrbitalsForDirections(AtomFunction carbon, Vector3 h1World, Vector3 h2World, out ElectronOrbitalFunction orb1, out ElectronOrbitalFunction orb2)
    {
        orb1 = carbon.GetLoneOrbitalWithOneElectron(h1World);
        orb2 = null;
        if (orb1 == null) return false;
        Vector3 d2 = h2World.sqrMagnitude > 1e-8f ? h2World.normalized : Vector3.right;
        float bestDot = -2f;
        foreach (var orb in carbon.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != carbon.transform || orb.Bond != null || orb.ElectronCount != 1) continue;
            if (orb == orb1) continue;
            float dot = Vector3.Dot(orb.transform.TransformDirection(Vector3.right), d2);
            if (dot > bestDot)
            {
                bestDot = dot;
                orb2 = orb;
            }
        }
        return orb2 != null;
    }

    /// <summary>
    /// Saturate ring carbons to sp³ single bonds: —CH₂— gets two H (109.5° pair); a carbon that already has three σ bonds (e.g. ring + substituent) gets one H.
    /// </summary>
    public void SaturateCycloalkaneWithHydrogen(AtomFunction[] atoms)
    {
        if (atoms == null || atomPrefab == null || Camera.main == null) return;
        int n = atoms.Length;
        if (n < 3 || n > 6) return;

        float bondLength = GetBondLength();

        foreach (var atom in atoms)
        {
            if (atom == null) continue;
            atom.ForceInitialize();

            int hNeeded = 4 - atom.CovalentBonds.Count;
            if (hNeeded <= 0) continue;

            int idx = System.Array.IndexOf(atoms, atom);
            Vector3 c = atom.transform.position;
            Vector3 prev = atoms[(idx + n - 1) % n].transform.position;
            Vector3 next = atoms[(idx + 1) % n].transform.position;
            Vector3 toPrev = (prev - c).normalized;
            Vector3 toNext = (next - c).normalized;
            if (toPrev.sqrMagnitude < 1e-8f || toNext.sqrMagnitude < 1e-8f) continue;

            if (hNeeded == 2)
            {
                VseprLayout.TwoHydrogenDirectionsFromBonds(toPrev, toNext, out Vector3 h1, out Vector3 h2);
                if (!TryPickTwoLoneOrbitalsForDirections(atom, h1, h2, out var carbonOrb1, out var carbonOrb2))
                    continue;

                Vector3 pos1 = c + h1 * bondLength;
                Vector3 pos2 = c + h2 * bondLength;

                var hGo1 = Instantiate(atomPrefab, pos1, Quaternion.identity);
                var hGo2 = Instantiate(atomPrefab, pos2, Quaternion.identity);
                if (!hGo1.TryGetComponent<AtomFunction>(out var hAtom1) || !hGo2.TryGetComponent<AtomFunction>(out var hAtom2))
                {
                    Destroy(hGo1);
                    Destroy(hGo2);
                    continue;
                }
                hAtom1.AtomicNumber = 1;
                hAtom2.AtomicNumber = 1;
                hAtom1.ForceInitialize();
                hAtom2.ForceInitialize();

                var hOrb1 = hAtom1.GetLoneOrbitalWithOneElectron(-h1);
                var hOrb2 = hAtom2.GetLoneOrbitalWithOneElectron(-h2);
                if (hOrb1 == null || hOrb2 == null)
                {
                    Destroy(hGo1);
                    Destroy(hGo2);
                    continue;
                }

                FormSigmaBondInstant(atom, hAtom1, carbonOrb1, hOrb1, redistributeAtomA: false, redistributeAtomB: false);
                FormSigmaBondInstant(atom, hAtom2, carbonOrb2, hOrb2, redistributeAtomA: false, redistributeAtomB: false);
                atom.RedistributeOrbitals();
            }
            else if (hNeeded == 1)
            {
                var seen = new HashSet<AtomFunction>();
                var dirs = new List<Vector3>();
                foreach (var b in atom.CovalentBonds)
                {
                    if (b == null) continue;
                    var other = b.AtomA == atom ? b.AtomB : b.AtomA;
                    if (other == null || !seen.Add(other)) continue;
                    Vector3 d = other.transform.position - c;
                    if (d.sqrMagnitude < 1e-8f) continue;
                    dirs.Add(d.normalized);
                }
                if (dirs.Count < 3) continue;

                Vector3 hDir = VseprLayout.OneHydrogenDirectionFromThreeBondsWorld(dirs[0], dirs[1], dirs[2]);
                var carbonOrb = atom.GetLoneOrbitalWithOneElectron(hDir);
                if (carbonOrb == null) continue;

                Vector3 pos = c + hDir * bondLength;
                var hGo = Instantiate(atomPrefab, pos, Quaternion.identity);
                if (!hGo.TryGetComponent<AtomFunction>(out var hAtom))
                {
                    Destroy(hGo);
                    continue;
                }
                hAtom.AtomicNumber = 1;
                hAtom.ForceInitialize();
                var hOrb = hAtom.GetLoneOrbitalWithOneElectron(-hDir);
                if (hOrb == null)
                {
                    Destroy(hGo);
                    continue;
                }

                FormSigmaBondInstant(atom, hAtom, carbonOrb, hOrb, redistributeAtomA: false, redistributeAtomB: false);
                atom.RedistributeOrbitals();
            }
        }

        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    void AddHydrogenAtDirection(AtomFunction atom, Vector3 dir, float bondLength)
    {
        var orb = atom.GetLoneOrbitalWithOneElectron(dir);
        if (orb == null) return;

        Vector3 hPos = atom.transform.position + dir * bondLength;
        var hGo = Instantiate(atomPrefab, hPos, Quaternion.identity);
        if (!hGo.TryGetComponent<AtomFunction>(out var hAtom))
        {
            Destroy(hGo);
            return;
        }
        hAtom.AtomicNumber = 1;
        hAtom.ForceInitialize();

        var hOrb = hAtom.GetLoneOrbitalWithOneElectron(-dir);
        if (hOrb == null)
        {
            Destroy(hGo);
            return;
        }

        FormSigmaBondInstant(atom, hAtom, orb, hOrb);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            atom.RedistributeOrbitals(refBondWorldDirection: dir.normalized);
        else
        {
            var bondAngle = atom.GetPrimaryBondDirectionAngle();
            atom.RedistributeOrbitals(piBondAngleOverride: bondAngle);
        }
        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    public void SaturateWithHydrogen(AtomFunction atom)
    {
        if (atom == null || atomPrefab == null || Camera.main == null) return;

        atom.ForceInitialize();
        float bondLength = GetBondLength();

        int step = 0;
        var orb = atom.GetLoneOrbitalWithOneElectron(Vector3.right);
        int pending0 = 0;
        foreach (var o in atom.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (o == null || o.transform.parent != atom.transform || o.Bond != null || o.ElectronCount != 1) continue;
            pending0++;
        }
        Debug.Log($"[H-auto] SaturateWithHydrogen start target Z={atom.AtomicNumber} use3D={OrbitalAngleUtility.UseFull3DOrbitalGeometry} lone-1e count≈{pending0} bondLength={bondLength:F3}");

        while (orb != null)
        {
            Vector3 dir = orb.transform.TransformDirection(Vector3.right);
            Vector3 dirN = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.right;
            Vector3 hPos = atom.transform.position + dirN * bondLength;

            var sbBefore = new StringBuilder();
            sbBefore.Append($"[H-auto] step {step} BEFORE bond: orbital→world dir={dirN} (mag={dir.magnitude:F3}) angles vs existing σ neighbors at C:");
            LogAnglesFromDirToBondedNeighbors(atom, null, dirN, sbBefore);
            Debug.Log(sbBefore.ToString());

            var hGo = Instantiate(atomPrefab, hPos, Quaternion.identity);
            if (!hGo.TryGetComponent<AtomFunction>(out var hAtom))
            {
                Destroy(hGo);
                break;
            }
            hAtom.AtomicNumber = 1;
            hAtom.ForceInitialize();

            var hOrb = hAtom.GetLoneOrbitalWithOneElectron(-dirN);
            if (hOrb == null)
            {
                Destroy(hGo);
                break;
            }

            FormSigmaBondInstant(atom, hAtom, orb, hOrb);
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                atom.RedistributeOrbitals(refBondWorldDirection: dirN);
            else
            {
                var bondAngle = atom.GetPrimaryBondDirectionAngle();
                atom.RedistributeOrbitals(piBondAngleOverride: bondAngle);
            }
            AtomFunction.SetupGlobalIgnoreCollisions();

            Vector3 actualDir = (hAtom.transform.position - atom.transform.position).normalized;
            var sbAfter = new StringBuilder();
            sbAfter.Append($"[H-auto] step {step} AFTER bond + redistribute: C→H dir={actualDir} | angles X–C–H (target=C):");
            LogAnglesFromDirToBondedNeighbors(atom, hAtom, actualDir, sbAfter);
            Debug.Log(sbAfter.ToString());

            step++;
            orb = atom.GetLoneOrbitalWithOneElectron(Vector3.right);
        }

        if (step > 0)
            DebugLogFullNeighborAngleMatrix(atom, step);
    }

    static void LogAnglesFromDirToBondedNeighbors(AtomFunction center, AtomFunction excludeNeighbor, Vector3 dirFromCenterToNewBond, StringBuilder sb)
    {
        int n = 0;
        foreach (var b in center.CovalentBonds)
        {
            if (b == null) continue;
            var other = b.AtomA == center ? b.AtomB : b.AtomA;
            if (other == null || other == excludeNeighbor) continue;
            Vector3 d = (other.transform.position - center.transform.position).normalized;
            if (d.sqrMagnitude < 1e-8f) continue;
            float ang = Vector3.Angle(dirFromCenterToNewBond, d);
            string sym = AtomFunction.GetElementSymbol(other.AtomicNumber);
            sb.Append($" ∠({sym}–C–new)={ang:F1}°");
            n++;
        }
        if (n == 0) sb.Append(" (no other neighbors)");
    }

    static void DebugLogFullNeighborAngleMatrix(AtomFunction center, int hAdded)
    {
        var neighbors = new List<AtomFunction>();
        foreach (var b in center.CovalentBonds)
        {
            if (b == null) continue;
            var other = b.AtomA == center ? b.AtomB : b.AtomA;
            if (other == null) continue;
            bool dup = false;
            foreach (var e in neighbors)
                if (e == other) { dup = true; break; }
            if (!dup) neighbors.Add(other);
        }
        var dirs = new List<Vector3>(neighbors.Count);
        foreach (var o in neighbors)
        {
            Vector3 d = (o.transform.position - center.transform.position).normalized;
            dirs.Add(d.sqrMagnitude > 1e-8f ? d : Vector3.zero);
        }

        var sb = new StringBuilder();
        sb.Append($"[H-auto] END saturated target Z={center.AtomicNumber} added {hAdded} H | pairwise angles at C (degrees):");
        for (int i = 0; i < neighbors.Count; i++)
        {
            string si = AtomFunction.GetElementSymbol(neighbors[i].AtomicNumber);
            for (int j = i + 1; j < neighbors.Count; j++)
            {
                if (dirs[i].sqrMagnitude < 1e-8f || dirs[j].sqrMagnitude < 1e-8f) continue;
                float a = Vector3.Angle(dirs[i], dirs[j]);
                string sj = AtomFunction.GetElementSymbol(neighbors[j].AtomicNumber);
                sb.Append($" ∠({si}–C–{sj})={a:F1}");
            }
        }
        sb.Append($" | tetrahedral ref≈109.47°");
        Debug.Log(sb.ToString());
    }

    float GetBondLength() => atomPrefab != null && atomPrefab.TryGetComponent<AtomFunction>(out var a) ? 1.2f * a.BondRadius : 0.96f;

    public void SetAtomPrefab(GameObject prefab) => atomPrefab = prefab;
}
