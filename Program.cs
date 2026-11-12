using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Text;

namespace FolderThumbnailFix
{
    class Program
    {
        static string myName = typeof(Program).Namespace;
        static string myPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        static string myExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        static string appParts = $@"{myPath}\AppParts";
        static string Icons = $@"{myPath}\AppParts\Icons";
        static string myIcon = $@"{Icons}\{myName}.ico";
        static string iconFull = $@"{Icons}\Transparent.ico";
        static string iconHalf = $@"{Icons}\HalfMask.ico";
        static string ResourceHacker = $@"{appParts}\ResourceHacker\ResourceHacker.exe";
        static string munFile = @"C:\Windows\SystemResources\imageres.dll.mun";
        static string oldFile = @"C:\Windows\SystemResources\imageres.dll.mun.old";
        static string newFile = @"C:\Windows\SystemResources\imageres.dll.mun.new";
        static string tempFile = Path.Combine(Path.GetTempPath(), "imageres.dll.mun");
        static string Option = "";

        static string sSetup = "Select thumbnail style:";
        static string sOK = "OK";
        static string sBuildError = "Windows 11 required";

        static float ScaleFactor = GetScale();
        static bool Dark = isDark();
        static bool ctrlKey = false;

        static string helpPage = "what-it-does";

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                Directory.SetCurrentDirectory(@"C:\");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                UnblockPath(myPath);

                ctrlKey = (GetAsyncKeyState(0x11) & 0x8000) != 0;

                string NTkey = @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion";
                int buildNumber = int.Parse(Registry.GetValue(NTkey, "CurrentBuild", "").ToString());

                if (!ctrlKey && buildNumber < 21996)
                {
                    CustomMessageBox.Show(sBuildError, myName);
                    return;
                }

                if (args.Length == 0) { InstallRemove(); return; }

                Option = args[0];

                switch (Option.ToLower())
                {
                    case "/install":
                        RunUAC(myExe, "/installUAC");
                        break;

                    case "/remove":
                        RunUAC(myExe, "/removeUAC");
                        break;

                    case "/installuac":
                        KillExplorer();
                        RunMeTrusted("/InstallTrusted");
                        Thread.Sleep(1000);
                        ResetThumbCache();
                        Thread.Sleep(1000);
                        Process.Start("explorer.exe");
                        break;

                    case "/removeuac":
                        KillExplorer();
                        RunMeTrusted("/RemoveTrusted");
                        Thread.Sleep(1000);
                        ResetThumbCache();
                        Thread.Sleep(1000);
                        Process.Start("explorer.exe");
                        break;

                    // Apply any icon style: FolderThumbnailFix.exe /apply "MyStyle.ico"
                    // (a bare file name is looked up in AppParts\Icons)
                    case "/apply":
                        if (args.Length < 2) return;
                        RunUAC(myExe, $"/applyUAC \"{ResolveStyle(args[1])}\"");
                        break;

                    case "/applyuac":
                        if (args.Length < 2) return;
                        if (!IsValidStyleIcon(args[1])) return;
                        KillExplorer();
                        RunMeTrusted($"/ApplyTrusted \"{args[1]}\"");
                        Thread.Sleep(1000);
                        ResetThumbCache();
                        Thread.Sleep(1000);
                        Process.Start("explorer.exe");
                        break;

                    case "/applytrusted":
                        if (args.Length < 2) return;
                        if (IsValidStyleIcon(args[1])) ReplaceIcon(args[1]);
                        break;

                    case "/installtrusted":
                        ReplaceIcon(iconFull);
                        break;

                    case "/removetrusted":
                        ReplaceIcon(iconHalf);
                        break;

                    default:
                        return;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC prompt was canceled by the user; do nothing and exit silently.
                return;
            }
            catch (Exception ex)
            {
                AllocConsole();
                Console.WriteLine($"An error occurred: {ex.Message}");
                Console.WriteLine("Press Enter to exit...");
                Console.ReadLine();
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllocConsole();

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteFile(string name);

        const int LIST_MODULES_ALL = 3;
        const uint PROCESS_QUERY_INFORMATION = 0x0400;
        const uint PROCESS_VM_READ = 0x0010;

        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EnumProcessModulesEx(IntPtr hProcess, IntPtr[] lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, StringBuilder lpFilename, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        public static void UnblockPath(string path)
        {
            string[] files = Directory.GetFiles(path);
            string[] dirs = Directory.GetDirectories(path);
            foreach (string file in files)
            {
                UnblockFile(file);
            }
            foreach (string dir in dirs)
            {
                UnblockPath(dir);
            }
        }

        public static bool UnblockFile(string fileName)
        {
            return DeleteFile(fileName + ":Zone.Identifier");
        }

        // Turn a bare file name into a full path inside AppParts\Icons
        static string ResolveStyle(string style)
        {
            if (Path.IsPathRooted(style)) return style;
            return Path.Combine(Icons, style);
        }

        // Only allow .ico files that live in AppParts\Icons to be written into the system file
        static bool IsValidStyleIcon(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string dir = Path.GetFullPath(Icons).TrimEnd('\\') + "\\";
                return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                    && full.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(full);
            }
            catch { return false; }
        }

        // Any extra .ico files the user has dropped into AppParts\Icons
        static string[] GetCustomStyles()
        {
            List<string> styles = new List<string>();
            try
            {
                foreach (string file in Directory.GetFiles(Icons, "*.ico"))
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals(Path.GetFileName(myIcon), StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals(Path.GetFileName(iconFull), StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals(Path.GetFileName(iconHalf), StringComparison.OrdinalIgnoreCase)) continue;
                    styles.Add(file);
                }
            }
            catch { }
            styles.Sort(StringComparer.OrdinalIgnoreCase);
            return styles.ToArray();
        }

        static void InstallRemove()
        {
            // If extra styles are present, show the picker instead of the two-button dialog
            string[] custom = GetCustomStyles();
            if (custom.Length > 0)
            {
                string chosen = StylePicker.Show(sSetup, myName, custom);
                if (chosen != null) RunUAC(myExe, $"/applyUAC \"{chosen}\"");
                return;
            }

            DialogResult result = TwoChoiceBox.Show(sSetup, myName, "", "");

            if (result == DialogResult.Yes)
            {
                RunUAC(myExe, "/installUAC");
            }
            if (result == DialogResult.No)
            {
                RunUAC(myExe, "/removeUAC");
            }
        }

        static void RunUAC(string fileName, string CommandLine)
        {
            Process p = new Process();
            p.StartInfo.FileName = fileName;
            p.StartInfo.Arguments = CommandLine;
            p.StartInfo.UseShellExecute = true;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.Verb = "runas";
            p.Start();
            p.WaitForExit();
        }

        static void KillExplorer()
        {
            using (Process p = new Process())
            {
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/f /im explorer.exe",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                };
                p.Start();
                p.WaitForExit();
            }
        }
        static void ResetThumbCache()
        {
            DeleteCacheFiles("thumbcache_*.db");
        }

        static void DeleteCacheFiles(string searchPattern)
        {
            string targetDirectory = $@"{Environment.GetEnvironmentVariable("LocalAppData")}\Microsoft\Windows\Explorer";

            try
            {
                string[] files = Directory.GetFiles(targetDirectory, searchPattern, SearchOption.TopDirectoryOnly);

                foreach (string file in files)
                {
                    try { File.Delete(file); }
                    catch { }
                }
            }
            catch { }
        }

        static void RunMeTrusted(string action)
        {
            ServiceController sc = new ServiceController
            {
                ServiceName = "TrustedInstaller",
            };

            if (sc.Status != ServiceControllerStatus.Running) sc.Start();

            Process[] proc = Process.GetProcessesByName("TrustedInstaller");

            proc = Process.GetProcessesByName("TrustedInstaller");
            TrustedInstaller.Run(proc[0].Id, $"\"{myExe}\" {action}");
        }

        static void ReplaceIcon(string icon)
        {
            try
            {
                File.Copy(munFile, tempFile, true);

                Process p = new Process();
                p.StartInfo.FileName = ResourceHacker;
                p.StartInfo.Arguments = $" -open \"{tempFile}\" -save \"{tempFile}\" -resource \"{icon}\" -action addoverwrite -mask icongroup,6,1033";
                p.StartInfo.UseShellExecute = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                p.WaitForExit();

                Thread.Sleep(500);

                File.Copy(tempFile, newFile, true);

                if (File.Exists(newFile))
                {
                    if (File.Exists(oldFile)) File.Delete(oldFile);
                    File.Move(munFile, oldFile);
                    File.Move(newFile, munFile);
                    File.Delete(oldFile);
                }
                else
                {
                    AllocConsole();
                    Console.WriteLine("Copy to .new failed. Aborting renames.");
                    Console.ReadLine();
                }
            }
            catch (Exception ex)
            {
                AllocConsole();
                Console.WriteLine("Error: " + ex.Message);
                Console.ReadLine();
            }
        }

        // Get current screen scaling factor
        static float GetScale()
        {
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
            {
                float dpiX = graphics.DpiX;
                return dpiX / 96;
            }
        }

        // Determine if dark colors (theme) are being used
        public static bool isDark()
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            const string valueName = "AppsUseLightTheme";

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                if (key != null)
                {
                    object value = key.GetValue(valueName);
                    if (value is int intValue)
                    {
                        return intValue == 0;
                    }
                }
            }
            return false; // Return false if the key or value is missing
        }

