using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Manages edit mode: selection (click atom to select, background to deselect), add-atom-to-selected,
/// H-auto mode (saturate new atoms with hydrogen). Toggle edit mode with E.
/// Eraser (D): removes only “end” atoms — fewer than two bonds to non-hydrogen neighbors (plus attached H on that center).
/// Left/right arrows cycle a fixed DFS order (no permuting by selection): root = selected heavy or σ-parent of selected H;
/// all σ-H on a center (stable instance-id order) before backbone heavies — CH4 cycles C,H,H,H,H (e.g. [C]HHHH ↔ CHHH[H] ↔ C[H]HHH).
/// Up/down orbitals only in edit mode.
/// </summary>
public class EditModeManager : MonoBehaviour
{
    [SerializeField] GameObject atomPrefab;
    bool editModeActive = true;
    bool hAutoMode = true;
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

    /// <summary>True when functional groups can attach: selected H (replace), or a free 1e lone orbital on the selected non-H atom (including the orbital auto-picked when the atom is clicked).</summary>
    public bool FunctionalGroupAttachmentReady()
    {
        if (!editModeActive || selectedAtom == null) return false;
        if (selectedAtom.AtomicNumber == 1 && !orbitalExplicitlySelected)
            return true;
        return selectedOrbital != null
            && selectedOrbital.Bond == null
            && selectedOrbital.ElectronCount == 1;
    }

    /// <summary>Distinct neighbors reached by a σ bond whose atomic number is not 1.</summary>
    public static int CountDistinctNonHydrogenNeighbors(AtomFunction a)
    {
        if (a == null) return 0;
        var seen = new HashSet<AtomFunction>();
        foreach (var b in a.CovalentBonds)
        {
            if (b == null) continue;
            var o = b.AtomA == a ? b.AtomB : b.AtomA;
            if (o != null && o.AtomicNumber != 1)
                seen.Add(o);
        }
        return seen.Count;
    }

    /// <summary>Eraser: remove this atom and its directly bonded hydrogens only if it is a chain end (&lt; 2 non-H neighbors).</summary>
    public bool TryEraseAtomIfChainEnd(AtomFunction atom)
    {
        if (!eraserMode || atom == null) return false;
        if (CountDistinctNonHydrogenNeighbors(atom) >= 2) return false;

        var remove = new HashSet<AtomFunction> { atom };
        foreach (var b in atom.CovalentBonds)
        {
            if (b == null) continue;
            var o = b.AtomA == atom ? b.AtomB : b.AtomA;
            if (o != null && o.AtomicNumber == 1)
                remove.Add(o);
        }

        AtomFunction keeperAtom = null;
        ElectronOrbitalFunction orbitalTowardRemoved = null;

        for (int guard = 0; guard < 64; guard++)
        {
            CovalentBond bridge = null;
            AtomFunction keeper = null;
            foreach (var a in remove)
            {
                if (a == null) continue;
                foreach (var bond in a.CovalentBonds.ToList())
                {
                    if (bond == null) continue;
                    var other = bond.AtomA == a ? bond.AtomB : bond.AtomA;
                    if (other == null) continue;
                    if (remove.Contains(other)) continue;
                    bridge = bond;
                    keeper = other;
                    break;
                }
                if (bridge != null) break;
            }
            if (bridge == null) break;
            keeperAtom = keeper;
            orbitalTowardRemoved = bridge.Orbital;
            bridge.BreakBond(keeper, instantRedistributionForDestroyPartner: true);
        }

        DestroyMolecule(remove);

        if (keeperAtom != null && orbitalTowardRemoved != null)
        {
            ClearSelectionHighlights();
            selectedAtom = keeperAtom;
            selectedMolecule = keeperAtom.GetConnectedMolecule();
            if (editModeActive)
            {
                selectedOrbital = orbitalTowardRemoved;
                orbitalExplicitlySelected = true;
            }
            else
            {
                selectedOrbital = null;
                orbitalExplicitlySelected = false;
            }
            ApplySelectionHighlights();
        }
        else if (selectedAtom != null)
            RefreshSelectedMoleculeAfterBondChange();

        AtomFunction.SetupGlobalIgnoreCollisions();
        return true;
    }

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

        var existing = GameObject.Find("EditModeBackground");
        GameObject go;
        if (existing != null)
        {
            go = existing;
            deselectBackgroundRoot = go;
            deselectBackgroundCollider = go.GetComponent<BoxCollider>();
            if (deselectBackgroundCollider == null)
                deselectBackgroundCollider = go.AddComponent<BoxCollider>();
        }
        else
        {
            go = new GameObject("EditModeBackground");
            deselectBackgroundRoot = go;
            go.transform.rotation = cam.transform.rotation;
            go.transform.localScale = new Vector3(100f, 100f, 0.01f);
            deselectBackgroundCollider = go.AddComponent<BoxCollider>();
            deselectBackgroundCollider.size = Vector3.one;
        }

