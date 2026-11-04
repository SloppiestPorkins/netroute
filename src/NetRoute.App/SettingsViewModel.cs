using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NetRoute.App;

/// <summary>
/// The Settings panel. Holds the choices that apply to the whole PC rather than to one app.
/// Currently one: what happens to an app when the connection it is assigned to goes down.
/// </summary>
public partial class SettingsViewModel(MainViewModel main) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Block))]
    private bool _fallBack = main.Status?.FallBackWhenDown ?? false;

    /// <summary>The other radio button: the same choice, the other way round, so two radios can bind directly.</summary>
    public bool Block
    {
        get => !FallBack;
        set => FallBack = !value;
    }

    public string Current => main.Status?.FallBackWhenDown == true
        ? "Right now: apps move to the other connection while theirs is down."
        : "Right now: apps stay blocked while their connection is down.";

    [RelayCommand]
    private Task Save()
    {
        main.Overlay = null;
        var message = FallBack
            ? "When a connection goes down, its apps now use the other one."
            : "When a connection goes down, its apps are blocked until it is back.";
        return main.Run(() => main.Client.SetFallBackWhenDownAsync(FallBack), message);
    }
}
