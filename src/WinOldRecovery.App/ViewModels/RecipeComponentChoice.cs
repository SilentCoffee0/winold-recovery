using CommunityToolkit.Mvvm.ComponentModel;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.Recipes;

namespace WinOldRecovery.App.ViewModels;

public sealed class RecipeComponentChoice : ObservableObject
{
    private Decision decision;

    public RecipeComponentChoice(RecipeComponent component, Decision decision)
    {
        ArgumentNullException.ThrowIfNull(component);
        Key = component.Key;
        Title = component.Title;
        Summary = component.Summary;
        Fixed = component.Fixed;
        this.decision = decision;
    }

    public string Key { get; }

    public string Title { get; }

    public string Summary { get; }

    public bool Fixed { get; }

    public bool CanChoose => !Fixed;

    public string Display =>
        Title + " — " + Summary + (Fixed ? " (fixed)" : " — " + decision);

    public bool Restore
    {
        get => decision == Decision.Restore;
        set
        {
            if (Fixed)
            {
                return;
            }

            Decision next = value ? Decision.Restore : Decision.LeaveBehind;
            if (SetProperty(ref decision, next, nameof(Restore)))
            {
                OnPropertyChanged(nameof(Display));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Decision Decision => decision;

    public event EventHandler? Changed;
}
