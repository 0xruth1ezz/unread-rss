using System.Text.Json;

namespace Unread.Core;

public sealed class LibraryStore(string directory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    public Task<LibrarySnapshot?> LoadAsync() => ReadAsync<LibrarySnapshot>("library.json");
    public Task SaveAsync(LibrarySnapshot library) => WriteAsync("library.json", library);
    public async Task<ReaderSettings> LoadSettingsAsync() => await ReadAsync<ReaderSettings>("settings.json") ?? new();
    public Task SaveSettingsAsync(ReaderSettings settings) => WriteAsync("settings.json", settings);

    private async Task<T?> ReadAsync<T>(string file)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options);
        }
        catch (JsonException)
        {
            // Keep the damaged file for recovery instead of destroying the only copy.
            File.Copy(Path.Combine(directory, file), Path.Combine(directory, file + ".corrupt"), true);
            return default;
        }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync<T>(string file, T value)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, file);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Options));
            File.Move(temporary, path, true);
        }
        finally { _gate.Release(); }
    }
}
