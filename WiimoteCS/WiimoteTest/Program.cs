using System;
using System.Windows.Forms;

namespace WiimoteTest
{
	static class Program
	{
		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main()
		{
			if (!OperatingSystem.IsWindows() || !OperatingSystem.IsWindowsVersionAtLeast(6, 1))
			{
				throw new Exception("At least Windows 6.1 required");
			}
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			Application.Run(new MultipleWiimoteForm());
		}
	}
}