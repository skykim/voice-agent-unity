using System;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>The shared frame of the per-model test scenes: header, control row, main area, log and status line.</summary>
    public sealed class TestScreenView : MonoBehaviour
    {
        [SerializeField] Text m_Status, m_Log;

        public Text Status => m_Status;

        public void AppendLog(string line, int maxLines = 12)
        {
            var lines = (m_Log.text.Length == 0 ? line : m_Log.text + "\n" + line).Split('\n');
            var start = Math.Max(0, lines.Length - maxLines);
            m_Log.text = string.Join("\n", lines, start, lines.Length - start);
        }
    }
}
