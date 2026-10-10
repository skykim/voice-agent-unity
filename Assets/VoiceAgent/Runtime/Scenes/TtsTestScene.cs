using VoiceAgent.UI;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.Scenes
{
    /// <summary>Supertonic 3 alone: type a sentence (Korean or English, chosen from the text), pick a voice style, listen.</summary>
    public sealed class TtsTestScene : MonoBehaviour
    {
        static readonly string[] Voices = { "F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5" };
        static readonly string[] Samples =
        {
            "Hi, I'm Nova. What can I do for you today?",
            "The living room lights are on. Want some music too?",
            "It's sunny and twenty two degrees in Seoul right now.",
            "Alan Turing was an English mathematician and logician.",
        };

        [SerializeField] TestScreenView m_Screen;
        [SerializeField] Button m_SpeakButton, m_VoiceButton, m_SampleButton;
        [SerializeField] Text m_VoiceLabel;
        [SerializeField] InputField m_Text;
        [SerializeField] SpeechOutput m_Speech;

        int m_Voice = 1, m_Sample;

        async void Start()
        {
            m_Text.text = Samples[0];
            m_VoiceLabel.text = $"Voice: {Voices[m_Voice]}";
            m_SpeakButton.onClick.AddListener(async () => await Speak());
            m_VoiceButton.onClick.AddListener(() =>
            {
                m_Voice = (m_Voice + 1) % Voices.Length;
                m_VoiceLabel.text = $"Voice: {Voices[m_Voice]}";
            });
            m_SampleButton.onClick.AddListener(() =>
            {
                m_Sample = (m_Sample + 1) % Samples.Length;
                m_Text.text = Samples[m_Sample];
            });
            m_Screen.Status.text = "Loading Supertonic…";
            await Awaitable.NextFrameAsync();
            if (!this) return;
            await ModelRoots.PrepareAsync(text => m_Screen.Status.text = text);
            if (!this) return;
            try
            {
                m_Speech.Load(ModelRoots.Tts);
                m_Screen.Status.text = $"model: {ModelRoots.Tts}";
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                m_Screen.Status.text = $"Couldn't load Supertonic: {e.Message}";
            }
        }

        async Awaitable Speak()
        {
            if (!m_Speech.IsLoaded || m_Speech.IsBusy || string.IsNullOrWhiteSpace(m_Text.text)) return;
            m_Screen.Status.text = "Synthesizing…";
            var text = m_Text.text;
            var source = m_Speech.GetComponent<AudioSource>();
            var previous = source.clip;
            await m_Speech.Speak(text, Voices[m_Voice]);
            if (!this) return;
            if (source.clip == previous)
            {
                m_Screen.Status.text = "Synthesis failed (see the Console).";
                return;
            }
            var seconds = source.clip.length;
            m_Screen.AppendLog($"[{Voices[m_Voice]}] {text}   / synth {m_Speech.LastSynthesisMs:F0} ms, audio {seconds:F2} s, RTF {m_Speech.LastSynthesisMs / 1000f / Mathf.Max(seconds, 0.01f):F3}");
            m_Screen.Status.text = $"model: {ModelRoots.Tts}";
        }
    }
}
