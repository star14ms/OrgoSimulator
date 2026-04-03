using UnityEngine;

/// <summary>
/// Stem mesh colliders are unreliable under non-uniform cylinder scale. This stores the world segment + tolerance so
/// <see cref="BondFormationTemplatePreviewInput"/> can pick stems by geometry (ray vs segment) instead of Physics.Raycast.
/// </summary>
public sealed class BondFormationTemplateStemRayPick : MonoBehaviour
{
    public Vector3 SegmentAWorld;
    public Vector3 SegmentBWorld;
    public float PickRadiusWorld;
    public BondFormationTemplatePreviewPick Pick;

    public void SetSegment(Vector3 a, Vector3 b, float pickRadiusWorld, BondFormationTemplatePreviewPick pick)
    {
        SegmentAWorld = a;
        SegmentBWorld = b;
        PickRadiusWorld = pickRadiusWorld;
        Pick = pick;
    }
}
