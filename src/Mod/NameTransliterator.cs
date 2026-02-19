using System;
using System.Collections.Generic;
using System.Text;

namespace NotTonightRussian
{
    /// <summary>
    /// Transliterates English names to Russian Cyrillic using lookup tables
    /// and rule-based phonetic fallback. Designed for Not Tonight's name pools.
    /// </summary>
    public static class NameTransliterator
    {
        // Cache transliterated results to avoid repeated work
        private static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // All known game names: English -> Russian
        // Populated from NameData.cs lookup tables
        private static readonly Dictionary<string, string> KnownNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static NameTransliterator()
        {
            // Load all lookup tables from NameData
            foreach (var kv in NameData.MaleFirstNames)
                KnownNames[kv.Key] = kv.Value;
            foreach (var kv in NameData.FemaleFirstNames)
                KnownNames[kv.Key] = kv.Value;
            foreach (var kv in NameData.Surnames)
                KnownNames[kv.Key] = kv.Value;
            foreach (var kv in NameData.GuestlistForenames)
                if (!KnownNames.ContainsKey(kv.Key))
                    KnownNames[kv.Key] = kv.Value;
            foreach (var kv in NameData.GuestlistSurnames)
                if (!KnownNames.ContainsKey(kv.Key))
                    KnownNames[kv.Key] = kv.Value;
        }

        /// <summary>
        /// Check if text looks like it contains an English name (two capitalized words).
        /// Returns true and sets result to the transliterated version if successful.
        /// </summary>
        public static bool TryTransliterate(string text, out string result)
        {
            result = null;
            if (text == null || text.Length < 3) return false;

            // Check cache first
            if (Cache.TryGetValue(text, out result))
                return result != null;

            // Quick check: must contain at least one ASCII letter
            bool hasAscii = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    hasAscii = true;
                    break;
                }
            }
            if (!hasAscii)
            {
                Cache[text] = null;
                return false;
            }

