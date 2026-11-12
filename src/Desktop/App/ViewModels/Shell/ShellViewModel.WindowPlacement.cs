using Avalonia;
using Avalonia.Controls;
using RTXLens.Presentation.Settings;

namespace RTXLens.App.ViewModels.Shell;

internal sealed partial class ShellViewModel
{
    private readonly Dictionary<Window, (string Key, Func<WindowPlacement> Capture)> _toolWindows = [];

    public void TrackToolWindow(Window window, string key, Window owner, bool besideOwner = false)
    {
        var saved = Preferences.WindowPlacements?.GetValueOrDefault(key);
        var screens = window.Screens;
        var screen = owner.Screens.ScreenFromWindow(owner) ?? screens.Primary;

        if (Preferences.RememberWindowPosition && saved is { X: { } x, Y: { } y }
            && screens.ScreenFromPoint(new PixelPoint(x, y)) is { } savedScreen
            && savedScreen.WorkingArea.Contains(new PixelPoint(x, y)))
        {
            screen = savedScreen;
            window.Position = new PixelPoint(x, y);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }

        if (Preferences.RememberWindowSize && saved is { Width: { } width, Height: { } height })
        {
            window.Width = width;
            window.Height = height;
        }

        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var scaling = screen.Scaling;
            window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, area.Width / scaling));
            window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, area.Height / scaling));

            var widthPixels = (int)Math.Ceiling(window.Width * scaling);
            var heightPixels = (int)Math.Ceiling(window.Height * scaling);
            var maxX = Math.Max(area.X, area.Right - widthPixels);
            var maxY = Math.Max(area.Y, area.Bottom - heightPixels);

            if (window.WindowStartupLocation == WindowStartupLocation.Manual)
            {
                window.Position = new PixelPoint(Math.Clamp(window.Position.X, area.X, maxX), Math.Clamp(window.Position.Y, area.Y, maxY));
            }
            else if (besideOwner)
            {
                const int gap = 12;
                var right = owner.Position.X + (int)Math.Ceiling(owner.Bounds.Width * owner.RenderScaling) + gap;
                var left = owner.Position.X - widthPixels - gap;
                if (right <= maxX || left >= area.X)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Position = new PixelPoint(right <= maxX ? right : left, Math.Clamp(owner.Position.Y, area.Y, maxY));
                }
            }
        }

        var normal = new WindowPlacement();

        _toolWindows.Add(window, (key, () => normal));
        window.Opened += (_, _) => Capture();
        window.PositionChanged += (_, _) => Capture();
        window.Resized += (_, _) => Capture();
        window.Closed += async (_, _) =>
        {
            _toolWindows.Remove(window);
            await SaveWindowPlacementsAsync([(key, normal)]);
        };
        return;

        void Capture()
        {
            if (window is { WindowState: WindowState.Normal, ClientSize: { Width: > 0, Height: > 0 } size })
            {
                normal = new WindowPlacement(Math.Min(7680, size.Width), Math.Min(4320, size.Height), window.Position.X, window.Position.Y);
            }
        }
    }

    public Task SaveMainPlacementAsync(Size size, PixelPoint position)
    {
        var placements = _toolWindows.Values.Select(window => (window.Key, Placement: window.Capture())).ToList();
        placements.Add(("main", new WindowPlacement(Math.Min(7680, size.Width), Math.Min(4320, size.Height), position.X, position.Y)));
        return SaveWindowPlacementsAsync(placements);
    }

    private async Task SaveWindowPlacementsAsync(IReadOnlyList<(string Key, WindowPlacement Placement)> updates)
    {
        await _preferencesGate.WaitAsync();
        try
        {
            var preferences = Preferences;
            if (preferences is { RememberWindowPosition: false, RememberWindowSize: false })
            {
                return;
            }

            var placements = preferences.WindowPlacements is { } existing
                ? new Dictionary<string, WindowPlacement>(existing)
                : new Dictionary<string, WindowPlacement>();
            var changed = false;
            foreach (var (key, captured) in updates)
            {
                var previous = placements.GetValueOrDefault(key) ?? new WindowPlacement();
                var next = previous;
                if (preferences.RememberWindowPosition && captured is { X: { } x, Y: { } y })
                {
                    next = next with { X = x, Y = y };
                }

                if (preferences.RememberWindowSize && captured is { Width: > 0 and var width, Height: > 0 and var height })
                {
                    if (key != "main")
                    {
                        next = next with { Width = width, Height = height };
                    }
                    else if (width >= 720 && height >= 550 && (preferences.WindowWidth != width || preferences.WindowHeight != height))
                    {
                        preferences = preferences with { WindowWidth = width, WindowHeight = height };
                        changed = true;
                    }
                }

                if (next != previous)
                {
                    placements[key] = next;
                    changed = true;
                }
            }

            if (changed)
            {
                var updated = preferences with { WindowPlacements = placements };
                await _store.SaveAsync(updated);
                Preferences = Preferences with
                {
                    WindowPlacements = placements,
                    WindowWidth = updated.WindowWidth,
                    WindowHeight = updated.WindowHeight
                };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            OperationStatus = error.Message;
        }
        finally
        {
            _preferencesGate.Release();
        }
    }
}
