using System;
using System.Linq;
using Poker.Application;
using Poker.Transport;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// Reactions use the existing public-chat channel. The sender is the server-authenticated chat seat.
internal sealed class Play2TableReactions
{
    private const string Marker = "[몸짓]";
    private readonly Play2TablePreview table;
    private readonly GameObject[] actors;
    private readonly Transform[] eyes;
    private readonly Camera camera;
    private readonly RectTransform menu;
    private readonly TextMeshProUGUI heading, aimLabel;
    private readonly TextMeshProUGUI aimButtonLabel;
    internal int AimSeat { get; private set; }
    private readonly Play2TableChips chips;
    private readonly Transform[] heads = new Transform[4];
    private readonly Quaternion[] headRest = new Quaternion[4];
    private readonly float[] started = { -100, -100, -100, -100 };
    private enum ResultPose { None, Win, Loss }
    private readonly ResultPose[] results = new ResultPose[4];
    private readonly Animator[] animators = new Animator[4];
    private readonly int[] resultLayers = new int[4];
    private readonly bool[] usingResultClip = new bool[4];
    private readonly int[] targets = new int[4];
    private readonly Play2Gesture[] gestures = new Play2Gesture[4];
    private string lastAccusation;
    private long newest;
    private int viewer, selected;
    private HoldemRoomPacket state;
    private float lastSubmitted = -100;
    internal bool IsOpen => menu.gameObject.activeSelf;

    internal Play2TableReactions(Play2TablePreview table, GameObject[] actors, Transform[] eyes,
        Camera camera, Transform canvas, TMP_FontAsset font, Play2TableChips chips)
    {
        this.table = table;
        this.chips = chips;
        this.actors = actors;
        this.eyes = eyes;
        this.camera = camera;
        menu = Play2HudArt.Plate("Player Reactions", canvas, Vector2.zero, new Vector2(252, 156), "PlayingPanel");
        Play2UGui.Image(Play2UGui.Fill("Pointer Surface", menu), Color.clear, true);
        heading = Play2UGui.Text("Target", menu, "", new Vector2(-6, 56), new Vector2(215, 28), 18,
            Play2UGui.Cream, font, style: FontStyles.Bold);
        heading.richText = false;
        Play2HudArt.Fit(heading, 12);
        Play2HudArt.Button("Wave", menu, "손 흔들기", new Vector2(-60, 14), new Vector2(109, 36),
            "PlayingAction", Play2UGui.Cream, font, () => Send(false), 15);
        Play2HudArt.Button("Finger Gesture", menu, "손가락 욕", new Vector2(60, 14), new Vector2(109, 36),
            "PlayingAction", Play2UGui.Cream, font, () => Send(true), 15);
        var aimButton = Play2HudArt.Button("Aim Bet", menu, "칩 던질 상대", new Vector2(0, -25), new Vector2(222, 32),
            "PlayingAction", Play2UGui.Cream, font, () => { AimSeat = AimSeat == selected ? 0 : selected; Close(); }, 15);
        aimButtonLabel = aimButton.GetComponentInChildren<TextMeshProUGUI>();
        aimLabel = Play2UGui.Text("Bet Aim", canvas, "베팅 조준", Vector2.zero, new Vector2(110, 25), 15,
            Play2UGui.Cream, font, style: FontStyles.Bold);
        aimLabel.raycastTarget = false;
        aimLabel.gameObject.SetActive(false);
        Play2HudArt.Button("Close Reactions", menu, "닫기", new Vector2(0, -59), new Vector2(82, 24),
            "ActionRow", Play2UGui.Cream, font, Close, 13);
        menu.gameObject.SetActive(false);
        for (int i = 0; i < actors.Length; i++)
        {
            animators[i] = actors[i].GetComponent<Animator>();
            resultLayers[i] = animators[i].GetLayerIndex("Table Result");
            heads[i] = animators[i].GetBoneTransform(HumanBodyBones.Head);
            headRest[i] = heads[i].localRotation;
        }
    }

