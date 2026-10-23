using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetRoute.Core.Adapters;
using NetRoute.Core.Policy;
using NetRoute.Core.Traffic;
using NetRoute.Ipc;
using NetRoute.Windows.Apps;

namespace NetRoute.App;

/// <summary>Look-ups shared by the view models: role icons, colours and names.</summary>
public static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static string Glyph(RoleId role) => role switch
    {
        RoleId.Gaming => "",     // game controller
        RoleId.Downloads => "",  // download
        _ => ""                  // globe
    };

    public static Brush RoleBrush(RoleId role) => Res(role switch
    {
        RoleId.Gaming => "GamingBrush",
        RoleId.Downloads => "DownloadsBrush",
        _ => "DefaultBrush"
    });

    public static Brush RoleTint(RoleId role) => Res(role switch
    {
        RoleId.Gaming => "GamingTintBrush",
        RoleId.Downloads => "DownloadsTintBrush",
        _ => "DefaultTintBrush"
    });
}

/// <summary>The whole window: which page is showing, the two network cards, and the app list.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly INetRouteClient _client;
    private bool _refreshing;
    private long _lastEventId = -1;

    public MainViewModel(INetRouteClient client)
    {
        _client = client;
        Setup = new SetupViewModel(this);
        Apps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoApps));
    }

    /// <summary>Raised for events worth a tray notification (network lost or restored, leaks).</summary>
    public event Action<string, string>? Notify;

    [ObservableProperty] private string _page = "Loading";
    [ObservableProperty] private string _overallText = "Connecting";
    [ObservableProperty] private Brush _overallBrush = Ui.Res("MutedBrush");
    [ObservableProperty] private Brush _overallTint = Ui.Res("NeutralTintBrush");
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private string? _routeTieText;
    [ObservableProperty] private string? _updateText;
    [ObservableProperty] private string _updateAction = "Get it";
    [ObservableProperty] private double _updateFraction;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateActionEnabled))]
    private bool _updateBusy;

    /// <summary>The banner's button, greyed out only while the service is actually fetching.</summary>
    public bool UpdateActionEnabled => !UpdateBusy;
    [ObservableProperty] private string? _redirectText;
    [ObservableProperty] private string? _serviceProblem;
    [ObservableProperty] private object? _overlay;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private string _checkupText = "Check-up";
    [ObservableProperty] private NewGamePrompt? _newGame;
    private int _refreshCount;
    private readonly LiveTracker _live = new();

    /// <summary>
    /// Whether anyone can see the window. Per-app speeds and latency are only asked for while this
    /// is true: asking is what makes the service switch on its latency measurements, and a window
    /// hidden in the tray should cost the service nothing.
    /// </summary>
    public bool LiveActive
    {
        get => _liveActive;
        set
        {
            _liveActive = value;
            if (!value)
            {
                // Nothing on screen may outlive its measurement: a strip left up while the window
                // is hidden would be the first thing shown, as if current, when it is reopened.
                foreach (var row in Apps)
                {
                    row.SetLive(null);
                }
            }
        }
    }

    private bool _liveActive = true;
    private readonly HashSet<string> _notNow = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText))]
    private bool _paused;

    public string PauseText => Paused ? "Resume" : "Pause";
    public ObservableCollection<RoleCardViewModel> Roles { get; } = [];
    public ObservableCollection<AppRowViewModel> Apps { get; } = [];
    public bool HasNoApps => Apps.Count == 0;
    public SetupViewModel Setup { get; }

    internal INetRouteClient Client => _client;
    internal ServiceStatusDto? Status { get; private set; }
    internal IReadOnlyList<AdapterDto> Adapters { get; private set; } = [];

    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }
        _refreshing = true;
        try
        {
            var status = await _client.GetStatusAsync();
            Adapters = await _client.GetAdaptersAsync();
            Status = status;
            ServiceProblem = null;
            Apply(status);
            UpdateRates();
            await RefreshLiveAsync(status);
            if (Page == "Main" && NewGame is null && _refreshCount++ % 5 == 0)
            {
                await DetectNewGameAsync();
            }
            if (Overlay is LiveViewModel live)
            {
                await live.RefreshAsync();
            }
        }
        catch (ServiceUnavailableException ex)
        {
            Page = "ServiceDown";
            ServiceProblem = ex.Message;
            SetOverall("Service not running", "BadBrush", "BadTintBrush");
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Apply(ServiceStatusDto status)
    {
        if (!status.SetupCompleted)
        {
            if (Page != "Setup")
            {
                Setup.Load(Adapters);
            }
            Page = "Setup";
            return;
        }
        Page = "Main";
        Paused = status.EnforcementPaused;

        var apps = status.Apps;
        if (status.EnforcementPaused)
        {
            SetOverall(status.PausedUntil is { } until ? $"Paused until {until.ToLocalTime():HH:mm}" : "Paused", "MutedBrush", "NeutralTintBrush");
        }
        else if (!status.EnforcementActive)
        {
            SetOverall("Needs attention", "WarnBrush", "WarnTintBrush");
        }
        else if (status.Headline() is { } headline)
        {
            // Said as a count: the other rules are installed and working, so this is not "off".
            SetOverall(headline, "WarnBrush", "WarnTintBrush");
        }
        else if (apps.Any(a => a.Verification is VerificationState.Leak))
        {
            SetOverall("Leak detected", "BadBrush", "BadTintBrush");
        }
        else if (apps.Any(a => a.Verification is VerificationState.Blocked))
        {
            SetOverall("Blocked", "BadBrush", "BadTintBrush");
        }
        else
        {
            SetOverall("Protected", "GoodBrush", "GoodTintBrush");
        }

        var banners = new List<string>();
        if (status.Summary() is { } failures)
        {
            banners.Add(failures + " Open Check-up for which ones.");
        }
        else if (status.LastError is { } error)
        {
            banners.Add(error.FriendlyMessage);
        }
        if (!status.RedirectionAvailable)
        {
            banners.Add("NetRoute can keep apps off the wrong network, but can't move them onto another one yet: " +
                        "the split-tunnel driver isn't running. Run RUN-NETROUTE-SETUP.cmd to fix this.");
        }
        Banner = banners.Count == 0 ? null : string.Join("\n", banners);
        RouteTieText = status.RouteTie?.Message;
        RedirectText = status.RedirectSummary;
        PauseDownloadsWhileGaming = status.DownloadsPause?.Enabled ?? false;
        _update = status.Update;
        UpdateText = status.Update is { } update ? Upgrade.Describe(update) : null;
        UpdateAction = status.Update is { } next ? Upgrade.ButtonText(next) : "Get it";
        UpdateFraction = status.Update?.Fraction ?? 0;
        UpdateBusy = status.Update?.State == UpdateState.Downloading;

        // The problems the service already knows about. Check-up finds more when opened.
        var issues = (status.EnforcementPaused ? 1 : 0) + (status.RouteTie is null ? 0 : 1) + (status.RedirectionAvailable ? 0 : 1)
                     + status.Roles.Count(r => r.Health != RoleHealth.Connected) + (status.Failed().Count > 0 ? 1 : 0);
        CheckupText = issues > 0 ? $"Check-up · {issues}" : "Check-up";

        SyncRoles(status.Roles);
        var downloading = status.Downloading;
        foreach (var card in Roles)
        {
            card.Activity = card.Role == RoleId.Downloads && downloading.Count > 0
                ? string.Join("   ", downloading.Take(2).Select(d => $"{d.App} is fetching {d.Item} · {Format.Bytes(d.BytesRemaining)} to go"))
                : null;
        }
        SyncApps(apps);
        RaiseNotifications(status.RecentEvents);
    }

    /// <summary>
    /// Per-app live figures for the main list. A failure here only blanks the live strip: the rest
    /// of the window works from the status the service already returned.
    /// </summary>
    private async Task RefreshLiveAsync(ServiceStatusDto status)
    {
        if (!LiveActive || Page != "Main")
        {
            return;
        }
        AppRatesDto? rates = null;
        try
        {
            rates = await _client.GetAppRatesAsync();
        }
        catch (Exception ex) when (ex is NetRouteServiceException or ServiceUnavailableException)
        {
        }

        var live = _live.Update(status.Apps, rates, DateTimeOffset.UtcNow);
        foreach (var row in Apps)
        {
            row.SetLive(live.GetValueOrDefault(row.Id));
        }
        LiveProblem = rates is { Available: false } ? rates.Problem : null;
    }

    [ObservableProperty] private string? _liveProblem;

    private readonly Dictionary<string, Counter> _counters = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Counter(long Received, long Sent, DateTime At, long StartReceived, long StartSent);

    /// <summary>
    /// Live speed per network, from Windows' own per-adapter byte counters. Read here in the
    /// app because they need no privileges and change every second.
    /// </summary>
    private void UpdateRates()
    {
        Dictionary<string, System.Net.NetworkInformation.NetworkInterface> nics;
        try
        {
            nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var card in Roles)
        {
            if (card.AdapterGuid is not { } id || !nics.TryGetValue(id, out var nic))
            {
                card.SetRates(null, null, null, null);
                continue;
            }

            long received, sent;
            try
            {
                var stats = nic.GetIPStatistics();
                received = stats.BytesReceived;
                sent = stats.BytesSent;
            }
            catch (Exception)
            {
                card.SetRates(null, null, null, null);
                continue;
            }

            if (!_counters.TryGetValue(id, out var previous) || received < previous.Received || sent < previous.Sent)
            {
                // First reading, or the counters reset because the adapter reconnected.
                _counters[id] = new Counter(received, sent, now, received, sent);
                card.SetRates(0, 0, 0, 0);
                continue;
            }

            var seconds = (now - previous.At).TotalSeconds;
            if (seconds < 0.2)
            {
                continue;
            }
            card.SetRates((received - previous.Received) / seconds, (sent - previous.Sent) / seconds,
                received - previous.StartReceived, sent - previous.StartSent);
            _counters[id] = previous with { Received = received, Sent = sent, At = now };
        }
    }

    [RelayCommand]
    private Task OpenLive() => OpenLiveAsync();

    public async Task OpenLiveAsync()
    {
        var vm = new LiveViewModel(this);
        Overlay = vm;
        await vm.RefreshAsync();
    }

    private void SetOverall(string text, string brush, string tint)
    {
        OverallText = text;
        OverallBrush = Ui.Res(brush);
        OverallTint = Ui.Res(tint);
    }

    private void SyncRoles(IReadOnlyList<RoleStatusDto> roles)
    {
        if (Roles.Count != roles.Count || Roles.Zip(roles).Any(p => p.First.Role != p.Second.Role))
        {
            Roles.Clear();
            foreach (var r in roles)
            {
                Roles.Add(new RoleCardViewModel(r.Role));
            }
        }
        for (var i = 0; i < roles.Count; i++)
        {
            Roles[i].Update(roles[i]);
        }
    }

    private void SyncApps(IReadOnlyList<AppStatusDto> apps)
    {
        // Update in place so the list doesn't flicker or lose its scroll position every refresh.
        if (Apps.Count != apps.Count || Apps.Zip(apps).Any(p => p.First.Id != p.Second.Rule.Id))
        {
            Apps.Clear();
            foreach (var a in apps.OrderBy(a => a.Rule.App.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                Apps.Add(new AppRowViewModel(a));
            }
            return;
        }
        foreach (var row in Apps)
        {
            row.Update(apps.First(a => a.Rule.Id == row.Id));
        }
    }

    private void RaiseNotifications(IReadOnlyList<ServiceEventDto> events)
    {
        var newest = events.Count == 0 ? 0 : events.Max(e => e.Id);
        if (_lastEventId < 0)
        {
            _lastEventId = newest;   // don't replay history on startup
            return;
        }
        foreach (var e in events.Where(e => e.Id > _lastEventId).OrderBy(e => e.Id))
        {
            if (e.Kind is ServiceEventKind.RoleOffline or ServiceEventKind.RoleRestored or ServiceEventKind.Leak
                or ServiceEventKind.EmergencyDisabled or ServiceEventKind.Resumed)
            {
                Notify?.Invoke(e.Title, e.Message);
            }
        }
        _lastEventId = Math.Max(_lastEventId, newest);
    }

    // ---- commands ----

    [RelayCommand]
    private Task Retry() => RefreshAsync();

    /// <summary>
    /// Starts the stopped service, via an administrator prompt. A stopped service means
    /// nothing is protecting the user's apps, so the fix should be one click, not a script.
    /// </summary>
    [RelayCommand]
    private async Task StartService()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sc.exe", "start NetRoute")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            });
            if (process is not null)
            {
                await process.WaitForExitAsync();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowToast("Starting NetRoute needs administrator permission.");
            return;
        }

        for (var i = 0; i < 10 && Page == "ServiceDown"; i++)
        {
            await Task.Delay(1000);
            await RefreshAsync();
        }
        if (Page == "ServiceDown")
        {
            ShowToast("The NetRoute service didn't start. Run INSTALL-NETROUTE.cmd again to repair it.");
        }
    }

    [RelayCommand]
    private async Task AddApp() => await OpenAddAppAsync();

    public async Task OpenAddAppAsync()
    {
        var vm = new AddAppViewModel(this);
        Overlay = vm;
        await vm.LoadAsync();
    }

    [RelayCommand]
    private void Why(AppRowViewModel row) => Overlay = new WhyViewModel(row.Dto);

    [RelayCommand]
    private void ChangeNetwork(AppRowViewModel row) => Overlay = new ChangeNetworkViewModel(this, row.Dto);

    [RelayCommand]
    private void ChangeRoleAdapter(RoleCardViewModel card) => Overlay = new AdapterPickerViewModel(this, card.Role);

    [RelayCommand]
    private Task TogglePause(AppRowViewModel row) => Run(
        () => _client.SetRulePausedAsync(row.Id, !row.IsPaused),
        row.IsPaused ? $"{row.Name} is protected again." : $"{row.Name} is paused and uses normal Windows routing.");

    [RelayCommand]
    private void Remove(AppRowViewModel row) => Overlay = new ConfirmViewModel(
        $"Remove {row.Name}?",
        $"NetRoute will stop managing {row.Name}. It will use normal Windows routing.",
        "Remove", () => Run(() => _client.RemoveRuleAsync(row.Id), $"Removed {row.Name}."));

    /// <summary>Resume straight away; pausing asks how long first (see <see cref="PauseViewModel"/>).</summary>
    [RelayCommand]
    private Task TogglePauseAll()
    {
        if (Paused)
        {
            return Resume();
        }
        Overlay = new PauseViewModel(this);
        return Task.CompletedTask;
    }

    [RelayCommand]
    public Task PauseFor(int? minutes) => Run(
        () => _client.SetEnforcementPausedAsync(true, minutes),
        minutes is { } m
            ? $"Protection is paused. It turns back on by itself at {DateTime.Now.AddMinutes(m):HH:mm}."
            : "Protection is paused until you press Resume. Your app list is kept.");

    [RelayCommand]
    private Task Resume() => Run(() => _client.SetEnforcementPausedAsync(false), "Protection is back on.");

    [RelayCommand]
    private Task OpenCheckup() => OpenCheckupAsync();

    public async Task OpenCheckupAsync()
    {
        var vm = new HealthViewModel(this);
        Overlay = vm;
        await vm.LoadAsync();
    }

    /// <summary>Set from status, so the tray can show the current setting without asking again.</summary>
    [ObservableProperty] private bool _pauseDownloadsWhileGaming;

    [RelayCommand]
    public Task TogglePauseDownloads() => Run(
        () => _client.SetPauseDownloadsAsync(!PauseDownloadsWhileGaming),
        PauseDownloadsWhileGaming ? "Downloads keep running while you play." : "Downloads will pause while you play.");

    [RelayCommand]
    private Task OpenSelfTest() => OpenSelfTestAsync();

    public async Task OpenSelfTestAsync()
    {
        var vm = new SelfTestViewModel(this);
        Overlay = vm;
        await vm.RunAsync();
    }

    [RelayCommand]
    private Task OpenHistory() => OpenHistoryAsync();

    public async Task OpenHistoryAsync()
    {
        var vm = new HistoryViewModel(this);
        Overlay = vm;
        await vm.LoadAsync();
    }

    /// <summary>Offers the installed download apps; closes itself when there's nothing to offer.</summary>
    public async Task OpenSuggestDownloadsAsync()
    {
        var vm = new SuggestDownloadsViewModel(this);
        Overlay = vm;
        if (await vm.LoadAsync() == 0 && Overlay == vm)
        {
            Overlay = null;
        }
    }

    private async Task DetectNewGameAsync()
    {
        try
        {
            var connections = await _client.GetConnectionsAsync();
            var ignored = new HashSet<string>(GuiState.Load().IgnoredGames, StringComparer.OrdinalIgnoreCase);
            ignored.UnionWith(_notNow);
            if (NewGameDetector.Find(connections, Status?.Apps ?? [], ignored) is { } game)
            {
                NewGame = game;
                Notify?.Invoke("New game detected", game.Message);
            }
        }
        catch (ServiceUnavailableException)
        {
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    [RelayCommand]
    private async Task AcceptNewGame()
    {
        if (NewGame is not { } game)
        {
            return;
        }
        NewGame = null;
        _notNow.Add(game.Root);
        await Run(() => _client.AddRuleAsync(game.Identity, RoleId.Gaming),
            $"{game.Name} now uses Gaming. Connections it already has stay where they are until it reconnects.");
    }

    [RelayCommand]
    private void DismissNewGame()
    {
        if (NewGame is { } game)
        {
            _notNow.Add(game.Root);
        }
        NewGame = null;
    }

    [RelayCommand]
    private void NeverNewGame()
    {
        if (NewGame is { } game)
        {
            GuiState.Ignore(game.Root);
        }
        NewGame = null;
    }

    [RelayCommand]
    private void EmergencyDisable() => Overlay = new ConfirmViewModel(
        "Emergency Disable",
        "This removes all NetRoute rules from Windows immediately and returns your network to normal. " +
        "Your app list is kept, and you can turn protection back on at any time.",
        "Disable everything", () => Run(() => _client.EmergencyDisableAsync(), "NetRoute is off. Normal Windows networking is restored."));

    /// <summary>One click for the tie in <see cref="RouteTieText"/>. The service makes the change; it has the rights to.</summary>
    [RelayCommand]
    private async Task FixRouteTie()
    {
        RouteFixResultDto? result = null;
        await Run(async () => result = await _client.FixRouteTieAsync());
        if (result is not null)
        {
            ShowToast(result.Message);
        }
    }

    private UpdateDto? _update;

    /// <summary>
    /// The one button on the update banner: fetch it, run it, or open the page when the feed
    /// published no hash for NetRoute to check the download against.
    /// </summary>
    [RelayCommand]
    private async Task GetUpdate()
    {
        if (_update is not { } update)
        {
            return;
        }
        switch (update.State)
        {
            case UpdateState.Ready:
                if (await Upgrade.StartAsync(update) is { } problem)
                {
                    ShowToast(problem);
                }
                return;

            case UpdateState.Downloading:
                return;

            case UpdateState.Available when update.Sha256 is { Length: 64 }:
                try
                {
                    var found = await Client.DownloadUpdateAsync();
                    UpdateText = found is null ? null : Upgrade.Describe(found);
                    UpdateAction = found is null ? "Get it" : Upgrade.ButtonText(found);
                    UpdateBusy = found?.State == UpdateState.Downloading;
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                    ShowToast("The update couldn't be fetched. Updates in Check-up has the details.");
                }
                return;

            default:
                // Either nothing to check a download against, or fetching it already went wrong.
                // Both cases end at the release's own page on GitHub.
                OpenPage(Upgrade.Page(update));
                return;
        }
    }

    /// <summary>Opens a page in the user's browser, and says so plainly when it can't.</summary>
    internal void OpenPage(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowToast("Couldn't open " + url);
        }
    }

    /// <summary>The Updates panel: where to look, whether to look, and what was found.</summary>
    [RelayCommand]
    private async Task ShowUpdates()
    {
        var vm = new UpdatesViewModel(this);
        Overlay = vm;
        await vm.LoadAsync();
    }

    [RelayCommand]
    private void CloseOverlay() => Overlay = null;

    /// <summary>Runs a service call, refreshes, and shows the outcome as a toast. Errors are shown in plain words (§31).</summary>
    internal async Task Run(Func<Task> action, string? success = null)
    {
        try
        {
            await action();
            if (success is not null)
            {
                ShowToast(success);
            }
        }
        catch (NetRouteServiceException ex)
        {
            ShowToast(ex.Error.FriendlyMessage);
        }
        catch (ServiceUnavailableException ex)
        {
            ShowToast(ex.Message);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ShowToast("Something went wrong. Details are in %LocalAppData%\\NetRoute\\gui.log.");
        }
        await RefreshAsync();
    }

    internal async void ShowToast(string message)
    {
        Toast = message;
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (Toast == message)
        {
            Toast = null;
        }
    }
}

/// <summary>One of the two network cards (§8).</summary>
public partial class RoleCardViewModel(RoleId role) : ObservableObject
{
    public RoleId Role { get; } = role;
    public string Glyph { get; } = Ui.Glyph(role);
    public Brush RoleBrush { get; } = Ui.RoleBrush(role);
    public Brush RoleTint { get; } = Ui.RoleTint(role);
    public string Title { get; } = role.DisplayName().ToUpperInvariant();

    [ObservableProperty] private string _downRate = "—";
    [ObservableProperty] private string _upRate = "—";
    [ObservableProperty] private string? _sessionText;

    /// <summary>The adapter's GUID, used to read its byte counters for the live speed.</summary>
    public string? AdapterGuid { get; private set; }

    public void SetRates(double? down, double? up, long? received, long? sent)
    {
        DownRate = down is { } d ? Format.Rate(d) : "—";
        UpRate = up is { } u ? Format.Rate(u) : "—";
        SessionText = received is { } rx && sent is { } tx
            ? $"Since NetRoute opened: {Format.Bytes(rx)} down  ·  {Format.Bytes(tx)} up"
            : null;
    }

    [ObservableProperty] private string _adapterName = "Not chosen";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private Brush _healthBrush = Ui.Res("MutedBrush");
    [ObservableProperty] private string? _note;

    /// <summary>What is being downloaded right now, when anything is.</summary>
    [ObservableProperty] private string? _activity;

    public void Update(RoleStatusDto r)
    {
        AdapterName = r.Adapter?.Name ?? r.LastKnownName ?? "Not chosen";
        AdapterGuid = r.Adapter?.Guid;

        var parts = new List<string>();
        if (r.Adapter is { } a)
        {
            parts.Add(a.Description);
            if (a.LinkSpeed.Length > 0) parts.Add(a.LinkSpeed);
        }
        if (r.LatencyMs is { } ms) parts.Add($"{ms} ms");
        if (r.PacketLossPercent is { } loss && loss > 0) parts.Add($"{loss:0.#}% loss");
        if (r.Ipv4Only) parts.Add("IPv4 only");
        Details = string.Join("  ·  ", parts);

        (HealthText, HealthBrush) = r.Health switch
        {
            RoleHealth.Connected => (r.InternetReachable == false ? "Connected, but the internet isn't answering" :
                                     r.InternetReachable == true ? "Connected  ·  Internet available" : "Connected", Ui.Res(r.InternetReachable == false ? "WarnBrush" : "GoodBrush")),
            RoleHealth.Offline => ("Offline", Ui.Res("BadBrush")),
            RoleHealth.Missing => ("Adapter not found", Ui.Res("BadBrush")),
            RoleHealth.NoInternet => ("No internet", Ui.Res("WarnBrush")),
            _ => ("Not chosen", Ui.Res("MutedBrush"))
        };

        Note = r.Health != RoleHealth.Connected && r.AssignedApps > 0
            ? $"{r.AssignedApps} app{(r.AssignedApps == 1 ? " is" : "s are")} set to use {Role.DisplayName()}. " +
              "Kill Switch is stopping them from falling back to another connection."
            : null;
    }
}

/// <summary>One row in YOUR APPS.</summary>
public partial class AppRowViewModel : ObservableObject
{
    public AppRowViewModel(AppStatusDto dto)
    {
        Id = dto.Rule.Id;
        Update(dto);
        SetLive(null);
    }

    public Guid Id { get; }
    public AppStatusDto Dto { get; private set; } = null!;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _glyph = "";
    [ObservableProperty] private Brush _roleBrush = Brushes.Gray;
    [ObservableProperty] private Brush _roleTint = Brushes.Gray;
    [ObservableProperty] private string _roleLabel = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private Brush _statusBrush = Brushes.Gray;
    [ObservableProperty] private Brush _statusTint = Brushes.Gray;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _pauseGlyph = "";
    [ObservableProperty] private string _pauseTip = "Pause";

    // Live strip: where the app really is, how fast, how laggy. Empty until the first reading.
    [ObservableProperty] private bool _hasLive;
    [ObservableProperty] private string _placeText = "";
    [ObservableProperty] private string _placeGlyph = "";
    [ObservableProperty] private Brush _placeBrush = Brushes.Gray;
    [ObservableProperty] private Brush _placeTint = Brushes.Transparent;
    [ObservableProperty] private Brush _cardBrush = Brushes.Gray;
    [ObservableProperty] private Brush _cardFill = Brushes.Gray;
    [ObservableProperty] private bool _isProblem;
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private string? _latencyText;
    [ObservableProperty] private string? _latencyTip;
    [ObservableProperty] private Brush _latencyBrush = Brushes.Gray;
    [ObservableProperty] private string _connectionText = "";
    [ObservableProperty] private Brush _rateBrush = Brushes.Gray;

    /// <summary>The verification sentence is shown when it adds something the strip doesn't: a problem, or no live data.</summary>
    public bool ShowSummary => !HasLive || IsProblem || Dto.Error is not null || Dto.Verification is not (VerificationState.Verified or VerificationState.NotRunning or VerificationState.Configured);

    /// <summary>Applies the latest live reading, or clears it when the service has none.</summary>
    public void SetLive(LiveApp? live)
    {
        HasLive = live is not null;
        if (live is null)
        {
            IsProblem = false;
            CardBrush = Ui.Res("BorderBrush");
            CardFill = Ui.Res("SurfaceBrush");
            OnPropertyChanged(nameof(ShowSummary));
            return;
        }

        var state = live.Placement.State;
        IsProblem = live.Placement.IsProblem;
        var (brush, tint, glyph) = state switch
        {
            PlacementState.Mismatch => ("BadBrush", "BadTintBrush", ""),
            PlacementState.Split => ("WarnBrush", "WarnTintBrush", ""),
            PlacementState.OnAssigned => ("GoodBrush", "GoodTintBrush", ""),
            _ => ("MutedBrush", "NeutralTintBrush", "")
        };
        PlaceText = live.Headline;
        PlaceGlyph = glyph;
        PlaceBrush = Ui.Res(brush);
        PlaceTint = Ui.Res(tint);
        CardBrush = IsProblem ? Ui.Res(brush) : Ui.Res("BorderBrush");
        CardFill = IsProblem ? Ui.Res(tint) : Ui.Res("SurfaceBrush");
        RateText = live.RateText;
        RateBrush = Ui.Res(live.HasTraffic ? "TextBrush" : "MutedBrush");
        LatencyText = live.Latency;
        LatencyTip = live.LatencyTip;
        LatencyBrush = Ui.Res(live.LatencyKind == LatencyKind.NotMeasurable ? "MutedBrush" : "TextBrush");
        ConnectionText = live.ConnectionText;
        OnPropertyChanged(nameof(ShowSummary));
    }

    public void Update(AppStatusDto dto)
    {
        Dto = dto;
        var role = dto.Rule.Role;
        Name = dto.Rule.App.DisplayName;
        Glyph = Ui.Glyph(role);
        RoleBrush = Ui.RoleBrush(role);
        RoleTint = Ui.RoleTint(role);
        RoleLabel = role.DisplayName();
        IsPaused = dto.Rule.Paused;
        PauseGlyph = IsPaused ? "" : "";
        PauseTip = IsPaused ? "Resume" : "Pause";
        Summary = dto.Error?.FriendlyMessage ?? dto.VerificationSummary;

        // Honest labels (§24): "Applied" is not "Verified".
        var (text, brush, tint) = dto.Error is not null ? ("Problem", "WarnBrush", "WarnTintBrush")
            : IsPaused ? ("Paused", "MutedBrush", "NeutralTintBrush")
            : dto.Verification switch
            {
                VerificationState.Verified => ("Verified", "GoodBrush", "GoodTintBrush"),
                VerificationState.Blocked => ("Blocked", "BadBrush", "BadTintBrush"),
                VerificationState.Leak => ("Leak", "BadBrush", "BadTintBrush"),
                VerificationState.Configured => ("Applied", "MutedBrush", "NeutralTintBrush"),
                VerificationState.NotRunning => ("Not running", "MutedBrush", "NeutralTintBrush"),
                _ => ("Windows routing", "MutedBrush", "NeutralTintBrush")
            };
        StatusText = text;
        StatusBrush = Ui.Res(brush);
        StatusTint = Ui.Res(tint);
        OnPropertyChanged(nameof(ShowSummary));
    }
}

/// <summary>A big "where should it connect?" button.</summary>
public sealed record DestinationChoice(RoleId Role, string Title, string Subtitle, bool IsCurrent)
{
    public string Glyph => Ui.Glyph(Role);
    public Brush RoleBrush => Ui.RoleBrush(Role);
    public Brush RoleTint => Ui.RoleTint(Role);

    public static List<DestinationChoice> For(ServiceStatusDto? status, RoleId? current)
    {
        string AdapterFor(RoleId r) => status?.Roles.FirstOrDefault(x => x.Role == r)?.Adapter?.Name ?? "Not set up";
        return
        [
            new(RoleId.Gaming, "Gaming", AdapterFor(RoleId.Gaming), current == RoleId.Gaming),
            new(RoleId.Downloads, "Downloads", AdapterFor(RoleId.Downloads), current == RoleId.Downloads),
            new(RoleId.Default, "Default", "Windows decides", current == RoleId.Default)
        ];
    }
}

/// <summary>One adapter in a picker.</summary>
public sealed record AdapterChoice(AdapterDto Dto)
{
    public string Name => Dto.Name;
    public string Line => string.Join("  ·  ", new[]
    {
        Dto.Description,
        Dto.Kind == AdapterKind.Virtual ? "VPN or tunnel" : Dto.LinkSpeed,
        Dto.HasIpv6Route ? null : "IPv4 only"
    }.Where(s => !string.IsNullOrEmpty(s)));
    public string StateText => Dto.State == AdapterState.Connected ? "Connected" : Dto.State.ToString();
    public Brush StateBrush => Ui.Res(Dto.State == AdapterState.Connected ? "GoodBrush" : "WarnBrush");
    public string Glyph => Dto.Kind == AdapterKind.WiFi ? "" : Dto.Kind == AdapterKind.Cellular ? "" : "";
}

/// <summary>First run (§5, §52). The user picks both; a recommendation is only a pre-selection.</summary>
public partial class SetupViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<AdapterChoice> Adapters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SameWarning))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private AdapterChoice? _gaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SameWarning))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private AdapterChoice? _downloads;

    public string? SameWarning => Gaming is not null && Gaming == Downloads
        ? "Both roles use the same connection, so NetRoute can't separate them." : null;

    public void Load(IReadOnlyList<AdapterDto> adapters)
    {
        Adapters.Clear();
        foreach (var a in adapters.Where(a => a.Selectable))
        {
            Adapters.Add(new AdapterChoice(a));
        }
        Gaming = Adapters.FirstOrDefault(a => a.Dto.Kind == AdapterKind.Ethernet);
        Downloads = Adapters.FirstOrDefault(a => a.Dto.Kind == AdapterKind.WiFi && a != Gaming)
                    ?? Adapters.FirstOrDefault(a => a != Gaming);
    }

    private bool CanFinish() => Gaming is not null && Downloads is not null;

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private async Task Finish()
    {
        await main.Run(
            () => main.Client.CompleteSetupAsync(Gaming!.Dto.Luid, Downloads!.Dto.Luid),
            $"You're ready. Games use {Gaming!.Name}, downloads use {Downloads!.Name}.");
        if (main.Page == "Main")
        {
            await main.OpenSuggestDownloadsAsync();
        }
    }
}