        // Make dialog title bar black
        public enum DWMWINDOWATTRIBUTE : uint
        {
            DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
        }

        [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void DwmSetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE attribute, ref int pvAttribute, uint cbAttribute);

        static void DarkTitleBar(IntPtr hWnd)
        {
            var preference = Convert.ToInt32(true);
            DwmSetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, ref preference, sizeof(uint));

        }

        // Dialog for simple OK messages
        public class CustomMessageBox : Form
        {
            private Label messageLabel;
            private Label buttonHelp;
            private Button buttonOK;

            public CustomMessageBox(string message, string caption)
            {
                message = $"\n{message}";

                Icon = new Icon(myIcon);
                StartPosition = FormStartPosition.Manual;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                Text = caption;
                Width = (int)(300 * ScaleFactor);
                Height = (int)(150 * ScaleFactor);
                MaximizeBox = false;
                MinimizeBox = false;

                messageLabel = new Label();
                messageLabel.Text = message;
                messageLabel.Font = new Font("Segoe UI", 10);
                messageLabel.TextAlign = ContentAlignment.TopCenter;
                messageLabel.Dock = DockStyle.Fill;

                using (Graphics g = CreateGraphics())
                {
                    SizeF size = g.MeasureString(message, new Font("Segoe UI", 10), Width);
                    Height = Math.Max(Height, (int)(size.Height + (int)(100 * ScaleFactor)));
                }

                buttonHelp = new Label();
                Image image = Image.FromFile($@"{appParts}\Icons\Question.png");
                Bitmap scaledImage = new Bitmap((int)(26 * ScaleFactor), (int)(26 * ScaleFactor));
                using (Graphics g = Graphics.FromImage(scaledImage))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, (int)(26 * ScaleFactor), (int)(26 * ScaleFactor));
                }
                buttonHelp.BackgroundImage = scaledImage;
                buttonHelp.BackgroundImageLayout = ImageLayout.Stretch;
                buttonHelp.Size = new Size((int)(26 * ScaleFactor), (int)(26 * ScaleFactor));
                buttonHelp.FlatStyle = FlatStyle.Flat;
                buttonHelp.Left = ClientSize.Width - (int)(30 * ScaleFactor);
                buttonHelp.Top = (int)(4 * ScaleFactor);
                buttonHelp.Click += ButtonHelp_Click;

