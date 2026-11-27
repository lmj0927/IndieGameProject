using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

internal static class Play2HudArt
{
    private static readonly Dictionary<string, Sprite> Sprites = new();
    private static Sprite rounded;
    private static TMP_FontAsset tableFont;
    private static readonly Dictionary<string, Color> Surfaces = new()
    {
        { "PinePanel", Play2UGui.Hex("18282AF5") },
        { "PlayingPanel", Play2UGui.Hex("18282AF5") },
        { "PlayingChat", Play2UGui.Hex("152124EF") },
        { "PlayingBadge", Play2UGui.Hex("172427E8") },
        { "PlayingAction", Play2UGui.Hex("293C3EFF") },
        { "GoldButton", Play2UGui.Hex("E7D3A6FF") },
        { "RedButton", Play2UGui.Hex("853F39FF") },
        { "ActionRow", Play2UGui.Hex("304244FF") },
        { "HeaderBadge", Play2UGui.Hex("263638FF") },
        { "SelectedTarget", Play2UGui.Hex("E7D3A6FF") },
        { "PlayingInput", Play2UGui.Hex("0E191CFF") },
        { "PlayingSend", Play2UGui.Hex("304244FF") },
        { "CreamPanel", Play2UGui.Hex("EEE9DFFF") },
        { "PaperRow", Play2UGui.Hex("DED8CBFF") },
        { "Keycap", Play2UGui.Hex("E7D3A6FF") },
        { "MarketPanel", Play2UGui.Hex("F0E1BC") },
        { "MarketSeat", Play2UGui.Hex("F4E9C9") },
        { "MarketPaper", Play2UGui.Hex("FFF9E6") },
        { "MarketButton", Play2UGui.Hex("FFF4D5") },
        { "MarketPrimary", Play2UGui.Hex("F5B96C") },
        { "MarketMoss", Play2UGui.Hex("365941") }
    };

    internal static readonly Color MarketInk = Play2UGui.Hex("2E452B"),
        MarketMuted = Play2UGui.Hex("566047"), MarketEdge = Play2UGui.Hex("988460"),
        MarketBrown = Play2UGui.Hex("553523"), MarketLightText = Play2UGui.Hex("FFF4D5");

    internal static TMP_FontAsset TableFont(TMP_FontAsset fallback)
    {
        if (tableFont != null) return tableFont;
        var regular = Resources.Load<Font>("InGameUI/Fonts/Pretendard-Regular");
        var semiBold = Resources.Load<Font>("InGameUI/Fonts/Pretendard-SemiBold");
        if (regular == null || semiBold == null) return fallback;

        var body = CreateTableFont(regular);
        var emphasis = CreateTableFont(semiBold);
        if (body == null || emphasis == null) return fallback;
        emphasis.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
        body.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
        body.fontWeightTable[(int)FontWeight.Bold / 100] =
            new TMP_FontWeightPair { regularTypeface = emphasis };
        return tableFont = body;
    }

    private static TMP_FontAsset CreateTableFont(Font source) =>
        TMP_FontAsset.CreateFontAsset(source, 56, 7, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
            1024, 1024, AtlasPopulationMode.Dynamic, true);

    internal static void Apply(UnityEngine.UI.Image image, string asset)
    {
        if (Surfaces.TryGetValue(asset, out var color))
        {
            if (rounded == null) rounded = CreateRoundedSurface();
            image.sprite = rounded;
            image.type = UnityEngine.UI.Image.Type.Sliced;
            image.color = color;
            return;
        }
        if (!Sprites.TryGetValue(asset, out var sprite))
        {
            sprite = Resources.Load<Sprite>("InGameUI/" + asset);
            Sprites.Add(asset, sprite);
            if (sprite == null) Debug.LogError($"Missing InGame UI sprite: {asset}");
        }

        image.sprite = sprite;
        image.type = sprite != null && sprite.border != Vector4.zero
            ? UnityEngine.UI.Image.Type.Sliced
            : UnityEngine.UI.Image.Type.Simple;
        image.color = Color.white;
    }

    private static Sprite CreateRoundedSurface()
    {
        const int size = 32;
        const float radius = 7;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "HUD rounded surface" };
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var point = new Vector2(Mathf.Abs(x + .5f - size / 2f), Mathf.Abs(y + .5f - size / 2f));
            var corner = Vector2.Max(point - Vector2.one * (size / 2f - radius), Vector2.zero);
            pixels[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(radius + .5f - corner.magnitude));
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100, 0,
            SpriteMeshType.FullRect, new Vector4(8, 8, 8, 8));
    }

    internal static UnityEngine.UI.Image Skin(UnityEngine.UI.Graphic graphic, string asset)
    {
        if (graphic is UnityEngine.UI.Image image)
        {
            Apply(image, asset);
            return image;
        }

        graphic.color = Color.clear;
        var background = Play2UGui.Fill("Sprite Background", graphic.rectTransform);
        background.SetAsFirstSibling();
        var spriteImage = Play2UGui.Image(background, Color.white);
        Apply(spriteImage, asset);
        return spriteImage;
    }

    internal static RectTransform Plate(string name, Transform parent, Vector2 position, Vector2 size,
        string asset, bool raycast = false)
    {
        var rect = Play2UGui.Rect(name, parent, position, size);
        Apply(Play2UGui.Image(rect, Color.white, raycast), asset);
        return rect;
    }

    internal static UnityEngine.UI.Button Button(string name, Transform parent, string label, Vector2 position,
        Vector2 size, string asset, Color textColor, TMP_FontAsset font, UnityAction click, float fontSize = 16)
    {
        var button = Play2UGui.Button(name, parent, label, position, size, Color.white, textColor, font, click, fontSize);
        Apply((UnityEngine.UI.Image)button.targetGraphic, asset);
        button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        return button;
    }

    internal static UnityEngine.UI.Button Clickable(RectTransform plate, UnityAction click)
    {
        var image = plate.GetComponent<UnityEngine.UI.Image>();
        image.raycastTarget = true;
        var button = plate.gameObject.AddComponent<Play2ActionButton>();
        button.targetGraphic = image;
        button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None };
        button.onClick.AddListener(click);
        return button;
    }

    internal static void Keycap(Transform parent, TMP_FontAsset font, string key, Vector2 position, float width = 38)
    {
        var cap = Plate(key + " Keycap", parent, position, new Vector2(width, 23), "Keycap");
        Play2UGui.Text("Key", cap, key, Vector2.zero, cap.sizeDelta, 13, Play2UGui.Ink, font, style: FontStyles.Bold);
    }

    internal static void Fit(TextMeshProUGUI label, float min)
    {
        label.enableAutoSizing = true;
        label.fontSizeMin = min;
        label.fontSizeMax = label.fontSize;
    }
}