/// <summary>Add App (§28, §10): pick a category and an app, then where it should connect.</summary>
public partial class AddAppViewModel(MainViewModel main) : ObservableObject
{
    private List<InstalledApp> _all = [];

    public IReadOnlyList<string> Categories { get; } = ["Games", "Game Pass & Store", "Download apps", "Chat", "Browsers", "Running now", "Everything"];
    public ObservableCollection<AppChoice> Results { get; } = [];
    public ObservableCollection<DestinationChoice> Destinations { get; } = [];

    [ObservableProperty] private string _category = "Games";
    [ObservableProperty] private string? _warning;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _loading = true;
    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private string? _empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private AppChoice? _selected;

    partial void OnCategoryChanged(string value) => Filter();
    partial void OnSearchChanged(string value) => Filter();

    public string SelectedName => Selected?.Name ?? "";

    public async Task LoadAsync()
    {
        // Discovery runs here, in the user's session: the service runs as SYSTEM and can't see
        // this user's Store and Game Pass packages.
        var win32 = Task.Run(() => new Win32AppDiscovery().Discover());
        var packaged = Task.Run(() => new PackagedAppDiscovery().DiscoverAsync());
        try
        {
            var existing = main.Status?.Apps.Select(a => a.Rule.App.StableKey).ToHashSet() ?? [];
            _all = (await win32).Concat(await packaged)
                .Where(a => !existing.Contains(a.Identity.StableKey))
                .ToList();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        Loading = false;
        Filter();
    }

    private void Filter()
    {
        IEnumerable<InstalledApp> q = Category switch
        {
            "Games" => _all.Where(a => a.Category == AppCategory.Game),
            "Game Pass & Store" => _all.Where(a => a.Identity.Kind == AppIdentityKind.Packaged),
            "Download apps" => _all.Where(a => a.Category == AppCategory.Launcher),
            "Chat" => _all.Where(a => a.Category == AppCategory.Communication),
            "Browsers" => _all.Where(a => a.Category == AppCategory.Browser),
            "Running now" => _all.Where(a => a.IsRunning),
            _ => _all
        };
        if (!string.IsNullOrWhiteSpace(Search))
        {
            q = q.Where(a => a.DisplayName.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase));
        }

        Results.Clear();
        foreach (var a in q.OrderByDescending(a => a.IsRunning).ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(300))
        {
            Results.Add(new AppChoice(a));
        }
        Empty = Loading ? null : Results.Count == 0 ? "Nothing here. Try another category, or browse for the program file." : null;
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose the program to route" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(dialog.FileName).FileDescription;
        var app = new InstalledApp
        {
            Identity = AppIdentity.ForExecutable(dialog.FileName, string.IsNullOrWhiteSpace(description) ? null : description),
            Category = AppCategory.Other,
            InstallLocation = null
        };
        Selected = new AppChoice(app);
        Next();
    }

