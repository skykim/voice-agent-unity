using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SentisModels;
using Unity.InferenceEngine;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>
    /// Supertonic 3 synthesis + playback on one AudioSource, sentence by sentence. A new Speak supersedes the previous one:
    /// it waits for a synthesis in flight (Supertonic's workers run one at a time) and the old reply's remaining sentences
    /// are dropped. The language follows the
    /// text: Korean when at least 30% of its letters are Hangul (an answer to a Korean question), otherwise English.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class SpeechOutput : MonoBehaviour
    {
        [Tooltip("Supertonic voice style (F1–F5, M1–M5). The scene overrides it with the persona's voice.")]
        public string Voice = "F2";
        [Tooltip("Flow-matching denoising steps per synthesis: fewer is faster, more is cleaner (the package default is 8).")]
        [SerializeField, Range(1, 8)] int m_TotalStep = 4;

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
            m_Tts = new SupertonicTts(BackendType.CPU, m_TotalStep);
            m_Tts.Load(modelRoot);
        }

        /// <summary>
        /// Speaks the text one sentence at a time: synthesizes the first sentence and returns once it starts playing, then
        /// synthesizes each next sentence while the previous one plays. <see cref="IsSpeaking"/> stays true until the last
        /// sentence has played; <see cref="LastSynthesisMs"/> is the first sentence's synthesis time.
        /// </summary>
        public async Awaitable Speak(string text, string voice = null)
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(text)) return;
            var request = ++m_Request;
            m_Source.Stop();
            IsBusy = true;
            var queued = false;
            try
            {
                while (m_Synthesizing)
                {
                    await Awaitable.NextFrameAsync();
                    if (request != m_Request) return;
                }
                // One language for the whole reply so its sentences share a voice.
                var language = LanguageOf(text);
                voice ??= Voice;
                var sentences = Sentences(text);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var pcm = await Synthesize(ForSpeech(sentences[0]), language, voice);
                LastSynthesisMs = clock.Elapsed.TotalMilliseconds;
                // Also stops here after OnDestroy, which bumps the request.
                if (request != m_Request) return;
                Play(pcm);
                if (sentences.Count > 1)
                {
                    queued = true;
                    _ = SpeakRest(sentences, language, voice, request);
                }
            }
            catch (Exception e)
            {
                // A synthesis cut short by OnDestroy disposing the workers isn't worth reporting.
                if (request == m_Request) Debug.LogException(e);
            }
            finally
            {
                if (!queued && request == m_Request) IsBusy = false;
            }
        }

        async Awaitable SpeakRest(List<string> sentences, SupertonicLanguage language, string voice, int request)
        {
            try
            {
                for (var i = 1; i < sentences.Count; i++)
                {
                    var pcm = await Synthesize(ForSpeech(sentences[i]), language, voice);
                    if (request != m_Request) return;
                    while (m_Source.isPlaying)
                    {
                        await Awaitable.NextFrameAsync();
                        if (request != m_Request) return;
                    }
                    Play(pcm);
                }
            }
            catch (Exception e)
            {
                if (request == m_Request) Debug.LogException(e);
            }
            finally
            {
                if (request == m_Request) IsBusy = false;
            }
        }

        async Awaitable<float[]> Synthesize(string sentence, SupertonicLanguage language, string voice)
        {
            m_Synthesizing = true;
            try
            {
                return await m_Tts.Synthesize(sentence, language, voice);
            }
            finally
            {
                m_Synthesizing = false;
            }
        }

        void Play(float[] pcm)
        {
            if (pcm == null || pcm.Length == 0) return;
            var clip = AudioClip.Create("tts", pcm.Length, 1, m_Tts.SampleRate, false);
            clip.SetData(pcm, 0);
            if (m_Source.clip != null) Destroy(m_Source.clip);
            m_Source.clip = clip;
            m_Source.Play();
        }

        // A sentence ends at . ! ? (or their full-width forms and …) followed by whitespace, or at a line break, so
        // decimals like 3.5 stay whole.
        static readonly Regex SentenceEnd = new(@"(?<=[.!?。！？…])\s+|\n+");

        /// <summary>
        /// The sentence as Supertonic gets it: ending in ".." (a closing period becomes "..", a sentence with no closing
        /// mark gets one), which gave the cleaner sentence endings. Questions and exclamations keep their mark for the
        /// intonation.
        /// </summary>
        public static string ForSpeech(string sentence)
        {
            var s = sentence.TrimEnd();
            if (s.Length == 0 || "?!？！…".IndexOf(s[^1]) >= 0) return s;
            return s.TrimEnd('.', '。') + "..";
        }

        public static List<string> Sentences(string text)
        {
            var sentences = new List<string>();
            foreach (var part in SentenceEnd.Split(text.Trim()))
                if (!string.IsNullOrWhiteSpace(part)) sentences.Add(part.Trim());
            if (sentences.Count == 0) sentences.Add(text.Trim());
            return sentences;
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
