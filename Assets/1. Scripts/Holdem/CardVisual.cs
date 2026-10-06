using UnityEngine;

public sealed class CardVisual : MonoBehaviour
{
    private const float CardWidth = 0.084f;
    private const float CardHeight = 0.12f;
    private const float AtlasColumns = 9f;
    private const float AtlasRowHeight = 0.158708f;
    private const float AtlasBottomMargin = 0.023878f;

    [SerializeField, Range(Card.MinRank, Card.MaxRank)]
    private int rank = Card.MinRank;

    [SerializeField]
    private Suit suit = Suit.Spade;

    [SerializeField]
    private MeshFilter frontMeshFilter;

    private Mesh runtimeMesh;

#if UNITY_EDITOR
    private Mesh previewMesh;
#endif

    public Card CurrentCard => new Card(rank, suit);

    public void SetCard(Card card)
    {
        rank = card.Rank;
        suit = card.Suit;
        RefreshMesh();
    }

    public void Configure(MeshFilter meshFilter)
    {
        frontMeshFilter = meshFilter;
        RefreshMesh();
    }

#if UNITY_EDITOR
    public void ApplyInspectorCard()
    {
        if (!Application.isPlaying)
        {
            CreatePreviewMesh();
        }

        SetCard(CurrentCard);
    }
#endif

    private void Awake()
    {
        if (!Application.isPlaying || frontMeshFilter == null || frontMeshFilter.sharedMesh == null)
        {
            return;
        }

        runtimeMesh = Instantiate(frontMeshFilter.sharedMesh);
        runtimeMesh.name = "Card Front Runtime";
        frontMeshFilter.sharedMesh = runtimeMesh;
        RefreshMesh();
    }

    private void OnDestroy()
    {
        if (runtimeMesh != null)
        {
            Destroy(runtimeMesh);
        }

#if UNITY_EDITOR
        if (previewMesh != null)
        {
            DestroyImmediate(previewMesh);
        }
#endif
    }

#if UNITY_EDITOR
    private void CreatePreviewMesh()
    {
        if (frontMeshFilter == null || frontMeshFilter.sharedMesh == null)
        {
            return;
        }

        if (previewMesh != null && frontMeshFilter.sharedMesh == previewMesh)
        {
            return;
        }

        if (previewMesh != null)
        {
            DestroyImmediate(previewMesh);
        }

        previewMesh = Instantiate(frontMeshFilter.sharedMesh);
        previewMesh.name = "Card Front Preview";
        previewMesh.hideFlags = HideFlags.DontSave;
        frontMeshFilter.sharedMesh = previewMesh;
    }
#endif

    private void RefreshMesh()
    {
        if (frontMeshFilter == null || frontMeshFilter.sharedMesh == null)
        {
            return;
        }

        if (rank == 2)
        {
            BuildTwoMesh(frontMeshFilter.sharedMesh, suit);
            return;
        }

        GetAtlasCell(rank, suit, out int column, out int row);
        BuildFullCardMesh(frontMeshFilter.sharedMesh, column, row);
    }

