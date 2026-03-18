using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class ElectronOrbitalFunction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] int electronCount;
    [SerializeField] ElectronFunction electronPrefab;
    [SerializeField] float electronSpacing = 0.5f; // Distance between electrons (larger = more spread)
    [SerializeField] Sprite stretchSprite; // Optional: single sprite for stretch. If null, uses procedural triangle+circle composite.
    AtomFunction bondedAtom;
    CovalentBond bond;
    bool isBeingHeld;
    Vector3 dragOffset;

    public const int MaxElectrons = 2;
    public int ElectronCount
    {
        get => electronCount;
        set
        {
            int oldCount = electronCount;
            electronCount = Mathf.Clamp(value, 0, MaxElectrons);
            int delta = electronCount - oldCount;
            SyncElectronObjects();
            if (delta != 0)
            {
                if (bond != null)
                    bond.NotifyElectronCountChanged();
                else if (bondedAtom != null)
                {
                    for (int i = 0; i < Mathf.Abs(delta); i++)
                        if (delta > 0) bondedAtom.OnElectronAdded();
                        else bondedAtom.OnElectronRemoved();
                }
            }
        }
    }

    public bool IsBonded => (bondedAtom != null || bond != null) && !isBeingHeld;
    public AtomFunction BondedAtom => bondedAtom;
    public CovalentBond Bond => bond;

    public void SetBondedAtom(AtomFunction atom) => bondedAtom = atom;

    public void SetBond(CovalentBond b) => bond = b;

    public void RemoveElectron(ElectronFunction electron)
    {
        if (electron.transform.parent != transform) return;
        electronCount = Mathf.Clamp(electronCount - 1, 0, MaxElectrons);
        electron.transform.SetParent(null);
        if (bond != null)
            bond.NotifyElectronCountChanged();
        else
            bondedAtom?.OnElectronRemoved();
    }

    public bool CanAcceptElectron() => electronCount < MaxElectrons;

    public bool AcceptElectron(ElectronFunction electron)
    {
        if (electronCount >= MaxElectrons) return false;
        Destroy(electron.gameObject);
        electronCount++;
        SyncElectronObjects();
        if (bond != null)
            bond.NotifyElectronCountChanged();
        else
        {
            bondedAtom?.OnElectronAdded();
            bondedAtom?.SetupIgnoreCollisions();
        }
        return true;
    }

    public bool ContainsPoint(Vector3 worldPos)
    {
        var col2D = GetComponent<Collider2D>();
        if (col2D != null) return col2D.OverlapPoint(worldPos);
        var col = GetComponent<Collider>();
        return col != null && col.bounds.Contains(worldPos);
    }

    public void SetPointerBlocked(bool blocked)
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col != null) col.enabled = !blocked;
        if (col2D != null) col2D.enabled = !blocked;
    }

    /// <summary>Highlight this orbital (e.g. when selected for bonding in edit mode).</summary>
    public void SetHighlighted(bool on)
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            if (on && originalColor.a == 0 && originalColor.r == 0 && originalColor.g == 0) originalColor = sr.color;
            sr.color = on ? new Color(1f, 1f, 0.6f, originalColor.a) : originalColor;
        }
        var baseScale = originalLocalScale.sqrMagnitude > 0.01f
            ? originalLocalScale
            : (on ? transform.localScale : transform.localScale / 1.2f);
        transform.localScale = on ? baseScale * 1.2f : baseScale;
    }

    /// <summary>Show or hide the orbital and its electron visuals. Used when bond displays as a line.</summary>
    public void SetVisualsEnabled(bool enabled)
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr != null) sr.enabled = enabled;
        foreach (var r in GetComponentsInChildren<Renderer>(true))
            r.enabled = enabled;
    }

    public void SetPhysicsEnabled(bool enabled)
    {
        void SetRb(GameObject go)
        {
            var rb = go.GetComponent<Rigidbody>();
            var rb2D = go.GetComponent<Rigidbody2D>();
            if (rb != null) rb.isKinematic = !enabled;
            if (rb2D != null) rb2D.simulated = enabled;
        }
        SetRb(gameObject);
        foreach (var e in GetComponentsInChildren<ElectronFunction>())
            SetRb(e.gameObject);
    }

    Vector3 originalLocalPosition;
    Vector3 originalLocalScale;
    Quaternion originalLocalRotation;
    Color originalColor;
    GameObject stretchVisual;
    SpriteRenderer mainSpriteRenderer;
    const float MinStretchLength = 0.5f;
    const float OffsetScale = 1.14f;
    [SerializeField] float apexPadding = 0.2f;
    static Sprite cachedTriangleSprite;
    static Sprite cachedCircleSprite;
    static Material cachedTriangleClipMaterial;
    static readonly int ClipCenterId = Shader.PropertyToID("_ClipCenter");
    static readonly int ClipRadiusXId = Shader.PropertyToID("_ClipRadiusX");
    static readonly int ClipRadiusYId = Shader.PropertyToID("_ClipRadiusY");

    static Material GetOrCreateTriangleClipMaterial()
    {
        if (cachedTriangleClipMaterial != null) return cachedTriangleClipMaterial;
        var shader = Shader.Find("OrgoSimulator/TriangleClipCircle");
        cachedTriangleClipMaterial = shader != null ? new Material(shader) : null;
        return cachedTriangleClipMaterial;
    }

    static Sprite GetOrCreateTriangleSprite()
    {
        if (cachedTriangleSprite != null) return cachedTriangleSprite;
        cachedTriangleSprite = CreateTriangleSprite();
        return cachedTriangleSprite;
    }

    static Sprite GetOrCreateCircleSprite()
    {
        if (cachedCircleSprite != null) return cachedCircleSprite;
        cachedCircleSprite = CreateCircleSprite();
        return cachedCircleSprite;
    }

    static Sprite CreateTriangleSprite(int width = 64, int height = 64)
    {
        var tex = new Texture2D(width, height);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color32[width * height];
        float cx = (width - 1) * 0.5f;
        for (int y = 0; y < height; y++)
        {
            float t = (float)y / (height - 1);
            float halfWidth = 0.5f * t;
            for (int x = 0; x < width; x++)
            {
                float dx = Mathf.Abs((x - cx) / (width * 0.5f));
                float alpha = dx <= halfWidth ? 1f - (dx / (halfWidth + 0.01f)) * 0.3f : 0f;
                if (alpha > 0.02f)
                    pixels[y * width + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255));
                else
                    pixels[y * width + x] = Color.clear;
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 32f);
    }

    static Sprite CreateCircleSprite(int size = 64)
    {
        var tex = new Texture2D(size, size);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color32[size * size];
        float cx = (size - 1) * 0.5f;
        float r = cx * 0.9f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx;
                float dy = y - cx;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = d <= r ? 1f - (d / r) * 0.3f : 0f;
                if (alpha > 0.01f)
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                else
                    pixels[y * size + x] = Color.clear;
            }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        isBeingHeld = true;
        originalLocalPosition = transform.localPosition;
        originalLocalScale = transform.localScale;
        originalLocalRotation = transform.localRotation;
        dragOffset = transform.position - ScreenToWorld(eventData.position);
        mainSpriteRenderer = GetComponent<SpriteRenderer>();
        if (mainSpriteRenderer != null) mainSpriteRenderer.enabled = false;
        CreateStretchVisual();
        SetPhysicsEnabled(false);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!isBeingHeld) return;
        Vector3 tip = ScreenToWorld(eventData.position) + dragOffset;
        transform.position = tip;
        UpdateStretchVisual(tip);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        isBeingHeld = false;
        Vector3 tip = transform.position;
        if (stretchVisual != null) { Destroy(stretchVisual); stretchVisual = null; }
        if (mainSpriteRenderer != null) mainSpriteRenderer.enabled = true;
        SetPhysicsEnabled(true);

        if (bond != null)
        {
            TryBreakBond(tip);
            return;
        }

        var sourceAtom = bondedAtom ?? transform.parent?.GetComponent<AtomFunction>();
        var targetAtom = AtomFunction.FindBondPartner(sourceAtom, tip, this);
        if (targetAtom != null)
        {
            var targetOrbital = targetAtom.GetAvailableLoneOrbitalForBond(this, electronCount, tip);
            if (targetOrbital != null)
            {
                bool alreadyBonded = sourceAtom.GetBondsTo(targetAtom) >= 1;
                if (alreadyBonded)
                {
                    if (FormCovalentBondPiStart(sourceAtom, targetAtom, targetOrbital))
                        return;
                }
                else if (FormCovalentBondSigmaStart(sourceAtom, targetAtom, targetOrbital, tip))
                {
                    return;
                }
            }
        }

        var swapTarget = TryFindSwapTarget(sourceAtom, tip);
        if (swapTarget != null)
        {
            SwapPositionsWith(swapTarget);
            return;
        }

        transform.localPosition = originalLocalPosition;
        transform.localScale = originalLocalScale;
        transform.localRotation = originalLocalRotation;
    }

    ElectronOrbitalFunction TryFindSwapTarget(AtomFunction atom, Vector3 tip)
    {
        if (atom == null || bond != null) return null;
        var orbitals = atom.GetComponentsInChildren<ElectronOrbitalFunction>();
        foreach (var orb in orbitals)
        {
            if (orb != this && orb.Bond == null && orb.ContainsPoint(tip))
                return orb;
        }
        return null;
    }

    void SwapPositionsWith(ElectronOrbitalFunction other)
    {
        transform.localPosition = other.transform.localPosition;
        transform.localRotation = other.transform.localRotation;
        other.transform.localPosition = originalLocalPosition;
        other.transform.localRotation = originalLocalRotation;
    }

    void TryBreakBond(Vector3 tip)
    {
        if (bond == null) return;
        var a = bond.AtomA;
        var b = bond.AtomB;
        if (a == null || b == null) return;
        float rA = a.BondRadius * 1.2f;
        float rB = b.BondRadius * 1.2f;
        float da = Vector3.Distance(tip, a.transform.position);
        float db = Vector3.Distance(tip, b.transform.position);
        AtomFunction returnTo = null;
        if (da <= rA && db <= rB)
            returnTo = da <= db ? a : b;
        else if (da <= rA)
            returnTo = a;
        else if (db <= rB)
            returnTo = b;
        if (returnTo != null)
            bond.BreakBond(returnTo);
        else
        {
            transform.localPosition = originalLocalPosition;
            transform.localScale = originalLocalScale;
            transform.localRotation = originalLocalRotation;
            bond.ReturnToLineView();
        }
    }

    [Tooltip("Step 1: Align molecule. Duration in seconds.")]
    [SerializeField] float bondAnimStep1Duration = 1.0f;
    [Tooltip("Step 2: Rearrange + Redistribute. Duration in seconds.")]
    [SerializeField] float bondAnimStep2Duration = 1.0f;
    [Tooltip("Step 3: Orbital-to-line transition. Duration in seconds.")]
    [SerializeField] float bondAnimStep3Duration = 1.0f;

    /// <summary>Sigma bond: starts animated formation. Returns true if animation started.</summary>
    bool FormCovalentBondSigmaStart(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3 dropPosition)
    {
        bool sourceAlreadyAligned = IsSourceOrbitalAlreadyAlignedWithTarget(sourceAtom, targetOrbital);
        bool cannotRearrangeSource = !sourceAlreadyAligned && (sourceAtom.GetPiBondCount() > 0 || IsSourceFlippedSideFilled(sourceAtom, targetAtom, targetOrbital));
        if (sourceAtom.CovalentBonds.Count > 0 && cannotRearrangeSource)
        {
            var sourceOrbitalOriginalTip = sourceAtom.transform.TransformPoint(originalLocalPosition);
            bool targetAlreadyAligned = IsOrbitalAlignedWithTargetPoint(targetOrbital, sourceOrbitalOriginalTip);
            bool cannotRearrangeTarget = !targetAlreadyAligned && (targetAtom.GetPiBondCount() > 0 || IsSourceFlippedSideFilled(targetAtom, sourceAtom, this, sourceOrbitalOriginalTip));
            if (targetAtom.CovalentBonds.Count > 0 && cannotRearrangeTarget)
                return false;
        }
        sourceAtom.StartCoroutine(FormCovalentBondSigmaCoroutine(sourceAtom, targetAtom, targetOrbital, dropPosition));
        return true;
    }

    IEnumerator FormCovalentBondSigmaCoroutine(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3 dropPosition)
    {
        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock) a.SetInteractionBlocked(true);

        try
        {
        int piBeforeSource = sourceAtom.GetPiBondCount();
        int piBeforeTarget = targetAtom.GetPiBondCount();
        bool isFlip = sourceAtom.CovalentBonds.Count > 0 && !IsSourceOrbitalAlreadyAlignedWithTarget(sourceAtom, targetOrbital) && (sourceAtom.GetPiBondCount() > 0 || IsSourceFlippedSideFilled(sourceAtom, targetAtom, targetOrbital));
        Vector3? targetPointOverride = isFlip ? sourceAtom.transform.TransformPoint(originalLocalPosition) : (Vector3?)null;

        ElectronOrbitalFunction sourceOrbital = this;
        AtomFunction rearrangeSource = isFlip ? targetAtom : sourceAtom;
        ElectronOrbitalFunction rearrangeTarget = isFlip ? this : targetOrbital;
        AtomFunction alignSource = isFlip ? targetAtom : sourceAtom;
        var referenceAtom = isFlip ? sourceAtom : targetAtom;
        var referenceOrbitalTip = targetPointOverride ?? targetOrbital.transform.position;

        // Step 1: AlignSourceAtomNextToTarget
        // First, snap source orbital back to its original position (before dragging), then animate molecule
        sourceOrbital.transform.localPosition = originalLocalPosition;
        sourceOrbital.transform.localRotation = originalLocalRotation;
        sourceOrbital.transform.localScale = originalLocalScale;

        var alignTargets = GetAlignTargetPositions(alignSource, referenceAtom, referenceOrbitalTip);
        const float alignPosThreshold = 0.05f;
        bool atomsAlreadyAligned = alignTargets.TrueForAll(t => Vector3.Distance(t.atom.transform.position, t.targetPos) < alignPosThreshold);
        if (!atomsAlreadyAligned)
        {
            var alignStarts = new List<(AtomFunction atom, Vector3 startPos)>();
            foreach (var (atom, targetPos) in alignTargets)
                alignStarts.Add((atom, atom.transform.position));
            for (float t = 0; t < bondAnimStep1Duration; t += Time.deltaTime)
            {
                float s = Mathf.Clamp01(t / bondAnimStep1Duration);
                s = s * s * (3f - 2f * s); // smoothstep
                for (int i = 0; i < alignTargets.Count; i++)
                {
                    var (atom, targetPos) = alignTargets[i];
                    var (_, startPos) = alignStarts[i];
                    atom.transform.position = Vector3.Lerp(startPos, targetPos, s);
                }
                yield return null;
            }
        }
        foreach (var (atom, targetPos) in alignTargets)
            atom.transform.position = targetPos;

        // Compute rearrange targets before bond creation (GetRearrangeTarget may use 'this')
        var rearrangeTargetInfo = GetRearrangeTarget(rearrangeSource, rearrangeTarget, targetPointOverride);

        // Create bond (instant) - always keep source orbital (the one we dragged) so it stays visible through steps 2 and 3
        sourceOrbital.transform.localPosition = originalLocalPosition;
        sourceOrbital.transform.localRotation = originalLocalRotation;
        var bondOrbitalStartWorldPos = sourceOrbital.transform.position;
        var bondOrbitalStartWorldRot = sourceOrbital.transform.rotation;

        int merged = sourceOrbital.ElectronCount + targetOrbital.ElectronCount;
        sourceAtom.UnbondOrbital(sourceOrbital);
        targetAtom.UnbondOrbital(targetOrbital);
        var bond = CovalentBond.Create(isFlip ? targetAtom : sourceAtom, isFlip ? sourceAtom : targetAtom, sourceOrbital, sourceAtom, animateOrbitalToBond: true);
        if (isFlip) targetAtom.transform.SetParent(null);
        var survivingOrbital = sourceOrbital;
        // Reparent target orbital to bond so it's not counted in target's lone orbitals (avoids double-count charge bug)
        targetOrbital.transform.SetParent(bond.transform, worldPositionStays: true);
        bond.SetOrbitalBeingFaded(targetOrbital); // Use merged count for charge during animation
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        var bondOrbitalEnd = bond.GetOrbitalTargetWorldState();
        float sigmaDiff = LogSigmaBondAngles(sourceAtom, bondOrbitalEnd.worldPos, bondOrbitalEnd.worldRot);
        bool sigmaNeedsFlip = Mathf.Abs(sigmaDiff) > 90f; // Source opposite to bond → flip so electrons don't overlap
        if (sigmaNeedsFlip)
        {
            bond.orbitalRotationFlipped = true;
            bondOrbitalEnd = bond.GetOrbitalTargetWorldState(); // Re-fetch with flip applied
        }

        // Step 2: Rearrange + Redistribute + animate bonding orbital to bond position (skip if no actual movement)
        var redistA = sourceAtom.GetRedistributeTargets(piBeforeSource, targetAtom);
        var redistB = targetAtom.GetRedistributeTargets(piBeforeTarget, sourceAtom);
        const float alignThreshold = 0.05f;
        const float posThreshold = 0.01f;
        const float rotThreshold = 1f;

        bool hasRearrangeMovement = false;
        if (rearrangeTargetInfo.HasValue)
        {
            var (orbToMove, targetPos, targetRot) = rearrangeTargetInfo.Value;
            if (orbToMove != null && (Vector3.Distance(orbToMove.transform.localPosition, targetPos) > posThreshold || Quaternion.Angle(orbToMove.transform.localRotation, targetRot) > rotThreshold))
                hasRearrangeMovement = true;
        }
        bool needsRearrange = hasRearrangeMovement;

        bool hasRedistributeMovement = false;
        foreach (var entry in redistA)
        {
            if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform
                && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > posThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > rotThreshold))
            { hasRedistributeMovement = true; break; }
        }
        if (!hasRedistributeMovement)
            foreach (var entry in redistB)
            {
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform
                    && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > posThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > rotThreshold))
                { hasRedistributeMovement = true; break; }
            }
        bool needsRedistribute = hasRedistributeMovement;

        bool orbitalAlreadyAtBond = Vector3.Distance(bondOrbitalStartWorldPos, bondOrbitalEnd.worldPos) < alignThreshold;
        bool skipStep2 = !needsRearrange && !needsRedistribute && orbitalAlreadyAtBond;

        var redistAStarts = new List<(Vector3 pos, Quaternion rot)>();
        var redistBStarts = new List<(Vector3 pos, Quaternion rot)>();
        foreach (var entry in redistA)
            redistAStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        foreach (var entry in redistB)
            redistBStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));

        Vector3? rearrangeStartPos = null;
        Quaternion? rearrangeStartRot = null;
        if (rearrangeTargetInfo.HasValue)
        {
            var (orbToMove, _, _) = rearrangeTargetInfo.Value;
            rearrangeStartPos = orbToMove.transform.localPosition;
            rearrangeStartRot = orbToMove.transform.localRotation;
        }

        if (!skipStep2)
        for (float t = 0; t < bondAnimStep2Duration; t += Time.deltaTime)
        {
            float s = Mathf.Clamp01(t / bondAnimStep2Duration);
            s = s * s * (3f - 2f * s); // smoothstep for position
            float rotT = 1f - (1f - s) * (1f - s); // ease-out quad - rotation leads, expresses orbital rotation visibly

            survivingOrbital.transform.position = Vector3.Lerp(bondOrbitalStartWorldPos, bondOrbitalEnd.worldPos, s);
            survivingOrbital.transform.rotation = Quaternion.Slerp(bondOrbitalStartWorldRot, bondOrbitalEnd.worldRot, rotT);

            if (rearrangeTargetInfo.HasValue)
            {
                var (orbToMove, targetPos, targetRot) = rearrangeTargetInfo.Value;
                if (orbToMove != null && rearrangeStartPos.HasValue && rearrangeStartRot.HasValue)
                {
                    orbToMove.transform.localPosition = Vector3.Lerp(rearrangeStartPos.Value, targetPos, s);
                    orbToMove.transform.localRotation = Quaternion.Slerp(rearrangeStartRot.Value, targetRot, rotT);
                }
            }
            for (int i = 0; i < redistA.Count; i++)
            {
                var entry = redistA[i];
                if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform)
                {
                    var (sp, sr) = redistAStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var entry = redistB[i];
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform)
                {
                    var (sp, sr) = redistBStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            yield return null;
        }

        if (rearrangeTargetInfo.HasValue)
        {
            var (orbToMove, targetPos, targetRot) = rearrangeTargetInfo.Value;
            if (orbToMove != null)
            {
                orbToMove.transform.localPosition = targetPos;
                orbToMove.transform.localRotation = targetRot;
            }
        }
        sourceAtom.ApplyRedistributeTargets(redistA);
        targetAtom.ApplyRedistributeTargets(redistB);
        bond.UpdateBondTransformToCurrentAtoms(); // Bond may be stale (atoms moved in step 1)
        bond.SnapOrbitalToBondPosition(); // Match step 3 start position (prevents teleport)
        bond.animatingOrbitalToBondPosition = false;

        // Step 3: Orbital-to-line transformation (bonding orbital shrinking, target orbital fading, line growing simultaneously)
        if (bond != null)
        {
            yield return bond.AnimateOrbitalToLine(bondAnimStep3Duration, targetOrbital);
            survivingOrbital.ElectronCount = merged; // Show merged electrons only after step 3
        }
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();
        }
        finally
        {
            foreach (var a in atomsToBlock) a.SetInteractionBlocked(false);
        }
    }

    (ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)? GetRearrangeTarget(AtomFunction sourceAtom, ElectronOrbitalFunction targetOrbital, Vector3? targetPointOverride)
    {
        float oppositeAngleWorld;
        if (targetPointOverride.HasValue)
        {
            var dir = (targetPointOverride.Value - sourceAtom.transform.position).normalized;
            oppositeAngleWorld = OrbitalAngleUtility.DirectionToAngleWorld(dir);
            foreach (var orb in sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb != this && orb.Bond == null && IsOrbitalAlignedWithTargetPoint(orb, targetPointOverride.Value))
                    return null;
            }
        }
        else
        {
            float targetAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(targetOrbital.transform);
            oppositeAngleWorld = OrbitalAngleUtility.NormalizeAngle(targetAngleWorld + 180f);
            float draggedAngleWorld = OrbitalAngleUtility.LocalRotationToAngleWorld(sourceAtom.transform, originalLocalRotation);
            if (Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(draggedAngleWorld - oppositeAngleWorld)) < 45f)
                return null;
        }
        var sourceOrbitals = sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>();
        ElectronOrbitalFunction orbToMove = null;
        float bestDelta = 360f;
        foreach (var orb in sourceOrbitals)
        {
            if (orb == this || orb.Bond != null) continue;
            float orbAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
            float delta = Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(orbAngleWorld - oppositeAngleWorld));
            if (delta < bestDelta) { bestDelta = delta; orbToMove = orb; }
        }
        if (orbToMove == null) return null;
        float freedSlotAngle = targetPointOverride.HasValue
            ? OrbitalAngleUtility.DirectionToAngleWorld((targetPointOverride.Value - sourceAtom.transform.position).normalized)
            : NormalizeAngle(originalLocalRotation.eulerAngles.z);
        var slotPos = GetCanonicalSlotPosition(freedSlotAngle, sourceAtom.BondRadius);
        return (orbToMove, slotPos.position, slotPos.rotation);
    }

    List<(AtomFunction atom, Vector3 targetPos)> GetAlignTargetPositions(AtomFunction moleculeRoot, AtomFunction referenceAtom, Vector3 referenceOrbitalTip)
    {
        var refPos = referenceAtom.transform.position;
        var toOrbital = referenceOrbitalTip - refPos;
        var dist = toOrbital.magnitude;
        if (dist < 0.01f) return new List<(AtomFunction, Vector3)>();
        var dir = toOrbital / dist;
        var newMoleculeRootPos = referenceOrbitalTip + dir * dist;
        var delta = newMoleculeRootPos - moleculeRoot.transform.position;
        var result = new List<(AtomFunction, Vector3)>();
        foreach (var a in moleculeRoot.GetConnectedMolecule())
            result.Add((a, a.transform.position + delta));
        return result;
    }

    bool FormCovalentBondPiStart(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        targetAtom.StartCoroutine(FormCovalentBondPiCoroutine(sourceAtom, targetAtom, targetOrbital));
        return true;
    }

    IEnumerator FormCovalentBondPiCoroutine(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock) a.SetInteractionBlocked(true);

        try
        {
        int piBeforeSource = sourceAtom.GetPiBondCount();
        int piBeforeTarget = targetAtom.GetPiBondCount();
        int mergedElectrons = electronCount + targetOrbital.ElectronCount;

        // Snap source orbital to original position (like sigma bond) before animating to bond center
        transform.localPosition = originalLocalPosition;
        transform.localRotation = originalLocalRotation;
        transform.localScale = originalLocalScale;

        sourceAtom.UnbondOrbital(this);
        targetAtom.UnbondOrbital(targetOrbital);

        var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: true);
        transform.SetParent(null); // Detach source orbital but keep visible for step 2 animation (preserves world pos from snap)
        bond.SetOrbitalBeingFaded(this); // Use merged count for charge during animation (source orbital is being faded)
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        yield return AnimateRedistributeOrbitals(sourceAtom, targetAtom, piBeforeSource, piBeforeTarget, this, targetOrbital, bond);

        if (bond != null)
        {
            bond.animatingOrbitalToBondPosition = false;
            yield return bond.AnimateOrbitalToLine(bondAnimStep3Duration, this);
            targetOrbital.ElectronCount = mergedElectrons; // Show merged electrons only after step 3
        }
        else
        {
            Destroy(gameObject);
        }
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();
        }
        finally
        {
            foreach (var a in atomsToBlock) a.SetInteractionBlocked(false);
        }
    }

    IEnumerator AnimateRedistributeOrbitals(AtomFunction sourceAtom, AtomFunction targetAtom, int piBeforeSource, int piBeforeTarget,
        ElectronOrbitalFunction sourceOrbital, ElectronOrbitalFunction targetOrbital, CovalentBond bond)
    {
        var redistA = sourceAtom.GetRedistributeTargets(piBeforeSource, targetAtom);
        var redistB = targetAtom.GetRedistributeTargets(piBeforeTarget, sourceAtom);
        if (redistA.Count == 0 && redistB.Count == 0)
            TryRedistributeOrbitalsAfterBondChange(sourceAtom, targetAtom, piBeforeSource, piBeforeTarget);

        var redistAStarts = new List<(Vector3 pos, Quaternion rot)>();
        var redistBStarts = new List<(Vector3 pos, Quaternion rot)>();
        foreach (var entry in redistA)
            redistAStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));
        foreach (var entry in redistB)
            redistBStarts.Add(entry.orb != null ? (entry.orb.transform.localPosition, entry.orb.transform.localRotation) : (Vector3.zero, Quaternion.identity));

        // Pi bond: animate both orbitals to bond center. Flip source or target so electrons align in a row.
        Vector3 bondTargetPos = Vector3.zero;
        Quaternion bondTargetRot = Quaternion.identity;
        Quaternion sourceTargetRot = Quaternion.identity;
        Quaternion targetTargetRot = Quaternion.identity;
        if (bond != null)
        {
            var bt = bond.GetOrbitalTargetWorldState();
            bondTargetPos = bt.Item1;
            bondTargetRot = bt.Item2;
            (float sourceDiff, float targetDiff) = LogPiBondAngles(sourceAtom, targetAtom, bondTargetPos, bondTargetRot, bond);
            bool flipTarget = Mathf.Abs(sourceDiff) < Mathf.Abs(targetDiff); // Source closer to bond → flip target
            if (flipTarget)
            {
                bond.orbitalRotationFlipped = true;
                bt = bond.GetOrbitalTargetWorldState(); // Re-fetch with flip
                bondTargetPos = bt.Item1;
                bondTargetRot = bt.Item2; // Target (bond orbital) gets flipped rotation
                sourceTargetRot = bondTargetRot * Quaternion.Euler(0f, 0f, 180f); // Source opposite to target
                targetTargetRot = bondTargetRot;
            }
            else
            {
                sourceTargetRot = bondTargetRot * Quaternion.Euler(0f, 0f, 180f);
                targetTargetRot = bondTargetRot;
            }
        }
        var sourceOrbStart = sourceOrbital != null ? (sourceOrbital.transform.position, sourceOrbital.transform.rotation) : (Vector3.zero, Quaternion.identity);
        var targetOrbStart = targetOrbital != null ? (targetOrbital.transform.position, targetOrbital.transform.rotation) : (Vector3.zero, Quaternion.identity);

        const float piAlignThreshold = 0.05f;
        const float piPosThreshold = 0.01f;
        const float piRotThreshold = 1f;
        bool sourceAtBond = sourceOrbital == null || bond == null || (Vector3.Distance(sourceOrbital.transform.position, bondTargetPos) < piAlignThreshold && Quaternion.Angle(sourceOrbital.transform.rotation, sourceTargetRot) < piRotThreshold);
        bool targetAtBond = targetOrbital == null || bond == null || (Vector3.Distance(targetOrbital.transform.position, bondTargetPos) < piAlignThreshold && Quaternion.Angle(targetOrbital.transform.rotation, targetTargetRot) < piRotThreshold);
        bool hasRedistMovement = false;
        foreach (var entry in redistA)
        {
            if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform
                && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > piPosThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > piRotThreshold))
            { hasRedistMovement = true; break; }
        }
        if (!hasRedistMovement)
            foreach (var entry in redistB)
            {
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform
                    && (Vector3.Distance(entry.orb.transform.localPosition, entry.pos) > piPosThreshold || Quaternion.Angle(entry.orb.transform.localRotation, entry.rot) > piRotThreshold))
                { hasRedistMovement = true; break; }
            }
        bool skipPiStep2 = sourceAtBond && targetAtBond && !hasRedistMovement;

        if (!skipPiStep2)
        for (float t = 0; t < bondAnimStep2Duration; t += Time.deltaTime)
        {
            float s = Mathf.Clamp01(t / bondAnimStep2Duration);
            s = s * s * (3f - 2f * s); // smoothstep for position
            float rotT = 1f - (1f - s) * (1f - s); // ease-out quad - rotation leads, expresses orbital rotation visibly

            if (sourceOrbital != null && bond != null)
            {
                sourceOrbital.transform.position = Vector3.Lerp(sourceOrbStart.Item1, bondTargetPos, s);
                sourceOrbital.transform.rotation = Quaternion.Slerp(sourceOrbStart.Item2, sourceTargetRot, rotT);
            }
            if (targetOrbital != null && bond != null)
            {
                targetOrbital.transform.position = Vector3.Lerp(targetOrbStart.Item1, bondTargetPos, s);
                targetOrbital.transform.rotation = Quaternion.Slerp(targetOrbStart.Item2, targetTargetRot, rotT);
            }

            for (int i = 0; i < redistA.Count; i++)
            {
                var entry = redistA[i];
                if (entry.orb != null && entry.orb.transform.parent == sourceAtom.transform)
                {
                    var (sp, sr) = redistAStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            for (int i = 0; i < redistB.Count; i++)
            {
                var entry = redistB[i];
                if (entry.orb != null && entry.orb.transform.parent == targetAtom.transform)
                {
                    var (sp, sr) = redistBStarts[i];
                    entry.orb.transform.localPosition = Vector3.Lerp(sp, entry.pos, s);
                    entry.orb.transform.localRotation = Quaternion.Slerp(sr, entry.rot, rotT);
                }
            }
            yield return null;
        }
        sourceAtom.ApplyRedistributeTargets(redistA);
        targetAtom.ApplyRedistributeTargets(redistB);

        // Snap both orbitals to exact position step 3 expects (prevents teleport)
        if (bond != null)
        {
            bond.UpdateBondTransformToCurrentAtoms(); // Bond may be stale
            bond.SnapOrbitalToBondPosition();
            if (sourceOrbital != null)
            {
                var bt = bond.GetOrbitalTargetWorldState();
                sourceOrbital.transform.position = bt.Item1;
                sourceOrbital.transform.rotation = sourceTargetRot;
            }
        }
    }

    static (float sourceDiff, float targetDiff) LogPiBondAngles(AtomFunction sourceAtom, AtomFunction targetAtom, Vector3 bondPos, Quaternion bondRot, CovalentBond bond)
    {
        var toCenterFromSource = bondPos - sourceAtom.transform.position;
        if (toCenterFromSource.sqrMagnitude < 0.0001f) { Debug.Log("[Pi] toCenterFromSource too small"); return (0f, 0f); }
        toCenterFromSource.Normalize();
        toCenterFromSource.z = 0;
        float sourceAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenterFromSource);
        var toCenterFromTarget = bondPos - targetAtom.transform.position;
        if (toCenterFromTarget.sqrMagnitude >= 0.0001f && bond != null)
        {
            toCenterFromTarget.Normalize();
            toCenterFromTarget.z = 0;
            float targetAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenterFromTarget);
            var bondDir = bondRot * Vector3.right;
            bondDir.z = 0;
            if (bondDir.sqrMagnitude >= 0.0001f)
            {
                bondDir.Normalize();
                float bondAngle = OrbitalAngleUtility.DirectionToAngleWorld(bondDir);
                float sourceDiff = OrbitalAngleUtility.NormalizeAngle(sourceAngle - bondAngle);
                float targetDiff = OrbitalAngleUtility.NormalizeAngle(targetAngle - bondAngle);
                bool flipTarget = Mathf.Abs(sourceDiff) < Mathf.Abs(targetDiff);
                Debug.Log($"[Pi] sourceAngle={sourceAngle:F1}° targetAngle={targetAngle:F1}° bondAngle={bondAngle:F1}° sourceDiff={sourceDiff:F1}° targetDiff={targetDiff:F1}° flipTarget={flipTarget}");
                return (sourceDiff, targetDiff);
            }
        }
        Debug.Log($"[Pi] sourceAngle={sourceAngle:F1}° (target/bond calc skipped)");
        return (0f, 0f);
    }

    static float LogSigmaBondAngles(AtomFunction sourceAtom, Vector3 bondPos, Quaternion bondRot)
    {
        var toCenter = bondPos - sourceAtom.transform.position;
        if (toCenter.sqrMagnitude < 0.0001f) { Debug.Log("[Sigma] toCenter too small"); return 0f; }
        toCenter.Normalize();
        toCenter.z = 0;
        float sourceAngle = OrbitalAngleUtility.DirectionToAngleWorld(toCenter);
        var bondDir = bondRot * Vector3.right;
        bondDir.z = 0;
        if (bondDir.sqrMagnitude < 0.0001f) { Debug.Log("[Sigma] bondDir too small"); return 0f; }
        bondDir.Normalize();
        float bondAngle = OrbitalAngleUtility.DirectionToAngleWorld(bondDir);
        float diff = OrbitalAngleUtility.NormalizeAngle(sourceAngle - bondAngle);
        Debug.Log($"[Sigma] sourceAngle={sourceAngle:F1}° bondAngle={bondAngle:F1}° diff={diff:F1}° flip={Mathf.Abs(diff) > 90f}");
        return diff;
    }

    static void TryRedistributeOrbitalsAfterBondChange(AtomFunction sourceAtom, AtomFunction targetAtom, int piBeforeSource, int piBeforeTarget)
    {
        if (sourceAtom != null && sourceAtom.GetPiBondCount() != piBeforeSource)
            sourceAtom.RedistributeOrbitals();
        if (targetAtom != null && targetAtom.GetPiBondCount() != piBeforeTarget)
            targetAtom.RedistributeOrbitals();
    }

    bool IsSourceOrbitalAlreadyAlignedWithTarget(AtomFunction sourceAtom, ElectronOrbitalFunction targetOrbital)
    {
        float targetAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(targetOrbital.transform);
        float oppositeAngleWorld = OrbitalAngleUtility.NormalizeAngle(targetAngleWorld + 180f);
        float draggedAngleWorld = OrbitalAngleUtility.LocalRotationToAngleWorld(sourceAtom.transform, originalLocalRotation);
        const float tolerance = 45f;
        return Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(draggedAngleWorld - oppositeAngleWorld)) < tolerance;
    }

    static bool IsOrbitalAlignedWithTargetPoint(ElectronOrbitalFunction orbital, Vector3 targetPoint)
    {
        var atomPos = orbital.transform.parent != null ? orbital.transform.parent.position : orbital.transform.position;
        var dirToTarget = (targetPoint - atomPos).normalized;
        float targetAngleWorld = OrbitalAngleUtility.DirectionToAngleWorld(dirToTarget);
        float orbitalAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(orbital.transform);
        const float tolerance = 45f;
        return Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(orbitalAngleWorld - targetAngleWorld)) < tolerance;
    }

    void RearrangeOrbitalToFaceTarget(AtomFunction sourceAtom, ElectronOrbitalFunction targetOrbital, Vector3? targetPointOverride = null)
    {
        float oppositeAngleWorld;
        if (targetPointOverride.HasValue)
        {
            var dirFromSourceToTarget = (targetPointOverride.Value - sourceAtom.transform.position).normalized;
            oppositeAngleWorld = OrbitalAngleUtility.DirectionToAngleWorld(dirFromSourceToTarget);
            foreach (var orb in sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb != this && orb.Bond == null && IsOrbitalAlignedWithTargetPoint(orb, targetPointOverride.Value))
                    return;
            }
        }
        else
        {
            float targetAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(targetOrbital.transform);
            oppositeAngleWorld = OrbitalAngleUtility.NormalizeAngle(targetAngleWorld + 180f);
            float draggedAngleWorld = OrbitalAngleUtility.LocalRotationToAngleWorld(sourceAtom.transform, originalLocalRotation);
            const float tolerance = 45f;
            if (Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(draggedAngleWorld - oppositeAngleWorld)) < tolerance)
                return;
        }

        var sourceOrbitals = sourceAtom.GetComponentsInChildren<ElectronOrbitalFunction>();
        ElectronOrbitalFunction orbToMove = null;
        float bestDelta = 360f;
        foreach (var orb in sourceOrbitals)
        {
            if (orb == this || orb.Bond != null) continue;
            float orbAngleWorld = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
            float delta = Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(orbAngleWorld - oppositeAngleWorld));
            if (delta < bestDelta)
            {
                bestDelta = delta;
                orbToMove = orb;
            }
        }
        if (orbToMove != null)
        {
            float freedSlotAngle = targetPointOverride.HasValue
                ? OrbitalAngleUtility.DirectionToAngleWorld((targetPointOverride.Value - sourceAtom.transform.position).normalized)
                : NormalizeAngle(originalLocalRotation.eulerAngles.z);
            var slotPos = GetCanonicalSlotPosition(freedSlotAngle, sourceAtom.BondRadius);
            orbToMove.transform.localPosition = slotPos.position;
            orbToMove.transform.localRotation = slotPos.rotation;
        }
    }

    public static (Vector3 position, Quaternion rotation) GetCanonicalSlotPosition(float angleDeg, float bondRadius)
    {
        float angle = NormalizeAngle(angleDeg);
        float rad = angle * Mathf.Deg2Rad;
        float offset = bondRadius * 0.6f;
        var dir = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f);
        var pos = dir * offset;
        var rot = Quaternion.Euler(0, 0, angle);
        return (pos, rot);
    }

    public static float NormalizeAngle(float deg)
    {
        while (deg > 180f) deg -= 360f;
        while (deg < -180f) deg += 360f;
        return deg;
    }

    bool IsSourceFlippedSideFilled(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3? orbitalTipOverride = null)
    {
        var targetPos = targetAtom.transform.position;
        var orbitalTip = orbitalTipOverride ?? targetOrbital.transform.position;
        var targetOrbitalDir = (orbitalTip - targetPos).normalized;
        float targetOrbitalAngle = OrbitalAngleUtility.DirectionToAngleWorld(targetOrbitalDir);
        float flippedAngle = OrbitalAngleUtility.NormalizeAngle(targetOrbitalAngle + 180f);
        const float toleranceDeg = 45f;
        var sourcePos = sourceAtom.transform.position;

        foreach (var b in sourceAtom.CovalentBonds)
        {
            if (b == null) continue;
            var other = b.AtomA == sourceAtom ? b.AtomB : b.AtomA;
            if (other == null) continue;
            var dirToOther = (other.transform.position - sourcePos).normalized;
            float bondAngleDeg = OrbitalAngleUtility.DirectionToAngleWorld(dirToOther);
            float bondAngleNorm = OrbitalAngleUtility.NormalizeAngle(bondAngleDeg);
            float delta = NormalizeAngle(bondAngleNorm - flippedAngle);
            float absDelta = Mathf.Abs(delta);
            bool isFlippedSide = absDelta < toleranceDeg;

            if (isFlippedSide)
                return true;
        }
        return false;
    }

    void AlignSourceAtomNextToTarget(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital, Vector3? targetOrbitalTipOverride = null)
    {
        var targetPos = targetAtom.transform.position;
        var targetOrbitalTip = targetOrbitalTipOverride ?? targetOrbital.transform.position;
        var toOrbital = targetOrbitalTip - targetPos;
        var distToOrbital = toOrbital.magnitude;
        if (distToOrbital < 0.01f) return;
        var dir = toOrbital / distToOrbital;
        var newSourcePos = targetOrbitalTip + dir * distToOrbital;
        var delta = newSourcePos - sourceAtom.transform.position;
        foreach (var a in sourceAtom.GetConnectedMolecule())
            a.transform.position += delta;
    }

    void AlignTargetAtomNextToSource(AtomFunction sourceAtom, AtomFunction targetAtom, ElectronOrbitalFunction targetOrbital)
    {
        var targetPos = targetAtom.transform.position;
        var targetOrbitalTip = targetOrbital.transform.position;
        var toOrbital = targetOrbitalTip - targetPos;
        var distToOrbital = toOrbital.magnitude;
        if (distToOrbital < 0.01f) return;
        var dir = toOrbital / distToOrbital;
        var sourceOrbitalTip = transform.position;
        var newTargetPos = sourceOrbitalTip - dir * distToOrbital;
        var delta = newTargetPos - targetAtom.transform.position;
        foreach (var a in targetAtom.GetConnectedMolecule())
            a.transform.position += delta;
    }

    void CreateStretchVisual()
    {
        if (stretchVisual != null) return;
        var anchor = GetAtomCenter();
        var tip = transform.position;
        stretchVisual = new GameObject("StretchVisual");
        stretchVisual.transform.SetParent(transform.parent);
        stretchVisual.transform.localScale = Vector3.one;

        var color = mainSpriteRenderer != null ? mainSpriteRenderer.color : new Color(1, 1, 1, 0.4f);
        var sortOrder = mainSpriteRenderer != null ? mainSpriteRenderer.sortingOrder : 0;
        var sortLayer = mainSpriteRenderer != null ? mainSpriteRenderer.sortingLayerID : 0;

        if (stretchSprite != null)
        {
            var sr = stretchVisual.AddComponent<SpriteRenderer>();
            sr.sprite = stretchSprite;
            sr.color = color;
            sr.sortingOrder = sortOrder;
            sr.sortingLayerID = sortLayer;
        }
        else
        {
            var tri = new GameObject("Triangle");
            tri.transform.SetParent(stretchVisual.transform);
            tri.transform.localPosition = Vector3.zero;
            tri.transform.localRotation = Quaternion.identity;
            tri.transform.localScale = Vector3.one;
            var triSr = tri.AddComponent<SpriteRenderer>();
            triSr.sprite = GetOrCreateTriangleSprite();
            triSr.color = color;
            triSr.sortingOrder = sortOrder;
            triSr.sortingLayerID = sortLayer;
            var clipMat = GetOrCreateTriangleClipMaterial();
            if (clipMat != null) triSr.material = clipMat;

            var circle = new GameObject("Circle");
            circle.transform.SetParent(stretchVisual.transform);
            circle.transform.localPosition = Vector3.zero;
            circle.transform.localRotation = Quaternion.identity;
            circle.transform.localScale = Vector3.one;
            var circleSr = circle.AddComponent<SpriteRenderer>();
            circleSr.sprite = GetOrCreateCircleSprite();
            circleSr.color = color;
            circleSr.sortingOrder = sortOrder;
            circleSr.sortingLayerID = sortLayer;
        }
        UpdateStretchVisual(tip);
    }

    Vector3 GetAtomCenter() => transform.parent != null ? transform.parent.position : transform.position;

    void UpdateStretchVisual(Vector3 tip)
    {
        if (stretchVisual == null) return;
        var anchor = GetAtomCenter();
        var dir = (tip - anchor);
        var distance = dir.magnitude;
        if (distance < 0.01f && bond == null) return;
        if (distance < 0.01f) distance = 0f;
        dir = distance > 0.01f ? dir / distance : Vector3.up;
        // Bond orbitals: allow stretch to reach origin (no min length). Lone orbitals: MinStretchLength avoids overlap.
        float minLength = (bond != null) ? 0f : MinStretchLength;
        distance = Mathf.Max(distance, minLength);

        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f;
        stretchVisual.transform.rotation = Quaternion.Euler(0, 0, angle);

        if (stretchSprite != null)
        {
            stretchVisual.transform.position = (anchor + tip) * 0.5f;
            stretchVisual.transform.localScale = Vector3.one;
        }
        else
        {
            stretchVisual.transform.localScale = Vector3.one;
            var tri = stretchVisual.transform.Find("Triangle");
            var circle = stretchVisual.transform.Find("Circle");
            // Bond orbitals: no nucleus at center, so triangle tip can reach the origin. Lone orbitals: use apexPadding to avoid overlapping nucleus.
            float padding = (bond != null) ? 0f : Mathf.Min(apexPadding, distance * 0.4f);
            float triLength = distance - padding;
            Vector3 triApex = anchor + padding * dir;
            Vector3 triBase = tip;
            stretchVisual.transform.position = (triApex + triBase) * 0.5f;
            const float ProceduralSpriteSize = 2f;
            float targetSize = ProceduralSpriteSize * originalLocalScale.y;
            float circleScale = targetSize / ProceduralSpriteSize;
            float triWidthScale = circleScale * 2f;
            float triHeightScale = triLength / ProceduralSpriteSize;
            if (tri != null)
            {
                tri.localPosition = Vector3.zero;
                tri.localRotation = Quaternion.identity;
                tri.localScale = new Vector3(triWidthScale, triHeightScale, 1f);
                var triSr = tri.GetComponent<SpriteRenderer>();
                if (triSr != null && triSr.material != null)
                {
                    var block = new MaterialPropertyBlock();
                    triSr.GetPropertyBlock(block);
                    block.SetVector(ClipCenterId, new Vector4(0f, 1f, 0f, 0f));
                    float clipRadius = circleScale;
                    block.SetFloat(ClipRadiusXId, clipRadius / Mathf.Max(triWidthScale, 0.001f));
                    block.SetFloat(ClipRadiusYId, clipRadius / Mathf.Max(triHeightScale, 0.001f));
                    triSr.SetPropertyBlock(block);
                }
            }
            if (circle != null)
            {
                circle.localPosition = new Vector3(0f, triHeightScale, 0f);
                circle.localRotation = Quaternion.identity;
                circle.localScale = new Vector3(circleScale * OffsetScale, circleScale * OffsetScale, 1f);
            }
        }
    }

    static Vector3 ScreenToWorld(Vector2 screenPos)
    {
        var mouse = new Vector3(screenPos.x, screenPos.y, -Camera.main.transform.position.z);
        return Camera.main.ScreenToWorldPoint(mouse);
    }

    void Start()
    {
        if (originalLocalScale.sqrMagnitude < 0.01f) originalLocalScale = transform.localScale;
        var sr = GetComponent<SpriteRenderer>();
        if (sr != null && originalColor.a < 0.01f) originalColor = sr.color;
        EnsureCollider();
        SyncElectronObjects();
        IgnoreCollisionsWithChildren();
    }

    void IgnoreCollisionsWithChildren()
    {
        var orbitalCol2D = GetComponent<Collider2D>();
        var orbitalCol = GetComponent<Collider>();
        var electrons = GetComponentsInChildren<ElectronFunction>();
        foreach (var electron in electrons)
        {
            IgnoreCollision(orbitalCol2D, orbitalCol, electron);
            foreach (var other in electrons)
            {
                if (electron == other) continue;
                IgnoreCollision(electron.GetComponent<Collider2D>(), electron.GetComponent<Collider>(),
                    other.GetComponent<Collider2D>(), other.GetComponent<Collider>());
            }
        }
    }

    static void IgnoreCollision(Collider2D a2D, Collider a3D, ElectronFunction b)
    {
        var b2D = b.GetComponent<Collider2D>();
        var b3D = b.GetComponent<Collider>();
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    static void IgnoreCollision(Collider2D a2D, Collider a3D, Collider2D b2D, Collider b3D)
    {
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    [SerializeField] Vector2 orbitalColliderSize = new Vector2(1f, 0.15f); // Thin bar so it doesn't overlap electrons at y=±0.25

    void EnsureCollider()
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col == null && col2D == null)
        {
            col = gameObject.AddComponent<BoxCollider>();
            col.isTrigger = true;
        }
        if (col is BoxCollider box)
            box.size = new Vector3(orbitalColliderSize.x, orbitalColliderSize.y, 0.1f);
        if (col2D is BoxCollider2D box2D)
            box2D.size = orbitalColliderSize;
    }

    void Update() { }

    void SyncElectronObjects()
    {
        if (electronPrefab == null) return;

        var existing = GetComponentsInChildren<ElectronFunction>();
        int target = electronCount;

        if (existing.Length > target)
        {
            for (int i = target; i < existing.Length; i++)
                Destroy(existing[i].gameObject);
        }
        else if (existing.Length < target)
        {
            for (int i = existing.Length; i < target; i++)
            {
                var electron = Instantiate(electronPrefab, transform);
                electron.SetSlotIndex(i);
                electron.SetOrbitalWidth(electronSpacing);
            }
        }

        var electrons = GetComponentsInChildren<ElectronFunction>();
        for (int i = 0; i < electrons.Length; i++)
        {
            electrons[i].SetSlotIndex(i);
            electrons[i].SetOrbitalWidth(electronSpacing);
        }

        IgnoreCollisionsWithChildren();
    }
}
