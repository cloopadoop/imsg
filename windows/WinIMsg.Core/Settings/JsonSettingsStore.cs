using System.Text.Json;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;

namespace WinIMsg.Core.Settings;

public sealed class JsonSettingsStore
{
    private readonly string _path;

    public JsonSettingsStore(string path)
    {
        _path = path;
    }

    public WinIMsgSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new WinIMsgSettings().Normalize();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<WinIMsgSettings>(json, ImsgJson.Options);
            return (settings ?? new WinIMsgSettings()).Normalize();
        }
        catch
        {
            return new WinIMsgSettings().Normalize();
        }
    }

    public void Save(WinIMsgSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings.Normalize(), ImsgJson.Options));
    }
}
