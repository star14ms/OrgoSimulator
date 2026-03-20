using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Infinite plane for pointer picking, atom drag, and spawning. In perspective + orbit, <see cref="SyncToPerspectiveOrbit"/> keeps the plane
/// perpendicular to the camera and at a comfortable depth (clamped) in front of the lens so it spans the frustum.
/// </summary>
public class MoleculeWorkPlane : MonoBehaviour
{
    public static MoleculeWorkPlane Instance { get; private set; }

    [SerializeField] Vector3 planeNormal = new Vector3(0f, 0f, 1f);
    [SerializeField] Vector3 planePoint = Vector3.zero;

    [Header("Debug")]
    [Tooltip("Editor: gizmos in the Scene view. Play: 3D lines in the Game view (LineRenderer) + Scene gizmos.")]
    [SerializeField] bool debugDrawWorkPlane;
    [SerializeField] Color debugPlaneEdgeColor = new Color(0.2f, 0.95f, 1f, 1f);
    [SerializeField] Color debugPlaneNormalColor = new Color(1f, 0.55f, 0.15f, 1f);
    [Tooltip("Half-width of the wire square drawn on the plane (world units). The real plane is infinite.")]
    [SerializeField] float debugPlaneHalfExtent = 12f;
    [Tooltip("Extra Debug.DrawLine pass each frame (Scene view while playing only).")]
    [SerializeField] bool debugDrawWorkPlaneSceneViewLines;
    [SerializeField] float debugLineWidth = 0.04f;

    GameObject _debugRoot;
    LineRenderer _lrSquare;
    LineRenderer _lrNormal;
    Material _matSquare;
    Material _matNormal;

    /// <summary>Current plane anchor (world space). Used when aligning molecules.</summary>
    public Vector3 WorldPlanePoint => planePoint;

    /// <summary>Unnormalized; use <see cref="WorldPlaneNormal"/> for a unit normal.</summary>
    public Vector3 WorldPlaneNormalRaw => planeNormal;

    public Vector3 WorldPlaneNormal =>
        planeNormal.sqrMagnitude > 1e-10f ? planeNormal.normalized : Vector3.forward;

    void Awake()
    {
        Instance = this;
        // Run after Instance is set (script order vs. ScrollOrbitCamera may vary).
        Camera.main?.GetComponent<ScrollOrbitCamera>()?.SyncMoleculeWorkPlaneToView();
    }

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        if (!debugDrawWorkPlane)
        {
            if (_debugRoot != null) _debugRoot.SetActive(false);
            return;
        }

