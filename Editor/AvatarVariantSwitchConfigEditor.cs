using Lanstard.AvatarVariantSwitcher;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

[CustomEditor(typeof(AvatarVariantSwitchConfig))]
public class AvatarVariantSwitchConfigEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawDefaultInspector();
        serializedObject.ApplyModifiedProperties();

        var config = (AvatarVariantSwitchConfig)target;

        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Workflow", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "1. Configure the variants list and each entry's includedRoots.\n2. Click Generate Menu to create the int parameter menu.\n3. Click Batch Upload All Variants to upload each variant and capture its blueprint ID.\n4. Click Export Mapping File to write the JSON used by the OSC bridge.",
            MessageType.Info);

        if (config.avatarDescriptor == null && GUILayout.Button("Auto Assign Avatar Descriptor"))
        {
            config.avatarDescriptor = config.GetComponent<VRCAvatarDescriptor>();
            if (config.avatarDescriptor != null && config.installTargetMenu == null)
            {
                config.installTargetMenu = config.avatarDescriptor.expressionsMenu;
            }

            EditorUtility.SetDirty(config);
        }

        EditorGUI.BeginDisabledGroup(AvatarVariantSwitchWorkflow.IsBusy);

        if (GUILayout.Button("Generate Menu"))
        {
            AvatarVariantSwitchWorkflow.GenerateMenu(config);
        }

        if (GUILayout.Button("Batch Upload All Variants"))
        {
            AvatarVariantSwitchWorkflow.StartBatchUpload(config);
        }

        if (GUILayout.Button("Export Mapping File"))
        {
            AvatarVariantSwitchWorkflow.ExportMappingFile(config);
        }

        EditorGUI.EndDisabledGroup();

        EditorGUILayout.EndVertical();
    }
}
