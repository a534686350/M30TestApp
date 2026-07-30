using System;
using System.Windows.Forms;

namespace F40MultiCalibrator;

internal static class Program
{
	[STAThread]
	private static void Main()
	{
		ApplicationConfiguration.Initialize();
		Application.Run(new MainForm());
	}
}
