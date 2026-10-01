using System;
using System.Collections.Generic;
using SentisModels;
using Unity.InferenceEngine;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>
    /// Microphone → Silero VAD → SenseVoice, one utterance per <see cref="Listen"/> (tap to talk).
    /// Speech start opens a SenseVoice utterance (with 0.4 s of pre-roll), speech end closes it; partials
    /// arrive while talking and one final per utterance. The language is detected per utterance (English or Korean).
    /// </summary>
    public sealed class VoiceInput : IDisposable
    {
        const int PreRollSamples = MicrophoneStream.SampleRate * 2 / 5;
        const float EndSilenceSeconds = 0.7f, NoSpeechTimeout = 6f;

        readonly MicrophoneStream m_Mic = new();
        readonly SileroVad m_Vad;
        readonly SenseVoiceRecognizer m_Stt;
        readonly float[] m_PreRoll = new float[PreRollSamples];
        readonly List<float> m_Utterance = new();
        float[] m_Finished = Array.Empty<float>();
        long m_PreRollEnd;
        bool m_Listening, m_InSpeech, m_Disposed;
        float m_ListenStarted;

        public event Action<string> Partial;
        public event Action<string> Final;
        public event Action SpeechStarted, ListeningStopped;

        public bool IsListening => m_Listening;
        public float Level => m_Mic.Level;
        public SenseVoiceRecognizer Recognizer => m_Stt;

        public VoiceInput()
        {
            m_Vad = new SileroVad(BackendType.CPU, 0.5f, 0.35f, EndSilenceSeconds);
            m_Vad.Load(ModelRoots.Vad);
            m_Stt = new SenseVoiceRecognizer(BackendType.GPUCompute, SenseVoiceLanguage.Auto, partialIntervalSeconds: 0.4f);
            m_Stt.Load(ModelRoots.Stt);

            m_Mic.Samples += OnSamples;
            m_Vad.SpeechStarted += OnSpeechStarted;
            m_Vad.SpeechEnded += OnSpeechEnded;
            m_Stt.PartialTranscript += text => Partial?.Invoke(text);
            m_Stt.ResultReady += async (result, final) =>
            {
                if (final) await FinishAsync(result);
            };
        }

        /// <summary>False without a microphone, or when SenseVoice didn't load (it logs the error).</summary>
        public bool Listen()
        {
            if (m_Listening) return true;
            if (m_Disposed || !m_Stt.IsLoaded || !m_Mic.Start()) return false;
            m_Vad.StartListening();
            m_Listening = true;
            m_InSpeech = false;
            m_ListenStarted = Time.time;
            return true;
        }

        public void Stop()
        {
            if (!m_Listening) return;
            if (m_InSpeech)
            {
                // A language retry transcribes this copy: the next utterance may already be filling m_Utterance.
                m_Finished = m_Utterance.ToArray();
                m_Stt.EndUtterance();
            }
            m_InSpeech = false;
            m_Listening = false;
            m_Vad.StopListening();
            ListeningStopped?.Invoke();
        }

        /// <summary>Call once per frame.</summary>
        public void Tick()
        {
            m_Mic.Poll();
            for (var i = 0; i < 8; i++) m_Vad.Pump();
            if (m_Listening && !m_InSpeech && Time.time - m_ListenStarted > NoSpeechTimeout) Stop();
        }

        void OnSamples(float[] samples, int count, long sequence)
        {
            // The pre-roll follows the mic even between turns so it never holds audio from an earlier utterance.
            var keep = Math.Min(count, PreRollSamples);
            Array.Copy(m_PreRoll, keep, m_PreRoll, 0, PreRollSamples - keep);
            Array.Copy(samples, count - keep, m_PreRoll, PreRollSamples - keep, keep);
            m_PreRollEnd = sequence + count;

            if (!m_Listening) return;
            m_Vad.PushSamples(samples, count);
            if (m_InSpeech)
            {
                m_Stt.PushSamples(samples, count, sequence);
                for (var i = 0; i < count; i++) m_Utterance.Add(samples[i]);
            }
        }

        void OnSpeechStarted()
        {
            if (!m_Listening || m_InSpeech) return;
            m_InSpeech = true;
            m_Stt.BeginUtterance();
            // Right after the mic opens the pre-roll holds fewer real samples than its length: push only those, so their
            // stream positions line up with the chunks that follow.
            var recent = m_PreRoll[(PreRollSamples - (int)Math.Min(PreRollSamples, m_PreRollEnd))..];
            m_Stt.PushSamples(recent, recent.Length, m_PreRollEnd - recent.Length);
            m_Utterance.Clear();
            m_Utterance.AddRange(recent);
            SpeechStarted?.Invoke();
        }

        async Awaitable FinishAsync(SenseVoiceResult result)
        {
            var retry = SpeechLanguage.Retry(result.Language);
            if (retry != null && m_Finished.Length > 0)
            {
                result = await m_Stt.TranscribeAsync(m_Finished, retry);
                if (m_Disposed) return;
            }
            Final?.Invoke(result.Text);
        }

        void OnSpeechEnded()
        {
            if (!m_InSpeech) return;
            Stop();
        }

        public void Dispose()
        {
            m_Disposed = true;
            m_Mic.Dispose();
            m_Vad.Dispose();
            m_Stt.Dispose();
        }
    }
}
