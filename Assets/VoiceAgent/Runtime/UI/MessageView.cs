using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>A chat bubble that wraps at a max width while letting short messages shrink to fit.</summary>
    public sealed class MessageView : MonoBehaviour
    {
        [SerializeField] Text m_Text;
        [SerializeField] LayoutElement m_TextLayout;
        [SerializeField] float m_MaxWidth = 540f;

        float m_Scale = -1f;

        public void SetText(string text)
        {
            m_Text.text = text;
            Refresh();
        }

        // The canvas scale can change after creation (window resize), which changes the measured width.
        void LateUpdate()
        {
            if (!Mathf.Approximately(m_Scale, m_Text.pixelsPerUnit)) Refresh();
        }

        void Refresh()
        {
            m_Scale = m_Text.pixelsPerUnit;
            var settings = m_Text.GetGenerationSettings(Vector2.zero);
            settings.horizontalOverflow = HorizontalWrapMode.Overflow;
            var width = m_Text.cachedTextGeneratorForLayout.GetPreferredWidth(m_Text.text, settings) / m_Scale;
            m_TextLayout.preferredWidth = Mathf.Min(m_MaxWidth, width + 6);
        }
    }
}
