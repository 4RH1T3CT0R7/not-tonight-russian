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
    [BepInPlugin("com.nottonight.russianlocalization", "Not Tonight Russian", "1.4.1")]
    public class RussianLocPlugin : BaseUnityPlugin
    {
        // Win32: register font for current session (available immediately, no restart needed)
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

        // Win32: broadcast font change to all windows so Unity picks up newly registered fonts
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_FONTCHANGE = 0x001D;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        private bool _injected = false;
        internal static Font CyrillicFont;
        internal static bool IsPixelFont = false; // true if LanaPixel/PressStart2P, false if Arial fallback

        // Reflection: direct access to UILabel.mFont (bitmap font backing field)
        // NGUI's bitmapFont property setter may not clear mFont when trueTypeFont is set,
        // causing bitmap font (ThinPixel_60) to take priority over trueTypeFont (Arial).
        // We use reflection to force-clear it.
        internal static FieldInfo MFontField;
        private static bool _mFontFieldResolved = false;

        // Diagnostic log for remote debugging (users can share this file)
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

            Logger.LogInfo("Not Tonight Russian Localization plugin loaded v1.4.1");
            DiagLog("=== Not Tonight Russian v1.4.1 ===");
            DiagLog("Plugin dir: " + pluginDir);
            UILabel_Patch.Log = Logger;

            InstallFontToUserDir();
            FindCyrillicFont();
            DiagFlush();

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
                    DiagLog("Harmony: Patched UILabel.ProcessText()");
                }
                else
                {
                    Logger.LogWarning("UILabel.ProcessText() not found, trying other overloads...");
                    DiagLog("Harmony: ProcessText() not found, trying overloads...");
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
                            DiagLog("Harmony: Patched first ProcessText overload");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Harmony patch error: " + ex.Message);
                DiagLog("Harmony ERROR: " + ex.Message);
            }

            // Resolve UILabel.mFont field via reflection for force-clearing bitmap fonts
            if (!_mFontFieldResolved)
            {
                MFontField = typeof(UILabel).GetField("mFont",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                _mFontFieldResolved = true;
                if (MFontField != null)
                {
                    Logger.LogInfo("Resolved UILabel.mFont field via reflection");
                    DiagLog("Reflection: UILabel.mFont field resolved OK");
                }
                else
                {
                    Logger.LogWarning("UILabel.mFont field NOT found — bitmap font clearing may fail!");
                    DiagLog("Reflection: UILabel.mFont field NOT FOUND!");
                }
            }

            DiagFlush();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // Font files to install: { filename, registry display name }
        private static readonly string[][] FontsToInstall = new string[][]
        {
            new string[] { "LanaPixel.ttf", "LanaPixel (TrueType)" },
            new string[] { "PressStart2P-Regular.ttf", "Press Start 2P (TrueType)" },
        };

        void InstallFontToUserDir()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location);

            // Step 1: Register fonts from plugin dir for CURRENT session (immediate availability)
            bool anyRegistered = false;
            foreach (var font in FontsToInstall)
            {
                string srcFont = Path.Combine(pluginDir, font[0]);
                if (File.Exists(srcFont))
                {
                    try
                    {
                        int result = AddFontResourceEx(srcFont, 0, IntPtr.Zero);
                        Logger.LogInfo("AddFontResourceEx(" + font[0] + ") = " + result);
                        DiagLog("AddFontResourceEx(" + font[0] + ") = " + result + " (file: " + srcFont + ")");
                        if (result > 0) anyRegistered = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("AddFontResourceEx failed: " + ex.Message);
                        DiagLog("AddFontResourceEx FAILED: " + ex.Message);
                    }
                }
                else
                {
                    Logger.LogWarning("Font file not found: " + srcFont);
                    DiagLog("Font file NOT FOUND: " + srcFont);
                }
            }

            // Broadcast WM_FONTCHANGE so Unity picks up newly registered fonts
            if (anyRegistered)
            {
                try
                {
                    SendMessage(HWND_BROADCAST, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero);
                    Logger.LogInfo("Sent WM_FONTCHANGE broadcast");
                    DiagLog("WM_FONTCHANGE broadcast sent");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("WM_FONTCHANGE failed: " + ex.Message);
                    DiagLog("WM_FONTCHANGE FAILED: " + ex.Message);
                }
            }

            // Step 2: Permanently install to user fonts dir (for future sessions)
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string userFontsDir = Path.Combine(Path.Combine(localAppData, "Microsoft"), "Windows");
                userFontsDir = Path.Combine(userFontsDir, "Fonts");
                Directory.CreateDirectory(userFontsDir);

                foreach (var font in FontsToInstall)
                {
                    string srcFont = Path.Combine(pluginDir, font[0]);
                    if (!File.Exists(srcFont)) continue;

                    string destFont = Path.Combine(userFontsDir, font[0]);
                    if (!File.Exists(destFont))
                    {
                        File.Copy(srcFont, destFont, true);
                        Logger.LogInfo("Installed font: " + font[0]);
                        DiagLog("Installed font to user dir: " + destFont);

                        using (var key = Registry.CurrentUser.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", true))
                        {
                            if (key != null)
                            {
                                key.SetValue(font[1], destFont);
                                Logger.LogInfo("Registered: " + font[1]);
                            }
                        }
                    }
                    else
                    {
                        DiagLog("Font already in user dir: " + destFont);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Font install error: " + ex.Message);
                DiagLog("Font install error: " + ex.Message);
            }
        }

        Font TryLoadFont(string name)
        {
            Font f = Font.CreateDynamicFontFromOSFont(name, 16);
            if (f == null || !f.dynamic)
            {
                DiagLog("TryLoadFont('" + name + "'): null or not dynamic");
                return null;
            }

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
                DiagLog("TryLoadFont('" + name + "'): advance=" + ci.advance + " (Arial=" + arialCi.advance + ") -> OK");
                return f;
            }

            Logger.LogInfo("Font '" + name + "': advance=" + ci.advance + " = Arial, skipping");
            DiagLog("TryLoadFont('" + name + "'): advance=" + ci.advance + " = Arial=" + arialCi.advance + " -> REJECTED (same as Arial)");
            return null;
        }

        // Try loading font by file path — Unity's CreateDynamicFontFromOSFont
        // accepts file paths in addition to font family names
        Font TryLoadFontByPath(string filePath, string displayName)
        {
            if (!File.Exists(filePath)) return null;

            try
            {
                Font f = Font.CreateDynamicFontFromOSFont(filePath, 16);
                if (f == null || !f.dynamic)
                {
                    DiagLog("TryLoadFontByPath('" + displayName + "'): null or not dynamic");
                    return null;
                }

                // Verify it renders Cyrillic (not just Arial fallback)
                f.RequestCharactersInTexture("ТЙ", 16);
                CharacterInfo ci;
                f.GetCharacterInfo('Т', out ci, 16);

                Font arial = Font.CreateDynamicFontFromOSFont("Arial", 16);
                arial.RequestCharactersInTexture("Т", 16);
                CharacterInfo arialCi;
                arial.GetCharacterInfo('Т', out arialCi, 16);

                if (ci.advance != arialCi.advance)
                {
                    Logger.LogInfo("Font by path '" + displayName + "': advance=" + ci.advance + " (Arial=" + arialCi.advance + ") - OK");
                    DiagLog("TryLoadFontByPath('" + displayName + "'): advance=" + ci.advance + " -> OK");
                    return f;
                }

                DiagLog("TryLoadFontByPath('" + displayName + "'): advance=" + ci.advance + " = Arial -> REJECTED");
            }
            catch (Exception ex)
            {
                DiagLog("TryLoadFontByPath('" + displayName + "'): ERROR " + ex.Message);
            }
            return null;
        }

        void FindCyrillicFont()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location);

            // Log available OS fonts for diagnostics
            try
            {
                string[] osFonts = Font.GetOSInstalledFontNames();
                DiagLog("OS fonts count: " + osFonts.Length);
                // Log only fonts containing "lana", "press", "pixel" (case-insensitive)
                foreach (string fn in osFonts)
                {
                    string lower = fn.ToLower();
                    if (lower.Contains("lana") || lower.Contains("press") || lower.Contains("pixel"))
                        DiagLog("  OS font match: '" + fn + "'");
                }
            }
            catch (Exception ex)
            {
                DiagLog("GetOSInstalledFontNames error: " + ex.Message);
            }

            // Method 1: Try pixel fonts by family name
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
                    IsPixelFont = true;
                    UnityEngine.Object.DontDestroyOnLoad(CyrillicFont);
                    Logger.LogInfo("Using pixel font: " + name);
                    DiagLog("RESULT: Using pixel font '" + name + "' (by name), IsPixelFont=true");
                    return;
                }
            }

            // Method 2: Try loading by full file path (bypasses OS font registry)
            DiagLog("Trying font loading by file path...");
            string[] fontFiles = new string[] {
                "LanaPixel.ttf",
                "PressStart2P-Regular.ttf"
            };
            foreach (string fileName in fontFiles)
            {
                string filePath = Path.Combine(pluginDir, fileName);
                Font f = TryLoadFontByPath(filePath, fileName);
                if (f != null)
                {
                    CyrillicFont = f;
                    IsPixelFont = true;
                    UnityEngine.Object.DontDestroyOnLoad(CyrillicFont);
                    Logger.LogInfo("Using pixel font from file: " + fileName);
                    DiagLog("RESULT: Using pixel font '" + fileName + "' (by path), IsPixelFont=true");
                    return;
                }
            }

            // Method 3: Try user fonts dir paths
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string userFontsDir = Path.Combine(Path.Combine(localAppData, "Microsoft"), "Windows");
                userFontsDir = Path.Combine(userFontsDir, "Fonts");
                foreach (string fileName in fontFiles)
                {
                    string filePath = Path.Combine(userFontsDir, fileName);
                    Font f = TryLoadFontByPath(filePath, "userfonts/" + fileName);
                    if (f != null)
                    {
                        CyrillicFont = f;
                        IsPixelFont = true;
                        UnityEngine.Object.DontDestroyOnLoad(CyrillicFont);
                        Logger.LogInfo("Using pixel font from user dir: " + fileName);
                        DiagLog("RESULT: Using pixel font '" + fileName + "' (user dir), IsPixelFont=true");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog("User fonts dir scan error: " + ex.Message);
            }

            // Fallback: Arial
            IsPixelFont = false;
            Logger.LogInfo("No pixel font found, using Arial");
            DiagLog("RESULT: No pixel font found, using Arial, IsPixelFont=false");
            CyrillicFont = Font.CreateDynamicFontFromOSFont("Arial", 16);
            if (CyrillicFont != null)
                UnityEngine.Object.DontDestroyOnLoad(CyrillicFont);

            if (CyrillicFont == null)
            {
                Logger.LogError("No Cyrillic font available!");
                DiagLog("CRITICAL: No Cyrillic font available at all!");
            }
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Logger.LogInfo("Scene: " + scene.name);
            DiagLog("Scene loaded: " + scene.name);
            DiagFlush();
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
                        // Force-clear bitmap font backing field via reflection
                        if (MFontField != null)
                            MFontField.SetValue(label, null);
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
        private static int _msgObjectLogCount = 0; // limit diagnostic spam

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
                int origSize = __instance.fontSize;
                string snippet = __instance.text != null
                    ? __instance.text.Replace("\n", "\\n")
                    : "";
                if (snippet.Length > 60) snippet = snippet.Substring(0, 60);

                bool isMsgObject = __instance.gameObject.name.Contains("MsgObject");
                bool isRadioMsg = __instance.gameObject.name.Contains("RadioMsg");

                // Diagnostic logging for dialog labels (first 50 per type)
                if ((isMsgObject || isRadioMsg) && _msgObjectLogCount < 50)
                {
                    _msgObjectLogCount++;
                    string bfName = "null";
                    try { if (__instance.bitmapFont != null) bfName = __instance.bitmapFont.name; } catch { }
                    string ttfName = "null";
                    try { if (__instance.trueTypeFont != null) ttfName = __instance.trueTypeFont.name; } catch { }

                    string diagMsg = "DIALOG #" + _msgObjectLogCount + ": "
                        + __instance.gameObject.name
                        + " bitmapFont=" + bfName
                        + " trueTypeFont=" + ttfName
                        + " origSz=" + origSize
                        + " ov=" + __instance.overflowMethod
                        + " w=" + __instance.width + " h=" + __instance.height
                        + " IsPixelFont=" + RussianLocPlugin.IsPixelFont
                        + " [" + snippet + "]";
                    if (Log != null) Log.LogInfo(diagMsg);
                    RussianLocPlugin.DiagLog(diagMsg);
                    RussianLocPlugin.DiagFlush();
                }

                // Force-clear bitmap font backing field via reflection.
                // CRITICAL: NGUI prioritizes bitmapFont (mFont) over trueTypeFont.
                // Dialog labels use bitmapFont=ThinPixel_60 (Latin-only atlas).
                // If mFont isn't cleared, Cyrillic text renders as empty glyphs.
                // The property setter bitmapFont=null also clears mTrueTypeFont,
                // so we bypass it and directly null the backing field.
                bool hadBitmapFont = false;
                if (RussianLocPlugin.MFontField != null)
                {
                    object mFontVal = RussianLocPlugin.MFontField.GetValue(__instance);
                    if (mFontVal != null)
                    {
                        hadBitmapFont = true;
                        RussianLocPlugin.MFontField.SetValue(__instance, null);
                    }
                }

                __instance.trueTypeFont = RussianLocPlugin.CyrillicFont;

                // Post-swap verification: ensure mFont is actually null
                if (RussianLocPlugin.MFontField != null)
                {
                    object postSwap = RussianLocPlugin.MFontField.GetValue(__instance);
                    if (postSwap != null)
                    {
                        // Still set! Force clear again
                        RussianLocPlugin.MFontField.SetValue(__instance, null);
                        if (Log != null)
                            Log.LogWarning("FORCE-CLEARED mFont post-swap on " + __instance.gameObject.name);
                        RussianLocPlugin.DiagLog("FORCE-CLEARED mFont post-swap: " + __instance.gameObject.name);
                    }
                    else if (hadBitmapFont && (isMsgObject || isRadioMsg) && _msgObjectLogCount <= 50)
                    {
                        RussianLocPlugin.DiagLog("  -> bitmapFont cleared OK, trueTypeFont="
                            + (__instance.trueTypeFont != null ? __instance.trueTypeFont.name : "null"));
                        RussianLocPlugin.DiagFlush();
                    }
                }

                // Cinematic titles (sz=100, huge widgets)
                if (origSize >= 80)
                    __instance.fontSize = 40;

                // Dialog speech bubbles (MsgObjectInput clones, sz=60)
                if (isMsgObject)
                {
                    if (RussianLocPlugin.IsPixelFont)
                    {
                        // Pixel font: small line height, need \n to push past top clip edge
                        if (__instance.fontSize > 22)
                            __instance.fontSize = 22;
                        __instance.useFloatSpacing = true;
                        __instance.floatSpacingY = 0f;
                        if (__instance.text != null && __instance.text.Length > 0 && __instance.text[0] != '\n')
                            __instance.text = "\n" + __instance.text;
                    }
                    else
                    {
                        // Arial/system font: use size 24 (not 18, which was too small)
                        if (__instance.fontSize > 24)
                            __instance.fontSize = 24;
                        __instance.useFloatSpacing = true;
                        __instance.floatSpacingY = -4f;
                        // Force ShrinkContent to prevent text clipping
                        __instance.overflowMethod = UILabel.Overflow.ShrinkContent;
                    }
                }
                // Radio message (RadioMsg, origSz=60)
                else if (isRadioMsg)
                {
                    if (RussianLocPlugin.IsPixelFont)
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
                        if (__instance.fontSize > 26)
                            __instance.fontSize = 26;
                        __instance.useFloatSpacing = true;
                        __instance.floatSpacingY = 0f;
                        __instance.overflowMethod = UILabel.Overflow.ShrinkContent;
                    }
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