    /// <summary>A whole folder, for a games library where naming each game would never end.</summary>
    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder. Every program inside it follows the same rule." };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        Selected = new AppChoice(new InstalledApp
        {
            Identity = AppIdentity.ForFolder(dialog.FolderName),
            Category = AppCategory.Other,
            InstallLocation = dialog.FolderName
        });
        Next();
    }

    private bool CanNext() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        Destinations.Clear();
        foreach (var d in DestinationChoice.For(main.Status, null))
        {
            Destinations.Add(d);
        }
        // Said before the choice, not after it goes wrong (see LocalHostingApps).
        Warning = Selected is { } choice && LocalHostingApps.Includes(choice.App.Identity)
            ? LocalHostingApps.Explain(choice.App.Identity)
            : null;
        OnPropertyChanged(nameof(SelectedName));
        Step = 2;
    }

    [RelayCommand]
    private void Back() => Step = 1;

    [RelayCommand]
    private async Task Choose(DestinationChoice destination)
    {
        var app = Selected!.App;
        main.Overlay = null;
        await main.Run(
            () => main.Client.AddRuleAsync(app.Identity with { InstallLocation = app.InstallLocation }, destination.Role),
            $"{app.DisplayName} now uses {destination.Title}.");
    }
}

public sealed record AppChoice(InstalledApp App)
{
    public string Name => App.DisplayName;
    public bool Running => App.IsRunning;
    public string Secondary => App.Identity.Kind == AppIdentityKind.Folder ? $"Every program in {App.Identity.InstallLocation}"
        : App.IsGamePass ? "Game Pass"
        : App.Identity.Kind == AppIdentityKind.Packaged ? "Microsoft Store"
        : App.Identity.Publisher ?? App.Category switch
        {
            AppCategory.Game => "Game",
            AppCategory.Launcher => "Game launcher",
            AppCategory.Browser => "Browser",
            AppCategory.Communication => "Chat",
            _ => System.IO.Path.GetFileName(App.Identity.ExecutablePath ?? "")
        };
}

