using OmpaLompa.Audio;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OmpaLompa.EditorTools
{
    /// <summary>
    /// Rebuilds Assets/Scenes/AudioTest.unity from scratch (menu: Ompa Lompa > Audio > Build AudioTest Scene).
    /// Only touches that one scene file; other scenes of the project are never opened or saved.
    /// </summary>
    public static class AudioTestSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/AudioTest.unity";

        [MenuItem("Ompa Lompa/Audio/Build AudioTest Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Audio rig: mic + detector + debug panel on one object.
            var rig = new GameObject("AudioRig");
            var mic = rig.AddComponent<MicrophoneInput>();
            var detector = rig.AddComponent<VoiceEventDetector>();
            var ui = rig.AddComponent<AudioDebugUI>();
            SetRef(detector, "microphone", mic);
            SetRef(ui, "microphone", mic);
            SetRef(ui, "detector", detector);

            // Floor
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(2f, 1f, 1f);
            floor.transform.position = new Vector3(0f, -0.5f, 0f);

            // Three cubes standing in for the creatures; alternate slide direction.
            float[] xs = { -3f, 0f, 3f };
            float[] dirs = { -1f, 1f, 1f };
            for (int i = 0; i < xs.Length; i++)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = $"TestCube_{i + 1}";
                cube.transform.position = new Vector3(xs[i], 0f, 0f);
                var reaction = cube.AddComponent<AudioTestCubeReaction>();

                var so = new SerializedObject(reaction);
                so.FindProperty("slideOffset").vector3Value = new Vector3(2.5f * dirs[i], 0f, 0f);
                so.ApplyModifiedPropertiesWithoutUndo();

                // Persistent listeners = exactly what colleagues do in the Inspector.
                UnityEventTools.AddVoidPersistentListener(detector.OnShout, reaction.Jump);
                UnityEventTools.AddVoidPersistentListener(detector.OnBlow, reaction.Slide);
            }

            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 2f, -9f);
                cam.transform.rotation = Quaternion.Euler(8f, 0f, 0f);
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[AudioTestSceneBuilder] Saved {ScenePath}");
        }

        /// <summary>Sets a private [SerializeField] object reference.</summary>
        static void SetRef(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
