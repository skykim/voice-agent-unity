using System;
using System.Diagnostics;
using System.Threading;
using SentisModels;
using UnityEngine;
using UnityEngine.UI;
using VoiceAgent.UI;

namespace VoiceAgent.Scenes
{
    /// <summary>
    /// Gemma3 270M alone: streamed greedy generation with prompt size, time to first token and tokens per second,
    /// under an editable system prompt (Nova's, none, or your own), plus embedding similarity on the device.
    /// </summary>
    public sealed class GemmaTestScene : MonoBehaviour
    {
        static readonly string[] Samples =
        {
            "Tell me a fun fact about octopuses.",
            "What should I cook for dinner tonight?",
            "Explain what a black hole is in one sentence.",
            "오늘 기분이 어때?",
            "재미있는 농담 하나 해 줘.",
        };

        static readonly string[] CompareWith =
        {
            "Turn on the lights", "What's the weather like?", "Play some music", "Who are you?", "불 좀 꺼 줘",
        };

        [SerializeField] TestScreenView m_Screen;
        [SerializeField] InputField m_Prompt, m_SystemPrompt;
        [SerializeField] Button m_GenerateButton, m_StopButton, m_SampleButton, m_PersonaButton, m_CompareButton;
        [SerializeField] Text m_PersonaLabel, m_Output;

        Gemma3Model m_Gemma;
        CancellationTokenSource m_Cancel;
        string m_Persona;
        Persona m_PersonaData;
        int m_Sample;

        public bool IsReady => m_Gemma != null && !m_Gemma.IsGenerating;

        async void Start()
        {
            m_Prompt.text = Samples[0];
            m_GenerateButton.onClick.AddListener(async () => await Generate());
            m_StopButton.onClick.AddListener(() => m_Cancel?.Cancel());
            m_SampleButton.onClick.AddListener(() => m_Prompt.text = Samples[m_Sample = (m_Sample + 1) % Samples.Length]);
            m_PersonaButton.onClick.AddListener(() => m_SystemPrompt.text = SystemKind == "Nova" ? string.Empty : m_Persona);
            m_SystemPrompt.onValueChanged.AddListener(_ => ApplySystemPrompt());
            m_CompareButton.onClick.AddListener(Compare);

            m_Screen.Status.text = "Loading Gemma3 (tokenizer + 1.7 GB graph)…";
            await Awaitable.NextFrameAsync();
            if (!this) return;
            await ModelRoots.PrepareAsync(text => m_Screen.Status.text = text);
            if (!this) return;
            var clock = Stopwatch.StartNew();
            m_PersonaData = Persona.Load();
            m_Persona = m_PersonaData.system_prompt;
            m_SystemPrompt.text = m_Persona;
            m_Gemma = ModelRoots.LoadGemma(m_Persona);
            var load = clock.Elapsed.TotalSeconds;
            clock.Restart();
            await m_Gemma.WarmupAsync();
            if (!this) return;
            ApplySystemPrompt();
            m_Screen.AppendLog($"loaded in {load:F1} s, GPU warm-up {clock.Elapsed.TotalSeconds:F1} s / {ModelRoots.GemmaPackage}");
            m_Screen.Status.text = "Ready: type a prompt and press Generate.";
        }

        /// <summary>"Nova" (the persona prompt), "none" (empty) or "custom" (edited in the box).</summary>
        string SystemKind => string.IsNullOrWhiteSpace(m_SystemPrompt.text) ? "none" : m_SystemPrompt.text == m_Persona ? "Nova" : "custom";

        void ApplySystemPrompt()
        {
            if (m_Gemma != null) m_Gemma.SystemPrompt = SystemKind == "none" ? null : m_SystemPrompt.text;
            m_PersonaLabel.text = $"System: {SystemKind}";
        }

        public async Awaitable Generate()
        {
            if (!IsReady || string.IsNullOrWhiteSpace(m_Prompt.text)) return;
            var prompt = m_Prompt.text;
            // Nova's prompt gets the line that names the question's language, as in the assistant.
            if (SystemKind == "Nova") m_Gemma.SystemPrompt = m_PersonaData.SystemPrompt(LangDetect.Of(prompt));
            var promptTokens = m_Gemma.Tokenize(m_Gemma.ChatPrompt(prompt)).Length;
            m_Cancel = new CancellationTokenSource();
            m_Output.text = "…";
            m_Screen.Status.text = $"Generating (prompt {promptTokens} tokens)…";

            var clock = Stopwatch.StartNew();
            double firstToken = 0;
            var tokens = 0;
            var answer = await m_Gemma.GenerateAsync(prompt, 96, text =>
            {
                if (tokens++ == 0) firstToken = clock.Elapsed.TotalSeconds;
                m_Output.text = ReplyText.Clean(text);
            }, m_Cancel.Token);
            if (!this) return;
            var total = clock.Elapsed.TotalSeconds;
            var stopped = m_Cancel.IsCancellationRequested;
            m_Cancel.Dispose();
            m_Cancel = null;

            answer = ReplyText.Clean(answer);
            m_Output.text = answer.Length > 0 ? answer : "(empty reply)";
            var rate = tokens > 1 ? (tokens - 1) / Math.Max(total - firstToken, 1e-3) : 0;
            var stats = $"prompt {promptTokens} tok / first token {firstToken * 1000:F0} ms / {tokens} tok in {total:F2} s / {rate:F1} tok/s{(stopped ? " / stopped" : string.Empty)}";
            m_Screen.Status.text = stats;
            m_Screen.AppendLog($"[system: {SystemKind}] Q: {prompt}\nA: {answer}\n   {stats}");
        }

        void Compare()
        {
            if (!IsReady || string.IsNullOrWhiteSpace(m_Prompt.text)) return;
            var clock = Stopwatch.StartNew();
            var texts = new string[CompareWith.Length + 1];
            texts[0] = m_Prompt.text;
            Array.Copy(CompareWith, 0, texts, 1, CompareWith.Length);
            var embeddings = m_Gemma.EmbedBatch(texts);
            var scores = m_Gemma.Similarity(embeddings[0], embeddings[1..]);
            var ms = clock.Elapsed.TotalMilliseconds;
            var best = 0;
            for (var i = 1; i < scores.Length; i++)
                if (scores[i] > scores[best]) best = i;
            var parts = new string[scores.Length];
            for (var i = 0; i < scores.Length; i++) parts[i] = $"{(i == best ? "▶ " : string.Empty)}{CompareWith[i]} {scores[i]:+0.00;-0.00}";
            m_Screen.AppendLog($"Similarity to '{m_Prompt.text}' (centered cosine, {ms:F0} ms for {scores.Length + 1} embeddings):\n   {string.Join("  /  ", parts)}");
        }

        void OnDestroy()
        {
            m_Cancel?.Cancel();
            m_Gemma?.Dispose();
        }
    }
}
