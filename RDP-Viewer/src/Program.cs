using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("RDP Viewer")]
[assembly: AssemblyDescription("Single-window RDP viewer with explicit dynamic-resolution feedback")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

namespace RdpViewer
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && (args[0] == "--self-test" || args[0] == "--smoke-test"))
                return SelfTests.Run(args[1], args[0] == "--smoke-test");
            if (args.Length != 0)
            {
                MessageBox.Show("Launch without arguments, or use --self-test REPORT_PATH / --smoke-test REPORT_PATH.", "RDP Viewer");
                return 2;
            }
            try
            {
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("RDP Viewer could not start. Keep the two interop DLLs beside the executable. " +
                    "Windows x64, .NET Framework 4.8 and Microsoft's installed RDP ActiveX control are required.\r\n\r\n" +
                    ex.Message, "RDP Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
