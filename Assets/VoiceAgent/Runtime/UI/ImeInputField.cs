using System;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>
    /// An InputField that survives typing Korean over a selection. While the IME is composing, uGUI's caret getters
    /// add the composition length, and Append then cuts the selection out of text with those positions, past its end
    /// (ArgumentOutOfRangeException in String.Substring). The selection is removed here with the plain positions first.
    /// </summary>
    public sealed class ImeInputField : InputField
    {
        protected override void Append(char input)
        {
            var composing = caretPositionInternal - m_CaretPosition;
            if (composing > 0 && !readOnly && m_CaretPosition != m_CaretSelectPosition)
            {
                var start = Math.Clamp(Math.Min(m_CaretPosition, m_CaretSelectPosition), 0, m_Text.Length);
                var end = Math.Clamp(Math.Max(m_CaretPosition, m_CaretSelectPosition), 0, m_Text.Length);
                m_Text = m_Text.Remove(start, end - start);
                m_CaretPosition = m_CaretSelectPosition = start;
            }
            base.Append(input);
        }
    }
}
