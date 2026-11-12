#if DEBUG
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using RTXLens.App.ViewModels.Shell;
using RTXLens.App.Views.Shell;
using RTXLens.Presentation.Msi.Afterburner;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Safeguard;

internal static class AfterburnerChangesWindow
{
    public static Task<bool> ShowAsync(Window owner, ShellViewModel shell, string title, AfterburnerChangeSet plan)
    {
        var window = new LensWindow
        {
            Title = title,
            Width = 980,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var tabs = new TabControl();
        if (Application.Current?.TryFindResource("ConfigurationTabStripTheme", out var tabTheme) == true)
        {
            tabs.Theme = (ControlTheme?)tabTheme;
        }

        foreach (var file in plan.Files)
        {
            var (before, beforeEncoding) = IniDocument.Preview(file.Before);
            var (after, afterEncoding) = IniDocument.Preview(file.After);
            var diff = new TextBox
            {
                Text = BuildDiff(before, after),
                IsReadOnly = true,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Consolas")
            };
            var detail = Caption($"{beforeEncoding} → {afterEncoding}");
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8, Children = { detail, diff } };
            Grid.SetRow(diff, 1);
            var item = new TabItem { Header = Path.GetFileName(file.RelativePath) + (file.Changed ? " (modified)" : " (unchanged)"), Content = layout };
            if (Application.Current?.TryFindResource("ConfigurationTabTheme", out var itemTheme) == true)
            {
                item.Theme = (ControlTheme?)itemTheme;
            }

            tabs.Items.Add(item);
        }

        tabs.SelectedIndex = 0;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { ActionButton("Cancel", "cancel-action", () => window.Close(false)), CommitButton("Continue", () => window.Close(true)) }
        };
        var body = new Grid
        {
            Margin = new Thickness(20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 12, Children = { tabs, actions }
        };
        Grid.SetRow(actions, 1);
        window.Body = body;
        shell.TrackToolWindow(window, "afterburner-preview", owner);
        return window.ShowDialog<bool>(owner);
    }

    private static string BuildDiff(string before, string after)
    {
        var original = before.ReplaceLineEndings("\n").Split('\n');
        var proposed = after.ReplaceLineEndings("\n").Split('\n');
        if ((long)original.Length * proposed.Length > 2_000_000)
        {
            return string.Join(Environment.NewLine, original.Select(line => "- " + line).Concat(proposed.Select(line => "+ " + line)));
        }

        var lengths = new int[original.Length + 1, proposed.Length + 1];
        for (var i = original.Length - 1; i >= 0; i--)
        {
            for (var j = proposed.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = original[i] == proposed[j] ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var lines = new List<string>();
        var left = 0;
        var right = 0;
        while (left < original.Length || right < proposed.Length)
        {
            if (left < original.Length && right < proposed.Length && original[left] == proposed[right])
            {
                lines.Add("  " + original[left++]);
                right++;
            }
            else if (right < proposed.Length && (left == original.Length || lengths[left, right + 1] > lengths[left + 1, right]))
            {
                lines.Add("+ " + proposed[right++]);
            }
            else
            {
                lines.Add("- " + original[left++]);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
#endif