    private static void BuildFullCardMesh(Mesh mesh, int column, int row)
    {
        float halfWidth = CardWidth * 0.5f;
        float halfHeight = CardHeight * 0.5f;
        GetCellBounds(column, row, out float uMin, out float uMax, out float vMin, out float vMax);

        mesh.Clear();
        mesh.vertices = new[]
        {
            new Vector3(-halfWidth, 0f, -halfHeight),
            new Vector3(-halfWidth, 0f, halfHeight),
            new Vector3(halfWidth, 0f, halfHeight),
            new Vector3(halfWidth, 0f, -halfHeight)
        };
        mesh.uv = new[]
        {
            new Vector2(uMin, vMin),
            new Vector2(uMin, vMax),
            new Vector2(uMax, vMax),
            new Vector2(uMax, vMin)
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private static void BuildTwoMesh(Mesh mesh, Suit cardSuit)
    {
        GetTwoSource(cardSuit, out int row, out bool useUpperHalf);
        GetCellBounds(0, row, out float uMin, out float uMax, out float vMin, out float vMax);
        float vMiddle = (vMin + vMax) * 0.5f;
        float sourceMin = useUpperHalf ? vMiddle : vMin;
        float sourceMax = useUpperHalf ? vMax : vMiddle;

        Vector2[] topUv = useUpperHalf
            ? CreateNormalUv(uMin, uMax, sourceMin, sourceMax)
            : CreateRotatedUv(uMin, uMax, sourceMin, sourceMax);
        Vector2[] bottomUv = useUpperHalf
            ? CreateRotatedUv(uMin, uMax, sourceMin, sourceMax)
            : CreateNormalUv(uMin, uMax, sourceMin, sourceMax);

        float halfWidth = CardWidth * 0.5f;
        float halfHeight = CardHeight * 0.5f;
        mesh.Clear();
        mesh.vertices = new[]
        {
            new Vector3(-halfWidth, 0f, 0f),
            new Vector3(-halfWidth, 0f, halfHeight),
            new Vector3(halfWidth, 0f, halfHeight),
            new Vector3(halfWidth, 0f, 0f),
            new Vector3(-halfWidth, 0f, -halfHeight),
            new Vector3(-halfWidth, 0f, 0f),
            new Vector3(halfWidth, 0f, 0f),
            new Vector3(halfWidth, 0f, -halfHeight)
        };
        mesh.uv = new[]
        {
            topUv[0], topUv[1], topUv[2], topUv[3],
            bottomUv[0], bottomUv[1], bottomUv[2], bottomUv[3]
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private static Vector2[] CreateNormalUv(float uMin, float uMax, float vMin, float vMax)
    {
        return new[]
        {
            new Vector2(uMin, vMin),
            new Vector2(uMin, vMax),
            new Vector2(uMax, vMax),
            new Vector2(uMax, vMin)
        };
    }

    private static Vector2[] CreateRotatedUv(float uMin, float uMax, float vMin, float vMax)
    {
        return new[]
        {
            new Vector2(uMax, vMax),
            new Vector2(uMax, vMin),
            new Vector2(uMin, vMin),
            new Vector2(uMin, vMax)
        };
    }

    private static void GetAtlasCell(int cardRank, Suit cardSuit, out int column, out int row)
    {
        if (cardRank >= 3 && cardRank <= 10)
        {
            column = cardRank - 2;
            row = cardSuit switch
            {
                Suit.Diamond => 2,
                Suit.Club => 3,
                Suit.Heart => 4,
                Suit.Spade => 5,
                _ => 0
            };
            return;
        }

        int faceColumn = cardRank switch
        {
            11 => 0,
            12 => 1,
            13 => 2,
            1 => 3,
            _ => 0
        };
        bool rightGroup = cardSuit == Suit.Club || cardSuit == Suit.Diamond;
        column = faceColumn + (rightGroup ? 5 : 0);
        row = cardSuit == Suit.Spade || cardSuit == Suit.Club ? 1 : 0;
    }

    private static void GetTwoSource(Suit cardSuit, out int row, out bool useUpperHalf)
    {
        switch (cardSuit)
        {
            case Suit.Spade:
                row = 5;
                useUpperHalf = true;
                break;
            case Suit.Heart:
                row = 5;
                useUpperHalf = false;
                break;
            case Suit.Club:
                row = 4;
                useUpperHalf = true;
                break;
            default:
                row = 4;
                useUpperHalf = false;
                break;
        }
    }

    private static void GetCellBounds(
        int column,
        int row,
        out float uMin,
        out float uMax,
        out float vMin,
        out float vMax)
    {
        uMin = column / AtlasColumns;
        uMax = (column + 1f) / AtlasColumns;
        vMin = AtlasBottomMargin + row * AtlasRowHeight;
        vMax = vMin + AtlasRowHeight;
    }
}
