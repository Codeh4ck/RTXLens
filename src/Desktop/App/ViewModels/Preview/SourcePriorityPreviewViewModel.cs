using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RTXLens.App.ViewModels.Preview;

internal sealed partial class SourcePriorityPreviewViewModel : ObservableObject
{
    private static readonly string[] DefaultSources = ["Nvidia", "ROG Astral", "WireView Pro II", "MSI MPG Ai1300TS / Ai1600TS"];

    public SourcePriorityPreviewViewModel()
    {
        MoveUpCommand = new RelayCommand<string>(source => Move(source, -1), source => CanMove(source, -1));
        MoveDownCommand = new RelayCommand<string>(source => Move(source, 1), source => CanMove(source, 1));
        RemoveCommand = new RelayCommand<string>(Remove, source => IndexOf(source) >= 0);
        AddCommand = new RelayCommand(Add, () => Sources.Count < DefaultSources.Length);

        Sources.CollectionChanged += (_, _) =>
        {
            MoveUpCommand.NotifyCanExecuteChanged();
            MoveDownCommand.NotifyCanExecuteChanged();
            RemoveCommand.NotifyCanExecuteChanged();
            AddCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<string> Sources { get; } = [.. DefaultSources];

    [ObservableProperty]
    public partial string? SelectedSource { get; set; } = DefaultSources[1];

    public IRelayCommand<string> MoveUpCommand { get; }
    public IRelayCommand<string> MoveDownCommand { get; }
    public IRelayCommand<string> RemoveCommand { get; }
    public IRelayCommand AddCommand { get; }

    private int IndexOf(string? source) => source is null ? -1 : Sources.IndexOf(source);

    private bool CanMove(string? source, int offset)
    {
        var index = IndexOf(source);
        return index >= 0 && index + offset >= 0 && index + offset < Sources.Count;
    }

    private void Move(string? source, int offset)
    {
        var index = IndexOf(source);
        if (index >= 0 && index + offset >= 0 && index + offset < Sources.Count)
        {
            Sources.Move(index, index + offset);
            SelectedSource = source;
        }
    }

    private void Remove(string? source)
    {
        var index = IndexOf(source);
        if (index < 0)
        {
            return;
        }

        Sources.RemoveAt(index);
        SelectedSource = Sources.Count == 0 ? null : Sources[Math.Min(index, Sources.Count - 1)];
    }

    private void Add()
    {
        var source = DefaultSources.FirstOrDefault(source => !Sources.Contains(source));
        if (source is not null)
        {
            Sources.Add(source);
            SelectedSource = source;
        }
    }
}