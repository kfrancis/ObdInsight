using System.Text.Json;

namespace ObdInsight.DevTools;

/// <summary>
/// Manages a history of previously connected BLE devices for quick reconnection.
/// Stores favorites in a JSON file in the user's app data folder.
/// </summary>
public class DeviceHistory
{
    private const string FileName = "obd-devtools-devices.json";
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ObdInsight",
        FileName);

    private List<SavedDevice> _devices = [];

    /// <summary>
    /// All saved devices, ordered by most recently used.
    /// </summary>
    public IReadOnlyList<SavedDevice> Devices => _devices.AsReadOnly();

    /// <summary>
    /// Load device history from disk.
    /// </summary>
    public static DeviceHistory Load()
    {
        var history = new DeviceHistory();

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var devices = JsonSerializer.Deserialize<List<SavedDevice>>(json);
                if (devices != null)
                {
                    history._devices = devices
                        .OrderByDescending(d => d.LastUsed)
                        .ToList();
                }
            }
        }
        catch
        {
            // Ignore errors loading history
        }

        return history;
    }

    /// <summary>
    /// Save the current device history to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_devices, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Ignore errors saving history
        }
    }

    /// <summary>
    /// Add or update a device in the history.
    /// </summary>
    public void AddOrUpdate(string address, string? name, string? profileName = null)
    {
        var normalized = NormalizeAddress(address);
        var existing = _devices.FirstOrDefault(d => 
            NormalizeAddress(d.Address) == normalized);

        if (existing != null)
        {
            existing.LastUsed = DateTime.UtcNow;
            existing.UseCount++;
            if (!string.IsNullOrEmpty(name))
                existing.Name = name;
            if (!string.IsNullOrEmpty(profileName))
                existing.ProfileName = profileName;
        }
        else
        {
            _devices.Add(new SavedDevice
            {
                Address = address,
                Name = name ?? address,
                ProfileName = profileName,
                LastUsed = DateTime.UtcNow,
                UseCount = 1
            });
        }

        // Keep only the most recent 10 devices
        _devices = _devices
            .OrderByDescending(d => d.LastUsed)
            .Take(10)
            .ToList();

        Save();
    }

    /// <summary>
    /// Remove a device from history.
    /// </summary>
    public void Remove(string address)
    {
        var normalized = NormalizeAddress(address);
        _devices.RemoveAll(d => NormalizeAddress(d.Address) == normalized);
        Save();
    }

    /// <summary>
    /// Mark a device as favorite (moves it to top).
    /// </summary>
    public void SetFavorite(string address, bool isFavorite)
    {
        var normalized = NormalizeAddress(address);
        var device = _devices.FirstOrDefault(d => 
            NormalizeAddress(d.Address) == normalized);

        if (device != null)
        {
            device.IsFavorite = isFavorite;
            Save();
        }
    }

    /// <summary>
    /// Get favorite devices first, then recent devices.
    /// </summary>
    public IEnumerable<SavedDevice> GetOrderedDevices()
    {
        return _devices
            .OrderByDescending(d => d.IsFavorite)
            .ThenByDescending(d => d.LastUsed);
    }

    private static string NormalizeAddress(string address)
    {
        return address.Replace(":", "").Replace("-", "").ToUpperInvariant();
    }
}

/// <summary>
/// Represents a saved BLE device.
/// </summary>
public class SavedDevice
{
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ProfileName { get; set; }
    public DateTime LastUsed { get; set; }
    public int UseCount { get; set; }
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Get a display string for this device.
    /// </summary>
    public string GetDisplayName()
    {
        var star = IsFavorite ? "? " : "";
        var name = !string.IsNullOrEmpty(Name) && Name != Address ? Name : "Unknown";
        return $"{star}{name} ({Address})";
    }
}
