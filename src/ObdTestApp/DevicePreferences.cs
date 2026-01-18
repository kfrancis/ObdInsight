using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ObdTestApp;

/// <summary>
/// Simple persistence helper for favorite and recently used BLE devices.
/// </summary>
internal sealed class DevicePreferences
{
    private const string PreferencesFileName = "ble-device-preferences.json";
    private const int MaxSavedDevices = 8;
    private static readonly string[] BuiltInFavoriteNames = new[] { "VEEPEAK" };

    private readonly string _storagePath;
    private readonly HashSet<string> _favoriteAddresses;
    private readonly HashSet<string> _savedAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _savedOrder = new();

    private DevicePreferences(string storagePath, HashSet<string> favoriteAddresses, IEnumerable<string> saved)
    {
        _storagePath = storagePath;
        _favoriteAddresses = favoriteAddresses;

        foreach (var address in saved)
        {
            if (_savedAddresses.Add(address))
                _savedOrder.AddLast(address);
        }

        TrimSaved();
    }

    public static DevicePreferences Load()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ObdInsight");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, PreferencesFileName);

        if (!File.Exists(path))
        {
            return new DevicePreferences(
                path,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<string>());
        }

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<DevicePreferencesModel>(json);
            return new DevicePreferences(
                path,
                new HashSet<string>(dto?.Favorites ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                dto?.Saved ?? Array.Empty<string>());
        }
        catch
        {
            // Fall back to an empty preferences file on corruption.
            return new DevicePreferences(
                path,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<string>());
        }
    }

    public bool IsFavorite(BleDeviceInfo device)
    {
        if (BuiltInFavoriteNames.Any(name =>
                device.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return _favoriteAddresses.Contains(device.Address);
    }

    public bool IsSaved(BleDeviceInfo device) => _savedAddresses.Contains(device.Address);

    public BleDeviceInfo? GetPreferredDevice(IEnumerable<BleDeviceInfo> devices) =>
        devices.Where(IsFavorite)
               .OrderByDescending(d => d.Rssi)
               .FirstOrDefault();

    /// <summary>
    /// Gets the most recently used favorite device without requiring a scan.
    /// Returns null if no favorite exists.
    /// </summary>
    public BleDeviceInfo? GetFavoriteDevice()
    {
        // Check if there's a favorite address saved
        var favoriteAddress = _favoriteAddresses.FirstOrDefault() ?? 
                             _savedOrder.FirstOrDefault(addr => _favoriteAddresses.Contains(addr));
        
        if (string.IsNullOrEmpty(favoriteAddress))
            return null;
        
        // Return a minimal device info with just the address
        // RSSI will be 0 since we haven't scanned, but that's OK for auto-connect
        return new BleDeviceInfo(
            "Favorite Device", // Placeholder name, will be updated on connect
            favoriteAddress,
            0, // RSSI unknown without scan
            Array.Empty<Guid>()); // No advertised services without scan
    }

    public void RememberDevice(BleDeviceInfo device, bool markAsFavorite)
    {
        ArgumentNullException.ThrowIfNull(device);

        AddSaved(device.Address);
        if (markAsFavorite)
        {
            _favoriteAddresses.Add(device.Address);
        }

        Save();
    }

    private void AddSaved(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;

        if (_savedAddresses.Add(address))
        {
            _savedOrder.AddFirst(address);
            TrimSaved();
        }
        else
        {
            var node = _savedOrder.Find(address);
            if (node != null)
            {
                _savedOrder.Remove(node);
                _savedOrder.AddFirst(node);
            }
        }
    }

    private void TrimSaved()
    {
        while (_savedOrder.Count > MaxSavedDevices)
        {
            var last = _savedOrder.Last;
            if (last == null)
                break;

            _savedAddresses.Remove(last.Value);
            _savedOrder.RemoveLast();
        }
    }

    private void Save()
    {
        var dto = new DevicePreferencesModel(
            _favoriteAddresses.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToArray(),
            _savedOrder.ToArray());

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_storagePath, json);
    }

    private sealed record DevicePreferencesModel(string[] Favorites, string[] Saved);
}
