using System.Text;

namespace VoiceAgent
{
    public static class ReplyText
    {
        /// <summary>Drops emoji and markdown from generated text: the TTS and the UI font can't render them.</summary>
        public static string Clean(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsSurrogate(c) || c == '*' || c == '#' || (c >= '\u2600' && c <= '\u27BF') || c == '\uFE0F') continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
