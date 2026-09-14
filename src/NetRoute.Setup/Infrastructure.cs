using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace NetRoute.Setup;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (Equals(field, value))
        {
            return false;
        }
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Command : ICommand
{
    private readonly Action _run;

    public Command(Action run) => _run = run;

    public event EventHandler CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object parameter) => true;

    public void Execute(object parameter) => _run();
}

public enum StepState
{
    Pending,
    Running,
    Done,
    Warning,
    Failed,
    Skipped
}

/// <summary>One line in the progress list.</summary>
public sealed class StepItem : Observable
{
    private static readonly Brush MutedBrush = Frozen("#98A1B2");
    private static readonly Brush AccentBrush = Frozen("#3B82F6");
    private static readonly Brush GoodBrush = Frozen("#22C55E");
    private static readonly Brush WarnBrush = Frozen("#F5A524");
    private static readonly Brush BadBrush = Frozen("#F05252");

    private StepState _state;
    private string _detail;

    public StepItem(string title) => Title = title;

    public string Title { get; }

    public StepState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(Glyph));
                Raise(nameof(Brush));
            }
        }
    }

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public string Glyph => State switch
    {
        StepState.Done => "",
        StepState.Warning => "",
        StepState.Failed => "",
        StepState.Running => "",
        StepState.Skipped => "",
        _ => ""
    };

    public Brush Brush => State switch
    {
        StepState.Done => GoodBrush,
        StepState.Warning => WarnBrush,
        StepState.Failed => BadBrush,
        StepState.Running => AccentBrush,
        _ => MutedBrush
    };

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        brush.Freeze();
        return brush;
    }
}

public sealed class SetupOptions
{
    public bool SetUpDriver = true;
    public bool StartWithWindows = true;
    public bool DesktopShortcut;
    public bool RemoveSettings;
}

/// <summary>A failure whose message is written for the person running setup.</summary>
public sealed class SetupException : Exception
{
    public SetupException(string message, Exception inner = null) : base(message, inner)
    {
    }
}

/// <summary>Setup's log: every step, command and output, for when something goes wrong.</summary>
public sealed class Log
{
    private readonly object _gate = new object();

    public Log(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath));
    }

    public string FilePath { get; }

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never be why setup fails.
            }
        }
    }
}

public sealed class PageVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null || value is string s && s.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
