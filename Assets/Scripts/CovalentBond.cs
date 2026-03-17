using UnityEngine;

/// <summary>
/// Represents a covalent bond between two atoms. Owns the shared orbital and its electrons.
/// Positions the orbital between the two atoms and notifies both when electrons change.
/// </summary>
public class CovalentBond : MonoBehaviour
{
    [SerializeField] AtomFunction atomA;
    [SerializeField] AtomFunction atomB;
    ElectronOrbitalFunction orbital;

    public AtomFunction AtomA => atomA;
    public AtomFunction AtomB => atomB;
    public ElectronOrbitalFunction Orbital => orbital;

    public int ElectronCount => orbital != null ? orbital.ElectronCount : 0;

    public static CovalentBond Create(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction sharedOrbital)
    {
        if (sourceAtom == null || targetAtom == null || sharedOrbital == null) return null;
        if (sourceAtom == targetAtom) return null;

        var bondGo = new GameObject("CovalentBond");
        var bond = bondGo.AddComponent<CovalentBond>();
        bond.Initialize(sourceAtom, targetAtom, sharedOrbital);
        return bond;
    }

    void Initialize(AtomFunction a, AtomFunction b, ElectronOrbitalFunction orb)
    {
        atomA = a;
        atomB = b;
        orbital = orb;

        atomA.RegisterBond(this);
        atomB.RegisterBond(this);

        orbital.SetBond(this);
        orbital.SetBondedAtom(null);

        atomA.SetupIgnoreCollisions();
        atomB.SetupIgnoreCollisions();
    }

    public void NotifyElectronCountChanged()
    {
        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();
    }

    public void BreakBond(AtomFunction returnOrbitalTo)
    {
        if (orbital == null) return;

        atomA?.UnregisterBond(this);
        atomB?.UnregisterBond(this);

        int totalElectrons = orbital.ElectronCount;
        var otherAtom = returnOrbitalTo == atomA ? atomB : atomA;
        var prefab = returnOrbitalTo.OrbitalPrefab ?? otherAtom?.OrbitalPrefab;
        if (prefab == null) return;

        orbital.SetBond(null);
        orbital.SetBondedAtom(returnOrbitalTo);
        orbital.transform.SetParent(returnOrbitalTo.transform);
        var dirToOther = (otherAtom.transform.position - returnOrbitalTo.transform.position).normalized;
        if (dirToOther.sqrMagnitude < 0.01f) dirToOther = Vector3.right;
        var slotA = returnOrbitalTo.GetSlotForNewOrbital(dirToOther, null);
        orbital.transform.localPosition = slotA.position;
        orbital.transform.localRotation = slotA.rotation;
        orbital.transform.localScale = Vector3.one * 0.6f;
        orbital.ElectronCount = totalElectrons;
        returnOrbitalTo.BondOrbital(orbital);

        var newOrbital = Instantiate(prefab, otherAtom.transform);
        newOrbital.transform.localScale = Vector3.one * 0.6f;
        var dirToReturn = (returnOrbitalTo.transform.position - otherAtom.transform.position).normalized;
        if (dirToReturn.sqrMagnitude < 0.01f) dirToReturn = Vector3.left;
        var slotB = otherAtom.GetSlotForNewOrbital(dirToReturn, null);
        newOrbital.transform.localPosition = slotB.position;
        newOrbital.transform.localRotation = slotB.rotation;
        newOrbital.ElectronCount = 0;
        newOrbital.SetBondedAtom(otherAtom);
        otherAtom.BondOrbital(newOrbital);

        atomA?.RefreshCharge();
        atomB?.RefreshCharge();
        atomA?.SetupIgnoreCollisions();
        atomB?.SetupIgnoreCollisions();

        orbital = null;
        Destroy(gameObject);
    }
}
