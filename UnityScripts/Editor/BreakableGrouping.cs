#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class BreakableGrouping
{
    [MenuItem("GameObject/Breakable/Group as [IgnoreOptFull]", false, 10)]
    private static void GroupIgnoreFull()
    {
        GroupSelection("[IgnoreOptFull]");
    }

    [MenuItem("GameObject/Breakable/Group as [IgnoreOptCustom(near=30,far=100)]", false, 11)]
    private static void GroupIgnoreCustomDefault()
    {
        GroupSelection("[IgnoreOptCustom(near=30,far=100)]");
    }

    [MenuItem("GameObject/Breakable/Group as [IgnoreOptCustom] (empty)", false, 12)]
    private static void GroupIgnoreCustomEmpty()
    {
        GroupSelection("[IgnoreOptCustom]");
    }

    [MenuItem("GameObject/Breakable/Group as Breakable (Zabor)", false, 20)]
    private static void GroupBreakable()
    {
        GroupSelection("Zabor");
    }

    [MenuItem("GameObject/Breakable/Group as Damaging", false, 21)]
    private static void GroupDamaging()
    {
        GroupSelection("Damaging");
    }

    private static void GroupSelection(string tag)
    {
        GameObject[] selection = Selection.gameObjects;
        if (selection == null || selection.Length == 0)
        {
            EditorUtility.DisplayDialog("Breakable Grouping", "Select at least one object first.", "OK");
            return;
        }

        Vector3 center = Vector3.zero;
        for (int i = 0; i < selection.Length; i++)
            center += selection[i].transform.position;
        center /= selection.Length;

        Transform parent = selection[0].transform.parent;

        GameObject group = new GameObject(tag);
        Undo.RegisterCreatedObjectUndo(group, "Create Breakable Group");
        group.transform.SetParent(parent, false);
        group.transform.position = center;

        for (int i = 0; i < selection.Length; i++)
        {
            GameObject go = selection[i];
            if (go == null || go == group)
                continue;

            Undo.SetTransformParent(go.transform, group.transform, "Parent to Breakable Group");
        }

        Selection.activeGameObject = group;
        EditorGUIUtility.PingObject(group);
    }
}
#endif