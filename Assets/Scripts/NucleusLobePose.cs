using UnityEngine;

/// <summary>
/// Unified lobe pose for nucleus-local hybrid targets: one applicator writes both <c>localPosition</c> and <c>localRotation</c>
/// so hybrid +X and pivot→center offset stay in a documented relationship (orbital redistribution / σ prebond / phase-1 lerp).
/// </summary>
/// <remarks>
/// Policy: new code that poses nucleus-child (or bond-parented-from-nucleus-ideal) hybrid lobes should build a
/// <see cref="NucleusLobeSpec"/> and call <see cref="NucleusLobePose.ApplyToNucleusChild"/> or
/// <see cref="NucleusLobePose.ApplyToOrbitalFromNucleusIdeal"/> instead of assigning <c>orb.transform.localRotation</c>
/// (or world rotation) without updating <c>localPosition</c> in the same conceptual operation.
/// Optional CI: <c>rg 'transform\\.localRotation\\s*=' Assets/Scripts --glob '*.cs'</c> and exclude this file / known exceptions.
/// </remarks>
public enum NucleusLobeDisplacementMode
{
    /// <summary>Center lies on the same ray as hybrid +X: <c>localPosition = tipDir * radius</c>.</summary>
    CenterAlongTip,
    /// <summary>Hybrid +X along <c>tipDir</c>, center on opposite ray: <c>localPosition = -tipDir * radius</c>.</summary>
    CenterAlongAntiTip,
    /// <summary>Use <see cref="NucleusLobeSpec.ExplicitLocalOffset"/>; rotation still aligns +X to <c>tipDir</c>.</summary>
    ExplicitLocalOffset,
}

/// <summary>Skip or constrain applicator (prebond pin world freeze = skip post-refresh enforcement).</summary>
[System.Flags]
public enum NucleusLobeFreezeFlags
{
    None = 0,
    /// <summary>Do not modify the orbital transform.</summary>
    SkipApply = 1,
}

/// <summary>Target pose in <b>nucleus</b> local space (for bond-parented orbitals, applicator converts to parent space).</summary>
public struct NucleusLobeSpec
{
    public Vector3 TipDirectionNucleusLocal;
    public NucleusLobeDisplacementMode DisplacementMode;
    public float Radius;
    public Vector3 ExplicitLocalOffset;
    public Quaternion? RollHint;
    public Quaternion? RollContinuity;
    public NucleusLobeFreezeFlags Freeze;
    /// <summary>When true, <see cref="ExplicitLocalRotation"/> is used instead of canonical roll from tip direction.</summary>
    public bool HasExplicitLocalRotation;
    public Quaternion ExplicitLocalRotation;

    public static NucleusLobeSpec ForTryMatchSnap(Vector3 newDirNucleusLocal, float bondRadius, Quaternion rollHint)
    {
        Vector3 d = newDirNucleusLocal.sqrMagnitude > 1e-16f ? newDirNucleusLocal.normalized : Vector3.right;
        var (pos, _) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(d, bondRadius, rollHint);
        return new NucleusLobeSpec
        {
            TipDirectionNucleusLocal = d,
            DisplacementMode = NucleusLobeDisplacementMode.CenterAlongTip,
            Radius = pos.magnitude,
            ExplicitLocalOffset = default,
            RollHint = rollHint,
            RollContinuity = rollHint,
            Freeze = NucleusLobeFreezeFlags.None,
        };
    }

    public static NucleusLobeSpec ForPinReservedDir(Vector3 pinDirNucleusLocal, float bondRadius, Quaternion rollHint)
    {
        return ForTryMatchSnap(pinDirNucleusLocal, bondRadius, rollHint);
    }

    /// <summary>Canonical slot along tip using the two-arg <see cref="ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection"/> (no roll hint), e.g. lone drag rigid steps.</summary>
    public static NucleusLobeSpec ForCanonicalSlotAlongTipNoRollHint(Vector3 dirNucleusLocal, float bondRadius)
    {
        Vector3 d = dirNucleusLocal.sqrMagnitude > 1e-16f ? dirNucleusLocal.normalized : Vector3.right;
        var (pos, _) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(d, bondRadius);
        return new NucleusLobeSpec
        {
            TipDirectionNucleusLocal = d,
            DisplacementMode = NucleusLobeDisplacementMode.CenterAlongTip,
            Radius = pos.magnitude,
            ExplicitLocalOffset = default,
            RollHint = null,
            RollContinuity = null,
            Freeze = NucleusLobeFreezeFlags.None,
        };
    }

    /// <summary>Coherent pose: tip direction = offset direction, <see cref="NucleusLobeDisplacementMode.CenterAlongTip"/>.</summary>
    public static NucleusLobeSpec FromCurrentNucleusChildOffset(ElectronOrbitalFunction orb, float bondRadius)
    {
        Vector3 lp = orb.transform.localPosition;
        if (lp.sqrMagnitude < 1e-16f)
        {
            return new NucleusLobeSpec
            {
                TipDirectionNucleusLocal = Vector3.right,
                DisplacementMode = NucleusLobeDisplacementMode.CenterAlongTip,
                Radius = bondRadius * 0.6f,
                RollHint = orb.transform.localRotation,
                RollContinuity = orb.transform.localRotation,
                Freeze = NucleusLobeFreezeFlags.None,
            };
        }

        Vector3 dir = lp.normalized;
        return new NucleusLobeSpec
        {
            TipDirectionNucleusLocal = dir,
            DisplacementMode = NucleusLobeDisplacementMode.CenterAlongTip,
            Radius = lp.magnitude,
            RollHint = orb.transform.localRotation,
            RollContinuity = orb.transform.localRotation,
            Freeze = NucleusLobeFreezeFlags.None,
        };
    }
}

