using System.Text.Json;
using ContextFlow.Infrastructure;

namespace ContextFlow.Services;

/// <summary>
/// On-device learning layer for the optimizer. It learns, per application:
/// <list type="bullet">
/// <item><b>Affinity</b> — how much the user works with the app (decayed, half-life 3 days).</item>
/// <item><b>Daily rhythm</b> — a 24-hour usage histogram, so apps the user usually opens at
/// this time of day are left alone.</item>
/// <item><b>Regret</b> — the app was optimized and the user came back to it within five
/// minutes. Regret raises that app's protection; optimizations that went
/// unnoticed for ten minutes count as successes.</item>
/// </list>
/// Everything is stored in %LOCALAPPDATA%\ContextFlow\ai-profile.json and never leaves the PC.
/// </summary>
public sealed class AdaptiveUsageModel
{
    private static readonly TimeSpan AffinityHalfLife = TimeSpan.FromDays(3);
    private static readonly TimeSpan RegretWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SuccessWindow = TimeSpan.FromMinutes(10);

    private readonly string _path;
    private readonly AppLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, AppProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    private string? _foreground;
    private DateTime _foregroundSinceUtc = DateTime.UtcNow;
    private DateTime _lastSaveUtc = DateTime.UtcNow;
    private bool _dirty;

    public AdaptiveUsageModel(string path, AppLog log)
    {
        _path = path;
        _log = log;
        Load();
    }

    /// <summary>Raised when the user returns to an app shortly after it was optimized.</summary>
    public event Action<string>? RegretDetected;

    public int LearnedApps
    {
        get
        {
            lock (_gate)
            {
                return _profiles.Count;
            }
        }
    }

    /// <summary>Share of optimizations the user did not have to undo (0..1).</summary>
    public double Accuracy
    {
        get
        {
            lock (_gate)
            {
                var success = _profiles.Values.Sum(p => p.Success);
                var regret = _profiles.Values.Sum(p => p.Regret);
                return success + regret < 0.5 ? 1 : success / (success + regret);
            }
        }
    }

    /// <summary>Call whenever the foreground application may have changed.</summary>
    public void ObserveForeground(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        var now = DateTime.UtcNow;
        string? regretApp = null;
        lock (_gate)
        {
            if (string.Equals(_foreground, processName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CreditDwellLocked(now);
            _foreground = processName;
            _foregroundSinceUtc = now;

            var profile = GetOrCreateLocked(processName);
            Decay(profile, now);
            profile.Affinity += 1;
            profile.Hourly[DateTime.Now.Hour] += 1;
            profile.LastUsedUtc = now;
            _dirty = true;

            if (_pending.Remove(processName, out var optimizedAt) && now - optimizedAt <= RegretWindow)
            {
                profile.Regret += 1;
                regretApp = processName;
            }
        }

        if (regretApp is not null)
        {
            _log.Info($"AI learning: user returned to {regretApp} shortly after it was optimized; protection raised.");
            RegretDetected?.Invoke(regretApp);
        }
    }

    /// <summary>Called once per optimizer pass: credits dwell time and settles pending outcomes.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            CreditDwellLocked(now);
            foreach (var settled in _pending.Where(p => now - p.Value >= SuccessWindow).ToList())
            {
                _pending.Remove(settled.Key);
                GetOrCreateLocked(settled.Key).Success += 1;
                _dirty = true;
            }

            if (_dirty && now - _lastSaveUtc >= TimeSpan.FromMinutes(2))
            {
                SaveLocked();
            }
        }
    }

    public void RecordOptimization(string processName)
    {
        lock (_gate)
        {
            _pending[processName] = DateTime.UtcNow;
            GetOrCreateLocked(processName).Optimizations += 1;
            _dirty = true;
        }
    }