/// <summary>Change Network (§29).</summary>
public partial class ChangeNetworkViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppStatusDto _app;

    public ChangeNetworkViewModel(MainViewModel main, AppStatusDto app)
    {
        _main = main;
        _app = app;
        Destinations = DestinationChoice.For(main.Status, app.Rule.Role);
        FallBackWhenDown = app.Rule.Mode == RoutingMode.Preferred;
    }

    public string Title => $"Where should {_app.Rule.App.DisplayName} connect?";
    public IReadOnlyList<DestinationChoice> Destinations { get; }

    /// <summary>
    /// What to do when the chosen connection is down: block (the kill switch, which is the point
    /// of Strict mode) or let Windows use the other one. Blocking is the default because silently
    /// using the other connection is the failure the whole app exists to prevent.
    /// </summary>
    [ObservableProperty] private bool _fallBackWhenDown;

    [RelayCommand]
    private async Task Choose(DestinationChoice destination)
    {
        _main.Overlay = null;
        if (destination.Role == _app.Rule.Role)
        {
            return;
        }
        var updated = _app.Rule with
        {
            Role = destination.Role,
            Mode = destination.Role == RoleId.Default ? RoutingMode.Default
                : FallBackWhenDown ? RoutingMode.Preferred : RoutingMode.Strict,
            KillSwitch = !FallBackWhenDown
        };
        await _main.Run(() => _main.Client.UpdateRuleAsync(updated), $"{_app.Rule.App.DisplayName} now uses {destination.Title}.");
    }
}

