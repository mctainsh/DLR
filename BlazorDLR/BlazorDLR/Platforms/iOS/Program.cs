using BlazorDLR.Shared.Diagnostics;
using UIKit;

namespace BlazorDLR;

public class Program
{
	// This is the main entry point of the application.
	static void Main(string[] args)
	{
		// The first managed statement in the process — before MAUI, before AppDelegate, before
		// anything that could fail and take the logging with it. The ring is in memory and needs
		// no setup, so this lands whether or not the file sink is wired up yet; MauiProgram points
		// that at a path a moment later and the line above it in the file is this one.
		DiagnosticLog.Write("===== DLR starting (iOS) =====");

		// if you want to use a different Application Delegate class from "AppDelegate"
		// you can specify it here.
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
