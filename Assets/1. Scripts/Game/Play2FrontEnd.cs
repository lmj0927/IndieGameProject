using System.Collections.Generic;
using System.Linq;
using MoreMountains.Tools;
using Poker.Application;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public sealed class Play2FrontEnd : MonoBehaviour
{
    [SerializeField] private Play2ChatCanvas chatCanvas;
    private enum Form { Closed, Join, Host, Quit }

    private Play2TablePreview table;
    private Play2TableHud hud;
    private VisualElement title, form, lobby, fields, curtain;
    private TextField nickname, room;
    private DropdownField capacity;
    private Label formTitle, formDetail, roomLabel, lobbyDetail;
    private Button confirm, cancel, ready, start, copy;
    private readonly Label[] seatNames = new Label[4], seatStates = new Label[4];
    private readonly GameObject[] actors = new GameObject[4];
    private readonly bool[] wasReady = new bool[4];
    private readonly List<Material> stageMaterials = new List<Material>();
    private GameObject stage;
    private Camera tableCamera;
    private HoldemRoomPacket lastPacket;
    private Form openForm;
    private string lastStatus;
    private bool wasConnecting;

    public void Initialize(Play2TablePreview owner, Camera camera, GameObject[] models)
    {
        table = owner;
        tableCamera = camera;
        var root = GetComponent<UIDocument>().rootVisualElement;
        hud = new Play2TableHud(root.Q("table-hud"), table);
        chatCanvas.Initialize(table);
        title = root.Q("title");
        form = root.Q("form");
        lobby = root.Q("lobby");
        curtain = root.Q("curtain");
        fields = root.Q("room-fields");
        nickname = root.Q<TextField>("nickname");
        nickname.value = PlayerPrefs.GetString("Play2.Nickname", "");
        room = root.Q<TextField>("room-code");
        capacity = root.Q<DropdownField>("capacity");
        capacity.choices = new List<string> { "2명", "3명", "4명" };
        capacity.index = 1;
        formTitle = root.Q<Label>("form-title");
        formDetail = root.Q<Label>("form-detail");
        roomLabel = root.Q<Label>("room-label");
        lobbyDetail = root.Q<Label>("lobby-detail");
        confirm = root.Q<Button>("confirm");
        cancel = root.Q<Button>("cancel");
        ready = root.Q<Button>("ready");
        start = root.Q<Button>("start");
        copy = root.Q<Button>("copy");
        root.Q<Button>("join").clicked += () => OpenForm(Form.Join);
        root.Q<Button>("host").clicked += () => OpenForm(Form.Host);
        root.Q<Button>("local").clicked += table.StartPreview;
        root.Q<Button>("quit").clicked += () => OpenForm(Form.Quit);
        root.Q<Button>("leave").clicked += table.Leave;
        confirm.clicked += Confirm;
        cancel.clicked += () => OpenForm(Form.Closed);
        start.clicked += table.StartHand;
        ready.clicked += table.ToggleReady;
        copy.clicked += () =>
        {
            GUIUtility.systemCopyBuffer = table.RoomCode;
            lobbyDetail.text = "방 코드를 복사했습니다.";
            Animate(lobbyDetail, 0.35f, 6f, 0f);
        };

        var seats = root.Q("seats");
        for (int i = 0; i < seatNames.Length; i++)
        {
            var seat = new VisualElement();
            seat.AddToClassList("seat");
            seatNames[i] = new Label();
            seatNames[i].AddToClassList("seat-name");
            seatStates[i] = new Label();
            seatStates[i].AddToClassList("seat-state");
            seat.Add(seatNames[i]);
            seat.Add(seatStates[i]);
            seats.Add(seat);
        }

        BuildStage(models);
        ShowTitle();
    }

    private void ShowTitle()
    {
        Show(title, true);
        Show(lobby, false);
        SetStage(false);
        OpenForm(Form.Closed);
        int index = 0;
        title.Query<Button>(className: "card").ForEach(card =>
            Animate(card, 0f, 32f, index++ * 0.075f));
    }

    private void OpenForm(Form next)
    {
        openForm = next;
        Show(form, next != Form.Closed);
        if (next == Form.Closed) return;
        Show(fields, next != Form.Quit);
        Show(capacity, next == Form.Host);
        formTitle.text = next == Form.Host ? "방 만들기" : next == Form.Join ? "방 참가하기" : "게임을 종료할까요?";
        confirm.text = next == Form.Host ? "방 열기" : next == Form.Join ? "참가하기" : "종료";
        formDetail.text = next == Form.Host ? "만든 방 코드를 친구에게 알려주세요." : "";
        room.value = next == Form.Host ? "OMC-" + System.Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant() : "";
        Animate(form, 0f, 12f, 0f);
        if (next != Form.Quit) nickname.Focus();
    }

    private void Confirm()
    {
        if (openForm == Form.Quit) { Application.Quit(); return; }
        string name = nickname.value.Trim();
        string code = room.value.Trim().ToUpperInvariant();
        if (name.Length == 0) { formDetail.text = "닉네임을 입력해 주세요."; return; }
        if (code.Length < 6 || code.Any(c => !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-'))
        { formDetail.text = "방 코드는 영문, 숫자, 하이픈으로 6자 이상 입력해 주세요."; return; }
        PlayerPrefs.SetString("Play2.Nickname", name);
        table.Connect(openForm == Form.Host, code, name, capacity.index + 2);
    }

    public void Present(HoldemRoomPacket packet, bool connecting, string status)
    {
        hud.Present(packet, status);
        chatCanvas.Present(packet);
        if (ReferenceEquals(packet, lastPacket) && connecting == wasConnecting && status == lastStatus) return;
        var previous = lastPacket;
        lastPacket = packet;
        wasConnecting = connecting;
        lastStatus = status;
        if (packet == null)
        {
            if (previous != null) ShowTitle();
            confirm.SetEnabled(!connecting);
            cancel.SetEnabled(!connecting);
            if (openForm != Form.Closed)
            {
                if (connecting) formDetail.text = "방에 연결하는 중...";
                else if (!string.IsNullOrEmpty(status)) formDetail.text = "연결 실패 · " + status;
            }
            return;
        }

        Show(title, false);
        Show(form, false);
        Show(lobby, !packet.hasGame);
        SetStage(!packet.hasGame);
        if (packet.hasGame)
        {
            if (previous?.hasGame != true)
            {
                Show(curtain, true);
                Animate(curtain, 1f, 0f, 0f, fadeOut: true);
            }
            return;
        }
        if (previous == null) Animate(lobby, 0f, 14f, 0f);

        int count = packet.rules.seatCapacity;
        roomLabel.text = table.IsLocal ? "로컬 프리뷰" : table.RoomCode + "  ·  " + packet.members.Length + "/" + count;
        Show(copy, !table.IsLocal);
        bool allReady = packet.members.Length == count && packet.members.All(member => member.connected && member.ready);
        var mine = packet.members.FirstOrDefault(member => member.seat == packet.viewerSeat);
        Show(ready, !table.IsLocal);
        ready.text = mine?.ready == true ? "준비 취소" : "준비";
        Show(start, table.IsHost);
        start.SetEnabled(allReady);
        lobbyDetail.text = !string.IsNullOrEmpty(status) ? status
            : table.IsLocal ? "네 좌석의 카드 배분을 연습합니다."
            : allReady ? "모두 준비되었습니다."
            : packet.members.Length < count ? "플레이어가 들어오기를 기다리는 중"
            : "모든 플레이어가 준비하면 방장이 시작할 수 있습니다.";

        for (int i = 0; i < actors.Length; i++)
        {
            var member = packet.members.FirstOrDefault(m => m.seat == i + 1);
            var plate = seatNames[i].parent;
            bool occupied = member?.connected == true;
            bool isReady = occupied && member.ready;
            bool entering = occupied && !actors[i].activeSelf;
            actors[i].SetActive(occupied);
            if (entering)
            {
                Vector3 end = actors[i].transform.localPosition;
                StartCoroutine(RevealActor(actors[i].transform, end));
            }
            seatNames[i].text = member == null ? "빈자리" : member.name + (member.host ? "  ·  방장" : "");
            seatStates[i].text = i >= count ? "닫힌 자리" : member == null ? "입장 대기"
                : !occupied ? "연결 끊김" : isReady ? "✓ 준비 완료" : "준비 중";
            plate.EnableInClassList("mine", i + 1 == packet.viewerSeat);
            seatStates[i].EnableInClassList("ready", isReady);
            if (isReady != wasReady[i]) Animate(plate, 0.45f, 7f, 0f);
            wasReady[i] = isReady;
        }
    }

    private System.Collections.IEnumerator RevealActor(Transform actor, Vector3 end)
    {
        float started = Time.unscaledTime;
        Vector3 from = end + Vector3.back * 0.18f + Vector3.down * 0.16f;
        while (Time.unscaledTime - started < 0.35f)
        {
            actor.localPosition = MMTween.Tween(Time.unscaledTime - started, 0f, 0.35f, from, end, MMTween.MMTweenCurve.EaseOutCubic);
            yield return null;
        }
        actor.localPosition = end;
    }

    internal static void Animate(VisualElement element, float initialOpacity, float offset, float delay, bool fadeOut = false)
    {
        float started = Time.unscaledTime + delay;
        element.style.opacity = initialOpacity;
        element.style.translate = new Translate(0, offset);
        IVisualElementScheduledItem tick = null;
        tick = element.schedule.Execute(() =>
        {
            float elapsed = Mathf.Clamp(Time.unscaledTime - started, 0f, 0.36f);
            float progress = MMTween.Tween(elapsed, 0f, 0.36f, 0f, 1f, MMTween.MMTweenCurve.EaseOutCubic);
            element.style.opacity = Mathf.Lerp(initialOpacity, fadeOut ? 0f : 1f, progress);
            element.style.translate = new Translate(0, offset * (1f - progress));
            if (elapsed >= 0.36f)
            {
                tick.Pause();
                if (fadeOut) Show(element, false);
            }
        }).Every(16);
    }

    private static void Show(VisualElement element, bool visible) => element.EnableInClassList("hidden", !visible);

    private void SetStage(bool visible)
    {
        stage.SetActive(visible);
        tableCamera.enabled = !visible;
    }

    private void BuildStage(GameObject[] models)
    {
        stage = new GameObject("Lobby Stage");
        stage.transform.SetParent(transform, false);
        stage.transform.position = new Vector3(0f, 1000f, 0f);
        var cameraObject = new GameObject("Lobby Camera");
        cameraObject.transform.SetParent(stage.transform, false);
        cameraObject.transform.localPosition = new Vector3(0f, 1.62f, -6f);
        cameraObject.transform.localRotation = Quaternion.Euler(8f, 0f, 0f);
        var lobbyCamera = cameraObject.AddComponent<Camera>();
        lobbyCamera.orthographic = true;
        lobbyCamera.orthographicSize = 1.45f;
        lobbyCamera.clearFlags = CameraClearFlags.SolidColor;
        lobbyCamera.backgroundColor = new Color(0.045f, 0.032f, 0.026f);
        lobbyCamera.farClipPlane = 14f;
        var wood = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        wood.color = new Color(0.28f, 0.14f, 0.079f);
        wood.SetFloat("_Smoothness", 0.06f);
        wood.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        stageMaterials.Add(wood);
        var darkerWood = new Material(wood) { color = new Color(0.21f, 0.10f, 0.055f) };
        stageMaterials.Add(darkerWood);
        for (int i = 0; i < 24; i++)
            Block("Stage plank", new Vector3((i - 11.5f) * 0.38f, -0.065f, 0.5f),
                new Vector3(0.375f, 0.13f, 13f), i % 3 == 0 ? darkerWood : wood);
        for (int i = 0; i < actors.Length; i++)
        {
            float x = (i - 1.5f) * 1.22f;
            Block("Seat riser", new Vector3(x, 0.19f, 0.29f), new Vector3(0.58f, 0.38f, 0.48f), darkerWood);
            actors[i] = Instantiate(models[i], stage.transform);
            actors[i].transform.localPosition = new Vector3(x, 0.19f, 0f);
            actors[i].transform.localRotation = Quaternion.Euler(0f, 180f + (i - 1.5f) * 8f, 0f);
            actors[i].SetActive(false);
        }
        StageLight(new Vector3(-2f, 3f, -3f), new Color(1f, 0.72f, 0.46f), 7f);
        StageLight(new Vector3(2f, 2f, 1f), new Color(0.96f, 0.57f, 0.26f), 4f);
        stage.SetActive(false);
    }

    private void Block(string label, Vector3 position, Vector3 size, Material material)
    {
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = label;
        Destroy(block.GetComponent<Collider>());
        block.transform.SetParent(stage.transform, false);
        block.transform.localPosition = position;
        block.transform.localScale = size;
        block.GetComponent<Renderer>().sharedMaterial = material;
    }

    private void StageLight(Vector3 position, Color color, float intensity)
    {
        var light = new GameObject("Stage light").AddComponent<Light>();
        light.transform.SetParent(stage.transform, false);
        light.transform.localPosition = position;
        light.type = LightType.Point;
        light.color = color;
        light.intensity = intensity;
        light.range = 9f;
    }

    private void OnDestroy()
    {
        foreach (var material in stageMaterials) Destroy(material);
    }
}
