using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RTXLens.App.Themes;
using RTXLens.Presentation.Settings;

namespace RTXLens.App.ViewModels.Appearance;

internal sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly Application _application;
    private readonly AppearanceStore _store;
    private readonly PaletteResources _resources;
    private bool _isApplying;
    public PaletteSelection SelectedPalette { get; private set; }

    public AppearanceViewModel(Application application, AppearanceStore store)
    {
        _application = application;
        _store = store;
        _resources = new PaletteResources(application);
        _application.ActualThemeVariantChanged += (_, _) => RefreshThemePalette();

        SetPalette(default);
    }

    [ObservableProperty]
    public partial Color Primary { get; set; }

    [ObservableProperty]
    public partial Color Secondary { get; set; }

    [ObservableProperty]
    public partial Color Tertiary { get; set; }

    [ObservableProperty]
    public partial Color Background { get; set; }

    [ObservableProperty]
    public partial bool LightTheme { get; set; }

    [ObservableProperty]
    public partial bool IsReady { get; private set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    private ThemeVariant Theme => _application.ActualThemeVariant == ThemeVariant.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    private ThemePalette Palette => new(Primary, Secondary, Tertiary, Background);
    public ThemePalette GetPalette(PaletteSelection selection, AppTheme theme) => ResolvePalette(selection, theme switch
    {
        AppTheme.Dark => ThemeVariant.Dark,
        AppTheme.Light => ThemeVariant.Light,
        _ => _application.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light
            ? ThemeVariant.Light : ThemeVariant.Dark
    });

    public async Task ApplyPaletteAsync(PaletteSelection selection)
    {
        await _store.SaveAsync(BuildSettings(selection));
        SetPalette(selection);
    }

    private ThemePalette ResolvePalette(PaletteSelection selection, ThemeVariant variant)
    {
        if (selection.Preset is { } preset)
        {
            return variant == ThemeVariant.Light ? preset.Light : preset.Dark;
        }

        return selection.CustomColors ?? _resources.ReadDefaults(variant);
    }

    private AppearanceSettings BuildSettings(PaletteSelection selection) =>
        AppearanceSettings.FromPalette(Theme == ThemeVariant.Light, selection.CustomColors) with { PresetId = selection.Preset?.Id };

    public async Task LoadAsync(AppTheme? theme = null)
    {
        try
        {
            if (await _store.LoadAsync() is { } settings)
            {
                if (theme is null)
                {
                    LightTheme = settings.LightTheme;
                }

                var preset = ThemePreset.All.FirstOrDefault(preset => preset.Id == settings.PresetId);
                SetPalette(preset is null ? new PaletteSelection(CustomColors: settings.ReadPalette()) : new PaletteSelection(preset));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            SetMessage("Saved appearance could not be loaded.", hasError: true);
        }
        finally
        {
            if (theme is { } configured)
            {
                SetTheme(configured);
            }

            IsReady = true;
        }
    }

    partial void OnPrimaryChanged(Color value) => PreviewColorChange();
    partial void OnSecondaryChanged(Color value) => PreviewColorChange();
    partial void OnTertiaryChanged(Color value) => PreviewColorChange();
    partial void OnBackgroundChanged(Color value) => PreviewColorChange();

    partial void OnLightThemeChanged(bool value) => SetTheme(value ? AppTheme.Light : AppTheme.Dark);

    public void SetTheme(AppTheme theme)
    {
        var variant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        if (_application.RequestedThemeVariant != variant)
        {
            _application.RequestedThemeVariant = variant;
            RefreshThemePalette();
        }
    }

    private void RefreshThemePalette() => SetPalette(SelectedPalette);

    [RelayCommand]
    private void ResetPalette()
    {
        SetPalette(default);
        SetMessage("Default colors restored.");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var settings = BuildSettings(SelectedPalette);
        try
        {
            await _store.SaveAsync(settings);
            SetMessage("Appearance saved.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetMessage("Appearance could not be saved.", hasError: true);
        }
    }

    private void SetPalette(PaletteSelection selection)
    {
        var palette = ResolvePalette(selection, Theme);
        SelectedPalette = selection;
        _isApplying = true;

        Primary = palette.Primary;
        Secondary = palette.Secondary;
        Tertiary = palette.Tertiary;
        Background = palette.Background;

        _isApplying = false;
        _resources.Apply(selection.IsDefault ? null : palette);
    }

    private void PreviewColorChange()
    {
        if (_isApplying)
        {
            return;
        }

        SelectedPalette = new PaletteSelection(CustomColors: Palette);
        _resources.Apply(Palette);
        SetMessage(null);
    }

    private void SetMessage(string? message, bool hasError = false)
    {
        HasError = hasError;
        Message = message;
    }
}