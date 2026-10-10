using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParquetExplorer.Services;

/// <summary>Small JSON document persisted atomically (write to temp file, then rename).</summary>
public sealed class JsonFileStore<T>(string path) where T : new()
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _lock = new();

    public string Path { get; } = path;

    public T Load()
    {
        lock (_lock)
        {
            if (!File.Exists(Path)) return new T();
            using var stream = File.OpenRead(Path);
            return JsonSerializer.Deserialize<T>(stream, Json) ?? new T();
        }
    }

    public void Save(T value)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, Path, overwrite: true);
        }
    }
}