                messageLabel.Padding = new Padding(0, 0, (int)(26 * ScaleFactor), 0);

                buttonOK = new Button();
                buttonOK.Text = sOK;
                buttonOK.DialogResult = DialogResult.OK;
                buttonOK.Font = new Font("Segoe UI", 9);
                buttonOK.Width = (int)(75 * ScaleFactor);
                buttonOK.Height = (int)(26 * ScaleFactor);
                buttonOK.Left = (ClientSize.Width - buttonOK.Width) / 2;
                buttonOK.Top = ClientSize.Height - buttonOK.Height - (int)(10 * ScaleFactor);
                if (Dark)
                {
                    buttonOK.FlatStyle = FlatStyle.Flat;
                    buttonOK.FlatAppearance.BorderColor = SystemColors.Highlight;
                    buttonOK.FlatAppearance.BorderSize = 1;
                    buttonOK.BackColor = Color.FromArgb(60, 60, 60);
                    buttonOK.FlatAppearance.MouseOverBackColor = Color.Black;
                    DarkTitleBar(Handle);
                    BackColor = Color.FromArgb(32, 32, 32);
                    ForeColor = Color.White;
                }

                if (Dark)
                {
                    DarkTitleBar(Handle);
                    BackColor = Color.FromArgb(32, 32, 32);
                    ForeColor = Color.White;
                }

