using System.Diagnostics;
using SentisModels;
using VoiceAgent.UI;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.Scenes
{
    /// <summary>Gemma3 270M + Decision AI alone: live probability for every command as you type, and the persona's chat reply.</summary>
    public sealed class AgentTestScene : MonoBehaviour
    {
        [SerializeField] TestScreenView m_Screen;
        [SerializeField] InputField m_Input;
        [SerializeField] Button m_ScoreButton, m_GenerateButton;
        [SerializeField] RectTransform m_Bars;
        [Tooltip("One command's row (Label, Track/Fill, Percent), copied into the bars once per command in Commands.json order.")]
        [SerializeField] GameObject m_BarTemplate;

        CommandCatalog m_Catalog;
        Persona m_Persona;
        RectTransform[] m_Fills;
        Text[] m_Percents;
        Gemma3Model m_Gemma;
        DecisionAIRanker m_Ranker;
        float m_ScoreAt = -1f;

        async void Start()
        {
            m_Catalog = CommandCatalog.Load();
            BuildBars();
            m_Input.onValueChanged.AddListener(_ => m_ScoreAt = Time.time + 0.1f);
            m_ScoreButton.onClick.AddListener(Score);
            m_GenerateButton.onClick.AddListener(async () => await Generate());

            m_Screen.Status.text = "Loading Gemma3 (tokenizer + 1.7 GB graph)…";
            await Awaitable.NextFrameAsync();
            if (!this) return;
            await ModelRoots.PrepareAsync(text => m_Screen.Status.text = text);
            if (!this) return;
            var clock = Stopwatch.StartNew();
            try
            {
                m_Persona = Persona.Load();
                m_Gemma = ModelRoots.LoadGemma(m_Persona.system_prompt);
                m_Ranker = new DecisionAIRanker(m_Gemma, ModelRoots.IntentHead, m_Catalog);
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogException(e);
                m_Screen.Status.text = $"Couldn't load Gemma3 or the intent head: {e.Message}";
                return;
            }
            m_Ranker.Score("warmup");
            await m_Gemma.GenerateAsync("hi", 2);
            if (!this) return;
            m_Screen.AppendLog($"loaded in {clock.Elapsed.TotalSeconds:F1} s (Decision AI val acc {m_Ranker.ValidationAccuracy:P1})");
            Score();
        }

        /// <summary>One bar per command, so the scene follows Commands.json without being rebuilt.</summary>
        void BuildBars()
        {
            m_BarTemplate.SetActive(false);
            m_Fills = new RectTransform[m_Catalog.Count];
            m_Percents = new Text[m_Catalog.Count];
            for (var i = 0; i < m_Catalog.Count; i++)
            {
                var command = m_Catalog.Commands[i];
                var row = Instantiate(m_BarTemplate, m_Bars).transform;
                row.name = command.id;
                row.gameObject.SetActive(true);
                row.Find("Label").GetComponent<Text>().text = $"{command.label} / {command.id}";
                var fill = row.Find("Track/Fill").GetComponent<Image>();
                fill.color = command.Tint;
                m_Fills[i] = fill.rectTransform;
                m_Percents[i] = row.Find("Percent").GetComponent<Text>();
            }
        }

        void Update()
        {
            if (m_ScoreAt >= 0f && Time.time >= m_ScoreAt)
            {
                m_ScoreAt = -1f;
                Score();
            }
        }

        void Score()
        {
            if (m_Ranker == null || string.IsNullOrWhiteSpace(m_Input.text)) return;
            var probs = m_Ranker.Score(m_Input.text);
            var best = 0;
            for (var i = 0; i < probs.Length && i < m_Fills.Length; i++)
            {
                if (probs[i] > probs[best]) best = i;
                m_Fills[i].anchorMax = new Vector2(Mathf.Max(0.004f, probs[i]), 1);
                m_Percents[i].text = $"{probs[i] * 100f:0.0}%";
                m_Percents[i].fontStyle = FontStyle.Normal;
            }
            m_Percents[best].fontStyle = FontStyle.Bold;
            m_Screen.Status.text = $"tokens {m_Gemma.Tokenize(m_Input.text).Length} / encoder {m_Ranker.LastEncoderMs:F1} ms / head {m_Ranker.LastHeadMs:F2} ms / top: {m_Catalog.Commands[best].id} ({probs[best]:P0})";
        }

        async Awaitable Generate()
        {
            if (m_Gemma == null || m_Gemma.IsGenerating) return;
            var text = m_Input.text;
            m_Gemma.SystemPrompt = m_Persona.SystemPrompt(LangDetect.Of(text));
            var clock = Stopwatch.StartNew();
            var tokens = 0;
            var answer = ReplyText.Clean(await m_Gemma.GenerateAsync(text, 64, _ => tokens++));
            if (!this) return;
            var seconds = clock.Elapsed.TotalSeconds;
            m_Screen.AppendLog($"Q: {text}\nA: {answer}   / {tokens} tokens in {seconds:F2} s ({tokens / seconds:F1} tok/s, one token per frame)");
        }

        void OnDestroy()
        {
            m_Ranker?.Dispose();
            m_Gemma?.Dispose();
        }
    }
}
