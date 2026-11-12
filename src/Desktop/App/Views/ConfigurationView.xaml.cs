using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RTXLens.App.Services;
using RTXLens.App.Views.Dialogs;
using RTXLens.Presentation.Configuration;

namespace RTXLens.App.Views;

public sealed partial class ConfigurationView : UserControl
{
    private readonly AppRuntime _runtime;
    private string? _selectedSection;
    private bool _rebuilding;
    private bool _pluginRefreshQueued;
    private bool _unloaded;
    private bool _helpOpen;
    public ConfigurationEditorViewModel Model { get; }
    internal bool IsFooterVisible => EditFooter.Visibility == Visibility.Visible;
    internal string? SelectedSection => (ConfigurationTabs.SelectedItem as TabViewItem)?.Tag as string;

    internal ConfigurationView(AppRuntime runtime)
    {
        _runtime = runtime;
        Model = runtime.Configuration;
        InitializeComponent();
        WarningsHost.Content = new WarningOptionsView(Model, runtime);
#if TELEMETRY_DEBUG
        var debugButton = new Button { Content = "Open Debug Console", Name = "OpenDebugConsoleButton" };
        debugButton.Click += (_, _) => _runtime.OpenDebugConsole();
        MaintenanceActions.Children.Add(debugButton);
#endif
        RebuildPlugins();
        RebuildSelections(null, null);
        ConfigurationTabs.SelectionChanged += OnSectionChanged;
        Model.Plugins.CollectionChanged += OnPluginsChanged;
        Model.Selections.CollectionChanged += RebuildSelections;
        Model.PropertyChanged += OnModelChanged;
        Unloaded += OnUnloaded;
        OnModelChanged(null, new PropertyChangedEventArgs(nameof(Model.HasExternalChanges)));
    }

    private void OnPluginsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_pluginRefreshQueued)
        {
            return;
        }

        _pluginRefreshQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pluginRefreshQueued = false;
            if (!_unloaded)
            {
                RebuildPlugins();
            }
        });
    }

    private void RebuildPlugins()
    {
        _rebuilding = true;
        try
        {
            var pluginTabs = ConfigurationTabs.TabItems.OfType<TabViewItem>()
                .Where(tab => tab.Tag is string id && id.StartsWith("plugin:", StringComparison.Ordinal)).ToArray();
            foreach (var tab in pluginTabs.Where(tab => Model.Plugins.All(plugin => "plugin:" + plugin.Id != (string)tab.Tag)))
            {
                ConfigurationTabs.TabItems.Remove(tab);
            }

            for (var index = 0; index < Model.Plugins.Count; index++)
            {
                var plugin = Model.Plugins[index];
                var id = "plugin:" + plugin.Id;
                var tab = pluginTabs.FirstOrDefault(tab => tab.Tag as string == id);
                if (tab is null)
                {
                    tab = new TabViewItem
                    {
                        Tag = id,
                        IsClosable = false,
                        Style = (Style)Resources["MonitorConfigurationTab"],
                        Content = new ScrollViewer
                        {
                            Padding = new Thickness(0, 16, 0, 8),
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            VerticalContentAlignment = VerticalAlignment.Top
                        }
                    };
                }
                tab.Header = plugin.IsAsus ? "ROG Astral" : plugin.IsWireView ? "WireView Pro II" : plugin.IsMsi ? "MSI PSU" : plugin.IsNvidia ? "Nvidia" : plugin.Name;
                var scroll = (ScrollViewer)tab.Content;
                if (scroll.Content is not PluginSettingsView existing || !ReferenceEquals(existing.Model, plugin))
                {
                    scroll.Content = new PluginSettingsView(plugin, _runtime) { VerticalAlignment = VerticalAlignment.Top };
                }

                var previousIndex = ConfigurationTabs.TabItems.IndexOf(tab);
                if (previousIndex == index)
                {
                    continue;
                }

                if (previousIndex >= 0)
                {
                    ConfigurationTabs.TabItems.Remove(tab);
                }

                ConfigurationTabs.TabItems.Insert(index, tab);
            }

            var selected = ConfigurationTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(tab => tab.Tag as string == _selectedSection)
                ?? ConfigurationTabs.TabItems.OfType<TabViewItem>().First();
            if (!ReferenceEquals(ConfigurationTabs.SelectedItem, selected))
            {
                ConfigurationTabs.SelectedItem = selected;
            }

            _selectedSection ??= (string)selected.Tag;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void RebuildSelections(object? sender, NotifyCollectionChangedEventArgs? args) => ProfilesHost.Content = new ConnectorProfilesView(Model.ConnectorProfiles);

    internal void SelectSection(string id) => ConfigurationTabs.SelectedItem = ConfigurationTabs.TabItems.OfType<TabViewItem>().Single(tab => tab.Tag as string == id);

    internal void TrySelectSection(string id)
    {
        var tab = ConfigurationTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(item => item.Tag as string == id);
        if (tab is not null)
        {
            ConfigurationTabs.SelectedItem = tab;
        }
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_rebuilding)
        {
            _selectedSection = SelectedSection;
            if (_selectedSection == "warnings" && DeviceWarningProfilesHost.Content is null)
            {
                DeviceWarningProfilesHost.Content = new DeviceWarningProfilesView(_runtime);
            }

            if (_selectedSection == "gpu-profiles" && SettingsProfilesHost.Content is null)
            {
                SettingsProfilesHost.Content = new SettingsProfilesView(_runtime);
            }
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        ConflictText.Visibility = Model.HasExternalChanges ? Visibility.Visible : Visibility.Collapsed;
        if (args.PropertyName == nameof(Model.RevisionText))
        {
            RebuildSelections(null, null);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _unloaded = true;
        ConfigurationTabs.SelectionChanged -= OnSectionChanged;
        Model.Plugins.CollectionChanged -= OnPluginsChanged;
        Model.Selections.CollectionChanged -= RebuildSelections;
        Model.PropertyChanged -= OnModelChanged;
    }

    internal Task ShowBackendRestartHelpAsync() => ShowHelpAsync(new BackendRestartHelpDialog());
    internal Task ShowSensorStreamHelpAsync() => ShowHelpAsync(new SensorStreamHelpDialog());
    internal Task ShowSourceSelectionsHelpAsync() => ShowHelpAsync(new SourceSelectionsHelpDialog());

    internal async Task ShowHelpAsync(ContentDialog dialog)
    {
        if (_helpOpen)
        {
            return;
        }

        _helpOpen = true;
        try
        {
            dialog.XamlRoot = XamlRoot;
            dialog.RequestedTheme = ActualTheme;
            await dialog.ShowAsync();
        }
        finally
        {
            _helpOpen = false;
        }
    }

    private async void BackendRestartInfo_Click(object sender, RoutedEventArgs args) => await ShowBackendRestartHelpAsync();
    private async void SensorStreamInfo_Click(object sender, RoutedEventArgs args) => await ShowSensorStreamHelpAsync();
    private async void SourceSelectionsInfo_Click(object sender, RoutedEventArgs args) => await ShowSourceSelectionsHelpAsync();
    private async void Apply_Click(object sender, RoutedEventArgs args) => await Model.ApplyAsync(_runtime.StopToken);
    private async void Reload_Click(object sender, RoutedEventArgs args) => await Model.ReloadAsync(_runtime.StopToken);
    private void AddSelection_Click(object sender, RoutedEventArgs args) => Model.AddSelection();
    private async void RestartService_Click(object sender, RoutedEventArgs args) => await _runtime.RestartServiceAsync();
}
