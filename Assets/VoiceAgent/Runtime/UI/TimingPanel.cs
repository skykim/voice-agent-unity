using System;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>
    /// Per-turn latency breakdown: the utterance, one waterfall row per stage after the end of speech
    /// (speech to text → intent → action → voice) on a shared time axis, the chosen command and the time until the
    /// reply starts.
    /// </summary>
    public sealed class TimingPanel : MonoBehaviour
    {
        public static readonly string[] StageNames = { "Speech to text", "Intent", "Action", "Voice" };
        static readonly Color[] StageColors =
        {
            new(0.35f, 0.8f, 0.8f), new(0.62f, 0.52f, 1f), new(1f, 0.7f, 0.3f), new(0.45f, 0.88f, 0.55f),
        };

        [SerializeField] Text m_Index, m_Utterance, m_Expected, m_Listening, m_Verdict, m_Total, m_AxisStart, m_AxisMid, m_AxisEnd;
        [SerializeField] TimingRow[] m_Rows;

        readonly (string Label, double Ms, double Start, bool Shown)[] m_Stages = new (string, double, double, bool)[StageNames.Length];
        double m_Axis = 4;

        void ApplyAxis(double seconds)
        {
            m_Axis = Math.Max(1, Math.Ceiling(seconds));
            m_AxisStart.text = "0 s";
            m_AxisMid.text = $"{m_Axis / 2:0.#} s";
            m_AxisEnd.text = $"{m_Axis:0} s";
            for (var i = 0; i < m_Stages.Length; i++)
                if (m_Stages[i].Shown) m_Rows[i].Show(m_Stages[i].Label, m_Stages[i].Ms, m_Stages[i].Start, m_Axis, StageColors[i]);
        }

        /// <summary>Starts a turn: header line, utterance (or a placeholder while listening) and a detail line.</summary>
        public void Begin(string header, string utterance, string detail)
        {
            m_Index.text = header;
            m_Utterance.text = utterance;
            m_Expected.text = detail;
            m_Listening.text = string.Empty;
            m_Verdict.text = string.Empty;
            m_Total.text = string.Empty;
            for (var i = 0; i < m_Rows.Length; i++)
            {
                m_Stages[i] = default;
                m_Rows[i].Pending(StageNames[i]);
            }
            ApplyAxis(4);
        }

        public void SetUtterance(string utterance) => m_Utterance.text = $"\"{utterance}\"";

        public void Listening(int partials, float speechSeconds) =>
            m_Listening.text = speechSeconds > 0
                ? $"spoke {speechSeconds:F2} s / {partials} partials"
                : $"listening / {partials} partials";

        /// <summary>Reveals stage <paramref name="stage"/> (0..3) starting where the previous stage ended.</summary>
        public void Stage(int stage, string label, double milliseconds, double startSeconds)
        {
            m_Stages[stage] = (label, milliseconds, startSeconds, true);
            var end = startSeconds + milliseconds / 1000;
            if (end > m_Axis) ApplyAxis(end);
            else m_Rows[stage].Show(label, milliseconds, startSeconds, m_Axis, StageColors[stage]);
        }

        public void Verdict(string chosen, float probability) => m_Verdict.text = $"• action: {chosen} ({probability * 100:0}%)";

        public void Total(double seconds) => m_Total.text = $"• elapsed time: {seconds:F2}s";
    }
}
