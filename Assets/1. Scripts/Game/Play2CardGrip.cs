using System.Collections.Generic;
using UnityEngine;

internal sealed class Play2CardGrip
{
    private const float Flight = 0.36f;
    private const float Lift = .57f;
    private const float Settle = .16f;
    private readonly Vector3 pocket;
    private const float GripHeight = 0.044f;
    private const float ViewWristBend = 40f * Mathf.Deg2Rad;
    private const float PutDown = 0.50f;
    private const float LowerArm = 0.30f;
    private const float FoldedScale = 1.10f;
    private readonly Transform upperArm, forearm, wrist, camera;
    private readonly Play2FingerRig fingers;
    private readonly Pose contact;
    private readonly Quaternion wristRest, upperRest, forearmRest;
    private readonly Vector3 shoulderLocal;
    private readonly Vector3 outward, lateral, deck, tableCenter, foldPile;
    private readonly Quaternion deckRotation;
    private readonly float firstLaunch, gap, heldCardScale;
    private readonly bool openingPeek;
    private readonly System.Action peekSound;
    private bool heardPeek;
    private readonly CardVisual first, second;
    private readonly GameObject[] fronts;
    private readonly GameObject viewModel;
    private float foldStarted = -1f;
    private bool folded, revealed, waitingToMuck;
    private bool inspect;
    private float inspection;
    internal bool IsInspecting => inspect;
    internal bool RestingFaceUp => revealed && folded;
    internal void AddRevealedCards(Play2ShowdownCards display, int[] ids)
    {
        display.Add(first, ids[0]);
        display.Add(second, ids[1]);
    }

    internal void Inspect(bool value) => inspect = value && !folded && foldStarted < 0;

    internal bool Contains(Vector2 point, Camera view)
    {
        return ContainsCard(first, point, view) || ContainsCard(second, point, view);
    }

    private static bool ContainsCard(CardVisual card, Vector2 point, Camera view)
    {
        if (!card.gameObject.activeInHierarchy) return false;
        Vector2 min = new(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new(float.NegativeInfinity, float.NegativeInfinity);
        for (int i = 0; i < 4; i++)
        {
            Vector3 local = new((i % 2 == 0 ? -1 : 1) * .047f, 0, (i < 2 ? -1 : 1) * .066f);
            Vector3 projected = view.WorldToScreenPoint(card.transform.TransformPoint(local));
            if (projected.z <= 0) return false;
            min = Vector2.Min(min, projected);
            max = Vector2.Max(max, projected);
        }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y).Contains(point);
    }
    private Pose foldFrom, firstInGrip, secondInGrip;
    private Quaternion muckFacing;

    internal static float Duration(float gap) => gap + Flight + Settle + Lift;

    internal Play2CardGrip(GameObject player, Transform view, Transform table, Transform eye, Vector3 dealOrigin,
        CardVisual first, CardVisual second, float delay, float gap, bool openingPeek, System.Action peekSound = null)
    {
        camera = view;
        this.first = first;
        this.second = second;
        fronts = new[] { first.transform.Find("Front").gameObject, second.transform.Find("Front").gameObject };
        heldCardScale = first.transform.localScale.x;
        this.gap = gap;
        this.openingPeek = openingPeek;
        this.peekSound = peekSound;
        firstLaunch = Time.time + delay;
        outward = Vector3.ProjectOnPlane(eye.position - table.position, Vector3.up).normalized;
        lateral = Vector3.Cross(outward, Vector3.up);
        tableCenter = table.position + Vector3.up * 0.045f;
        deck = dealOrigin;
        deckRotation = Quaternion.LookRotation(-outward) * Quaternion.Euler(0f, 0f, 180f);
        muckFacing = deckRotation;
        pocket = table.position + outward * .50f + lateral * (view != null ? -.125f : .125f) + Vector3.up * .013f;
        foldPile = table.position + outward * 0.48f + lateral * 0.08f + Vector3.up * 0.006f;

        bool left = camera != null;
        if (left)
        {
            viewModel = Object.Instantiate(player, camera, false);
            if (player.TryGetComponent<Play2FingerRig>(out var originalRig))
                viewModel.GetComponent<Play2FingerRig>().BindViewCopy(originalRig);
            viewModel.name = "First Person Left Arm";
            viewModel.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            viewModel.transform.localScale = player.transform.lossyScale;
            viewModel.SetActive(true);
            player = viewModel;
        }
        var animator = player.GetComponent<Animator>();
        upperArm = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        forearm = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
        wrist = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
        wristRest = wrist.localRotation;
        shoulderLocal = upperArm.localPosition;
        upperRest = upperArm.localRotation;
        forearmRest = forearm.localRotation;
        fingers = Play2FingerRig.Attach(animator);
        fingers.SetCardGrip(left, 1f);
        contact = fingers.CardContact(left, wrist);
        fingers.SetCardGrip(left, 0f);

        if (left)
        {
            animator.enabled = false;
            var armSkin = player.GetComponentInChildren<SkinnedMeshRenderer>();
            fingers.PrepareViewMesh(armSkin);
            ShowArmOnly(armSkin, upperArm);
            viewModel.SetActive(false);
        }
    }

