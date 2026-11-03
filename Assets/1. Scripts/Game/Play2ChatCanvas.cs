using System.Linq;
using Poker.Application;
using Poker.Foundation;
using Poker.Transport;
using UnityEngine;

public sealed class Play2ChatCanvas : MonoBehaviour
{
    [SerializeField] private GameObject panel;
    [SerializeField] private UnityEngine.UI.ScrollRect scroll;
    [SerializeField] private TMPro.TMP_Text messages;
    [SerializeField] private TMPro.TMP_InputField input;
    [SerializeField] private UnityEngine.UI.Toggle dealer;
    [SerializeField] private UnityEngine.UI.Button send;

    private Play2TablePreview table;
    private HoldemRoomPacket packet;
    private long lastMessage = -1;

    internal void Initialize(Play2TablePreview owner)
    {
        table = owner;
        send.onClick.AddListener(Send);
        input.onSubmit.AddListener(_ => Send());
        panel.SetActive(false);
    }

    internal void Present(HoldemRoomPacket value)
    {
        if (ReferenceEquals(packet, value)) return;
        packet = value;
        bool visible = packet?.hasGame == true && packet.hasChat;
        panel.SetActive(visible);
        if (!visible)
        {
            lastMessage = -1;
            messages.text = "";
            input.SetTextWithoutNotify("");
            dealer.SetIsOnWithoutNotify(false);
            return;
        }

        bool canChat = !packet.paused;
        bool canRequest = canChat && packet.hasOwnUtterances && packet.ownUtterances.canSubmit
            && packet.ownUtterances.remaining > 0 && packet.game.street < (int)HoldemStreet.River;
        input.interactable = send.interactable = canChat;
        dealer.interactable = canRequest;
        if (!canRequest) dealer.SetIsOnWithoutNotify(false);

        var entries = packet.chat.entries;
        long newest = entries.Length == 0 ? -1 : entries[entries.Length - 1].sequence;
        if (newest == lastMessage) return;
        lastMessage = newest;
        messages.text = string.Join("\n", entries.Select(entry =>
        {
            string name = packet.members.FirstOrDefault(member => member.seat == entry.seat)?.name ?? $"{entry.seat}번";
            return $"{name}  {entry.text}";
        }));
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
        scroll.StopMovement();
        scroll.verticalNormalizedPosition = 0;
    }

    private void Send()
    {
        if (packet?.hasGame != true || !send.interactable || string.IsNullOrWhiteSpace(input.text)) return;
        bool toDealer = dealer.interactable && dealer.isOn;
        if (!table.Submit(new HoldemWireRequest
        {
            type = "chat", text = input.text.Trim(), toDealer = toDealer,
            handId = packet.game.handId, street = packet.game.street, version = packet.game.version,
            windowId = toDealer ? packet.ownUtterances.windowId : null
        })) return;
        input.SetTextWithoutNotify("");
        dealer.SetIsOnWithoutNotify(false);
        input.ActivateInputField();
    }
}
