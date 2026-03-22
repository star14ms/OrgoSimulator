using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Small sphere at world (0,0,0) to mark the origin. No collider; URP Unlit.
/// When <see cref="ScrollOrbitCamera"/> orbit pivot is the initial focus (e.g. origin), color matches the edit selection ring; otherwise <see cref="defaultColor"/>.
/// </summary>
public sealed class WorldOriginMarker : MonoBehaviour
{
    [SerializeField] float diameter = 0.16f;
    [Tooltip("Used when orbit pivot is on the selected atom (scroll center not at initial/origin).")]
    [SerializeField] Color defaultColor = Color.white;

    Material dotMaterial;

    void Awake()
    {
        transform.position = Vector3.zero;
    }

    void Start()
    {
        transform.position = Vector3.zero;

        var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dot.name = "OriginDot";
        dot.transform.SetParent(transform, false);
        dot.transform.localPosition = Vector3.zero;
        dot.transform.localScale = Vector3.one * Mathf.Max(0.01f, diameter);
        Destroy(dot.GetComponent<Collider>());

        var mr = dot.GetComponent<MeshRenderer>();
        if (mr == null) return;

        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Universal");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) return;

        dotMaterial = new Material(sh) { name = "WorldOriginDot" };
        mr.sharedMaterial = dotMaterial;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.allowOcclusionWhenDynamic = false;

        RefreshDotColor();
    }

    void LateUpdate()
    {
        RefreshDotColor();
    }

    void RefreshDotColor()
    {
        if (dotMaterial == null) return;

        Color c = defaultColor;
        var cam = Camera.main;
        if (cam != null && !cam.orthographic)
        {
            var orbit = cam.GetComponent<ScrollOrbitCamera>();
            if (orbit != null && orbit.IsOrbitFocusAtInitialPivot())
                c = AtomFunction.GetSelectionHighlightRingColorRgb();
        }

        if (dotMaterial.HasProperty("_BaseColor")) dotMaterial.SetColor("_BaseColor", c);
        if (dotMaterial.HasProperty("_Color")) dotMaterial.SetColor("_Color", c);
    }
}
