using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NotTonightRussianInstaller
{
    // P/Invoke for unblocking files (remove Zone.Identifier ADS) and font management
    static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteFile(string lpFileName);

        // Register a font for the current GDI session. flags=0 makes the font visible
        // to OTHER processes too (game launches as separate process). FR_PRIVATE (0x10)
        // would make it private to current process only — useless for our case.
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool RemoveFontResourceEx(string lpFileName, uint fl, IntPtr pdv);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam,
            IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        public const uint WM_FONTCHANGE = 0x001D;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
    }

    public class InstallerForm : Form
    {
        private Label titleLabel;
        private Label versionLabel;
        private Label pathLabel;
        private TextBox pathTextBox;
        private Button browseButton;
        private GroupBox infoGroup;
        private Label infoLabel;
        private Button installButton;
        private Button uninstallButton;
        private ProgressBar progressBar;
        private RichTextBox logBox;
        private Label statusLabel;
        private BackgroundWorker worker;

        private const string ModVersion = "1.8.0";

        // Game exe can be either "Not Tonight.exe" or "NotTonight.exe"
        private static readonly string[] GameExeNames = { "Not Tonight.exe", "NotTonight.exe" };
        // Game folder can be either "Not Tonight" or "NotTonight"
        private static readonly string[] GameFolderNames = { "Not Tonight", "NotTonight" };

        // Fonts shipped with the mod. RegName is the Windows registry value name for
        // the font; data part differs between HKLM (just filename) and HKCU (full path).
        // Family names (LanaPixel / Press Start 2P) are verified against the actual TTF
        // name table — they match what RussianLocPlugin.FindCyrillicFont() searches for.
        private class FontEntry
        {
            public string FileName;
            public string RegName;
        }

        private static readonly FontEntry[] FontsToInstall = new[]
        {
            new FontEntry { FileName = "LanaPixel.ttf",            RegName = "LanaPixel (TrueType)" },
            new FontEntry { FileName = "PressStart2P-Regular.ttf", RegName = "Press Start 2P (TrueType)" },
        };

        private const string FontsRegSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

        public InstallerForm()
        {
            InitializeComponents();
            string detected = DetectGamePath();
            if (detected != null)
            {
                pathTextBox.Text = detected;
                CheckInstalledState(detected);
            }
        }

        private void InitializeComponents()
        {
            this.Text = "Not Tonight \u2014 \u0420\u0443\u0441\u0438\u0444\u0438\u043a\u0430\u0442\u043e\u0440";
            this.Size = new Size(620, 560);
            this.MinimumSize = new Size(620, 560);
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(24, 24, 32);
            this.ForeColor = Color.FromArgb(220, 220, 230);

            // DPI scaling support
            this.AutoScaleDimensions = new SizeF(96F, 96F);
            this.AutoScaleMode = AutoScaleMode.Dpi;

            // Font with fallback
            Font baseFont = CreateFont(9f);
            this.Font = baseFont;

            // Title
            titleLabel = new Label
            {
                Text = "Not Tonight \u2014 \u0420\u0443\u0441\u0438\u0444\u0438\u043a\u0430\u0442\u043e\u0440",
                Font = CreateFont(18f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 80, 80),
                AutoSize = true,
                Location = new Point(20, 15)
            };

            versionLabel = new Label
            {
                Text = "\u0412\u0435\u0440\u0441\u0438\u044f " + ModVersion + "  |  Artem Lytkin (4RH1T3CT0R)",
                Font = CreateFont(8.5f),
                ForeColor = Color.FromArgb(140, 140, 160),
                AutoSize = true,
                Location = new Point(22, 52)
            };

            // Path selection
            pathLabel = new Label
            {
                Text = "\u041f\u0430\u043f\u043a\u0430 \u0441 \u0438\u0433\u0440\u043e\u0439 Not Tonight:",
                AutoSize = true,
                Location = new Point(20, 85)
            };

            pathTextBox = new TextBox
            {
                Location = new Point(20, 105),
                Size = new Size(470, 24),
                BackColor = Color.FromArgb(40, 40, 55),
                ForeColor = Color.FromArgb(220, 220, 230),
                BorderStyle = BorderStyle.FixedSingle
            };

            browseButton = new Button
            {
                Text = "\u041e\u0431\u0437\u043e\u0440...",
                Location = new Point(500, 104),
                Size = new Size(85, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 50, 70),
                ForeColor = Color.FromArgb(220, 220, 230)
            };
            browseButton.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 100);
            browseButton.Click += BrowseButton_Click;

            // Info box
            infoGroup = new GroupBox
            {
                Text = "\u0411\u0443\u0434\u0443\u0442 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d\u044b",
                Location = new Point(20, 140),
                Size = new Size(565, 100),
                ForeColor = Color.FromArgb(180, 180, 200)
            };

            infoLabel = new Label
            {
                Text = "\u2022 BepInEx (\u0444\u0440\u0435\u0439\u043c\u0432\u043e\u0440\u043a \u043c\u043e\u0434\u043e\u0432)\n" +
                       "\u2022 XUnity.AutoTranslator (\u0441\u0438\u0441\u0442\u0435\u043c\u0430 \u043f\u0435\u0440\u0435\u0432\u043e\u0434\u0430)\n" +
                       "\u2022 NotTonightRussian.dll (\u043c\u043e\u0434 \u0440\u0443\u0441\u0438\u0444\u0438\u043a\u0430\u0446\u0438\u0438)\n" +
                       "\u2022 \u041f\u0435\u0440\u0435\u0432\u043e\u0434\u044b (8,000+ \u043a\u043b\u044e\u0447\u0435\u0439 I2 + XUnity)",
                Location = new Point(15, 22),
                Size = new Size(535, 70),
                ForeColor = Color.FromArgb(200, 200, 215)
            };
            infoGroup.Controls.Add(infoLabel);

            // Buttons
            installButton = new Button
            {
                Text = "\u0423\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u044c",
                Location = new Point(20, 252),
                Size = new Size(160, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 100, 60),
                ForeColor = Color.White,
                Font = CreateFont(10f, FontStyle.Bold)
            };
            installButton.FlatAppearance.BorderColor = Color.FromArgb(60, 140, 80);
            installButton.Click += InstallButton_Click;

            uninstallButton = new Button
            {
                Text = "\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u043c\u043e\u0434",
                Location = new Point(190, 252),
                Size = new Size(140, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(100, 40, 40),
                ForeColor = Color.White,
                Font = CreateFont(10f)
            };
            uninstallButton.FlatAppearance.BorderColor = Color.FromArgb(140, 60, 60);
            uninstallButton.Click += UninstallButton_Click;

            // Progress
            progressBar = new ProgressBar
            {
                Location = new Point(20, 300),
                Size = new Size(565, 20),
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };

            statusLabel = new Label
            {
                Text = "",
                Location = new Point(20, 325),
                Size = new Size(565, 20),
                ForeColor = Color.FromArgb(160, 160, 180)
            };

            // Log
            logBox = new RichTextBox
            {
                Location = new Point(20, 350),
                Size = new Size(565, 155),
                ReadOnly = true,
                BackColor = Color.FromArgb(16, 16, 24),
                ForeColor = Color.FromArgb(180, 180, 200),
                BorderStyle = BorderStyle.FixedSingle,
                Font = CreateFont(8.5f, FontStyle.Regular, "Consolas"),
                ScrollBars = RichTextBoxScrollBars.Vertical
            };

            // Worker
            worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += Worker_DoWork;
            worker.ProgressChanged += Worker_ProgressChanged;
            worker.RunWorkerCompleted += Worker_RunWorkerCompleted;

            this.Controls.AddRange(new Control[]
            {
                titleLabel, versionLabel, pathLabel, pathTextBox, browseButton,
                infoGroup, installButton, uninstallButton, progressBar,
                statusLabel, logBox
            });
        }

        /// <summary>
        /// Create a font with fallback: tries preferred family, then Segoe UI, then system default.
        /// </summary>
        private static Font CreateFont(float size, FontStyle style = FontStyle.Regular, string preferred = "Segoe UI")
        {
            string[] families = { preferred, "Segoe UI", "Tahoma", "Arial" };
            foreach (var name in families)
            {
                try
                {
                    var f = new Font(name, size, style);
                    if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(SystemFonts.DefaultFont.FontFamily, size, style);
        }

        /// <summary>
        /// Find the game exe in the given folder. Returns full path or null.
        /// Checks both "Not Tonight.exe" and "NotTonight.exe".
        /// </summary>
        private static string FindGameExe(string folder)
        {
            foreach (var name in GameExeNames)
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "\u0412\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u043f\u0430\u043f\u043a\u0443 \u0441 \u0438\u0433\u0440\u043e\u0439 Not Tonight";
                dialog.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(pathTextBox.Text) && Directory.Exists(pathTextBox.Text))
                    dialog.SelectedPath = pathTextBox.Text;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    pathTextBox.Text = dialog.SelectedPath;
                    CheckInstalledState(dialog.SelectedPath);
                }
            }
        }

        private void CheckInstalledState(string gamePath)
        {
            bool installed = File.Exists(Path.Combine(gamePath, "BepInEx", "plugins", "NotTonightRussian", "NotTonightRussian.dll"));
            if (installed)
            {
                statusLabel.Text = "\u0421\u0442\u0430\u0442\u0443\u0441: \u043c\u043e\u0434 \u0443\u0436\u0435 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d";
                statusLabel.ForeColor = Color.FromArgb(100, 200, 120);
            }
            else
            {
                statusLabel.Text = "\u0421\u0442\u0430\u0442\u0443\u0441: \u043c\u043e\u0434 \u043d\u0435 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d";
                statusLabel.ForeColor = Color.FromArgb(200, 200, 100);
            }
        }

        private void Log(string message, Color? color = null)
        {
            if (logBox.InvokeRequired)
            {
                logBox.Invoke(new Action(() => Log(message, color)));
                return;
            }
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionColor = color ?? Color.FromArgb(180, 180, 200);
            logBox.AppendText(message + "\n");
            logBox.ScrollToCaret();
        }

        private void SetButtonsEnabled(bool enabled)
        {
            installButton.Enabled = enabled;
            uninstallButton.Enabled = enabled;
            browseButton.Enabled = enabled;
            pathTextBox.Enabled = enabled;
        }

        // ========== INSTALL ==========

        private void InstallButton_Click(object sender, EventArgs e)
        {
            string gamePath = pathTextBox.Text.Trim().Trim('"');

            if (string.IsNullOrEmpty(gamePath))
            {
                MessageBox.Show(
                    "\u0423\u043a\u0430\u0436\u0438\u0442\u0435 \u043f\u0443\u0442\u044c \u043a \u043f\u0430\u043f\u043a\u0435 \u0441 \u0438\u0433\u0440\u043e\u0439.",
                    "\u041e\u0448\u0438\u0431\u043a\u0430", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (FindGameExe(gamePath) == null)
            {
                MessageBox.Show(
                    "\u0424\u0430\u0439\u043b \u0438\u0433\u0440\u044b \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d \u0432:\n" + gamePath +
                    "\n\n\u041f\u0440\u043e\u0432\u0435\u0440\u044c\u0442\u0435 \u043f\u0443\u0442\u044c \u0438 \u043f\u043e\u043f\u0440\u043e\u0431\u0443\u0439\u0442\u0435 \u0441\u043d\u043e\u0432\u0430.\n\n" +
                    "\u041e\u0436\u0438\u0434\u0430\u0435\u0442\u0441\u044f: Not Tonight.exe \u0438\u043b\u0438 NotTonight.exe",
                    "\u041e\u0448\u0438\u0431\u043a\u0430", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                var procs = Process.GetProcessesByName("Not Tonight");
                if (procs.Length == 0)
                    procs = Process.GetProcessesByName("NotTonight");
                if (procs.Length > 0)
                {
                    MessageBox.Show(
                        "\u0418\u0433\u0440\u0430 Not Tonight \u0437\u0430\u043f\u0443\u0449\u0435\u043d\u0430.\n\u0417\u0430\u043a\u0440\u043e\u0439\u0442\u0435 \u0438\u0433\u0440\u0443 \u043f\u0435\u0440\u0435\u0434 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u043e\u0439.",
                        "\u041e\u0448\u0438\u0431\u043a\u0430", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            catch { }

            logBox.Clear();
            SetButtonsEnabled(false);
            progressBar.Visible = true;
            progressBar.Value = 0;
            worker.RunWorkerAsync(new WorkerArgs { GamePath = gamePath, Mode = "install" });
        }

        // ========== UNINSTALL ==========

        private void UninstallButton_Click(object sender, EventArgs e)
        {
            string gamePath = pathTextBox.Text.Trim().Trim('"');

            if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath))
            {
                MessageBox.Show(
                    "\u0423\u043a\u0430\u0436\u0438\u0442\u0435 \u043f\u0443\u0442\u044c \u043a \u043f\u0430\u043f\u043a\u0435 \u0441 \u0438\u0433\u0440\u043e\u0439.",
                    "\u041e\u0448\u0438\u0431\u043a\u0430", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var result = MessageBox.Show(
                "\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u0440\u0443\u0441\u0438\u0444\u0438\u043a\u0430\u0442\u043e\u0440 \u0438\u0437:\n" + gamePath + "\n\n" +
                "\u0411\u0443\u0434\u0443\u0442 \u0443\u0434\u0430\u043b\u0435\u043d\u044b: BepInEx, winhttp.dll, doorstop_config.ini",
                "\u041f\u043e\u0434\u0442\u0432\u0435\u0440\u0436\u0434\u0435\u043d\u0438\u0435",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
                return;

            logBox.Clear();
            SetButtonsEnabled(false);
            progressBar.Visible = true;
            progressBar.Value = 0;
            worker.RunWorkerAsync(new WorkerArgs { GamePath = gamePath, Mode = "uninstall" });
        }

        // ========== BACKGROUND WORKER ==========

        private class WorkerArgs
        {
            public string GamePath;
            public string Mode;
        }

        private void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            var args = (WorkerArgs)e.Argument;
            if (args.Mode == "install")
                DoInstall(args.GamePath);
            else
                DoUninstall(args.GamePath);
        }

        private void DoInstall(string gamePath)
        {
            string tempDir = Path.Combine(Path.GetTempPath(),
                "NotTonightRussian_install_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            bool fontsOk = true;
            string fontFailureDetails = null;
            string pluginDir = Path.Combine(gamePath, "BepInEx", "plugins", "NotTonightRussian");

            try
            {
                Directory.CreateDirectory(tempDir);

                // Step 1: Extract
                worker.ReportProgress(10, "\u0420\u0430\u0441\u043f\u0430\u043a\u043e\u0432\u043a\u0430 \u0434\u0430\u043d\u043d\u044b\u0445...");
                Log("[1/4] \u0420\u0430\u0441\u043f\u0430\u043a\u043e\u0432\u043a\u0430 \u0434\u0430\u043d\u043d\u044b\u0445...");

                string zipTemp = Path.Combine(tempDir, "data.zip");
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("data.zip"))
                {
                    if (stream == null)
                    {
                        Log("\u041e\u0428\u0418\u0411\u041a\u0410: \u043d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u043d\u0430\u0439\u0442\u0438 \u0432\u0441\u0442\u0440\u043e\u0435\u043d\u043d\u044b\u0435 \u0434\u0430\u043d\u043d\u044b\u0435.", Color.Red);
                        return;
                    }

                    using (var file = File.Create(zipTemp))
                    {
                        stream.CopyTo(file);
                    }
                }

                ZipFile.ExtractToDirectory(zipTemp, tempDir);
                File.Delete(zipTemp);
                Log("   \u0420\u0430\u0441\u043f\u0430\u043a\u043e\u0432\u043a\u0430 OK", Color.FromArgb(100, 200, 120));

                // Step 2: Copy
                worker.ReportProgress(35, "\u041a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u043d\u0438\u0435 \u0444\u0430\u0439\u043b\u043e\u0432...");
                Log("[2/4] \u041a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u043d\u0438\u0435 \u0444\u0430\u0439\u043b\u043e\u0432...");

                int copied = CopyDirectory(tempDir, gamePath);
                Log("   \u0421\u043a\u043e\u043f\u0438\u0440\u043e\u0432\u0430\u043d\u043e: " + copied + " \u0444\u0430\u0439\u043b\u043e\u0432", Color.FromArgb(100, 200, 120));

                // Step 3: Install fonts (system-wide if admin, else per-user fallback)
                worker.ReportProgress(60, "\u0423\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u0430 \u0448\u0440\u0438\u0444\u0442\u043e\u0432...");
                Log("[3/4] \u0423\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u0430 \u0448\u0440\u0438\u0444\u0442\u043e\u0432...");

                // \u0423\u0434\u0430\u043b\u0438\u0442\u044c \u0443\u0441\u0442\u0430\u0440\u0435\u0432\u0448\u0438\u0439 diag-\u043b\u043e\u0433 \u043c\u043e\u0434\u0430 \u2014 \u043e\u043d \u0441\u0431\u0438\u0432\u0430\u0435\u0442 \u0441 \u0442\u043e\u043b\u043a\u0443 \u043f\u0440\u0438 \u043f\u043e\u0432\u0442\u043e\u0440\u043d\u043e\u0439 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u0435
                TryDeleteStaleDiagLog(pluginDir);

                fontsOk = InstallFonts(pluginDir, out fontFailureDetails);
                if (fontsOk)
                {
                    Log("   \u0428\u0440\u0438\u0444\u0442\u044b \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d\u044b \u0438 \u0437\u0430\u0440\u0435\u0433\u0438\u0441\u0442\u0440\u0438\u0440\u043e\u0432\u0430\u043d\u044b", Color.FromArgb(100, 200, 120));
                }
                else
                {
                    Log("   \u0428\u0440\u0438\u0444\u0442\u044b \u041d\u0415 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043b\u0435\u043d\u044b \u0430\u0432\u0442\u043e\u043c\u0430\u0442\u0438\u0447\u0435\u0441\u043a\u0438:", Color.FromArgb(255, 200, 100));
                    if (!string.IsNullOrEmpty(fontFailureDetails))
                    {
                        foreach (var line in fontFailureDetails.Split('\n'))
                        {
                            if (!string.IsNullOrEmpty(line))
                                Log("     " + line, Color.FromArgb(255, 200, 100));
                        }
                    }
                }

                // Step 4: Verify
                worker.ReportProgress(85, "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430...");
                Log("[4/4] \u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430...");

                string[] checkFiles = new string[]
                {
                    "winhttp.dll",
                    "doorstop_config.ini",
                    "BepInEx\\core\\BepInEx.dll",
                    "BepInEx\\plugins\\NotTonightRussian\\NotTonightRussian.dll",
                    "BepInEx\\plugins\\NotTonightRussian\\translations.txt",
                    "BepInEx\\plugins\\NotTonightRussian\\LanaPixel.ttf",
                    "BepInEx\\plugins\\NotTonightRussian\\PressStart2P-Regular.ttf",
                    "BepInEx\\config\\AutoTranslatorConfig.ini",
                    "BepInEx\\Translation\\ru\\Text\\_AutoGeneratedTranslations.txt"
                };

                bool ok = true;
                foreach (var cf in checkFiles)
                {
                    if (!File.Exists(Path.Combine(gamePath, cf)))
                    {
                        Log("   \u041d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d: " + cf, Color.FromArgb(255, 100, 100));
                        ok = false;
                    }
                }

                if (ok)
                {
                    worker.ReportProgress(100, "\u0423\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u0430 \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u0430!");
                    Log("");
                    Log("\u0423\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u0430 \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u0430!", Color.FromArgb(255, 80, 80));
                    Log("\u0417\u0430\u043f\u0443\u0441\u0442\u0438\u0442\u0435 Not Tonight \u0447\u0435\u0440\u0435\u0437 Steam \u2014 \u0440\u0443\u0441\u0441\u043a\u0438\u0439 \u044f\u0437\u044b\u043a \u0432\u043a\u043b\u044e\u0447\u0438\u0442\u0441\u044f \u0430\u0432\u0442\u043e\u043c\u0430\u0442\u0438\u0447\u0435\u0441\u043a\u0438.");
                }
                else
                {
                    worker.ReportProgress(100, "\u0423\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u0430 \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u0430 \u0441 \u043e\u0448\u0438\u0431\u043a\u0430\u043c\u0438");
                    Log("\u0423\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u0430 \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u0430, \u043d\u043e \u043d\u0435\u043a\u043e\u0442\u043e\u0440\u044b\u0435 \u0444\u0430\u0439\u043b\u044b \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d\u044b.", Color.FromArgb(255, 200, 100));
                }
            }
            catch (Exception ex)
            {
                Log("\u041e\u0428\u0418\u0411\u041a\u0410: " + ex.Message, Color.Red);
                worker.ReportProgress(100, "\u041e\u0448\u0438\u0431\u043a\u0430 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u043a\u0438");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
            }

            // Show font-failure dialog after the install flow finishes (regardless of
            // verify outcome). MessageBox must run on UI thread \u2014 marshal via Invoke.
            // C# 7.3 requires explicit MethodInvoker cast for the lambda.
            if (!fontsOk)
            {
                try
                {
                    string capturedPluginDir = pluginDir;
                    this.Invoke((MethodInvoker)delegate { ShowFontInstallFailedDialog(capturedPluginDir); });
                }
                catch { }
            }
        }

        private void DoUninstall(string gamePath)
        {
            worker.ReportProgress(10, "\u0423\u0434\u0430\u043b\u0435\u043d\u0438\u0435 \u043c\u043e\u0434\u0430...");
            Log("\u0423\u0434\u0430\u043b\u0435\u043d\u0438\u0435 \u0440\u0443\u0441\u0438\u0444\u0438\u043a\u0430\u0442\u043e\u0440\u0430...");

            int removed = 0;

            string[] filesToDelete = new string[]
            {
                "winhttp.dll",
                "doorstop_config.ini",
                ".doorstop_version"
            };

            string[] dirsToDelete = new string[]
            {
                "BepInEx"
            };

            foreach (var f in filesToDelete)
            {
                string fullPath = Path.Combine(gamePath, f);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        File.Delete(fullPath);
                        Log("   \u0423\u0434\u0430\u043b\u0451\u043d: " + f);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Log("   \u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u0443\u0434\u0430\u043b\u0438\u0442\u044c " + f + ": " + ex.Message, Color.FromArgb(255, 200, 100));
                    }
                }
            }

            worker.ReportProgress(40, "\u0423\u0434\u0430\u043b\u0435\u043d\u0438\u0435 \u043f\u0430\u043f\u043e\u043a...");

            foreach (var d in dirsToDelete)
            {
                string fullPath = Path.Combine(gamePath, d);
                if (Directory.Exists(fullPath))
                {
                    try
                    {
                        Directory.Delete(fullPath, true);
                        Log("   \u0423\u0434\u0430\u043b\u0451\u043d\u0430 \u043f\u0430\u043f\u043a\u0430: " + d);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        Log("   \u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u0443\u0434\u0430\u043b\u0438\u0442\u044c " + d + ": " + ex.Message, Color.FromArgb(255, 200, 100));
                    }
                }
            }

            worker.ReportProgress(100, "\u0423\u0434\u0430\u043b\u0435\u043d\u0438\u0435 \u0437\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u043e");
            Log("");
            Log("\u041c\u043e\u0434 \u0443\u0434\u0430\u043b\u0451\u043d. \u0423\u0434\u0430\u043b\u0435\u043d\u043e \u044d\u043b\u0435\u043c\u0435\u043d\u0442\u043e\u0432: " + removed, Color.FromArgb(255, 80, 80));
        }

        private void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            progressBar.Value = e.ProgressPercentage;
            if (e.UserState is string msg)
                statusLabel.Text = msg;
        }

        private void Worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetButtonsEnabled(true);
            CheckInstalledState(pathTextBox.Text.Trim().Trim('"'));
        }

        // ========== GAME PATH DETECTION ==========

        private static string DetectGamePath()
        {
            // Method 1: Common Steam library paths
            string[] drives = { "C", "D", "E", "F", "G", "H" };
            string[] steamRoots =
            {
                @":\Program Files (x86)\Steam\steamapps\common",
                @":\Program Files\Steam\steamapps\common",
                @":\Steam\steamapps\common",
                @":\SteamLibrary\steamapps\common",
                @":\Games\Steam\steamapps\common",
                @":\Games\SteamLibrary\steamapps\common"
            };

            foreach (var drive in drives)
            {
                foreach (var root in steamRoots)
                {
                    foreach (var folder in GameFolderNames)
                    {
                        string path = drive + root + "\\" + folder;
                        if (FindGameExe(path) != null)
                            return path;
                    }
                }
            }

            // Method 2: Steam registry + libraryfolders.vdf
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null)
                    {
                        string steamPath = key.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(steamPath))
                        {
                            steamPath = steamPath.Replace("/", "\\");

                            // Check main Steam library
                            foreach (var folder in GameFolderNames)
                            {
                                string mainLib = Path.Combine(steamPath, "steamapps", "common", folder);
                                if (FindGameExe(mainLib) != null)
                                    return mainLib;
                            }

                            // Check additional libraries from VDF
                            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                            if (File.Exists(vdfPath))
                            {
                                string vdf = File.ReadAllText(vdfPath);
                                int idx = 0;
                                while (true)
                                {
                                    idx = vdf.IndexOf("\"path\"", idx);
                                    if (idx < 0) break;
                                    int q1 = vdf.IndexOf("\"", idx + 6);
                                    if (q1 < 0) break;
                                    int q2 = vdf.IndexOf("\"", q1 + 1);
                                    if (q2 < 0) break;
                                    string libPath = vdf.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\");
                                    foreach (var folder in GameFolderNames)
                                    {
                                        string check = Path.Combine(libPath, "steamapps", "common", folder);
                                        if (FindGameExe(check) != null)
                                            return check;
                                    }
                                    idx = q2 + 1;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // Method 3: Installer placed next to game exe
            string selfDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(selfDir) && FindGameExe(selfDir) != null)
                return selfDir;

            return null;
        }

        private static int CopyDirectory(string source, string target)
        {
            int count = 0;
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string rel = dir.Substring(source.Length).TrimStart('\\');
                Directory.CreateDirectory(Path.Combine(target, rel));
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(source.Length).TrimStart('\\');
                string dest = Path.Combine(target, rel);
                File.Copy(file, dest, true);
                // Remove Windows "downloaded from internet" block — without this,
                // .NET/Mono refuses to load BepInEx DLLs on other users' machines
                NativeMethods.DeleteFile(dest + ":Zone.Identifier");
                count++;
            }

            return count;
        }

        // ========== FONT INSTALLATION ==========

        private static bool IsRunningAsAdmin()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // Per-user font installation (HKCU + %LOCALAPPDATA%\Microsoft\Windows\Fonts)
        // is only supported since Windows 10 version 1809 (build 17763). Earlier
        // builds will accept the registry write but GDI/FontCache won't pick it up.
        // Environment.OSVersion lies on Win10/11 without an OS-version manifest, so
        // we read the build number from the registry instead.
        private static bool SupportsPerUserFonts()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null) return false;
                    string buildStr = key.GetValue("CurrentBuildNumber") as string;
                    int build;
                    return !string.IsNullOrEmpty(buildStr)
                        && int.TryParse(buildStr, out build)
                        && build >= 17763;
                }
            }
            catch { return false; }
        }

        private static bool FilesAreEqual(string a, string b)
        {
            try
            {
                var fiA = new FileInfo(a);
                var fiB = new FileInfo(b);
                if (!fiA.Exists || !fiB.Exists) return false;
                if (fiA.Length != fiB.Length) return false;

                const int bufSize = 64 * 1024;
                using (var sa = fiA.OpenRead())
                using (var sb = fiB.OpenRead())
                {
                    byte[] ba = new byte[bufSize];
                    byte[] bb = new byte[bufSize];
                    int read;
                    while ((read = sa.Read(ba, 0, bufSize)) > 0)
                    {
                        int read2 = sb.Read(bb, 0, bufSize);
                        if (read != read2) return false;
                        for (int i = 0; i < read; i++)
                            if (ba[i] != bb[i]) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private bool InstallFonts(string pluginDir, out string failureDetails)
        {
            var failures = new System.Collections.Generic.List<string>();
            bool admin = IsRunningAsAdmin();
            bool perUserOk = SupportsPerUserFonts();
            bool anyRegistered = false;
            bool allOk = true;

            string winFontsDir = Path.Combine(
                Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts");
            string userFontsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts");

            foreach (var font in FontsToInstall)
            {
                string srcPath = Path.Combine(pluginDir, font.FileName);
                if (!File.Exists(srcPath))
                {
                    failures.Add(font.FileName + ": не найден в " + pluginDir);
                    allOk = false;
                    continue;
                }

                bool installed = false;
                string lastError = null;

                // Strategy 1: system-wide install (only with admin rights)
                if (admin)
                {
                    string destPath = Path.Combine(winFontsDir, font.FileName);
                    try
                    {
                        if (!File.Exists(destPath) || !FilesAreEqual(srcPath, destPath))
                        {
                            // Try to copy. If the file is locked by Font Cache Service,
                            // first unregister, then retry the copy once.
                            try
                            {
                                File.Copy(srcPath, destPath, true);
                            }
                            catch (Exception copyEx)
                                when (copyEx is IOException || copyEx is UnauthorizedAccessException)
                            {
                                try { NativeMethods.RemoveFontResourceEx(destPath, 0, IntPtr.Zero); } catch { }
                                File.Copy(srcPath, destPath, true);
                            }
                        }

                        using (var key = Registry.LocalMachine.OpenSubKey(FontsRegSubKey, true))
                        {
                            if (key == null)
                                throw new InvalidOperationException("HKLM\\" + FontsRegSubKey + " недоступен");
                            key.SetValue(font.RegName, font.FileName);
                        }

                        NativeMethods.AddFontResourceEx(destPath, 0, IntPtr.Zero);

                        // Verify by re-reading the registry — most reliable signal
                        using (var key = Registry.LocalMachine.OpenSubKey(FontsRegSubKey))
                        {
                            string val = key != null ? key.GetValue(font.RegName) as string : null;
                            if (string.Equals(val, font.FileName, StringComparison.OrdinalIgnoreCase))
                            {
                                Log("   " + font.FileName + " -> " + winFontsDir + " [система]",
                                    Color.FromArgb(100, 200, 120));
                                installed = true;
                                anyRegistered = true;
                            }
                            else
                            {
                                lastError = "запись в HKLM не подтверждена (значение: '" + (val ?? "<нет>") + "')";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = "система: " + ex.Message;
                    }
                }

                // Strategy 2: per-user install (no admin needed, Win10 1809+ only)
                if (!installed && perUserOk)
                {
                    string destPath = Path.Combine(userFontsDir, font.FileName);
                    try
                    {
                        Directory.CreateDirectory(userFontsDir);

                        if (!File.Exists(destPath) || !FilesAreEqual(srcPath, destPath))
                        {
                            try
                            {
                                File.Copy(srcPath, destPath, true);
                            }
                            catch (Exception copyEx)
                                when (copyEx is IOException || copyEx is UnauthorizedAccessException)
                            {
                                try { NativeMethods.RemoveFontResourceEx(destPath, 0, IntPtr.Zero); } catch { }
                                File.Copy(srcPath, destPath, true);
                            }
                        }

                        // HKCU stores the FULL PATH (different from HKLM convention)
                        using (var key = Registry.CurrentUser.CreateSubKey(FontsRegSubKey))
                        {
                            if (key == null)
                                throw new InvalidOperationException("HKCU\\" + FontsRegSubKey + " недоступен");
                            key.SetValue(font.RegName, destPath);
                        }

                        NativeMethods.AddFontResourceEx(destPath, 0, IntPtr.Zero);

                        using (var key = Registry.CurrentUser.OpenSubKey(FontsRegSubKey))
                        {
                            string val = key != null ? key.GetValue(font.RegName) as string : null;
                            if (string.Equals(val, destPath, StringComparison.OrdinalIgnoreCase))
                            {
                                Log("   " + font.FileName + " -> " + userFontsDir + " [для пользователя]",
                                    Color.FromArgb(100, 200, 120));
                                installed = true;
                                anyRegistered = true;
                            }
                            else
                            {
                                lastError = "запись в HKCU не подтверждена";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (lastError != null) lastError += "; ";
                        lastError = (lastError ?? "") + "пользователь: " + ex.Message;
                    }
                }
                else if (!installed && !perUserOk && !admin)
                {
                    // Neither admin nor Win10 1809+ — nothing we can do automatically.
                    if (lastError == null)
                        lastError = "нет прав администратора, а Windows < 10 1809 не поддерживает установку для пользователя";
                }

                if (!installed)
                {
                    allOk = false;
                    failures.Add(font.FileName + ": " + (lastError ?? "неизвестная ошибка"));
                }
            }

            // Notify all top-level windows that the font set has changed so the
            // Font Cache Service rescans before the game launches.
            if (anyRegistered)
            {
                try
                {
                    IntPtr res;
                    NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST,
                        NativeMethods.WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero,
                        NativeMethods.SMTO_ABORTIFHUNG, 1500, out res);
                }
                catch { }
            }

            failureDetails = allOk ? null : string.Join("\n", failures.ToArray());
            return allOk;
        }

        private void ShowFontInstallFailedDialog(string pluginDir)
        {
            string msg =
                "ВНИМАНИЕ: шрифты для русификации не удалось установить автоматически.\n\n" +
                "Без них пиксельный шрифт игры заменяется на Arial,\n" +
                "и имена персонажей могут наезжать на возраст и интерфейс.\n\n" +
                "КАК УСТАНОВИТЬ ВРУЧНУЮ:\n" +
                "1. Нажмите «Да» — откроется папка с .ttf файлами\n" +
                "2. Кликните правой кнопкой по LanaPixel.ttf\n" +
                "3. На Windows 11 выберите «Показать дополнительные параметры»\n" +
                "4. Нажмите «Установить для всех пользователей»\n" +
                "5. Подтвердите запрос UAC, если он появится\n" +
                "6. Повторите шаги 2–5 для PressStart2P-Regular.ttf\n" +
                "7. Перезапустите игру\n\n" +
                "Открыть папку с шрифтами сейчас?";

            DialogResult result;
            try
            {
                result = MessageBox.Show(this, msg,
                    "Шрифты не установлены автоматически",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            }
            catch
            {
                return;
            }

            if (result == DialogResult.Yes && Directory.Exists(pluginDir))
            {
                try { Process.Start("explorer.exe", "\"" + pluginDir + "\""); }
                catch { }
            }
        }

        private static void TryDeleteStaleDiagLog(string pluginDir)
        {
            try
            {
                string log = Path.Combine(pluginDir, "NotTonightRussian_diag.txt");
                if (File.Exists(log)) File.Delete(log);
            }
            catch { }
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            // Enable DPI awareness before any UI is created
            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
        }
    }
}
