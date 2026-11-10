using System.ComponentModel;
using Microsoft.UI.Xaml;
using RTXLens.Contracts.Diagnostics;

namespace RTXLens.App.Views;

public sealed record EventLogRow(ServiceLogEntry Entry, EventLogColumnWidths Widths)
{
    public string Time => Entry.Timestamp.ToLocalTime().ToString("G", System.Globalization.CultureInfo.CurrentCulture);
    public string Source => Entry.Source;
    public string Message => Entry.Message;
    public string Detail => string.IsNullOrWhiteSpace(Entry.Detail) ? Entry.Message : $"{Entry.Message}\n{Entry.Detail}";
    public Visibility Information => Visible(Entry.Level == ServiceLogLevel.Information);
    public Visibility Warning => Visible(Entry.Level == ServiceLogLevel.Warning);
    public Visibility Error => Visible(Entry.Level == ServiceLogLevel.Error);

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class EventLogColumnWidths : INotifyPropertyChanged
{
    private readonly double[] _widths = [165, 115, 175, 400];
    private bool _messageResized;

    public GridLength Time => new(_widths[0]);
    public GridLength Level => new(_widths[1]);
    public GridLength Source => new(_widths[2]);
    public GridLength Message => new(_widths[3]);
    public double Total => _widths.Sum() + 52;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Resize(int column, double delta)
    {
        _widths[column] = Math.Clamp(_widths[column] + delta, 72, 2400);
        _messageResized |= column == 3;
        Notify();
    }

    internal void FitMessage(double available)
    {
        if (!_messageResized)
        {
            _widths[3] = Math.Clamp(available - _widths[0] - _widths[1] - _widths[2] - 52, 160, 2400);
            Notify();
        }
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