    internal void Present(HoldemRoomPacket packet)
    {
        if (ReferenceEquals(state, packet)) return;
        bool reset = state?.sessionId != packet?.sessionId || viewer != packet?.viewerSeat;
        bool newHand = state?.game?.handId != packet?.game?.handId;
        state = packet;
        if (reset || newHand) Release();
        if (reset) viewer = packet?.viewerSeat ?? 0;
        if (packet?.hasGame != true || packet.hasChat != true) { Close(); return; }
        if (AimSeat != 0 && !packet.members.Any(m => m.seat == AimSeat && m.connected)) AimSeat = 0;
        if (IsOpen && !packet.members.Any(m => m.seat == selected && m.connected)) Close();
        var entries = packet.chat.entries;
        if (reset) newest = entries.LastOrDefault()?.sequence ?? 0;
        foreach (var entry in entries)
        {
            if (entry.sequence <= newest) continue;
            newest = entry.sequence;
            if (!Parse(entry.text, out int target, out bool finger) || target == entry.seat
                || !packet.members.Any(m => m.seat == target && m.connected)) continue;
            int index = entry.seat - 1;
            if (index < 0 || index >= actors.Length) continue;
            if (chips.IsBetting(index)) continue;
            chips.FreeHand(index);
            targets[index] = target - 1;
            gestures[index] = finger ? Play2Gesture.Rude : Play2Gesture.Wave;
            started[index] = Time.time;
        }
        var verdict = packet.hasTurnAccusation ? packet.turnAccusation : null;
        string accusation = verdict == null ? null : $"{verdict.handId}:{verdict.street}:{verdict.accuser}:{verdict.target}";
        if (reset || newHand) lastAccusation = accusation;
        else if (accusation != null && lastAccusation != accusation)
        {
            lastAccusation = accusation;
            table.PlayAccusationSound(verdict.caught);
            Begin(verdict.accuser, verdict.target, Play2Gesture.Point, 0);
            Begin(verdict.target, verdict.accuser, verdict.caught ? Play2Gesture.Despair : Play2Gesture.ThumbsDown, .45f);
        }
    }

    private void Begin(int seat, int target, Play2Gesture gesture, float delay)
    {
        int index = seat - 1;
        if (index < 0 || index >= actors.Length || target < 1 || target > actors.Length) return;
        targets[index] = target - 1;
        gestures[index] = gesture;
        started[index] = Time.time + delay;
    }

    internal void ShowWinner(int seat, bool handOnTable, bool animate)
    {
        ShowResult(seat, ResultPose.Win, handOnTable, animate);
    }

    internal void FinishShowdown(HoldemGamePacket game, bool animate)
    {
        foreach (var seat in game.seats)
        {
            if (!Play2SettlementSummary.IsPublicHand(seat)) continue;
            ShowResult(seat.seat, seat.awarded > 0 ? ResultPose.Win : ResultPose.Loss, true, animate);
        }
    }

    private void ShowResult(int seat, ResultPose result, bool handOnTable, bool animate)
    {
        int i = seat - 1;
        if (results[i] == result) return;
        results[i] = result;
        // An unrevealed winner still needs the card arm. First person has a separate hand rig.
        if (handOnTable && seat != viewer && resultLayers[i] >= 0 && animators[i].isActiveAndEnabled)
        {
            CancelGesture(seat);
            usingResultClip[i] = true;
            var a = animators[i];
            a.Play(result == ResultPose.Win ? "Table Result.Win" : "Table Result.Loss",
                resultLayers[i], animate ? 0f : 1f);
            a.SetLayerWeight(resultLayers[i], animate ? 0f : 1f);
        }
        else if (animate)
            Begin(seat, seat, result == ResultPose.Win ? Play2Gesture.Celebrate : Play2Gesture.Despair, 0f);
    }

