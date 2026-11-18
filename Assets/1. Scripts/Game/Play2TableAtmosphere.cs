using System.Linq;
using Poker.Application;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class Play2TableAtmosphere : MonoBehaviour
{
    private const string RoomKey = "Play2.DarkRoom";
    internal static bool DarkRoom => PlayerPrefs.GetInt(RoomKey, 0) == 1;
    private Renderer[] surroundings;
    private bool[] surroundingsVisible;
    private GameObject room, felt;
    private Light[] tableLights;
    private MaterialPropertyBlock properties;
    private SkinnedMeshRenderer[][] actors;
    private Color[][] originalColors;
    private Light[] directions;
    private float[] intensities;
    private Color[] lightColors;
    private AmbientMode ambientMode;
    private Color ambientColor;
    private float ambientIntensity, reflectionIntensity;
    private bool initialized;
    private readonly float[] shade = { 1, 1, 1, 1 };
    private readonly float[] targetShade = { 1, 1, 1, 1 };
    private Light[] seatLights;
    private readonly float[] seatIntensity = new float[4];
    private float stageDim, targetDim;
    private bool indoor;

    internal void Initialize(Transform table, GameObject[] players, Transform[] eyes)
    {
        properties = new MaterialPropertyBlock();
        actors = players.Select(player => player.GetComponentsInChildren<SkinnedMeshRenderer>(true)).ToArray();
        originalColors = actors.Select(skins => skins.Select(skin => skin.sharedMaterial.GetColor("_BaseColor")).ToArray()).ToArray();
        directions = FindObjectsByType<Light>(FindObjectsSortMode.None).Where(light => light.type == LightType.Directional).ToArray();
        intensities = directions.Select(light => light.intensity).ToArray();
        lightColors = directions.Select(light => light.color).ToArray();
        ambientMode = RenderSettings.ambientMode;
        ambientColor = RenderSettings.ambientLight;
        ambientIntensity = RenderSettings.ambientIntensity;
        reflectionIntensity = RenderSettings.reflectionIntensity;
        string[] environmentRoots = { "Terrain", "Buildings", "Sky", "Rocks", "Path", "Vegetation", "Props", "Exterior" };
        surroundings = FindObjectsByType<Renderer>(FindObjectsSortMode.None)
            .Where(renderer => environmentRoots.Contains(renderer.transform.root.name)
                && renderer.name != "table" && !renderer.name.StartsWith("Table Chair ", System.StringComparison.Ordinal)).ToArray();
        surroundingsVisible = surroundings.Select(renderer => renderer.enabled).ToArray();
        room = Instantiate(Resources.Load<GameObject>("TablePresentation/QuietRoom"), table.position, Quaternion.identity, transform);
        var key = AddLight("Table overhead", table.position + Vector3.up * 2.01f, LightType.Spot, 3.6f, 6);
        key.transform.rotation = Quaternion.Euler(90, 0, 0);
        key.spotAngle = 82;
        key.innerSpotAngle = 60;
        key.shadows = LightShadows.Soft;
        var fill = AddLight("Table face fill", table.position + Vector3.up * .70f, LightType.Point, .55f, 3.5f);
        tableLights = new[] { key, fill };
        seatLights = new Light[players.Length];
        for (int i = 0; i < seatLights.Length; i++)
        {
            Vector3 inward = Vector3.ProjectOnPlane(table.position - eyes[i].position, Vector3.up).normalized;
            var light = AddLight("Seat " + (i + 1) + " light",
                eyes[i].position + inward * .48f + Vector3.up * .82f, LightType.Spot, 0, 3);
            light.transform.LookAt(eyes[i].position + inward * .11f - Vector3.up * .38f);
            light.spotAngle = 48;
            light.innerSpotAngle = 28;
            seatLights[i] = light;
        }

        felt = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        felt.name = "Table felt";
        Destroy(felt.GetComponent<Collider>());
        felt.transform.SetParent(table, false);
        felt.transform.localPosition = Vector3.up * .002f;
        felt.transform.localScale = new Vector3(1.17f, .001f, 1.17f);
        felt.GetComponent<Renderer>().sharedMaterial = Resources.Load<Material>("TablePresentation/Felt");
        initialized = true;
        ApplyRoomMode();
    }

    internal static void SelectRoom(bool indoor)
    {
        PlayerPrefs.SetInt(RoomKey, indoor ? 1 : 0);
        FindFirstObjectByType<Play2TableAtmosphere>()?.ApplyRoomMode();
    }

    private void ApplyRoomMode()
    {
        indoor = DarkRoom;
        room.SetActive(indoor);
        felt.SetActive(indoor);
        for (int i = 0; i < surroundings.Length; i++)
            surroundings[i].enabled = !indoor && surroundingsVisible[i];
        foreach (var light in tableLights) light.enabled = indoor;
        RenderSettings.ambientMode = indoor ? AmbientMode.Flat : ambientMode;
        RenderSettings.ambientLight = indoor ? new Color(.15f, .17f, .21f) : ambientColor;
        RenderSettings.ambientIntensity = indoor ? .6f : ambientIntensity;
        RenderSettings.reflectionIntensity = indoor ? .18f : reflectionIntensity;
        for (int i = 0; i < directions.Length; i++)
        {
            directions[i].intensity = indoor ? .05f : intensities[i];
            directions[i].color = indoor ? new Color(.56f, .65f, .85f) : lightColors[i];
        }
    }

    private Light AddLight(string label, Vector3 position, LightType type, float intensity, float range)
    {
        var item = new GameObject(label);
        item.transform.SetParent(transform, false);
        item.transform.position = position;
        var light = item.AddComponent<Light>();
        light.type = type;
        light.intensity = intensity;
        light.range = range;
        light.color = new Color(1, .88f, .69f);
        return light;
    }

    internal void Present(HoldemRoomPacket packet, Play2TablePreview.ShowdownBeat beat, int revealingSeat, int[] highlighted)
    {
        var game = packet?.game;
        bool reveal = beat == Play2TablePreview.ShowdownBeat.Reveal;
        bool award = beat == Play2TablePreview.ShowdownBeat.Award || beat == Play2TablePreview.ShowdownBeat.Finished;
        targetDim = reveal ? .72f : award ? 1f : 0f;
        int remaining = game?.seats.Count(seat => seat.dealtIn &&
            seat.status != (int)HoldemSeatStatus.Folded && seat.status != (int)HoldemSeatStatus.Busted) ?? 0;
        for (int i = 0; i < targetShade.Length; i++)
        {
            var seat = game?.seats.FirstOrDefault(value => value.seat == i + 1);
            bool folded = seat?.status == (int)HoldemSeatStatus.Folded;
            bool outOfGame = seat?.status == (int)HoldemSeatStatus.Busted;
            targetShade[i] = outOfGame ? .40f : folded ? .60f : 1f;
            bool livePair = remaining == 2 && seat?.dealtIn == true && !folded && !outOfGame && game?.hasResult == false;
            bool openingHand = i + 1 == revealingSeat;
            bool focus = beat == Play2TablePreview.ShowdownBeat.Finished || System.Array.IndexOf(highlighted, i + 1) >= 0;
            seatIntensity[i] = award && seat?.awarded > 0 ? (focus ? 2.35f : 1.2f) : reveal && openingHand ? 1.8f : livePair ? 1.5f : 0f;
        }
    }

    private void LateUpdate()
    {
        if (!initialized) return;
        stageDim = Mathf.MoveTowards(stageDim, targetDim, Time.deltaTime * 2.2f);
        for (int i = 0; i < directions.Length; i++)
            directions[i].intensity = (indoor ? .05f : intensities[i]) * Mathf.Lerp(1f, .48f, stageDim);
        if (indoor) tableLights[0].intensity = Mathf.Lerp(3.6f, 2.45f, stageDim);
        for (int i = 0; i < actors.Length; i++)
        {
            shade[i] = Mathf.MoveTowards(shade[i], targetShade[i], Time.deltaTime * .8f);
            for (int mesh = 0; mesh < actors[i].Length; mesh++)
            {
                var skin = actors[i][mesh];
                skin.GetPropertyBlock(properties);
                properties.SetColor("_BaseColor", originalColors[i][mesh] * new Color(shade[i], shade[i], shade[i], 1));
                skin.SetPropertyBlock(properties);
            }
            var light = seatLights[i];
            light.intensity = Mathf.MoveTowards(light.intensity, seatIntensity[i], Time.deltaTime * 7f);
            light.enabled = light.intensity > .015f && (indoor || stageDim > .015f);
        }
    }

    private void OnDestroy()
    {
        if (!initialized) return;
        RenderSettings.ambientMode = ambientMode;
        RenderSettings.ambientLight = ambientColor;
        RenderSettings.ambientIntensity = ambientIntensity;
        RenderSettings.reflectionIntensity = reflectionIntensity;
        for (int i = 0; i < directions.Length; i++)
        {
            if (directions[i] == null) continue;
            directions[i].intensity = intensities[i];
            directions[i].color = lightColors[i];
        }
    }
}
