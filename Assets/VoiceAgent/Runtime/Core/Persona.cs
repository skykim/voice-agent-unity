using System;
using UnityEngine;

namespace VoiceAgent
{
    /// <summary>
    /// Who the assistant is (Resources/Persona.json): the self-introduction is a fixed line picked by the
    /// "introduce_self" command, and the system prompt shapes every Gemma3 chat reply.
    /// </summary>
    [Serializable]
    public sealed class Persona
    {
        public string name, voice, greeting, introduction, system_prompt, reply_language, search_reply, not_sure, search_miss_prefix, low_confidence;
        public string introduction_ko, reply_language_ko, not_sure_ko, search_miss_prefix_ko, low_confidence_ko;

        /// <summary>
        /// The system prompt for a turn in <paramref name="lang"/>: the persona, <see cref="search_reply"/> when Gemma answers
        /// a search question the web couldn't, and one line naming only that language. Without that line Gemma answers
        /// Korean in English; a rule naming both languages made it answer English in Korean.
        /// </summary>
        public string SystemPrompt(Lang lang, bool search = false) =>
            $"{system_prompt} {(search ? search_reply + " " : string.Empty)}{(lang == Lang.Ko ? reply_language_ko : reply_language)}";

        public string Introduction(Lang lang) => lang == Lang.Ko ? introduction_ko : introduction;
        public string NotSure(Lang lang) => lang == Lang.Ko ? not_sure_ko : not_sure;
        public string SearchMissPrefix(Lang lang) => lang == Lang.Ko ? search_miss_prefix_ko : search_miss_prefix;

        /// <summary>The low-confidence question with the top suggestion filled in.</summary>
        public string Clarify(Command command, Lang lang = Lang.En) => (lang == Lang.Ko ? low_confidence_ko : low_confidence).Replace("{command}", command.Label(lang));

        public static Persona Load() => JsonUtility.FromJson<Persona>(Resources.Load<TextAsset>("Persona").text);
    }
}
