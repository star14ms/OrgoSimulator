using UnityEngine;

/// <summary>
/// Ideal electron-domain directions (VSEPR): maximally separated in 3D for linear through octahedral.
/// Used for perspective (non-orthographic) orbital placement and redistribution.
/// Orthographic (2D) cycloalkane H placement uses <see cref="TwoHydrogenDirectionsFromBondsPlanarXY"/> /
/// <see cref="OneHydrogenDirectionFromThreeBondsWorldPlanarXY"/> so hydrogens stay in the XY work plane.
/// </summary>
public static class VseprLayout
{
    public static Vector3[] GetIdealLocalDirections(int n)
    {
        switch (n)
        {
            case 1:
                return new[] { Vector3.right };
            case 2:
                return new[] { Vector3.right, Vector3.left };
            case 3:
            {
                float t = 120f * Mathf.Deg2Rad;
                return new[]
                {
                    Vector3.right,
                    new Vector3(Mathf.Cos(t), Mathf.Sin(t), 0f),
                    new Vector3(Mathf.Cos(2f * t), Mathf.Sin(2f * t), 0f)
                };
            }
            case 4:
            {
                float s = 1f / Mathf.Sqrt(3f);
                return new[]
                {
                    new Vector3(s, s, s),
                    new Vector3(s, -s, -s),
                    new Vector3(-s, s, -s),
                    new Vector3(-s, -s, s)
                };
            }
            case 5:
            {
                float t = 120f * Mathf.Deg2Rad;
                return new[]
                {
                    Vector3.forward,
                    Vector3.back,
                    Vector3.right,
                    new Vector3(Mathf.Cos(t), Mathf.Sin(t), 0f),
                    new Vector3(Mathf.Cos(2f * t), Mathf.Sin(2f * t), 0f)
                };
            }
            case 6:
                return new[]
                {
                    Vector3.right,
                    Vector3.left,
                    Vector3.up,
                    Vector3.down,
                    Vector3.forward,
                    Vector3.back
                };
            default:
            {
                var dirs = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    float ang = 2f * Mathf.PI * i / n;
                    dirs[i] = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                }
                return dirs;
            }
        }
    }

    /// <summary>
    /// Trigonal planar (n=3): rotate the canonical equilateral triangle in its plane by <paramref name="thetaDeg"/>
    /// about the plane normal (preserves 120°). Then <see cref="AlignFirstDirectionTo"/> maps vertex 0 to the guide.
    /// </summary>
    public static Vector3[] TrigonalIdealRotatedInPlane(float thetaDeg)
    {
        Vector3[] baseIdeal = GetIdealLocalDirections(3);
        Vector3 n0 = Vector3.Cross(baseIdeal[0], baseIdeal[1]);
        if (n0.sqrMagnitude < 1e-14f) return baseIdeal;
        n0.Normalize();
        Quaternion R = Quaternion.AngleAxis(thetaDeg, n0);
        var outD = new Vector3[3];
        for (int i = 0; i < 3; i++)
            outD[i] = (R * baseIdeal[i]).normalized;
        return outD;
    }

    /// <summary>Rotate ideal frame so the first ideal direction matches <paramref name="target"/>.</summary>
    public static Vector3[] AlignFirstDirectionTo(Vector3[] idealDirs, Vector3 target)
    {
        if (idealDirs == null || idealDirs.Length == 0) return idealDirs;
        var t = target.normalized;
        if (t.sqrMagnitude < 1e-8f) t = Vector3.right;
        var a = idealDirs[0].normalized;
        if (a.sqrMagnitude < 1e-8f) a = Vector3.right;
        Quaternion q = Quaternion.FromToRotation(a, t);
        var outDirs = new Vector3[idealDirs.Length];
        for (int i = 0; i < idealDirs.Length; i++)
            outDirs[i] = (q * idealDirs[i]).normalized;
        return outDirs;
    }

    /// <summary>
    /// Regular tetrahedron (four domains): same shape as <see cref="GetIdealLocalDirections"/>(4) but rotate so vertex
    /// <paramref name="vertexIndex"/> (0…3) matches <paramref name="target"/> instead of always aligning vertex 0.
    /// Breaking O–H from different sides yields the same σ axis magnitude pattern but different “first vertex” choice;
    /// always using <see cref="AlignFirstDirectionTo"/> can relabel the frame and force ~90° lone motion when the shell was already tetrahedral.
    /// </summary>
    public static Vector3[] AlignTetrahedronKthVertexTo(Vector3[] idealTet4, int vertexIndex, Vector3 target)
    {
        if (idealTet4 == null || idealTet4.Length != 4 || vertexIndex < 0 || vertexIndex >= 4)
            return AlignFirstDirectionTo(idealTet4, target);
        var t = target.normalized;
        if (t.sqrMagnitude < 1e-8f) t = Vector3.right;
        var vk = idealTet4[vertexIndex].normalized;
        if (vk.sqrMagnitude < 1e-8f) return AlignFirstDirectionTo(idealTet4, target);
        Quaternion q = Quaternion.FromToRotation(vk, t);
        var outDirs = new Vector3[4];
        for (int i = 0; i < 4; i++)
            outDirs[i] = (q * idealTet4[i]).normalized;
        return outDirs;
    }

    /// <summary>
    /// Ring methylene (—CH₂—): two C bond directions from carbon toward neighbors (world). Returns H directions
    /// with H—C—H ≈ 109.5°, **puckered above/below** the plane of the two ring bonds (not coplanar with it).
    /// </summary>
    public static void TwoHydrogenDirectionsFromBonds(Vector3 bondFromCarbonToNeighbor1World, Vector3 bondFromCarbonToNeighbor2World, out Vector3 h1World, out Vector3 h2World)
    {
        Vector3 u1 = bondFromCarbonToNeighbor1World.normalized;
        Vector3 u2 = bondFromCarbonToNeighbor2World.normalized;

        Vector3 ridge = Vector3.Cross(u1, u2);
        if (ridge.sqrMagnitude < 1e-10f)
        {
            Vector3 refAxis = Mathf.Abs(u1.y) < 0.92f ? Vector3.up : Vector3.right;
            ridge = Vector3.Cross(u1, refAxis);
        }
        ridge.Normalize();

        Vector3 bisIn = (u1 + u2).normalized;
        if (bisIn.sqrMagnitude < 1e-10f)
            bisIn = u1;
        Vector3 bisOut = -bisIn;

        // Tetrahedral H—C—H: h± = cos(α)·bisOut ± sin(α)·ridge, cos(2α) = -⅓ → α = ½ arccos(-⅓)
        float alpha = 0.5f * Mathf.Acos(-1f / 3f);
        float c = Mathf.Cos(alpha);
        float s = Mathf.Sin(alpha);
        h1World = (c * bisOut + s * ridge).normalized;
        h2World = (c * bisOut - s * ridge).normalized;
    }

    /// <summary>
    /// Orthographic / 2D work plane: same H—C—H opening as <see cref="TwoHydrogenDirectionsFromBonds"/>, but ridge lies in XY
    /// so both H directions stay in the plane (no pucker along Z).
    /// </summary>
    public static void TwoHydrogenDirectionsFromBondsPlanarXY(Vector3 bondFromCarbonToNeighbor1World, Vector3 bondFromCarbonToNeighbor2World, out Vector3 h1World, out Vector3 h2World)
    {
        Vector3 u1 = bondFromCarbonToNeighbor1World;
        u1.z = 0f;
        Vector3 u2 = bondFromCarbonToNeighbor2World;
        u2.z = 0f;
        if (u1.sqrMagnitude < 1e-10f) u1 = Vector3.right;
        else u1.Normalize();
        if (u2.sqrMagnitude < 1e-10f) u2 = Vector3.left;
        else u2.Normalize();

        Vector3 bisIn = (u1 + u2).normalized;
        if (bisIn.sqrMagnitude < 1e-10f)
            bisIn = u1;
        Vector3 bisOut = -bisIn;

        Vector3 ridge = new Vector3(-bisOut.y, bisOut.x, 0f);
        if (ridge.sqrMagnitude < 1e-10f)
            ridge = Mathf.Abs(u1.x) < 0.92f ? Vector3.right : Vector3.up;
        ridge.Normalize();

        float alpha = 0.5f * Mathf.Acos(-1f / 3f);
        float co = Mathf.Cos(alpha);
        float si = Mathf.Sin(alpha);
        h1World = (co * bisOut + si * ridge).normalized;
        h2World = (co * bisOut - si * ridge).normalized;
    }

    /// <summary>
    /// sp² center with two σ axes (from center toward neighbors, world): third σ direction in the same plane,
    /// ~120° from each (e.g. aromatic C—H radially outward from a regular ring when the two C—C axes are 120° apart).
    /// </summary>
    public static Vector3 TrigonalPlanarThirdDirectionFromTwoBondsWorld(Vector3 toNeighbor1World, Vector3 toNeighbor2World)
    {
        Vector3 u1 = toNeighbor1World.normalized;
        Vector3 u2 = toNeighbor2World.normalized;
        Vector3 sum = u1 + u2;
        if (sum.sqrMagnitude < 1e-10f)
            return Vector3.up;
        return (-sum).normalized;
    }

    /// <summary>
    /// sp³ carbon with three σ substituents already placed: approximate direction for the fourth (e.g. one H)
    /// opposite the umbrella of the three bond vectors (from C toward neighbors, world).
    /// </summary>
    public static Vector3 OneHydrogenDirectionFromThreeBondsWorld(Vector3 toNeighbor1World, Vector3 toNeighbor2World, Vector3 toNeighbor3World)
    {
        Vector3 s = toNeighbor1World.normalized + toNeighbor2World.normalized + toNeighbor3World.normalized;
        if (s.sqrMagnitude < 1e-10f) return Vector3.up;
        return (-s).normalized;
    }

    /// <summary>
    /// Orthographic / 2D: like <see cref="OneHydrogenDirectionFromThreeBondsWorld"/> but bond vectors are projected to XY first;
    /// degenerate umbrella uses <see cref="Vector3.right"/> instead of world up.
    /// </summary>
    public static Vector3 OneHydrogenDirectionFromThreeBondsWorldPlanarXY(Vector3 toNeighbor1World, Vector3 toNeighbor2World, Vector3 toNeighbor3World)
    {
        Vector3 n1 = NormalizeBondDirectionXY(toNeighbor1World);
        Vector3 n2 = NormalizeBondDirectionXY(toNeighbor2World);
        Vector3 n3 = NormalizeBondDirectionXY(toNeighbor3World);
        if (n1.sqrMagnitude < 1e-10f) n1 = Vector3.right;
        if (n2.sqrMagnitude < 1e-10f) n2 = Vector3.up;
        if (n3.sqrMagnitude < 1e-10f) n3 = Vector3.left;
        Vector3 sum = n1 + n2 + n3;
        if (sum.sqrMagnitude < 1e-10f) return Vector3.right;
        return (-sum).normalized;
    }

    static Vector3 NormalizeBondDirectionXY(Vector3 v)
    {
        v.z = 0f;
        if (v.sqrMagnitude < 1e-10f) return Vector3.zero;
        return v.normalized;
    }
}