    internal float ReachOverTable
    {
        get
        {
            if (folded) return 0f;
            if (foldStarted >= 0f)
            {
                float t = Time.time - foldStarted;
                return (revealed ? 1f : .32f) * Ease(t / .28f) * (1f - Ease((t - .45f) / .35f));
            }
            float dealt = Time.time - firstLaunch;
            return Ease(dealt / Flight) * (1f - Ease((dealt - gap - Flight - Settle) / Lift));
        }
    }

    internal void UpdatePose(float standingRise = 0f, float pushTime = -1f, Vector3 pushPalm = default)
    {
        if (folded) return;
        if (foldStarted >= 0f)
        {
            UpdateFold(Time.time - foldStarted);
            return;
        }

        inspection = Mathf.MoveTowards(inspection, inspect ? 1 : 0, Time.deltaTime / .24f);
        float time = Time.time - firstLaunch;
        bool left = camera != null;
        bool visible = time >= 0f && (left || wrist.gameObject.activeInHierarchy);
        if (viewModel != null) viewModel.SetActive(visible);
        first.gameObject.SetActive(visible);
        second.gameObject.SetActive(visible && time >= gap);
        if (!visible) return;

        float reach = Ease(time / Flight);
        float opening = Mathf.Clamp01((time - gap - Flight - Settle) / Lift);
        float lift = Ease(opening);
        float curl = Ease((time - gap - Flight + .03f) / (Settle + .05f));
        // The thumb relaxes just enough to accept the second card.
        float secondCatch = 1f - Mathf.Abs((time - gap - Flight) / 0.12f);
        float thumbSlip = 0f;
        if (openingPeek && left && opening > 0f && opening < 1f)
        {
            float release = Mathf.Clamp01((opening - .34f) / .26f);
            thumbSlip = .48f * Mathf.Sin(release * Mathf.PI);
            if (!heardPeek && release >= .5f) { heardPeek = true; peekSound?.Invoke(); }
        }
        fingers.SetCardGrip(left, curl, Mathf.Max(thumbSlip, .22f * Ease(secondCatch)));

        Vector3 gripPoint;
        Quaternion cardRotation;
        if (left)
        {
            Vector3 receive = pocket + outward * .051f + Vector3.up * .015f;
            receive = Vector3.Lerp(camera.TransformPoint(new Vector3(-.22f, -.37f, .33f)), receive, reach);
            Vector3 resting = new(-.097f, -.219f, .46f);
            Vector3 reading = new(-.055f, -.190f, .43f);
            gripPoint = Vector3.Lerp(receive, camera.TransformPoint(Vector3.Lerp(resting, reading, Ease(inspection))), lift);
            float receivePitch = openingPeek ? -30f : -53f;
            cardRotation = camera.rotation * Quaternion.Euler(Mathf.Lerp(receivePitch, Mathf.Lerp(-86f, -89f, inspection), lift), -2f, 0f);
        }
        else
        {
            Vector3 catchPoint = pocket + outward * .05f + Vector3.up * .012f;
            Vector3 held = tableCenter + outward * 0.48f + lateral * 0.16f + Vector3.up * (0.058f + standingRise * .62f);
            gripPoint = Vector3.Lerp(Vector3.Lerp(wrist.TransformPoint(contact.position), catchPoint, reach), held, lift);
            Vector3 face = Vector3.Lerp(outward * 0.55f + Vector3.up * 0.83f, outward + Vector3.up * 0.22f, lift);
            cardRotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(Vector3.up, face), face);
        }

