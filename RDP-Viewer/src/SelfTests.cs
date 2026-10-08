using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RdpViewer
{
    internal static class SelfTests
    {
        public static int Run(string reportPath, bool smoke)
        {
            var results = new List<string>();
            int failures = 0;
            Action<string, Action> test = (name, action) =>
            {
                try { action(); results.Add("PASS " + name); }
                catch (Exception ex) { failures++; results.Add("FAIL " + name + ": " + ex); }
            };
            if (!smoke)
            {
                test("Portrait bottom half excludes taskbar and preserves negative X", () =>
                    Equal(new Rectangle(-1440, 1280, 1440, 1232), LayoutMath.Zone(new Rectangle(-1440, 0, 1440, 2560), new Rectangle(-1440, 0, 1440, 2512), Placement.BottomHalf)));
                test("Portrait top half does not inherit bottom taskbar loss", () =>
                    Equal(new Rectangle(-1440, 0, 1440, 1280), LayoutMath.Zone(new Rectangle(-1440, 0, 1440, 2560), new Rectangle(-1440, 0, 1440, 2512), Placement.TopHalf)));
                test("Offset monitor and side taskbar", () =>
                    Equal(new Rectangle(-1360, 1180, 1360, 1280), LayoutMath.Zone(new Rectangle(-1440, -100, 1440, 2560), new Rectangle(-1360, -100, 1360, 2560), Placement.BottomHalf)));
                test("Full work area", () =>
                    Equal(new Rectangle(1920, 40, 2560, 1400), LayoutMath.Zone(new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 40, 2560, 1400), Placement.FullWorkArea)));
                test("Off-screen saved window clamps to surviving monitor", () =>
                    Equal(new Rectangle(0, 0, 900, 600), LayoutMath.Clamp(new Rectangle(-5000, -5000, 900, 600), new Rectangle(0, 0, 1920, 1040), new Size(880, 500))));
                test("Oversized saved window fits work area", () =>
                    Equal(new Rectangle(0, 0, 1920, 1040), LayoutMath.Clamp(new Rectangle(0, 0, 9000, 6000), new Rectangle(0, 0, 1920, 1040), new Size(880, 500))));
                test("Odd width rounded down, not stretched", () => Equal(1400, DisplayPlan.Create(1401, 1100, 96).Width));
                test("100 percent DPI", () => Equal((uint)100, DisplayPlan.Create(1400, 1100, 96).Scale));
                test("150 percent DPI", () => Equal((uint)150, DisplayPlan.Create(1400, 1100, 144).Scale));
                test("Scale capped at protocol maximum", () => Equal((uint)500, DisplayPlan.Create(1400, 1100, 960).Scale));
                test("Tiny viewport rejected", () => Throws(() => DisplayPlan.Create(199, 300, 96)));
                test("Over-limit viewport rejected", () => Throws(() => DisplayPlan.Create(8193, 300, 96)));
                test("DPI-only changes are not deduplicated", () => Equal(false, DisplayPlan.Create(1400, 1100, 96).SameAs(DisplayPlan.Create(1400, 1100, 144))));
                test("Identical display plans deduplicate", () => Equal(true, DisplayPlan.Create(1400, 1100, 96).SameAs(DisplayPlan.Create(1400, 1100, 96))));
                test("FQDN accepted", () => Equal("pc.example.test", Endpoint.Parse(" pc.example.test ", 3389).Host));
                test("IPv4 accepted", () => Equal("192.0.2.1", Endpoint.Parse("192.0.2.1", 3389).Host));
                test("IPv6 brackets removed", () => Equal("2001:db8::1", Endpoint.Parse("[2001:db8::1]", 3389).Host));
                test("URL rejected", () => Throws(() => Endpoint.Parse("https://example.test", 3389)));
                test("Embedded port rejected", () => Throws(() => Endpoint.Parse("example.test:3389", 3389)));
                test("Invalid port rejected", () => Throws(() => Endpoint.Parse("pc", 65536)));
                test("Blank host rejected", () => Throws(() => Endpoint.Parse(" ", 3389)));
                test("Control characters rejected", () => Throws(() => Endpoint.Parse("pc\r\nother", 3389)));
                test("Layout XML round trip", () =>
                {
                    var layout = new SavedLayout { Version = 1, Monitor = "DISPLAY3", OffsetX = 0, OffsetY = 1280, Width = 1440, Height = 1232 };
                    var copy = LayoutStore.ReadXml(LayoutStore.ToXml(layout));
                    Equal(layout.Monitor, copy.Monitor); Equal(layout.Height, copy.Height);
                });
                test("Layout has no credential or server fields", () =>
                {
                    foreach (var property in typeof(SavedLayout).GetProperties())
                        if (property.Name.ToLowerInvariant().Contains("password") || property.Name.ToLowerInvariant().Contains("user") || property.Name.ToLowerInvariant().Contains("server"))
                            throw new Exception("Unexpected identity field.");
                });
                test("DTD in saved layout is rejected", () => Throws(() => LayoutStore.ReadXml("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///does-not-exist'>]><SavedLayout>&e;</SavedLayout>")));
                test("Invalid saved-layout version rejected", () => Throws(() => LayoutStore.ReadXml(LayoutStore.ToXml(new SavedLayout { Version = 99, Width = 1000, Height = 700 }))));
            }
            else
            {
                test("ActiveX creation, interface availability and security setters WITHOUT a network connection", () =>
                {
                    using (var form = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(-16000, -16000, 1000, 750), ShowInTaskbar = false })
                    {
                        var panel = new Panel { Dock = DockStyle.Fill };
                        form.Controls.Add(panel);
                        form.Show();
                        Application.DoEvents();
                        using (var session = new RdpSession(panel))
                        {
                            session.Initialize();
                            var plan = session.CurrentPlan();
                            session.Configure(Endpoint.Parse("rdp-viewer.invalid", 3389), "", false, plan);
                            Equal(true, session.SecureConfigurationVerified());
                            Equal(false, session.Busy);
                            Equal(false, session.LoggedIn);
                            panel.Width = 700;
                            Application.DoEvents();
                        }
                        form.Close();
                    }
                });
            }
            results.Add("\n" + (results.Count - failures) + " passed; " + failures + " failed.");
            results.Add("No live RDP logon, remote resize, certificate infrastructure or corporate policy was tested.");
            string fullPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllLines(fullPath, results);
            return failures == 0 ? 0 : 1;
        }
        private static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual + ".");
        }
        private static void Throws(Action action)
        {
            try { action(); } catch { return; }
            throw new Exception("Expected an exception.");
        }
    }
}
