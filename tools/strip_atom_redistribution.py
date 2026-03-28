#!/usr/bin/env python3
"""One-off removal of electron redistribution implementation blocks in AtomFunction.cs."""
from pathlib import Path

def main():
    root = Path(__file__).resolve().parents[1]
    path = root / "Assets" / "Scripts" / "AtomFunction.cs"
    text = path.read_text(encoding="utf-8")

    def cut(start_marker: str, end_marker: str, label: str) -> str:
        a = text.find(start_marker)
        b = text.find(end_marker)
        if a == -1 or b == -1 or b <= a:
            raise SystemExit(f"strip_atom_redistribution: markers not found or bad order for {label}: a={a} b={b}")
        return text[:a] + text[b:]

    # Private TryRelax* helpers only used by removed RedistributeOrbitals3DOld
    text = cut(
        "    /// <summary>\n    /// AX₃E with coplanar σ axes (e.g. after π bond break): move σ neighbors",
        "    /// <summary>\n    /// σ-only bond break: <b>2 or 3</b> σ neighbors, non-bond shell compatible with",
        "try_relax_block",
    )

    # Carbocation / rigid spread / TrySpread — only fed redistribution pipeline (starts after TryAppendRigid)
    text = cut(
        "    void ApplyRigidSigmaCleavagePivotOrbitalTargets(",
        "    void RedistributeOrbitals3DOld(",
        "carbocation_spread_block",
    )

    # RedistributeOrbitals3DOld … GetRedistributeTargets3DVseprTryMatch (exclusive of ApplyRedistributeTargets)
    text = cut(
        "    void RedistributeOrbitals3DOld(",
        "    public void ApplyRedistributeTargets(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets)",
        "redistribute_3d_pipeline",
    )

    path.write_text(text, encoding="utf-8")
    print("OK: stripped redistribution blocks from AtomFunction.cs")

if __name__ == "__main__":
    main()
