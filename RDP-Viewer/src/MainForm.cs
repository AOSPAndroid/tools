using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RdpViewer
{
    internal sealed class MainForm : Form
    {
        private readonly Panel viewport = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.AppWorkspace };
        private readonly ToolStripTextBox server = new ToolStripTextBox { Width = 220, ToolTipText = "Host name or IP address. Prefer the full DNS name matching the server certificate." };
        private readonly ToolStripTextBox port = new ToolStripTextBox { Width = 48, Text = "3389" };
        private readonly ToolStripTextBox account = new ToolStripTextBox { Width = 190, ToolTipText = "Optional DOMAIN\\user or user@domain. No password is entered here." };
        private readonly ToolStripButton connect = new ToolStripButton("Connect");
        private readonly ToolStripButton disconnect = new ToolStripButton("Disconnect") { Enabled = false };
        private readonly ToolStripButton clipboard = new ToolStripButton("Clipboard: off") { CheckOnClick = true };
        private readonly ToolStripComboBox monitors = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly ToolStripComboBox placement = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 112 };
        private readonly ToolStripButton remember = new ToolStripButton("Remember layout") { CheckOnClick = true, Checked = true, ToolTipText = "Saves monitor and window bounds locally. Does not save host names or credentials." };
        private readonly ToolStripStatusLabel status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Timer debounce = new Timer { Interval = 300 };
        private readonly Queue<string> messages = new Queue<string>();
        private RdpSession session;
        private bool ready, restoring;
        private SavedLayout saved;

        public MainForm()
        {
            Text = "RDP Viewer - dynamic resolution";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            MinimumSize = new Size(880, 500);
            Size = new Size(1100, 800);
            StartPosition = FormStartPosition.Manual;
            var connectionBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            connectionBar.Items.AddRange(new ToolStripItem[] {
                new ToolStripLabel("Computer"), server, new ToolStripLabel("Port"), port,
                new ToolStripLabel("Account"), account, connect, disconnect, clipboard });
            var layoutBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            var apply = new ToolStripButton("Place window");
            var retry = new ToolStripButton("Retry fit");
            var diagnostics = new ToolStripButton("Diagnostics");
            layoutBar.Items.AddRange(new ToolStripItem[] { new ToolStripLabel("Monitor"), monitors, placement, apply, retry, remember, diagnostics });
            placement.Items.AddRange(new object[] { "Bottom half", "Top half", "Full work area" });
            placement.SelectedIndex = 0;
            var statusBar = new StatusStrip { Dock = DockStyle.Bottom };
            statusBar.Items.Add(status);
            Controls.Add(viewport);
            Controls.Add(statusBar);
            Controls.Add(layoutBar);
            Controls.Add(connectionBar);
            status.Text = "Direct-PC RDP only. Enter a computer, then Connect. Passwords stay in the Windows prompt.";
            connect.Click += (s, e) => ConnectSession();
            disconnect.Click += (s, e) => Guard(() => session.Disconnect());
            apply.Click += (s, e) => Guard(ApplyPlacement);
            retry.Click += (s, e) => Guard(() => session.ResizeSession(true));
            diagnostics.Click += (s, e) => ShowDiagnostics();
            clipboard.CheckedChanged += (s, e) => clipboard.Text = clipboard.Checked ? "Clipboard: on" : "Clipboard: off";
            debounce.Tick += (s, e) => { debounce.Stop(); if (session != null) Guard(() => session.ResizeSession(false)); };
            ResizeEnd += (s, e) => ScheduleResize();
            Shown += OnFirstShown;
            FormClosing += OnClosing;
            FormClosed += (s, e) =>
            {
                SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
                debounce.Dispose();
                if (session != null) session.Dispose();
            };
            SystemEvents.DisplaySettingsChanged += DisplaysChanged;
            try { saved = LayoutStore.Load(); } catch (Exception ex) { Log("Saved layout ignored: " + ex.Message); }
        }
        private void OnFirstShown(object sender, EventArgs e)
        {
            RefreshMonitors(saved == null ? null : saved.Monitor);
            Guard(() =>
            {
                var selected = (MonitorChoice)monitors.SelectedItem;
                if (saved != null)
                {
                    Rectangle work = selected.Screen.WorkingArea;
                    Bounds = LayoutMath.Clamp(new Rectangle(work.Left + saved.OffsetX, work.Top + saved.OffsetY, saved.Width, saved.Height), work, MinimumSize);
                }
                else ApplyPlacement();
                session = new RdpSession(viewport);
                session.StatusChanged += UpdateStatus;
                session.Diagnostic += Log;
                session.GeometryChanged += ScheduleResize;
                session.LoggedInEvent += () => BeginInvoke(new Action(() => Guard(() => session.ResizeSession(true))));
                session.Initialize();
                ready = true;
                UpdateButtons();
            });
            if (!ready) connect.Enabled = false;
        }
        private void ConnectSession()
        {
            Guard(() =>
            {
                int parsedPort;
                if (!int.TryParse(port.Text, out parsedPort)) throw new ArgumentException("Port must be a number between 1 and 65535.");
                Endpoint endpoint = Endpoint.Parse(server.Text, parsedPort);
                session.Connect(endpoint, account.Text, clipboard.Checked);
                UpdateButtons();
            });
        }
        private void UpdateStatus(string text)
        {
            if (IsDisposed) return;
            status.Text = text;
            status.ToolTipText = text;
            Log(text);
            UpdateButtons();
        }
        private void UpdateButtons()
        {
            bool busy = session != null && session.Busy;
            connect.Enabled = ready && !busy;
            disconnect.Enabled = busy;
            server.Enabled = port.Enabled = account.Enabled = clipboard.Enabled = !busy;
        }
        private void ScheduleResize()
        {
            if (IsDisposed || restoring || WindowState == FormWindowState.Minimized) return;
            debounce.Stop();
            debounce.Start();
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x02E0 && ready) ScheduleResize(); // WM_DPICHANGED, after Windows applies its geometry.
        }
        private void RefreshMonitors(string preferred)
        {
            if (preferred == null && monitors.SelectedItem is MonitorChoice)
                preferred = ((MonitorChoice)monitors.SelectedItem).Screen.DeviceName;
            var screens = Screen.AllScreens.OrderBy(x => x.Bounds.Left).ThenBy(x => x.Bounds.Top).ToArray();
            monitors.Items.Clear();
            foreach (var screen in screens) monitors.Items.Add(new MonitorChoice(screen, screen == screens[0]));
            MonitorChoice choice = monitors.Items.Cast<MonitorChoice>().FirstOrDefault(x => x.Screen.DeviceName == preferred);
            if (choice == null)
                choice = monitors.Items.Cast<MonitorChoice>().FirstOrDefault(x => x.Screen.Bounds.Width == 1440 && x.Screen.Bounds.Height == 2560);
            monitors.SelectedItem = choice ?? monitors.Items.Cast<MonitorChoice>().First(x => x.Screen.Primary);
        }
        private void ApplyPlacement()
        {
            var choice = monitors.SelectedItem as MonitorChoice;
            if (choice == null) return;
            Rectangle target = LayoutMath.Zone(choice.Screen.Bounds, choice.Screen.WorkingArea, (Placement)placement.SelectedIndex);
            if (target.Width < MinimumSize.Width || target.Height < MinimumSize.Height)
                throw new InvalidOperationException("That zone is smaller than the viewer's minimum window size. Choose Full work area or a larger monitor.");
            restoring = true;
            WindowState = FormWindowState.Normal;
            Bounds = target;
            restoring = false;
            // A cross-DPI move can adjust non-client dimensions. Reapply after that transition.
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                Bounds = target;
                ScheduleResize();
            }));
        }
        private void DisplaysChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() => Guard(() =>
                {
                    RefreshMonitors(null);
                    Screen screen = Screen.FromControl(this);
                    if (WindowState == FormWindowState.Normal) Bounds = LayoutMath.Clamp(Bounds, screen.WorkingArea, MinimumSize);
                    ScheduleResize();
                })));
            }
            catch (InvalidOperationException) { }
        }
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && session != null && session.Busy &&
                MessageBox.Show(this, "Disconnect this viewer? This does not log off the remote Windows session.", "Close RDP Viewer",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            { e.Cancel = true; return; }
            try
            {
                if (!remember.Checked) LayoutStore.Delete();
                else
                {
                    Rectangle rect = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                    Screen screen = Screen.FromRectangle(rect);
                    LayoutStore.Save(new SavedLayout { Version = 1, Monitor = screen.DeviceName,
                        OffsetX = rect.Left - screen.WorkingArea.Left, OffsetY = rect.Top - screen.WorkingArea.Top,
                        Width = rect.Width, Height = rect.Height });
                }
            }
            catch (Exception ex) { MessageBox.Show(this, "Window layout could not be saved: " + ex.Message, "RDP Viewer"); }
        }
        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                Log(ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + "): " + ex.Message);
                status.Text = "Operation failed. See Diagnostics.";
                UpdateButtons();
                MessageBox.Show(this, ex.Message, "RDP Viewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        private void Log(string text)
        {
            messages.Enqueue(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'") + "  " + text);
            while (messages.Count > 200) messages.Dequeue();
        }
        private void ShowDiagnostics()
        {
            using (var dialog = new Form { Text = "RDP Viewer - diagnostics (memory only)", StartPosition = FormStartPosition.CenterParent,
                Size = new Size(880, 520), MinimizeBox = false, Font = Font })
            {
                var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                    Dock = DockStyle.Fill, Font = new Font("Consolas", 9F), Text = string.Join(Environment.NewLine, messages) };
                var copy = new Button { Text = "Copy diagnostics", Dock = DockStyle.Bottom, Height = 36 };
                copy.Click += (s, e) => Guard(() => Clipboard.SetText(text.Text.Length == 0 ? "No diagnostics." : text.Text));
                dialog.Controls.Add(text);
                dialog.Controls.Add(copy);
                dialog.ShowDialog(this);
            }
        }
        private sealed class MonitorChoice
        {
            public Screen Screen { get; private set; }
            private readonly bool leftmost;
            public MonitorChoice(Screen screen, bool left) { Screen = screen; leftmost = left; }
            public override string ToString()
            {
                return Screen.DeviceName + " | " + Screen.Bounds.Width + " x " + Screen.Bounds.Height +
                    (leftmost ? " | leftmost" : "") + (Screen.Primary ? " | primary" : "");
            }
        }
    }
}
