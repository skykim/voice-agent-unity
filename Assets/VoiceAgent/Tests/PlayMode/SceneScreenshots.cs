using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using VoiceAgent.Scenes;
using Object = UnityEngine.Object;

namespace VoiceAgent.Tests
{
    /// <summary>
    /// Plays each scene, drives it through its input field and saves Logs/Screenshots/&lt;scene&gt;.png.
    /// Overlay canvases are switched to a camera canvas rendering into a RenderTexture so batchmode can capture them.
    /// </summary>
    public class SceneScreenshots
    {
        static readonly string Folder = Path.GetFullPath("Logs/Screenshots");

        [UnityTest, Timeout(600000)]
        public IEnumerator VoiceAgentDemo()
        {
            yield return Load("VoiceAgentDemo");
            // The scene greets once every model is loaded.
            yield return WaitForText("Tap the orb or type", 300f);
            yield return Submit("who are you?");
            yield return WaitSeconds(3f);
            yield return Submit("turn on the living room lights");
            yield return WaitSeconds(3f);
            yield return Submit("turn the volume up");
            yield return WaitSeconds(3f);
            yield return Submit("what's the weather like in Tokyo?");
            yield return WaitSeconds(8f);
            yield return Submit("how are you today?");
            yield return WaitSeconds(8f);
            yield return Submit("한국의 수도는 어디야?");
            yield return WaitForText("서울", 20f);
            yield return WaitSeconds(4f);
            yield return Type("make it quieter");
            yield return Capture("VoiceAgentDemo");
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator AgentTest()
        {
            yield return Load("AgentTest");
            yield return WaitForText("loaded in", 180f);
            yield return Type("turn the volume down a bit");
            yield return WaitSeconds(0.5f);
            yield return Capture("AgentTest");
        }

        [UnityTest]
        public IEnumerator TtsTest()
        {
            yield return Load("TtsTest");
            yield return WaitSeconds(3f);
            yield return Capture("TtsTest");
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator GemmaTest()
        {
            yield return Load("GemmaTest");
            yield return WaitForText("Ready", 120f);
            var scene = Object.FindAnyObjectByType<GemmaTestScene>();
            var generate = scene.Generate();
            while (!generate.GetAwaiter().IsCompleted) yield return null;
            generate.GetAwaiter().GetResult();
            yield return Click("Compare");
            yield return WaitSeconds(1f);
            yield return Capture("GemmaTest");

            // Without a system prompt the prompt is shorter and the log says so.
            yield return Click("System: Nova");
            Assert.IsTrue(Object.FindObjectsByType<Text>().Any(t => t.text == "System: none"));
            generate = scene.Generate();
            while (!generate.GetAwaiter().IsCompleted) yield return null;
            generate.GetAwaiter().GetResult();
            yield return WaitForText("[system: none]", 5f);
        }

        [UnityTest]
        public IEnumerator SttTest()
        {
            yield return Load("SttTest");
            yield return WaitSeconds(3f);
            yield return Capture("SttTest");
        }

        [UnityTest]
        public IEnumerator VadTest()
        {
            yield return Load("VadTest");
            yield return WaitSeconds(1f);
            yield return Capture("VadTest");
        }

        static IEnumerator Load(string scene)
        {
            SceneManager.LoadScene(scene);
            yield return null;
            yield return null;
        }

        static IEnumerator WaitSeconds(float seconds)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        static IEnumerator WaitForText(string fragment, float timeout)
        {
            var end = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < end)
            {
                if (Object.FindObjectsByType<Text>().Any(t => t.text.Contains(fragment))) yield break;
                yield return null;
            }
            Assert.Fail($"'{fragment}' did not appear within {timeout} s");
        }

        static InputField Field() => Object.FindObjectsByType<InputField>().First();

        static IEnumerator Click(string label)
        {
            var button = Object.FindObjectsByType<Button>().First(b => b.GetComponentInChildren<Text>()?.text == label);
            button.onClick.Invoke();
            yield return null;
        }

        static IEnumerator Submit(string text)
        {
            var field = Field();
            field.text = text;
            yield return WaitSeconds(0.3f);
            field.onSubmit.Invoke(text);
            yield return WaitSeconds(0.5f);
        }

        static IEnumerator Type(string text)
        {
            Field().text = text;
            yield return WaitSeconds(0.5f);
        }

        static IEnumerator Capture(string name)
        {
            Directory.CreateDirectory(Folder);
            var camera = Camera.main;
            var rt = new RenderTexture(1280, 800, 24);
            camera.targetTexture = rt;
            foreach (var canvas in Object.FindObjectsByType<Canvas>())
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;
            }
            yield return null;
            yield return null;
            Canvas.ForceUpdateCanvases();
            camera.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            File.WriteAllBytes(Path.Combine(Folder, name + ".png"), tex.EncodeToPNG());
            camera.targetTexture = null;
            Object.Destroy(rt);
            Object.Destroy(tex);
            Debug.Log($"[Screenshot] {name}: " + string.Join(" | ", Object.FindObjectsByType<Text>()
                .Where(t => t.name is "Status" or "Text").Select(t => t.text.Replace("\n", " ")).Where(s => s.Length > 0).Take(12)));
        }
    }
}