            // Check for Cyrillic characters
            bool hasCyrillic = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '\u0400' && c <= '\u04FF')
                {
                    hasCyrillic = true;
                    break;
                }
            }

            // Mixed Cyrillic+Latin: try replacing known Latin names within the string
            if (hasCyrillic && hasAscii)
            {
                result = TryTransliterateMixed(text);
                Cache[text] = result;
                return result != null;
            }
            // Pure Cyrillic: already translated
            if (hasCyrillic)
            {
                Cache[text] = null;
                return false;
            }

            // Try to match "FirstName LastName" pattern
            string trimmed = text.Trim();

            // Game sometimes sends "First\nLast" (newline-separated on ID cards)
            // Normalize newlines to spaces, then restore later
            bool hadNewline = trimmed.IndexOf('\n') >= 0;
            string normalized = hadNewline
                ? trimmed.Replace("\r\n", " ").Replace("\n", " ")
                : trimmed;

            // Split into words
            string[] words = normalized.Split(' ');
            if (words.Length < 1 || words.Length > 4)
            {
                Cache[text] = null;
                return false;
            }

            // Check if this looks like a name (capitalized words, all ASCII letters)
            bool isNameLike = true;
            int nameWordCount = 0;
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w.Length == 0) continue;

                // Allow "de", "van", "von", "o'" as connectors
                if (w == "de" || w == "van" || w == "von" || w == "del" || w == "di")
                {
                    nameWordCount++;
                    continue;
                }

                // Must start with uppercase ASCII letter
                if (w[0] < 'A' || w[0] > 'Z')
                {
                    // Allow o'Something
                    if (w.Length > 2 && w[0] == 'o' && w[1] == '\'')
                    {
                        nameWordCount++;
                        continue;
                    }
                    isNameLike = false;
                    break;
                }

                // Rest must be letters, hyphens, or apostrophes
                bool allLetters = true;
                for (int j = 1; j < w.Length; j++)
                {
                    char c = w[j];
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          c == '-' || c == '\'' || c == '\u00E9' || c == '\u00E0' ||
                          c == '\u00F3' || c == '\u00E1' || c == '\u00ED'))
                    {
                        allLetters = false;
                        break;
                    }
                }
                if (!allLetters)
                {
                    isNameLike = false;
                    break;
                }
                nameWordCount++;
            }

            if (!isNameLike || nameWordCount < 1)
            {
                Cache[text] = null;
                return false;
            }

            // Single-word text: require it to be a known name (avoid false positives)
            bool hasKnown = false;
            if (nameWordCount == 1)
            {
                for (int i = 0; i < words.Length; i++)
                {
                    if (words[i].Length > 0 && KnownNames.ContainsKey(words[i]))
                    {
                        hasKnown = true;
                        break;
                    }
                }
                if (!hasKnown)
                {
                    Cache[text] = null;
                    return false;
                }
            }

            // For multi-word names: prefer known names but allow rule-based for
            // any "CapWord CapWord" pattern (ID cards, guest lists).
            // DynamicTranslations already catches venue names before we get here.
            if (!hasKnown && nameWordCount < 2)
            {
                Cache[text] = null;
                return false;
            }

            // Transliterate each word
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < words.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                string w = words[i];

                // Connectors
                if (w == "de") { sb.Append("де"); continue; }
                if (w == "van") { sb.Append("ван"); continue; }
                if (w == "von") { sb.Append("фон"); continue; }
                if (w == "del") { sb.Append("дель"); continue; }
                if (w == "di") { sb.Append("ди"); continue; }

                // Try lookup first
                string ru;
                if (KnownNames.TryGetValue(w, out ru))
                {
                    // Preserve original case pattern
                    if (IsAllUpper(w) && ru.Length > 0)
                        ru = ru.ToUpper();
                    sb.Append(ru);
                }
                else
                {
                    // Rule-based fallback
                    string transliterated = TransliterateWord(w);
                    sb.Append(transliterated);
                }
            }

            result = sb.ToString();

            // Don't return if nothing changed
            if (result == normalized)
            {
                Cache[text] = null;
                return false;
            }

            // Restore newline if original had one (ID card "First\nLast" format)
            if (hadNewline)
            {
                int spacePos = result.IndexOf(' ');
                if (spacePos >= 0)
                    result = result.Substring(0, spacePos) + "\n" + result.Substring(spacePos + 1);
            }

            Cache[text] = result;
            return true;
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }

        /// <summary>
        /// Handle mixed Cyrillic+Latin text by finding and replacing known Latin name-words.
        /// Scans right-to-left to preserve string indices during replacement.
        /// </summary>
        private static string TryTransliterateMixed(string text)
        {
            StringBuilder sb = new StringBuilder(text);
            bool changed = false;
            int i = text.Length - 1;
            while (i >= 0)
            {
                if (!IsAsciiLetter(text[i])) { i--; continue; }
                // Found end of a Latin word — find its start
                int end = i + 1;
                while (i >= 0 && (IsAsciiLetter(text[i]) || text[i] == '-' || text[i] == '\''))
                    i--;
                int start = i + 1;
                string word = text.Substring(start, end - start);
                string ru;
                if (KnownNames.TryGetValue(word, out ru))
                {
                    if (IsAllUpper(word) && ru.Length > 0)
                        ru = ru.ToUpper();
                    sb.Remove(start, end - start);
                    sb.Insert(start, ru);
                    changed = true;
                }
                i--;
            }
            return changed ? sb.ToString() : null;
        }

        private static bool IsAllUpper(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'a' && c <= 'z') return false;
            }
            return true;
        }

        /// <summary>
        /// Rule-based English-to-Russian name transliteration.
        /// Handles common English phonetic patterns.
        /// </summary>
        private static string TransliterateWord(string word)
        {
            if (word == null || word.Length == 0) return word;

            StringBuilder sb = new StringBuilder();
            string lower = word.ToLower();
            int len = lower.Length;
            bool capitalize = char.IsUpper(word[0]);

            for (int i = 0; i < len; i++)
            {
                char c = lower[i];
                bool isFirst = (sb.Length == 0);

                // Multi-character patterns (check longest first)

                // "ough"
                if (i + 3 < len && lower.Substring(i, 4) == "ough")
                {
                    sb.Append("о");
                    i += 3;
                    continue;
                }
                // "tion"
                if (i + 3 < len && lower.Substring(i, 4) == "tion")
                {
                    sb.Append("шн");  // approximation for names
                    i += 3;
                    continue;
                }
                // "tch"
                if (i + 2 < len && lower.Substring(i, 3) == "tch")
                {
                    sb.Append("тч");
                    i += 2;
                    continue;
                }
                // "sch"
                if (i + 2 < len && lower.Substring(i, 3) == "sch")
                {
                    sb.Append("ш");
                    i += 2;
                    continue;
                }
                // "sh"
                if (i + 1 < len && lower.Substring(i, 2) == "sh")
                {
                    sb.Append("ш");
                    i++;
                    continue;
                }
                // "ch"
                if (i + 1 < len && lower.Substring(i, 2) == "ch")
                {
                    sb.Append("ч");
                    i++;
                    continue;
                }
                // "th"
                if (i + 1 < len && lower.Substring(i, 2) == "th")
                {
                    sb.Append("т");
                    i++;
                    continue;
                }
                // "ph"
                if (i + 1 < len && lower.Substring(i, 2) == "ph")
                {
                    sb.Append("ф");
                    i++;
                    continue;
                }
                // "wh"
                if (i + 1 < len && lower.Substring(i, 2) == "wh")
                {
                    sb.Append("у");
                    i++;
                    continue;
                }
                // "ck"
                if (i + 1 < len && lower.Substring(i, 2) == "ck")
                {
                    sb.Append("к");
                    i++;
                    continue;
                }
                // "gh" (usually silent in English names)
                if (i + 1 < len && lower.Substring(i, 2) == "gh")
                {
                    // After vowel, typically silent
                    if (i > 0 && IsVowel(lower[i - 1]))
                    {
                        i++;
                        continue;
                    }
                    sb.Append("г");
                    i++;
                    continue;
                }
                // "ee"
                if (i + 1 < len && lower.Substring(i, 2) == "ee")
                {
                    sb.Append("и");
                    i++;
                    continue;
                }
                // "oo"
                if (i + 1 < len && lower.Substring(i, 2) == "oo")
                {
                    sb.Append("у");
                    i++;
                    continue;
                }
                // "ou"
                if (i + 1 < len && lower.Substring(i, 2) == "ou")
                {
                    sb.Append("ау");
                    i++;
                    continue;
                }
                // "ow" at end of word
                if (i + 1 < len && lower.Substring(i, 2) == "ow" && (i + 2 >= len || !IsVowel(lower[i + 2])))
                {
                    sb.Append("оу");
                    i++;
                    continue;
                }
                // "ew"
                if (i + 1 < len && lower.Substring(i, 2) == "ew")
                {
                    sb.Append("ью");
                    i++;
                    continue;
                }
                // "ay"
                if (i + 1 < len && lower.Substring(i, 2) == "ay")
                {
                    sb.Append("ей");
                    i++;
                    continue;
                }
                // "ey"
                if (i + 1 < len && lower.Substring(i, 2) == "ey")
                {
                    sb.Append("ей");
                    i++;
                    continue;
                }
                // "ie" at end
                if (i + 1 < len && lower.Substring(i, 2) == "ie" && i + 2 >= len)
                {
                    sb.Append("и");
                    i++;
                    continue;
                }
                // "qu"
                if (i + 1 < len && lower.Substring(i, 2) == "qu")
                {
                    sb.Append("кв");
                    i++;
                    continue;
                }

                // Single character mappings
                switch (c)
                {
                    case 'a':
                        // "a" before consonant at end or followed by consonant
                        sb.Append("а");
                        break;
                    case 'b': sb.Append("б"); break;
                    case 'c':
                        // "c" before e/i/y = "с", otherwise "к"
                        if (i + 1 < len && (lower[i + 1] == 'e' || lower[i + 1] == 'i' || lower[i + 1] == 'y'))
                            sb.Append("с");
                        else
                            sb.Append("к");
                        break;
                    case 'd': sb.Append("д"); break;
                    case 'e':
                        // Silent "e" at end of word
                        if (i == len - 1 && len > 2)
                            break; // skip silent e
                        sb.Append("е");
                        break;
                    case 'f': sb.Append("ф"); break;
                    case 'g':
                        // "g" before e/i = "дж" in names, otherwise "г"
                        if (i + 1 < len && (lower[i + 1] == 'e' || lower[i + 1] == 'i'))
                            sb.Append("дж");
                        else
                            sb.Append("г");
                        break;
                    case 'h': sb.Append("х"); break;
                    case 'i': sb.Append("и"); break;
                    case 'j': sb.Append("дж"); break;
                    case 'k': sb.Append("к"); break;
                    case 'l': sb.Append("л"); break;
                    case 'm': sb.Append("м"); break;
                    case 'n': sb.Append("н"); break;
                    case 'o': sb.Append("о"); break;
                    case 'p': sb.Append("п"); break;
                    case 'r': sb.Append("р"); break;
                    case 's':
                        // "s" between vowels = "з"
                        if (i > 0 && i + 1 < len && IsVowel(lower[i - 1]) && IsVowel(lower[i + 1]))
                            sb.Append("з");
                        else
                            sb.Append("с");
                        break;
                    case 't': sb.Append("т"); break;
                    case 'u': sb.Append("у"); break;
                    case 'v': sb.Append("в"); break;
                    case 'w': sb.Append("у"); break;
                    case 'x': sb.Append("кс"); break;
                    case 'y':
                        // "y" as consonant at start, vowel otherwise
                        if (i == 0)
                            sb.Append("й");
                        else
                            sb.Append("и");
                        break;
                    case 'z': sb.Append("з"); break;
                    case '\'': sb.Append("'"); break;
                    case '-': sb.Append("-"); break;
                    // Accented characters common in European names
                    case '\u00E9': sb.Append("е"); break; // é
                    case '\u00E8': sb.Append("е"); break; // è
                    case '\u00E0': sb.Append("а"); break; // à
                    case '\u00E1': sb.Append("а"); break; // á
                    case '\u00F3': sb.Append("о"); break; // ó
                    case '\u00ED': sb.Append("и"); break; // í
                    case '\u00F1': sb.Append("нь"); break; // ñ
                    case '\u00FC': sb.Append("у"); break; // ü
                    case '\u00F6': sb.Append("ё"); break; // ö
                    case '\u00E4': sb.Append("э"); break; // ä
                    default:
                        sb.Append(c);
                        break;
                }
            }

            // Capitalize first letter if original was capitalized
            if (capitalize && sb.Length > 0)
            {
                sb[0] = char.ToUpper(sb[0]);
            }

            return sb.ToString();
        }

        private static bool IsVowel(char c)
        {
            return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u' || c == 'y';
        }
    }
}
