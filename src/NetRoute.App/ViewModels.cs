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
    [ObservableProperty] private string? _redirectText;
    [ObservableProperty] private string? _serviceProblem;
    [ObservableProperty] private object? _overlay;
    [ObservableProperty] private string? _toast;

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
            SetOverall("Paused", "MutedBrush", "NeutralTintBrush");
        }
        else if (!status.EnforcementActive)
        {
            SetOverall("Needs attention", "WarnBrush", "WarnTintBrush");
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
        if (status.LastError is { } error)
        {
            banners.Add(error.FriendlyMessage);
        }
        if (!status.RedirectionAvailable)
        {
            banners.Add("NetRoute can keep apps off the wrong network, but can't move them onto another one yet: " +
                        "the split-tunnel driver isn't running. Run RUN-NETROUTE-SETUP.cmd to fix this.");
        }
        Banner = banners.Count == 0 ? null : string.Join("\n", banners);
        RedirectText = status.RedirectSummary;

        SyncRoles(status.Roles);
        SyncApps(apps);
        RaiseNotifications(status.RecentEvents);
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
            if (e.Kind is ServiceEventKind.RoleOffline or ServiceEventKind.RoleRestored or ServiceEventKind.Leak or ServiceEventKind.EmergencyDisabled)
            {
                Notify?.Invoke(e.Title, e.Message);
            }
        }
        _lastEventId = Math.Max(_lastEventId, newest);
    }

    // ---- commands ----

    [RelayCommand]
    private Task Retry() => RefreshAsync();

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

    [RelayCommand]
    private Task TogglePauseAll() => Run(
        () => _client.SetEnforcementPausedAsync(!Paused),
        Paused ? "Protection is back on." : "Protection is paused. Your app list is kept.");

    [RelayCommand]
    private void EmergencyDisable() => Overlay = new ConfirmViewModel(
        "Emergency Disable",
        "This removes all NetRoute rules from Windows immediately and returns your network to normal. " +
        "Your app list is kept, and you can turn protection back on at any time.",
        "Disable everything", () => Run(() => _client.EmergencyDisableAsync(), "NetRoute is off. Normal Windows networking is restored."));

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

    [ObservableProperty] private string _adapterName = "Not chosen";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private Brush _healthBrush = Ui.Res("MutedBrush");
    [ObservableProperty] private string? _note;

    public void Update(RoleStatusDto r)
    {
        AdapterName = r.Adapter?.Name ?? r.LastKnownName ?? "Not chosen";

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
    public string Line => string.Join("  ·  ", new[] { Dto.Description, Dto.LinkSpeed, Dto.HasIpv6Route ? null : "IPv4 only" }.Where(s => !string.IsNullOrEmpty(s)));
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
    private Task Finish() => main.Run(
        () => main.Client.CompleteSetupAsync(Gaming!.Dto.Luid, Downloads!.Dto.Luid),
        $"You're ready. Games use {Gaming!.Name}, downloads use {Downloads!.Name}.");
}

/// <summary>Add App (§28, §10): pick a category and an app, then where it should connect.</summary>
public partial class AddAppViewModel(MainViewModel main) : ObservableObject
{
    private List<InstalledApp> _all = [];

    public IReadOnlyList<string> Categories { get; } = ["Games", "Game Pass & Store", "Download apps", "Chat", "Browsers", "Running now", "Everything"];
    public ObservableCollection<AppChoice> Results { get; } = [];
    public ObservableCollection<DestinationChoice> Destinations { get; } = [];

    [ObservableProperty] private string _category = "Games";
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

    private bool CanNext() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        Destinations.Clear();
        foreach (var d in DestinationChoice.For(main.Status, null))
        {
            Destinations.Add(d);
        }
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
    public string Secondary => App.IsGamePass ? "Game Pass"
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
    }

    public string Title => $"Where should {_app.Rule.App.DisplayName} connect?";
    public IReadOnlyList<DestinationChoice> Destinations { get; }

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
            Mode = destination.Role == RoleId.Default ? RoutingMode.Default : RoutingMode.Strict
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
