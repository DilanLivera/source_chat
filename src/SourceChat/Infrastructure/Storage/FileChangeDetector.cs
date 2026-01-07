using System.Security.Cryptography;
using System.Text.Json;
using SourceChat.Features.Shared;

namespace SourceChat.Infrastructure.Storage;

internal class FileChangeDetector
{
    private readonly string _trackingFilePath;
    private readonly Dictionary<string, FileTrackingInfo> _fileTracking;

    public FileChangeDetector(string dbPath)
    {
        _trackingFilePath = Path.ChangeExtension(dbPath, ".tracking.json");
        _fileTracking = LoadTracking();
    }

    public bool HasFileChanged(string filePath, DateTime lastModified)
    {
        string absolutePath = Path.GetFullPath(filePath);

        if (!_fileTracking.TryGetValue(absolutePath, out FileTrackingInfo? tracking))
        {
            return true; // New file
        }

        return tracking.LastModified != lastModified;
    }

    public async Task<string> GetFileHashAsync(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream);

        return Convert.ToHexString(hash);
    }

    public void UpdateFileTracking(string filePath, DateTime lastModified, string hash)
    {
        string absolutePath = Path.GetFullPath(filePath);
        _fileTracking[absolutePath] = new FileTrackingInfo
        {
            LastModified = lastModified,
            Hash = hash,
            LastProcessed = DateTime.UtcNow
        };
    }

    public void RemoveFileTracking(string filePath)
    {
        string absolutePath = Path.GetFullPath(filePath);
        _fileTracking.Remove(absolutePath);
    }

    public List<string> GetTrackedFiles()
    {
        return _fileTracking.Keys.ToList();
    }

    public void SaveTracking()
    {
        string json = JsonSerializer.Serialize(_fileTracking,
                                               new JsonSerializerOptions
                                               {
                                                   WriteIndented = true
                                               });
        File.WriteAllText(_trackingFilePath, json);
    }

    private Dictionary<string, FileTrackingInfo> LoadTracking()
    {
        if (!File.Exists(_trackingFilePath))
        {
            return new Dictionary<string, FileTrackingInfo>();
        }

        try
        {
            string json = File.ReadAllText(_trackingFilePath);

            return JsonSerializer.Deserialize<Dictionary<string, FileTrackingInfo>>(json) ?? new Dictionary<string, FileTrackingInfo>();
        }
        catch
        {
            return new Dictionary<string, FileTrackingInfo>();
        }
    }

    public void ClearTracking()
    {
        _fileTracking.Clear();
        if (File.Exists(_trackingFilePath))
        {
            File.Delete(_trackingFilePath);
        }
    }
}