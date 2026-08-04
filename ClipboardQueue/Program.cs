using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ClipboardQueue;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, @"Local\ClipboardQueue_SingleInstance", out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "Clipboard Queue is already running.",
                "Clipboard Queue",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        bool startHidden = args.Any(a =>
            string.Equals(a, "--hidden", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(startHidden));
    }
}
