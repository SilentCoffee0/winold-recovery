using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WinOldRecovery.App.ViewModels;
using WinOldRecovery.Core.Browse;

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

    private void OnWindowSizeChanged(object sender, EventArgs e)
    {
        ViewModel.SetWindowWidth(ActualWidth);
        if (SplitterColumn is null || DetailColumn is null)
        {
            return;
        }

        if (ViewModel.CompactLayout)
        {
            SplitterColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(0);
        }
        else
        {
            SplitterColumn.Width = new GridLength(12);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private void OnStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            int.TryParse(tag, out int step) &&
            Enum.IsDefined(typeof(WorkflowStep), step))
        {
            ViewModel.CurrentStep = (WorkflowStep)step;
            if (ViewModel.CurrentStep == WorkflowStep.Preview)
            {
                _ = ViewModel.PreparePreviewAsync();
            }
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

        if (e.Key == Key.F1)
        {
            ViewModel.OpenHelp();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (e.OriginalSource is TextBoxBase or PasswordBox)
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

    private void OnFilesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView list)
        {
            return;
        }

        List<TreeNodeRow> rows = [];
        foreach (object item in list.SelectedItems)
        {
            if (item is TreeNodeRow row)
            {
                rows.Add(row);
            }
        }

        ViewModel.ReplaceSelection(rows);
    }
}
