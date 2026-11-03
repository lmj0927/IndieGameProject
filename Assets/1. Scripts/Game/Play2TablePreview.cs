using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MoreMountains.Feedbacks;
using MoreMountains.Tools;
using Poker.Application;
using Poker.Foundation;
using Poker.Online;
using Poker.Transport;
using UnityEngine;

public sealed class Play2TablePreview : MonoBehaviour
{
    [SerializeField] private Camera seatCamera;
    [SerializeField] private Transform tabletop;
    [SerializeField] private GameObject[] players;
    [SerializeField] private Transform[] eyes;
    [SerializeField] private CardVisual cardPrefab;
    [SerializeField] private Play2FrontEnd frontEnd;
    [SerializeField] private Poker.Llm.Unity.LlmUnityMentAnalyzer hostAnalyzer;

    private readonly List<CardVisual> cards = new List<CardVisual>(13);
    private readonly HashSet<int> dealtSeats = new HashSet<int>();
    private readonly OpponentGrip[] opponentGrips = new OpponentGrip[4];
    private readonly Guid[] previewSeats = new Guid[4];
    private FusionClient online;
    private HoldemHostCommandBridge preview;
    private CryptographicRandomSource previewRandom;
    private HoldemLlmTableDealer previewDealer;
    private Poker.Llm.Unity.LlmUnityMentAnalyzer roomAnalyzer;
    private HoldemRoomPacket state;
    private string status = "";
    private string handId;
    private int viewer, board;
    private bool connecting;
    private float animationUntil;
    private float nextPreviewAction;

    private sealed class OpponentGrip
    {
        public Transform UpperArm, Forearm, Hand, Cards;
        public Vector3 Target;
        public Vector3 CardContactOffset;
        public Quaternion Facing;
        public float Started;

