using System.Text.Json;
using System.Text.Json.Serialization;
using ContextFlow.Infrastructure;
using ContextFlow.Models;

namespace ContextFlow.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly AppPaths _paths;
    private readonly AppLog _log;

    public SettingsService(AppPaths paths, AppLog log)
    {
        _paths = paths;
        _log = log;
    }

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_paths.SettingsFile))
                {
                    var json = File.ReadAllText(_paths.SettingsFile);
                    Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                    _log.Info($"Settings loaded from {_paths.SettingsFile}.");
                }
                else
                {
                    Current = new AppSettings();
                    _log.Info("No settings file found; defaults created.");
                }
            }
            catch (Exception ex)
            {
                _log.Error("Could not load settings; attempting backup/defaults.", ex);
                Current = TryLoadBackup() ?? new AppSettings();
            }

            Current.Sanitize();
            return Current;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                _paths.EnsureCreated();
                var tempFile = _paths.SettingsFile + ".tmp";
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(tempFile, json);

                if (File.Exists(_paths.SettingsFile))
                {
                    try
                    {
                        File.Copy(_paths.SettingsFile, _paths.SettingsBackupFile, overwrite: true);
                    }
                    catch
                    {
                        // Backup is best-effort only.
                    }
                }

                File.Move(tempFile, _paths.SettingsFile, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to persist settings.", ex);
            }
        }
    }

    private AppSettings? TryLoadBackup()
    {
        try
        {
            if (!File.Exists(_paths.SettingsBackupFile))
            {
                return null;
            }

            var json = File.ReadAllText(_paths.SettingsBackupFile);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
