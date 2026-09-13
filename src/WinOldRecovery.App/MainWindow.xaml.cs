using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinOldRecovery.App.ViewModels;

namespace WinOldRecovery.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
    }

    public ShellViewModel ViewModel { get; }

    private void OnStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            int.TryParse(tag, out int step) &&
            Enum.IsDefined(typeof(WorkflowStep), step))
        {
            ViewModel.CurrentStep = (WorkflowStep)step;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            ViewModel.SearchNow();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        string? key = e.Key switch
        {
            Key.R => "R",
            Key.L => "L",
            Key.U => "U",
            Key.O => "O",
            Key.I => "I",
            Key.Space => "Space",
            _ => null,
        };
        if (key is not null && ViewModel.ApplyKeyboard(key))
        {
            e.Handled = true;
        }
    }
}
