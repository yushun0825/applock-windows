using System.IO;
using System.Text.Json;

namespace AppLock.Platform;

/// <summary>
/// 止めたプロセスと隠した窓の記録。アプリロックが途中で終わっても(強制終了など)、
/// 次に起動したときに、止めたままのアプリを放っておかないようにする。
/// </summary>
internal sealed class FrozenJournal(string directory)
{
    public sealed record ProcessEntry(uint Pid, long CreateTime, string AppId);

    public sealed record WindowEntry(long Hwnd, uint Pid, long CreateTime, string AppId);

    public sealed class Data
    {
        public List<ProcessEntry> Frozen { get; set; } = [];
        public List<WindowEntry> Hidden { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private string FilePath => Path.Combine(directory, "frozen.json");

    public Data? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Options) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(Data data, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(directory);
            if (data.Frozen.Count == 0 && data.Hidden.Count == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            log($"止めた記録を書けない: {e.Message}");
        }
    }
}