/// <summary>Change which adapter a role uses (§38). Apps follow the role; none are reassigned (§33).</summary>
public partial class AdapterPickerViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public AdapterPickerViewModel(MainViewModel main, RoleId role)
    {
        _main = main;
        Role = role;
        Adapters = main.Adapters.Where(a => a.Selectable).Select(a => new AdapterChoice(a)).ToList();
        var current = main.Status?.Roles.FirstOrDefault(r => r.Role == role)?.Adapter?.Luid;
        Selected = Adapters.FirstOrDefault(a => a.Dto.Luid == current);
    }

    public RoleId Role { get; }
    public string Title => $"Choose your {Role.DisplayName()} network";
    public IReadOnlyList<AdapterChoice> Adapters { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private AdapterChoice? _selected;

    private bool CanSave() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        _main.Overlay = null;
        RoleChangeResultDto? result = null;
        await _main.Run(async () => result = await _main.Client.SetRoleAdapterAsync(Role, Selected!.Dto.Luid));
        if (result is not null)
        {
            _main.ShowToast(result.Message);
        }
    }
}

/// <summary>"Why?" (§30), built from the same reasons the service used to decide.</summary>
public sealed class WhyViewModel(AppStatusDto app)
{
    public string Title { get; } = $"{app.Rule.App.DisplayName} is using {app.Rule.Role.DisplayName()} because:";
    public IReadOnlyList<ReasonLine> Reasons { get; } = app.Reasons.Select(r => new ReasonLine(r.Good, r.Text)).ToList();
    public string Summary { get; } = app.Error?.FriendlyMessage ?? app.VerificationSummary;
    public string? Technical { get; } = app.Error?.TechnicalDetail;

