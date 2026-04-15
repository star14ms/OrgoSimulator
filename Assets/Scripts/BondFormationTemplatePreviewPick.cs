using UnityEngine;

/// <summary>Marker on template preview stem/tip meshes for raycast picking and description text.</summary>
public class BondFormationTemplatePreviewPick : MonoBehaviour
{
    static readonly System.Collections.Generic.Dictionary<int, BondFormationTemplatePreviewPick> ByLinkedOrbitalInstanceId
        = new System.Collections.Generic.Dictionary<int, BondFormationTemplatePreviewPick>();

    public string Description { get; private set; }

    /// <summary>VSEPR hybrid this stem/tip represents (same permutation slot as template geometry).</summary>
    public ElectronOrbitalFunction LinkedOrbital { get; private set; }

    /// <summary>Tip + stem <see cref="Renderer"/>s for this slot (shared reference across stem and tip picks).</summary>
    public Renderer[] LinkedGroupRenderers { get; private set; }

    public void SetDescription(string description)
    {
        Description = description ?? "";
    }

    public void SetLinkedOrbital(ElectronOrbitalFunction orb, Renderer[] groupRenderers)
    {
        if (LinkedOrbital != null)
            ByLinkedOrbitalInstanceId.Remove(LinkedOrbital.GetInstanceID());
        LinkedOrbital = orb;
        LinkedGroupRenderers = groupRenderers;
        if (LinkedOrbital != null)
            ByLinkedOrbitalInstanceId[LinkedOrbital.GetInstanceID()] = this;
    }

    public static bool TryGetByLinkedOrbital(ElectronOrbitalFunction orbital, out BondFormationTemplatePreviewPick pick)
    {
        pick = null;
        if (orbital == null) return false;
        return ByLinkedOrbitalInstanceId.TryGetValue(orbital.GetInstanceID(), out pick) && pick != null;
    }

    void OnDestroy()
    {
        if (LinkedOrbital != null)
            ByLinkedOrbitalInstanceId.Remove(LinkedOrbital.GetInstanceID());
    }
}
