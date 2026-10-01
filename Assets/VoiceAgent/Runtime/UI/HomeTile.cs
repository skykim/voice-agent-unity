using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>One device tile of the home panel: tinted background and icon circle while on, plus a state line.</summary>
    public sealed class HomeTile : MonoBehaviour
    {
        static readonly Color Off = new(1, 1, 1, 0.14f);

        [SerializeField] Image m_Background, m_Icon;
        [SerializeField] Text m_State;

        public Image Icon => m_Icon;

        public void Set(bool on, Color color, string state)
        {
            m_Background.color = on ? new Color(color.r, color.g, color.b, 0.45f) : Off;
            m_Icon.color = on ? color : new Color(1, 1, 1, 0.3f);
            m_State.text = state;
        }
    }
}
