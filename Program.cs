using System;
using System.Windows.Forms;

namespace PalmRejectorPro
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new OverlayForm());
        }
    }
}