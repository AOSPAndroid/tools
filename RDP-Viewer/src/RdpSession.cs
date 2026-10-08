using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AxMSTSCLib;
using MSTSCLib;

namespace RdpViewer
{
    internal sealed class RdpSession : IDisposable
    {
        private readonly Panel container;
        private AxMsRdpClient10NotSafeForScripting control;
        private readonly Timer verification = new Timer { Interval = 500 };
        private DisplayPlan requested;
        private Size confirmed;
        private DateTime requestedAt;
        private bool waiting, timedOut, disposed;
        public bool Busy { get; private set; }
        public bool LoggedIn { get; private set; }
        public event Action<string> StatusChanged;
        public event Action<string> Diagnostic;
        public event Action LoggedInEvent;
        public event Action GeometryChanged;

        public RdpSession(Panel parent)
        {
            container = parent;
            container.SizeChanged += ContainerResized;
            verification.Tick += VerifyTick;
        }
        public void Initialize()
        {
            if (control != null) return;
            control = new AxMsRdpClient10NotSafeForScripting();
            ((ISupportInitialize)control).BeginInit();
            control.TabIndex = 0;
            container.Controls.Add(control);
            ((ISupportInitialize)control).EndInit();
            ResizeControl();
            control.CreateControl();
            // These casts test the actual installed interfaces, not just the wrapper assembly.
            var display = (IMsRdpClient9)control.GetOcx();
            var credentials = (IMsRdpClientNonScriptable5)control.GetOcx();
            control.OnConnecting += (s, e) => Publish("Connecting; complete the Windows credential prompt.");
            control.OnConnected += (s, e) => Publish("RDP transport connected; waiting for logon.");
            control.OnLoginComplete += (s, e) =>
            {
                if (disposed) return;
                LoggedIn = true;
                Publish("Logged in; requesting the current viewport size.");
                if (LoggedInEvent != null) LoggedInEvent();
                control.Focus();
            };
            control.OnRemoteDesktopSizeChange += (s, e) =>
            {
                if (disposed) return;
                confirmed = new Size(e.width, e.height);
                Log("Remote-size event: " + e.width + " x " + e.height + ".");
                if (requested != null && confirmed == requested.Size)
                {
                    waiting = false;
                    verification.Stop();
                    Publish("Resolution confirmed: " + e.width + " x " + e.height + ".");
                }
                else if (LoggedIn)
                    Publish("Remote reports " + e.width + " x " + e.height + "; waiting for the requested size.");
            };
            control.OnDisconnected += (s, e) =>
            {
                if (disposed) return;
                LoggedIn = false;
                Busy = false;
                waiting = false;
                verification.Stop();
                uint extended = 0;
                string detail = "";
                try
                {
                    extended = (uint)control.ExtendedDisconnectReason;
                    detail = control.GetErrorDescription((uint)e.discReason, extended);
                }
                catch (COMException) { }
                Log("Disconnected: reason=" + e.discReason + "; extended=" + extended + ". " + detail);
                Publish("Disconnected (" + e.discReason + "/" + extended + "). See Diagnostics for details.");
            };
            control.OnFatalError += (s, e) =>
            {
                if (disposed) return;
                LoggedIn = false;
                verification.Stop();
                Log("RDP fatal error: " + e.errorCode + ".");
                Publish("RDP error " + e.errorCode + ". Use Disconnect, then inspect Diagnostics.");
            };
            control.SizeChanged += (s, e) => { if (GeometryChanged != null) GeometryChanged(); };
            Log("Microsoft RDP ActiveX control initialized; IMsRdpClient9 and credential interfaces available.");
        }
        private void ContainerResized(object sender, EventArgs e) { ResizeControl(); }
        private void ResizeControl()
        {
            if (control == null) return;
            // Reserve at most one pixel rather than asking for an illegal odd remote width.
            int width = Math.Max(0, container.ClientSize.Width);
            control.SetBounds(0, 0, width - width % 2, Math.Max(0, container.ClientSize.Height));
        }
        public DisplayPlan CurrentPlan()
        {
            if (control == null || !control.IsHandleCreated) throw new InvalidOperationException("RDP control is not initialized.");
            Size size = Native.ClientPixels(control.Handle);
            int dpi = (int)Native.GetDpiForWindow(control.Handle);
            return DisplayPlan.Create(size.Width, size.Height, dpi == 0 ? 96 : dpi);
        }
        internal void Configure(Endpoint endpoint, string account, bool clipboard, DisplayPlan plan)
        {
            if (Busy) throw new InvalidOperationException("Disconnect before changing connection settings.");
            Initialize();
            account = (account ?? "").Trim();
            if (account.Length > 256 || account.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("Invalid account name.");
            string domain = "", username = account;
            int slash = account.IndexOf('\\');
            if (slash >= 0)
            {
                domain = account.Substring(0, slash);
                username = account.Substring(slash + 1);
                if (domain.Length == 0 || username.Length == 0 || username.Contains("\\"))
                    throw new ArgumentException("Use DOMAIN\\user, user@domain, or leave Account blank.");
            }
            control.Server = endpoint.Host;
            control.Domain = domain;
            control.UserName = username;
            control.DesktopWidth = plan.Width;
            control.DesktopHeight = plan.Height;
            control.ColorDepth = 32;
            control.FullScreen = false;
            var advanced = control.AdvancedSettings9;
            advanced.RDPPort = endpoint.Port;
            advanced.SmartSizing = false;
            advanced.EnableCredSspSupport = true;
            advanced.AuthenticationLevel = 1; // Server authentication must succeed; no ignore-certificate option.
            advanced.EnableAutoReconnect = false;
            advanced.RedirectClipboard = clipboard;
            advanced.RedirectDrives = false;
            advanced.RedirectPrinters = false;
            advanced.RedirectPorts = false;
            advanced.RedirectSmartCards = false;
            advanced.AudioCaptureRedirectionMode = false;
            control.SecuredSettings2.AudioRedirectionMode = 2;
            var ns3 = (IMsRdpClientNonScriptable3)control.GetOcx();
            ns3.EnableCredSspSupport = true;
            ns3.PromptForCredentials = true;
            ns3.RedirectDynamicDevices = false;
            ns3.RedirectDynamicDrives = false;
            ns3.ShowRedirectionWarningDialog = true;
            var ns4 = (IMsRdpClientNonScriptable4)control.GetOcx();
            ns4.AllowCredentialSaving = false;
            ns4.PromptForCredsOnClient = true;
            var ns5 = (IMsRdpClientNonScriptable5)control.GetOcx();
            ns5.AllowPromptingForCredentials = true;
            ns5.UseMultimon = false;
            ns5.DisableConnectionBar = true;
            Log("Connection configured: CredSSP enabled, server authentication required, smart sizing off, clipboard=" + clipboard + ".");
        }
        internal bool SecureConfigurationVerified()
        {
            var ns4 = (IMsRdpClientNonScriptable4)control.GetOcx();
            return control.AdvancedSettings9.EnableCredSspSupport && control.AdvancedSettings9.AuthenticationLevel == 1 &&
                !control.AdvancedSettings9.SmartSizing && !ns4.AllowCredentialSaving;
        }
        public void Connect(Endpoint endpoint, string account, bool clipboard)
        {
            if (Busy) throw new InvalidOperationException("An RDP connection is already active.");
            // Recreate between connections so transient state cannot leak into another target.
            if (control != null)
            {
                container.Controls.Remove(control);
                control.Dispose();
                control = null;
            }
            Initialize();
            requested = null;
            confirmed = Size.Empty;
            waiting = timedOut = false;
            Configure(endpoint, account, clipboard, CurrentPlan());
            Busy = true;
            LoggedIn = false;
            Publish("Connecting; credentials stay in the Windows RDP interface.");
            try { control.Connect(); }
            catch
            {
                Busy = false;
                Publish("Connection failed before logon. See Diagnostics.");
                throw;
            }
        }
        public void ResizeSession(bool force)
        {
            if (!LoggedIn || disposed) return;
            DisplayPlan plan = CurrentPlan();
            if (!force && plan.SameAs(requested)) return;
            requested = plan;
            requestedAt = DateTime.UtcNow;
            waiting = true;
            timedOut = false;
            try
            {
                // A method return is not proof of a server-side resize. Confirm via the size-change event.
                // Physical dimensions are unspecified (0); this is a virtual window, not a physical monitor.
                ((IMsRdpClient9)control.GetOcx()).UpdateSessionDisplaySettings(
                    (uint)plan.Width, (uint)plan.Height, 0, 0, 0, plan.Scale, 100);
                Log("Display request: " + plan.Width + " x " + plan.Height + "; scale=" + plan.Scale + "%.");
                if (confirmed == plan.Size)
                {
                    waiting = false;
                    Publish("Resolution matches: " + plan.Width + " x " + plan.Height + ". Scale requested: " + plan.Scale + "%.");
                }
                else
                {
                    Publish("Resize requested: " + plan.Width + " x " + plan.Height + "; awaiting remote confirmation.");
                    verification.Start();
                }
            }
            catch (COMException ex)
            {
                waiting = false;
                verification.Stop();
                Log("UpdateSessionDisplaySettings failed: 0x" + ex.ErrorCode.ToString("X8") + ". " + ex.Message);
                Publish("Dynamic resize failed (0x" + ex.ErrorCode.ToString("X8") + "). No stretching or reconnect fallback was applied.");
            }
        }
        private void VerifyTick(object sender, EventArgs e)
        {
            if (!waiting || timedOut || !LoggedIn) return;
            if ((DateTime.UtcNow - requestedAt).TotalSeconds < 6) return;
            timedOut = true;
            verification.Stop();
            Log("No matching remote-size confirmation within 6 seconds.");
            Publish("Resize not confirmed. Remote: " + confirmed.Width + " x " + confirmed.Height +
                "; requested: " + requested.Width + " x " + requested.Height + ". Retry fit or inspect Diagnostics.");
        }
        public void Disconnect()
        {
            if (control == null) return;
            verification.Stop();
            LoggedIn = false;
            if (control.Connected != 0)
            {
                Publish("Disconnecting; the remote session is not being logged off.");
                control.Disconnect();
            }
            else { Busy = false; Publish("Disconnected."); }
        }
        private void Log(string text) { if (!disposed && Diagnostic != null) Diagnostic(text); }
        private void Publish(string text) { if (!disposed && StatusChanged != null) StatusChanged(text); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            container.SizeChanged -= ContainerResized;
            verification.Stop();
            verification.Dispose();
            if (control != null)
            {
                try { if (control.Connected != 0) control.Disconnect(); } catch (COMException) { }
                container.Controls.Remove(control);
                control.Dispose();
                control = null;
            }
        }
    }
}
