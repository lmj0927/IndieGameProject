using UnityEditor;
using UnityEngine;

public static class CardPrefabGenerator
{
    private const string AtlasPath = "Assets/ithappy/Casino_Free/Textures/Cards.png";
    private const string SourceMaterialPath = "Assets/ithappy/Casino_Free/Materials/Cards.mat";
    private const string RootFolder = "Assets/2. Prefabs/Cards";
    private const string MaterialFolder = RootFolder + "/Materials";
    private const string FrontMeshPath = RootFolder + "/Card_FrontMesh.asset";
    private const string PrefabPath = RootFolder + "/Card.prefab";

    private const float CardWidth = 0.084f;
    private const float CardHeight = 0.12f;
    private const float CardThickness = 0.003f;
    private const float SurfaceOffset = 0.0001f;

    [MenuItem("Tools/One More Card/Generate Reusable Card Prefab")]
    public static void Generate()
    {
        Texture2D atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(AtlasPath);
        Material frontMaterial = AssetDatabase.LoadAssetAtPath<Material>(SourceMaterialPath);
        if (atlas == null || frontMaterial == null)
        {
            Debug.LogError("Cards.png or Cards.mat was not found in the Casino_Free asset.");
            return;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            Debug.LogError("Universal Render Pipeline/Lit shader was not found.");
            return;
        }

        EnsureFolder(RootFolder);
        EnsureFolder(MaterialFolder);

        Material edgeMaterial = CreateOrUpdateColorMaterial(
            MaterialFolder + "/Card_Edge.mat",
            shader,
            new Color(0.82f, 0.82f, 0.8f));
        Material backMaterial = CreateOrUpdateBackMaterial(shader, atlas);
        Mesh frontMesh = LoadOrCreateFrontMesh();

        GameObject root = new GameObject("Card");
        BoxCollider collider = root.AddComponent<BoxCollider>();
        collider.size = new Vector3(CardWidth, CardThickness, CardHeight);

        CreateBody(root.transform, edgeMaterial);
        MeshFilter frontMeshFilter = CreateFront(root.transform, frontMesh, frontMaterial);
        CreateBack(root.transform, backMaterial);

        CardVisual visual = root.AddComponent<CardVisual>();
        visual.Configure(frontMeshFilter);
        visual.SetCard(new Card(Card.MinRank, Suit.Spade));

        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        EditorUtility.SetDirty(frontMesh);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        Debug.Log($"Generated one reusable PNG-based card prefab at {PrefabPath}.");
    }

    private static void CreateBody(Transform parent, Material material)
    {
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Body";
        body.transform.SetParent(parent, false);
        body.transform.localScale = new Vector3(CardWidth, CardThickness, CardHeight);
        body.GetComponent<MeshRenderer>().sharedMaterial = material;
        Object.DestroyImmediate(body.GetComponent<BoxCollider>());
    }

    private static MeshFilter CreateFront(Transform parent, Mesh mesh, Material material)
    {
        GameObject front = new GameObject("Front");
        front.transform.SetParent(parent, false);
        front.transform.localPosition = new Vector3(0f, CardThickness * 0.5f + SurfaceOffset, 0f);

        MeshFilter meshFilter = front.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = mesh;
        MeshRenderer renderer = front.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        return meshFilter;
    }

    private static void CreateBack(Transform parent, Material material)
    {
        GameObject back = GameObject.CreatePrimitive(PrimitiveType.Quad);
        back.name = "Back";
        back.transform.SetParent(parent, false);
        back.transform.localPosition = new Vector3(0f, -CardThickness * 0.5f - SurfaceOffset, 0f);
        back.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        back.transform.localScale = new Vector3(CardWidth, CardHeight, 1f);
        back.GetComponent<MeshRenderer>().sharedMaterial = material;
        Object.DestroyImmediate(back.GetComponent<MeshCollider>());
    }

    private static Mesh LoadOrCreateFrontMesh()
    {
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(FrontMeshPath);
        if (mesh != null)
        {
            return mesh;
        }

        mesh = new Mesh { name = "Card Front" };
        AssetDatabase.CreateAsset(mesh, FrontMeshPath);
        return mesh;
    }

    private static Material CreateOrUpdateColorMaterial(string path, Shader shader, Color color)
    {
        Material material = LoadOrCreateMaterial(path, shader);
        material.SetTexture("_BaseMap", null);
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Smoothness", 0.25f);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static Material CreateOrUpdateBackMaterial(Shader shader, Texture2D atlas)
    {
        Material material = LoadOrCreateMaterial(MaterialFolder + "/Card_Back.mat", shader);
        material.SetTexture("_BaseMap", atlas);
        material.SetTextureScale("_BaseMap", new Vector2(1f / 9f, 0.158726f));
        material.SetTextureOffset("_BaseMap", new Vector2(0f, 0.499354f));
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Smoothness", 0.25f);
        material.SetFloat("_Cull", 0f);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static Material LoadOrCreateMaterial(string path, Shader shader)
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        material.shader = shader;
        return material;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        int separator = path.LastIndexOf('/');
        string parent = path.Substring(0, separator);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, path.Substring(separator + 1));
    }
}
