using UnityEngine;
using UnityEngine.UI;

namespace VoiceAgent.UI
{
    /// <summary>
    /// White Material Icons (Apache 2.0, Resources/Icons) in the colored icon circles. The home tiles have theirs set in the
    /// scenes; a chip loads the icon of the command it shows.
    /// </summary>
    public static class Icons
    {
        /// <summary>Icons/&lt;name&gt; from Resources, or null when there is no such icon.</summary>
        public static Sprite Get(string name) => string.IsNullOrEmpty(name) ? null : Resources.Load<Sprite>($"Icons/{name}");

        /// <summary>Shows <paramref name="sprite"/> in the glyph, or the letter when it is null.</summary>
        public static void Show(Image glyph, Text letter, Sprite sprite, string fallback)
        {
            glyph.sprite = sprite;
            glyph.enabled = sprite != null;
            if (letter == null) return;
            letter.enabled = sprite == null;
            letter.text = fallback;
        }
    }
}
