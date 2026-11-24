using System.Runtime.InteropServices;
using System.Text;
using RTXLens.Presentation.Settings;

namespace RTXLens.WarningUI;

public static class WarningSoundPlayer
{
    // All paths passed to MCI are local files with application-controlled names.
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint mciSendStringW(string command, StringBuilder? result, int length, nint callback);

    public static void Stop() => mciSendStringW("close lensWarning", null, 0, 0);

    public static void Play(AppPreferences prefs, string directory, bool loop)
    {
        Stop();
        var stock = Path.Combine(AppContext.BaseDirectory, "Sounds");
        var path = prefs.WarningSound == "Custom" ? Path.Combine(directory, "warning-custom.wav")
            : Path.Combine(stock, prefs.WarningSound + ".mp3");
        if (!Open(path) && !Open(Path.Combine(stock, "Warning1.mp3")))
        {
            throw new IOException("Windows could not open the warning sound.");
        }

        if (mciSendStringW($"setaudio lensWarning volume to {prefs.WarningSoundVolume * 10}", null, 0, 0) != 0
            || mciSendStringW(loop ? "play lensWarning from 0 repeat" : "play lensWarning from 0", null, 0, 0) != 0)
        {
            Stop();
            throw new IOException("Windows could not play the warning sound.");
        }
    }

    private static bool Open(string path) => File.Exists(path)
        && mciSendStringW($"open \"{path}\" type mpegvideo alias lensWarning", null, 0, 0) == 0;
}
