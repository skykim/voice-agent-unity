using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace VoiceAgent.Tests
{
    /// <summary>Every scene carries its UI in the scene file, with each scene script's references assigned.</summary>
    public class SceneAuthoringTests
    {
        static string[] ScenePaths => EditorBuildSettings.scenes.Select(s => s.path).ToArray();

        [Test]
        public void AllSixScenesAreInBuildSettings() => Assert.AreEqual(6, ScenePaths.Length);

        [TestCaseSource(nameof(ScenePaths))]
        public void SceneHasPlacedUiAndWiredReferences(string path)
        {
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Assert.IsNotNull(Object.FindAnyObjectByType<Canvas>(), "no Canvas in the scene");
            Assert.IsNotNull(Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>(), "no EventSystem in the scene");
            Assert.Greater(Object.FindObjectsByType<Graphic>(FindObjectsInactive.Include).Length, 10, "too few UI graphics placed");

            var ours = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include)
                .Where(b => b.GetType().Namespace?.StartsWith("VoiceAgent") == true).ToList();
            Assert.IsNotEmpty(ours);
            foreach (var behaviour in ours)
            {
                // Only the fields our classes declare: a subclass of a Unity component (ImeInputField) also inherits
                // references that are meant to stay empty, such as its explicit navigation targets.
                var declared = OwnFields(behaviour.GetType());
                var property = new SerializedObject(behaviour).GetIterator();
                while (property.NextVisible(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_Script") continue;
                    if (!declared.Contains(property.propertyPath.Split('.')[0])) continue;
                    Assert.IsNotNull(property.objectReferenceValue, $"{path}: {behaviour.GetType().Name}.{property.propertyPath} is not assigned");
                }
            }
        }

        static HashSet<string> OwnFields(Type type)
        {
            var names = new HashSet<string>();
            for (var t = type; t != null && t.Namespace?.StartsWith("VoiceAgent") == true; t = t.BaseType)
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    names.Add(field.Name);
            return names;
        }

        /// <summary>
        /// A click focuses an InputField only through a raycast-target graphic on the field's own object: without one the
        /// click lands on whatever is behind it, and the field can't be clicked into once it loses focus.
        /// </summary>
        [TestCaseSource(nameof(ScenePaths))]
        public void InputFieldsCanBeClicked(string path)
        {
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            foreach (var field in Object.FindObjectsByType<InputField>(FindObjectsInactive.Include))
            {
                var graphic = field.GetComponent<Graphic>();
                Assert.IsNotNull(graphic, $"{path}: {field.name} has no graphic to receive clicks");
                Assert.IsTrue(graphic.raycastTarget, $"{path}: {field.name}'s graphic is not a raycast target");
                Assert.AreSame(graphic, field.targetGraphic, $"{path}: {field.name}'s target graphic is not its own graphic");
            }
        }
    }
}