        EnsureDebugPlayModeVisual();
        UpdateDebugPlayModeVisual();
        if (debugDrawWorkPlaneSceneViewLines) DrawWorkPlaneDebugLines();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        if (_matSquare != null)
        {
            Destroy(_matSquare);
            _matSquare = null;
        }
        if (_matNormal != null)
        {
            Destroy(_matNormal);
            _matNormal = null;
        }
    }

    void OnDrawGizmos()
    {
        if (!debugDrawWorkPlane) return;
        DrawWorkPlaneGizmos();
    }

    static void OrthonormalBasisOnPlane(Vector3 unitNormal, out Vector3 tangent, out Vector3 bitangent)
    {
        if (Mathf.Abs(Vector3.Dot(unitNormal, Vector3.up)) > 0.92f)
            tangent = Vector3.Cross(unitNormal, Vector3.right).normalized;
        else
            tangent = Vector3.Cross(unitNormal, Vector3.up).normalized;
        bitangent = Vector3.Cross(unitNormal, tangent);
    }

    void GetDebugPlaneQuad(out Vector3 v00, out Vector3 v01, out Vector3 v11, out Vector3 v10, out Vector3 center, out Vector3 normal)
    {
        normal = WorldPlaneNormal;
        center = WorldPlanePoint;
        OrthonormalBasisOnPlane(normal, out var t, out var bt);
        float e = Mathf.Max(0.05f, debugPlaneHalfExtent);
        v00 = center - t * e - bt * e;
        v01 = center - t * e + bt * e;
        v11 = center + t * e + bt * e;
        v10 = center + t * e - bt * e;
    }

    void DrawWorkPlaneGizmos()
    {
        GetDebugPlaneQuad(out var v00, out var v01, out var v11, out var v10, out var center, out var normal);
        Gizmos.color = debugPlaneEdgeColor;
        Gizmos.DrawLine(v00, v01);
        Gizmos.DrawLine(v01, v11);
        Gizmos.DrawLine(v11, v10);
        Gizmos.DrawLine(v10, v00);
        Gizmos.color = debugPlaneNormalColor;
        float nLen = Mathf.Clamp(debugPlaneHalfExtent * 0.35f, 0.35f, 3f);
        Gizmos.DrawLine(center, center + normal * nLen);
        Gizmos.DrawSphere(center, Mathf.Max(0.02f, debugPlaneHalfExtent * 0.015f));
    }

    void DrawWorkPlaneDebugLines()
    {
        GetDebugPlaneQuad(out var v00, out var v01, out var v11, out var v10, out var center, out var normal);
        Debug.DrawLine(v00, v01, debugPlaneEdgeColor);
        Debug.DrawLine(v01, v11, debugPlaneEdgeColor);
        Debug.DrawLine(v11, v10, debugPlaneEdgeColor);
        Debug.DrawLine(v10, v00, debugPlaneEdgeColor);
        float nLen = Mathf.Clamp(debugPlaneHalfExtent * 0.35f, 0.35f, 3f);
        Debug.DrawLine(center, center + normal * nLen, debugPlaneNormalColor);
    }

    void EnsureDebugPlayModeVisual()
    {
        if (_debugRoot != null) return;
        _debugRoot = new GameObject("WorkPlane_DebugVisual");
        _debugRoot.transform.SetParent(transform, false);

        _matSquare = CreateDebugLineMaterial(debugPlaneEdgeColor);
        _matNormal = CreateDebugLineMaterial(debugPlaneNormalColor);

        _lrSquare = _debugRoot.AddComponent<LineRenderer>();
        SetupLineRenderer(_lrSquare, _matSquare, debugLineWidth, true);
        _lrSquare.loop = true;
        _lrSquare.positionCount = 4;

        var nGo = new GameObject("Normal");
        nGo.transform.SetParent(_debugRoot.transform, false);
        _lrNormal = nGo.AddComponent<LineRenderer>();
        SetupLineRenderer(_lrNormal, _matNormal, debugLineWidth, false);
        _lrNormal.loop = false;
        _lrNormal.positionCount = 2;
    }

    static Material CreateDebugLineMaterial(Color c)
    {
        var builtin = Resources.GetBuiltinResource<Material>("Default-Line.mat");
        if (builtin != null)
        {
            var matBuiltin = new Material(builtin);
            if (matBuiltin.HasProperty("_Color")) matBuiltin.color = c;
            return matBuiltin;
        }

        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply");
        if (sh == null) sh = Shader.Find("Hidden/Internal-Colored");
        if (sh == null) return null;
        var matShader = new Material(sh);
        if (matShader.HasProperty("_Color")) matShader.color = c;
        if (matShader.HasProperty("_TintColor")) matShader.SetColor("_TintColor", c);
        if (matShader.HasProperty("_BaseColor")) matShader.SetColor("_BaseColor", c);
        return matShader;
    }

    static void SetupLineRenderer(LineRenderer lr, Material mat, float width, bool loop)
    {
        lr.loop = loop;
        lr.useWorldSpace = true;
        lr.widthMultiplier = 1f;
        lr.startWidth = width;
        lr.endWidth = width;
        if (mat != null) lr.material = mat;
        lr.numCornerVertices = 4;
        lr.numCapVertices = 2;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.allowOcclusionWhenDynamic = false;
    }

    void UpdateDebugPlayModeVisual()
    {
        GetDebugPlaneQuad(out var v00, out var v01, out var v11, out var v10, out var center, out var normal);
        float nLen = Mathf.Clamp(debugPlaneHalfExtent * 0.35f, 0.35f, 3f);

        _lrSquare.SetPosition(0, v00);
        _lrSquare.SetPosition(1, v01);
        _lrSquare.SetPosition(2, v11);
        _lrSquare.SetPosition(3, v10);
        _lrSquare.startColor = _lrSquare.endColor = debugPlaneEdgeColor;
        if (_matSquare != null && _matSquare.HasProperty("_BaseColor")) _matSquare.SetColor("_BaseColor", debugPlaneEdgeColor);
        if (_matSquare != null && _matSquare.HasProperty("_Color")) _matSquare.color = debugPlaneEdgeColor;
        _lrSquare.startWidth = _lrSquare.endWidth = debugLineWidth;

        _lrNormal.SetPosition(0, center);
        _lrNormal.SetPosition(1, center + normal * nLen);
        _lrNormal.startColor = _lrNormal.endColor = debugPlaneNormalColor;
        if (_matNormal != null && _matNormal.HasProperty("_BaseColor")) _matNormal.SetColor("_BaseColor", debugPlaneNormalColor);
        if (_matNormal != null && _matNormal.HasProperty("_Color")) _matNormal.color = debugPlaneNormalColor;
        _lrNormal.startWidth = _lrNormal.endWidth = debugLineWidth;
    }

    /// <summary>
    /// Perspective only: plane normal = camera forward; plane depth = orbit focus projected onto the view axis, clamped.
    /// Called when the orbit camera enables or once orbit scroll has settled so drag/create use this sheet.
    /// </summary>
    public void SyncToPerspectiveOrbit(Camera cam, Vector3 orbitFocusWorld, float minDepthAlongView, float maxDepthAlongView)
    {
        if (cam == null || cam.orthographic) return;
        Vector3 f = cam.transform.forward;
        if (f.sqrMagnitude < 1e-10f) return;
        f.Normalize();
        float d = Vector3.Dot(orbitFocusWorld - cam.transform.position, f);
        float lo = Mathf.Min(minDepthAlongView, maxDepthAlongView);
        float hi = Mathf.Max(minDepthAlongView, maxDepthAlongView);
        d = Mathf.Clamp(d, lo, hi);
        planeNormal = f;
        planePoint = cam.transform.position + f * d;
    }

    public bool TryGetWorldPoint(Camera cam, Vector2 screenPosition, out Vector3 world)
    {
        world = default;
        if (cam == null)
            return false;

        var plane = new Plane(planeNormal.normalized, planePoint);
        var ray = cam.ScreenPointToRay(screenPosition);
        if (!plane.Raycast(ray, out float enter))
            return false;

        world = ray.GetPoint(enter);
        return true;
    }

    /// <summary>Maps viewport xy (z ignored) to a point on this work plane; z component of viewport is unused.</summary>
    public bool TryViewportToPlane(Camera cam, Vector3 viewportXy01, out Vector3 world)
    {
        if (cam == null)
        {
            world = default;
            return false;
        }

        var sp = cam.ViewportToScreenPoint(viewportXy01);
        return TryGetWorldPoint(cam, new Vector2(sp.x, sp.y), out world);
    }
}
