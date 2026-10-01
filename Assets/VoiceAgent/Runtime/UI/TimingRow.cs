using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>One pipeline stage in the timing panel: name, duration and a waterfall bar on the shared time axis.</summary>
    public sealed class TimingRow : MonoBehaviour
    {
        [SerializeField] Text m_Label, m_Value;
        [SerializeField] Image m_Fill;
        [SerializeField] CanvasGroup m_Group;

        /// <param name="start">Seconds from the end of speech where this stage began.</param>
        /// <param name="axisSeconds">Length of the shared time axis.</param>
        public void Show(string label, double milliseconds, double start, double axisSeconds, Color color)
        {
            m_Label.text = label;
            m_Value.text = milliseconds < 1000 ? $"{milliseconds:F0} ms" : $"{milliseconds / 1000:F2} s";
            var from = (float)(start / axisSeconds);
            var to = (float)((start + milliseconds / 1000) / axisSeconds);
            m_Fill.rectTransform.anchorMin = new Vector2(Mathf.Clamp01(from), 0);
            m_Fill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(Mathf.Max(to, from + 0.012f)), 1);
            m_Fill.color = color;
            m_Group.alpha = 1f;
        }

        /// <summary>Stage not reached yet: name only, dimmed.</summary>
        public void Pending(string label)
        {
            m_Label.text = label;
            m_Value.text = "…";
            m_Fill.rectTransform.anchorMin = Vector2.zero;
            m_Fill.rectTransform.anchorMax = new Vector2(0, 1);
            m_Group.alpha = 0.35f;
        }
    }
}