    private bool AllGood => app.Error is null && app.Reasons.All(r => r.Good)
                            && app.Verification is not (VerificationState.Leak or VerificationState.Blocked);

    public string Footer => AllGood ? "Everything looks good." : "Something needs attention. See the lines marked above.";
    public Brush FooterBrush => Ui.Res(AllGood ? "GoodBrush" : "WarnBrush");
    public Brush FooterTint => Ui.Res(AllGood ? "GoodTintBrush" : "WarnTintBrush");
}

public sealed record ReasonLine(bool Good, string Text)
{
    public string Glyph => Good ? "" : "";
    public Brush Brush => Ui.Res(Good ? "GoodBrush" : "BadBrush");
}

/// <summary>
/// "What's using each network": every app with live connections, grouped by the adapter
/// the connections actually use. Not limited to apps with rules.
///
/// <para>Per-second samples are smoothed, rows keep their place between re-sorts, and an app that
/// goes quiet for a moment keeps its row (empty of rate) for a few seconds instead of vanishing
/// and reappearing. Apps that have a rule and are using a network other than the one they were
/// assigned are marked on the row.</para>
/// </summary>
public partial class LiveViewModel(MainViewModel main) : ObservableObject
{
    /// <summary>How long a row whose app has gone quiet stays up before it is allowed to leave.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(8);

