using UnityEngine;

[AddComponentMenu("")]
[RequireComponent(typeof(CanvasRenderer))]
internal sealed class Play2EmotionWheelGraphic : UnityEngine.UI.MaskableGraphic
{
    internal const float Radius = 224f;
    internal const float LabelRadius = 167f;
    private const float InnerRadius = 112f;
    private int count;
    private int selected = -1;
    private bool village;

    internal void SetSelection(int segmentCount, int segment, bool useVillageTheme)
    {
        if (count == segmentCount && selected == segment && village == useVillageTheme) return;
        count = segmentCount;
        selected = segment;
        village = useVillageTheme;
        SetVerticesDirty();
    }

    internal static Vector2 Direction(float clockwiseDegrees)
    {
        float radians = clockwiseDegrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
    }

    protected override void OnPopulateMesh(UnityEngine.UI.VertexHelper mesh)
    {
        mesh.Clear();
        if (count == 0) return;

        Band(mesh, 0f, InnerRadius - 5f, 0f, 360f, village ? Play2UGui.Hex("263E2DEC") : Play2UGui.Hex("101B18F5"));
        Band(mesh, InnerRadius - 5f, InnerRadius - 3f, 0f, 360f, Play2UGui.Trim);
        Band(mesh, Radius + 2f, Radius + 3f, 0f, 360f, Play2UGui.Trim);
        float sector = 360f / count;
        for (int i = 0; i < count; i++)
        {
            float start = (i - .5f) * sector + 1.35f;
            float end = (i + .5f) * sector - 1.35f;
            bool active = i == selected;
            Color fill = village
                ? active ? Play2UGui.Hex("F5B96C") : Play2UGui.Hex("EEE1BDEB")
                : active ? Play2UGui.Hex("DFC28C") : Play2UGui.Hex("18372DF2");
            Band(mesh, InnerRadius, Radius, start, end, fill);
            Band(mesh, Radius - 3f, Radius, start, end,
                active ? Play2UGui.Gold : village ? Play2HudArt.MarketEdge : Play2UGui.Hex("648476B0"));
        }
    }

    private static void Band(UnityEngine.UI.VertexHelper mesh, float inner, float outer, float from, float to, Color32 tint)
    {
        int count = Mathf.CeilToInt((to - from) / 4f);
        int start = mesh.currentVertCount;
        for (int i = 0; i <= count; i++)
        {
            var direction = Direction(Mathf.Lerp(from, to, (float)i / count));
            mesh.AddVert(direction * inner, tint, Vector2.zero);
            mesh.AddVert(direction * outer, tint, Vector2.zero);
            if (i == 0) continue;

            int index = start + i * 2;
            mesh.AddTriangle(index - 2, index - 1, index + 1);
            mesh.AddTriangle(index - 2, index + 1, index);
        }
    }
}