/// <summary>Static applicators for <see cref="NucleusLobeSpec"/>; prefer these over ad-hoc <c>localRotation</c> writes.</summary>
public static class NucleusLobePose
{
    const float kCanonicalSlotFrac = 0.6f;

    /// <summary>Apply to a nucleus direct child. <paramref name="atom"/> must be the parent nucleus.</summary>
    public static void ApplyToNucleusChild(AtomFunction atom, ElectronOrbitalFunction orb, in NucleusLobeSpec spec)
    {
        if (atom == null || orb == null || orb.transform.parent != atom.transform) return;
        if ((spec.Freeze & NucleusLobeFreezeFlags.SkipApply) != 0) return;

        Vector3 tip = spec.TipDirectionNucleusLocal.sqrMagnitude > 1e-16f
            ? spec.TipDirectionNucleusLocal.normalized
            : Vector3.right;

        Quaternion rot;
        if (spec.HasExplicitLocalRotation)
            rot = spec.ExplicitLocalRotation;
        else
        {
            Quaternion? hint = spec.RollHint;
            Quaternion? cont = spec.RollContinuity ?? spec.RollHint;
            if (hint.HasValue)
                (_, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(
                    tip, atom.BondRadius, hint, cont);
            else
                (_, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(tip, atom.BondRadius);
        }

        float r = spec.Radius;
        if (r < 1e-6f)
            r = atom.BondRadius * kCanonicalSlotFrac;

        Vector3 lp;
        switch (spec.DisplacementMode)
        {
            case NucleusLobeDisplacementMode.CenterAlongAntiTip:
                lp = -tip * r;
                break;
            case NucleusLobeDisplacementMode.ExplicitLocalOffset:
                lp = spec.ExplicitLocalOffset;
                break;
            default:
                lp = tip * r;
                break;
        }

        orb.transform.localPosition = lp;
        orb.transform.localRotation = rot;
        DebugAssertInvariant(orb, spec);
    }

    /// <summary>Apply using nucleus-space tip direction; converts to orbital parent local when parent is a bond.</summary>
    public static void ApplyToOrbitalFromNucleusIdeal(AtomFunction nucleus, ElectronOrbitalFunction orb, in NucleusLobeSpec spec)
    {
        if (nucleus == null || orb == null) return;
        if ((spec.Freeze & NucleusLobeFreezeFlags.SkipApply) != 0) return;

        if (orb.transform.parent == nucleus.transform)
        {
            ApplyToNucleusChild(nucleus, orb, spec);
            return;
        }

        Vector3 tipN = spec.TipDirectionNucleusLocal.sqrMagnitude > 1e-16f
            ? spec.TipDirectionNucleusLocal.normalized
            : Vector3.right;
        Transform p = orb.transform.parent;
        Vector3 dirLocal = tipN;
        if (p != null && p != nucleus.transform)
        {
            Vector3 w = nucleus.transform.TransformDirection(tipN);
            dirLocal = p.InverseTransformDirection(w).normalized;
        }

        Quaternion rot;
        if (spec.HasExplicitLocalRotation)
            rot = spec.ExplicitLocalRotation;
        else
        {
            Quaternion? hint = spec.RollHint;
            Quaternion? cont = spec.RollContinuity ?? spec.RollHint;
            if (hint.HasValue)
                (_, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(
                    dirLocal, nucleus.BondRadius, hint, cont);
            else
                (_, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(dirLocal, nucleus.BondRadius);
        }

        float r = spec.Radius;
        if (r < 1e-6f)
            r = nucleus.BondRadius * kCanonicalSlotFrac;

        Vector3 lpParent;
        switch (spec.DisplacementMode)
        {
            case NucleusLobeDisplacementMode.CenterAlongAntiTip:
                lpParent = -dirLocal * r;
                break;
            case NucleusLobeDisplacementMode.ExplicitLocalOffset:
                lpParent = spec.ExplicitLocalOffset;
                break;
            default:
                lpParent = dirLocal * r;
                break;
        }

        orb.transform.localPosition = lpParent;
        orb.transform.localRotation = rot;
        DebugAssertInvariant(orb, spec);
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
    static void DebugAssertInvariant(ElectronOrbitalFunction orb, in NucleusLobeSpec spec)
    {
        Vector3 lp = orb.transform.localPosition;
        Vector3 tipAxis = (orb.transform.localRotation * Vector3.right).normalized;
        if (lp.sqrMagnitude < 1e-16f) return;
        float ang = Vector3.Angle(lp.normalized, tipAxis);
        if (spec.HasExplicitLocalRotation || spec.DisplacementMode == NucleusLobeDisplacementMode.ExplicitLocalOffset)
            return;
        if (spec.DisplacementMode == NucleusLobeDisplacementMode.CenterAlongAntiTip)
        {
            if (ang < 179f)
                Debug.LogWarning("[nucleus-lobe-pose] invariant: expected ~180° between offset and +X for AntiTip, ang=" + ang);
        }
        else if (spec.DisplacementMode == NucleusLobeDisplacementMode.CenterAlongTip)
        {
            if (ang > 1.5f)
                Debug.LogWarning("[nucleus-lobe-pose] invariant: expected ~0° between offset and +X for AlongTip, ang=" + ang);
        }
    }
}
