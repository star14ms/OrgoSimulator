using UnityEngine;

/// <summary>
/// Stem mesh colliders are unreliable under non-uniform cylinder scale. This stores the world segment + tolerance so
/// <see cref="BondFormationTemplatePreviewInput"/> can pick stems by geometry (ray vs segment) instead of Physics.Raycast.
/// </summary>
[DefaultExecutionOrder(-80)]
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

    void LateUpdate()
    {
        if (Pick == null) return;
        // Default cylinder mesh runs along local Y from -1..+1; stem has no collider for Physics.Raycast — keep segment in sync with the scaled mesh every frame.
        SegmentAWorld = transform.TransformPoint(0f, -1f, 0f);
        SegmentBWorld = transform.TransformPoint(0f, 1f, 0f);
    }
}