        public void UpdatePose()
        {
            // Reach toward the deal, then draw the cards back toward the chest.
            Vector3 shoulder = UpperArm.position;
            Vector3 elbow = Forearm.position;
            Vector3 wrist = Hand.position;
            Quaternion wristRotation = Hand.rotation;
            float upper = Vector3.Distance(shoulder, elbow);
            float lower = Vector3.Distance(elbow, wrist);
            float elapsed = Time.time - Started;
            Vector3 catchPoint = Target + Facing * Vector3.forward * 0.075f + Vector3.up * 0.018f;
            float reachWeight = Mathf.SmoothStep(0f, 1f, elapsed / 0.36f);
            float settleWeight = Mathf.SmoothStep(0f, 1f, (elapsed - 0.36f) / 0.28f);
            Vector3 handTarget = Vector3.Lerp(catchPoint, Target, settleWeight);
            Vector3 offset = Vector3.Lerp(wrist, handTarget, reachWeight) - shoulder;
            Vector3 axis = offset.normalized;
            float reach = Mathf.Clamp(offset.magnitude, Mathf.Abs(upper - lower) + 0.001f, upper + lower - 0.001f);
            Vector3 bend = Vector3.ProjectOnPlane(Vector3.down, axis).normalized;
            float along = (upper * upper - lower * lower + reach * reach) / (2f * reach);
            Vector3 nextElbow = shoulder + axis * along + bend * Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));
            UpperArm.rotation = Quaternion.FromToRotation(elbow - shoulder, nextElbow - shoulder) * UpperArm.rotation;
            Forearm.rotation = Quaternion.FromToRotation(Hand.position - Forearm.position,
                shoulder + axis * reach - Forearm.position) * Forearm.rotation;
            // Supinate at the wrist without changing the receiving arm pose.
            float inspect = Mathf.SmoothStep(0f, 1f, (elapsed - 0.2f) / 0.48f);
            float gripRoll = 180f * inspect;
            Quaternion roll = Quaternion.AngleAxis(gripRoll, Hand.position - Forearm.position);
            Hand.rotation = roll * wristRotation;
            Cards.rotation = Facing;
            Cards.position = Hand.position + Cards.TransformDirection(CardContactOffset);
        }
    }

    private void LateUpdate()
    {
        foreach (var grip in opponentGrips)
        {
            if (grip == null) continue;
            bool visible = grip.Hand.gameObject.activeInHierarchy;
            grip.Cards.gameObject.SetActive(visible);
            if (visible) grip.UpdatePose();
        }
    }

    private void Awake() => SetState(null);

    private void Start() => frontEnd.Initialize(this, seatCamera, players);

    internal bool IsLocal => preview != null;
    internal bool IsHost => IsLocal || (online != null && online.IsHost);
    internal string RoomCode => online?.RoomName ?? "";
    internal bool IsDealing => Time.time < animationUntil;

    internal void ToggleReady()
    {
        var mine = state?.members.FirstOrDefault(member => member.seat == viewer);
        if (online != null && mine != null)
            online.Send(new HoldemWireRequest { type = "ready", ready = !mine.ready });
    }

    private void Update()
    {
        if (online != null)
        {
            online.Poll();
            while (online.TryReadResponse(out var response))
                ReadReceipt(response);
            if (online.IsClosed)
            {
                status = online.ConnectionError ?? online.AdmissionError
                    ?? (string.IsNullOrEmpty(status) ? "Disconnected" : status);
                Destroy(online);
                online = null;
                DestroyRoomAnalyzer();
                SetState(null);
            }
            else SetState(online.Latest);
        }
        else if (preview != null)
        {
            previewDealer.Tick();
            if (preview.TryCreateProjection(previewSeats[0], out var packet, state?.revision ?? -1))
                SetState(packet);
            PlayPreviewOpponent();
        }
        frontEnd.Present(state, connecting || (online != null && state == null), status);
    }

    private void PlayPreviewOpponent()
    {
        var game = state?.game;
        if (game == null || game.currentSeat <= 1 || game.dealPending || game.revealPending
            || game.hasResult || IsDealing || Time.time < nextPreviewAction) return;
        Guid actor = previewSeats[game.currentSeat - 1];
        if (!preview.TryCreateProjection(actor, out var packet) || !packet.game.hasLegal) return;
        var legal = packet.game.legal;
        if (legal.check || legal.call)
        {
            var request = PreviewRequest("act");
            request.handId = game.handId;
            request.version = game.version;
            request.action = (int)(legal.check ? BettingActionKind.Check : BettingActionKind.Call);
            preview.Dispatch(actor, request);
            nextPreviewAction = Time.time + 0.65f;
        }
    }

    internal bool Submit(HoldemWireRequest request)
    {
        if (state == null) return false;
        status = "";
        if (preview == null) return online?.Send(request) != null;
        request.protocol = HoldemRoomPacketMapper.ProtocolVersion;
        request.id = Guid.NewGuid().ToString("N");
        request.sessionId = state.sessionId;
        var reply = preview.Dispatch(previewSeats[0], request).Response;
        ReadReceipt(reply);
        return reply.accepted;
    }

    private void ReadReceipt(HoldemWireResponse response)
    {
        if (response.type == "chat-receipt")
        {
            status = !response.accepted ? "채팅 전송 실패 · " + response.chatError
                : !response.hasUtteranceReceipt ? ""
                : response.utteranceError == "None" ? "딜러에게 요청을 전달했습니다."
                : "채팅 전송 완료 · 딜러 요청 거절 (" + response.utteranceError + ")";
        }
        else if (response.type == "receipt" && !response.accepted)
            status = "행동 거절 · " + response.error;
    }

    private Poker.Llm.Unity.LlmUnityMentAnalyzer CreateRoomAnalyzer()
    {
        if (hostAnalyzer == null) return null;
        string model = hostAnalyzer.GetComponent<LLMUnity.LLM>().model;
        if (string.IsNullOrEmpty(model) || !System.IO.File.Exists(LLMUnity.LLM.GetLLMManagerAssetRuntime(model)))
            return null;
        roomAnalyzer = Instantiate(hostAnalyzer, transform);
        return roomAnalyzer;
    }

    private void DestroyRoomAnalyzer()
    {
        if (roomAnalyzer != null) Destroy(roomAnalyzer.gameObject);
        roomAnalyzer = null;
    }

    internal async void Connect(bool host, string room, string nickname, int capacity)
    {
        if (online != null || preview != null || connecting) return;
        connecting = true;
        status = "";
        var client = gameObject.AddComponent<FusionClient>();
        online = client;
        try
        {
            await client.Connect(host, room.Trim().ToUpperInvariant(), new HoldemClientIdentity(nickname.Trim()),
                capacity: capacity, llmDealer: host, hostAnalyzer: host ? CreateRoomAnalyzer() : null);
        }
        catch (ArgumentException error)
        {
            status = error.Message;
            if (client != null) client.Dispose();
        }
        finally { connecting = false; }
    }

    internal void StartPreview()
    {
        if (preview != null || online != null || connecting) return;
        status = "";
        previewRandom = new CryptographicRandomSource();
        var host = new HoldemClientIdentity("You");
        preview = new HoldemHostCommandBridge(host, HoldemLlmTableDealer.CreateConfig(),
            new SeatId(1), previewRandom, utterancePolicy: HoldemLlmTableDealer.CreateUtterancePolicy(), chatEnabled: true);
        var analyzer = CreateRoomAnalyzer();
        if (analyzer != null) analyzer.gameObject.SetActive(true);
        previewDealer = new HoldemLlmTableDealer(preview, analyzer);
        for (int i = 0; i < previewSeats.Length; i++)
        {
            var identity = i == 0 ? host : new HoldemClientIdentity("Preview " + (i + 1));
            previewSeats[i] = Guid.NewGuid();
            preview.Dispatch(previewSeats[i], identity.AdmissionRequest());
            preview.Dispatch(previewSeats[i], PreviewRequest("ready"));
        }
    }

    private HoldemWireRequest PreviewRequest(string type) => new HoldemWireRequest
    {
        protocol = HoldemRoomPacketMapper.ProtocolVersion, type = type, ready = true,
        id = Guid.NewGuid().ToString("N"), sessionId = preview.SessionId.ToString("N")
    };

    internal void StartHand()
    {
        if (!IsHost) return;
        Submit(new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"),
            version = state?.hasGame == true ? state.game.version : 0 });
    }

    private void SetState(HoldemRoomPacket packet)
    {
        if (ReferenceEquals(state, packet) && viewer != 0) return;
        state = packet;
        int viewerSeat = state?.viewerSeat ?? 1;
        if (viewerSeat != viewer)
        {
            viewer = viewerSeat;
            var eye = eyes[viewer - 1];
            seatCamera.transform.SetPositionAndRotation(eye.position, eye.rotation);
        }
        for (int i = 0; i < players.Length; i++)
            players[i].SetActive(i + 1 != viewer && (state == null || state.members.Any(m => m.seat == i + 1 && m.connected)));

        var game = state?.hasGame == true ? state.game : null;
        if (handId != game?.handId)
        {
            ClearCards();
            handId = game?.handId;
        }
        if (game == null) return;
        var seatsInHand = game.seats.Where(seat => seat.dealtIn).ToArray();
        for (int i = 0; i < seatsInHand.Length; i++)
        {
            var seat = seatsInHand[i];
            bool own = seat.seat == viewer;
            if (own && seat.visibleCards.Length < 2) continue;
            if (!dealtSeats.Add(seat.seat)) continue;
            for (int index = 0; index < 2; index++)
            {
                float delay = (i + index * seatsInHand.Length) * 0.10f;
                if (own)
                    StartCoroutine(Deal(seat.visibleCards[index], index, true, delay));
                else
                    StartCoroutine(DealOpponentBack(seat.seat, index, delay));
            }
        }
        int first = board;
        while (board < game.board.Length)
        {
            StartCoroutine(Deal(game.board[board], board, false, (board - first) * 0.11f));
            board++;
        }
    }

    private IEnumerator Deal(int cardId, int index, bool hand, float delay)
    {
        animationUntil = Mathf.Max(animationUntil, Time.time + delay + (hand ? 0.5f : 0.95f));
        if (delay > 0f) yield return new WaitForSeconds(delay);
        var cameraTransform = seatCamera.transform;
        var card = Instantiate(cardPrefab, hand ? cameraTransform : tabletop);
        cards.Add(card);
        var value = Poker.Foundation.Card.FromId(cardId);
        Suit suit = value.Suit switch
        {
            Poker.Foundation.Suit.Clubs => Suit.Club,
            Poker.Foundation.Suit.Diamonds => Suit.Diamond,
            Poker.Foundation.Suit.Hearts => Suit.Heart,
            _ => Suit.Spade
        };
        card.SetCard(new Card(value.Rank == Rank.Ace ? 1 : (int)value.Rank, suit));
        var pose = card.transform;
        float yaw = cameraTransform.eulerAngles.y;
        Quaternion boardFacing = Quaternion.Euler(0f, yaw, 0f);
        Vector3 end = hand
            ? new Vector3(-0.145f + index * 0.082f, -0.167f, 0.41f + index * 0.012f)
            : boardFacing * new Vector3(-0.35f + index * 0.175f, 0.012f, -0.055f);
        Vector3 start = hand ? end + new Vector3(0.16f, -0.23f, 0.3f) : boardFacing * new Vector3(0f, 0.15f, 0.43f);
        pose.localScale = Vector3.one * (hand ? 1.05f : 1.65f);
        pose.localPosition = start;
        pose.localRotation = hand ? Quaternion.AngleAxis(index == 0 ? 10f : -8f, Vector3.forward) * Quaternion.Euler(-90f, 0f, 0f)
            : boardFacing * Quaternion.Euler(0f, 0f, 179.9f);
        if (hand && index == 0) AddPreviewGrip(pose);

        var motion = AddDealMotion(card, start, end);
        if (!hand)
        {
            var flip = new MMF_Rotation
            {
                AnimateRotationTarget = pose, Mode = MMF_Rotation.Modes.ToDestination,
                ToDestinationSpace = Space.Self, DestinationAngles = boardFacing.eulerAngles,
                AnimateRotationDuration = 0.27f,
                ToDestinationTween = new MMTweenType(AnimationCurve.EaseInOut(0, 0, 1, 1)),
                Timing = new MMFeedbackTiming { InitialDelay = 0.54f }
            };
            motion.AddFeedback(flip);
        }
        motion.Initialization();
        motion.PlayFeedbacks();
    }

    private IEnumerator DealOpponentBack(int seat, int index, float delay)
    {
        animationUntil = Mathf.Max(animationUntil, Time.time + delay + 0.5f);
        if (delay > 0f) yield return new WaitForSeconds(delay);

        var grip = opponentGrips[seat - 1] ?? CreateOpponentGrip(seat);
        Vector3 start = grip.Cards.InverseTransformPoint(tabletop.position + Vector3.up * 0.18f);
        Quaternion cardAngle = Quaternion.Euler(-106f, 0f, 0f)
            * Quaternion.AngleAxis(index == 0 ? -17f : 17f, Vector3.up);
        // Both cards fan out from the same lower edge held between the fingers.
        Vector3 end = cardAngle * new Vector3(0f, -index * 0.004f, 0.062f);

        var card = Instantiate(cardPrefab, grip.Cards);
        cards.Add(card);
        card.transform.Find("Front").gameObject.SetActive(false);
        card.transform.localScale = Vector3.one * 1.15f;
        card.transform.localPosition = start;
        card.transform.localRotation = cardAngle;
        var motion = AddDealMotion(card, start, end);
        motion.Initialization();
        motion.PlayFeedbacks();
    }

    private OpponentGrip CreateOpponentGrip(int seat)
    {
        Vector3 outward = Vector3.ProjectOnPlane(eyes[seat - 1].position - tabletop.position, Vector3.up).normalized;
        var animator = players[seat - 1].GetComponent<Animator>();
        var pivot = new GameObject("Seat " + seat + " Held Cards").transform;
        pivot.SetParent(tabletop, false);
        pivot.rotation = Quaternion.LookRotation(-outward);
        var grip = new OpponentGrip
        {
            UpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm),
            Forearm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm),
            Hand = animator.GetBoneTransform(HumanBodyBones.RightHand),
            Cards = pivot,
            // The owl's longer fingers need extra room when the palm turns up.
            CardContactOffset = new Vector3(0.018f, 0.025f, seat == 4 ? 0.132f : 0.108f),
            Facing = pivot.rotation,
            Target = tabletop.position + outward * 0.65f + pivot.right * 0.13f + Vector3.up * 0.035f,
            Started = Time.time
        };
        grip.UpdatePose();
        return opponentGrips[seat - 1] = grip;
    }

    private static MMF_Player AddDealMotion(CardVisual card, Vector3 start, Vector3 end)
    {
        var motion = card.gameObject.AddComponent<MMF_Player>();
        motion.AddFeedback(new MMF_Position
        {
            AnimatePositionTarget = card.gameObject, Space = MMF_Position.Spaces.Local,
            Mode = MMF_Position.Modes.AtoB, RelativePosition = false, InitialPosition = start,
            DestinationPosition = end, AnimatePositionDuration = 0.36f,
            AnimatePositionTween = new MMTweenType(AnimationCurve.EaseInOut(0, 0, 1, 1))
        });
        return motion;
    }

    // A small first-person grip follows the dealt card while the full-body avatar is hidden locally.
    private void AddPreviewGrip(Transform card)
    {
        Color fur = viewer switch
        {
            2 => new Color(0.36f, 0.37f, 0.39f),
            3 => new Color(0.64f, 0.37f, 0.22f),
            4 => new Color(0.72f, 0.56f, 0.28f),
            _ => new Color(0.77f, 0.36f, 0.13f)
        };
        AddGripPart(card, "Sleeve", new Vector3(-0.036f, -0.007f, -0.127f),
            new Vector3(0.045f, 0.038f, 0.16f), new Color(0.15f, 0.25f, 0.28f));
        AddGripPart(card, "Palm", new Vector3(-0.029f, -0.009f, -0.059f),
            new Vector3(0.04f, 0.027f, 0.05f), fur);
        AddGripPart(card, "Thumb", new Vector3(-0.033f, 0.007f, -0.047f),
            new Vector3(0.017f, 0.016f, 0.037f), fur);
    }

    private static void AddGripPart(Transform card, string label, Vector3 position, Vector3 scale, Color color)
    {
        var part = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        part.name = label;
        Destroy(part.GetComponent<Collider>());
        part.transform.SetParent(card, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        var properties = new MaterialPropertyBlock();
        properties.SetColor("_BaseColor", color);
        properties.SetColor("_Color", color);
        part.GetComponent<Renderer>().SetPropertyBlock(properties);
    }

    private void ClearCards()
    {
        StopAllCoroutines();
        foreach (var card in cards) Destroy(card.gameObject);
        cards.Clear();
        for (int i = 0; i < opponentGrips.Length; i++)
        {
            if (opponentGrips[i] != null) Destroy(opponentGrips[i].Cards.gameObject);
            opponentGrips[i] = null;
        }
        dealtSeats.Clear();
        board = 0;
        animationUntil = 0f;
    }

    internal void Leave()
    {
        if (online != null) { online.Dispose(); Destroy(online); online = null; }
        previewDealer?.Dispose();
        previewDealer = null;
        DestroyRoomAnalyzer();
        preview?.Dispose();
        preview = null;
        previewRandom?.Dispose();
        previewRandom = null;
        status = "";
        SetState(null);
    }

    private void OnDestroy()
    {
        previewDealer?.Dispose();
        preview?.Dispose();
        previewRandom?.Dispose();
    }
}
