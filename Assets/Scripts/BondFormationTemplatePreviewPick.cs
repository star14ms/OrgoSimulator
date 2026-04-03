using UnityEngine;

/// <summary>Marker on template preview stem/tip meshes for raycast picking and description text.</summary>
public class BondFormationTemplatePreviewPick : MonoBehaviour
{
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
        LinkedOrbital = orb;
        LinkedGroupRenderers = groupRenderers;
    }
}
