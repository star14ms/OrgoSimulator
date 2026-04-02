using UnityEngine;
using UnityEditor;

/// <summary>Lightweight checks for template azimuth / permutation-cost helpers (no scene required).</summary>
public static class TemplateTwistInvariantMenu
{
    [MenuItem("Tools/OrgoSimulator/Validate template-twist math helper")]
    static void ValidateTemplateTwistMath()
    {
        bool ok = AtomFunction.EditorSelfCheck_TemplateTwistRotationPreservesUnitDirs();
        if (ok)
            Debug.Log("[template-twist] Editor self-check passed rotation preserves unit dirs.");
        else
            Debug.Log("[template-twist] Editor self-check FAILED rotation preserves unit dirs.");
    }
}
