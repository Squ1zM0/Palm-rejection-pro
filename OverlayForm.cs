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

        private readonly List<PalmBlob> palmBlobs = new List<PalmBlob>();
        private readonly System.Windows.Forms.Timer blobDecayTimer;
        private readonly List<Rectangle> onboardingSamples = new List<Rectangle>();
        private bool onboardingActive;

        private readonly Color kiOrange = Color.FromArgb(150, 255, 120, 0);
        private readonly Color kiGold = Color.FromArgb(220, 255, 215, 0);
        private readonly Color kiRed = Color.FromArgb(210, 255, 70, 40);

        private Brush? palmBrush;
        private Pen? outerRingPen;
        private Pen? validPen;
        private Pen? rejectedPen;

        private Rectangle lastContactRect = Rectangle.Empty;
        private bool lastWasRejected = false;

        private string lastDetail = "READY";

        public OverlayForm()
        {
            LoadSettings();
            InitBrushes();
            blobDecayTimer = new System.Windows.Forms.Timer();
            blobDecayTimer.Tick += BlobDecayTimer_Tick;
            ConfigureBlobTimer();

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
            blobDecayTimer.Start();
            EnsureScouter();
            UpdateScouter();
            BeginOnboardingIfNeeded();
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
            palmBrush = new SolidBrush(kiOrange);
            outerRingPen = new Pen(kiGold, 2f) { DashStyle = DashStyle.Dash };
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

                m.Result = (IntPtr)HTCLIENT;
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
                            : PointToRect(ti.pointerInfo.ptPixelLocation, 15, 15);

                        Rectangle contact = RectFromRECT(rc);
                        int historyCount = (int)ti.pointerInfo.historyCount;

                        if (onboardingActive)
                        {
                            CaptureOnboardingSample(contact);
                        }

                        TouchBlobResult result = UpdateBlobs(contact, ti, historyCount);
                        lastContactRect = contact;
                        lastWasRejected = result.IsRejected;

                        string detail =
                            $"POS {contact.X},{contact.Y}\n" +
                            $"SIZE {contact.Width}x{contact.Height}\n" +
                            $"BLOBS {result.LiveBlobCount}\n" +
                            $"MODE {(result.IsRejected ? "PALM/ARM BLOCK" : "VALID TOUCH")}";

                        UpdateScouter(detail);
                        Invalidate();

                        if (result.IsRejected)
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

        private TouchBlobResult UpdateBlobs(Rectangle contact, POINTER_TOUCH_INFO ti, int historyCount)
        {
            Rectangle padded = Expand(contact, settings.PalmPadding);

            PalmBlob? targetBlob = null;
            List<PalmBlob> overlapping = new List<PalmBlob>();
            for (int i = 0; i < palmBlobs.Count; i++)
            {
                PalmBlob blob = palmBlobs[i];
                if (!blob.IsAlive)
                    continue;

                if (blob.OuterRing.IntersectsWith(contact))
                    overlapping.Add(blob);
            }

            if (overlapping.Count > 0)
            {
                targetBlob = overlapping[0];
                for (int i = 1; i < overlapping.Count; i++)
                {
                    PalmBlob extra = overlapping[i];
                    targetBlob.InnerMask = Rectangle.Union(targetBlob.InnerMask, extra.InnerMask);
                    palmBlobs.Remove(extra);
                }

                targetBlob.InnerMask = Rectangle.Union(targetBlob.InnerMask, padded);
                targetBlob.OuterRing = Expand(targetBlob.InnerMask, settings.OuterRingSize);
                targetBlob.LastSeen = DateTime.UtcNow;
                targetBlob.Confidence = 1f;
            }
            else if (IsLargeContact(contact, ti, historyCount))
            {
                PalmBlob seeded = new PalmBlob
                {
                    InnerMask = padded,
                    OuterRing = Expand(padded, settings.OuterRingSize),
                    LastSeen = DateTime.UtcNow,
                    Confidence = 1f
                };
                palmBlobs.Add(seeded);
            }

            bool rejected = palmBlobs.Any(blob => blob.IsAlive && blob.InnerMask.IntersectsWith(contact));
            int liveCount = palmBlobs.Count(blob => blob.IsAlive);
            return new TouchBlobResult
            {
                IsRejected = rejected,
                LiveBlobCount = liveCount
            };
        }

        private bool IsLargeContact(Rectangle contact, POINTER_TOUCH_INFO ti, int historyCount)
        {
            int width = Math.Max(1, contact.Width);
            int height = Math.Max(1, contact.Height);
            int longSide = Math.Max(width, height);
            int shortSide = Math.Min(width, height);
            int area = width * height;

            if (width >= settings.PalmThreshold || height >= settings.PalmThreshold)
                return true;

            if (area >= settings.AbsoluteAreaThreshold)
                return true;

            if ((ti.touchMask & TOUCH_MASK.PRESSURE) != 0 &&
                ti.pressure >= settings.PressureThreshold)
                return true;

            if ((ti.touchMask & TOUCH_MASK.ORIENTATION) != 0 &&
                (ti.orientation <= settings.OrientationLowReject ||
                 ti.orientation >= settings.OrientationHighReject) &&
                longSide >= settings.OrientationSizeGate)
                return true;

            if (shortSide > 0)
            {
                float aspect = (float)longSide / shortSide;
                if (aspect >= settings.ArmAspectRatioThreshold &&
                    shortSide >= settings.ArmMinShortSide)
                    return true;
            }

            if (historyCount >= 2 && area >= settings.AbsoluteAreaThreshold / 2)
                return true;

            return false;
        }

        private void BlobDecayTimer_Tick(object? sender, EventArgs e)
        {
            bool changed = false;
            for (int i = palmBlobs.Count - 1; i >= 0; i--)
            {
                PalmBlob blob = palmBlobs[i];
                if ((DateTime.UtcNow - blob.LastSeen).TotalMilliseconds >= blobDecayTimer.Interval)
                {
                    blob.Confidence -= settings.BlobDecayRate;
                    changed = true;
                }

                if (!blob.IsAlive)
                {
                    palmBlobs.RemoveAt(i);
                    changed = true;
                }
                else
                {
                    palmBlobs[i] = blob;
                }
            }

            if (changed)
                Invalidate();
        }

        private void ConfigureBlobTimer()
        {
            blobDecayTimer.Interval = Math.Max(50, settings.BlobDecayIntervalMs);
        }

        private static Rectangle Expand(Rectangle rect, int margin)
        {
            if (rect.IsEmpty)
                return rect;

            return Rectangle.FromLTRB(
                rect.Left - margin,
                rect.Top - margin,
                rect.Right + margin,
                rect.Bottom + margin);
        }

        private void BeginOnboardingIfNeeded()
        {
            if (settings.OnboardingCompleted)
                return;

            onboardingActive = true;
            onboardingSamples.Clear();
            MessageBox.Show(
                "Palm calibration: place your palm on the touchscreen for a moment to capture baseline size.",
                "Palm Rejector Pro Calibration",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        public void StartCalibration()
        {
            settings.OnboardingCompleted = false;
            SaveSettings();
            BeginOnboardingIfNeeded();
            UpdateScouterStatus("CALIBRATING");
        }

        private void CaptureOnboardingSample(Rectangle contact)
        {
            int area = Math.Max(1, contact.Width) * Math.Max(1, contact.Height);
            if (area < 200)
                return;

            onboardingSamples.Add(contact);
            UpdateScouterStatus($"CALIBRATING {onboardingSamples.Count}/12");
            if (onboardingSamples.Count >= 12)
            {
                CompleteOnboarding();
            }
        }

        private void CompleteOnboarding()
        {
            onboardingActive = false;
            if (onboardingSamples.Count == 0)
                return;

            List<int> widths = onboardingSamples.Select(r => Math.Max(r.Width, r.Height)).OrderBy(v => v).ToList();
            List<int> areas = onboardingSamples.Select(r => Math.Max(1, r.Width) * Math.Max(1, r.Height)).OrderBy(v => v).ToList();

            int widthP75 = widths[(int)(widths.Count * 0.75)];
            int areaP75 = areas[(int)(areas.Count * 0.75)];

            settings.PalmThreshold = Math.Max(20, (int)Math.Round(widthP75 * 0.7));
            settings.AbsoluteAreaThreshold = Math.Max(700, (int)Math.Round(areaP75 * 0.55));
            settings.OnboardingCompleted = true;
            SaveSettings();
            UpdateScouterStatus("CALIBRATION COMPLETE");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (!settings.Enabled)
                return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            DrawBlobs(g);

            if (!lastContactRect.IsEmpty)
            {
                Pen? pen = lastWasRejected ? rejectedPen : validPen;
                if (pen != null)
                    g.DrawRectangle(pen, lastContactRect);
            }
        }

        private void DrawBlobs(Graphics g)
        {
            for (int i = 0; i < palmBlobs.Count; i++)
            {
                PalmBlob blob = palmBlobs[i];
                if (!blob.IsAlive)
                    continue;

                if (palmBrush != null)
                    g.FillRectangle(palmBrush, blob.InnerMask);

                if (outerRingPen != null)
                    g.DrawRectangle(outerRingPen, blob.OuterRing);
            }
        }

        private void CreateTray()
        {
            trayMenu = new ContextMenuStrip();

            trayMenu.Items.Add("Toggle Overlay", null, (s, e) => ToggleEnabled());
            trayMenu.Items.Add("Threshold +5", null, (s, e) => IncreaseThreshold());
            trayMenu.Items.Add("Threshold -5", null, (s, e) => DecreaseThreshold());
            trayMenu.Items.Add("Opacity +0.05", null, (s, e) => IncreaseOpacity());
            trayMenu.Items.Add("Opacity -0.05", null, (s, e) => DecreaseOpacity());
            trayMenu.Items.Add("Recalibrate", null, (s, e) => StartCalibration());
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

            blobDecayTimer.Stop();
            blobDecayTimer.Dispose();
            trayIcon?.Dispose();
            trayMenu?.Dispose();
            palmBrush?.Dispose();
            outerRingPen?.Dispose();
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

        private struct ClusterInfo
        {
            public Rectangle BoundingBox { get; set; }
            public int Area => IsEmpty ? 0 : BoundingBox.Width * BoundingBox.Height;
            public bool IsEmpty => BoundingBox.Width <= 0 || BoundingBox.Height <= 0;

            public bool Contains(Rectangle r)
            {
                return !IsEmpty && BoundingBox.IntersectsWith(r);
            }
        }

        private struct TouchBlobResult
        {
            public bool IsRejected;
            public int LiveBlobCount;
        }

        private class PalmBlob
        {
            public Rectangle InnerMask { get; set; }
            public Rectangle OuterRing { get; set; }
            public DateTime LastSeen { get; set; }
            public float Confidence { get; set; }
            public bool IsAlive => Confidence > 0.05f;
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
            public int PalmPadding { get; set; } = 40;
            public int OuterRingSize { get; set; } = 60;
            public float BlobDecayRate { get; set; } = 0.08f;
            public int BlobDecayIntervalMs { get; set; } = 150;
            public bool Enabled { get; set; } = true;
            public bool OnboardingCompleted { get; set; } = false;
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
