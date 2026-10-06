using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(CardVisual))]
[CanEditMultipleObjects]
public sealed class CardVisualEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        if (!GUILayout.Button("SetCard 실행 (미리보기)"))
        {
            return;
        }

        foreach (Object selectedTarget in targets)
        {
            CardVisual visual = (CardVisual)selectedTarget;
            visual.ApplyInspectorCard();
            EditorUtility.SetDirty(visual);
        }

        SceneView.RepaintAll();
    }
}
