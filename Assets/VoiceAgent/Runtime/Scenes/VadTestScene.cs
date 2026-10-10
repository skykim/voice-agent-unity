using System;
using SentisModels;
using VoiceAgent.UI;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.Scenes
{
    /// <summary>Silero VAD alone: live speech probability per 32 ms frame, speech segments in the log.</summary>
    public sealed class VadTestScene : MonoBehaviour
    {
        const float EndSilenceSeconds = 0.6f;

        [SerializeField] TestScreenView m_Screen;
        [SerializeField] Button m_Toggle;
        [SerializeField] Text m_ToggleLabel, m_Indicator;
        [SerializeField] RectTransform[] m_Bars;
        [SerializeField] Image[] m_BarImages;

        MicrophoneStream m_Mic;
        SileroVad m_Vad;
        float[] m_Probabilities;
        int m_Head;
        float m_SpeechStart;
        bool m_Running, m_Speech;

        async void Start()
        {
            m_Probabilities = new float[m_Bars.Length];
            await ModelRoots.PrepareAsync(text => m_Screen.Status.text = text);
            if (!this) return;
            m_Toggle.onClick.AddListener(Toggle);
            m_Mic = new MicrophoneStream();
            m_Vad = new SileroVad(BackendType.CPU, 0.5f, 0.35f, EndSilenceSeconds);
            m_Vad.Load(ModelRoots.Vad);
            m_Vad.VoiceProbability += p =>
            {
                m_Probabilities[m_Head] = p;
                m_Head = (m_Head + 1) % m_Probabilities.Length;
            };
            m_Vad.SpeechStarted += () =>
            {
                m_Speech = true;
                m_SpeechStart = Time.time;
                m_Screen.AppendLog($"{DateTime.Now:HH:mm:ss.f}  speech start");
            };
            m_Vad.SpeechEnded += () =>
            {
                m_Speech = false;
                m_Screen.AppendLog($"{DateTime.Now:HH:mm:ss.f}  speech end   ({Time.time - m_SpeechStart:F2} s incl. {EndSilenceSeconds} s hangover)");
            };
            m_Mic.Samples += (samples, count, _) => m_Vad.PushSamples(samples, count);
            m_Screen.Status.text = $"model: {ModelRoots.Vad}";
        }

        void Toggle()
        {
            m_Running = !m_Running;
            if (m_Running)
            {
                if (!m_Mic.Start())
                {
                    m_Running = false;
                    m_Screen.AppendLog("No microphone found.");
                    return;
                }
                m_Vad.StartListening();
                m_Screen.Status.text = $"mic: {m_Mic.DeviceName}";
            }
            else
            {
                m_Vad.StopListening();
                m_Mic.Stop();
                m_Speech = false;
            }
            m_ToggleLabel.text = m_Running ? "Stop mic" : "Start mic";
        }

        void Update()
        {
            m_Mic?.Poll();
            for (var i = 0; i < 8; i++) m_Vad?.Pump();
            m_Indicator.text = !m_Running ? "idle" : m_Speech ? "● SPEECH" : "silence";
            m_Indicator.color = m_Speech ? new Color(1f, 0.45f, 0.5f) : Color.white;
            var n = m_Bars.Length;
            for (var i = 0; i < n; i++)
            {
                var p = m_Probabilities[(m_Head + i) % n];
                m_Bars[i].anchorMax = new Vector2(m_Bars[i].anchorMax.x, Mathf.Max(0.005f, p));
                m_BarImages[i].color = p >= 0.5f ? new Color(1f, 0.45f, 0.5f) : new Color(0.5f, 0.75f, 1f, 0.8f);
            }
        }

        void OnDestroy()
        {
            m_Mic?.Dispose();
            m_Vad?.Dispose();
        }
    }
}