    internal void Animate()
    {
        bool showAim = AimSeat > 0 && state?.game?.hasResult == false && heads[AimSeat - 1] != null;
        aimLabel.gameObject.SetActive(showAim);
        if (showAim)
        {
            Vector3 point = camera.WorldToScreenPoint(heads[AimSeat - 1].position + Vector3.up * .17f);
            RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)menu.parent, point, null, out var local);
            aimLabel.rectTransform.anchoredPosition = local;
            aimLabel.enabled = point.z > 0;
        }
        for (int i = 0; i < actors.Length; i++)
        {
            if (usingResultClip[i] && animators[i].isActiveAndEnabled)
            {
                int layer = resultLayers[i];
                float blend = animators[i].GetLayerWeight(layer);
                if (blend < 1f) animators[i].SetLayerWeight(layer, Mathf.MoveTowards(blend, 1f, Time.unscaledDeltaTime / .24f));
            }
            float elapsed = Time.time - started[i];
            if (started[i] < 0)
            {
                if (results[i] == ResultPose.Win && !usingResultClip[i] && heads[i] != null)
                    heads[i].localRotation = headRest[i] * Quaternion.Euler(-8f + 1.4f * Mathf.Sin(Time.time * 2.2f), 0f, -4f);
                continue;
            }
            if (elapsed < 0) continue;
            if (chips.IsBetting(i)) { CancelGesture(i + 1); continue; }
            if (elapsed >= 1.33f)
            {
                chips.RestHand(i);
                heads[i].localRotation = headRest[i];
                started[i] = -100;
                continue;
            }
            Vector3 outward = Vector3.ProjectOnPlane(eyes[i].position - table.TableCenter, Vector3.up).normalized;
            Vector3 side = i + 1 == viewer ? camera.transform.right : Vector3.Cross(Vector3.up, outward);
            Vector3 origin = table.TableCenter + outward * .48f + side * .14f + Vector3.up * .12f;
            if (gestures[i] == Play2Gesture.Despair) chips.RestHand(i);
            else if (gestures[i] == Play2Gesture.Celebrate)
                chips.FreeHand(i).Gesture(table.TableCenter, Vector3.Lerp(origin, heads[i].position, .35f), elapsed, gestures[i]);
            else chips.FreeHand(i).Gesture(eyes[targets[i]].position, origin, elapsed, gestures[i]);
            float weight = Mathf.Min(Mathf.Clamp01(elapsed / .2f), Mathf.Clamp01((1.33f - elapsed) / .3f));
            // Modest head turn keeps the original seated rig and card-holding arm intact.
            Vector3 direction = actors[i].transform.InverseTransformDirection(eyes[targets[i]].position - heads[i].position);
            float yaw = Mathf.Clamp(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, -28, 28);
            heads[i].localRotation = headRest[i] * Quaternion.Euler(
                gestures[i] == Play2Gesture.Despair ? 17 * weight : gestures[i] == Play2Gesture.Celebrate ? -8 * weight : 0,
                gestures[i] is Play2Gesture.Despair or Play2Gesture.Celebrate ? 0 : yaw * weight, 0);
        }
    }

    internal void CancelGesture(int seat)
    {
        int index = seat - 1;
        if (index < 0 || index >= actors.Length) return;
        started[index] = -100;
        chips.RestHand(index);
        heads[index].localRotation = headRest[index];
    }

    internal bool HandlePointer()
    {
        bool wasOpen = IsOpen;
        if (IsOpen && Keyboard.current?.escapeKey.wasPressedThisFrame == true) { Close(); return true; }
        var mouse = Mouse.current;
        if (state?.hasGame != true || mouse?.leftButton.wasPressedThisFrame != true) return wasOpen;
        Vector2 point = mouse.position.ReadValue();
        if (!camera.pixelRect.Contains(point) || EventSystem.current?.IsPointerOverGameObject() == true) return wasOpen;
        Close();
        int nearest = -1;
        float distance = float.PositiveInfinity;
        for (int i = 0; i < actors.Length; i++)
        {
            if (i + 1 == viewer || !actors[i].activeInHierarchy) continue;
            var skin = actors[i].GetComponentInChildren<SkinnedMeshRenderer>();
            Vector3 center = camera.WorldToScreenPoint(skin.bounds.center + Vector3.up * skin.bounds.extents.y * .25f);
            float radius = Mathf.Abs(camera.WorldToScreenPoint(skin.bounds.center + camera.transform.right * skin.bounds.extents.x).x - center.x);
            float d = Vector2.Distance(point, center);
            if (center.z > 0 && d < Mathf.Max(28, radius) && d < distance) { nearest = i; distance = d; }
        }
        if (nearest < 0) return wasOpen;
        selected = nearest + 1;
        heading.text = Name(state, selected) + "에게";
        aimButtonLabel.text = selected == AimSeat ? "조준 해제" : "칩 던질 상대";
        RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)menu.parent, point, null, out var local);
        var area = ((RectTransform)menu.parent).rect;
        menu.anchoredPosition = new Vector2(Mathf.Clamp(local.x, area.xMin + 132, area.xMax - 132),
            Mathf.Clamp(local.y - 82, area.yMin + 80, area.yMax - 80));
        menu.gameObject.SetActive(true);
        return true;
    }

    private void Send(bool finger)
    {
        if (state?.hasGame != true || !state.members.Any(m => m.seat == selected && m.connected))
        {
            Close();
            return;
        }
        if (Time.unscaledTime - lastSubmitted < 1.5f) return;
        if (table.Submit(new HoldemWireRequest { type = "chat", text = Marker + selected + (finger ? "F" : "W") }))
        {
            lastSubmitted = Time.unscaledTime;
            Close();
        }
    }

    private void Close() { if (menu != null) menu.gameObject.SetActive(false); }
    private static string Name(HoldemRoomPacket packet, int seat) => packet.members.FirstOrDefault(m => m.seat == seat)?.name ?? seat + "번";

    internal static string Describe(string text, HoldemRoomPacket packet) => Parse(text, out int target, out bool finger)
        ? Name(packet, target) + (finger ? "에게 손가락 욕" : "에게 손 흔들기") : text;

    private static bool Parse(string text, out int target, out bool finger)
    {
        target = 0;
        finger = false;
        if (text == null || text.Length != Marker.Length + 2 || !text.StartsWith(Marker, StringComparison.Ordinal)) return false;
        target = text[Marker.Length] - '0';
        char kind = text[text.Length - 1];
        finger = kind == 'F';
        return target >= 1 && target <= 4 && (finger || kind == 'W');
    }

    internal void ClearAim() => AimSeat = 0;

    internal void Release()
    {
        ClearAim();
        Close();
        for (int i = 0; i < actors.Length; i++)
        {
            chips.RestHand(i);
            results[i] = ResultPose.None;
            if (usingResultClip[i] && animators[i] != null && animators[i].isActiveAndEnabled)
                animators[i].SetLayerWeight(resultLayers[i], 0f);
            usingResultClip[i] = false;
            if (heads[i] != null) heads[i].localRotation = headRest[i];
            started[i] = -100;
        }
    }
}
