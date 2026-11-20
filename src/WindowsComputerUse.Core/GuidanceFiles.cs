using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsComputerUse.Core;

public static class GuidanceFiles
{
    public static string Child(string directory, string id)
    {
        if (!Regex.IsMatch(id ?? "", "^[a-zA-Z0-9_-]{1,80}$"))
            throw new DesktopException("invalid_argument", "Invalid guidance identifier.");
        return Path.Combine(directory, id!);
    }

    public static void Write<T>(string path, T value) => WriteText(path, JsonSerializer.Serialize(value, Wire.Json));
    public static void WriteText(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, value, new UTF8Encoding(false)); File.Move(temp, path, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Wire.Json)
        ?? throw new DesktopException("guidance_corrupt", "Guidance data is empty.");

    public static GuidanceFrame Snapshot(string directory, string id, Func<Observation> inspect,
        Func<(CaptureInfo Info, string Image)> capture)
    {
        Observation? observation = null; CaptureInfo? info = null; string? imageFile = null;
        List<string> warnings = [];
        try { observation = inspect(); } catch (Exception ex) { warnings.Add("UIA: " + ex.Message); }
        try
        {
            var result = capture(); info = result.Info; imageFile = id + ".png";
            Directory.CreateDirectory(Path.Combine(directory, "frames"));
            File.WriteAllBytes(Path.Combine(directory, "frames", imageFile), Convert.FromBase64String(result.Image));
        }
        catch (Exception ex) { warnings.Add("Capture: " + ex.Message); }
        var frame = new GuidanceFrame(id, DateTimeOffset.UtcNow, observation, info, imageFile, warnings.ToArray());
        Write(Path.Combine(directory, "frames", id + ".json"), frame);
        return frame;
    }
}
