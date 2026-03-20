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

    [Tooltip("Edit-mode deselect plane is placed this far past the molecule work plane (along the camera view) so it sorts behind atoms/orbitals/bonds in the physics raycast.")]
    [SerializeField] float deselectBackgroundPastWorkPlane = 22f;

    GameObject deselectBackgroundRoot;
    BoxCollider deselectBackgroundCollider;

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
        deselectBackgroundRoot = go;
        go.transform.rotation = cam.transform.rotation;
        go.transform.localScale = new Vector3(100f, 100f, 0.01f);

        deselectBackgroundCollider = go.AddComponent<BoxCollider>();
        deselectBackgroundCollider.size = Vector3.one;
        deselectBackgroundCollider.isTrigger = true;
        deselectBackgroundCollider.enabled = editModeActive;

        var handler = go.AddComponent<BackgroundClickHandler>();
        handler.editModeManager = this;

        RepositionDeselectBackground();
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
        if (deselectBackgroundCollider != null)
            deselectBackgroundCollider.enabled = on;
        if (!on)
        {
            if (selectedOrbital != null) selectedOrbital.SetHighlighted(false);
            selectedOrbital = null;
            orbitalExplicitlySelected = false;
            if (selectedAtom != null) selectedAtom.SetSelectionHighlight(true);
            return;
        }

        RepositionDeselectBackground();
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

    /// <summary>
    /// Keeps the deselect collider behind the molecule work plane. A fixed near depth (previously 15) sat
    /// between the camera and the sheet whenever the work plane was farther than that, so the background
    /// won the raycast and clicks never reached atoms, orbitals, or bonds.
    /// </summary>
    public void RepositionDeselectBackground()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var bg = deselectBackgroundRoot != null ? deselectBackgroundRoot : GameObject.Find("EditModeBackground");
        if (bg == null) return;
        bg.transform.position = ComputeDeselectBackgroundWorldPosition(cam);
        bg.transform.rotation = cam.transform.rotation;
    }

    static Vector3 ComputeDeselectBackgroundWorldPosition(Camera cam, float pastPlaneMargin)
    {
        Vector3 f = cam.transform.forward;
        if (f.sqrMagnitude < 1e-10f)
            f = Vector3.forward;
        else
            f.Normalize();

        var wp = MoleculeWorkPlane.Instance;
        if (wp != null)
        {
            float d = Vector3.Dot(wp.WorldPlanePoint - cam.transform.position, f);
            if (d > 0.05f)
                return cam.transform.position + f * (d + pastPlaneMargin);
        }

        return cam.transform.position + f * 15f;
    }

    Vector3 ComputeDeselectBackgroundWorldPosition(Camera cam) =>
        ComputeDeselectBackgroundWorldPosition(cam, deselectBackgroundPastWorkPlane);

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

        while (true)
        {
            ElectronOrbitalFunction orb;
            Vector3 dirN;
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                var sigmaWorld = CollectSigmaBondNeighborDirectionsWorld(atom);
                dirN = ComputeNextHydrogenDirectionWorldForSaturate(atom, sigmaWorld);
                orb = atom.GetLoneOrbitalWithOneElectron(dirN);
            }
            else
            {
                orb = atom.GetLoneOrbitalWithOneElectron(Vector3.right);
                if (orb == null) break;
                Vector3 dir = orb.transform.TransformDirection(Vector3.right);
                dirN = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.right;
            }

            if (orb == null) break;

            Vector3 hPos = atom.transform.position + dirN * bondLength;

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
        }
    }

    /// <summary>Unit vectors from <paramref name="center"/> toward each bonded neighbor (one entry per neighbor atom).</summary>
    static List<Vector3> CollectSigmaBondNeighborDirectionsWorld(AtomFunction center)
    {
        var list = new List<Vector3>();
        if (center == null) return list;
        var seen = new HashSet<AtomFunction>();
        foreach (var b in center.CovalentBonds)
        {
            if (b == null) continue;
            var other = b.AtomA == center ? b.AtomB : b.AtomA;
            if (other == null || !seen.Add(other)) continue;
            Vector3 d = other.transform.position - center.transform.position;
            if (d.sqrMagnitude < 1e-10f) continue;
            list.Add(d.normalized);
        }

        return list;
    }

    /// <summary>
    /// Next H direction for incremental saturation. Uses tetrahedral (~109.5°) when there is no π on the center;
    /// when π is already present (e.g. benzene built σ then π then H), uses trigonal planar (~120° in the σ plane)
    /// so C—H stays in the ring plane instead of CH₂-style pucker.
    /// </summary>
    static Vector3 ComputeNextHydrogenDirectionWorldForSaturate(AtomFunction center, List<Vector3> existingSigmaWorld)
    {
        int n = existingSigmaWorld.Count;
        bool piPresent = center != null && center.GetPiBondCount() > 0;

        if (n == 0)
            return VseprLayout.GetIdealLocalDirections(4)[0].normalized;

        if (n == 1)
        {
            var ideal = piPresent ? VseprLayout.GetIdealLocalDirections(3) : VseprLayout.GetIdealLocalDirections(4);
            return VseprLayout.AlignFirstDirectionTo(ideal, existingSigmaWorld[0])[1].normalized;
        }

        if (n == 2)
        {
            if (piPresent)
                return VseprLayout.TrigonalPlanarThirdDirectionFromTwoBondsWorld(existingSigmaWorld[0], existingSigmaWorld[1]);
            VseprLayout.TwoHydrogenDirectionsFromBonds(existingSigmaWorld[0], existingSigmaWorld[1], out var h1, out var _);
            return h1.normalized;
        }

        return VseprLayout.OneHydrogenDirectionFromThreeBondsWorld(
            existingSigmaWorld[0], existingSigmaWorld[1], existingSigmaWorld[2]);
    }

    float GetBondLength() => atomPrefab != null && atomPrefab.TryGetComponent<AtomFunction>(out var a) ? 1.2f * a.BondRadius : 0.96f;

    public void SetAtomPrefab(GameObject prefab) => atomPrefab = prefab;
}
