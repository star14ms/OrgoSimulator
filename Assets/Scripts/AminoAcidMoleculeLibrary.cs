using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Creates proteinogenic amino-acid molecules from three-letter abbreviations.
/// Uses NH2-CH(R)-COOH backbone and builds an explicit heavy-atom side chain.
/// </summary>
public static class AminoAcidMoleculeLibrary
{
    public static readonly IReadOnlyList<string> ThreeLetterAbbreviations = new[]
    {
        "Ala", "Arg", "Asn", "Asp", "Cys",
        "Gln", "Glu", "Gly", "His", "Ile",
        "Leu", "Lys", "Met", "Phe", "Pro",
        "Ser", "Thr", "Trp", "Tyr", "Val"
    };

    public static bool TryCreate(
        string abbreviation,
        Vector3 center,
        MoleculeBuilder moleculeBuilder,
        EditModeManager editModeManager,
        Func<int, Vector3, int, AtomFunction> spawnAtom,
        out AtomFunction alphaCarbon)
    {
        alphaCarbon = null;
        if (string.IsNullOrEmpty(abbreviation) || editModeManager == null || spawnAtom == null)
            return false;
        bool prevHAuto = editModeManager.HAutoMode;
        editModeManager.SetHAutoMode(false); // Preserve sequential heavy-atom growth path for zig-zag direction control.
        try
        {
            alphaCarbon = spawnAtom(6, center, 0);
            if (alphaCarbon == null) return false;
            editModeManager.OnAtomClicked(alphaCarbon);

            // Backbone built exactly like edit-mode atom adds.
            var amineN = AddByEditMode(alphaCarbon, 7, editModeManager);
            var carboxylC = AddByEditMode(alphaCarbon, 6, editModeManager);
            if (amineN == null || carboxylC == null) return false;

            // Carboxyl: π O at deterministic hint, then hydroxyl O via edit mode so C=O planar redistribute does not scramble –OH.
            AtomFunction carbonylO = null;
            AtomFunction hydroxylO = null;
            if (TryAttachPredeterminedCarbonylOAndPromotePi(
                    carboxylC,
                    GetCarboxylOxygenPosition(carboxylC, alphaCarbon, true),
                    spawnAtom,
                    editModeManager,
                    out carbonylO))
                hydroxylO = AddByEditModeToAcylAfterCarbonylPlanar(carboxylC, alphaCarbon, carbonylO, 8, editModeManager, alphaCarbon, null);
            if (hydroxylO != null)
            {
                Vector3 ohDir = (hydroxylO.transform.position - carboxylC.transform.position).normalized;
                if (carbonylO != null)
                    ohDir += (hydroxylO.transform.position - carbonylO.transform.position).normalized;
                if (ohDir.sqrMagnitude < 1e-10f)
                    ohDir = hydroxylO.transform.up;
                AddByEditMode(hydroxylO, 1, editModeManager, preferredDirectionWorld: ohDir.normalized);
            }

            // Sidechain root (beta carbon), except glycine.
            AtomFunction beta = null;
            if (!string.Equals(abbreviation, "Gly"))
                beta = AddByEditMode(alphaCarbon, 6, editModeManager);

            // α–NH₂: lone lobes toward C(COOH)→Cα, then Newman stagger vs Cα substituents (needs Cβ present when not Gly).
            AlignBackboneAmineLonePairsTowardCarboxylToAlpha(amineN, alphaCarbon, carboxylC);
            if (amineN != null && alphaCarbon != null)
                amineN.TryStaggerNewmanRelativeToPartner(alphaCarbon);

            BuildSideChainByEditMode(abbreviation, alphaCarbon, beta, amineN, carboxylC, editModeManager, spawnAtom);
            // H fill: no full redistribute; on π centers use lobe axes for H spawn (not VSEPR-only) and skip σ–H neighbor snap after each H.
            ForceFinalHydrogenFill(alphaCarbon, editModeManager);
            AtomFunction.SetupGlobalIgnoreCollisions();
            return true;
        }
        finally
        {
            editModeManager.SetHAutoMode(prevHAuto);
        }
    }

