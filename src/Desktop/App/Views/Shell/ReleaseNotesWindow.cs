using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using RTXLens.Client.Updates;
using RTXLens.Contracts.Updates;
using static RTXLens.App.Views.Shell.DesktopControls;

namespace RTXLens.App.Views.Shell;

internal sealed class ReleaseNotesWindow : LensWindow
{
    private static readonly MarkdownPipeline MarkdownOptions = new MarkdownPipelineBuilder().DisableHtml().Build();
    public ReleaseNotesWindow(ReleaseNotes? notes, string? expectedTag = null, string? releaseUrl = null)
    {
        Title = "RTX Lens - Release notes";
        Width = 780;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        Body = body;
        if (notes is null)
        {
            body.Children.Add(Message(expectedTag is null ? "Release notes could not be loaded."
                : $"Release notes for {expectedTag} are unavailable.", "error"));
            if (Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri))
            {
                body.Children.Add(new HyperlinkButton { Content = "Open release", NavigateUri = uri });
            }
            return;
        }

        body.Children.Add(new TextBlock { Text = $"RTX Lens {notes.ReleaseTag}", Classes = { "heading" } });
        body.Children.Add(Caption($"Published {notes.PublishedAt.ToLocalTime():g}"));
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new HyperlinkButton { Content = "Download ZIP", NavigateUri = ReleaseNotes.DownloadFor(notes.ReleaseUrl) },
                new HyperlinkButton { Content = "Open release", NavigateUri = new Uri(notes.ReleaseUrl) }
            }
        });
        var changes = new StackPanel { Spacing = 8 };
        AddBlocks(Markdown.Parse(notes.Changelog, MarkdownOptions), changes);
        body.Children.Add(Section("Changes", changes));
    }

    public static async Task ShowAsync(Window owner, CancellationToken token, string? expectedTag = null, string? releaseUrl = null)
    {
        ReleaseNotes? notes;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            notes = await new ReleaseNotesClient(http).GetAsync(token);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException
            or OperationCanceledException)
        {
            notes = null;
        }

        if (!token.IsCancellationRequested && owner.IsVisible)
        {
            if (expectedTag is not null && notes?.ReleaseTag != expectedTag)
            {
                notes = null;
            }

            await new ReleaseNotesWindow(notes, expectedTag, releaseUrl).ShowDialog(owner);
        }
    }

    private static void AddBlocks(ContainerBlock blocks, StackPanel target)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ListBlock list:
                    var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var text = new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•" };
                        var itemContent = new StackPanel { Spacing = 4 };
                        AddBlocks(item, itemContent);
                        var row = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                            ColumnSpacing = 8,
                            Children = { text, itemContent }
                        };
                        Grid.SetColumn(itemContent, 1);
                        target.Children.Add(row);
                    }

                    break;
                case CodeBlock code:
                    target.Children.Add(new TextBlock
                    {
                        Text = code.Lines.ToString(),
                        FontFamily = new FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    break;
                case LeafBlock { Inline: { } inline } leaf:
                    var paragraph = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    if (leaf is HeadingBlock)
                    {
                        paragraph.FontSize = 17;
                        paragraph.FontWeight = FontWeight.SemiBold;
                    }

                    AddInlines(inline, paragraph.Inlines!);
                    target.Children.Add(paragraph);
                    break;
                case ContainerBlock nested:
                    AddBlocks(nested, target);
                    break;
            }
        }
    }

    private static void AddInlines(ContainerInline source, InlineCollection target)
    {
        foreach (var child in source)
        {
            Avalonia.Controls.Documents.Inline? output = child switch
            {
                LiteralInline text => new Run(text.Content.ToString()),
                CodeInline code => new Run(code.Content) { FontFamily = new FontFamily("Consolas") },
                LineBreakInline { IsHard: true } => new LineBreak(),
                LineBreakInline => new Run(" "),
                HtmlEntityInline entity => new Run(entity.Transcoded.ToString()),
                EmphasisInline { DelimiterCount: 2 } => new Bold(),
                EmphasisInline => new Italic(),
                ContainerInline => new Span(),
                _ => null
            };

            if (output is Span span && child is ContainerInline nested)
            {
                AddInlines(nested, span.Inlines);
            }

            if (output is not null)
            {
                target.Add(output);
            }
        }
    }
}
