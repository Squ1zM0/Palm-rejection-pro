using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PalmRejectorPro
{
    public class OverlayForm : Form
    {
        private const int WM_POINTERUPDATE = 0x0245;
        private const int WM_POINTERDOWN = 0x0246;
        private const int WM_POINTERUP = 0x0247;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        private const int HTCLIENT = 1;

        private const uint PT_TOUCH = 0x00000002;

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private Settings settings = new Settings();
        private readonly object settingsLock = new object();

        private readonly string settingsPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PalmRejectorPro",
                "settings.json");

        private NotifyIcon? trayIcon;
        private ContextMenuStrip? trayMenu;
        private ScouterForm? scouterForm;

        private readonly List<TimestampedTouch> recentContacts = new List<TimestampedTouch>();
        private readonly TimeSpan contactHistoryWindow = TimeSpan.FromMilliseconds(300);

        private readonly Color kiBlue = Color.FromArgb(140, 0, 170, 255);
        private readonly Color kiOrange = Color.FromArgb(150, 255, 120, 0);
        private readonly Color kiGold = Color.FromArgb(220, 255, 215, 0);
        private readonly Color kiRed = Color.FromArgb(210, 255, 70, 40);

        private Brush? fingertipBrush;
        private Brush? palmBrush;
        private Pen? clusterPen;
        private Pen? validPen;
        private Pen? rejectedPen;

        private ClusterInfo lastLeftCluster = new ClusterInfo { BoundingBox = Rectangle.Empty };
        private ClusterInfo lastRightCluster = new ClusterInfo { BoundingBox = Rectangle.Empty };
        private Rectangle lastContactRect = Rectangle.Empty;
        private bool lastWasRejected = false;

        private string lastDetail = "READY";

        public OverlayForm()
        {
            LoadSettings();
            InitBrushes();

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            TransparencyKey = Color.Black;
            DoubleBuffered = true;
            KeyPreview = true;
            Opacity = settings.OverlayOpacity;

            CreateTray();

            KeyDown += OverlayForm_KeyDown;
            Shown += OverlayForm_Shown;
            Resize += OverlayForm_Resize;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        private void OverlayForm_Shown(object? sender, EventArgs e)
        {
            ApplyOverlayOpacity();
            RegisterForTouchInput();
            EnsureScouter();
            UpdateScouter();
        }

        private void OverlayForm_Resize(object? sender, EventArgs e)
        {
            RepositionScouter();
        }

        private void EnsureScouter()
        {
            if (scouterForm == null || scouterForm.IsDisposed)
            {
                scouterForm = new ScouterForm(this);
                scouterForm.Show();
            }

            RepositionScouter();
        }

        private void RepositionScouter()
        {
            if (scouterForm == null || scouterForm.IsDisposed)
                return;

            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle area = screen.WorkingArea;

            scouterForm.Location = new Point(
                area.Right - scouterForm.Width - settings.ScouterMarginRight,
                area.Bottom - scouterForm.Height - settings.ScouterMarginBottom);
        }

        private void InitBrushes()
        {
            fingertipBrush = new SolidBrush(kiBlue);
            palmBrush = new SolidBrush(kiOrange);
            clusterPen = new Pen(kiGold, 2f);
            validPen = new Pen(Color.Cyan, 3f);
            rejectedPen = new Pen(kiRed, 3f);
        }

        private void ApplyOverlayOpacity()
        {
            Opacity = Math.Max(0.05, Math.Min(1.0, settings.OverlayOpacity));
            Invalidate();
        }

        private void RegisterForTouchInput()
        {
            if (!IsHandleCreated)
                return;

            bool ok = RegisterPointerInputTarget(Handle, PT_TOUCH);
            if (!ok)
            {
                UpdateScouterStatus($"TOUCH REG FAIL ({Marshal.GetLastWin32Error()})");
            }
        }

        public void IncreaseThreshold()
        {
            settings.PalmThreshold += 5;
            SaveSettings();
            UpdateScouter();
        }

        public void DecreaseThreshold()
        {
            settings.PalmThreshold = Math.Max(5, settings.PalmThreshold - 5);
            SaveSettings();
            UpdateScouter();
        }

        public void IncreaseOpacity()
        {
            settings.OverlayOpacity = Math.Min(1.0, settings.OverlayOpacity + 0.05);
            ApplyOverlayOpacity();
            SaveSettings();
            UpdateScouter();
        }

        public void DecreaseOpacity()
        {
            settings.OverlayOpacity = Math.Max(0.10, settings.OverlayOpacity - 0.05);
            ApplyOverlayOpacity();
            SaveSettings();
            UpdateScouter();
        }

        public void ToggleEnabled()
        {
            settings.Enabled = !settings.Enabled;
            SaveSettings();
            UpdateScouter();
            Invalidate();
        }

        public void ExitApp()
        {
            if (trayIcon != null)
                trayIcon.Visible = false;

            Application.Exit();
        }

        public void UpdateScouterStatus(string detail)
        {
            lastDetail = detail;
            scouterForm?.UpdateDisplay(settings, lastDetail);
        }

        public void UpdateScouter(string detail = "")
        {
            if (!string.IsNullOrWhiteSpace(detail))
                lastDetail = detail;

            scouterForm?.UpdateDisplay(settings, lastDetail);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                if (IsOverScouterScreenRegion())
                {
                    m.Result = (IntPtr)HTCLIENT;
                    return;
                }

                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }

            if (settings.Enabled &&
                (m.Msg == WM_POINTERDOWN || m.Msg == WM_POINTERUPDATE || m.Msg == WM_POINTERUP))
            {
                uint pointerId = GET_POINTERID_WPARAM(m.WParam);

                if (GetPointerType(pointerId, out uint pointerType) && pointerType == PT_TOUCH)
                {
                    if (GetPointerTouchInfo(pointerId, out POINTER_TOUCH_INFO ti))
                    {
                        RECT rc = (ti.touchMask & TOUCH_MASK.CONTACTAREA) != 0
                            ? ti.rcContact
                            : PointToRect(ti.pointerInfo.ptPixelLocation, 5, 5);

                        Rectangle contact = RectFromRECT(rc);

                        AddRecentContact(contact, ti);

                        var (left, right) = ComputeLeftRightClusters();
                        bool fingertipIsLeft = left.Area <= right.Area;
                        ClusterInfo fingertipCluster = fingertipIsLeft ? left : right;
                        ClusterInfo palmCluster = fingertipIsLeft ? right : left;

                        bool inPalm = palmCluster.Contains(contact);
                        bool rejected = IsRejected(contact, inPalm, palmCluster, fingertipCluster, ti);

                        lastLeftCluster = left;
                        lastRightCluster = right;
                        lastContactRect = contact;
                        lastWasRejected = rejected;

                        string detail =
                            $"POS {contact.X},{contact.Y}\n" +
                            $"SIZE {contact.Width}x{contact.Height}\n" +
                            $"L {left.Area}  R {right.Area}\n" +
                            $"MODE {(rejected ? "PALM/ARM BLOCK" : "VALID TOUCH")}";

                        UpdateScouter(detail);
                        Invalidate();

                        if (rejected)
                        {
                            m.Result = IntPtr.Zero;
                            return;
                        }
                    }
                    else
                    {
                        UpdateScouterStatus($"TOUCH INFO FAIL ({Marshal.GetLastWin32Error()})");
                    }
                }
            }

            base.WndProc(ref m);
        }

        private bool IsOverScouterScreenRegion()
        {
            if (scouterForm == null || scouterForm.IsDisposed || !scouterForm.Visible)
                return false;

            Point cursor = Cursor.Position;
            return scouterForm.Bounds.Contains(cursor);
        }

        private bool IsRejected(
            Rectangle contact,
            bool inPalmCluster,
            ClusterInfo palmCluster,
            ClusterInfo fingertipCluster,
            POINTER_TOUCH_INFO ti)
        {
            int w = Math.Max(1, contact.Width);
            int h = Math.Max(1, contact.Height);
            int longSide = Math.Max(w, h);
            int shortSide = Math.Min(w, h);
            int area = w * h;
            int threshold = settings.PalmThreshold;

            if (w >= threshold || h >= threshold)
                return true;

            if (area >= settings.AbsoluteAreaThreshold)
                return true;

            float aspect = (float)longSide / shortSide;
            if (aspect >= settings.ArmAspectRatioThreshold && shortSide >= settings.ArmMinShortSide)
                return true;

            if (inPalmCluster && palmCluster.Area >= settings.ClusterAreaThreshold)
                return true;

            if (fingertipCluster.Area > 0 &&
                palmCluster.Area >= fingertipCluster.Area * settings.PalmToFingerAreaRatio &&
                palmCluster.Contains(contact))
                return true;

            if ((ti.touchMask & TOUCH_MASK.PRESSURE) != 0 &&
                ti.pressure >= settings.PressureThreshold)
                return true;

            if ((ti.touchMask & TOUCH_MASK.ORIENTATION) != 0)
            {
                if ((ti.orientation <= settings.OrientationLowReject ||
                     ti.orientation >= settings.OrientationHighReject) &&
                    longSide >= settings.OrientationSizeGate)
                {
                    return true;
                }
            }

            return false;
        }

        private void AddRecentContact(Rectangle rect, POINTER_TOUCH_INFO ti)
        {
            lock (recentContacts)
            {
                recentContacts.Add(new TimestampedTouch
                {
                    Rect = rect,
                    Info = ti,
                    Time = DateTime.UtcNow
                });

                DateTime cutoff = DateTime.UtcNow - contactHistoryWindow;
                recentContacts.RemoveAll(x => x.Time < cutoff);
            }
        }

        private (ClusterInfo left, ClusterInfo right) ComputeLeftRightClusters()
        {
            List<Rectangle> rects;
            lock (recentContacts)
            {
                rects = recentContacts.Select(x => x.Rect).ToList();
            }

            if (rects.Count == 0)
                return (EmptyCluster(), EmptyCluster());

            var centers = rects
                .Select(r => r.Left + (r.Width / 2))
                .OrderBy(x => x)
                .ToList();

            int medianX = centers[centers.Count / 2];

            List<Rectangle> leftRects = rects
                .Where(r => (r.Left + r.Width / 2) <= medianX)
                .ToList();

            List<Rectangle> rightRects = rects
                .Where(r => (r.Left + r.Width / 2) > medianX)
                .ToList();

            ClusterInfo left = MakeCluster(leftRects);
            ClusterInfo right = MakeCluster(rightRects);

            if (left.IsEmpty && rightRects.Count > 0)
            {
                Rectangle candidate = rightRects.OrderBy(r => r.Left).First();
                left = MakeCluster(new List<Rectangle> { candidate });
                right = MakeCluster(rightRects.Where(r => r != candidate).ToList());
            }
            else if (right.IsEmpty && leftRects.Count > 0)
            {
                Rectangle candidate = leftRects.OrderByDescending(r => r.Right).First();
                right = MakeCluster(new List<Rectangle> { candidate });
                left = MakeCluster(leftRects.Where(r => r != candidate).ToList());
            }

            return (left, right);
        }

        private static ClusterInfo MakeCluster(List<Rectangle> rects)
        {
            if (rects == null || rects.Count == 0)
                return EmptyCluster();

            return new ClusterInfo
            {
                BoundingBox = Rectangle.FromLTRB(
                    rects.Min(r => r.Left),
                    rects.Min(r => r.Top),
                    rects.Max(r => r.Right),
                    rects.Max(r => r.Bottom))
            };
        }

        private static ClusterInfo EmptyCluster()
        {
            return new ClusterInfo { BoundingBox = Rectangle.Empty };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (!settings.Enabled)
                return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool leftIsPalm = lastLeftCluster.Area >= lastRightCluster.Area;

            DrawCluster(g, lastLeftCluster, leftIsPalm);
            DrawCluster(g, lastRightCluster, !leftIsPalm);

            if (!lastContactRect.IsEmpty)
            {
                Pen? pen = lastWasRejected ? rejectedPen : validPen;
                if (pen != null)
                    g.DrawRectangle(pen, lastContactRect);
            }
        }

        private void DrawCluster(Graphics g, ClusterInfo cluster, bool isPalm)
        {
            if (cluster.IsEmpty)
                return;

            Brush? brush = isPalm ? palmBrush : fingertipBrush;
            if (brush != null)
                g.FillRectangle(brush, cluster.BoundingBox);

            if (clusterPen != null)
                g.DrawRectangle(clusterPen, cluster.BoundingBox);
        }

        private void CreateTray()
        {
            trayMenu = new ContextMenuStrip();

            trayMenu.Items.Add("Toggle Overlay", null, (s, e) => ToggleEnabled());
            trayMenu.Items.Add("Threshold +5", null, (s, e) => IncreaseThreshold());
            trayMenu.Items.Add("Threshold -5", null, (s, e) => DecreaseThreshold());
            trayMenu.Items.Add("Opacity +0.05", null, (s, e) => IncreaseOpacity());
            trayMenu.Items.Add("Opacity -0.05", null, (s, e) => DecreaseOpacity());
            trayMenu.Items.Add("Start With Windows", null, (s, e) => ToggleStartup());
            trayMenu.Items.Add("Exit", null, (s, e) => ExitApp());

            trayIcon = new NotifyIcon
            {
                Text = "Palm Rejector Pro",
                Visible = true,
                ContextMenuStrip = trayMenu,
                Icon = SystemIcons.Shield
            };
        }

        private void OverlayForm_KeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up:
                    IncreaseThreshold();
                    break;
                case Keys.Down:
                    DecreaseThreshold();
                    break;
                case Keys.Escape:
                    ExitApp();
                    break;
            }
        }

        private void ToggleStartup()
        {
            const string appName = "PalmRejectorPro";

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                true);

            if (key == null)
                return;

            if (key.GetValue(appName) == null)
                key.SetValue(appName, Application.ExecutablePath);
            else
                key.DeleteValue(appName, false);
        }

        private void LoadSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName(settingsPath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(settingsPath))
                {
                    Settings? loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
                    if (loaded != null)
                        settings = loaded;
                }
                else
                {
                    settings = new Settings();
                    SaveSettings();
                }
            }
            catch
            {
                settings = new Settings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                lock (settingsLock)
                {
                    string dir = Path.GetDirectoryName(settingsPath) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllText(
                        settingsPath,
                        JsonSerializer.Serialize(
                            settings,
                            new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch
            {
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            if (IsHandleCreated)
                UnregisterPointerInputTarget(Handle, PT_TOUCH);

            if (scouterForm != null && !scouterForm.IsDisposed)
            {
                scouterForm.Close();
                scouterForm.Dispose();
                scouterForm = null;
            }

            trayIcon?.Dispose();
            trayMenu?.Dispose();
            fingertipBrush?.Dispose();
            palmBrush?.Dispose();
            clusterPen?.Dispose();
            validPen?.Dispose();
            rejectedPen?.Dispose();
        }

        private static uint GET_POINTERID_WPARAM(IntPtr wParam)
        {
            return (uint)(wParam.ToInt64() & 0xFFFF);
        }

        private static Rectangle RectFromRECT(RECT rc)
        {
            return new Rectangle(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top);
        }

        private static RECT PointToRect(POINT p, int halfW, int halfH)
        {
            return new RECT
            {
                left = p.X - halfW,
                top = p.Y - halfH,
                right = p.X + halfW,
                bottom = p.Y + halfH
            };
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetPointerTouchInfo(uint pointerId, out POINTER_TOUCH_INFO touchInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetPointerType(uint pointerId, out uint pointerType);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterPointerInputTarget(IntPtr hwnd, uint pointerType);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPointerInputTarget(IntPtr hwnd, uint pointerType);

        private struct TimestampedTouch
        {
            public Rectangle Rect;
            public POINTER_TOUCH_INFO Info;
            public DateTime Time;
        }

        private struct ClusterInfo
        {
            public Rectangle BoundingBox;
            public int Area => Math.Max(1, BoundingBox.Width * BoundingBox.Height);
            public bool IsEmpty => BoundingBox.Width <= 0 || BoundingBox.Height <= 0;

            public bool Contains(Rectangle r)
            {
                return !IsEmpty && BoundingBox.IntersectsWith(r);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_INFO
        {
            public uint pointerType;
            public uint pointerId;
            public uint frameId;
            public uint pointerFlags;
            public IntPtr sourceDevice;
            public IntPtr hwndTarget;
            public POINT ptPixelLocation;
            public POINT ptHimetricLocation;
            public POINT ptPixelLocationRaw;
            public POINT ptHimetricLocationRaw;
            public uint dwTime;
            public uint historyCount;
            public int InputData;
            public uint dwKeyStates;
            public ulong PerformanceCount;
            public uint ButtonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_TOUCH_INFO
        {
            public POINTER_INFO pointerInfo;
            public TOUCH_FLAGS touchFlags;
            public TOUCH_MASK touchMask;
            public RECT rcContact;
            public RECT rcContactRaw;
            public uint orientation;
            public uint pressure;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        public enum TOUCH_FLAGS : uint
        {
            NONE = 0
        }

        [Flags]
        public enum TOUCH_MASK : uint
        {
            NONE = 0x00000000,
            CONTACTAREA = 0x00000001,
            ORIENTATION = 0x00000002,
            PRESSURE = 0x00000004
        }

        public class Settings
        {
            public int PalmThreshold { get; set; } = 45;
            public int AbsoluteAreaThreshold { get; set; } = 1500;
            public bool Enabled { get; set; } = true;
            public double OverlayOpacity { get; set; } = 0.85;
            public uint PressureThreshold { get; set; } = 800;
            public float ArmAspectRatioThreshold { get; set; } = 3.0f;
            public int ArmMinShortSide { get; set; } = 18;
            public int ClusterAreaThreshold { get; set; } = 2400;
            public double PalmToFingerAreaRatio { get; set; } = 1.8;
            public uint OrientationLowReject { get; set; } = 8;
            public uint OrientationHighReject { get; set; } = 82;
            public int OrientationSizeGate { get; set; } = 24;
            public int ScouterMarginRight { get; set; } = 18;
            public int ScouterMarginBottom { get; set; } = 18;
        }
    }

    public class ScouterForm : Form
    {
        private readonly OverlayForm owner;

        private Label? titleLabel;
        private Label? infoLabel;
        private Label? detailLabel;

        private Button? toggleButton;
        private Button? thresholdDownButton;
        private Button? thresholdUpButton;
        private Button? opacityDownButton;
        private Button? opacityUpButton;
        private Button? exitButton;

        public ScouterForm(OverlayForm owner)
        {
            this.owner = owner;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            Width = 250;
            Height = 205;
            BackColor = Color.FromArgb(20, 20, 20);
            DoubleBuffered = true;

            BuildUi();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;
                return cp;
            }
        }

        private void BuildUi()
        {
            titleLabel = new Label
            {
                Left = 10,
                Top = 8,
                Width = 230,
                Height = 24,
                Text = "⚡ SCOUTER CONTROL ⚡",
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gold,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };

            infoLabel = new Label
            {
                Left = 12,
                Top = 38,
                Width = 226,
                Height = 56,
                ForeColor = Color.FromArgb(255, 200, 80),
                BackColor = Color.Transparent,
                Font = new Font("Consolas", 9, FontStyle.Bold)
            };

            detailLabel = new Label
            {
                Left = 12,
                Top = 96,
                Width = 226,
                Height = 42,
                ForeColor = Color.LightCyan,
                BackColor = Color.Transparent,
                Font = new Font("Consolas", 8, FontStyle.Regular)
            };

            toggleButton = MakeButton("ON/OFF", 12, 144, 70);
            thresholdDownButton = MakeButton("THR-", 88, 144, 70);
            thresholdUpButton = MakeButton("THR+", 164, 144, 70);

            opacityDownButton = MakeButton("OP-", 12, 172, 70);
            opacityUpButton = MakeButton("OP+", 88, 172, 70);
            exitButton = MakeButton("EXIT", 164, 172, 70);

            toggleButton.Click += (s, e) => owner.ToggleEnabled();
            thresholdDownButton.Click += (s, e) => owner.DecreaseThreshold();
            thresholdUpButton.Click += (s, e) => owner.IncreaseThreshold();
            opacityDownButton.Click += (s, e) => owner.DecreaseOpacity();
            opacityUpButton.Click += (s, e) => owner.IncreaseOpacity();
            exitButton.Click += (s, e) => owner.ExitApp();

            Controls.Add(titleLabel);
            Controls.Add(infoLabel);
            Controls.Add(detailLabel);
            Controls.Add(toggleButton);
            Controls.Add(thresholdDownButton);
            Controls.Add(thresholdUpButton);
            Controls.Add(opacityDownButton);
            Controls.Add(opacityUpButton);
            Controls.Add(exitButton);

            Paint += ScouterForm_Paint;
        }

        private Button MakeButton(string text, int x, int y, int width)
        {
            Button btn = new Button
            {
                Text = text,
                Left = x,
                Top = y,
                Width = width,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(35, 35, 35),
                ForeColor = Color.Gold,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                TabStop = false
            };

            btn.FlatAppearance.BorderColor = Color.Gold;
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 60);
            btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 90, 90);

            return btn;
        }

        private void ScouterForm_Paint(object? sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using Pen borderPen = new Pen(Color.Gold, 1.5f);
            e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
        }

        public void UpdateDisplay(OverlayForm.Settings settings, string detail)
        {
            if (infoLabel != null)
            {
                infoLabel.Text =
                    $"Active : {settings.Enabled}\n" +
                    $"Thresh : {settings.PalmThreshold}px\n" +
                    $"Opacity: {settings.OverlayOpacity:0.00}";
            }

            if (detailLabel != null)
            {
                detailLabel.Text = detail;
            }
        }
    }
}