        // Non-trigger: PhysicsRaycaster often ignores triggers, so clicks would never reach the background.
        deselectBackgroundCollider.isTrigger = false;
        // Always on: selection works outside edit mode (E off); background must still receive pointer events to deselect.
        deselectBackgroundCollider.enabled = true;

        var handler = go.GetComponent<BackgroundClickHandler>();
        if (handler == null)
            handler = go.AddComponent<BackgroundClickHandler>();
        handler.editModeManager = this;

        RepositionDeselectBackground();
    }

    void Update()
    {
        if (Keyboard.current?.eKey.wasPressedThisFrame == true)
            SetEditMode(!editModeActive);
        if (Keyboard.current?.dKey.wasPressedThisFrame == true)
            eraserMode = !eraserMode;

        if (selectedAtom == null) return;

        var k = Keyboard.current;
        if (k == null) return;

        if (editModeActive)
        {
            if (k.upArrowKey.wasPressedThisFrame)
                CycleSelectedOrbitalInList(+1);
            else if (k.downArrowKey.wasPressedThisFrame)
                CycleSelectedOrbitalInList(-1);
        }

        if (k.rightArrowKey.wasPressedThisFrame)
            CycleSelectedAtomInMolecule(+1);
        else if (k.leftArrowKey.wasPressedThisFrame)
            CycleSelectedAtomInMolecule(-1);
    }

    void CycleSelectedOrbitalInList(int delta)
    {
        if (selectedAtom == null) return;
        var next = selectedAtom.GetAdjacentOrbitalInList(selectedOrbital, delta);
        if (next == null || next == selectedOrbital) return;
        if (selectedOrbital != null) selectedOrbital.SetHighlighted(false);
        selectedOrbital = next;
        selectedOrbital.SetHighlighted(true);
    }

    static int CompareAtomInstanceId(AtomFunction a, AtomFunction b) =>
        a.GetInstanceID().CompareTo(b.GetInstanceID());

    static AtomFunction PickDfsRootHeavy_MinInstanceId(HashSet<AtomFunction> mol)
    {
        AtomFunction best = null;
        foreach (var a in mol)
        {
            if (a == null || a.AtomicNumber <= 1) continue;
            if (best == null || CompareAtomInstanceId(a, best) < 0)
                best = a;
        }
        return best;
    }

    /// <summary>Heavy atom σ-bound to this hydrogen within the molecule; null if none.</summary>
    static AtomFunction SigmaParentHeavyInMolecule(AtomFunction hydrogen, HashSet<AtomFunction> mol)
    {
        if (hydrogen == null || hydrogen.AtomicNumber != 1 || mol == null) return null;
        AtomFunction parent = null;
        foreach (var n in hydrogen.GetDistinctSigmaNeighborAtoms())
        {
            if (n == null || !mol.Contains(n) || n.AtomicNumber <= 1) continue;
            if (parent == null || CompareAtomInstanceId(n, parent) < 0)
                parent = n;
        }
        return parent;
    }

    /// <summary>
    /// DFS root = selected heavy, or σ-parent of selected H, else lowest-instance-id heavy (fallback).
    /// Yields [C]HC… after adding H on selected empty orbital of [C]C.
    /// </summary>
    static AtomFunction PickDfsRootForMoleculeOrdering(HashSet<AtomFunction> mol, AtomFunction selectionForOrdering)
    {
        if (selectionForOrdering != null && mol != null && mol.Contains(selectionForOrdering))
        {
            if (selectionForOrdering.AtomicNumber > 1)
                return selectionForOrdering;
            var p = SigmaParentHeavyInMolecule(selectionForOrdering, mol);
            if (p != null)
                return p;
        }
        return PickDfsRootHeavy_MinInstanceId(mol);
    }

    /// <summary>
    /// Stable DFS: σ-H neighbors (instance id) before σ-heavy neighbors. Selection must not reorder this list or arrow cycles skip atoms.
    /// </summary>
    static void DfsAppendMoleculeAtomOrder(
        AtomFunction node,
        HashSet<AtomFunction> mol,
        HashSet<AtomFunction> visited,
        List<AtomFunction> result)
    {
        if (node == null || !mol.Contains(node) || visited.Contains(node)) return;
        visited.Add(node);
        result.Add(node);

        var heavies = new List<AtomFunction>();
        var hydrogens = new List<AtomFunction>();
        foreach (var n in node.GetDistinctSigmaNeighborAtoms())
        {
            if (n == null || !mol.Contains(n)) continue;
            if (n.AtomicNumber > 1) heavies.Add(n);
            else hydrogens.Add(n);
        }

        heavies.Sort(CompareAtomInstanceId);
        hydrogens.Sort(CompareAtomInstanceId);

        foreach (var h in hydrogens)
            DfsAppendMoleculeAtomOrder(h, mol, visited, result);
        foreach (var v in heavies)
            DfsAppendMoleculeAtomOrder(v, mol, visited, result);
    }

    static List<AtomFunction> FallbackMoleculeListPositionSort(HashSet<AtomFunction> mol)
    {
        var list = mol.Where(a => a != null).ToList();
        list.Sort((a, b) =>
        {
            Vector3 pa = a.transform.position;
            Vector3 pb = b.transform.position;
            int c = pa.x.CompareTo(pb.x);
            if (c != 0) return c;
            c = pa.y.CompareTo(pb.y);
            if (c != 0) return c;
            c = pa.z.CompareTo(pb.z);
            if (c != 0) return c;
            return CompareAtomInstanceId(a, b);
        });
        return list;
    }

    static List<AtomFunction> BuildOrderedMoleculeAtomList(HashSet<AtomFunction> mol, AtomFunction selectionForOrdering)
    {
        if (mol == null || mol.Count == 0) return new List<AtomFunction>();

        var root = PickDfsRootForMoleculeOrdering(mol, selectionForOrdering);
        if (root == null)
            return FallbackMoleculeListPositionSort(mol);

        var visited = new HashSet<AtomFunction>();
        var result = new List<AtomFunction>(mol.Count);
        DfsAppendMoleculeAtomOrder(root, mol, visited, result);

        var leftovers = new List<AtomFunction>();
        foreach (var a in mol)
        {
            if (a != null && !visited.Contains(a))
                leftovers.Add(a);
        }
        leftovers.Sort(CompareAtomInstanceId);
        foreach (var a in leftovers)
            DfsAppendMoleculeAtomOrder(a, mol, visited, result);

        return result;
    }

    void CycleSelectedAtomInMolecule(int delta)
    {
        if (selectedMolecule == null || selectedMolecule.Count == 0 || selectedAtom == null) return;
        var ordered = BuildOrderedMoleculeAtomList(selectedMolecule, selectedAtom);
        if (ordered.Count == 0) return;
        int idx = ordered.IndexOf(selectedAtom);
        if (idx < 0) idx = 0;
        int n = ordered.Count;
        int nextIdx = ((idx + delta) % n + n) % n;
        var nextAtom = ordered[nextIdx];
        if (nextAtom == selectedAtom) return;
        ClearSelectionHighlights();
        selectedAtom = nextAtom;
        orbitalExplicitlySelected = false;
        if (editModeActive)
        {
            var orbs = selectedAtom.GetAllOrbitalsSortedForArrowCycling();
            selectedOrbital = orbs.Count > 0 ? orbs[0] : null;
            ApplySelectionHighlights();
        }
        else
        {
            selectedOrbital = null;
            if (selectedAtom != null) selectedAtom.SetSelectionHighlight(true);
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

    /// <summary>
    /// Rebuilds the atom set used for left/right arrow cycling after bonds change (new atoms, H-auto, replace H, etc.).
    /// </summary>
    public void RefreshSelectedMoleculeAfterBondChange()
    {
        if (selectedAtom == null)
        {
            selectedMolecule = null;
            return;
        }

        selectedMolecule = selectedAtom.GetConnectedMolecule();
    }

    /// <summary>Periodic table / toolbar: replace a selected terminal hydrogen with another element (unchanged behavior).</summary>
    public bool TryReplaceSelectedHydrogenWithAtom(int atomicNumber)
    {
        if (selectedAtom == null || atomPrefab == null || Camera.main == null) return false;
        if (selectedAtom.AtomicNumber != 1 || orbitalExplicitlySelected) return false;
        return TryReplaceHydrogenWithAtom(atomicNumber);
    }

    /// <summary>
    /// Periodic table / toolbar: bond a new atom to the selection — terminal H replacement, or σ bond from the
    /// selected lone (1e) orbital (or closest valid lone orbital when none highlighted).
    /// </summary>
    public bool TryAddAtomToSelected(int atomicNumber)
    {
        if (selectedAtom == null || atomPrefab == null || Camera.main == null) return false;

        if (selectedAtom.AtomicNumber == 1 && !orbitalExplicitlySelected)
            return TryReplaceHydrogenWithAtom(atomicNumber);

        // Bond from anchor only when there is a usable 1e lone. If the user explicitly picked an orbital that is no
        // longer 1e (bonded / filled), do not silently fall back to another lobe — spawn nearby and select the new atom instead.
        var oneELones = selectedAtom.GetLoneOrbitalsWithOneElectronSortedByAngle();
        ElectronOrbitalFunction orb = null;
        if (oneELones.Count > 0)
        {
            if (orbitalExplicitlySelected)
            {
                if (selectedOrbital != null && selectedOrbital.Bond == null && selectedOrbital.ElectronCount == 1 &&
                    oneELones.Contains(selectedOrbital))
                    orb = selectedOrbital;
            }
            else
            {
                if (selectedOrbital != null && selectedOrbital.Bond == null && selectedOrbital.ElectronCount == 1 &&
                    oneELones.Contains(selectedOrbital))
                    orb = selectedOrbital;
                if (orb == null)
                    orb = selectedAtom.GetOrbitalClosestToAngle(0f);
            }
        }

        // Toolbar H on heavy: match H-auto / VSEPR (tetrahedral or π-aware planar), not "orbital closest to 0°" in view.
        Vector3? hydrogenBondDirWorld = null;
        if (atomicNumber == 1 && selectedAtom.AtomicNumber > 1 && oneELones.Count > 0 && !orbitalExplicitlySelected)
        {
            Vector3 dirN;
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                var sigmaWorld = CollectSigmaBondNeighborDirectionsWorld(selectedAtom);
                dirN = ComputeNextHydrogenDirectionWorldForSaturate(selectedAtom, sigmaWorld);
            }
            else
            {
                var dirsXY = CollectSigmaBondNeighborDirectionsWorldXY(selectedAtom);
                dirN = ComputeNextHydrogenDirectionWorldForSaturatePlanarXY(selectedAtom, dirsXY);
            }
            var idealOrb = selectedAtom.GetLoneOrbitalWithOneElectron(dirN);
            if (idealOrb != null)
            {
                orb = idealOrb;
                hydrogenBondDirWorld = dirN.sqrMagnitude > 1e-10f ? dirN.normalized : (Vector3?)null;
            }
        }

        bool anchorHasBondingLone = orb != null;

        Vector3 newPos;
        if (anchorHasBondingLone)
        {
            Vector3 dir = hydrogenBondDirWorld ?? orb.transform.TransformDirection(Vector3.right);
            if (dir.sqrMagnitude < 1e-10f) dir = Vector3.right;
            else dir.Normalize();
            newPos = selectedAtom.transform.position + dir * GetBondLength();
        }
        else
        {
            float bl = GetBondLength();
            Vector3 offset = Vector3.right * bl;
            if (Camera.main != null)
            {
                Vector3 fd = Camera.main.transform.forward;
                if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry) fd.z = 0f;
                if (fd.sqrMagnitude > 1e-6f) offset = fd.normalized * bl;
            }
            newPos = selectedAtom.transform.position + offset;
        }

        var newAtomGo = Instantiate(atomPrefab, newPos, Quaternion.identity);
        if (!newAtomGo.TryGetComponent<AtomFunction>(out var newAtom))
        {
            Destroy(newAtomGo);
            return false;
        }
        newAtom.AtomicNumber = atomicNumber;
        newAtom.ForceInitialize();

        if (anchorHasBondingLone)
        {
            Vector3 dirToSelected = (selectedAtom.transform.position - newAtom.transform.position).normalized;
            var newOrb = newAtom.GetLoneOrbitalWithOneElectron(dirToSelected);
            if (newOrb == null)
            {
                Destroy(newAtomGo);
                return false;
            }

            var anchor = selectedAtom;
            bool heavyHeavy = atomicNumber > 1 && anchor.AtomicNumber > 1;
            bool hOnHeavy = atomicNumber == 1 && anchor.AtomicNumber > 1;
            bool anchorHasHeavySigmaNeighbor = false;
            if (hOnHeavy)
            {
                foreach (var n in anchor.GetDistinctSigmaNeighborAtoms())
                {
                    if (n != null && n.AtomicNumber > 1)
                    {
                        anchorHasHeavySigmaNeighbor = true;
                        break;
                    }
                }
            }
            // Heavy–heavy: keep anchor geometry (chain extend). H next to another heavy (e.g. —CH₂—): VSEPR relax on anchor.
            // H on a center σ-bonded only to other H's (CH₄ / —CH₃): skip full anchor relax — it re-clamps to a canonical
            // tetrahedron and visibly spins the molecule on the last H.
            bool redistributeAnchor = !heavyHeavy && (!hOnHeavy || anchorHasHeavySigmaNeighbor);
            HashSet<AtomFunction> pinChildSigmaRelax = atomicNumber > 1
                ? new HashSet<AtomFunction>(newAtom.GetAtomsOnSideOfSigmaBond(anchor))
                : null;
            HashSet<AtomFunction> pinAnchorSigmaRelax = null;
            if (redistributeAnchor)
            {
                AtomFunction backbone = null;
                foreach (var n in anchor.GetDistinctSigmaNeighborAtoms())
                {
                    if (n == null || n == newAtom) continue;
                    if (n.AtomicNumber > 1) { backbone = n; break; }
                    if (backbone == null) backbone = n;
                }
                if (backbone != null)
                    pinAnchorSigmaRelax = new HashSet<AtomFunction>(anchor.GetAtomsOnSideOfSigmaBond(backbone));
            }
            FormSigmaBondInstant(anchor, newAtom, orb, newOrb, redistributeAtomA: redistributeAnchor, redistributeAtomB: true, pinSigmaRelaxForAtomA: pinAnchorSigmaRelax, pinSigmaRelaxForAtomB: pinChildSigmaRelax);

            if (atomicNumber == 1 && anchor.AtomicNumber > 1 && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                anchor.TryPlaceTetrahedralHydrogenSubstituentsAboutSingleHeavyNeighbor(GetBondLength());

            selectedOrbital?.SetHighlighted(false);
            orbitalExplicitlySelected = false;

            if (hAutoMode)
                SaturateWithHydrogen(newAtom, pinChildSigmaRelax);

            // Saturation rebuilds tetrahedral geometry (and TryPlaceTetrahedralHydrogenSubstituentsAboutSingleHeavyNeighbor
            // re-seats —CH₃ H), which clears an earlier Newman twist — stagger after H-auto so the new center is vs the anchor.
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && newAtom.AtomicNumber > 1)
                newAtom.TryStaggerNewmanRelativeToPartner(anchor);

            // Same-element heavy (e.g. C on C): continue from the new tip so repeated toolbar adds extend the chain.
            bool sameHeavyExtend = atomicNumber > 1 && anchor.AtomicNumber == atomicNumber;
            if (sameHeavyExtend)
            {
                anchor.SetSelectionHighlight(false);
                selectedAtom = newAtom;
                selectedMolecule = newAtom.GetConnectedMolecule();
                selectedOrbital = newAtom.GetOrbitalClosestToAngle(0f);
            }
            else
            {
                selectedOrbital = anchor.GetOrbitalClosestToAngle(0f);
            }
            if (selectedOrbital != null) selectedOrbital.SetHighlighted(true);
            selectedAtom.SetSelectionHighlight(true);
            ApplySelectionHighlights();
            RefreshSelectedMoleculeAfterBondChange();
        }
        else
        {
            // Only take over selection when the new center actually exposes a 1e lone (user can continue bonding from it).
            var newOnes = newAtom.GetLoneOrbitalsWithOneElectronSortedByAngle();
            if (newOnes.Count == 0)
            {
                Destroy(newAtomGo);
                return false;
            }

            selectedAtom.SetSelectionHighlight(false);
            selectedOrbital?.SetHighlighted(false);
            selectedAtom = newAtom;
            selectedMolecule = newAtom.GetConnectedMolecule();
            selectedOrbital = newOnes[0];
            orbitalExplicitlySelected = true;
            selectedAtom.SetSelectionHighlight(true);
            if (selectedOrbital != null) selectedOrbital.SetHighlighted(true);
            ApplySelectionHighlights();
            RefreshSelectedMoleculeAfterBondChange();

            if (hAutoMode)
                SaturateWithHydrogen(newAtom);
        }

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
        bondToBreak.BreakBond(parentAtom, instantRedistributionForDestroyPartner: true);
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

        // H-auto saturation (and work-plane sync) can shift apparent framing; freeze perspective camera + orbit pivot for this replace sequence.
        bool preserveCamView = hAutoMode && Camera.main != null && !Camera.main.orthographic;
        ScrollOrbitCamera orbitCam = null;
        Vector3 savedCamPos = default;
        Quaternion savedCamRot = default;
        Vector3 savedOrbitFocus = default;
        if (preserveCamView)
        {
            var cam = Camera.main;
            savedCamPos = cam.transform.position;
            savedCamRot = cam.transform.rotation;
            orbitCam = cam.GetComponent<ScrollOrbitCamera>();
            if (orbitCam != null) savedOrbitFocus = orbitCam.OrbitFocusWorld;
        }

        try
        {
            // Do not σ-relax or re-snap the parent: keep its electron/orbital layout as-is; the added atom redistributes (and H-auto / Newman stagger only affect the new center).
            var pinFramework = new HashSet<AtomFunction>(newAtom.GetAtomsOnSideOfSigmaBond(parentAtom));
            FormSigmaBondInstant(parentAtom, newAtom, parentOrb, newOrb, redistributeAtomA: false, redistributeAtomB: true, pinSigmaRelaxForAtomB: pinFramework);

            selectedAtom = newAtom;
            selectedOrbital = newAtom.GetOrbitalClosestToAngle(0f);
            selectedAtom.SetSelectionHighlight(true);
            if (selectedOrbital != null) selectedOrbital.SetHighlighted(true);

            if (hAutoMode)
                SaturateWithHydrogen(newAtom, pinFramework);

            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && newAtom.AtomicNumber > 1)
                newAtom.TryStaggerNewmanRelativeToPartner(parentAtom);

            AtomFunction.SetupGlobalIgnoreCollisions();
            return true;
        }
        finally
        {
            if (preserveCamView && Camera.main != null)
            {
                var cam = Camera.main;
                if (orbitCam != null)
                    orbitCam.RestoreCameraPoseAndFocus(savedCamPos, savedCamRot, savedOrbitFocus);
                else
                    cam.transform.SetPositionAndRotation(savedCamPos, savedCamRot);
            }
        }
    }

    void FormSigmaBondInstant(AtomFunction atomA, AtomFunction atomB, ElectronOrbitalFunction orbA, ElectronOrbitalFunction orbB, bool redistributeAtomA = true, bool redistributeAtomB = true, HashSet<AtomFunction> pinSigmaRelaxForAtomA = null, HashSet<AtomFunction> pinSigmaRelaxForAtomB = null, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        int merged = orbA.ElectronCount + orbB.ElectronCount;
        int sigmaBeforeA = atomA.GetDistinctSigmaNeighborCount();
        int sigmaBeforeB = atomB.GetDistinctSigmaNeighborCount();
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: true);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            if (redistributeAtomA)
                atomA.RedistributeOrbitals(newSigmaBondPartnerHint: atomB, sigmaNeighborCountBeforeHint: sigmaBeforeA, pinAtomsForSigmaRelax: pinSigmaRelaxForAtomA, freezeSigmaNeighborSubtreeRoot: freezeSigmaNeighborSubtreeRoot);
            if (redistributeAtomB)
            {
                Vector3 dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;
                float bondAngleFromB = Mathf.Atan2(dirBtoA.y, dirBtoA.x) * Mathf.Rad2Deg;
                atomB.RedistributeOrbitals(piBondAngleOverride: bondAngleFromB, refBondWorldDirection: dirBtoA, newSigmaBondPartnerHint: atomA, sigmaNeighborCountBeforeHint: sigmaBeforeB, pinAtomsForSigmaRelax: pinSigmaRelaxForAtomB, freezeSigmaNeighborSubtreeRoot: freezeSigmaNeighborSubtreeRoot);
            }
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }

        if (bond != null && OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            float bl = GetBondLength();
            if (atomA != null && atomA.AtomicNumber > 1 && atomB != null && atomB.AtomicNumber == 1)
                atomA.SnapHydrogenSigmaNeighborsToBondOrbitalAxes(bl);
            else if (atomB != null && atomB.AtomicNumber > 1 && atomA != null && atomA.AtomicNumber == 1)
                atomB.SnapHydrogenSigmaNeighborsToBondOrbitalAxes(bl);
        }

        RefreshSelectedMoleculeAfterBondChange();
    }

    /// <summary>Pins everything on the framework side of <paramref name="heavyCenter"/>'s σ bond to a heavy neighbor (same as H-auto saturating a terminal C).</summary>
    HashSet<AtomFunction> TryBuildSigmaRelaxPinForHeavyCenter(AtomFunction heavyCenter)
    {
        if (heavyCenter == null || !OrbitalAngleUtility.UseFull3DOrbitalGeometry || heavyCenter.AtomicNumber <= 1)
            return null;
        AtomFunction heavyNeighbor = null;
        foreach (var n in heavyCenter.GetDistinctSigmaNeighborAtoms())
        {
            if (n != null && n.AtomicNumber > 1) { heavyNeighbor = n; break; }
        }
        if (heavyNeighbor == null) return null;
        return new HashSet<AtomFunction>(heavyCenter.GetAtomsOnSideOfSigmaBond(heavyNeighbor));
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
    /// Perspective: 3D-puckered VSEPR via <see cref="VseprLayout.TwoHydrogenDirectionsFromBonds"/>. Orthographic: planar XY via <see cref="VseprLayout.TwoHydrogenDirectionsFromBondsPlanarXY"/> (legacy 2D behavior).
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
                Vector3 h1, h2;
                if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
                    VseprLayout.TwoHydrogenDirectionsFromBonds(toPrev, toNext, out h1, out h2);
                else
                    VseprLayout.TwoHydrogenDirectionsFromBondsPlanarXY(toPrev, toNext, out h1, out h2);
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

                Vector3 hDir = OrbitalAngleUtility.UseFull3DOrbitalGeometry
                    ? VseprLayout.OneHydrogenDirectionFromThreeBondsWorld(dirs[0], dirs[1], dirs[2])
                    : VseprLayout.OneHydrogenDirectionFromThreeBondsWorldPlanarXY(dirs[0], dirs[1], dirs[2]);
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
                if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && atom.AtomicNumber > 1)
                    atom.SnapHydrogenSigmaNeighborsToBondOrbitalAxes(bondLength);
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

        float bondLen = GetBondLength();
        HashSet<AtomFunction> pinAddH = TryBuildSigmaRelaxPinForHeavyCenter(atom);
        FormSigmaBondInstant(atom, hAtom, orb, hOrb, pinSigmaRelaxForAtomA: pinAddH, pinSigmaRelaxForAtomB: pinAddH);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            atom.RedistributeOrbitals(refBondWorldDirection: dir.normalized, pinAtomsForSigmaRelax: pinAddH);
            if (atom.AtomicNumber > 1)
                atom.SnapHydrogenSigmaNeighborsToBondOrbitalAxes(bondLen);
        }
        else
        {
            var bondAngle = atom.GetPrimaryBondDirectionAngle();
            atom.RedistributeOrbitals(piBondAngleOverride: bondAngle, pinAtomsForSigmaRelax: pinAddH);
        }
        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    /// <param name="freezeSigmaNeighborSubtreeRoot">With FG attach, pass substrate attachment atom so σ-relax skips parent subtree without O(N) pins.</param>
    /// <param name="incrementalCollisionInvolvingAtoms">When non-null (e.g. functional-group attach), refresh ignores for this set only after each H add instead of the whole scene.</param>
    public void SaturateWithHydrogen(AtomFunction atom, HashSet<AtomFunction> pinSigmaRelaxNeighbors = null, IReadOnlyList<AtomFunction> incrementalCollisionInvolvingAtoms = null, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (atom == null || atomPrefab == null || Camera.main == null) return;

        atom.ForceInitialize();

        if (pinSigmaRelaxNeighbors == null && freezeSigmaNeighborSubtreeRoot == null)
            pinSigmaRelaxNeighbors = TryBuildSigmaRelaxPinForHeavyCenter(atom);

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
                var dirsXY = CollectSigmaBondNeighborDirectionsWorldXY(atom);
                dirN = ComputeNextHydrogenDirectionWorldForSaturatePlanarXY(atom, dirsXY);
                orb = atom.GetLoneOrbitalWithOneElectron(dirN);
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

            FormSigmaBondInstant(atom, hAtom, orb, hOrb, pinSigmaRelaxForAtomA: pinSigmaRelaxNeighbors, pinSigmaRelaxForAtomB: pinSigmaRelaxNeighbors, freezeSigmaNeighborSubtreeRoot: freezeSigmaNeighborSubtreeRoot);
            if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            {
                atom.RedistributeOrbitals(refBondWorldDirection: dirN, pinAtomsForSigmaRelax: pinSigmaRelaxNeighbors, freezeSigmaNeighborSubtreeRoot: freezeSigmaNeighborSubtreeRoot);
            }
            else
            {
                var bondAngle = atom.GetPrimaryBondDirectionAngle();
                atom.RedistributeOrbitals(piBondAngleOverride: bondAngle, pinAtomsForSigmaRelax: pinSigmaRelaxNeighbors, freezeSigmaNeighborSubtreeRoot: freezeSigmaNeighborSubtreeRoot);
            }
            if (incrementalCollisionInvolvingAtoms != null && incrementalCollisionInvolvingAtoms.Count > 0)
                AtomFunction.SetupIgnoreCollisionsInvolvingAtoms(incrementalCollisionInvolvingAtoms);
            else
                AtomFunction.SetupGlobalIgnoreCollisions();
        }

        // if (OrbitalAngleUtility.UseFull3DOrbitalGeometry && atom != null && atom.AtomicNumber > 1)
        //     atom.TryPlaceTetrahedralHydrogenSubstituentsAboutSingleHeavyNeighbor(bondLength);
    }

    /// <summary>
    /// H-auto-style saturation on <paramref name="atoms"/> only (same logic as <see cref="SaturateWithHydrogen"/>, repeated until no progress).
    /// Used after functional-group attachment so substituent OH / NH₂ / etc. fill without touching the rest of the molecule or the anchor atom.
    /// </summary>
    /// <param name="incrementalIgnoreSubstrateParent">With <paramref name="incrementalIgnoreGroupAnchor"/>, limits physics ignore updates to the FG fragment after each H.</param>
    public void SaturateAtomsWithHydrogenPass(
        IReadOnlyList<AtomFunction> atoms,
        HashSet<AtomFunction> pinSigmaRelaxNeighbors = null,
        AtomFunction incrementalIgnoreSubstrateParent = null,
        AtomFunction incrementalIgnoreGroupAnchor = null,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (atoms == null || atoms.Count == 0 || atomPrefab == null || Camera.main == null) return;
        for (int round = 0; round < 16; round++)
        {
            bool anyProgress = false;
            foreach (var a in atoms)
            {
                if (a == null) continue;
                int before = a.GetLoneOrbitalsWithOneElectronSortedByAngle().Count;
                if (before == 0) continue;
                IReadOnlyList<AtomFunction> involving = null;
                if (incrementalIgnoreSubstrateParent != null && incrementalIgnoreGroupAnchor != null)
                    involving = incrementalIgnoreSubstrateParent.GetAtomsOnSideOfSigmaBond(incrementalIgnoreGroupAnchor);
                SaturateWithHydrogen(a, pinSigmaRelaxNeighbors, involving, freezeSigmaNeighborSubtreeRoot);
                int after = a.GetLoneOrbitalsWithOneElectronSortedByAngle().Count;
                if (after < before) anyProgress = true;
            }
            if (!anyProgress) break;
        }
        // When saturating a tethered FG, BuildFunctionalGroup finishes with one involving-atoms pass after Newman stagger.
        if (incrementalIgnoreSubstrateParent == null || incrementalIgnoreGroupAnchor == null)
            AtomFunction.SetupGlobalIgnoreCollisions();
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

    /// <summary>σ directions toward neighbors, projected to XY for orthographic saturation.</summary>
    static List<Vector3> CollectSigmaBondNeighborDirectionsWorldXY(AtomFunction center)
    {
        var full = CollectSigmaBondNeighborDirectionsWorld(center);
        var list = new List<Vector3>();
        foreach (var d in full)
        {
            Vector3 v = d;
            v.z = 0f;
            if (v.sqrMagnitude < 1e-10f) continue;
            list.Add(v.normalized);
        }
        return list;
    }

    /// <summary>
    /// Next H direction in the work plane when σ neighbors are given in XY (orthographic saturation).
    /// Avoids reusing the same preferred axis every step (previous bug: always <see cref="Vector3.right"/>).
    /// </summary>
    static Vector3 ComputeNextHydrogenDirectionWorldForSaturatePlanarXY(AtomFunction center, List<Vector3> dirsXY)
    {
        int n = dirsXY.Count;
        bool piPresent = center != null && center.GetPiBondCount() > 0;

        if (n == 0)
            return Vector3.right;

        if (n == 1)
        {
            var ideal = piPresent ? VseprLayout.GetIdealLocalDirections(3) : VseprLayout.GetIdealLocalDirections(4);
            var aligned = VseprLayout.AlignFirstDirectionTo(ideal, dirsXY[0]);
            for (int i = 1; i < aligned.Length; i++)
            {
                Vector3 v = aligned[i];
                v.z = 0f;
                if (v.sqrMagnitude > 1e-10f)
                    return v.normalized;
            }
            Vector3 perp = new Vector3(-dirsXY[0].y, dirsXY[0].x, 0f);
            return perp.sqrMagnitude > 1e-10f ? perp.normalized : Vector3.up;
        }

        if (n == 2)
        {
            if (piPresent)
            {
                Vector3 v = VseprLayout.TrigonalPlanarThirdDirectionFromTwoBondsWorld(dirsXY[0], dirsXY[1]);
                v.z = 0f;
                return v.sqrMagnitude > 1e-10f ? v.normalized : Vector3.right;
            }
            VseprLayout.TwoHydrogenDirectionsFromBondsPlanarXY(dirsXY[0], dirsXY[1], out var h1, out var _);
            return h1.normalized;
        }

        return VseprLayout.OneHydrogenDirectionFromThreeBondsWorldPlanarXY(dirsXY[0], dirsXY[1], dirsXY[2]);
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

    /// <summary>Attach a preset functional group in edit mode: replace selected H, or bond to selected free 1e orbital.</summary>
    public bool TryAttachFunctionalGroup(FunctionalGroupKind kind)
    {
        if (!editModeActive || atomPrefab == null || Camera.main == null) return false;
        if (selectedAtom == null) return false;
        if (moleculeBuilder == null)
            moleculeBuilder = FindFirstObjectByType<MoleculeBuilder>();
        if (moleculeBuilder == null) return false;

        if (selectedAtom.AtomicNumber == 1 && !orbitalExplicitlySelected)
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
            bondToBreak.BreakBond(parentAtom, instantRedistributionForDestroyPartner: true);
            if (selectedAtom != null) selectedAtom.SetSelectionHighlight(false);
            if (selectedOrbital != null) selectedOrbital.SetHighlighted(false);
            Destroy(hydrogen.gameObject);

            if (!moleculeBuilder.BuildFunctionalGroup(kind, parentAtom, hPos, this, out var sel, preserveAttachmentParentGeometry: true))
                return false;

            selectedAtom = sel;
            selectedMolecule = selectedAtom != null ? selectedAtom.GetConnectedMolecule() : null;
            selectedOrbital = selectedAtom != null ? selectedAtom.GetOrbitalClosestToAngle(0f) : null;
            orbitalExplicitlySelected = false;
            ApplySelectionHighlights();
            RefreshSelectedMoleculeAfterBondChange();
            return true;
        }

        var orb = selectedOrbital ?? selectedAtom.GetOrbitalClosestToAngle(0f);
        if (orb == null || orb.Bond != null || orb.ElectronCount != 1) return false;

        Vector3 dir = orb.transform.TransformDirection(Vector3.right);
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            dir.z = 0f;
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.right;
            else dir.Normalize();
        }
        else if (dir.sqrMagnitude < 1e-8f)
            dir = Vector3.right;
        else
            dir.Normalize();

        float bl = GetBondLength();
        Vector3 anchorPos = selectedAtom.transform.position + dir * bl;

        if (!moleculeBuilder.BuildFunctionalGroup(kind, selectedAtom, anchorPos, this, out _, preserveAttachmentParentGeometry: true))
            return false;

        selectedOrbital?.SetHighlighted(false);
        selectedOrbital = selectedAtom.GetOrbitalClosestToAngle(0f);
        if (selectedOrbital != null) selectedOrbital.SetHighlighted(true);
        RefreshSelectedMoleculeAfterBondChange();
        return true;
    }
}
