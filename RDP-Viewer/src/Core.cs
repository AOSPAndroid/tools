using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

namespace RdpViewer
{
    internal sealed class Endpoint
    {
        public string Host { get; private set; }
        public int Port { get; private set; }
        public static Endpoint Parse(string text, int port)
        {
            string host = (text ?? "").Trim();
            if (host.StartsWith("[") && host.EndsWith("]")) host = host.Substring(1, host.Length - 2);
            if (port < 1 || port > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
            IPAddress address;
            bool ip = IPAddress.TryParse(host, out address);
            bool dns = host.Length > 0 && host.Length <= 253 && Regex.IsMatch(host,
                @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.?\z");
            if (!ip && !dns)
                throw new ArgumentException("Enter a host name or IP address only, not a URL or an .rdp file. Use the separate Port field.");
            return new Endpoint { Host = host, Port = port };
        }
    }

    internal sealed class DisplayPlan
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public uint Scale { get; private set; }
        public Size Size { get { return new Size(Width, Height); } }
        public static DisplayPlan Create(int width, int height, int dpi)
        {
            // MS-RDPEDISP requires 200..8192 pixels and an even width.
            if (width < 200 || height < 200 || width > 8192 || height > 8192)
                throw new ArgumentException("The remote view must be between 200 and 8192 pixels in each dimension.");
            return new DisplayPlan
            {
                Width = width - width % 2,
                Height = height,
                Scale = (uint)Math.Max(100, Math.Min(500, Math.Round(Math.Max(96, dpi) * 100.0 / 96.0)))
            };
        }
        public bool SameAs(DisplayPlan other)
        {
            return other != null && Width == other.Width && Height == other.Height && Scale == other.Scale;
        }
    }

    internal enum Placement { BottomHalf, TopHalf, FullWorkArea }

    internal static class LayoutMath
    {
        public static Rectangle Zone(Rectangle bounds, Rectangle work, Placement placement)
        {
            Rectangle area = Rectangle.Intersect(bounds, work);
            int middle = bounds.Top + bounds.Height / 2;
            if (placement == Placement.BottomHalf)
                area = Rectangle.FromLTRB(area.Left, Math.Max(area.Top, middle), area.Right, area.Bottom);
            else if (placement == Placement.TopHalf)
                area = Rectangle.FromLTRB(area.Left, area.Top, area.Right, Math.Min(area.Bottom, middle));
            if (area.Width < 1 || area.Height < 1) throw new ArgumentException("No usable space in that monitor zone.");
            return area;
        }
        public static Rectangle Clamp(Rectangle value, Rectangle work, Size minimum)
        {
            int width = Math.Min(work.Width, Math.Max(minimum.Width, value.Width));
            int height = Math.Min(work.Height, Math.Max(minimum.Height, value.Height));
            int left = Math.Max(work.Left, Math.Min(value.Left, work.Right - width));
            int top = Math.Max(work.Top, Math.Min(value.Top, work.Bottom - height));
            return new Rectangle(left, top, width, height);
        }
    }

    // Only layout is serialized. No server names, usernames, tokens or passwords.
    public sealed class SavedLayout
    {
        public int Version { get; set; }
        public string Monitor { get; set; }
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal static class LayoutStore
    {
        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDP-Viewer", "layout.xml"); }
        }
        public static SavedLayout ReadXml(string xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8192 };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
            {
                var value = (SavedLayout)new XmlSerializer(typeof(SavedLayout)).Deserialize(reader);
                if (value == null || value.Version != 1 || value.Width < 1 || value.Height < 1 ||
                    value.Width > 32768 || value.Height > 32768 || Math.Abs((long)value.OffsetX) > 131072 ||
                    Math.Abs((long)value.OffsetY) > 131072 || (value.Monitor ?? "").Length > 256)
                    throw new InvalidDataException("Invalid saved window layout.");
                return value;
            }
        }
        public static string ToXml(SavedLayout value)
        {
            using (var writer = new StringWriter())
            {
                new XmlSerializer(typeof(SavedLayout)).Serialize(writer, value);
                return writer.ToString();
            }
        }
        public static SavedLayout Load()
        {
            if (!File.Exists(FilePath)) return null;
            if (new FileInfo(FilePath).Length > 16384) throw new InvalidDataException("Saved layout is too large.");
            return ReadXml(File.ReadAllText(FilePath));
        }
        public static void Save(SavedLayout value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, ToXml(value), System.Text.Encoding.Unicode);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static void Delete() { if (File.Exists(FilePath)) File.Delete(FilePath); }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);
        internal static Size ClientPixels(IntPtr hwnd)
        {
            Rect r;
            if (!GetClientRect(hwnd, out r)) throw new System.ComponentModel.Win32Exception();
            return new Size(r.Right - r.Left, r.Bottom - r.Top);
        }
    }
}
