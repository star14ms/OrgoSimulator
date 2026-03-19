using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using System.Collections.Generic;

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
        {
            editModeActive = !editModeActive;
            if (!editModeActive) { ClearSelectionHighlights(); selectedAtom = null; selectedOrbital = null; selectedMolecule = null; }
        }
        if (!editModeActive && (selectedAtom != null || selectedOrbital != null))
        {
            ClearSelectionHighlights();
            selectedAtom = null;
            selectedOrbital = null;
            selectedMolecule = null;
        }
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
        editModeActive = on;
        if (!on) { ClearSelectionHighlights(); selectedAtom = null; selectedOrbital = null; selectedMolecule = null; }
    }
    public void SetHAutoMode(bool on) => hAutoMode = on;
    public void SetEraserMode(bool on) => eraserMode = on;

    public void DestroyMolecule(HashSet<AtomFunction> atoms)
    {
        if (atoms == null) return;
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

    public void OnAtomClicked(AtomFunction atom)
    {
        if (!editModeActive) return;
        orbitalExplicitlySelected = false;
        var newOrb = atom != null ? atom.GetOrbitalClosestToAngle(0f) : null;
        if (selectedAtom == atom && selectedOrbital == newOrb) return;
        ClearSelectionHighlights();
        selectedAtom = atom;
        selectedMolecule = atom != null ? atom.GetConnectedMolecule() : null;
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
        if (!editModeActive) return;
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

    void FormSigmaBondInstant(AtomFunction atomA, AtomFunction atomB, ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB)
    {
        int merged = orbA.ElectronCount + orbB.ElectronCount;
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            atomA.RedistributeOrbitals();
            Vector3 dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;
            float bondAngleFromB = Mathf.Atan2(dirBtoA.y, dirBtoA.x) * Mathf.Rad2Deg;
            atomB.RedistributeOrbitals(piBondAngleOverride: bondAngleFromB);
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    /// <summary>Saturate cycloalkane carbons with H facing outward. Angular spacing: 90° for C3-C5, 60° for C6.</summary>
    public void SaturateCycloalkaneWithHydrogen(AtomFunction[] atoms, Vector3 center)
    {
        if (atoms == null || atomPrefab == null || Camera.main == null) return;
        int n = atoms.Length;
        if (n < 3 || n > 6) return;

        float halfAngleDeg = (n == 6) ? 30f : 45f; // 60° or 90° between the two H's
        float bondLength = GetBondLength();

        foreach (var atom in atoms)
        {
            if (atom == null) continue;
            atom.ForceInitialize();

            Vector3 outward = (atom.transform.position - center).normalized;
            if (outward.sqrMagnitude < 0.01f) outward = Vector3.right;

            Vector3 dir1 = Rotate2D(outward, halfAngleDeg);
            Vector3 dir2 = Rotate2D(outward, -halfAngleDeg);

            AddHydrogenAtDirection(atom, dir1, bondLength);
            AddHydrogenAtDirection(atom, dir2, bondLength);
        }
    }

    static Vector3 Rotate2D(Vector3 v, float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
        return new Vector3(v.x * c - v.y * s, v.x * s + v.y * c, v.z);
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
        var bondAngle = atom.GetPrimaryBondDirectionAngle();
        atom.RedistributeOrbitals(piBondAngleOverride: bondAngle);
        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    public void SaturateWithHydrogen(AtomFunction atom)
    {
        if (atom == null || atomPrefab == null || Camera.main == null) return;

        atom.ForceInitialize();
        float bondLength = GetBondLength();

        var orb = atom.GetLoneOrbitalWithOneElectron(Vector3.right);
        while (orb != null)
        {
            Vector3 dir = orb.transform.TransformDirection(Vector3.right);
            Vector3 hPos = atom.transform.position + dir * bondLength;

            var hGo = Instantiate(atomPrefab, hPos, Quaternion.identity);
            if (!hGo.TryGetComponent<AtomFunction>(out var hAtom))
            {
                Destroy(hGo);
                break;
            }
            hAtom.AtomicNumber = 1;
            hAtom.ForceInitialize();

            var hOrb = hAtom.GetLoneOrbitalWithOneElectron(-dir);
            if (hOrb == null)
            {
                Destroy(hGo);
                break;
            }

            FormSigmaBondInstant(atom, hAtom, orb, hOrb);
            var bondAngle = atom.GetPrimaryBondDirectionAngle();
            atom.RedistributeOrbitals(piBondAngleOverride: bondAngle);
            AtomFunction.SetupGlobalIgnoreCollisions();

            orb = atom.GetLoneOrbitalWithOneElectron(Vector3.right);
        }
    }

    float GetBondLength() => atomPrefab != null && atomPrefab.TryGetComponent<AtomFunction>(out var a) ? 1.2f * a.BondRadius : 0.96f;

    public void SetAtomPrefab(GameObject prefab) => atomPrefab = prefab;
}
