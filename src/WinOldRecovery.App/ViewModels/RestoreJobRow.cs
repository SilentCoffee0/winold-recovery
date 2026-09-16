using CommunityToolkit.Mvvm.ComponentModel;

namespace WinOldRecovery.App.ViewModels;

public sealed class RestoreJobRow : ObservableObject
{
    private string status;

    public RestoreJobRow(long key, string name, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        Key = key;
        Name = name;
        this.status = status;
    }

    public long Key { get; }

    public string Name { get; }

    public string Status
    {
        get => status;
        private set
        {
            if (SetProperty(ref status, value))
            {
                OnPropertyChanged(nameof(Display));
                OnPropertyChanged(nameof(Glyph));
            }
        }
    }

    public string Display => Glyph + "  " + Name;

    public string Glyph => Status switch
    {
        "done" => "✔",
        "running" => "▶",
        "paused" => "❚❚",
        "failed" => "⚠",
        _ => "○",
    };

    public void SetStatus(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Status = value;
    }
}
