using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace PalmRejectorPro
{
    public partial class OverlayForm : Form
    {
        private readonly Timer updateTimer;
        private readonly List<SupportBlob> blobs = new();
        private const int OuterPadding = 60;
        private const float DecayRate = 0.0025f;
        private Point? currentTouchPoint = null;

        public OverlayForm()
        {
            // Form setup – transparent overlay
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Normal;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = Screen.PrimaryScreen.Bounds;
            this.BackColor = Color.Lime;          // key color
            this.TransparencyKey = Color.Lime;     // lime becomes transparent
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;

            // Optional: make form click-through to underlying apps
            // (comment out if you want the overlay to receive mouse/touch)
            // this.SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            // IntPtr exStyle = GetWindowLong(this.Handle, GWL_EXSTYLE);
            // exStyle = (IntPtr)((int)exStyle | WS_EX_TRANSPARENT);
            // SetWindowLong(this.Handle, GWL_EXSTYLE, exStyle);

            // Timer for decay animation
            updateTimer = new Timer();
            updateTimer.Interval = 16; // ~60 fps
            updateTimer.Tick += UpdateLoop;
            updateTimer.Start();

            // Enable mouse/touch events
            this.MouseDown += OnMouseTouch;
            this.MouseMove += OnMouseTouch;
            this.MouseUp += (s, e) => currentTouchPoint = null;
        }

        private void OnMouseTouch(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Create a small rectangle around the touch point (e.g., 10x10)
                var rect = new Rectangle(e.X - 5, e.Y - 5, 10, 10);
                RegisterTouch(rect);
                currentTouchPoint = e.Location;
                Invalidate(); // immediate visual feedback
            }
        }

        private void UpdateLoop(object? sender, EventArgs e)
        {
            bool changed = false;
            foreach (var blob in blobs.ToList())
            {
                blob.Confidence -= DecayRate;
                if (blob.Confidence <= 0f)
                {
                    blobs.Remove(blob);
                    changed = true;
                    continue;
                }
                blob.UpdateMasks();
                changed = true;
            }
            if (changed) Invalidate();
        }

        public void RegisterTouch(Rectangle contactRect)
        {
            var matchingBlob = blobs.FirstOrDefault(b => b.OuterMask.IntersectsWith(contactRect));
            if (matchingBlob != null)
            {
                matchingBlob.Absorb(contactRect);
            }
            else
            {
                blobs.Add(new SupportBlob(contactRect));
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            foreach (var blob in blobs)
            {
                // Inner mask: red, solid outline, semi-transparent fill (confidence)
                using var innerPen = new Pen(Color.Red, 2);
                using var innerBrush = new SolidBrush(Color.FromArgb(
                    (int)(blob.Confidence * 80), Color.Red));
                e.Graphics.FillRectangle(innerBrush, blob.InnerMask);
                e.Graphics.DrawRectangle(innerPen, blob.InnerMask);

                // Outer mask: orange, dashed outline
                using var outerPen = new Pen(Color.Orange, 2) { DashStyle = DashStyle.Dash };
                e.Graphics.DrawRectangle(outerPen, blob.OuterMask);
            }
        }

        private class SupportBlob
        {
            public Rectangle InnerMask;
            public Rectangle OuterMask;
            public float Confidence = 1.0f;

            public SupportBlob(Rectangle initial)
            {
                InnerMask = initial;
                UpdateMasks();
            }

            public void Absorb(Rectangle rect)
            {
                InnerMask = Rectangle.Union(InnerMask, rect);
                Confidence = Math.Min(1.0f, Confidence + 0.08f);
                UpdateMasks();
            }

            public void UpdateMasks()
            {
                OuterMask = Rectangle.Inflate(InnerMask, OuterPadding, OuterPadding);
            }
        }
    }
}