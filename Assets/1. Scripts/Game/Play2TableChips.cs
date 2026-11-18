using System;
using System.Collections;
using System.Collections.Generic;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;

// Only confirmed public contributions trigger motion. Piles are symbolic; the HUD owns the exact amount.
internal sealed class Play2TableChips : MonoBehaviour
{
    private readonly Transform[] pile = new Transform[18];
    private readonly Transform[] moving = new Transform[4];
    private readonly Play2BettingHand[] hands = new Play2BettingHand[4];
    private readonly Queue<HoldemHistoryEntryPacket> bets = new();
    private readonly Transform[] allInStack = new Transform[12];
    private int pushChips;
    private readonly long[] paidBySeat = new long[4];
    private long handPot, paidTotal;
    private readonly Transform[] heads = new Transform[4];
    private HoldemHistoryEntryPacket action;
    private bool struck;
    private Quaternion beforeFlinch;
    private Transform flinching;
    private Vector3 hitPoint, dropPoint, pushStart, pushStop, pushSide;
    internal float PushTime(int index) => activeSeat == index && action?.allIn == true ? Time.time - began : -1;
    internal bool WaitToStand(int index)
    {
        if (action?.allIn == true && activeSeat == index) return true;
        foreach (var bet in bets) if (bet.seat == index + 1 && bet.allIn) return true;
        return false;
    }
    internal bool IsAnimating => activeSeat >= 0 || bets.Count > 0;
    internal bool IsBetting(int index)
    {
        if (activeSeat == index) return true;
        foreach (var bet in bets) if (bet.seat == index + 1) return true;
        return false;
    }
    internal Play2BettingHand FreeHand(int index) => hands[index] ??=
        new Play2BettingHand(players[index], index + 1 == viewer ? viewCamera : null);
    internal void RestHand(int index) => hands[index]?.Rest();
    private static readonly Vector3 PotPosition = new(-.28f, .014f, .27f);
    private Transform[] eyes;
    private GameObject[] players;
    private Transform viewCamera;
    private Play2TableAudio sound;
    private Material material;
    private Mesh chipMesh;
    private int viewer, activeSeat = -1, targetCount, shownCount;
    private float began;
    private bool landed;
    private Vector3 flightStart, flightEnd, touchDown;
    private float releaseYaw, landingYaw;