        Quaternion wristRotation = cardRotation * Quaternion.Inverse(contact.rotation);
        if (!left) wristRotation = Quaternion.Slerp(wrist.rotation, wristRotation, reach);
        Vector3 wristTarget = gripPoint - wristRotation * Vector3.Scale(contact.position, wrist.lossyScale);
        PoseArm(wristTarget, wristRotation, reach, lift);

        gripPoint = wrist.TransformPoint(contact.position);
        Quaternion gripRotation = wrist.rotation * contact.rotation;
        MoveCard(first.transform, 0, time, opening, lift, gripPoint, gripRotation);
        if (time >= gap) MoveCard(second.transform, 1, time - gap, opening, lift, gripPoint, gripRotation);
        if (waitingToMuck && time >= gap + Flight) { BeginPutDown(); return; }
        if (pushTime >= 0f)
        {
            float lower = Ease(pushTime / .20f) * (1f - Ease((pushTime - 1.04f) / .26f));
            Quaternion down = fingers.PalmRotation(left, -outward, Vector3.down);
            Quaternion heldWrist = wrist.rotation;
            Vector3 heldTarget = wrist.position;
            Vector3 cardPad = RestForPush(first.transform, 0, lower);
            RestForPush(second.transform, 1, lower);
            float atChips = Ease((pushTime - .18f) / .12f) * (1f - Ease((pushTime - .80f) / .21f));
            Vector3 pad = Vector3.Lerp(cardPad + outward * .025f + Vector3.up * .016f, pushPalm, atChips);
            Vector3 openTarget = pad - down * Vector3.Scale(fingers.PushContact(left), wrist.lossyScale);
            fingers.SetCardGrip(left, Mathf.Lerp(curl, .32f, lower));
            PoseArm(Vector3.Lerp(heldTarget, openTarget, lower), Quaternion.Slerp(heldWrist, down, lower), 1, lift, true);
        }
    }

    private Vector3 RestForPush(Transform card, int index, float down)
    {
        var rest = FoldedCardPose(index);
        Vector3 position = rest.position + outward * .025f;
        card.SetPositionAndRotation(Vector3.Lerp(card.position, position, down),
            Quaternion.Slerp(card.rotation, rest.rotation, down));
        return position;
    }

    private void PoseArm(Vector3 wristTarget, Quaternion wristRotation, float reach, float lift, bool push = false)
    {
        Quaternion neutralForearm = wristRotation * Quaternion.Inverse(wristRest);
        Vector3 neutralDirection = neutralForearm * wrist.localPosition.normalized;
        if (viewModel != null)
        {
            upperArm.SetLocalPositionAndRotation(shoulderLocal, upperRest);
            forearm.localRotation = forearmRest;
            float elbowHeight = Mathf.Lerp(-0.58f, -0.40f, lift);
            Vector3 towardGrip = wristTarget - camera.TransformPoint(new Vector3(-0.34f, elbowHeight, 0.20f));
            Vector3 armDirection = push ? towardGrip.normalized : Vector3.RotateTowards(neutralDirection, towardGrip.normalized, ViewWristBend, 0f);
            Quaternion armRotation = Quaternion.FromToRotation(neutralDirection, armDirection) * neutralForearm;
            // Move the whole skeleton so sleeve vertices blended with the upper arm stay with the forearm.
            Transform body = viewModel.transform;
            body.rotation = armRotation * Quaternion.Inverse(forearm.rotation) * body.rotation;
            body.position += wristTarget - wrist.position;
            Play2TableArm.AnchorViewSleeve(upperArm, forearm, camera, true);
        }
        else
        {
            Play2TableArm.Reach(upperArm, forearm, wrist, wristTarget);
        }
        wrist.rotation = wristRotation;
    }

    internal void Fold(bool animate = true)
    {
        if (folded || foldStarted >= 0f || waitingToMuck) return;
        if (!animate || (viewModel == null && !wrist.gameObject.activeInHierarchy))
        {
            FinishFold();
            return;
        }
        if (Time.time < firstLaunch + gap + Flight || !second.gameObject.activeSelf)
        {
            waitingToMuck = true;
            return;
        }
        BeginPutDown();
    }

    private void BeginPutDown()
    {
        waitingToMuck = false;
        foldStarted = Time.time;
        foldFrom = new Pose(wrist.TransformPoint(contact.position), wrist.rotation * contact.rotation);
        firstInGrip = RelativeToFoldGrip(first.transform);
        secondInGrip = RelativeToFoldGrip(second.transform);
        var heading = Vector3.ProjectOnPlane(first.transform.forward, Vector3.up);
        if (heading.sqrMagnitude < .12f) heading = -outward;
        muckFacing = Quaternion.LookRotation(heading.normalized, Vector3.down);
    }

    internal void Reveal(int firstId, int secondId, bool animate = true)
    {
        if (revealed || folded || foldStarted >= 0f) return;
        revealed = true;
        Play2TablePreview.SetCardValue(first, firstId);
        Play2TablePreview.SetCardValue(second, secondId);
        first.transform.Find("Front").gameObject.SetActive(true);
        second.transform.Find("Front").gameObject.SetActive(true);
        Fold(animate);
    }

    private Pose RelativeToFoldGrip(Transform card)
    {
        Quaternion inverse = Quaternion.Inverse(foldFrom.rotation);
        return new Pose(inverse * (card.position - foldFrom.position), inverse * card.rotation);
    }

    private void UpdateFold(float time)
    {
        bool left = viewModel != null;
        if (!wrist.gameObject.activeInHierarchy || time >= PutDown + LowerArm)
        {
            FinishFold();
            return;
        }

        if (!revealed)
        {
            Muck(time, left);
            return;
        }
        float down = Ease(time / PutDown);
        float release = Ease((time - .34f) / .16f);
        float retreat = Ease((time - PutDown) / LowerArm);
        Vector3 placeGrip = foldPile + outward * .035f + Vector3.up * .032f;
        Quaternion gripRotation = Quaternion.Slerp(foldFrom.rotation, Quaternion.LookRotation(-outward), down);
        Quaternion wristRotation = gripRotation * Quaternion.Inverse(contact.rotation);
        Vector3 wristTarget = Vector3.Lerp(foldFrom.position, placeGrip, down) -
            wristRotation * Vector3.Scale(contact.position, wrist.lossyScale);
        Vector3 idle = left ? camera.TransformPoint(new Vector3(-.16f, -.43f, .35f)) : wrist.position;
        Quaternion idleRotation = left ? foldFrom.rotation * Quaternion.Inverse(contact.rotation) : wrist.rotation;
        fingers.SetCardGrip(left, 1f - release);
        PoseArm(Vector3.Lerp(wristTarget, idle, retreat), Quaternion.Slerp(wristRotation, idleRotation, retreat),
            1f - retreat, 1f - down);
        Vector3 actualGrip = wrist.TransformPoint(contact.position);
        Quaternion actualRotation = wrist.rotation * contact.rotation;
        UpdateFoldingCard(first, firstInGrip, 0, actualGrip, actualRotation, release);
        UpdateFoldingCard(second, secondInGrip, 1, actualGrip, actualRotation, release);
    }

    private void Muck(float time, bool left)
    {
        float lower = Ease(time / .32f);
        float push = Mathf.Clamp01((time - .32f) / .24f);
        // The fingers accelerate the packet. The last few centimetres slide freely on the felt.
        float slide = push < .5f ? 2f * push * push : 1f - 2f * (1f - push) * (1f - push);
        float release = Ease((time - .36f) / .11f);
        float retreat = Ease((time - .44f) / .31f);
        Pose top = FoldedCardPose(1);
        Vector3 approach = top.position + outward * .085f;
        Vector3 fingerPad = approach - outward * (.085f * Mathf.Min(slide, .5f))
            + outward * .040f + Vector3.up * .012f;
        Quaternion heldWrist = foldFrom.rotation * Quaternion.Inverse(contact.rotation);
        Quaternion flatWrist = fingers.PalmRotation(left, -outward, Vector3.down);
        Quaternion wristRotation = Quaternion.Slerp(heldWrist, flatWrist, lower);
        Vector3 start = foldFrom.position - heldWrist * Vector3.Scale(contact.position, wrist.lossyScale);
        Vector3 atFelt = fingerPad - flatWrist * Vector3.Scale(fingers.PushContact(left), wrist.lossyScale);
        Vector3 target = Vector3.Lerp(start, atFelt, lower);
        target.y += .022f * Mathf.Sin(Mathf.PI * lower);

        // The animator supplies the remote hand's resting pose each frame.
        Vector3 rest = left ? camera.TransformPoint(new Vector3(-.24f, -.43f, .16f)) : wrist.position;
        Quaternion restRotation = left ? flatWrist : wrist.rotation;
        Vector3 leaving = Vector3.Lerp(target, rest, retreat);
        leaving.y += .020f * Mathf.Sin(Mathf.PI * retreat);
        PoseArm(leaving, Quaternion.Slerp(wristRotation, restRotation, retreat), 1f - retreat, 1f - lower, true);
        fingers.SetCardGrip(left, (1f - .65f * lower) * (1f - release));
        Vector3 pinch = wrist.TransformPoint(contact.position);
        Quaternion facing = wrist.rotation * contact.rotation;
        PlaceMuckedCard(first, firstInGrip, 0, pinch, facing, lower, slide);
        PlaceMuckedCard(second, secondInGrip, 1, pinch, facing, lower, slide);
    }

    private void PlaceMuckedCard(CardVisual card, Pose held, int index, Vector3 pinch, Quaternion rotation, float lower, float slide)
    {
        Pose rest = FoldedCardPose(index);
        Quaternion face = Quaternion.Slerp(rotation * held.rotation, rest.rotation, lower);
        Vector3 center = Vector3.Lerp(pinch + rotation * held.position, rest.position + outward * (.085f * (1f - slide)), lower);
        float scale = Mathf.Lerp(heldCardScale, FoldedScale, lower);
        float cornerHeight = scale * (.047f * Mathf.Abs((face * Vector3.right).y) + .066f * Mathf.Abs((face * Vector3.forward).y));
        center.y = Mathf.Max(center.y, rest.position.y + cornerHeight);
        card.transform.SetPositionAndRotation(center, face);
        card.transform.localScale = Vector3.one * scale;
        if (lower >= 1f) fronts[index].SetActive(false);
    }

    private void UpdateFoldingCard(CardVisual card, Pose inGrip, int index, Vector3 grip, Quaternion rotation, float release)
    {
        Pose placed = FoldedCardPose(index);
        Vector3 held = grip + rotation * inGrip.position;
        card.transform.SetPositionAndRotation(Vector3.Lerp(held, placed.position, release),
            Quaternion.Slerp(rotation * inGrip.rotation, placed.rotation, release));
        card.transform.localScale = Vector3.one * Mathf.Lerp(heldCardScale, FoldedScale, release);
        if (revealed || release >= .10f) card.transform.Find("Front").gameObject.SetActive(revealed);
    }

    private Pose FoldedCardPose(int index)
    {
        if (revealed)
        {
            Vector3 shown = foldPile - lateral * .08f + outward * .027f
                + lateral * (index == 0 ? -.053f : .053f) + Vector3.up * .002f;
            return new Pose(shown, Quaternion.LookRotation(-outward));
        }
        // Offset the top card just enough for both backs to read as separate cards.
        float spread = camera != null ? .025f : .036f;
        Vector3 position = foldPile + lateral * ((camera != null ? -.16f : .02f) + index * spread)
            + (outward * .005f + Vector3.up * .0045f) * index;
        Quaternion rotation = muckFacing * Quaternion.AngleAxis(index == 0 ? -4f : 5f, Vector3.up);
        return new Pose(position, rotation);
    }

    private void FinishFold()
    {
        PutCardFaceDown(first, 0);
        PutCardFaceDown(second, 1);
        fingers.SetCardGrip(viewModel != null, 0f);
        if (viewModel != null) viewModel.SetActive(false);
        folded = true;
    }

    private void PutCardFaceDown(CardVisual card, int index)
    {
        Pose pose = FoldedCardPose(index);
        card.transform.SetPositionAndRotation(pose.position, pose.rotation);
        card.transform.localScale = Vector3.one * FoldedScale;
        card.transform.Find("Front").gameObject.SetActive(revealed);
        card.gameObject.SetActive(true);
    }

    private void MoveCard(Transform card, int index, float time, float opening, float fan, Vector3 gripPoint, Quaternion facing)
    {
        float travel = Ease(time / Flight);
        float spread = camera != null ? Mathf.Lerp(12f, 18f, inspection) : 18f;
        float angle = index == 0 ? -spread : spread;
        Quaternion rotation = facing * Quaternion.AngleAxis(angle * fan, Vector3.up);
        if (openingPeek && opening < 1f)
        {
            // Both cards hinge at the pinch. The second corner follows the first under the thumb.
            float start = .12f + index * .28f;
            float turn = Mathf.Clamp01((opening - start) / (1f - start));
            float hinge = Ease(turn);
            // The thumb holds the corner down, then releases it with a short paper snap.
            float pitch = 112f * (1f - hinge) - 5f * Mathf.Sin(Mathf.PI * turn) * hinge;
            rotation *= Quaternion.AngleAxis(pitch, Vector3.right);
        }
        Vector3 held = gripPoint + rotation * new Vector3(0f, index * 0.0035f - 0.0015f, GripHeight * card.localScale.z);
        float separation = camera != null ? .014f : .010f;
        held += facing * Vector3.right * ((index == 0 ? -separation : separation) * fan);
        Vector3 landing = pocket + lateral * (index == 0 ? -.022f : .022f) + Vector3.up * (.003f * index);
        Quaternion back = deckRotation * Quaternion.AngleAxis(index == 0 ? -7f : 7f, Vector3.up);
        if (time < Flight)
        {
            Vector3 slide = Vector3.Lerp(deck, landing, travel) + Vector3.up * (Mathf.Sin(travel * Mathf.PI) * .031f);
            card.SetPositionAndRotation(slide, Quaternion.Slerp(deckRotation, back, travel));
            return;
        }
        float pickup = Ease(opening / .60f);
        card.SetPositionAndRotation(Vector3.Lerp(landing, held, pickup), Quaternion.Slerp(back, rotation, pickup));
        if (camera != null)
        {
            bool readable = !waitingToMuck && opening >= (openingPeek ? .28f + .19f * index : .16f);
            if (fronts[index].activeSelf != readable) fronts[index].SetActive(readable);
        }
    }

    private static float Ease(float value) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(value));

    internal static void ShowArmOnly(SkinnedMeshRenderer skin, Transform upperArm)
    {
        var mesh = skin.sharedMesh;
        var bones = skin.bones;
        var arm = new bool[bones.Length];
        for (int i = 0; i < arm.Length; i++)
            arm[i] = bones[i].IsChildOf(upperArm);

        var counts = mesh.GetBonesPerVertex().ToArray();
        var weights = mesh.GetAllBoneWeights().ToArray();
        var visible = new bool[mesh.vertexCount];
        int offset = 0;
        for (int i = 0; i < visible.Length; i++)
        {
            float armWeight = 0f;
            for (int j = 0; j < counts[i]; j++)
            {
                var weight = weights[offset++];
                if (arm[weight.boneIndex]) armWeight += weight.weight;
            }
            visible[i] = armWeight > 0.40f;
        }

        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
        {
            var indices = mesh.GetTriangles(submesh);
            var kept = new List<int>();
            for (int i = 0; i < indices.Length; i += 3)
            {
                if (!visible[indices[i]] || !visible[indices[i + 1]] || !visible[indices[i + 2]]) continue;
                kept.Add(indices[i]);
                kept.Add(indices[i + 1]);
                kept.Add(indices[i + 2]);
            }
            mesh.SetTriangles(kept, submesh, false);
        }
        skin.updateWhenOffscreen = true;
        skin.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    internal void Release()
    {
        if (viewModel != null) Object.Destroy(viewModel);
        else fingers.SetCardGrip(false, 0f);
    }
}