                Controls.Add(buttonHelp);
                Controls.Add(buttonOK);
                Controls.Add(messageLabel);

                Point cursorPosition = Cursor.Position;
                int dialogX = Cursor.Position.X - Width / 2;
                int dialogY = Cursor.Position.Y - Height / 2 - (int)(50 * ScaleFactor);
                Screen screen = Screen.FromPoint(cursorPosition);
                int screenWidth = screen.WorkingArea.Width;
                int screenHeight = screen.WorkingArea.Height;
                int baseX = screen.Bounds.X;
                int baseY = screen.Bounds.Y;
                dialogX = Math.Max(baseX, Math.Min(baseX + screenWidth - Width, dialogX));
                dialogY = Math.Max(baseY, Math.Min(baseY + screenHeight - Height, dialogY));
                Location = new Point(dialogX, dialogY);
            }
            public static DialogResult Show(string message, string caption)
            {
                using (var customMessageBox = new CustomMessageBox(message, caption))
                {
                    return customMessageBox.ShowDialog();
                }
            }

        }

        // Dialog for install/Remove
        public class TwoChoiceBox : Form
        {
            private Label messageLabel;
            private Label buttonHelp;
            private Label buttonYes;
            private Label buttonNo;
            Bitmap imageHelp;
            Bitmap imageHelpHover;
            Bitmap imageYes;
            Bitmap imageYesHover;
            Bitmap imageNo;
            Bitmap imageNoHover;

            public TwoChoiceBox(string message, string caption, string button1, string button2)
            {
                message = $"\n{message}";

                Icon = new Icon(myIcon);
                StartPosition = FormStartPosition.Manual;
                Text = caption;
                Width = (int)(300 * ScaleFactor);
                Height = (int)(192 * ScaleFactor);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;

                messageLabel = new Label();
                messageLabel.Text = message;
                messageLabel.Font = new Font("Segoe UI", 10);
                messageLabel.TextAlign = ContentAlignment.TopCenter;
                messageLabel.Dock = DockStyle.Fill;
                messageLabel.Padding = new Padding(0, 0, (int)(26 * ScaleFactor), 0);

                using (Graphics g = CreateGraphics())
                {
                    SizeF size = g.MeasureString(message, new Font("Segoe UI", 10), Width);
                    Height = Math.Max(Height, (int)(size.Height + (int)(100 * ScaleFactor)));
                }

                buttonHelp = new Label();
				
                Image image = Image.FromFile($@"{appParts}\Icons\Question.png");
                imageHelp = new Bitmap((int)(26 * ScaleFactor), (int)(26 * ScaleFactor));
                using (Graphics g = Graphics.FromImage(imageHelp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, (int)(26 * ScaleFactor), (int)(26 * ScaleFactor));
                }

                string hoverImage = "QuestionL.png";
                if (Dark) hoverImage = "QuestionD.png";
                image = Image.FromFile($@"{appParts}\Icons\{hoverImage}");
                imageHelpHover = new Bitmap((int)(26 * ScaleFactor), (int)(26 * ScaleFactor));
                using (Graphics g = Graphics.FromImage(imageHelpHover))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, (int)(26 * ScaleFactor), (int)(26 * ScaleFactor));
                }

                buttonHelp.MouseEnter += buttonHelp_MouseEnter;
                buttonHelp.MouseLeave += buttonHelp_MouseLeave;

                buttonHelp.BackgroundImage = imageHelp;
                buttonHelp.BackgroundImageLayout = ImageLayout.Stretch;
                buttonHelp.Size = new Size((int)(26 * ScaleFactor), (int)(26 * ScaleFactor));
                buttonHelp.FlatStyle = FlatStyle.Flat;
                buttonHelp.Left = ClientSize.Width - (int)(30 * ScaleFactor);
                buttonHelp.Top = (int)(4 * ScaleFactor);
                buttonHelp.Click += ButtonHelp_Click;

                buttonYes = new Label();
				
                image = Image.FromFile($@"{appParts}\Icons\ThumbFull.png");
                imageYes = new Bitmap((int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                using (Graphics g = Graphics.FromImage(imageYes))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, (int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                }

                hoverImage = "ThumbFullL.png";
                if (Dark) hoverImage = "ThumbFullD.png";
                image = Image.FromFile($@"{appParts}\Icons\{hoverImage}");
                imageYesHover = new Bitmap((int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                using (Graphics g = Graphics.FromImage(imageYesHover))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, (int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                }

                buttonYes.MouseEnter += buttonYes_MouseEnter;
                buttonYes.MouseLeave += buttonYes_MouseLeave;

                buttonYes.BackgroundImage = imageYes;
                buttonYes.BackgroundImageLayout = ImageLayout.Stretch;
                buttonYes.Size = new Size((int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                buttonYes.FlatStyle = FlatStyle.Flat;

                buttonYes.Left = (int)(10 * ScaleFactor);
                buttonYes.Top = ClientSize.Height - buttonYes.Height;
                buttonYes.Click += (s, e) => { this.DialogResult = DialogResult.Yes; this.Close(); };

                buttonNo = new Label();
				
                image = Image.FromFile($@"{appParts}\Icons\ThumbHalf.png");
                imageNo = new Bitmap((int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                using (Graphics g = Graphics.FromImage(imageNo))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, (int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                }

                hoverImage = "ThumbHalfL.png";
                if (Dark) hoverImage = "ThumbHalfD.png";
                image = Image.FromFile($@"{appParts}\Icons\{hoverImage}");
                imageNoHover = new Bitmap((int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                using (Graphics g = Graphics.FromImage(imageNoHover))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, (int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                }

                buttonNo.MouseEnter += buttonNo_MouseEnter;
                buttonNo.MouseLeave += buttonNo_MouseLeave;

                buttonNo.BackgroundImage = imageNo;
                buttonNo.BackgroundImageLayout = ImageLayout.Stretch;
                buttonNo.Size = new Size((int)(96 * ScaleFactor), (int)(96 * ScaleFactor));
                buttonNo.FlatStyle = FlatStyle.Flat;

                buttonNo.Left = ClientSize.Width - (int)(108 * ScaleFactor);
                buttonNo.Top = ClientSize.Height - buttonYes.Height;
                buttonNo.Click += (s, e) => { this.DialogResult = DialogResult.No; this.Close(); };


                if (Dark)
                {
                    DarkTitleBar(Handle);
                    BackColor = Color.FromArgb(32, 32, 32);
                    ForeColor = Color.White;
                }

                Controls.Add(buttonHelp);
                Controls.Add(buttonYes);
                Controls.Add(buttonNo);
                Controls.Add(messageLabel);

                Point cursorPosition = Cursor.Position;
                int dialogX = Cursor.Position.X - Width / 2;
                int x = 50;
                int dialogY = Cursor.Position.Y - Height / 2 - (int)(x * ScaleFactor);
                Screen screen = Screen.FromPoint(cursorPosition);
                int screenWidth = screen.WorkingArea.Width;
                int screenHeight = screen.WorkingArea.Height;
                int baseX = screen.Bounds.X;
                int baseY = screen.Bounds.Y;
                dialogX = Math.Max(baseX, Math.Min(baseX + screenWidth - Width, dialogX));
                dialogY = Math.Max(baseY, Math.Min(baseY + screenHeight - Height, dialogY));
                Location = new Point(dialogX, dialogY);
            }

            private void buttonHelp_MouseEnter(object sender, EventArgs e)
            {
                buttonHelp.BackgroundImage = imageHelpHover;
            }
            private void buttonHelp_MouseLeave(object sender, EventArgs e)
            {
                buttonHelp.BackgroundImage = imageHelp;
            }
            private void buttonYes_MouseEnter(object sender, EventArgs e)
            {
                buttonYes.BackgroundImage = imageYesHover;
            }
            private void buttonYes_MouseLeave(object sender, EventArgs e)
            {
                buttonYes.BackgroundImage = imageYes;
            }
            private void buttonNo_MouseEnter(object sender, EventArgs e)
            {
                buttonNo.BackgroundImage = imageNoHover;
            }
            private void buttonNo_MouseLeave(object sender, EventArgs e)
            {
                buttonNo.BackgroundImage = imageNo;
            }
            public static DialogResult Show(string message, string caption, string button1, string button2)
            {
                using (var TwoChoiceBox = new TwoChoiceBox(message, caption, button1, button2))
                {
                    return TwoChoiceBox.ShowDialog();
                }
            }
        }

        // Dialog for choosing between the built-in styles and any custom .ico styles
        public class StylePicker : Form
        {
            private ListView list;
            private ImageList images;
            private Button buttonApply;
            private Button buttonCancel;
            private int imgSize;
            public string SelectedIcon = null;

            public StylePicker(string message, string caption, string[] customStyles)
            {
                Icon = new Icon(myIcon);
                Text = caption;
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ClientSize = new Size((int)(460 * ScaleFactor), (int)(380 * ScaleFactor));

                Label messageLabel = new Label();
                messageLabel.Text = message;
                messageLabel.Font = new Font("Segoe UI", 10);
                messageLabel.TextAlign = ContentAlignment.MiddleCenter;
                messageLabel.Dock = DockStyle.Top;
                messageLabel.Height = (int)(40 * ScaleFactor);

                imgSize = (int)(96 * ScaleFactor);
                images = new ImageList();
                images.ColorDepth = ColorDepth.Depth32Bit;
                images.ImageSize = new Size(imgSize, imgSize);

                list = new ListView();
                list.View = View.LargeIcon;
                list.LargeImageList = images;
                list.MultiSelect = false;
                list.HideSelection = false;
                list.BorderStyle = BorderStyle.None;
                list.Font = new Font("Segoe UI", 9);
                list.Dock = DockStyle.Fill;
                list.ItemActivate += (s, e) => Accept();
                list.SelectedIndexChanged += (s, e) => { buttonApply.Enabled = list.SelectedItems.Count > 0; };

                // Built-in styles first, then anything found in the Icons folder
                AddStyle("Full size", iconFull, $@"{Icons}\ThumbFull.png");
                AddStyle("Half (default)", iconHalf, $@"{Icons}\ThumbHalf.png");
                foreach (string file in customStyles)
                {
                    // An optional PNG with the same name is used as the preview image
                    AddStyle(Path.GetFileNameWithoutExtension(file), file, Path.ChangeExtension(file, ".png"));
                }

                Panel bottom = new Panel();
                bottom.Dock = DockStyle.Bottom;
                bottom.Height = (int)(44 * ScaleFactor);

                buttonCancel = new Button();
                buttonCancel.Text = "Cancel";
                buttonCancel.DialogResult = DialogResult.Cancel;
                buttonCancel.Width = (int)(80 * ScaleFactor);
                buttonCancel.Height = (int)(28 * ScaleFactor);
                buttonCancel.Left = bottom.ClientSize.Width - buttonCancel.Width - (int)(12 * ScaleFactor);
                buttonCancel.Top = (int)(8 * ScaleFactor);
                buttonCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                buttonApply = new Button();
                buttonApply.Text = "Apply";
                buttonApply.Enabled = false;
                buttonApply.Width = (int)(80 * ScaleFactor);
                buttonApply.Height = (int)(28 * ScaleFactor);
                buttonApply.Left = buttonCancel.Left - buttonApply.Width - (int)(8 * ScaleFactor);
                buttonApply.Top = (int)(8 * ScaleFactor);
                buttonApply.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                buttonApply.Click += (s, e) => Accept();

                bottom.Controls.Add(buttonApply);
                bottom.Controls.Add(buttonCancel);
                CancelButton = buttonCancel;

                if (Dark)
                {
                    DarkTitleBar(Handle);
                    BackColor = Color.FromArgb(32, 32, 32);
                    ForeColor = Color.White;
                    list.BackColor = Color.FromArgb(32, 32, 32);
                    list.ForeColor = Color.White;
                    foreach (Button b in new Button[] { buttonApply, buttonCancel })
                    {
                        b.FlatStyle = FlatStyle.Flat;
                        b.BackColor = Color.FromArgb(60, 60, 60);
                        b.FlatAppearance.MouseOverBackColor = Color.Black;
                    }
                }

                // Fill control first, then the docked edges
                Controls.Add(list);
                Controls.Add(bottom);
                Controls.Add(messageLabel);
            }

            private void AddStyle(string label, string iconPath, string previewPng)
            {
                images.Images.Add(MakePreview(iconPath, previewPng, imgSize));
                ListViewItem item = new ListViewItem(label, images.Images.Count - 1);
                item.Tag = iconPath;
                list.Items.Add(item);
            }

            // Mask icons can be fully transparent, so prefer a PNG preview if there is one;
            // otherwise draw the icon over a gray background so it is visible.
            private static Bitmap MakePreview(string iconPath, string previewPng, int size)
            {
                Bitmap bmp = new Bitmap(size, size);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    try
                    {
                        if (File.Exists(previewPng))
                        {
                            using (Image img = Image.FromFile(previewPng))
                            {
                                g.DrawImage(img, 0, 0, size, size);
                            }
                        }
                        else
                        {
                            g.Clear(Color.FromArgb(128, 128, 128));
                            using (Icon ico = new Icon(iconPath, new Size(size, size)))
                            {
                                g.DrawIcon(ico, new Rectangle(0, 0, size, size));
                            }
                        }
                    }
                    catch { }
                }
                return bmp;
            }

            private void Accept()
            {
                if (list.SelectedItems.Count == 0) return;
                SelectedIcon = (string)list.SelectedItems[0].Tag;
                DialogResult = DialogResult.OK;
                Close();
            }

            public static string Show(string message, string caption, string[] customStyles)
            {
                using (var picker = new StylePicker(message, caption, customStyles))
                {
                    return picker.ShowDialog() == DialogResult.OK ? picker.SelectedIcon : null;
                }
            }
        }

        static void ButtonHelp_Click(object sender, EventArgs e)
        {
            Process.Start("https://lesferch.github.io/FolderThumbnailFix#" + helpPage);
        }
    }

    //Credit for the following TrustedInstaller code: https://github.com/rara64/GetTrustedInstaller
    class TrustedInstaller
    {
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateProcess(string lpApplicationName, string lpCommandLine, ref SECURITY_ATTRIBUTES lpProcessAttributes, ref SECURITY_ATTRIBUTES lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, [In] ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern UInt32 WaitForSingleObject(IntPtr handle, UInt32 milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetHandleInformation(IntPtr hObject, HANDLE_FLAGS dwMask, HANDLE_FLAGS dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, ref IntPtr lpTargetHandle, uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

        public static void Run(int parentProcessId, string binaryPath)
        {
            const int PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x00020000;

            const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
            const uint CREATE_NEW_CONSOLE = 0x00000010;

            var pInfo = new PROCESS_INFORMATION();
            var siEx = new STARTUPINFOEX();

            IntPtr lpValueProc = IntPtr.Zero;
            IntPtr hSourceProcessHandle = IntPtr.Zero;
            var lpSize = IntPtr.Zero;

            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
            siEx.lpAttributeList = Marshal.AllocHGlobal(lpSize);
            InitializeProcThreadAttributeList(siEx.lpAttributeList, 1, 0, ref lpSize);

            IntPtr parentHandle = OpenProcess(ProcessAccessFlags.CreateProcess | ProcessAccessFlags.DuplicateHandle, false, parentProcessId);

            lpValueProc = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(lpValueProc, parentHandle);

            UpdateProcThreadAttribute(siEx.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, lpValueProc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);

            var ps = new SECURITY_ATTRIBUTES();
            var ts = new SECURITY_ATTRIBUTES();
            ps.nLength = Marshal.SizeOf(ps);
            ts.nLength = Marshal.SizeOf(ts);

            // lpCommandLine was used instead of lpApplicationName to allow for arguments to be passed
            bool ret = CreateProcess(null, binaryPath, ref ps, ref ts, true, EXTENDED_STARTUPINFO_PRESENT | CREATE_NEW_CONSOLE, IntPtr.Zero, null, ref siEx, out pInfo);

            String stringPid = pInfo.dwProcessId.ToString();

        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public Int32 cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public Int32 dwX;
            public Int32 dwY;
            public Int32 dwXSize;
            public Int32 dwYSize;
            public Int32 dwXCountChars;
            public Int32 dwYCountChars;
            public Int32 dwFillAttribute;
            public Int32 dwFlags;
            public Int16 wShowWindow;
            public Int16 cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [Flags]
        public enum ProcessAccessFlags : uint
        {
            All = 0x001F0FFF,
            Terminate = 0x00000001,
            CreateThread = 0x00000002,
            VirtualMemoryOperation = 0x00000008,
            VirtualMemoryRead = 0x00000010,
            VirtualMemoryWrite = 0x00000020,
            DuplicateHandle = 0x00000040,
            CreateProcess = 0x000000080,
            SetQuota = 0x00000100,
            SetInformation = 0x00000200,
            QueryInformation = 0x00000400,
            QueryLimitedInformation = 0x00001000,
            Synchronize = 0x00100000
        }

        [Flags]
        enum HANDLE_FLAGS : uint
        {
            None = 0,
            INHERIT = 1,
            PROTECT_FROM_CLOSE = 2
        }
    }

}
