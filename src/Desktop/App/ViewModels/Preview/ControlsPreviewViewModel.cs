using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RTXLens.App.ViewModels.Appearance;

namespace RTXLens.App.ViewModels.Preview;

internal sealed partial class ControlsPreviewViewModel(AppearanceViewModel appearance) : ObservableObject
{
    public AppearanceViewModel Appearance { get; } = appearance;

    public SourcePriorityPreviewViewModel SourcePriority { get; } = new();

    [ObservableProperty]
    public partial bool SortAscending { get; private set; }

    public string SortIndicator => SortAscending ? "▲" : "▼";
    public string FirstEventTime => SortAscending ? "09:31:44" : "09:32:10";
    public string FirstEventMessage => SortAscending ? "Service connected" : "GPU detected";
    public string SecondEventTime => SortAscending ? "09:32:10" : "09:31:44";
    public string SecondEventMessage => SortAscending ? "GPU detected" : "Service connected";

    [RelayCommand]
    private void ToggleSort()
    {
        SortAscending = !SortAscending;

        OnPropertyChanged(nameof(SortIndicator));
        OnPropertyChanged(nameof(FirstEventTime));
        OnPropertyChanged(nameof(FirstEventMessage));
        OnPropertyChanged(nameof(SecondEventTime));
        OnPropertyChanged(nameof(SecondEventMessage));
    }
}