    static AtomFunction AddByEditMode(
        AtomFunction anchor,
        int atomicNumber,
        EditModeManager editModeManager,
        AtomFunction twoStepsBehind = null,
        Vector3? preferredDirectionWorld = null,
        int initialChargeOverride = 0)
    {
        if (anchor == null || editModeManager == null) return null;
        var before = anchor.GetConnectedMolecule();
        var beforeSet = before != null ? new HashSet<AtomFunction>(before) : new HashSet<AtomFunction>();
        editModeManager.OnAtomClicked(anchor);
        if (preferredDirectionWorld.HasValue && preferredDirectionWorld.Value.sqrMagnitude > 1e-10f)
        {
            editModeManager.SelectLoneOrbitalTowardDirection(preferredDirectionWorld.Value.normalized);
        }
        else if (twoStepsBehind != null)
        {
            Vector3 zigzagDir = anchor.transform.position - twoStepsBehind.transform.position;
            editModeManager.SelectLoneOrbitalTowardDirection(zigzagDir);
        }
        if (!editModeManager.TryAddAtomToSelected(atomicNumber, initialChargeOverride))
            return null;

        var after = anchor.GetConnectedMolecule();
        if (after != null)
        {
            AtomFunction bondedNew = null;
            AtomFunction anyNew = null;
            foreach (var a in after)
            {
                if (a == null || a == anchor) continue;
                if (beforeSet.Contains(a)) continue;
                if (anyNew == null && a.AtomicNumber == atomicNumber)
                    anyNew = a;
                if (a.AtomicNumber == atomicNumber && anchor.GetBondsTo(a) > 0)
                {
                    bondedNew = a;
                    break;
                }
            }
            if (bondedNew != null) return bondedNew;
            if (anyNew != null) return anyNew;
        }

        AtomFunction bestFallback = null;
        foreach (var n in anchor.GetDistinctSigmaNeighborAtoms())
        {
            if (n == null || n.AtomicNumber != atomicNumber) continue;
            if (beforeSet.Contains(n)) continue;
            bestFallback = n;
            break;
        }
        if (bestFallback != null) return bestFallback;
        return null;
    }

    /// <summary>
    /// Uses the same idea as lone-drag onto another nonbond: <see cref="ElectronOrbitalFunction.TrySwapNonbondNucleusChildLocalPoses"/>
    /// exchanges slots so the 1e lobe does not get re-aimed into the 2e lobe’s geometry (which caused overlap).
    /// Pairs: two 1e lobes → swap worst vs best dot to <paramref name="worldDir"/>; one 1e + one 2e → swap those.
    /// </summary>
    static void SwapAnchorNonbondLobesForBondDirectionTowardWorld(AtomFunction anchor, Vector3 worldDir)
    {
        if (anchor == null || worldDir.sqrMagnitude < 1e-12f) return;
        Vector3 d = worldDir.normalized;

        var ones = new List<ElectronOrbitalFunction>();
        ElectronOrbitalFunction twoE = null;
        foreach (var orb in anchor.BondedOrbitals)
        {
            if (orb == null || orb.Bond != null) continue;
            if (orb.ElectronCount == 1)
                ones.Add(orb);
            else if (orb.ElectronCount == 2)
                twoE = orb;
        }

        if (ones.Count >= 2)
        {
            ElectronOrbitalFunction worst = null;
            ElectronOrbitalFunction best = null;
            float worstDot = 2f;
            float bestDot = -2f;
            foreach (var o in ones)
            {
                float dot = Vector3.Dot(OrbitalAngleUtility.GetOrbitalDirectionWorld(o.transform), d);
                if (dot < worstDot)
                {
                    worstDot = dot;
                    worst = o;
                }
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = o;
                }
            }
            if (worst != null && best != null && worst != best)
                ElectronOrbitalFunction.TrySwapNonbondNucleusChildLocalPoses(worst, best);
            return;
        }