    private readonly RateSmoother _rates = new();
    private readonly ValueHold<(RttSummary Rtt, LatencyKind Kind)> _latency = new(LiveTracker.LatencyHold);
    private readonly Dictionary<string, StickyOrder> _orders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<LiveGroup> Groups { get; } = [];

    [ObservableProperty] private string? _empty = "Reading live connections…";
    [ObservableProperty] private string? _footnote;

    public async Task RefreshAsync()
    {
        IReadOnlyList<ConnectionDto> connections;
        try
        {
            connections = await main.Client.GetConnectionsAsync();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Empty = "Couldn't read live connections from the NetRoute service.";
            return;
        }

        // Speeds come from a separate measurement (ETW, in the service). Without them the list
        // still shows who is on which network, just not how fast.
        AppRatesDto? rates = null;
        try
        {
            rates = await main.Client.GetAppRatesAsync();
        }
        catch (Exception ex) when (ex is NetRouteServiceException or ServiceUnavailableException)
        {
        }
        var speeds = rates?.Rates.Where(r => r.InterfaceName is not null).ToList() ?? [];

        var now = DateTimeOffset.UtcNow;
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var assigned = main.Status?.Apps ?? [];

        var adapters = connections.Select(c => c.InterfaceName).Concat(speeds.Select(r => r.InterfaceName))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Adapters an app was on a moment ago stay in the list too, so a group doesn't blink out
        // when its last app goes quiet.
        adapters.AddRange(_lastSeen.Where(e => now - e.Value <= Linger).Select(e => e.Key[..e.Key.IndexOf('|')])
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(a => !adapters.Contains(a, StringComparer.OrdinalIgnoreCase)));

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = adapters
            .Select(adapter =>
            {
                var sockets = connections.Where(c => Same(c.InterfaceName, adapter)).ToList();
                var moving = speeds.Where(r => Same(r.InterfaceName, adapter)).ToList();
                var card = main.Roles.FirstOrDefault(r => Same(r.AdapterName, adapter));

                var names = sockets.Select(c => c.ProcessName).Concat(moving.Select(r => r.ProcessName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var items = names.Select(name =>
                {
                    var key = $"{adapter}|{name}";
                    seenKeys.Add(key);
                    _lastSeen[key] = now;
                    var mine = sockets.Where(c => Same(c.ProcessName, name)).ToList();
                    var rate = moving.Where(r => Same(r.ProcessName, name)).ToList();
                    var (down, up) = _rates.Update(key, rate.Sum(r => r.DownBytesPerSecond), rate.Sum(r => r.UpBytesPerSecond), now);

                    // Only rows the service actually measured count; an app with none shows no latency at all.
                    var (latency, tip, _) = LatencyView.Describe(_latency.Get(key, LatencyView.Fresh(rate), now), LatencyView.TriedAndFailed(rate));
                    return new LiveItem(name, Detail(mine), mine.Count, down, up, latency, tip)
                    {
                        Warning = Warning(assigned, rate, adapter)
                    };
                }).ToList();

                // Apps that were here a moment ago and have gone quiet keep their row, idle, so
                // the list doesn't jump as connections close and reopen.
                foreach (var (key, at) in _lastSeen.ToList())
                {
                    var separator = key.IndexOf('|');
                    if (!Same(key[..separator], adapter) || seenKeys.Contains(key) || now - at > Linger)
                    {
                        continue;
                    }
                    seenKeys.Add(key);
                    var (down, up) = _rates.Update(key, 0, 0, now);
                    items.Add(new LiveItem(key[(separator + 1)..], "", 0, down, up));
                }

                var order = _orders.TryGetValue(adapter, out var existing) ? existing : _orders[adapter] = new StickyOrder();
                var sorted = order.Order(items.Select(i => (i.Name, i.Down + i.Up)).ToList(), now);
                var rank = sorted.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
                items = items.OrderBy(i => rank[i.Name]).ToList();

                var subtitle = card is null
                    ? $"{items.Count} app{(items.Count == 1 ? "" : "s")}"
                    : $"{card.Role.DisplayName()} network  ·  ↓ {card.DownRate}  ↑ {card.UpRate}";
                return new LiveGroup(adapter, subtitle, card?.RoleBrush ?? Ui.Res("DefaultBrush"), card?.Glyph ?? Ui.Glyph(RoleId.Default),
                    items.Take(12).ToList(), items.Count > 12 ? $"+ {items.Count - 12} more" : null, sockets.Count, 0);
            })
            // Gaming and Downloads first, in the order of the cards above, then the rest by name:
            // a fixed order, so a group never jumps because it got busier.
            .OrderBy(g => Position(g.Title))
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _rates.Keep(seenKeys);
        _latency.Keep(seenKeys);
        foreach (var gone in _lastSeen.Keys.Where(k => !seenKeys.Contains(k)).ToList())
        {
            _lastSeen.Remove(gone);
        }

        // Update in place when the shape is unchanged, so the list keeps its scroll position.
        if (Groups.Count == groups.Count && Groups.Zip(groups).All(p => p.First.Title == p.Second.Title))
        {
            for (var i = 0; i < groups.Count; i++)
            {
                Groups[i] = groups[i];
            }
        }
        else
        {
            Groups.Clear();
            foreach (var group in groups)
            {
                Groups.Add(group);
            }
        }

        var loose = connections.Count(c => c.InterfaceName is null);
        Empty = groups.Count == 0 ? "No app has network connections right now." : null;
        var notes = new List<string>();
        if (loose > 0)
        {
            notes.Add($"{loose} more socket{(loose == 1 ? " isn't" : "s aren't")} tied to one network yet (for example UDP that hasn't sent anything), so {(loose == 1 ? "it isn't" : "they aren't")} shown.");
        }
        if (rates is { Available: false })
        {
            notes.Add($"Per-app speeds aren't available: {rates.Problem}");
        }
        Footnote = notes.Count == 0 ? null : string.Join("\n", notes);
    }

    private int Position(string adapter)
    {
        for (var i = 0; i < main.Roles.Count; i++)
        {
            if (string.Equals(main.Roles[i].AdapterName, adapter, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return int.MaxValue;
    }

    /// <summary>"Assigned to Gaming (Ethernet)" when this app has a rule for a network other than the one it is on.</summary>
    private static string? Warning(IReadOnlyList<AppStatusDto> assigned, List<AppRateDto> rows, string adapter)
    {
        foreach (var row in rows)
        {
            var rule = assigned.FirstOrDefault(a => AppMatching.Covers(a.Rule.App, row.ExecutablePath, row.PackageFamilyName));
            if (rule is { Action: EnforcementAction.PinToAdapter, Rule.Paused: false, ResolvedAdapterName: { } expected }
                && !string.Equals(expected, adapter, StringComparison.OrdinalIgnoreCase))
            {
                return $"Assigned to {rule.Rule.Role.DisplayName()} ({expected})";
            }
        }
        return null;
    }

    private static string Detail(List<ConnectionDto> connections)
    {
        var tcp = connections.Count(c => c.Protocol == TransportProtocol.Tcp);
        var udp = connections.Count(c => c.Protocol == TransportProtocol.Udp);
        var parts = new List<string>();
        if (tcp > 0) parts.Add($"{tcp} TCP");
        if (udp > 0) parts.Add($"{udp} UDP");
        return string.Join("  ·  ", parts);
    }
}

public sealed record LiveGroup(string Title, string Subtitle, Brush Accent, string Glyph, IReadOnlyList<LiveItem> Items, string? More, int Total, double Speed);

/// <param name="Latency">Round trip, as text, saying which kind it is; null when not measured, so nothing is shown rather than "0 ms".</param>
/// <param name="LatencyTip">What that figure is and isn't.</param>
public sealed record LiveItem(string Name, string Detail, int Count, double Down, double Up, string? Latency = null, string? LatencyTip = null)
{
    /// <summary>Set when the app is on a network other than its assigned one.</summary>
    public string? Warning { get; init; }

    public string Rate => Down + Up >= 1 ? $"↓ {Format.Rate(Down)}   ↑ {Format.Rate(Up)}" : "idle";
}

/// <summary>A yes/no question with a destructive action.</summary>
public partial class ConfirmViewModel(string title, string message, string confirmText, Func<Task> action) : ObservableObject
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;

    [RelayCommand]
    private async Task Confirm()
    {
        if (Application.Current.MainWindow?.DataContext is MainViewModel main)
        {
            main.Overlay = null;
        }
        await action();
    }
}
