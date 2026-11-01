using UnityEditor;
using UnityEngine;

namespace OmpaLompa.Audio.Editor
{
    /// <summary>
    /// Replaces the plain 'Device Name' text field with a dropdown listing the connected microphones.
    /// </summary>
    [CustomEditor(typeof(MicrophoneInput))]
    public class MicrophoneInputEditor : UnityEditor.Editor
    {
        string[] devices = System.Array.Empty<string>();
        bool listed;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Microphone", EditorStyles.boldLabel);
            SerializedProperty nameProp = serializedObject.FindProperty("deviceName");

            if (!listed) Refresh(); // query once, Microphone.devices is slow

            using (new EditorGUILayout.HorizontalScope())
            {
                int current = System.Array.IndexOf(devices, nameProp.stringValue);
                // Index 0 = "Default" (empty name -> first device at runtime).
                var options = new string[devices.Length + 1];
                options[0] = "(first available)";
                devices.CopyTo(options, 1);

                int picked = EditorGUILayout.Popup("Device", current + 1, options);
                if (picked != current + 1)
                    nameProp.stringValue = picked == 0 ? "" : devices[picked - 1];

                if (GUILayout.Button("Refresh", GUILayout.Width(60))) Refresh();
            }

            if (devices.Length == 0)
                EditorGUILayout.HelpBox("No microphone detected.", MessageType.Warning);
            else if (!string.IsNullOrEmpty(nameProp.stringValue) && System.Array.IndexOf(devices, nameProp.stringValue) < 0)
                EditorGUILayout.HelpBox($"'{nameProp.stringValue}' is not connected; the first device will be used.", MessageType.Info);

            EditorGUILayout.Space();
            // Draw everything else, skipping the raw name field and the script field.
            DrawPropertiesExcluding(serializedObject, "m_Script", "deviceName");
            serializedObject.ApplyModifiedProperties();
        }

        void Refresh()
        {
            devices = Microphone.devices;
            listed = true;
        }
    }
}
