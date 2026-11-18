using Poker.Application;
using UnityEngine;
using TMPro;

public sealed class Play2FrontEnd : MonoBehaviour
{
    [SerializeField] private Play2ChatCanvas chatCanvas;

    private Play2TableHud hud;
    private RectTransform canvasRect;
    private Play2TablePreview table;
    internal RectTransform HudRoot { get; private set; }
    internal TMP_FontAsset Font => chatCanvas.Font;
    internal bool CaptureChatInput() => chatCanvas.CaptureTableInput();

    public void Initialize(Play2TablePreview table)
    {
        this.table = table;
        chatCanvas.Initialize(table);
        var canvasObject = new GameObject("Play2 Game HUD", typeof(RectTransform), typeof(Canvas),
            typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);
        canvasRect = (RectTransform)canvasObject.transform;
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;
        var scaler = canvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.Expand;
        chatCanvas.GetComponent<UnityEngine.UI.CanvasScaler>().screenMatchMode = scaler.screenMatchMode;
        chatCanvas.GetComponent<Canvas>().sortingOrder = 10;
        hud = new Play2TableHud(canvasObject.transform, table, chatCanvas.Font, this);
        HudRoot = (RectTransform)canvasRect.Find("Game HUD");
    }

    public void Present(HoldemRoomPacket packet, string status, bool blockActions = false)
    {
        float width = canvasRect.rect.width;
        if (width > 0)
        {
            float dock = packet?.hasGame == true && packet.hasChat ? Play2ChatCanvas.DockWidth : 0;
            float tableLeft = dock / width;
            if (!Mathf.Approximately(HudRoot.anchorMin.x, tableLeft))
            {
                HudRoot.anchorMin = new Vector2(tableLeft, 0f);
                table.SetViewportLeft(tableLeft);
            }
        }
        hud.Present(packet, status);
        chatCanvas.Present(packet);
        if (!hud.CanOpenChat) chatCanvas.DiscardFocusRequest();
        else if (chatCanvas.HandleEnter()) return;
        hud.TickPracticeAdvance(chatCanvas.IsTyping || blockActions);
        hud.HandleKeys(chatCanvas.IsTyping || blockActions);
    }
}
