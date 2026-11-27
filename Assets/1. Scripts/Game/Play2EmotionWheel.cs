using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

internal sealed class Play2EmotionWheel : IDisposable
{
    private static readonly string[] Labels = { "중지", "가리키기", "손짓", "질문", "엄지척", "불신", "승리" };
    internal const string MiddleFingerMotion = "SittingMiddleFingerLeft";
    internal static readonly string[] Motions = { MiddleFingerMotion, "SittingPointLeft", "SittingGestureLeft",
        "SittingAskQuestionLeft", "SittingThumbsUp", "SittingDisbelief", "SittingVictory" };
    private const float DeadZone = 44f;
    private readonly RectTransform overlay, pointer;
    private readonly Canvas canvas;
    private readonly Play2EmotionWheelGraphic wheel;
    private readonly TextMeshProUGUI[] labels = new TextMeshProUGUI[Labels.Length];
    private readonly TextMeshProUGUI centerLabel, centerHint;
    private readonly InputAction middleButton = new InputAction(type: InputActionType.Button, binding: "<Mouse>/middleButton");
    private readonly Action<int> confirm;
    private Mouse mouse;
    private bool canOpen, cursorWasVisible, releasePending;
    private bool village;
    private Vector2 direction, lastMousePosition;
    private int selected;
    internal bool IsOpen => mouse != null;

    internal Play2EmotionWheel(Transform parent, Canvas canvas, TMP_FontAsset font, Action<int> confirm)
    {
        this.canvas = canvas;
        this.confirm = confirm;
        overlay = Play2UGui.Fill("Emotion Selection", parent);
        Play2UGui.Image(overlay, Play2UGui.Hex("07120F49"), canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null);
        float size = (Play2EmotionWheelGraphic.Radius + 4f) * 2f;
        var rim = Play2UGui.Rect("Wheel", overlay, Vector2.zero, new Vector2(size, size));
        wheel = rim.gameObject.AddComponent<Play2EmotionWheelGraphic>();
        wheel.raycastTarget = false;
        for (int i = 0; i < labels.Length; i++)
        {
            var position = Play2EmotionWheelGraphic.Direction(i * 360f / labels.Length) * Play2EmotionWheelGraphic.LabelRadius;
            labels[i] = Play2UGui.Text("Emotion " + i, rim, Labels[i], position, new Vector2(112f, 32f), 21f,
                Play2UGui.Cream, font, style: FontStyles.Bold);
        }
        centerLabel = Play2UGui.Text("Selection", rim, "", new Vector2(0f, 17f), new Vector2(202f, 42f), 27f,
            Play2UGui.Cream, font, style: FontStyles.Bold);
        centerHint = Play2UGui.Text("Confirm", rim, "", new Vector2(0f, -20f), new Vector2(205f, 28f), 15f, Play2UGui.Gold, font);
        pointer = Play2UGui.Diamond("Mouse Direction", rim, Vector2.zero, Play2UGui.Cream);
        overlay.gameObject.SetActive(false);
        middleButton.started += context =>
        {
            if (context.control.device is Mouse source && canOpen && !IsOpen)
                Open(source);
        };
        middleButton.canceled += _ => releasePending = IsOpen;
        middleButton.Enable();
    }

    internal bool Tick(bool allowed)
    {
        canOpen = allowed && Application.isFocused;
        if (!IsOpen) return false;
        if (!canOpen || !mouse.added || Keyboard.current?.escapeKey.wasPressedThisFrame == true)
        {
            Cancel();
            return true;
        }
        Vector2 position = mouse.position.ReadValue();
        Vector2 movement = mouse.delta.ReadValue();
        if (movement == Vector2.zero) movement = position - lastMousePosition;
        lastMousePosition = position;
        UpdateSelection(movement / canvas.scaleFactor);
        if (releasePending)
        {
            int choice = selected;
            Cancel();
            if (choice >= 0) confirm(choice);
        }
        else if (!mouse.middleButton.isPressed) Cancel();
        return true;
    }

    private void Open(Mouse source)
    {
        mouse = source;
        lastMousePosition = source.position.ReadValue();
        cursorWasVisible = Cursor.visible;
        Cursor.visible = false;
        direction = Vector2.zero;
        village = !Play2TableAtmosphere.DarkRoom;
        pointer.GetComponent<UnityEngine.UI.Image>().color = village ? Play2HudArt.MarketBrown : Play2UGui.Cream;
        overlay.SetAsLastSibling();
        overlay.gameObject.SetActive(true);
        UpdateSelection(Vector2.zero);
    }

    private void UpdateSelection(Vector2 movement)
    {
        direction = Vector2.ClampMagnitude(direction + movement, Play2EmotionWheelGraphic.Radius - 14f);
        bool outside = direction.sqrMagnitude >= DeadZone * DeadZone;
        pointer.anchoredPosition = direction;
        pointer.gameObject.SetActive(outside);
        float sector = 360f / labels.Length;
        float angle = Mathf.Repeat(Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg, 360f);
        selected = outside ? Mathf.FloorToInt((angle + sector * .5f) / sector) % labels.Length : -1;
        wheel.SetSelection(labels.Length, selected, village);
        for (int i = 0; i < labels.Length; i++)
            labels[i].color = village ? i == selected ? Play2HudArt.MarketBrown : Play2HudArt.MarketInk
                : i == selected ? Play2UGui.Dark : Play2UGui.Cream;
        centerLabel.text = selected < 0 ? "이모트 선택" : Labels[selected];
        centerHint.text = selected < 0 ? "마우스를 움직이세요" : "휠 버튼을 떼면 재생";
    }

    internal void Cancel()
    {
        canOpen = false;
        if (!IsOpen) return;
        mouse = null;
        releasePending = false;
        Cursor.visible = cursorWasVisible;
        if (overlay != null) overlay.gameObject.SetActive(false);
    }

    public void Dispose()
    {
        Cancel();
        middleButton.Dispose();
    }
}
