using Avalonia;
using Avalonia.Controls;
using RTXLens.Contracts.Warnings;

namespace RTXLens.WarningUI;

public sealed partial class WarningPopup : Window
{
    private Mutex? _positionMarker;
    private int _slot;
    private WarningState[]? _states;
    private bool _simulation;

    public WarningPopup() : this(null) { }

    public WarningPopup(Action? viewWarnings)
    {
        InitializeComponent();
        ViewWarnings.IsVisible = viewWarnings is not null;
        ViewWarnings.Click += (_, _) => viewWarnings?.Invoke();
        Dismiss.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            // The dashboard, preview and standalone notifier share this screen corner.
            // Keeping the named handle open reserves a slot even across processes.
            for (_slot = 0; ; _slot++)
            {
                var marker = new Mutex(false, @"Local\RTXLens.WarningPopup." + _slot, out var created);
                if (created) { _positionMarker = marker; break; }
                marker.Dispose();
            }
            Place();
        };
        Closed += (_, _) => { _positionMarker?.Dispose(); _positionMarker = null; };
        SizeChanged += (_, _) => Place();
    }

    public void Update(string heading, string detail, string source = "")
    {
        _states = null;
        ShowCards([PopupWarning.Create(heading, source, detail)]);
    }

    public void Update(WarningState[] states, bool simulation = false)
    {
        if (_simulation == simulation && _states is not null && _states.SequenceEqual(states))
        {
            return;
        }

        _simulation = simulation;
        _states = states;
        ShowCards(states.OrderByDescending(state => state.StartedAt)
            .Select(state => PopupWarning.FromState(state, simulation)).ToArray());
    }

    private void ShowCards(PopupWarning[] cards)
    {
        CountLabel.Text = cards.Length == 1 ? "Warning" : $"{cards.Length} warnings";
        Cards.ItemsSource = cards;
    }

    private void Place()
    {
        if (_positionMarker is null || Screens.Primary is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        const double gap = 12;
        var areaWidth = area.Width / scale;
        var areaHeight = area.Height / scale;
        var columns = Math.Max(1, (int)((areaWidth - gap) / (360 + gap)));
        var width = Math.Min(410, (areaWidth - gap) / columns - gap);
        Width = Math.Max(1, width);
        MaxHeight = Math.Max(1, (areaHeight - 3 * gap) / 2);
        var right = area.Right - (int)Math.Round((gap + (_slot / 2 % columns) * (Width + gap)) * scale);
        var y = _slot % 2 == 0 ? area.Bottom - (int)Math.Round((Bounds.Height + gap) * scale)
            : area.Y + (int)Math.Round(gap * scale);
        Position = new PixelPoint(right - (int)Math.Round(Width * scale), y);
    }
}
