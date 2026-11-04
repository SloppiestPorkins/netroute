using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NetRoute.App;

/// <summary>
/// One app's settings, opened from its row. The row shows only the app and its status; everything
/// else lives here, so the list stays readable (4 Nov 2026).
/// </summary>
public partial class AppSettingsViewModel(MainViewModel main, AppRowViewModel row) : ObservableObject
{
    public string Name => row.Name;
    public string Status => row.StatusText;
    public string Detail => row.Summary;
    public string PauseText => row.IsPaused ? "Resume this app" : "Pause this app";

    [RelayCommand]
    private void ChangeNetwork()
    {
        main.ChangeNetworkCommand.Execute(row);
    }

    [RelayCommand]
    private void Why()
    {
        main.WhyCommand.Execute(row);
    }

    [RelayCommand]
    private void TogglePause()
    {
        main.TogglePauseCommand.Execute(row);
        main.Overlay = null;
    }

    [RelayCommand]
    private void Remove()
    {
        main.RemoveCommand.Execute(row);
    }

    [RelayCommand]
    private void Close() => main.Overlay = null;
}
