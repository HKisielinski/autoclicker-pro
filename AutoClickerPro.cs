using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualBasic;

namespace AutoClickerApp
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        // --- Win32 interop ---
        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct POINTAPI { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public POINTAPI pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // A single recorded step: either a click at a screen position, or a key press.
        struct SequenceStep
        {
            public bool IsKey;
            public Point Position;
            public Keys Key;

            public static SequenceStep ForClick(Point p) { return new SequenceStep { IsKey = false, Position = p }; }
            public static SequenceStep ForKey(Keys k) { return new SequenceStep { IsKey = true, Key = k }; }
        }

        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        const uint KEYEVENTF_KEYUP = 0x0002;

        const int WM_HOTKEY = 0x0312;
        const int HOTKEY_ID = 1;
        const int HOTKEY_ID_RECORD = 2;
        const uint VK_F6 = 0x75;
        const uint VK_F7 = 0x76;

        const int WH_MOUSE_LL = 14;
        const int WH_KEYBOARD_LL = 13;
        const int WM_LBUTTONDOWN_HOOK = 0x0201;
        const int WM_KEYDOWN_HOOK = 0x0100;
        const int WM_SYSKEYDOWN_HOOK = 0x0104;

        // --- Licensing (Gumroad) ---
        // TODO before shipping: replace both constants below.
        //  - GUMROAD_PRODUCT_PERMALINK: the part of your product's URL after gumroad.com/l/
        //    (used only to build the "Buy License" browser link).
        //  - GUMROAD_PRODUCT_ID: the product's unique ID, shown in the License key block on the
        //    product's Content page while editing (Gumroad dashboard -> your product -> Content
        //    tab -> the "License key" block). This is REQUIRED by Gumroad's verify API for any
        //    product created after Jan 9, 2023 - product_permalink alone is no longer accepted.
        const string GUMROAD_PRODUCT_PERMALINK = "elmyts";
        const string GUMROAD_PRODUCT_ID = "IUP5J8WWTTU9l08vcLRz7w==";
        static string GumroadBuyUrl { get { return "https://gumroad.com/l/" + GUMROAD_PRODUCT_PERMALINK; } }
        const int TRIAL_CLICK_LIMIT = 100;

        // --- Updates (GitHub Releases) ---
        const string APP_VERSION = "1.0.2";
        const string GITHUB_REPO = "HKisielinski/autoclicker-pro";
        Button btnCheckUpdate;

        bool isLicensed = false;
        string licenseKey = "";
        int trialClicksUsed = 0;
        DateTime lastVerifiedUtc = DateTime.MinValue;

        GroupBox grpLicense;
        Label lblLicenseStatus, lblPricing;
        Button btnBuyLicense, btnEnterKey;

        // --- UI controls ---
        NumericUpDown numHours, numMinutes, numSeconds, numMillis;
        CheckBox chkRandomize;
        NumericUpDown numRandomMs;
        RadioButton radCurrentPos, radFixedPos, radSequence;
        NumericUpDown numX, numY;
        Button btnPickPos;
        ListBox lstSequence;
        Button btnAddSeq, btnRemoveSeq, btnSeqUp, btnSeqDown, btnClearSeq, btnRecord;
        ComboBox cmbSavedSeq;
        Button btnSaveSeq, btnLoadSeq, btnDeleteSaved;
        ComboBox cmbButton;
        CheckBox chkDouble;
        RadioButton radInfinite, radCount;
        NumericUpDown numCount;
        Button btnStartStop;
        Label lblStatus, lblCount, lblHotkeyInfo;
        Timer clickTimer;
        Timer pickCountdownTimer;

        List<SequenceStep> sequence = new List<SequenceStep>();
        int seqPlayIndex = 0;
        long baseIntervalMs;
        readonly Random rng = new Random();

        Dictionary<string, List<SequenceStep>> savedSequences = new Dictionary<string, List<SequenceStep>>();
        readonly string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoClickerPro");
        string SaveFile { get { return Path.Combine(saveDir, "sequences.txt"); } }
        string LicenseFile { get { return Path.Combine(saveDir, "license.txt"); } }

        bool isRunning = false;
        long clicksDone = 0;
        int pickCountdown = 0;
        int pickMode = 0; // 0 = set fixed position, 1 = add to sequence
        Button activePickButton;

        NotifyIcon trayIcon;
        ToolStripMenuItem menuToggle;
        bool allowExit = false;
        bool trayHintShown = false;

        bool isRecording = false;
        IntPtr recordHookId = IntPtr.Zero;
        LowLevelMouseProc recordHookProc;
        IntPtr recordKeyHookId = IntPtr.Zero;
        LowLevelKeyboardProc recordKeyHookProc;

        public MainForm()
        {
            Text = "AutoClicker Pro";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(340, 1030);
            Font = new Font("Segoe UI", 9F);

            try
            {
                Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null) Icon = appIcon;
            }
            catch
            {
                // fall back to the default .NET form icon if extraction fails for any reason
            }

            BuildUi();
            LoadAllFromFile();
            RefreshSavedCombo();
            LoadLicenseState();
            UpdateLicenseUi();
            SetupTray();

            clickTimer = new Timer();
            clickTimer.Tick += ClickTimer_Tick;

            pickCountdownTimer = new Timer();
            pickCountdownTimer.Interval = 1000;
            pickCountdownTimer.Tick += PickCountdownTimer_Tick;

            Load += (s, e) =>
            {
                RegisterHotKey(Handle, HOTKEY_ID, 0, VK_F6);
                RegisterHotKey(Handle, HOTKEY_ID_RECORD, 0, VK_F7);
            };

            Shown += (s, e) => RefreshSubscriptionStatusAsync();

            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Hide();
                    if (!trayHintShown)
                    {
                        trayIcon.ShowBalloonTip(2000, "AutoClicker Pro",
                            "The app keeps running in the background. Click the tray icon to bring it back.", ToolTipIcon.Info);
                        trayHintShown = true;
                    }
                }
            };

            FormClosing += (s, e) =>
            {
                if (!allowExit)
                {
                    e.Cancel = true;
                    WindowState = FormWindowState.Minimized;
                }
                else
                {
                    if (isRecording) StopRecording();
                    SaveLicenseState();
                    UnregisterHotKey(Handle, HOTKEY_ID);
                    UnregisterHotKey(Handle, HOTKEY_ID_RECORD);
                    trayIcon.Visible = false;
                }
            };
        }

        void SetupTray()
        {
            var menuShow = new ToolStripMenuItem("Show window", null, (s, e) => ShowFromTray());
            menuToggle = new ToolStripMenuItem("Start (F6)", null, (s, e) => ToggleRunning());
            var menuExit = new ToolStripMenuItem("Exit", null, (s, e) => { allowExit = true; Close(); });

            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add(menuShow);
            trayMenu.Items.Add(menuToggle);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(menuExit);

            trayIcon = new NotifyIcon();
            trayIcon.Icon = this.Icon ?? SystemIcons.Application;
            trayIcon.Text = "AutoClicker Pro - stopped";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += (s, e) => ShowFromTray();
        }

        void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        void BuildUi()
        {
            int y = 10;

            grpLicense = new GroupBox { Text = "License", Left = 10, Top = y, Width = 310, Height = 105 };
            lblLicenseStatus = new Label { Left = 10, Top = 20, Width = 290, Height = 18, Text = "Trial mode", ForeColor = Color.DimGray };
            lblPricing = new Label { Left = 10, Top = 38, Width = 290, Height = 16, Text = "Plans: $5 / month or $10 / year", ForeColor = Color.Gray, Font = new Font("Segoe UI", 8F) };
            btnBuyLicense = new Button { Text = "Buy License", Left = 10, Top = 68, Width = 140 };
            btnEnterKey = new Button { Text = "Enter License Key", Left = 160, Top = 68, Width = 140 };
            btnBuyLicense.Click += BtnBuyLicense_Click;
            btnEnterKey.Click += BtnEnterKey_Click;
            grpLicense.Controls.Add(lblLicenseStatus);
            grpLicense.Controls.Add(lblPricing);
            grpLicense.Controls.Add(btnBuyLicense);
            grpLicense.Controls.Add(btnEnterKey);
            Controls.Add(grpLicense);
            y += 115;

            var grpInterval = new GroupBox { Text = "Click Interval", Left = 10, Top = y, Width = 310, Height = 100 };
            numHours = new NumericUpDown { Left = 10, Top = 25, Width = 55, Maximum = 23 };
            numMinutes = new NumericUpDown { Left = 80, Top = 25, Width = 55, Maximum = 59 };
            numSeconds = new NumericUpDown { Left = 150, Top = 25, Width = 55, Maximum = 59, Value = 1 };
            numMillis = new NumericUpDown { Left = 220, Top = 25, Width = 65, Maximum = 999 };
            grpInterval.Controls.Add(new Label { Text = "hrs", Left = 10, Top = 45, Width = 55, TextAlign = ContentAlignment.MiddleCenter });
            grpInterval.Controls.Add(new Label { Text = "min", Left = 80, Top = 45, Width = 55, TextAlign = ContentAlignment.MiddleCenter });
            grpInterval.Controls.Add(new Label { Text = "sec", Left = 150, Top = 45, Width = 55, TextAlign = ContentAlignment.MiddleCenter });
            grpInterval.Controls.Add(new Label { Text = "ms", Left = 220, Top = 45, Width = 65, TextAlign = ContentAlignment.MiddleCenter });
            grpInterval.Controls.Add(numHours);
            grpInterval.Controls.Add(numMinutes);
            grpInterval.Controls.Add(numSeconds);
            grpInterval.Controls.Add(numMillis);
            chkRandomize = new CheckBox { Text = "Randomize interval, ±", Left = 10, Top = 72, Width = 160 };
            numRandomMs = new NumericUpDown { Left = 172, Top = 70, Width = 70, Maximum = 60000, Enabled = false };
            chkRandomize.CheckedChanged += (s, e) => { numRandomMs.Enabled = chkRandomize.Checked; };
            grpInterval.Controls.Add(chkRandomize);
            grpInterval.Controls.Add(numRandomMs);
            grpInterval.Controls.Add(new Label { Text = "ms", Left = 248, Top = 72, Width = 40, TextAlign = ContentAlignment.MiddleLeft });
            Controls.Add(grpInterval);
            y += 110;

            var grpPos = new GroupBox { Text = "Click Position", Left = 10, Top = y, Width = 310, Height = 140 };
            radCurrentPos = new RadioButton { Text = "Current cursor position", Left = 10, Top = 20, Width = 250, Checked = true };
            radFixedPos = new RadioButton { Text = "Fixed position:", Left = 10, Top = 45, Width = 110 };
            numX = new NumericUpDown { Left = 120, Top = 44, Width = 60, Maximum = 10000, Enabled = false };
            numY = new NumericUpDown { Left = 190, Top = 44, Width = 60, Maximum = 10000, Enabled = false };
            btnPickPos = new Button { Text = "Pick position (3s)", Left = 10, Top = 75, Width = 200, Enabled = false, Tag = "Pick position (3s)" };
            radSequence = new RadioButton { Text = "Position sequence (list below)", Left = 10, Top = 108, Width = 260 };
            btnPickPos.Click += (s, e) => StartPick(0, btnPickPos);
            grpPos.Controls.Add(radCurrentPos);
            grpPos.Controls.Add(radFixedPos);
            grpPos.Controls.Add(numX);
            grpPos.Controls.Add(numY);
            grpPos.Controls.Add(btnPickPos);
            grpPos.Controls.Add(radSequence);
            Controls.Add(grpPos);
            y += 150;

            var grpSeq = new GroupBox { Text = "Steps: clicks + key presses (in this order, looped)", Left = 10, Top = y, Width = 310, Height = 195 };
            lstSequence = new ListBox { Left = 10, Top = 20, Width = 290, Height = 80 };
            btnAddSeq = new Button { Text = "Add position (3s)", Left = 10, Top = 105, Width = 140, Tag = "Add position (3s)" };
            btnRemoveSeq = new Button { Text = "Remove selected", Left = 160, Top = 105, Width = 140 };
            btnSeqUp = new Button { Text = "▲ Move up", Left = 10, Top = 132, Width = 90 };
            btnSeqDown = new Button { Text = "▼ Move down", Left = 110, Top = 132, Width = 90 };
            btnClearSeq = new Button { Text = "Clear", Left = 210, Top = 132, Width = 90 };
            btnRecord = new Button { Text = "🔴 Record clicks + keys (F7)", Left = 10, Top = 161, Width = 290, Height = 26, Tag = "🔴 Record clicks + keys (F7)" };
            btnAddSeq.Click += (s, e) => StartPick(1, btnAddSeq);
            btnRemoveSeq.Click += BtnRemoveSeq_Click;
            btnSeqUp.Click += (s, e) => MoveSelected(-1);
            btnSeqDown.Click += (s, e) => MoveSelected(1);
            btnClearSeq.Click += (s, e) => { sequence.Clear(); RefreshSequenceList(); };
            btnRecord.Click += (s, e) => ToggleRecording();
            grpSeq.Controls.Add(lstSequence);
            grpSeq.Controls.Add(btnAddSeq);
            grpSeq.Controls.Add(btnRemoveSeq);
            grpSeq.Controls.Add(btnSeqUp);
            grpSeq.Controls.Add(btnSeqDown);
            grpSeq.Controls.Add(btnClearSeq);
            grpSeq.Controls.Add(btnRecord);
            Controls.Add(grpSeq);
            y += 205;

            var grpSaved = new GroupBox { Text = "Saved Sequences", Left = 10, Top = y, Width = 310, Height = 90 };
            cmbSavedSeq = new ComboBox { Left = 10, Top = 20, Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
            btnSaveSeq = new Button { Text = "Save as...", Left = 10, Top = 50, Width = 95 };
            btnLoadSeq = new Button { Text = "Load", Left = 110, Top = 50, Width = 90 };
            btnDeleteSaved = new Button { Text = "Delete", Left = 205, Top = 50, Width = 95 };
            btnSaveSeq.Click += BtnSaveSeq_Click;
            btnLoadSeq.Click += BtnLoadSeq_Click;
            btnDeleteSaved.Click += BtnDeleteSaved_Click;
            grpSaved.Controls.Add(cmbSavedSeq);
            grpSaved.Controls.Add(btnSaveSeq);
            grpSaved.Controls.Add(btnLoadSeq);
            grpSaved.Controls.Add(btnDeleteSaved);
            Controls.Add(grpSaved);

            EventHandler posModeChanged = (s, e) =>
            {
                numX.Enabled = numY.Enabled = btnPickPos.Enabled = radFixedPos.Checked;
                bool seqMode = radSequence.Checked;
                foreach (Control c in grpSeq.Controls) c.Enabled = seqMode;
                foreach (Control c in grpSaved.Controls) c.Enabled = seqMode;
                btnRecord.Enabled = true; // recording switches the mode itself, so keep it always clickable
            };
            radCurrentPos.CheckedChanged += posModeChanged;
            radFixedPos.CheckedChanged += posModeChanged;
            radSequence.CheckedChanged += posModeChanged;
            posModeChanged(null, EventArgs.Empty);
            y += 100;

            var grpClick = new GroupBox { Text = "Click Type", Left = 10, Top = y, Width = 310, Height = 65 };
            cmbButton = new ComboBox { Left = 10, Top = 25, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbButton.Items.AddRange(new object[] { "Left button", "Right button", "Middle button" });
            cmbButton.SelectedIndex = 0;
            chkDouble = new CheckBox { Text = "Double click", Left = 150, Top = 27, Width = 150 };
            grpClick.Controls.Add(cmbButton);
            grpClick.Controls.Add(chkDouble);
            Controls.Add(grpClick);
            y += 75;

            var grpCount = new GroupBox { Text = "Click Count", Left = 10, Top = y, Width = 310, Height = 65 };
            radInfinite = new RadioButton { Text = "Unlimited", Left = 10, Top = 22, Width = 110, Checked = true };
            radCount = new RadioButton { Text = "Limit to:", Left = 10, Top = 40, Width = 100 };
            numCount = new NumericUpDown { Left = 115, Top = 39, Width = 80, Maximum = 1000000, Minimum = 1, Value = 10, Enabled = false };
            radInfinite.CheckedChanged += (s, e) => { numCount.Enabled = radCount.Checked; };
            radCount.CheckedChanged += (s, e) => { numCount.Enabled = radCount.Checked; };
            grpCount.Controls.Add(radInfinite);
            grpCount.Controls.Add(radCount);
            grpCount.Controls.Add(numCount);
            Controls.Add(grpCount);
            y += 75;

            btnStartStop = new Button { Text = "Start (F6)", Left = 10, Top = y, Width = 310, Height = 35, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
            btnStartStop.Click += (s, e) => ToggleRunning();
            Controls.Add(btnStartStop);
            y += 45;

            lblStatus = new Label { Text = "Status: stopped", Left = 10, Top = y, Width = 310, TextAlign = ContentAlignment.MiddleCenter };
            Controls.Add(lblStatus);
            y += 20;

            lblCount = new Label { Text = "Actions performed: 0", Left = 10, Top = y, Width = 310, TextAlign = ContentAlignment.MiddleCenter };
            Controls.Add(lblCount);
            y += 20;

            lblHotkeyInfo = new Label { Text = "F6 = start/stop clicking, F7 = start/stop recording clicks + key presses (global). Closing the window (X) minimizes it to the system tray — right-click the tray icon to exit for good.", Left = 10, Top = y, Width = 310, Height = 55, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleCenter };
            Controls.Add(lblHotkeyInfo);

            btnCheckUpdate = new Button { Text = "Check for Updates (v" + APP_VERSION + ")", Left = 10, Top = 990, Width = 310, Height = 28 };
            btnCheckUpdate.Click += BtnCheckUpdate_Click;
            Controls.Add(btnCheckUpdate);
        }

        // --- Sequence: list editing ---

        void RefreshSequenceList()
        {
            lstSequence.Items.Clear();
            for (int i = 0; i < sequence.Count; i++)
            {
                var step = sequence[i];
                string desc = step.IsKey
                    ? string.Format("{0}. Key: {1}", i + 1, step.Key)
                    : string.Format("{0}. Click ({1}, {2})", i + 1, step.Position.X, step.Position.Y);
                lstSequence.Items.Add(desc);
            }
        }

        void BtnRemoveSeq_Click(object sender, EventArgs e)
        {
            int idx = lstSequence.SelectedIndex;
            if (idx < 0) return;
            sequence.RemoveAt(idx);
            RefreshSequenceList();
            if (idx < lstSequence.Items.Count) lstSequence.SelectedIndex = idx;
        }

        void MoveSelected(int dir)
        {
            int idx = lstSequence.SelectedIndex;
            int newIdx = idx + dir;
            if (idx < 0 || newIdx < 0 || newIdx >= sequence.Count) return;
            SequenceStep tmp = sequence[idx];
            sequence[idx] = sequence[newIdx];
            sequence[newIdx] = tmp;
            RefreshSequenceList();
            lstSequence.SelectedIndex = newIdx;
        }

        void StartPick(int mode, Button btn)
        {
            pickMode = mode;
            activePickButton = btn;
            pickCountdown = 3;
            btnPickPos.Enabled = false;
            btnAddSeq.Enabled = false;
            btn.Text = "Set cursor... " + pickCountdown;
            pickCountdownTimer.Start();
        }

        void PickCountdownTimer_Tick(object sender, EventArgs e)
        {
            pickCountdown--;
            if (pickCountdown <= 0)
            {
                pickCountdownTimer.Stop();
                var p = Cursor.Position;
                if (pickMode == 0)
                {
                    numX.Value = Math.Max(numX.Minimum, Math.Min(numX.Maximum, p.X));
                    numY.Value = Math.Max(numY.Minimum, Math.Min(numY.Maximum, p.Y));
                }
                else
                {
                    sequence.Add(SequenceStep.ForClick(p));
                    RefreshSequenceList();
                }
                activePickButton.Text = (string)activePickButton.Tag;
                btnPickPos.Enabled = radFixedPos.Checked;
                btnAddSeq.Enabled = radSequence.Checked;
            }
            else
            {
                activePickButton.Text = "Set cursor... " + pickCountdown;
            }
        }

        // --- Sequence: recording clicks live ---

        void ToggleRecording()
        {
            if (isRunning)
            {
                MessageBox.Show(this, "Stop playback first before you start recording.",
                    "Playback Active", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (isRecording) StopRecording();
            else StartRecording();
        }

        void StartRecording()
        {
            radSequence.Checked = true;
            isRecording = true;
            btnRecord.Text = "⏹ Stop recording (F7) — 0";
            lblStatus.Text = "Status: RECORDING — click, or press keys, in your target app";
            lblStatus.ForeColor = Color.Red;

            recordHookProc = RecordHookCallback;
            recordHookId = SetWindowsHookEx(WH_MOUSE_LL, recordHookProc, GetModuleHandle(null), 0);

            recordKeyHookProc = RecordKeyHookCallback;
            recordKeyHookId = SetWindowsHookEx(WH_KEYBOARD_LL, recordKeyHookProc, GetModuleHandle(null), 0);
        }

        void StopRecording()
        {
            isRecording = false;
            if (recordHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(recordHookId);
                recordHookId = IntPtr.Zero;
            }
            if (recordKeyHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(recordKeyHookId);
                recordKeyHookId = IntPtr.Zero;
            }
            btnRecord.Text = (string)btnRecord.Tag;
            lblStatus.Text = "Status: stopped";
            lblStatus.ForeColor = Color.Black;
        }

        IntPtr RecordHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN_HOOK)
            {
                MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                Point pt = new Point(hookStruct.pt.x, hookStruct.pt.y);

                if (!Bounds.Contains(pt))
                {
                    sequence.Add(SequenceStep.ForClick(pt));
                    RefreshSequenceList();
                    lstSequence.TopIndex = Math.Max(0, lstSequence.Items.Count - 1);
                    btnRecord.Text = "⏹ Stop recording (F7) — " + sequence.Count;
                }
            }
            return CallNextHookEx(recordHookId, nCode, wParam, lParam);
        }

        // Records key presses typed into the target app while recording, so a sequence can
        // interleave clicks and keys (e.g. click, type "r", click, type "a"). Keys pressed while
        // this app itself is focused (including F6/F7) are ignored - only presses sent to
        // whatever window the user is actually recording into count as steps.
        IntPtr RecordKeyHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam.ToInt32() == WM_KEYDOWN_HOOK || wParam.ToInt32() == WM_SYSKEYDOWN_HOOK))
            {
                KBDLLHOOKSTRUCT hookStruct = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                uint vk = hookStruct.vkCode;

                if (vk != VK_F6 && vk != VK_F7 && GetForegroundWindow() != Handle)
                {
                    sequence.Add(SequenceStep.ForKey((Keys)vk));
                    RefreshSequenceList();
                    lstSequence.TopIndex = Math.Max(0, lstSequence.Items.Count - 1);
                    btnRecord.Text = "⏹ Stop recording (F7) — " + sequence.Count;
                }
            }
            return CallNextHookEx(recordKeyHookId, nCode, wParam, lParam);
        }

        // --- Sequence: save / load named presets ---

        void RefreshSavedCombo()
        {
            string current = cmbSavedSeq.SelectedItem as string;
            cmbSavedSeq.Items.Clear();
            var names = new List<string>(savedSequences.Keys);
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (var n in names) cmbSavedSeq.Items.Add(n);
            if (current != null && cmbSavedSeq.Items.Contains(current))
                cmbSavedSeq.SelectedItem = current;
        }

        void BtnSaveSeq_Click(object sender, EventArgs e)
        {
            if (sequence.Count == 0)
            {
                MessageBox.Show(this, "The position list is empty — add at least one position before saving.",
                    "No Positions", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string suggested = cmbSavedSeq.SelectedItem as string ?? "";
            string name = Interaction.InputBox("Enter a name for this sequence:", "Save Sequence", suggested);
            name = (name ?? "").Trim();
            if (name.Length == 0) return;

            savedSequences[name] = new List<SequenceStep>(sequence);
            SaveAllToFile();
            RefreshSavedCombo();
            cmbSavedSeq.SelectedItem = name;
        }

        void BtnLoadSeq_Click(object sender, EventArgs e)
        {
            string name = cmbSavedSeq.SelectedItem as string;
            if (name == null || !savedSequences.ContainsKey(name)) return;
            sequence = new List<SequenceStep>(savedSequences[name]);
            RefreshSequenceList();
        }

        void BtnDeleteSaved_Click(object sender, EventArgs e)
        {
            string name = cmbSavedSeq.SelectedItem as string;
            if (name == null || !savedSequences.ContainsKey(name)) return;
            var confirm = MessageBox.Show(this, "Delete saved sequence \"" + name + "\"?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
            savedSequences.Remove(name);
            SaveAllToFile();
            RefreshSavedCombo();
        }

        void LoadAllFromFile()
        {
            savedSequences.Clear();
            if (!File.Exists(SaveFile)) return;
            try
            {
                string currentName = null;
                List<SequenceStep> currentList = null;
                foreach (var rawLine in File.ReadAllLines(SaveFile))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("#"))
                    {
                        currentName = line.Substring(1);
                        currentList = new List<SequenceStep>();
                        savedSequences[currentName] = currentList;
                    }
                    else if (currentList != null)
                    {
                        string[] parts = line.Split(',');
                        int px, py, vk;
                        if (parts.Length == 3 && parts[0] == "C" && int.TryParse(parts[1], out px) && int.TryParse(parts[2], out py))
                            currentList.Add(SequenceStep.ForClick(new Point(px, py)));
                        else if (parts.Length == 2 && parts[0] == "K" && int.TryParse(parts[1], out vk))
                            currentList.Add(SequenceStep.ForKey((Keys)vk));
                        else if (parts.Length == 2 && int.TryParse(parts[0], out px) && int.TryParse(parts[1], out py))
                            // legacy format (pre-1.0.2 save files): plain "x,y" with no prefix
                            currentList.Add(SequenceStep.ForClick(new Point(px, py)));
                    }
                }
            }
            catch
            {
                // corrupted save file — ignore, start with an empty saved-sequence list
            }
        }

        void SaveAllToFile()
        {
            try
            {
                Directory.CreateDirectory(saveDir);
                using (var w = new StreamWriter(SaveFile, false))
                {
                    foreach (var kv in savedSequences)
                    {
                        w.WriteLine("#" + kv.Key);
                        foreach (var step in kv.Value)
                        {
                            if (step.IsKey) w.WriteLine("K," + (int)step.Key);
                            else w.WriteLine("C," + step.Position.X + "," + step.Position.Y);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save sequences: " + ex.Message, "Save Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // --- Licensing ---

        void LoadLicenseState()
        {
            isLicensed = false;
            licenseKey = "";
            trialClicksUsed = 0;
            if (!File.Exists(LicenseFile)) return;
            try
            {
                foreach (var rawLine in File.ReadAllLines(LicenseFile))
                {
                    string line = rawLine.Trim();
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq);
                    string v = line.Substring(eq + 1);
                    if (k == "licensed") isLicensed = (v == "1");
                    else if (k == "key") licenseKey = v;
                    else if (k == "trialClicks") int.TryParse(v, out trialClicksUsed);
                    else if (k == "lastVerified")
                    {
                        long ticks;
                        if (long.TryParse(v, out ticks)) lastVerifiedUtc = new DateTime(ticks, DateTimeKind.Utc);
                    }
                }
            }
            catch
            {
                // corrupted license file — fall back to trial mode
            }
        }

        void SaveLicenseState()
        {
            try
            {
                Directory.CreateDirectory(saveDir);
                using (var w = new StreamWriter(LicenseFile, false))
                {
                    w.WriteLine("licensed=" + (isLicensed ? "1" : "0"));
                    w.WriteLine("key=" + licenseKey);
                    w.WriteLine("trialClicks=" + trialClicksUsed);
                    w.WriteLine("lastVerified=" + lastVerifiedUtc.Ticks);
                }
            }
            catch
            {
                // best-effort; if this fails the trial counter simply won't persist across runs
            }
        }

        void UpdateLicenseUi()
        {
            if (isLicensed)
            {
                string masked = licenseKey.Length > 4 ? "•••• " + licenseKey.Substring(licenseKey.Length - 4) : licenseKey;
                lblLicenseStatus.Text = "Subscription active — thank you! (key ending " + masked + ")";
                lblLicenseStatus.ForeColor = Color.Green;
                btnEnterKey.Text = "Change License Key";
                lblPricing.Visible = false;
            }
            else
            {
                lblLicenseStatus.Text = string.Format("Trial mode — {0}/{1} clicks used", trialClicksUsed, TRIAL_CLICK_LIMIT);
                lblLicenseStatus.ForeColor = trialClicksUsed >= TRIAL_CLICK_LIMIT ? Color.Red : Color.DimGray;
                btnEnterKey.Text = "Enter License Key";
                lblPricing.Visible = true;
            }
        }

        void BtnBuyLicense_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start(GumroadBuyUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the purchase page: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void BtnEnterKey_Click(object sender, EventArgs e)
        {
            string key = Interaction.InputBox("Enter your license key (from your purchase email):", "Activate License", licenseKey);
            key = (key ?? "").Trim();
            if (key.Length == 0) return;

            Cursor = Cursors.WaitCursor;
            btnEnterKey.Enabled = false;
            btnBuyLicense.Enabled = false;
            string error;
            bool subscriptionEnded;
            bool ok = VerifyGumroadLicense(key, out error, out subscriptionEnded);
            Cursor = Cursors.Default;
            btnEnterKey.Enabled = true;
            btnBuyLicense.Enabled = true;

            if (ok && subscriptionEnded)
            {
                MessageBox.Show(this, "This license key is valid, but its subscription is no longer active (cancelled, expired, or a payment failed). Please renew your subscription and try again.",
                    "Subscription Not Active", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (ok)
            {
                isLicensed = true;
                licenseKey = key;
                lastVerifiedUtc = DateTime.UtcNow;
                SaveLicenseState();
                UpdateLicenseUi();
                MessageBox.Show(this, "License activated successfully. Thank you for your subscription!",
                    "License Activated", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, error, "License Activation Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Checks a license key against Gumroad and reports whether its subscription (if any) has
        // ended — Gumroad keeps the key valid forever but sets these timestamps once a recurring
        // subscription is cancelled, fails to renew, or otherwise ends.
        bool VerifyGumroadLicense(string key, out string errorMessage, out bool subscriptionEnded)
        {
            errorMessage = null;
            subscriptionEnded = false;
            string json;

            try
            {
                // .NET Framework 4 doesn't negotiate TLS 1.2 by default, which modern APIs
                // (including Gumroad's) require - without this, every call fails with a
                // SecureChannelFailure before it even reaches the server.
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var client = new WebClient())
                {
                    var data = new NameValueCollection();
                    data["product_id"] = GUMROAD_PRODUCT_ID;
                    data["license_key"] = key;
                    data["increment_uses_count"] = "false";
                    byte[] responseBytes = client.UploadValues("https://api.gumroad.com/v2/licenses/verify", data);
                    json = System.Text.Encoding.UTF8.GetString(responseBytes);
                }
            }
            catch (WebException wex)
            {
                // Gumroad answers an invalid/unknown license key with a non-200 status that still
                // carries a normal {"success":false,"message":"..."} JSON body. WebClient turns any
                // non-200 status into a WebException, so read that body before assuming this is a
                // real connectivity problem - otherwise a simple typo shows up as "check your
                // internet connection", which is misleading and wrong.
                if (wex.Response == null)
                {
                    errorMessage = "Could not reach the license server. Check your internet connection and try again.";
                    return false;
                }
                using (var stream = wex.Response.GetResponseStream())
                using (var reader = new System.IO.StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                errorMessage = "Unexpected error while verifying the license: " + ex.Message;
                return false;
            }

            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                var result = serializer.Deserialize<Dictionary<string, object>>(json);

                if (!(result.ContainsKey("success") && result["success"] is bool && (bool)result["success"]))
                {
                    errorMessage = result.ContainsKey("message") ? Convert.ToString(result["message"]) : "This license key is not valid.";
                    return false;
                }

                object purchaseObj;
                if (result.TryGetValue("purchase", out purchaseObj) && purchaseObj is Dictionary<string, object>)
                {
                    var purchase = (Dictionary<string, object>)purchaseObj;
                    subscriptionEnded = HasValue(purchase, "subscription_cancelled_at")
                        || HasValue(purchase, "subscription_failed_at")
                        || HasValue(purchase, "subscription_ended_at");
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = "Unexpected error while verifying the license: " + ex.Message;
                return false;
            }
        }

        static bool HasValue(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null;
        }

        // Re-checks subscription status at most once every 24h, in the background, without
        // blocking the UI or requiring the app to be online to start up.
        void RefreshSubscriptionStatusAsync()
        {
            if (!isLicensed) return;
            if ((DateTime.UtcNow - lastVerifiedUtc).TotalHours < 24) return;

            string keyCopy = licenseKey;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                bool subEnded;
                bool ok = VerifyGumroadLicense(keyCopy, out error, out subEnded);
                if (!ok) return; // offline or transient error — keep the cached license, retry later

                try
                {
                    if (IsHandleCreated && !IsDisposed)
                    {
                        Invoke((MethodInvoker)delegate
                        {
                            lastVerifiedUtc = DateTime.UtcNow;
                            if (subEnded) isLicensed = false;
                            SaveLicenseState();
                            UpdateLicenseUi();
                        });
                    }
                }
                catch
                {
                    // form closed mid-check — nothing to update
                }
            });
        }

        // --- Updates ---

        void BtnCheckUpdate_Click(object sender, EventArgs e)
        {
            Cursor = Cursors.WaitCursor;
            btnCheckUpdate.Enabled = false;
            string latestVersion, releaseUrl, error;
            bool ok = GetLatestVersionFromGitHub(out latestVersion, out releaseUrl, out error);
            Cursor = Cursors.Default;
            btnCheckUpdate.Enabled = true;

            if (!ok)
            {
                MessageBox.Show(this, error, "Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsNewerVersion(latestVersion, APP_VERSION))
            {
                var result = MessageBox.Show(this,
                    "A new version is available: v" + latestVersion + " (you have v" + APP_VERSION + ").\n\nOpen the download page now?",
                    "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (result == DialogResult.Yes)
                {
                    try { Process.Start(releaseUrl); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Could not open the download page: " + ex.Message, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show(this, "You're up to date (v" + APP_VERSION + ").", "No Updates Available",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        bool GetLatestVersionFromGitHub(out string version, out string releaseUrl, out string errorMessage)
        {
            version = null;
            releaseUrl = null;
            errorMessage = null;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var client = new WebClient())
                {
                    // GitHub's API rejects requests with no User-Agent header.
                    client.Headers.Add("User-Agent", "AutoClickerPro-UpdateCheck");
                    string json = client.DownloadString("https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest");
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var result = serializer.Deserialize<Dictionary<string, object>>(json);

                    object tagObj;
                    if (!result.TryGetValue("tag_name", out tagObj) || tagObj == null)
                    {
                        errorMessage = "Unexpected response from the update server.";
                        return false;
                    }
                    version = Convert.ToString(tagObj).TrimStart('v', 'V');

                    object urlObj;
                    releaseUrl = result.TryGetValue("html_url", out urlObj) && urlObj != null
                        ? Convert.ToString(urlObj)
                        : "https://github.com/" + GITHUB_REPO + "/releases/latest";
                    return true;
                }
            }
            catch (WebException)
            {
                errorMessage = "Could not check for updates. Check your internet connection and try again.";
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = "Unexpected error while checking for updates: " + ex.Message;
                return false;
            }
        }

        static bool IsNewerVersion(string latest, string current)
        {
            try
            {
                return new Version(latest) > new Version(current);
            }
            catch
            {
                return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
            }
        }

        // --- Start / Stop / clicking ---

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_ID) ToggleRunning();
                else if (id == HOTKEY_ID_RECORD) ToggleRecording();
            }
            base.WndProc(ref m);
        }

        void ToggleRunning()
        {
            if (isRunning) StopClicking();
            else StartClicking();
        }

        void StartClicking()
        {
            if (isRecording)
            {
                MessageBox.Show(this, "Stop recording first (F7) before starting playback.",
                    "Recording Active", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!isLicensed && trialClicksUsed >= TRIAL_CLICK_LIMIT)
            {
                OfferPurchase();
                return;
            }
            if (radSequence.Checked && sequence.Count == 0)
            {
                MessageBox.Show(this, "Add at least one position to the sequence.", "No Positions",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            long totalMs = (long)numHours.Value * 3600000L + (long)numMinutes.Value * 60000L + (long)numSeconds.Value * 1000L + (long)numMillis.Value;
            if (totalMs < 10) totalMs = 10;
            if (totalMs > int.MaxValue) totalMs = int.MaxValue;
            baseIntervalMs = totalMs;

            clicksDone = 0;
            seqPlayIndex = 0;
            clickTimer.Interval = ComputeIntervalMs();
            isRunning = true;
            btnStartStop.Text = "Stop (F6)";
            menuToggle.Text = "Stop (F6)";
            trayIcon.Text = "AutoClicker Pro - RUNNING";
            lblStatus.Text = "Status: RUNNING";
            lblStatus.ForeColor = Color.Green;
            clickTimer.Start();
        }

        void StopClicking()
        {
            clickTimer.Stop();
            isRunning = false;
            btnStartStop.Text = "Start (F6)";
            menuToggle.Text = "Start (F6)";
            trayIcon.Text = "AutoClicker Pro - stopped";
            lblStatus.Text = "Status: stopped";
            lblStatus.ForeColor = Color.Black;
            if (!isLicensed) SaveLicenseState();
        }

        void OfferPurchase()
        {
            var result = MessageBox.Show(this,
                "You've reached the trial limit of " + TRIAL_CLICK_LIMIT + " clicks.\n\nPurchase a license to unlock unlimited use. Open the purchase page now?",
                "Trial Limit Reached", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (result == DialogResult.Yes) BtnBuyLicense_Click(null, EventArgs.Empty);
        }

        void ClickTimer_Tick(object sender, EventArgs e)
        {
            if (!isLicensed && trialClicksUsed >= TRIAL_CLICK_LIMIT)
            {
                StopClicking();
                OfferPurchase();
                return;
            }

            string stepInfo = "";

            if (radFixedPos.Checked)
            {
                Cursor.Position = new Point((int)numX.Value, (int)numY.Value);
                DoClick();
                if (chkDouble.Checked) DoClick();
            }
            else if (radSequence.Checked)
            {
                if (sequence.Count == 0) { StopClicking(); return; }
                if (seqPlayIndex >= sequence.Count) seqPlayIndex = 0;
                SequenceStep step = sequence[seqPlayIndex];
                stepInfo = string.Format(" (step {0}/{1})", seqPlayIndex + 1, sequence.Count);
                seqPlayIndex = (seqPlayIndex + 1) % sequence.Count;

                if (step.IsKey)
                {
                    DoKeyPress(step.Key);
                }
                else
                {
                    Cursor.Position = step.Position;
                    DoClick();
                    if (chkDouble.Checked) DoClick();
                }
            }
            else
            {
                DoClick();
                if (chkDouble.Checked) DoClick();
            }

            clicksDone++;
            if (!isLicensed)
            {
                trialClicksUsed++;
                lblLicenseStatus.Text = string.Format("Trial mode — {0}/{1} clicks used", trialClicksUsed, TRIAL_CLICK_LIMIT);
            }
            lblCount.Text = "Actions performed: " + clicksDone;
            lblStatus.Text = "Status: RUNNING" + stepInfo;

            clickTimer.Interval = ComputeIntervalMs();

            if (radCount.Checked && clicksDone >= numCount.Value)
            {
                StopClicking();
            }
        }

        // Picks the next interval: the fixed base interval, or - when randomization is on - a
        // uniformly random value within ±numRandomMs of it (clamped so it never drops below 10ms).
        int ComputeIntervalMs()
        {
            if (!chkRandomize.Checked || numRandomMs.Value <= 0)
                return (int)Math.Min(baseIntervalMs, int.MaxValue);

            int range = (int)numRandomMs.Value;
            long lo = Math.Max(10, baseIntervalMs - range);
            long hi = Math.Min(int.MaxValue, baseIntervalMs + range);
            if (hi < lo) hi = lo;
            return rng.Next((int)lo, (int)hi + 1);
        }

        void DoClick()
        {
            uint down, up;
            switch (cmbButton.SelectedIndex)
            {
                case 1: down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; break;
                case 2: down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; break;
                default: down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; break;
            }
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }

        void DoKeyPress(Keys key)
        {
            byte vk = (byte)key;
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
