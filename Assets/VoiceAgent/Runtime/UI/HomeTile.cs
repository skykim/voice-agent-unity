using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>One device tile of the home panel: tinted background and icon circle while on, plus a state line.</summary>
    public sealed class HomeTile : MonoBehaviour
    {
        static readonly Color Off = new(0.105f, 0.125f, 0.165f), IconOff = new(0.17f, 0.195f, 0.24f);

        [SerializeField] Image m_Background, m_Icon;
        [SerializeField] Text m_State;

        public Image Icon => m_Icon;

        public void Set(bool on, Color color, string state)
        {
            m_Background.color = on ? new Color(color.r, color.g, color.b, 0.35f) : Off;
            m_Icon.color = on ? color : IconOff;
            m_State.text = state;
        }
    }
}
