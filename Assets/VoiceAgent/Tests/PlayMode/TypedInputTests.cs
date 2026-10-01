using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using VoiceAgent.UI;
using Object = UnityEngine.Object;

namespace VoiceAgent.Tests
{
    /// <summary>Typing in the chat field and pressing Enter runs the command and empties the field, as speaking does.</summary>
    public class TypedInputTests
    {
        [UnityTest, Timeout(600000)]
        public IEnumerator Enter_RunsTheCommandAndClearsTheField()
        {
            SceneManager.LoadScene("VoiceAgentDemo");
            // The load happens on the next frame; until then the previous test's scene (and its greeting) is still there.
            yield return null;
            yield return null;
            yield return WaitForText("Tap the orb or type", 300f);

            yield return Enter("turn on the lights");
            Assert.AreEqual(string.Empty, Field().text, "Enter empties the field");
            yield return WaitForText("Lights are on.", 10f);
            yield return WaitSeconds(3f);

            // Gemma is still generating the chat reply on the next frame: Enter keeps the text instead of dropping it.
            yield return Enter("how are you today?");
            yield return Enter("turn off the lights");
            Assert.AreEqual("turn off the lights", Field().text, "a busy turn leaves the text in the field");
            Assert.IsTrue(Texts().Any(t => t.Contains(AssistantView.BusyNotice)), "the status line says why");

            // Once the reply is done, the same Enter runs it.
            var end = Time.realtimeSinceStartup + 30f;
            while (Field().text.Length > 0 && Time.realtimeSinceStartup < end)
            {
                yield return WaitSeconds(0.5f);
                yield return Enter(Field().text);
            }
            Assert.AreEqual(string.Empty, Field().text);
            yield return WaitForText("Lights are off.", 10f);
            Assert.IsFalse(Texts().Any(t => t.Contains(AssistantView.BusyNotice)), "the notice goes away once the turn runs");
        }

        static InputField Field() => Object.FindObjectsByType<InputField>().First();

        static string[] Texts() => Object.FindObjectsByType<Text>().Select(t => t.text).ToArray();

        /// <summary>Types the text and presses Enter (the field's onSubmit, which a single-line field fires on Return).</summary>
        static IEnumerator Enter(string text)
        {
            var field = Field();
            field.text = text;
            yield return null;
            field.onSubmit.Invoke(field.text);
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
                if (Texts().Any(t => t.Contains(fragment))) yield break;
                yield return null;
            }
            Assert.Fail($"'{fragment}' did not appear within {timeout} s");
        }
    }
}
