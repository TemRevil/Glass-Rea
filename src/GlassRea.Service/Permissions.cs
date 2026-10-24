using GlassRea.Core.Alerts;
using GlassRea.Core.Protocol;
using GlassRea.Core.Storage;

namespace GlassRea.Service;

/// <summary>
/// What the person has allowed the service to do (Settings > General > Permissions, and the screen the app shows
/// first). A worker that needs system rights waits here until its feature is on (<see cref="WaitAllowed"/>) and stops
/// as soon as it's turned off (<see cref="WhileAllowed"/>), without the service restarting.
/// </summary>
public sealed class Permissions(Database db, ILogger<Permissions>? log = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Feature, TaskCompletionSource> _waiting = [];
    private readonly Dictionary<Feature, CancellationTokenSource> _allowed = [];

    /// <summary>Raised after a feature is turned on or off, with its new state.</summary>
    public event Action<Feature, bool>? Changed;

    /// <summary>
    /// Once, the first time a 1.2.8 or later service starts: everything off on a new install; on one that already
    /// recorded, everything stays on as before, and the app shows the screen once so the person can review it.
    /// </summary>
    public void Initialize()
    {
        if (PermissionKeys.All.Any(f => db.GetSetting(PermissionKeys.For(f)) is not null)) return;
        var installed = db.GetSetting(SettingKeys.FirstEtwHour) is not null || db.HasUsage();
        foreach (var f in PermissionKeys.All) db.SetSetting(PermissionKeys.For(f), installed ? "true" : "false");
        db.SetSetting(PermissionKeys.Reviewed, "false");
        // Capture has run since its first hour on such a PC, so new-app alerts keep counting from there.
        if (installed && long.TryParse(db.GetSetting(SettingKeys.FirstEtwHour), out var first) && db.GetSetting(SettingKeys.CaptureStartedAt) is null)
            db.SetSetting(SettingKeys.CaptureStartedAt, (first * 3600).ToString());
        log?.LogInformation(installed
            ? "Permissions start on, as this PC already used every feature; the app asks once to review them"
            : "Permissions start off until they're allowed in the app");
    }

    public PermissionSet Current => new(Allowed(Feature.Usage), Allowed(Feature.Blocking), Allowed(Feature.History),
        Allowed(Feature.Devices), db.GetSetting(PermissionKeys.Reviewed) == "true");

    public bool Allowed(Feature f) => db.GetSetting(PermissionKeys.For(f)) == "true";

    public void Set(Feature f, bool on)
    {
        TaskCompletionSource? waiting = null;
        CancellationTokenSource? allowed = null;
        lock (_gate)
        {
            if (Allowed(f) == on) return;
            db.SetSetting(PermissionKeys.For(f), on ? "true" : "false");
            if (on) _waiting.Remove(f, out waiting);
            else _allowed.Remove(f, out allowed);
        }
        // Outside the lock: what runs on these may ask again.
        waiting?.TrySetResult();
        allowed?.Cancel();
        log?.LogInformation("{Feature} is {State}", PermissionText.Title(f), on ? "on" : "off");
        Changed?.Invoke(f, on);
    }

    /// <summary>
    /// The app's "permissions.set": each <see cref="PermissionKeys.For"/> key to "true" or "false", and
    /// <see cref="PermissionKeys.Reviewed"/> to "true". Checked whole before anything changes.
    /// </summary>
    public void Apply(IReadOnlyDictionary<string, string> changes)
    {
        var features = new List<(Feature, bool)>();
        var reviewed = false;
        foreach (var (key, value) in changes)
        {
            if (value is not ("true" or "false")) throw new InvalidOperationException($"{key} must be true or false.");
            if (key == PermissionKeys.Reviewed) { reviewed = value == "true"; continue; }
            var f = PermissionKeys.All.Where(f => PermissionKeys.For(f) == key).Select(f => (Feature?)f).FirstOrDefault()
                ?? throw new InvalidOperationException($"Unknown permission: {key}");
            features.Add((f, value == "true"));
        }
        foreach (var (f, on) in features) Set(f, on);
        if (reviewed) MarkReviewed();
    }

    public void MarkReviewed() => db.SetSetting(PermissionKeys.Reviewed, "true");

    /// <summary>Completes once <paramref name="f"/> is allowed (at once if it already is).</summary>
    public Task WaitAllowed(Feature f, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Allowed(f)) return Task.CompletedTask;
            if (!_waiting.TryGetValue(f, out var tcs))
                _waiting[f] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return tcs.Task.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> while <paramref name="f"/> is allowed: it starts once the feature is turned on, its
    /// token is cancelled when it's turned off, and it starts again when it's turned back on. Ends when the work
    /// finishes on its own or the service stops.
    /// </summary>
    public async Task WhileOn(Feature f, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await WaitAllowed(f, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, WhileAllowed(f));
            try { await work(run.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (run.IsCancellationRequested) { }
            if (!run.IsCancellationRequested) return;
        }
    }

    /// <summary>Cancelled when <paramref name="f"/> is turned off; already cancelled if it's off now.</summary>
    public CancellationToken WhileAllowed(Feature f)
    {
        lock (_gate)
        {
            if (!Allowed(f)) return new CancellationToken(canceled: true);
            if (!_allowed.TryGetValue(f, out var cts)) _allowed[f] = cts = new CancellationTokenSource();
            return cts.Token;
        }
    }
}
