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
    [BepInPlugin("com.nottonight.russianlocalization", "Not Tonight Russian", "1.7.0")]
    public class RussianLocPlugin : BaseUnityPlugin
    {
        // Win32: register font for current session
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
        private const uint WM_FONTCHANGE = 0x001D;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        private bool _injected = false;
        internal static Font CyrillicFont;

        // Diagnostic log
        private static StringBuilder _diag = new StringBuilder();
        private static string _diagPath;

        internal static void DiagLog(string msg)
        {
            _diag.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg);
        }

        internal static void DiagFlush()
        {
            if (_diagPath != null && _diag.Length > 0)
            {
                try { File.WriteAllText(_diagPath, _diag.ToString(), Encoding.UTF8); }
                catch { }
            }
        }

        void Awake()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location);
            _diagPath = Path.Combine(pluginDir, "NotTonightRussian_diag.txt");

            Logger.LogInfo("Not Tonight Russian v1.6.1");
            DiagLog("=== Not Tonight Russian v1.6.1 ===");
            DiagLog("Plugin dir: " + pluginDir);
            UILabel_Patch.Log = Logger;

            InstallFonts();
            FindCyrillicFont();
            DiagFlush();

            // Apply Harmony patch — ONLY Prefix, NO Postfix (same as v1.0.0)
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
                    DiagLog("Harmony: Patched UILabel.ProcessText() (prefix only)");
                }
            }
            catch (Exception ex)
            {
                DiagLog("Harmony ERROR: " + ex.Message);
            }

            DiagFlush();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // Font files to install
        private static readonly string[][] FontsToInstall = new string[][] {
            new string[] { "LanaPixel.ttf", "LanaPixel (TrueType)" },
            new string[] { "PressStart2P-Regular.ttf", "Press Start 2P (TrueType)" },
        };

        void InstallFonts()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location);
            bool anyRegistered = false;

            // Try to install to system fonts dir (C:\Windows\Fonts) — requires admin
            try
            {
                string winDir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
                string systemFontsDir = Path.Combine(winDir, "Fonts");
                foreach (var font in FontsToInstall)
                {
                    string srcFont = Path.Combine(pluginDir, font[0]);
                    if (!File.Exists(srcFont)) continue;
                    string destFont = Path.Combine(systemFontsDir, font[0]);
                    if (!File.Exists(destFont))
                    {
                        File.Copy(srcFont, destFont, true);
                        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", true))
                        { if (key != null) key.SetValue(font[1], font[0]); }
                        DiagLog("Installed font to system: " + destFont);
                    }
                    else
                    {
                        DiagLog("Font already in system dir: " + destFont);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog("System font install failed (no admin?): " + ex.Message);
                DiagLog("HINT: Right-click font files in plugin folder -> 'Install for all users', then restart the game");
            }

            // Register fonts for current session
            foreach (var font in FontsToInstall)
            {
                string srcFont = Path.Combine(pluginDir, font[0]);
                if (File.Exists(srcFont))
                {
                    try
                    {
                        int result = AddFontResourceEx(srcFont, 0, IntPtr.Zero);
                        DiagLog("AddFontResourceEx(" + font[0] + ") = " + result);
                        if (result > 0) anyRegistered = true;
                    }
                    catch (Exception ex) { DiagLog("AddFontResourceEx FAILED: " + ex.Message); }
                }
            }
            if (anyRegistered)
            {
                try
                {
                    IntPtr res;
                    SendMessageTimeout(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out res);
                    DiagLog("WM_FONTCHANGE sent (timeout=1s)");
                }
                catch { }
            }
        }

        Font TryLoadFont(string name)
        {
            Font f = Font.CreateDynamicFontFromOSFont(name, 16);
            if (f == null || !f.dynamic) return null;
            f.RequestCharactersInTexture("ТЙ", 16);
            CharacterInfo ci;
            f.GetCharacterInfo('Т', out ci, 16);
            Font arial = Font.CreateDynamicFontFromOSFont("Arial", 16);
            arial.RequestCharactersInTexture("Т", 16);
            CharacterInfo arialCi;
            arial.GetCharacterInfo('Т', out arialCi, 16);
            if (ci.advance != arialCi.advance)
            {
                DiagLog("TryLoadFont('" + name + "'): advance=" + ci.advance + " (Arial=" + arialCi.advance + ") -> OK");
                return f;
            }
            // Font found by name but metrics match Arial — trust if Unity returned correct name
            if (f.name == name)
            {
                DiagLog("TryLoadFont('" + name + "'): metrics match Arial but font.name OK -> TRUSTING");
                return f;
            }
            DiagLog("TryLoadFont('" + name + "'): same as Arial -> REJECTED");
            return null;
        }

        Font TryLoadFontByPath(string filePath, string displayName)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                Font f = Font.CreateDynamicFontFromOSFont(filePath, 16);
                if (f == null || !f.dynamic) return null;
                f.RequestCharactersInTexture("ТЙ", 16);
                CharacterInfo ci; f.GetCharacterInfo('Т', out ci, 16);
                Font arial = Font.CreateDynamicFontFromOSFont("Arial", 16);
                arial.RequestCharactersInTexture("Т", 16);
                CharacterInfo arialCi; arial.GetCharacterInfo('Т', out arialCi, 16);
                if (ci.advance != arialCi.advance)
                {
                    DiagLog("TryLoadFontByPath('" + displayName + "'): OK");
                    return f;
                }
            }
            catch { }
            return null;
        }

        void FindCyrillicFont()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string[] candidates = new string[] { "LanaPixel", "Lana Pixel", "Press Start 2P" };
            foreach (string name in candidates)
            {
                Font f = TryLoadFont(name);
                if (f != null)
                {
                    CyrillicFont = f;
                    UnityEngine.Object.DontDestroyOnLoad(CyrillicFont);
                    DiagLog("RESULT: Using '" + name + "'");
                    return;
                }
            }
            // Try by path
            foreach (string fn in new[] { "LanaPixel.ttf", "PressStart2P-Regular.ttf" })
            {
                Font f = TryLoadFontByPath(Path.Combine(pluginDir, fn), fn);
                if (f != null)
                {
                    CyrillicFont = f;
                    UnityEngine.Object.DontDestroyOnLoad(CyrillicFont);
                    DiagLog("RESULT: Using '" + fn + "' (by path)");
                    return;
                }
            }
            // Fallback: Arial
            CyrillicFont = Font.CreateDynamicFontFromOSFont("Arial", 16);
            if (CyrillicFont != null) UnityEngine.Object.DontDestroyOnLoad(CyrillicFont);
            DiagLog("RESULT: Using Arial fallback");
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            DiagLog("Scene loaded: " + scene.name);
            DiagFlush();
            if (!_injected) StartCoroutine(InjectWithDelay());
            else StartCoroutine(SwapFontsDelayed());
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
            if (CyrillicFont == null) return;
            var labels = UnityEngine.Object.FindObjectsOfType<UILabel>();
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
                catch { }
            }
            DiagLog("SwapAllLabelFonts: " + swapped + "/" + labels.Length);
            DiagFlush();
        }

        void InjectTranslations()
        {
            if (_injected) return;
            try
            {
                var sources = LocalizationManager.Sources;
                if (sources == null || sources.Count == 0) return;
                var translations = LoadTranslations();
                if (translations.Count == 0) return;
                int totalInjected = 0;
                foreach (var source in sources)
                {
                    int enIdx = source.GetLanguageIndex("English");
                    if (enIdx < 0) continue;
                    int injected = 0;
                    foreach (var term in source.mTerms)
                    {
                        if (term.Languages == null || enIdx >= term.Languages.Length) continue;
                        string ru;
                        if (translations.TryGetValue(term.Term, out ru))
                        {
                            term.Languages[enIdx] = ru;
                            injected++;
                        }
                    }
                    totalInjected += injected;
                }
                DiagLog("Injected " + totalInjected + " translations");
                LocalizationManager.LocalizeAll(true);
                _injected = true;
            }
            catch (Exception ex) { DiagLog("Inject error: " + ex); }
        }

        Dictionary<string, string> LoadTranslations()
        {
            var translations = new Dictionary<string, string>();
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string filePath = Path.Combine(pluginDir, "translations.txt");
            if (!File.Exists(filePath))
                filePath = Path.Combine(Path.Combine(pluginDir, "NotTonightRussian"), "translations.txt");
            if (!File.Exists(filePath)) return translations;
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

        void OnDestroy() { SceneManager.sceneLoaded -= OnSceneLoaded; }
    }

    public static class UILabel_Patch
    {
        internal static ManualLogSource Log;

        // Dynamic translations
        private static readonly Dictionary<string, string> DynamicTranslations =
            new Dictionary<string, string>
        {
            {"The King's Head, Bampton, Devon", "Королевская Голова, Бэмптон, Девон"},
            {"Home, Block B, Flat 7", "Дом, Блок Б, Квартира 7"},
            {"Kings Head", "Королевская Голова"},
            {"KINGS HEAD", "КОРОЛЕВСКАЯ ГОЛОВА"},
            {"FLAT", "КВАРТИРА"}, {"Flat", "Квартира"},
            {"Dave Stobart", "Дэйв Стобарт"}, {"DAVE STOBART", "ДЭЙВ СТОБАРТ"},
            {"Harrison Pace", "Гаррисон Пейс"}, {"Simon Tavener", "Саймон Тавенер"},
            {"Brian Prendegast", "Брайан Прендегаст"},
            {"Tarquin Futtock-Smythe", "Тарквин Фатток-Смайт"},
            {"Susan Kozlowska", "Сьюзан Козловска"}, {"Officer Jupp", "Офицер Юпп"},
            {"F", "Ж"}, {"M", "М"},
            {"JUPP", "ЮПП"}, {"DAVE", "ДЭЙВ"}, {"FERRISS", "ФЕРРИС"},
            {"MYLARNA", "МИЛАРНА"}, {"SHANNON", "ШЕННОН"}, {"LUCILLE", "ЛЮСИЛЬ"},
            {"GALAHAD", "ГАЛАХАД"}, {"GUINEVERE", "ГВИНЕВРА"},
            {"JONESY", "ДЖОНСИ"}, {"BONESY", "БОНСИ"},
            {"FRANÇOIS", "ФРАНСУА"}, {"TONI", "ТОНИ"},
            {"Jupp", "Юпп"}, {"Dave", "Дэйв"}, {"Ferriss", "Феррис"},
            {"Mylarna", "Миларна"}, {"Shannon", "Шеннон"}, {"Lucille", "Люсиль"},
            {"Galahad", "Галахад"}, {"Guinevere", "Гвиневра"},
            {"Jonesy", "Джонси"}, {"Bonesy", "Бонси"},
            {"François", "Франсуа"}, {"Toni", "Тони"},
            {"DAVE@PUB", "ДЭЙВ@ПАБ"},
            {"BRITISH MUSEUM", "БРИТАНСКИЙ МУЗЕЙ"}, {"British Museum", "Британский музей"},
            {"THE BRITISH MUSEUM", "БРИТАНСКИЙ МУЗЕЙ"},
            {"TIKI HEAD", "ТИКИ-ХЕД"}, {"Tiki Head", "Тики-Хед"},
            {"TIKI HEAD DAY", "ТИКИ-ХЕД ДЕНЬ"}, {"TIKI HEAD NIGHTS", "ТИКИ-ХЕД НОЧИ"},
            {"CLUB NEO", "КЛУБ НЕО"}, {"Club Neo", "Клуб Нео"}, {"NEO", "НЕО"}, {"Neo", "Нео"},
            {"FIRE AND ICE", "ОГОНЬ И ЛЁД"}, {"Fire and Ice", "Огонь и Лёд"},
            {"FIRE & ICE", "ОГОНЬ И ЛЁД"},
            {"CLUB FERRISS", "КЛУБ ФЕРРИС"}, {"Club Ferriss", "Клуб Феррис"},
            {"BOOZE BARGE", "БУХАЯ БАРЖА"}, {"Booze Barge", "Бухая Баржа"},
            {"LE ROSBIF", "ЛЕ РОСБИФ"}, {"Le Rosbif", "Ле Росбиф"},
            {"THE INDIE FEST", "ИНДИ-ФЕСТ"}, {"The Indie Fest", "Инди-Фест"},
            {"INDIE FEST", "ИНДИ-ФЕСТ"}, {"Indie Fest", "Инди-Фест"},
            {"AL FRESCO", "НА СВЕЖЕМ ВОЗДУХЕ"},
            {"CLASSICAL", "КЛАССИКА"}, {"Classical", "Классика"},
            {"ROCK", "РОК"}, {"Rock", "Рок"},
            {"POP", "ПОП"}, {"Pop", "Поп"},
            {"INDIE", "ИНДИ"}, {"Indie", "Инди"},
            {"ELECTRONIC", "ЭЛЕКТРОНИКА"}, {"Electronic", "Электроника"},
            {"HIP HOP", "ХИП-ХОП"}, {"Hip Hop", "Хип-хоп"},
            {"DANCE", "ДЭНС"}, {"Dance", "Дэнс"},
            {"Dance hits", "Танц. хиты"}, {"DANCE HITS", "ТАНЦ. ХИТЫ"},
            {"Танцевальные хиты", "Танц. хиты"}, {"ТАНЦЕВАЛЬНЫЕ ХИТЫ", "ТАНЦ. ХИТЫ"},
            {"Jupp Security", "Безопасность Юппа"}, {"JUPP SECURITY", "БЕЗОПАСНОСТЬ ЮППА"},
            {"HARRISON PACE", "ГАРРИСОН ПЕЙС"},
            {"ОБЩАЯ ОЧЕРЕДЬ", "ОБЩ. ОЧЕРЕДЬ"},
            // Map venue location subtitles (NightclubName, NightclubLocation)
            {"Club Neo, Exeter, Devon", "Клуб Нео, Эксетер, Девон"},
            {"Fire and Ice, Exeter, Devon", "Огонь и Лёд, Эксетер, Девон"},
            {"The Tiki Head, Exeter, Devon", "Тики-Хед, Эксетер, Девон"},
            {"The King's Head, Exeter, Devon", "Королевская Голова, Эксетер, Девон"},
            {"King's Head, Bampton, Devon", "Королевская Голова, Бэмптон, Девон"},
            {"The King's Head 3, Bampton, Devon", "Королевская Голова, Бэмптон, Девон"},
            {"The King's Head?, Bampton, Devon", "Королевская Голова?, Бэмптон, Девон"},
            {"The Kings Head 3, Bampton, Devon", "Королевская Голова, Бэмптон, Девон"},
            {"The Kings Head 4, Bampton, Devon", "Королевская Голова, Бэмптон, Девон"},
            {"Angels, Exeter, Devon", "Ангелы, Эксетер, Девон"},
            {"Carpark Popup, Exeter, Devon", "Парковка-попап, Эксетер, Девон"},
            {"British Museum, London, England", "Британский музей, Лондон, Англия"},
            {"Casino, Exeter, Devon", "Казино, Эксетер, Девон"},
            {"The Gammon, Bristol, Somerset", "Гэммон, Бристоль, Сомерсет"},
            {"The Indie Festival, Glasto, Somerset", "Инди-фест, Гласто, Сомерсет"},
            {"The Indie festival, Glasto, Somerset", "Инди-фест, Гласто, Сомерсет"},
            {"Festival of Rock, Glasto, Somerset", "Рок-фест, Гласто, Сомерсет"},
            {"Cheese and Chunes Fest, Glasto, Somerset", "Фест «Сыр и Мелодии», Гласто, Сомерсет"},
            {"The London Wall, London, England", "Лондонская стена, Лондон, Англия"},
            {"The Dover border, Dover", "Границa в Дувре, Дувр"},
            {"Le RosBif, Toulouse", "Ле Росбиф, Тулуза"},
            {"Le Coq Café, Toulouse", "Le Coq Café, Тулуза"},
            {"Le Petit Bateau, Toulouse", "Le Petit Bateau, Тулуза"},
            {"Ferriss' apartment, Toulouse", "Квартира Ферриса, Тулуза"},
            {"JULY BALL, Exeter, Devon", "ИЮЛЬСКИЙ БАЛ, Эксетер, Девон"},
            {"January Ball, Exeter, Devon", "Январский бал, Эксетер, Девон"},
            {"October Ball, Exeter, Devon", "Октябрьский бал, Эксетер, Девон"},
            {"Spring Ball, Exeter, Devon", "Весенний бал, Эксетер, Девон"},
            {"The Election, Wincanton, Somerset", "Выборы, Уинкантон, Сомерсет"},
            {"The Election, Wincanton, England", "Выборы, Уинкантон, Англия"},
            {"Town hall, Exeter, Devon", "Ратуша, Эксетер, Девон"},
            {"Home, Block B, Flat 7, Exeter, Devon", "Дом, Блок Б, Кв. 7, Эксетер, Девон"},
            {"Europeans Citizens Relocation Block C, Yeovil, Somerset", "Блок переселения евро-граждан C, Йовил, Сомерсет"},
        };

        private static readonly Dictionary<string, string> DayAbbrevFix =
            new Dictionary<string, string>
        {
            {"ПОН", "ПН"}, {"ВТО", "ВТ"}, {"СРЕ", "СР"},
            {"ЧЕТ", "ЧТ"}, {"ПЯТ", "ПТ"}, {"СУБ", "СБ"}, {"ВОС", "ВС"},
        };

        private static readonly Dictionary<string, string> MonthMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"January","01"},{"February","02"},{"March","03"},{"April","04"},
            {"May","05"},{"June","06"},{"July","07"},{"August","08"},
            {"September","09"},{"October","10"},{"November","11"},{"December","12"},
            {"Январь","01"},{"Февраль","02"},{"Март","03"},{"Апрель","04"},
            {"Май","05"},{"Июнь","06"},{"Июль","07"},{"Август","08"},
            {"Сентябрь","09"},{"Октябрь","10"},{"Ноябрь","11"},{"Декабрь","12"},
            {"ЯНВ","01"},{"ФЕВ","02"},{"МАР","03"},{"АПР","04"},
            {"ИЮН","06"},{"ИЮЛ","07"},{"АВГ","08"},{"СЕН","09"},
            {"ОКТ","10"},{"НОЯ","11"},{"ДЕК","12"},
            {"JAN","01"},{"FEB","02"},{"MAR","03"},{"APR","04"},
            {"JUN","06"},{"JUL","07"},{"AUG","08"},{"SEP","09"},
            {"OCT","10"},{"NOV","11"},{"DEC","12"},
        };

        private static string ReformatNamedDate(string text)
        {
            if (text == null || text.Length < 6) return text;
            foreach (var kv in MonthMap)
            {
                int mi = text.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase);
                if (mi < 0) continue;
                if (mi > 0 && char.IsLetter(text[mi - 1])) continue;
                int afterName = mi + kv.Key.Length;
                if (afterName < text.Length && char.IsLetter(text[afterName])) continue;
                int ys = afterName;
                while (ys < text.Length && text[ys] == ' ') ys++;
                if (ys + 4 > text.Length) continue;
                string yearStr = text.Substring(ys, 4);
                int year;
                if (!int.TryParse(yearStr, out year) || year < 1900 || year > 2100) continue;
                if (ys + 4 < text.Length && char.IsDigit(text[ys + 4])) continue;
                int p = mi - 1;
                while (p >= 0 && (text[p] == ' ' || text[p] == ',')) p--;
                if (p >= 1)
                {
                    string suf = text.Substring(p - 1, 2).ToLower();
                    if (suf == "st" || suf == "nd" || suf == "rd" || suf == "th") p -= 2;
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
                        return text.Substring(0, dayStart) + fmt + text.Substring(ys + 4);
                    }
                }
                string fmtNoDay = kv.Value + "." + yearStr;
                return text.Substring(0, mi) + fmtNoDay + text.Substring(ys + 4);
            }
            return text;
        }

        private static string ReformatNumericDate(string text)
        {
            if (text == null || text.Length < 5) return text;
            for (int i = 0; i <= text.Length - 5; i++)
            {
                if (!char.IsDigit(text[i])) continue;
                if (i > 0 && char.IsDigit(text[i - 1])) continue;
                int de = i + 1;
                while (de < text.Length && char.IsDigit(text[de])) de++;
                int day; if (!int.TryParse(text.Substring(i, de - i), out day) || day < 1 || day > 31) continue;
                if (de >= text.Length) continue;
                char s1 = text[de]; if (s1 != ',' && s1 != '.') continue;
                int ms = de + 1; int me = ms;
                while (me < text.Length && char.IsDigit(text[me])) me++;
                int month; if (!int.TryParse(text.Substring(ms, me - ms), out month) || month < 1 || month > 12) continue;
                if (me >= text.Length) continue;
                char s2 = text[me]; if (s2 != ',' && s2 != '.') continue;
                int yrs = me + 1; int yre = yrs;
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

        // === EXACT v1.0.0 Prefix logic + diagnostics ===
        public static void Prefix(UILabel __instance)
        {
            if (RussianLocPlugin.CyrillicFont == null) return;

            // Dynamic text replacement
            if (__instance.text != null && __instance.text.Length > 0)
            {
                string replacement;
                if (DynamicTranslations.TryGetValue(__instance.text, out replacement))
                    __instance.text = replacement;
                else
                {
                    string trimmed = __instance.text.Trim();
                    if (trimmed != __instance.text && DynamicTranslations.TryGetValue(trimmed, out replacement))
                        __instance.text = replacement;
                    else
                    {
                        string after = ReformatNamedDate(__instance.text);
                        after = ReformatNumericDate(after);
                        if (after != __instance.text) __instance.text = after;

                        if (__instance.text.Length >= 4 && !char.IsLetter(__instance.text[3]))
                        {
                            string dayRepl;
                            if (DayAbbrevFix.TryGetValue(__instance.text.Substring(0, 3), out dayRepl))
                                __instance.text = dayRepl + __instance.text.Substring(3);
                        }

                        string nameResult;
                        if (NameTransliterator.TryTransliterate(__instance.text, out nameResult))
                            __instance.text = nameResult;
                    }
                }
            }

            // Font swap — EXACT v1.0.0 logic (simple property setter)
            if (__instance.trueTypeFont != RussianLocPlugin.CyrillicFont)
            {
                __instance.trueTypeFont = RussianLocPlugin.CyrillicFont;

                int origSize = __instance.fontSize;

                // Dialog speech bubbles
                if (__instance.gameObject.name.Contains("MsgObject"))
                {
                    if (__instance.fontSize > 22)
                        __instance.fontSize = 22;
                    __instance.useFloatSpacing = true;
                    __instance.floatSpacingY = 0f;
                    if (__instance.text != null && __instance.text.Length > 0 && __instance.text[0] != '\n')
                        __instance.text = "\n" + __instance.text;
                }
                // Radio messages
                else if (__instance.gameObject.name.Contains("RadioMsg"))
                {
                    if (__instance.fontSize > 28)
                        __instance.fontSize = 28;
                    __instance.useFloatSpacing = true;
                    __instance.floatSpacingY = 8f;
                    if (__instance.text != null && __instance.text.Length > 0 && __instance.text[0] != '\n')
                        __instance.text = "\n" + __instance.text;
                }
                // Cinematic titles
                else if (origSize >= 80)
                {
                    __instance.fontSize = 40;
                    __instance.spacingY = 8;
                }
                // General labels with bitmap font sizing
                else if (__instance.fontSize > 40)
                {
                    __instance.fontSize = 32;
                }
                // No spacingY changes for general labels — causes overlap

                // ID card name: shift down a few pixels so tall Cyrillic letters (Ш, Ф) don't clip at top
                if (__instance.text != null && __instance.text.Contains("\n")
                    && __instance.GetComponentInParent<ClubberID>() != null)
                {
                    Vector3 pos = __instance.transform.localPosition;
                    pos.y -= 9f;
                    __instance.transform.localPosition = pos;
                }
            }

            // ID card name: ensure small line spacing (runs every ProcessText call)
            if (__instance.text != null && __instance.text.Contains("\n")
                && __instance.GetComponentInParent<ClubberID>() != null)
            {
                __instance.spacingY = -5;
            }
        }
    }
}