        if (ones.Count == 1 && twoE != null)
            ElectronOrbitalFunction.TrySwapNonbondNucleusChildLocalPoses(ones[0], twoE);
    }

    static void BuildSideChainByEditMode(
        string abbr,
        AtomFunction alphaCarbon,
        AtomFunction beta,
        AtomFunction amineN,
        AtomFunction carboxylC,
        EditModeManager editModeManager,
        Func<int, Vector3, int, AtomFunction> spawnAtom)
    {
        if (beta == null || editModeManager == null || spawnAtom == null) return;
        Vector3 alphaToBetaDir = alphaCarbon != null
            ? (beta.transform.position - alphaCarbon.transform.position).normalized
            : Vector3.zero;
        Vector3 alphaHydrogenLikeDir = GetAlphaHydrogenLikeDirection(alphaCarbon, amineN, carboxylC, beta);
        Vector3 carboxylToAlphaDir = Vector3.zero;
        if (carboxylC != null && alphaCarbon != null)
        {
            Vector3 dCa = alphaCarbon.transform.position - carboxylC.transform.position;
            if (dCa.sqrMagnitude > 1e-10f)
                carboxylToAlphaDir = dCa.normalized;
        }

        switch (abbr)
        {
            case "Ala":
                break;
            case "Val":
                AddByEditMode(beta, 6, editModeManager, amineN, alphaHydrogenLikeDir);
                AddByEditMode(beta, 6, editModeManager, amineN);
                break;
            case "Leu":
            {
                // γ is CH(Me)₂: first Me uses α→β stagger; second must bond from a different sp³ lobe (swap + out-of-plane pref).
                var g = AddByEditMode(beta, 6, editModeManager, amineN, alphaHydrogenLikeDir);
                if (g == null) break;
                var me1 = AddByEditMode(g, 6, editModeManager, beta, alphaToBetaDir);
                if (me1 == null) break;
                Vector3 uB = beta.transform.position - g.transform.position;
                Vector3 uM = me1.transform.position - g.transform.position;
                Vector3 pref2 = Vector3.Cross(uB, uM);
                if (pref2.sqrMagnitude < 1e-12f)
                {
                    Vector3 aux = alphaToBetaDir.sqrMagnitude > 1e-12f ? alphaToBetaDir : alphaHydrogenLikeDir;
                    pref2 = Vector3.Cross(uB, aux);
                }
                if (pref2.sqrMagnitude > 1e-12f)
                {
                    pref2.Normalize();
                    SwapAnchorNonbondLobesForBondDirectionTowardWorld(g, pref2);
                    AddByEditMode(g, 6, editModeManager, beta, pref2);
                }
                else
                    AddByEditMode(g, 6, editModeManager, beta);
                break;
            }
            case "Ile":
            {
                AddByEditMode(beta, 6, editModeManager, amineN, alphaHydrogenLikeDir);
                var g = AddByEditMode(beta, 6, editModeManager, amineN, alphaHydrogenLikeDir);
                if (g != null) AddByEditMode(g, 6, editModeManager, beta);
                break;
            }
            case "Ser":
                AddByEditMode(beta, 8, editModeManager, amineN);
                break;
            case "Thr":
                AddByEditMode(beta, 8, editModeManager, amineN);
                AddByEditMode(beta, 6, editModeManager, amineN);
                break;
            case "Cys":
                AddByEditMode(beta, 16, editModeManager, amineN, alphaHydrogenLikeDir);
                break;
            case "Met":
            {
                // Zig-zag: β→γ ∥ carboxyl→Cα; γ→S ∥ Cα→β; S→Me ∥ β→γ (same as “two steps back” along Cα–Cβ–Cγ–…).
                Vector3 zigCarboxylAlpha = carboxylToAlphaDir.sqrMagnitude > 1e-10f ? carboxylToAlphaDir : alphaHydrogenLikeDir;
                var g = AddByEditMode(beta, 6, editModeManager, amineN, zigCarboxylAlpha);
                Vector3 betaToGammaDir = Vector3.zero;
                if (g != null && beta != null)
                {
                    Vector3 dBg = g.transform.position - beta.transform.position;
                    if (dBg.sqrMagnitude > 1e-10f)
                        betaToGammaDir = dBg.normalized;
                }
                var s = g != null ? AddByEditMode(g, 16, editModeManager, beta, alphaToBetaDir) : null;
                Vector3 sMePref = betaToGammaDir.sqrMagnitude > 1e-10f ? betaToGammaDir : zigCarboxylAlpha;
                if (s != null)
                {
                    SwapAnchorNonbondLobesForBondDirectionTowardWorld(s, sMePref);
                    // Spin all S lone lobes about the S–Cγ axis so the 1e σ lobe lies in-plane with β→γ (N−2→N−1 zig), same as lone-drag with one σ neighbor.
                    Vector3 dPref = sMePref.sqrMagnitude > 1e-10f ? sMePref.normalized : Vector3.forward;
                    var ref1e = s.GetLoneOrbitalWithOneElectron(dPref);
                    if (ref1e != null)
                        ElectronOrbitalFunction.TrySpinLoneOrbitalsAroundSingleSigmaFromWorldTip(
                            s, ref1e, s.transform.position + dPref * (s.BondRadius * 2.2f));
                    AddByEditMode(s, 6, editModeManager, g, sMePref);
                }
                break;
            }
            case "Asp":
            {
                var c = AddByEditMode(beta, 6, editModeManager, amineN, alphaHydrogenLikeDir);
                if (c != null)
                {
                    AtomFunction oCarbonyl = null;
                    if (TryAttachPredeterminedCarbonylOAndPromotePi(
                            c,
                            GetSideChainCarbonylOxygenWorld(c, beta, alphaCarbon, true),
                            spawnAtom,
                            editModeManager,
                            out oCarbonyl))
                    {
                        var oAcid = AddByEditModeToAcylAfterCarbonylPlanar(c, beta, oCarbonyl, 8, editModeManager, beta, null);
                        if (oAcid != null && c.GetBondsTo(oAcid) == 1)
                            TryIonizeCarboxylateOxygen(oAcid, c);
                    }
                }
                break;
            }
            case "Glu":
            {
                var g = AddByEditMode(beta, 6, editModeManager, amineN, alphaHydrogenLikeDir);
                var c = g != null ? AddByEditMode(g, 6, editModeManager, beta, alphaToBetaDir) : null;
                if (c != null)
                {
                    AtomFunction oCarbonyl = null;
                    if (TryAttachPredeterminedCarbonylOAndPromotePi(
                            c,
                            GetSideChainCarbonylOxygenWorld(c, g, alphaCarbon, true),
                            spawnAtom,
                            editModeManager,
                            out oCarbonyl))
                    {
                        var oAcid = AddByEditModeToAcylAfterCarbonylPlanar(c, g, oCarbonyl, 8, editModeManager, g, null);
                        if (oAcid != null && c.GetBondsTo(oAcid) == 1)
                            TryIonizeCarboxylateOxygen(oAcid, c);
                    }
                }
                break;
            }
            case "Asn":
            {
                var c = AddByEditMode(beta, 6, editModeManager, amineN, alphaHydrogenLikeDir);
                if (c != null)
                {
                    AtomFunction oCarbonyl = null;
                    if (TryAttachPredeterminedCarbonylOAndPromotePi(
                            c,
                            GetSideChainCarbonylOxygenWorld(c, beta, alphaCarbon, true),
                            spawnAtom,
                            editModeManager,
                            out oCarbonyl))
                        AddByEditModeToAcylAfterCarbonylPlanar(c, beta, oCarbonyl, 7, editModeManager, beta, null);
                }
                break;
            }
            case "Gln":
            {
                var g = AddByEditMode(beta, 6, editModeManager, amineN, alphaHydrogenLikeDir);
                var c = g != null ? AddByEditMode(g, 6, editModeManager, beta, alphaToBetaDir) : null;
                if (c != null)
                {
                    AtomFunction oCarbonyl = null;
                    if (TryAttachPredeterminedCarbonylOAndPromotePi(
                            c,
                            GetSideChainCarbonylOxygenWorld(c, g, alphaCarbon, true),
                            spawnAtom,
                            editModeManager,
                            out oCarbonyl))
                        AddByEditModeToAcylAfterCarbonylPlanar(c, g, oCarbonyl, 7, editModeManager, g, null);
                }
                break;
            }
            case "Lys":
            {
                // Zig-zag Cβ–Cγ–Cδ–Cε–Nε: same alternation as backbone/Met (N−2→N−1 ∥ N→N+1); terminal N uses measured γ→δ.
                Vector3 zigCa = carboxylToAlphaDir.sqrMagnitude > 1e-10f ? carboxylToAlphaDir : alphaHydrogenLikeDir;
                var c1 = AddByEditMode(beta, 6, editModeManager, amineN, zigCa);
                var c2 = c1 != null ? AddByEditMode(c1, 6, editModeManager, beta, alphaToBetaDir) : null;
                Vector3 betaToGammaMeas = Vector3.zero;
                if (c1 != null && beta != null)
                {
                    Vector3 d = c1.transform.position - beta.transform.position;
                    if (d.sqrMagnitude > 1e-10f)
                        betaToGammaMeas = d.normalized;
                }
                var c3 = c2 != null
                    ? AddByEditMode(c2, 6, editModeManager, c1, betaToGammaMeas.sqrMagnitude > 1e-10f ? betaToGammaMeas : zigCa)
                    : null;
                Vector3 gammaToDeltaMeas = Vector3.zero;
                if (c1 != null && c2 != null)
                {
                    Vector3 d = c2.transform.position - c1.transform.position;
                    if (d.sqrMagnitude > 1e-10f)
                        gammaToDeltaMeas = d.normalized;
                }
                if (c3 != null)
                    AddByEditMode(c3, 7, editModeManager, c2, gammaToDeltaMeas.sqrMagnitude > 1e-10f ? gammaToDeltaMeas : alphaToBetaDir);
                break;
            }
            case "Arg":
            {
                var cGamma = AddByEditMode(beta, 6, editModeManager, amineN, alphaHydrogenLikeDir);
                var cDelta = cGamma != null ? AddByEditMode(cGamma, 6, editModeManager, beta, alphaToBetaDir) : null;
                var nEps = cDelta != null ? AddByEditMode(cDelta, 7, editModeManager, cGamma) : null;
                if (nEps == null || cDelta == null) break;
                Vector3 nContinue = (nEps.transform.position - cDelta.transform.position).normalized;
                var cGuan = AddByEditMode(nEps, 6, editModeManager, cDelta, nContinue);
                if (cGuan != null)
                {
                    var nIm = AttachAtomAtPredeterminedPosition(cGuan, 7, GetGuanidineNitrogenWorld(cGuan, nEps, false), spawnAtom, editModeManager, 1);
                    AttachAtomAtPredeterminedPosition(cGuan, 7, GetGuanidineNitrogenWorld(cGuan, nEps, true), spawnAtom, editModeManager);
                    if (nIm != null) TryPromoteToDoubleBond(cGuan, nIm, editModeManager);
                }
                break;
            }
            case "His":
            {
                // Imidazole: C₁ = Cβ (beta). Chain C₁–C₂–N₁–C₃–N₂–C₄ with zig-zag from carboxyl→Cα (Lys/Met style), then σ ring C₂–C₄; π N₁=C₃, C₂=C₄.
                Vector3 zigCarboxylAlpha = carboxylToAlphaDir.sqrMagnitude > 1e-10f ? carboxylToAlphaDir : alphaHydrogenLikeDir;
                var c2 = AddByEditMode(beta, 6, editModeManager, amineN, zigCarboxylAlpha);
                if (c2 == null) break;
                Vector3 betaToC2Meas = Vector3.zero;
                if (beta != null && c2 != null)
                {
                    Vector3 d = c2.transform.position - beta.transform.position;
                    if (d.sqrMagnitude > 1e-10f)
                        betaToC2Meas = d.normalized;
                }
                var n1 = AddByEditMode(c2, 7, editModeManager, beta, alphaToBetaDir.sqrMagnitude > 1e-10f ? alphaToBetaDir : zigCarboxylAlpha);
                if (n1 == null) break;
                Vector3 c2ToN1Meas = Vector3.zero;
                if (c2 != null && n1 != null)
                {
                    Vector3 d = n1.transform.position - c2.transform.position;
                    if (d.sqrMagnitude > 1e-10f)
                        c2ToN1Meas = d.normalized;
                }
                // Zig-zag alternation uses N−2→N−1, not the incoming C2→N1 vector.
                var c3 = AddByEditMode(
                    n1,
                    6,
                    editModeManager,
                    c2,
                    betaToC2Meas.sqrMagnitude > 1e-10f ? betaToC2Meas : zigCarboxylAlpha);
                if (c3 == null) break;
                Vector3 n1ToC3Meas = Vector3.zero;
                if (n1 != null && c3 != null)
                {
                    Vector3 d = c3.transform.position - n1.transform.position;
                    if (d.sqrMagnitude > 1e-10f)
                        n1ToC3Meas = d.normalized;
                }
                var n2 = AddByEditMode(
                    c3,
                    7,
                    editModeManager,
                    n1,
                    c2ToN1Meas.sqrMagnitude > 1e-10f ? c2ToN1Meas : alphaToBetaDir);
                if (n2 == null) break;
                Vector3 c3ToN2Meas = Vector3.zero;
                if (c3 != null && n2 != null)
                {
                    Vector3 d = n2.transform.position - c3.transform.position;
                    if (d.sqrMagnitude > 1e-10f)
                        c3ToN2Meas = d.normalized;
                }
                var c4 = AddByEditMode(
                    n2,
                    6,
                    editModeManager,
                    c3,
                    n1ToC3Meas.sqrMagnitude > 1e-10f ? n1ToC3Meas : zigCarboxylAlpha);
                if (c4 == null) break;
                if (!TryBondDirect(c2, c4, editModeManager))
                    break;
                TryPromoteToDoubleBond(n1, c3, editModeManager);
                TryPromoteToDoubleBond(c2, c4, editModeManager);
                break;
            }
            case "Phe":
            case "Tyr":
            case "Trp":
                BuildAromaticByEditMode(beta, abbr, editModeManager);
                break;
            case "Pro":
            {
                var c1 = AddByEditMode(beta, 6, editModeManager, amineN);
                var c2 = c1 != null ? AddByEditMode(c1, 6, editModeManager, beta) : null;
                if (c2 != null && amineN != null)
                    TryBondDirect(amineN, c2, editModeManager);
                break;
            }
        }
    }

    static void BuildAromaticByEditMode(AtomFunction beta, string abbr, EditModeManager editModeManager)
    {
        var r = AddByEditMode(beta, 6, editModeManager);
        if (r == null) return;
        var c2 = AddByEditMode(r, 6, editModeManager, beta);
        var c3 = c2 != null ? AddByEditMode(c2, 6, editModeManager, r) : null;
        var c4 = c3 != null ? AddByEditMode(c3, 6, editModeManager, c2) : null;
        if (c4 != null && r != null)
            TryBondDirect(c4, r, editModeManager);
        if (abbr == "Tyr" && c4 != null) AddByEditMode(c4, 8, editModeManager, c3);
        if (abbr == "Trp" && c4 != null) AddByEditMode(c4, 7, editModeManager, c3);
    }

    static bool TryPromoteToDoubleBond(AtomFunction atomA, AtomFunction atomB, EditModeManager editModeManager)
    {
        if (atomA == null || atomB == null || editModeManager == null) return false;
        if (atomA.GetBondsTo(atomB) != 1) return false;
        Vector3 dAB = (atomB.transform.position - atomA.transform.position).normalized;
        Vector3 dBA = -dAB;
        var oa = atomA.GetLoneOrbitalForBondFormation(dAB);
        var ob = atomB.GetLoneOrbitalForBondFormation(dBA);
        if (oa == null || ob == null) return false;
        var piRunner = SigmaBondFormation.EnsureRunnerInScene();
        if (piRunner != null
            && piRunner.TryBeginOrbitalDragPiFormation(
                atomA,
                atomB,
                oa,
                ob,
                redistributionGuideTieBreakDraggedOrbital: oa,
                // Builder path requires immediate completion before post-condition checks.
                animate: false))
        {
            return atomA.GetBondsTo(atomB) >= 2;
        }
        editModeManager.FormSigmaBondInstant(
            atomA,
            atomB,
            oa,
            ob,
            redistributeAtomA: false,
            redistributeAtomB: false);
        return atomA.GetBondsTo(atomB) >= 2;
    }

    static AtomFunction AttachAtomAtPredeterminedPosition(
        AtomFunction anchor,
        int atomicNumber,
        Vector3 worldPos,
        Func<int, Vector3, int, AtomFunction> spawnAtom,
        EditModeManager editModeManager,
        int spawnCharge = 0)
    {
        if (anchor == null || spawnAtom == null || editModeManager == null) return null;
        var atom = spawnAtom(atomicNumber, worldPos, spawnCharge);
        if (atom == null) return null;
        if (!TryBondDirect(anchor, atom, editModeManager))
        {
            UnityEngine.Object.Destroy(atom.gameObject);
            return null;
        }
        return atom;
    }

    /// <summary>
    /// Spawns carbonyl O at <paramref name="carbonylWorldPos"/> and promotes C=O π. Call this before any other acyl substituent
    /// so <see cref="TryPromoteToDoubleBond"/> trigonal redistribute does not scramble atoms added only via edit mode afterward.
    /// </summary>
    static bool TryAttachPredeterminedCarbonylOAndPromotePi(
        AtomFunction acylC,
        Vector3 carbonylWorldPos,
        Func<int, Vector3, int, AtomFunction> spawnAtom,
        EditModeManager editModeManager,
        out AtomFunction carbonylO)
    {
        carbonylO = null;
        if (acylC == null || spawnAtom == null || editModeManager == null) return false;
        carbonylO = AttachAtomAtPredeterminedPosition(acylC, 8, carbonylWorldPos, spawnAtom, editModeManager);
        if (carbonylO == null) return false;
        return TryPromoteToDoubleBond(acylC, carbonylO, editModeManager);
    }

    /// <summary>
    /// After <see cref="TryAttachPredeterminedCarbonylOAndPromotePi"/>, bonds the next substituent with normal edit-mode rules,
    /// then relaxes the acyl center with three σ neighbors (when π is present).
    /// </summary>
    static AtomFunction AddByEditModeToAcylAfterCarbonylPlanar(
        AtomFunction acylC,
        AtomFunction chainRefNeighbor,
        AtomFunction carbonylO,
        int atomicNumber,
        EditModeManager editModeManager,
        AtomFunction twoStepsBehind = null,
        Vector3? preferredDirectionWorld = null)
    {
        if (acylC == null || editModeManager == null) return null;
        AtomFunction added = preferredDirectionWorld.HasValue
            ? AddByEditMode(acylC, atomicNumber, editModeManager, twoStepsBehind, preferredDirectionWorld.Value)
            : AddByEditMode(acylC, atomicNumber, editModeManager, twoStepsBehind);
        if (carbonylO != null && acylC.GetPiBondCount() > 0)
            EnforceSideChainAcylTrigonalGeometry(acylC, chainRefNeighbor, carbonylO);
        return added;
    }

    static bool TryBondDirect(AtomFunction atomA, AtomFunction atomB, EditModeManager editModeManager)
    {
        if (atomA == null || atomB == null || editModeManager == null) return false;
        Vector3 dAB = (atomB.transform.position - atomA.transform.position).normalized;
        Vector3 dBA = -dAB;
        var oa = atomA.GetLoneOrbitalForBondFormation(dAB);
        var ob = atomB.GetLoneOrbitalForBondFormation(dBA);
        if (oa == null || ob == null) return false;
        var sigmaRunner = SigmaBondFormation.EnsureRunnerInScene();
        if (sigmaRunner != null
            && sigmaRunner.TryBeginOrbitalDragSigmaFormation(
                atomA,
                atomB,
                oa,
                ob,
                redistributionGuideTieBreakDraggedOrbital: oa,
                animate: false))
            return true;
        editModeManager.FormSigmaBondInstant(
            atomA,
            atomB,
            oa,
            ob,
            redistributeAtomA: false,
            redistributeAtomB: false);
        return true;
    }

    /// <summary>
    /// Before H-auto fill: aim backbone α–NH₂ lone lobes using the same σ-axis spin as a lone-orbital drag with one σ neighbor
    /// (<see cref="ElectronOrbitalFunction.TrySpinLoneOrbitalsAroundSingleSigmaFromWorldTip"/>), toward <c>C(carboxyl) → Cα</c>.
    /// Then <see cref="TryCreate"/> calls <see cref="AtomFunction.TryStaggerNewmanRelativeToPartner"/> vs Cα.
    /// </summary>
    static void AlignBackboneAmineLonePairsTowardCarboxylToAlpha(
        AtomFunction amineN,
        AtomFunction alphaCarbon,
        AtomFunction carboxylC)
    {
        if (amineN == null || alphaCarbon == null || carboxylC == null) return;
        if (amineN.AtomicNumber != 7) return;

        Vector3 guideW = alphaCarbon.transform.position - carboxylC.transform.position;
        if (guideW.sqrMagnitude < 1e-12f) return;
        guideW.Normalize();

        var loneNonBond = new List<ElectronOrbitalFunction>();
        foreach (var orb in amineN.BondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount <= 0) continue;
            loneNonBond.Add(orb);
        }
        loneNonBond.Sort((a, b) =>
        {
            int c = b.ElectronCount.CompareTo(a.ElectronCount);
            return c != 0 ? c : a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex());
        });
        if (loneNonBond.Count == 0) return;

        Vector3 tipWorld = amineN.transform.position + guideW * (amineN.BondRadius * 2.2f);
        ElectronOrbitalFunction.TrySpinLoneOrbitalsAroundSingleSigmaFromWorldTip(amineN, loneNonBond[0], tipWorld);
    }

    static Vector3 GetCarboxylOxygenPosition(AtomFunction carboxylC, AtomFunction alphaCarbon, bool carbonylSide)
    {
        if (carboxylC == null) return Vector3.zero;
        float L = carboxylC != null ? 1.2f * carboxylC.BondRadius : 0.96f;
        Vector3 cPos = carboxylC.transform.position;
        Vector3 dirToAlpha = alphaCarbon != null
            ? (alphaCarbon.transform.position - cPos).normalized
            : Vector3.left;
        var tri = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(3), dirToAlpha);
        Vector3 oDir = carbonylSide ? tri[1].normalized : tri[2].normalized;
        return cPos + oDir * L;
    }

    /// <summary>
    /// sp² substituents on side-chain carbonyl / amide carbon. Matches backbone <see cref="GetCarboxylOxygenPosition"/>:
    /// first trigonal axis aligns from this carbon toward <paramref name="alphaCarbon"/> so the Cα–R–C–(=O) zigzag
    /// stays coherent with backbone carboxyl–Cα geometry; <paramref name="refNeighbor"/> is fallback when α is missing.
    /// <paramref name="carbonylSide"/> selects tri[1] vs tri[2] for =O vs O⁻/NH₂.
    /// </summary>
    static Vector3 GetSideChainCarbonylOxygenWorld(
        AtomFunction carbonylC,
        AtomFunction refNeighbor,
        AtomFunction alphaCarbon,
        bool carbonylSide)
    {
        if (carbonylC == null) return Vector3.zero;
        float L = carbonylC != null ? 1.2f * carbonylC.BondRadius : 0.96f;
        Vector3 cPos = carbonylC.transform.position;
        // Same convention as backbone carboxyl: from side-chain acyl C toward Cα (not only toward β) to fix trigonal twist in the plane.
        Vector3 dirAlign = alphaCarbon != null
            ? (alphaCarbon.transform.position - cPos).normalized
            : refNeighbor != null
                ? (refNeighbor.transform.position - cPos).normalized
                : Vector3.left;
        if (dirAlign.sqrMagnitude < 1e-10f) dirAlign = Vector3.left;
        var tri = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(3), dirAlign);
        Vector3 oDir = carbonylSide ? tri[1].normalized : tri[2].normalized;
        return cPos + oDir * L;
    }

    /// <summary>Guanyl Cζ: tri[0] toward Nε; tri[1]/[2] for iminium vs amino nitrogens.</summary>
    static Vector3 GetGuanidineNitrogenWorld(AtomFunction cGuan, AtomFunction nEpsilon, bool secondSubstituentSlot)
    {
        if (cGuan == null) return Vector3.zero;
        float L = 1.2f * cGuan.BondRadius;
        Vector3 cPos = cGuan.transform.position;
        Vector3 dirToN = nEpsilon != null
            ? (nEpsilon.transform.position - cPos).normalized
            : Vector3.right;
        if (dirToN.sqrMagnitude < 1e-10f) dirToN = Vector3.right;
        var tri = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(3), dirToN);
        Vector3 d = secondSubstituentSlot ? tri[2].normalized : tri[1].normalized;
        return cPos + d * L;
    }

    /// <summary>
    /// sp² acyl carbon after side-chain π (amide / carboxylate): previously called
    /// <see cref="MoleculeBuilder.ForceTrigonalPlanarNeighborPositionsForPiCenter"/> to snap σ neighbors to 120°.
    /// That pass runs <b>after</b> π phase-1 prebond alignment and reassigns O / second σ substituents in a parent-anchored
    /// ideal trigonal frame, which breaks amide <b>guide</b> ⊥ +z (Asn/Gln). Library geometry is already set by deterministic
    /// O placement + π redistribution; we do not hard-force neighbors here.
    /// </summary>
    static void EnforceSideChainAcylTrigonalGeometry(AtomFunction acylC, AtomFunction chainRefNeighbor, AtomFunction carbonylPartner)
    {
        if (acylC == null || chainRefNeighbor == null || carbonylPartner == null) return;
        if (acylC.GetPiBondCount() <= 0) return;
    }

    /// <summary>
    /// Deprotonated carboxylate: second O was built neutral; add one electron to a non-bond 1e lone (not σ to <paramref name="carboxylC"/>).
    /// </summary>
    static void TryIonizeCarboxylateOxygen(AtomFunction oAtom, AtomFunction carboxylC)
    {
        if (oAtom == null || oAtom.AtomicNumber != 8 || carboxylC == null) return;
        Vector3 awayFromC = (oAtom.transform.position - carboxylC.transform.position).normalized;
        if (awayFromC.sqrMagnitude > 1e-10f)
        {
            var orb = oAtom.GetLoneOrbitalWithOneElectron(awayFromC);
            if (orb != null && orb.Bond == null && orb.ElectronCount == 1)
            {
                orb.ElectronCount = 2;
                return;
            }
        }
        foreach (var orb in oAtom.BondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount != 1) continue;
            orb.ElectronCount = 2;
            return;
        }
    }

    static Vector3 GetAlphaHydrogenLikeDirection(AtomFunction alpha, AtomFunction amineN, AtomFunction carboxylC, AtomFunction beta)
    {
        if (alpha == null) return Vector3.up;
        Vector3 sum = Vector3.zero;
        int count = 0;
        void AddDir(AtomFunction n)
        {
            if (n == null) return;
            Vector3 d = n.transform.position - alpha.transform.position;
            if (d.sqrMagnitude < 1e-10f) return;
            sum += d.normalized;
            count++;
        }
        AddDir(amineN);
        AddDir(carboxylC);
        AddDir(beta);
        if (count == 0) return Vector3.up;
        Vector3 hLike = -sum.normalized;
        return hLike.sqrMagnitude > 1e-10f ? hLike : Vector3.up;
    }

    /// <summary>
    /// Fills implicit H on each heavy atom in the α-connected molecule. Uses <see cref="EditModeManager.SaturateWithHydrogen"/> with
    /// <c>preferOrbitalSpawnDirectionOnPiCenters</c> so Arg guanidine / imine N–H follow π phase-1 lobe directions instead of VSEPR-only spawn.
    /// </summary>
    static void ForceFinalHydrogenFill(AtomFunction alphaCarbon, EditModeManager editModeManager)
    {
        if (alphaCarbon == null || editModeManager == null) return;
        var mol = alphaCarbon.GetConnectedMolecule();
        if (mol == null) return;
        foreach (var a in mol)
        {
            if (a == null || a.AtomicNumber <= 1) continue;
            editModeManager.SaturateWithHydrogen(a, preferOrbitalSpawnDirectionOnPiCenters: true);
        }
    }
}