    public void Initialize(Transform[] seatEyes, Material chipMaterial, GameObject[] actors,
        Transform view, Play2TableAudio audio)
    {
        eyes = seatEyes;
        material = chipMaterial;
        tint = new MaterialPropertyBlock();
        chipMesh = Resources.Load<Mesh>("TablePresentation/PokerChip");
        players = actors;
        viewCamera = view;
        sound = audio;
        for (int i = 0; i < pile.Length; i++)
        {
            pile[i] = MakeChip("Pot chip", StackColor(i));
            pile[i].localPosition = StackPosition(i);
            pile[i].localRotation = Quaternion.Euler(0, StackYaw(i), 0);
        }
        for (int i = 0; i < moving.Length; i++)
        {
            moving[i] = MakeChip("Bet from seat " + (i + 1), StackColor(0));
            heads[i] = actors[i].GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head);
        }
        for (int i = 0; i < allInStack.Length; i++)
            allInStack[i] = MakeChip("All-in chip " + (i + 1), StackColor(i % 2 == 0 ? 0 : 6));
    }

    private static Vector3 StackPosition(int i) => PotPosition
        + new Vector3((i / 6) * .071f, (i % 6) * .0062f, (i / 6 == 1 ? -.025f : 0));

    private static Color StackColor(int index) => index / 6 == 1 ? Play2UGui.Hex("405D71") : Play2UGui.Hex("AE5042");
    private static float StackYaw(int index) => (index * 47 + 9) % 360;

    private Transform MakeChip(string label, Color color)
    {
        var chip = new GameObject(label).transform;
        chip.SetParent(transform, false);
        chip.gameObject.AddComponent<MeshFilter>().sharedMesh = chipMesh;
        var renderer = chip.gameObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = new[] { material, material };
        Tint(renderer, 0, color);
        Tint(renderer, 1, Play2UGui.Cream);
        chip.gameObject.SetActive(false);
        return chip;
    }

    private MaterialPropertyBlock tint;

    private void Tint(Renderer renderer, int slot, Color color)
    {
        tint.SetColor("_BaseColor", color);
        tint.SetFloat("_Smoothness", .24f);
        tint.SetFloat("_Metallic", 0);
        renderer.SetPropertyBlock(tint, slot);
    }

    public void Present(HoldemRoomPacket previous, HoldemRoomPacket next)
    {
        var game = next?.hasGame == true ? next.game : null;
        var old = previous?.hasGame == true ? previous.game : null;
        handPot = game?.pot ?? 0;
        if (game == null || old == null || previous.sessionId != next.sessionId || game.handId != old.handId || viewer != next.viewerSeat)
        {
            ResetHands();
            paidTotal = 0;
            Array.Clear(paidBySeat, 0, paidBySeat.Length);
            targetCount = PileSize(handPot);
            viewer = next?.viewerSeat ?? 1;
            ShowPile(targetCount);
            return;
        }
        targetCount = PileSize(handPot - paidTotal);
        var history = next.history;
        if (history?.entries == null || previous.history?.entries == null) return;
        long previousCount = previous.history.omittedCount + previous.history.entries.Length;
        int first = (int)Math.Max(0, Math.Min(history.entries.Length, previousCount - history.omittedCount));
        for (int i = first; i < history.entries.Length; i++)
        {
            var entry = history.entries[i];
            if (entry.seat < 1 || entry.seat > moving.Length) continue;
            if (entry.hasAction && entry.action == (int)BettingActionKind.Check) sound.Check();
            if (entry.hasAction && entry.action == (int)BettingActionKind.Fold
                || entry.kind == (int)HoldemHistoryKind.FixedFold) sound.Fold();
            if (!entry.hasAction || entry.amount <= 0) continue;
            if (entry.action == (int)BettingActionKind.Call || entry.action == (int)BettingActionKind.BetTo
                || entry.action == (int)BettingActionKind.RaiseTo)
                bets.Enqueue(entry);
        }
        if (activeSeat < 0 && bets.Count == 0) ShowPile(targetCount);
    }

    // Called after the card-holding arm has been posed, so the free hand cannot fight the grip animation.
    internal void UpdateHands()
    {
        if (activeSeat < 0 && bets.Count > 0) StartBet();
        if (activeSeat < 0) return;
        int index = activeSeat;
        float time = Time.time - began;
        var chip = moving[index];
        if (action.allIn)
        {
            AnimatePush(time);
            if (time >= 1.30f) FinishBet();
            return;
        }
        hands[index].Pose(time);
        if (time < .26f)
        {
            chip.position = hands[index].Contact;
            chip.localRotation = Quaternion.Euler(0, releaseYaw, 0);
            flightStart = chip.localPosition;
        }
        else if (action.chipAimSeat > 0)
        {
            AnimateTargeted(chip, time);
            if (time < 1.28f) return;
        }
        else
        {
            float t = Mathf.Clamp01((time - .26f) / .28f);
            chip.localPosition = Vector3.Lerp(flightStart, touchDown, t)
                + Vector3.up * (.043f * 4f * t * (1 - t));
            chip.localRotation = Quaternion.Euler(10f * Mathf.Sin(t * Mathf.PI),
                Mathf.LerpAngle(releaseYaw, landingYaw - 12f, t), 0);
            if (t >= 1)
            {
                if (!landed) { landed = true; sound.ChipLand(); }
                float settle = Mathf.Clamp01((time - .54f) / .13f);
                float slide = 1f - (1f - settle) * (1f - settle);
                chip.localPosition = Vector3.Lerp(touchDown, flightEnd, slide);
                chip.localRotation = Quaternion.Euler(3f * Mathf.Sin(settle * 2f * Mathf.PI) * (1f - settle),
                    landingYaw - 12f * (1f - slide), 0);
            }
        }
        if (time < .76f) return;
        FinishBet();
    }

    private void FinishBet()
    {
        moving[activeSeat].gameObject.SetActive(false);
        if (action.allIn)
            foreach (var token in allInStack) token.gameObject.SetActive(false);
        hands[activeSeat].Rest();
        activeSeat = -1;
        ShowPile(targetCount);
    }

    private void StartBet()
    {
        action = bets.Dequeue();
        activeSeat = action.seat - 1;
        began = Time.time;
        landed = struck = false;
        FreeHand(activeSeat);
        Vector3 seat = transform.InverseTransformPoint(eyes[activeSeat].position);
        Vector3 outward = new Vector3(seat.x, 0, seat.z).normalized;
        Vector3 side = activeSeat + 1 == viewer
            ? transform.InverseTransformDirection(viewCamera.right)
            : Vector3.Cross(Vector3.up, outward);
        var start = outward * .54f + side * .15f + Vector3.up * .08f;
        var release = outward * .40f + side * .11f + Vector3.up * .11f;
        hands[activeSeat].Begin(transform.TransformPoint(start), transform.TransformPoint(release));
        int landingSlot = Mathf.Max(0, targetCount - 1);
        flightEnd = StackPosition(landingSlot);
        // On felt a chip can slide farther than on the rim of a stack.
        touchDown = Vector3.MoveTowards(flightEnd, new Vector3(release.x, flightEnd.y, release.z),
            landingSlot % 6 == 0 ? .028f : .006f);
        Vector3 forward = transform.InverseTransformDirection(eyes[activeSeat].forward);
        releaseYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        landingYaw = StackYaw(landingSlot);
        Tint(moving[activeSeat].GetComponent<Renderer>(), 0, StackColor(landingSlot));
        if (shownCount > landingSlot) ShowPile(landingSlot);
        if (action.allIn)
        {
            pushSide = side;
            pushStart = outward * .58f + Vector3.up * PotPosition.y;
            pushStop = Vector3.Lerp(pushStart, flightEnd, .28f);
            hands[activeSeat].Begin(PushPalm(0f, false), PushPalm(.65f, false));
            pushChips = Mathf.Clamp(1 + (int)Math.Log(Math.Max(1L, action.amount), 1.5), 1, allInStack.Length);
            for (int i = 0; i < allInStack.Length; i++) allInStack[i].gameObject.SetActive(i < pushChips);
        }
        else moving[activeSeat].gameObject.SetActive(true);
    }

    // The same wager chip strikes the chosen seat, loses height, then is raked into the pot.
    private void AnimateTargeted(Transform chip, float time)
    {
        int target = action.chipAimSeat - 1;
        if (!struck)
        {
            var head = heads[target];
            Vector3 face = transform.InverseTransformPoint(head.position);
            Vector3 inward = Vector3.ProjectOnPlane(-face, Vector3.up).normalized;
            hitPoint = face + inward * .082f + Vector3.up * .068f;
            if (target + 1 == viewer)
                hitPoint = transform.InverseTransformPoint(viewCamera.TransformPoint(new Vector3(.085f, -.055f, .22f)));
            dropPoint = Vector3.ProjectOnPlane(face, Vector3.up).normalized * .48f + Vector3.up * PotPosition.y;
        }
        float approach = Mathf.Clamp01((time - .26f) / .31f);
        if (approach < 1)
        {
            chip.localPosition = Vector3.Lerp(flightStart, hitPoint, approach)
                + Vector3.up * (.075f * 4f * approach * (1f - approach));
            chip.localRotation = Quaternion.Euler(42f * approach, releaseYaw + 150f * approach, 12f * approach);
            return;
        }
        struck = true;
        float rebound = Mathf.Clamp01((time - .57f) / .29f);
        float slide = Mathf.SmoothStep(0, 1, Mathf.Clamp01((time - .97f) / .31f));
        Vector3 afterHit = Vector3.Lerp(hitPoint, dropPoint, rebound);
        afterHit.y = Mathf.Lerp(hitPoint.y, dropPoint.y, rebound * rebound);
        float bounce = Mathf.Clamp01((time - .86f) / .11f);
        chip.localPosition = Vector3.Lerp(afterHit, flightEnd, slide)
            + Vector3.up * (.012f * 4f * bounce * (1f - bounce));
        chip.localRotation = Quaternion.Euler(42f * (1f - rebound) + 5f * Mathf.Sin(bounce * Mathf.PI),
            Mathf.LerpAngle(releaseYaw + 150f, landingYaw, (rebound + slide) * .5f), 0);
        if (!landed && rebound >= 1) { sound.ChipLand(); landed = true; }
        float kick = Mathf.Clamp01((time - .57f) / .32f);
        float weight = Mathf.Sin(kick * Mathf.PI) * (1f - kick);
        if (weight <= .001f) return;
        if (target + 1 == viewer)
        {
            flinching = viewCamera;
            beforeFlinch = flinching.localRotation;
            flinching.localRotation *= Quaternion.Euler(-2f * weight, 0, 2f * weight);
        }
        else
        {
            flinching = heads[target];
            beforeFlinch = flinching.localRotation;
            float sign = Vector3.Dot(eyes[target].right, eyes[activeSeat].position - eyes[target].position) < 0 ? -1f : 1f;
            flinching.localRotation *= Quaternion.Euler(-15f * weight, -6f * sign * weight, 8f * sign * weight);
        }
    }

    internal void RestoreFlinch()
    {
        if (flinching == null) return;
        flinching.localRotation = beforeFlinch;
        flinching = null;
    }

    internal Vector3 PushPalm(float time, bool cardHand)
    {
        float forward = Mathf.SmoothStep(0, 1, Mathf.Clamp01((time - .21f) / .40f));
        Vector3 center = Vector3.Lerp(pushStart, pushStop, forward);
        Vector3 outward = Vector3.ProjectOnPlane(pushStart, Vector3.up).normalized;
        return transform.TransformPoint(center + pushSide * (cardHand ? -.125f : .125f)
            + outward * .078f + Vector3.up * .021f);
    }

    private void AnimatePush(float time)
    {
        float progress = Mathf.SmoothStep(0, 1, Mathf.Clamp01((time - .21f) / .40f));
        float rake = Mathf.SmoothStep(0, 1, Mathf.Clamp01((time - .63f) / .33f));
        Vector3 center = Vector3.Lerp(pushStart, pushStop, progress);
        for (int i = 0; i < pushChips; i++)
        {
            var token = allInStack[i];
            Vector3 offset = pushSide * (i % 2 == 0 ? -.063f : .063f) + Vector3.up * (i / 2 * .0062f);
            int slot = Mathf.Clamp(targetCount - pushChips + i, 0, pile.Length - 1);
            token.localPosition = Vector3.Lerp(center + offset, StackPosition(slot), rake);
            token.localRotation = Quaternion.Euler(0, Mathf.LerpAngle(StackYaw(i), StackYaw(slot), rake), 0);
        }
        if (time < .96f)
            hands[activeSeat].Push(PushPalm(time, false), time);
        else if (allInStack[0].gameObject.activeSelf)
        {
            foreach (var token in allInStack) token.gameObject.SetActive(false);
            hands[activeSeat].Rest();
            ShowPile(targetCount);
        }
        if (!landed && progress >= 1) { sound.ChipLand(); landed = true; }
    }

    private int PileSize(long chips) => chips <= 0 ? 0 : Mathf.Clamp(1 + (int)Math.Log(chips, 1.6), 1, pile.Length);

    // A tie can show several hands for the same pot. Only a newly credited amount moves.
    internal void ShowPayout(int seat, long amount, bool animate)
    {
        int index = seat - 1;
        long increase = amount - paidBySeat[index];
        if (increase <= 0) return;
        paidBySeat[index] = amount;
        paidTotal += increase;
        int previousCount = targetCount;
        targetCount = PileSize(handPot - paidTotal);
        ShowPile(targetCount);
        if (animate) StartCoroutine(SlidePayout(index, Mathf.Clamp(previousCount - targetCount, 1, 3)));
    }

    private IEnumerator SlidePayout(int index, int count)
    {
        Vector3 outward = Vector3.ProjectOnPlane(transform.InverseTransformPoint(eyes[index].position), Vector3.up).normalized;
        Vector3 side = Vector3.Cross(Vector3.up, outward);
        Vector3 destination = outward * .56f + side * .10f;
        int first = index * 3;
        for (int i = 0; i < count; i++) allInStack[first + i].gameObject.SetActive(true);
        float start = Time.unscaledTime;
        float t;
        do
        {
            t = Mathf.Clamp01((Time.unscaledTime - start) / .58f);
            float slide = Mathf.SmoothStep(0f, 1f, t);
            for (int i = 0; i < count; i++)
            {
                var token = allInStack[first + i];
                Vector3 from = StackPosition(i);
                Vector3 to = destination + side * (i * .015f) + Vector3.up * from.y;
                token.localPosition = Vector3.Lerp(from, to, slide);
                token.localRotation = Quaternion.Euler(0, StackYaw(i) + slide * (7f + i * 4f), 0);
            }
            yield return null;
        } while (t < 1f);
        for (int i = 0; i < count; i++) allInStack[first + i].gameObject.SetActive(false);
    }

    private void ShowPile(int count)
    {
        shownCount = count;
        for (int i = 0; i < pile.Length; i++) pile[i].gameObject.SetActive(i < count);
    }

    private void ResetHands()
    {
        StopAllCoroutines();
        RestoreFlinch();
        foreach (var token in allInStack) if (token != null) token.gameObject.SetActive(false);
        bets.Clear();
        activeSeat = -1;
        for (int i = 0; i < hands.Length; i++)
        {
            hands[i]?.Release();
            hands[i] = null;
            if (moving[i] != null) moving[i].gameObject.SetActive(false);
        }
    }

    private void OnDestroy() => ResetHands();
}
