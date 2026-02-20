using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Microsoft.Win32;
using UnityEngine;
using UnityEngine.SceneManagement;
using I2.Loc;

namespace NotTonightRussian
{
    [BepInPlugin("com.nottonight.russianlocalization", "Not Tonight Russian", "1.1.0")]
    public class RussianLocPlugin : BaseUnityPlugin
    {
        private bool _injected = false;
        internal static Font CyrillicFont;

        void Awake()
        {
            Logger.LogInfo("Not Tonight Russian Localization plugin loaded v1.1.0");
            UILabel_Patch.Log = Logger;

            InstallFontToUserDir();
            FindCyrillicFont();

            // Apply Harmony patches
            try
            {
                var harmony = new Harmony("com.nottonight.russianlocalization");

                var original = typeof(UILabel).GetMethod("ProcessText",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);

                if (original != null)
                {
                    var prefix = typeof(UILabel_Patch).GetMethod("Prefix",
                        BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(original, new HarmonyMethod(prefix));
                    Logger.LogInfo("Patched UILabel.ProcessText()");
                }
                else
                {
                    Logger.LogWarning("UILabel.ProcessText() not found, trying other overloads...");
                    var methods = typeof(UILabel).GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var m in methods)
                    {
                        if (m.Name == "ProcessText" && m.DeclaringType == typeof(UILabel))
                        {
                            var prefix = typeof(UILabel_Patch).GetMethod("Prefix",
                                BindingFlags.Static | BindingFlags.Public);
                            harmony.Patch(m, new HarmonyMethod(prefix));
                            Logger.LogInfo("Patched first ProcessText overload");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Harmony patch error: " + ex.Message);
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void InstallFontToUserDir()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string srcFont = Path.Combine(pluginDir, "PressStart2P-Regular.ttf");
            if (!File.Exists(srcFont))
            {
                Logger.LogWarning("Source font not found: " + srcFont);
                return;
            }

            try
            {
                // Windows per-user fonts directory (no admin needed)
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string userFontsDir = Path.Combine(Path.Combine(localAppData, "Microsoft"), "Windows");
                userFontsDir = Path.Combine(userFontsDir, "Fonts");
                Directory.CreateDirectory(userFontsDir);

                string destFont = Path.Combine(userFontsDir, "PressStart2P-Regular.ttf");
                if (!File.Exists(destFont))
                {
                    File.Copy(srcFont, destFont, true);
                    Logger.LogInfo("Copied font to: " + destFont);

                    // Register in per-user font registry
                    using (var key = Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", true))
                    {
                        if (key != null)
                        {
                            key.SetValue("Press Start 2P (TrueType)", destFont);
                            Logger.LogInfo("Registered font in user registry");
                        }
                    }
                }
                else
                {
                    Logger.LogInfo("Font already installed in user fonts dir");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Font install error: " + ex.Message);
            }
        }

        Font TryLoadFont(string name)
        {
            Font f = Font.CreateDynamicFontFromOSFont(name, 16);
            if (f == null || !f.dynamic) return null;

            // Verify it's actually this font, not Arial fallback
            f.RequestCharactersInTexture("ТЙ", 16);
            CharacterInfo ci;
            f.GetCharacterInfo('Т', out ci, 16);

            Font arial = Font.CreateDynamicFontFromOSFont("Arial", 16);
            arial.RequestCharactersInTexture("Т", 16);
            CharacterInfo arialCi;
            arial.GetCharacterInfo('Т', out arialCi, 16);

            if (ci.advance != arialCi.advance)
            {
                Logger.LogInfo("Font '" + name + "': advance=" + ci.advance + " (Arial=" + arialCi.advance + ") - OK");
                return f;
            }

            Logger.LogInfo("Font '" + name + "': advance=" + ci.advance + " = Arial, skipping");
            return null;
        }

        void FindCyrillicFont()
        {
            // Try pixel fonts in preference order: LanaPixel (11px, best Cyrillic), Press Start 2P (8px)
            string[] candidates = new string[] {
                "LanaPixel",
                "Lana Pixel",
                "Press Start 2P"
            };

            foreach (string name in candidates)
            {
                Font f = TryLoadFont(name);
                if (f != null)
                {
                    CyrillicFont = f;
                    UnityEngine.Object.DontDestroyOnLoad(CyrillicFont);
                    Logger.LogInfo("Using pixel font: " + name);
                    return;
                }
            }

            // Fallback: Arial
            Logger.LogInfo("No pixel font found, using Arial");
            CyrillicFont = Font.CreateDynamicFontFromOSFont("Arial", 16);
            if (CyrillicFont != null)
                UnityEngine.Object.DontDestroyOnLoad(CyrillicFont);

            if (CyrillicFont == null)
                Logger.LogError("No Cyrillic font available!");
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Logger.LogInfo("Scene: " + scene.name);
            if (!_injected)
            {
                StartCoroutine(InjectWithDelay());
            }
            else
            {
                StartCoroutine(SwapFontsDelayed());
            }
        }

        IEnumerator SwapFontsDelayed()
        {
            yield return new WaitForSeconds(0.5f);
            SwapAllLabelFonts();
        }

        IEnumerator InjectWithDelay()
        {
            yield return new WaitForSeconds(1.0f);
            InjectTranslations();
            yield return null;
            SwapAllLabelFonts();
        }

        void SwapAllLabelFonts()
        {
            if (CyrillicFont == null)
            {
                Logger.LogWarning("No CyrillicFont to swap to");
                return;
            }

            var labels = UnityEngine.Object.FindObjectsOfType<UILabel>();
            Logger.LogInfo("Found " + labels.Length + " UILabels in scene");

            int swapped = 0;
            foreach (var label in labels)
            {
                try
                {
                    if (label.trueTypeFont != CyrillicFont)
                    {
                        int origSize = label.fontSize;
                        label.trueTypeFont = CyrillicFont;
                        label.fontSize = origSize;
                        swapped++;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Error swapping font on label: " + ex.Message);
                }
            }

            Logger.LogInfo("Swapped font on " + swapped + "/" + labels.Length + " labels");
        }

        void InjectTranslations()
        {
            if (_injected) return;

            try
            {
                var sources = LocalizationManager.Sources;
                if (sources == null || sources.Count == 0)
                {
                    Logger.LogWarning("No I2 LanguageSource found!");
                    return;
                }

                var translations = LoadTranslations();
                if (translations.Count == 0)
                {
                    Logger.LogError("No translations loaded!");
                    return;
                }

                Logger.LogInfo("Loaded " + translations.Count + " translations");

                int totalInjected = 0;
                foreach (var source in sources)
                {
                    int enIdx = source.GetLanguageIndex("English");
                    if (enIdx < 0) continue;

                    int injected = 0;
                    foreach (var term in source.mTerms)
                    {
                        if (term.Languages == null || enIdx >= term.Languages.Length)
                            continue;
                        string ru;
                        if (translations.TryGetValue(term.Term, out ru))
                        {
                            term.Languages[enIdx] = ru;
                            injected++;
                        }
                    }
                    Logger.LogInfo("Source (" + source.mTerms.Count + "): injected " + injected);
                    totalInjected += injected;
                }

                Logger.LogInfo("Total injected: " + totalInjected);
                LocalizationManager.LocalizeAll(true);
                _injected = true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Inject error: " + ex);
            }
        }

        Dictionary<string, string> LoadTranslations()
        {
            var translations = new Dictionary<string, string>();
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string filePath = Path.Combine(pluginDir, "translations.txt");
            if (!File.Exists(filePath))
                filePath = Path.Combine(Path.Combine(pluginDir, "NotTonightRussian"), "translations.txt");
            if (!File.Exists(filePath))
            {
                Logger.LogError("Translation file not found: " + filePath);
                return translations;
            }

            string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue;
                int sepIdx = line.IndexOf('=');
                if (sepIdx > 0)
                {
                    string key = line.Substring(0, sepIdx);
                    string value = line.Substring(sepIdx + 1);
                    value = value.Replace("\\n", "\n").Replace("\\t", "\t");
                    translations[key] = value;
                }
            }
            return translations;
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }

    // Separate patch class
    public static class UILabel_Patch
    {
        internal static ManualLogSource Log;

        // Dynamic text translations for strings not in I2 (venue names, map labels, etc.)
        private static readonly Dictionary<string, string> DynamicTranslations =
            new Dictionary<string, string>
        {
            // Venue titles
            {"The King's Head, Bampton, Devon", "Королевская Голова, Бэмптон, Девон"},
            {"Home, Block B, Flat 7", "Дом, Блок Б, Квартира 7"},
            // Map marker short names (game sends UPPERCASE, with/without trailing space)
            {"Kings Head", "Королевская Голова"},
            {"KINGS HEAD", "КОРОЛЕВСКАЯ ГОЛОВА"},
            // Map tab/section labels
            {"FLAT", "КВАРТИРА"},
            {"Flat", "Квартира"},
            // Owner names
            {"Dave Stobart", "Дэйв Стобарт"},
            {"DAVE STOBART", "ДЭЙВ СТОБАРТ"},
            // Story NPCs
            {"Harrison Pace", "Гаррисон Пейс"},
            {"Simon Tavener", "Саймон Тавенер"},
            {"Brian Prendegast", "Брайан Прендегаст"},
            {"Tarquin Futtock-Smythe", "Тарквин Фатток-Смайт"},
            {"Susan Kozlowska", "Сьюзан Козловска"},
            {"Officer Jupp", "Офицер Юпп"},
            // Gender on ID cards
            {"F", "Ж"},
            {"M", "М"},
            // Phone contacts — single names (game shows UPPERCASE)
            {"JUPP", "ЮПП"},
            {"DAVE", "ДЭЙВ"},
            {"FERRISS", "ФЕРРИС"},
            {"MYLARNA", "МИЛАРНА"},
            {"SHANNON", "ШЕННОН"},
            {"LUCILLE", "ЛЮСИЛЬ"},
            {"GALAHAD", "ГАЛАХАД"},
            {"GUINEVERE", "ГВИНЕВРА"},
            {"JONESY", "ДЖОНСИ"},
            {"BONESY", "БОНСИ"},
            {"FRANÇOIS", "ФРАНСУА"},
            {"TONI", "ТОНИ"},
            // Mixed-case variants
            {"Jupp", "Юпп"},
            {"Dave", "Дэйв"},
            {"Ferriss", "Феррис"},
            {"Mylarna", "Миларна"},
            {"Shannon", "Шеннон"},
            {"Lucille", "Люсиль"},
            {"Galahad", "Галахад"},
            {"Guinevere", "Гвиневра"},
            {"Jonesy", "Джонси"},
            {"Bonesy", "Бонси"},
            {"François", "Франсуа"},
            {"Toni", "Тони"},
            // Phone contact compound names
            {"DAVE@PUB", "ДЭЙВ@ПАБ"},
            // Venue names (job cards, map labels)
            {"BRITISH MUSEUM", "БРИТАНСКИЙ МУЗЕЙ"},
            {"British Museum", "Британский музей"},
            {"THE BRITISH MUSEUM", "БРИТАНСКИЙ МУЗЕЙ"},
            {"TIKI HEAD", "ТИКИ-ХЕД"},
            {"Tiki Head", "Тики-Хед"},
            {"TIKI HEAD DAY", "ТИКИ-ХЕД ДЕНЬ"},
            {"TIKI HEAD NIGHTS", "ТИКИ-ХЕД НОЧИ"},
            {"CLUB NEO", "КЛУБ НЕО"},
            {"Club Neo", "Клуб Нео"},
            {"FIRE AND ICE", "ОГОНЬ И ЛЁД"},
            {"Fire and Ice", "Огонь и Лёд"},
            {"FIRE & ICE", "ОГОНЬ И ЛЁД"},
            {"CLUB FERRISS", "КЛУБ ФЕРРИС"},
            {"Club Ferriss", "Клуб Феррис"},
            {"BOOZE BARGE", "БУХАЯ БАРЖА"},
            {"Booze Barge", "Бухая Баржа"},
            {"LE ROSBIF", "ЛЕ РОСБИФ"},
            {"Le Rosbif", "Ле Росбиф"},
            {"THE INDIE FEST", "ИНДИ-ФЕСТ"},
            {"The Indie Fest", "Инди-Фест"},
            {"INDIE FEST", "ИНДИ-ФЕСТ"},
            {"Indie Fest", "Инди-Фест"},
            {"AL FRESCO", "НА СВЕЖЕМ ВОЗДУХЕ"},
            // Music genre labels
            {"CLASSICAL", "КЛАССИКА"},
            {"Classical", "Классика"},
            {"ROCK", "РОК"},
            {"Rock", "Рок"},
            {"POP", "ПОП"},
            {"Pop", "Поп"},
            {"INDIE", "ИНДИ"},
            {"Indie", "Инди"},
            {"ELECTRONIC", "ЭЛЕКТРОНИКА"},
            {"Electronic", "Электроника"},
            {"HIP HOP", "ХИП-ХОП"},
            {"Hip Hop", "Хип-хоп"},
            {"DANCE", "ДЭНС"},
            {"Dance", "Дэнс"},
            // Compound NPC references
            {"Jupp Security", "Безопасность Юппа"},
            {"JUPP SECURITY", "БЕЗОПАСНОСТЬ ЮППА"},
            {"HARRISON PACE", "ГАРРИСОН ПЕЙС"},
            // UI labels that overflow their widgets
            {"ОБЩАЯ ОЧЕРЕДЬ", "ОБЩ. ОЧЕРЕДЬ"},
        };

        // Day abbreviation fixes: game truncates "Среда" to "СРЕ" (3 chars) — replace with 2-char
        private static readonly Dictionary<string, string> DayAbbrevFix =
            new Dictionary<string, string>
        {
            {"ПОН", "ПН"}, {"ВТО", "ВТ"}, {"СРЕ", "СР"},
            {"ЧЕТ", "ЧТ"}, {"ПЯТ", "ПТ"}, {"СУБ", "СБ"}, {"ВОС", "ВС"},
        };

        // Month name/abbreviation -> number for date reformatting
        private static readonly Dictionary<string, string> MonthMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Full English
            {"January","01"},{"February","02"},{"March","03"},{"April","04"},
            {"May","05"},{"June","06"},{"July","07"},{"August","08"},
            {"September","09"},{"October","10"},{"November","11"},{"December","12"},
            // Full Russian
            {"Январь","01"},{"Февраль","02"},{"Март","03"},{"Апрель","04"},
            {"Май","05"},{"Июнь","06"},{"Июль","07"},{"Август","08"},
            {"Сентябрь","09"},{"Октябрь","10"},{"Ноябрь","11"},{"Декабрь","12"},
            // Abbreviated Russian (game truncates to 3 chars)
            {"ЯНВ","01"},{"ФЕВ","02"},{"МАР","03"},{"АПР","04"},
            {"ИЮН","06"},{"ИЮЛ","07"},{"АВГ","08"},{"СЕН","09"},
            {"ОКТ","10"},{"НОЯ","11"},{"ДЕК","12"},
            // Abbreviated English
            {"JAN","01"},{"FEB","02"},{"MAR","03"},{"APR","04"},
            {"JUN","06"},{"JUL","07"},{"AUG","08"},{"SEP","09"},
            {"OCT","10"},{"NOV","11"},{"DEC","12"},
        };

        // Reformat "Nth MonthName YYYY" -> "DD.MM.YYYY"
        private static string ReformatNamedDate(string text)
        {
            if (text == null || text.Length < 6) return text;

            foreach (var kv in MonthMap)
            {
                int mi = text.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase);
                if (mi < 0) continue;
                // Avoid partial match: check word boundary before month name
                if (mi > 0 && char.IsLetter(text[mi - 1])) continue;
                // Check word boundary after month name
                int afterName = mi + kv.Key.Length;
                if (afterName < text.Length && char.IsLetter(text[afterName])) continue;

                // Find year after month
                int ys = afterName;
                while (ys < text.Length && text[ys] == ' ') ys++;
                if (ys + 4 > text.Length) continue;
                string yearStr = text.Substring(ys, 4);
                int year;
                if (!int.TryParse(yearStr, out year) || year < 1900 || year > 2100) continue;
                if (ys + 4 < text.Length && char.IsDigit(text[ys + 4])) continue;

                // Find day before month
                int p = mi - 1;
                while (p >= 0 && (text[p] == ' ' || text[p] == ',')) p--;
                // Skip ordinal suffixes
                if (p >= 1)
                {
                    string suf = text.Substring(p - 1, 2).ToLower();
                    if (suf == "st" || suf == "nd" || suf == "rd" || suf == "th")
                        p -= 2;
                }
                int dayEnd = p + 1;
                while (p >= 0 && char.IsDigit(text[p])) p--;
                int dayStart = p + 1;

                if (dayStart < dayEnd)
                {
                    int day;
                    if (int.TryParse(text.Substring(dayStart, dayEnd - dayStart), out day) && day >= 1 && day <= 31)
                    {
                        string fmt = day.ToString("D2") + "." + kv.Value + "." + yearStr;
                        text = text.Substring(0, dayStart) + fmt + text.Substring(ys + 4);
                        return text;
                    }
                }

                // No day found - month+year only (expiry dates like "ИЮН 2019")
                string fmtNoDay = kv.Value + "." + yearStr;
                text = text.Substring(0, mi) + fmtNoDay + text.Substring(ys + 4);
                return text;
            }
            return text;
        }

        // Reformat numeric dates: "22,2,2000" -> "22.02.2000", "2.1.18" -> "02.01.18"
        private static string ReformatNumericDate(string text)
        {
            if (text == null || text.Length < 5) return text;

            for (int i = 0; i <= text.Length - 5; i++)
            {
                if (!char.IsDigit(text[i])) continue;
                if (i > 0 && char.IsDigit(text[i - 1])) continue;

                // Read day
                int de = i + 1;
                while (de < text.Length && char.IsDigit(text[de])) de++;
                int day;
                if (!int.TryParse(text.Substring(i, de - i), out day) || day < 1 || day > 31) continue;

                // Separator
                if (de >= text.Length) continue;
                char s1 = text[de];
                if (s1 != ',' && s1 != '.') continue;

                // Read month
                int ms = de + 1;
                int me = ms;
                while (me < text.Length && char.IsDigit(text[me])) me++;
                int month;
                if (!int.TryParse(text.Substring(ms, me - ms), out month) || month < 1 || month > 12) continue;

                // Second separator
                if (me >= text.Length) continue;
                char s2 = text[me];
                if (s2 != ',' && s2 != '.') continue;

                // Read year
                int yrs = me + 1;
                int yre = yrs;
                while (yre < text.Length && char.IsDigit(text[yre])) yre++;
                int yLen = yre - yrs;
                if (yLen != 2 && yLen != 4) continue;
                if (yre < text.Length && char.IsDigit(text[yre])) continue;
                string yearStr = text.Substring(yrs, yLen);

                string fmt = day.ToString("D2") + "." + month.ToString("D2") + "." + yearStr;
                text = text.Substring(0, i) + fmt + text.Substring(yre);
                i += fmt.Length - 1;
            }
            return text;
        }

        public static void Prefix(UILabel __instance)
        {
            if (RussianLocPlugin.CyrillicFont == null) return;

            // Dynamic text replacement (runs every ProcessText call)
            if (__instance.text != null && __instance.text.Length > 0)
            {
                string replacement;
                if (DynamicTranslations.TryGetValue(__instance.text, out replacement))
                    __instance.text = replacement;
                else
                {
                    // Retry with trimmed text (game sometimes sends trailing spaces)
                    string trimmed = __instance.text.Trim();
                    if (trimmed != __instance.text && DynamicTranslations.TryGetValue(trimmed, out replacement))
                        __instance.text = replacement;
                    else
                    {
                        // Reformat dates: "2nd January 2018" -> "02.01.2018"
                        string after = ReformatNamedDate(__instance.text);
                        // Reformat numeric: "22,2,2000" -> "22.02.2000"
                        after = ReformatNumericDate(after);
                        if (after != __instance.text)
                            __instance.text = after;

                        // Fix 3-char day abbreviations (game truncates "Среда"→"СРЕ")
                        if (__instance.text.Length >= 4 && !char.IsLetter(__instance.text[3]))
                        {
                            string dayRepl;
                            if (DayAbbrevFix.TryGetValue(__instance.text.Substring(0, 3), out dayRepl))
                                __instance.text = dayRepl + __instance.text.Substring(3);
                        }

                        // Transliterate English names to Cyrillic
                        string nameResult;
                        if (NameTransliterator.TryTransliterate(__instance.text, out nameResult))
                            __instance.text = nameResult;
                    }
                }
            }

            if (__instance.trueTypeFont != RussianLocPlugin.CyrillicFont)
            {
                __instance.trueTypeFont = RussianLocPlugin.CyrillicFont;

                int origSize = __instance.fontSize;
                string snippet = __instance.text != null
                    ? __instance.text.Replace("\n", "\\n")
                    : "";
                if (snippet.Length > 40) snippet = snippet.Substring(0, 40);

                // Cinematic titles (sz=100, huge widgets)
                if (origSize >= 80)
                    __instance.fontSize = 40;

                // Dialog speech bubbles (MsgObjectInput clones, sz=60)
                // \n pushes text below top clip edge; negative spacing compresses the blank line
                if (__instance.gameObject.name.Contains("MsgObject"))
                {
                    if (__instance.fontSize > 22)
                        __instance.fontSize = 22;
                    __instance.useFloatSpacing = true;
                    __instance.floatSpacingY = 0f;
                    if (__instance.text != null && __instance.text.Length > 0 && __instance.text[0] != '\n')
                        __instance.text = "\n" + __instance.text;
                }
                // Radio message (RadioMsg, origSz=60)
                else if (__instance.gameObject.name.Contains("RadioMsg"))
                {
                    if (__instance.fontSize > 28)
                        __instance.fontSize = 28;
                    __instance.useFloatSpacing = true;
                    __instance.floatSpacingY = 8f;
                    if (__instance.text != null && __instance.text.Length > 0 && __instance.text[0] != '\n')
                        __instance.text = "\n" + __instance.text;
                }
                else
                {
                    // Cap labels with inflated fontSize designed for bitmap font
                    if (__instance.fontSize > 40)
                        __instance.fontSize = 32;

                    // Cinematic text spacingY
                    if (origSize >= 80)
                        __instance.spacingY = 8;
                    else if (__instance.height > 50 && __instance.spacingY < 4)
                        __instance.spacingY = 4;
                }

                if (Log != null)
                    Log.LogInfo("FontPatch: " + __instance.gameObject.name
                        + " origSz=" + origSize + " newSz=" + __instance.fontSize
                        + " ov=" + __instance.overflowMethod
                        + " w=" + __instance.width + " h=" + __instance.height
                        + " spY=" + __instance.spacingY
                        + " [" + snippet + "]");
            }
        }
    }
}
