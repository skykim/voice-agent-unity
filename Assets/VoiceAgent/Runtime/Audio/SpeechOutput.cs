using System;
using SentisModels;
using Unity.InferenceEngine;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>
    /// Supertonic 3 synthesis + playback on one AudioSource. A new Speak supersedes the previous one: it waits for a
    /// synthesis in flight (Supertonic's workers run one at a time) and that result is dropped. The language follows the
    /// text: Korean when at least 30% of its letters are Hangul (an answer to a Korean question), otherwise English.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class SpeechOutput : MonoBehaviour
    {
        [Tooltip("Supertonic voice style (F1–F5, M1–M5). The scene overrides it with the persona's voice.")]
        public string Voice = "F2";

        SupertonicTts m_Tts;
        AudioSource m_Source;
        int m_Request;
        bool m_Synthesizing;

        public bool IsLoaded => m_Tts != null && m_Tts.Loaded;
        public bool IsBusy { get; private set; }
        public bool IsSpeaking => IsBusy || (m_Source != null && m_Source.isPlaying);
        public double LastSynthesisMs { get; private set; }

        void Awake() => m_Source = GetComponent<AudioSource>();

        public void Load(string modelRoot)
        {
            m_Tts?.Dispose();
            m_Tts = new SupertonicTts(BackendType.GPUCompute);
            m_Tts.Load(modelRoot);
        }

        public async Awaitable Speak(string text, string voice = null)
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(text)) return;
            var request = ++m_Request;
            m_Source.Stop();
            IsBusy = true;
            try
            {
                while (m_Synthesizing)
                {
                    await Awaitable.NextFrameAsync();
                    if (request != m_Request) return;
                }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                float[] pcm;
                m_Synthesizing = true;
                try
                {
                    pcm = await m_Tts.Synthesize(text, LanguageOf(text), voice ?? Voice);
                }
                finally
                {
                    m_Synthesizing = false;
                }
                LastSynthesisMs = clock.Elapsed.TotalMilliseconds;
                // Also stops here after OnDestroy, which bumps the request.
                if (request != m_Request || pcm == null || pcm.Length == 0) return;
                var clip = AudioClip.Create("tts", pcm.Length, 1, m_Tts.SampleRate, false);
                clip.SetData(pcm, 0);
                if (m_Source.clip != null) Destroy(m_Source.clip);
                m_Source.clip = clip;
                m_Source.Play();
            }
            catch (Exception e)
            {
                // A synthesis cut short by OnDestroy disposing the workers isn't worth reporting.
                if (request == m_Request) Debug.LogException(e);
            }
            finally
            {
                if (request == m_Request) IsBusy = false;
            }
        }

        public static SupertonicLanguage LanguageOf(string text)
        {
            int hangul = 0, letters = 0;
            foreach (var c in text)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if (c >= '가' && c <= '힣') hangul++;
            }
            return letters > 0 && hangul * 10 >= letters * 3 ? SupertonicLanguage.ko : SupertonicLanguage.en;
        }

        public void Stop()
        {
            m_Request++;
            IsBusy = false;
            if (m_Source != null) m_Source.Stop();
        }

        void OnDestroy()
        {
            Stop();
            if (m_Source != null && m_Source.clip != null) Destroy(m_Source.clip);
            m_Tts?.Dispose();
            m_Tts = null;
        }
    }
}
