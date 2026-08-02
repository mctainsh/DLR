using Android.App;
using Android.Content;
using Android.Content.PM;

namespace BlazorDLR;

// .gpx share-sheet + "Open with" integration (SharedFrontend.md §7 Phase 1). Three
// filters so the app appears both when a file manager opens a .gpx and when another
// app shares one:
//   1. VIEW on content:/file: URIs whose mime type is application/gpx+xml — what
//      browsers and cloud clients declare when they know the type.
//   2. VIEW on any URI whose path ends in .gpx — catches sources that hand the file
//      out as application/octet-stream without a specific mime.
//   3. SEND on application/gpx+xml — the "Share" sheet variant.
// The DEFAULT + BROWSABLE categories are needed so the system offers this app as a
// handler; MAUI's generated activity class name is what the manifest's <activity>
// block would have to reference, which is why the filters live here instead.
[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
	new[] { Intent.ActionView },
	Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
	DataSchemes = new[] { "content", "file" },
	DataMimeType = "application/gpx+xml")]
[IntentFilter(
	new[] { Intent.ActionView },
	Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
	DataSchemes = new[] { "content", "file" },
	DataMimeType = "*/*",
	DataPathPattern = ".*\\.gpx")]
[IntentFilter(
	new[] { Intent.ActionSend },
	Categories = new[] { Intent.CategoryDefault },
	DataMimeType = "application/gpx+xml")]
public class MainActivity : MauiAppCompatActivity
{
}
