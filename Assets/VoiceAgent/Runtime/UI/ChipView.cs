using System;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>One suggestion in the side panel: colored icon, command label, probability and a bar to scale.</summary>
    public sealed class ChipView : MonoBehaviour
    {
        [SerializeField] Button m_Button;
        [SerializeField] Image m_Background, m_Icon, m_Fill;
        [SerializeField] Text m_Letter, m_Label, m_Percent;
        [Tooltip("The command's icon over the colored circle; the letter shows instead when a command has no icon file.")]
        [SerializeField] Image m_Glyph;
        [SerializeField] Color m_Normal = new(1, 1, 1, 0.82f), m_Best = new(1, 1, 1, 0.96f);

        Command m_Command;

        public event Action<Command> Clicked;

        void Awake()
        {
            m_Button.onClick.AddListener(() => { if (m_Command != null) Clicked?.Invoke(m_Command); });
        }

        public void Show(RankedCommand ranked, bool best)
        {
            m_Command = ranked.Command;
            m_Background.color = best ? m_Best : m_Normal;
            m_Icon.color = ranked.Command.Tint;
            m_Fill.color = ranked.Command.Tint;
            m_Fill.rectTransform.anchorMax = new Vector2(Mathf.Clamp(ranked.Probability, 0.01f, 1f), 1f);
            // A command without an icon file (Commands.json "icon") shows its first letter instead.
            Icons.Show(m_Glyph, m_Letter, Icons.Get(ranked.Command.icon), ranked.Command.label[..1]);
            m_Label.text = ranked.Command.label;
            m_Label.fontStyle = best ? FontStyle.Bold : FontStyle.Normal;
            m_Percent.text = $"{ranked.Probability * 100f:0}%";
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            m_Command = null;
            gameObject.SetActive(false);
        }
    }
}
