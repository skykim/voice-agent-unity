using VoiceAgent.UI;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.Scenes
{
    /// <summary>
    /// SenseVoice (language detected per utterance, English or Korean) with VAD endpointing: partials while speaking, and a
    /// final per utterance with the first pass's tags and timings (the text is VoiceInput's, after any language retry).
    /// </summary>
    public sealed class SttTestScene : MonoBehaviour
    {
        [SerializeField] TestScreenView m_Screen;
        [SerializeField] Button m_Listen, m_Continuous;
        [SerializeField] Text m_ListenLabel, m_ContinuousLabel, m_Partial;

        VoiceInput m_Voice;
        SentisModels.SenseVoiceResult m_FirstPass;
        bool m_ContinuousOn;

        async void Start()
        {
            m_Listen.onClick.AddListener(ToggleListen);
            m_Continuous.onClick.AddListener(() =>
            {
                m_ContinuousOn = !m_ContinuousOn;
                m_ContinuousLabel.text = $"Continuous: {(m_ContinuousOn ? "on" : "off")}";
            });
            m_Screen.Status.text = "Loading SenseVoice…";
            await Awaitable.NextFrameAsync();
            if (!this) return;
            await ModelRoots.PrepareAsync(text => m_Screen.Status.text = text);
            if (!this) return;
            m_Voice = new VoiceInput();
            m_Voice.SpeechStarted += () => m_Partial.text = "…";
            m_Voice.Partial += text => m_Partial.text = text;
            m_Voice.ListeningStopped += () => m_ListenLabel.text = "Listen";
            m_Voice.Recognizer.ResultReady += (result, final) =>
            {
                if (final) m_FirstPass = result;
            };
            m_Voice.Final += text =>
            {
                if (!this) return;
                var r = m_FirstPass;
                m_Partial.text = string.IsNullOrWhiteSpace(text) ? "(no speech)" : text;
                m_Screen.AppendLog(r == null ? text : $"[{r.Language}] [{r.Emotion}] [{r.AudioEvent}] {text}   " +
                                                      $"/ audio {r.AudioSeconds:F1}s, fbank {r.FeatureMs:F0} ms, encoder {r.InferenceMs:F0} ms, RTF {r.Rtf:F3}");
                if (m_ContinuousOn) ToggleListen();
            };
            m_Screen.Status.text = $"model: {ModelRoots.Stt}";
        }

        void ToggleListen()
        {
            if (m_Voice == null) return;
            if (m_Voice.IsListening)
            {
                m_Voice.Stop();
                return;
            }
            if (!m_Voice.Listen())
            {
                m_Screen.AppendLog("Can't listen: no microphone, or the speech model didn't load.");
                return;
            }
            m_ListenLabel.text = "Listening… (stop)";
            m_Partial.text = "Speak now";
        }

        void Update() => m_Voice?.Tick();

        void OnDestroy() => m_Voice?.Dispose();
    }
}