    /// <summary>Learned signals for the policy model, all normalized to 0..1.</summary>
    public LearnedSignals GetSignals(string processName)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(processName, out var profile))
            {
                return new LearnedSignals(0, 0, 0);
            }

            Decay(profile, DateTime.UtcNow);
            var affinity = 1 - Math.Exp(-profile.Affinity / 8d);

            // Beta prior (1 success, 1 regret) keeps a single event from dominating.
            var regret = (profile.Regret + 0.25) / (profile.Regret + profile.Success + 2);

            var hour = DateTime.Now.Hour;
            var max = profile.Hourly.Max();
            double predicted = 0;
            if (max > 0)
            {
                var local = profile.Hourly[hour] + 0.5 * (profile.Hourly[(hour + 23) % 24] + profile.Hourly[(hour + 1) % 24]);
                var confidence = Math.Min(1, profile.Hourly.Sum() / 20d);
                predicted = Math.Clamp(local / (max * 2), 0, 1) * confidence;
            }

            return new LearnedSignals(affinity, regret, predicted);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _profiles.Clear();
            _pending.Clear();
            _dirty = true;
            SaveLocked();
        }

        _log.Info("AI learning profile was reset by the user.");
    }

    public void Save()
    {
        lock (_gate)
        {
            if (_dirty)
            {
                SaveLocked();
            }
        }
    }

    private void CreditDwellLocked(DateTime now)
    {
        if (_foreground is null)
        {
            return;
        }

        // Each 10 minutes of focused use counts as one extra activation.
        var minutes = (now - _foregroundSinceUtc).TotalMinutes;
        if (minutes <= 0)
        {
            return;
        }

        var profile = GetOrCreateLocked(_foreground);
        profile.Affinity += minutes / 10d;
        profile.Hourly[DateTime.Now.Hour] += minutes / 10d;
        _foregroundSinceUtc = now;
        _dirty = true;
    }

    private static void Decay(AppProfile profile, DateTime now)
    {
        var elapsed = now - profile.DecayedAtUtc;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var factor = Math.Pow(0.5, elapsed.TotalSeconds / AffinityHalfLife.TotalSeconds);
        profile.Affinity *= factor;
        // Outcomes and rhythm fade much more slowly (~1 month) so they stay meaningful.
        var slow = Math.Pow(0.5, elapsed.TotalDays / 30d);
        profile.Regret *= slow;
        profile.Success *= slow;
        for (var i = 0; i < profile.Hourly.Length; i++)
        {
            profile.Hourly[i] *= slow;
        }

        profile.DecayedAtUtc = now;
    }

    private AppProfile GetOrCreateLocked(string name)
    {
        if (!_profiles.TryGetValue(name, out var profile))
        {
            profile = new AppProfile { DecayedAtUtc = DateTime.UtcNow };
            _profiles[name] = profile;
        }

        return profile;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, AppProfile>>(File.ReadAllText(_path));
            if (data is null)
            {
                return;
            }

            foreach (var (name, profile) in data)
            {
                if (profile.Hourly is not { Length: 24 })
                {
                    profile.Hourly = new double[24];
                }

                _profiles[name] = profile;
            }

            _log.Info($"AI learning profile loaded: {_profiles.Count} app(s).");
        }
        catch (Exception ex)
        {
            _log.Warn($"AI learning profile could not be read; starting fresh. {ex.Message}");
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_profiles));
            File.Move(temp, _path, overwrite: true);
            _dirty = false;
            _lastSaveUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _log.Warn($"AI learning profile could not be saved: {ex.Message}");
        }
    }

    public sealed class AppProfile
    {
        public double Affinity { get; set; }
        public double[] Hourly { get; set; } = new double[24];
        public double Regret { get; set; }
        public double Success { get; set; }
        public int Optimizations { get; set; }
        public DateTime LastUsedUtc { get; set; }
        public DateTime DecayedAtUtc { get; set; }
    }
}

public readonly record struct LearnedSignals(double Affinity, double RegretRate, double PredictedUseSoon);
