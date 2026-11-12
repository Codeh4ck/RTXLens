using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;

namespace RTXLens.App.Controls;

/// <summary>A single-selection list with independently configurable actions.</summary>
/// <remarks>Move and remove commands receive the selected item. The add command has no parameter.</remarks>
public sealed class EditableList : TemplatedControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty = ItemsControl.ItemsSourceProperty.AddOwner<EditableList>();
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty = ItemsControl.ItemTemplateProperty.AddOwner<EditableList>();
    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<EditableList, object?>(nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> AddCommandProperty =
        AvaloniaProperty.Register<EditableList, ICommand?>(nameof(AddCommand));

    public static readonly StyledProperty<ICommand?> MoveUpCommandProperty =
        AvaloniaProperty.Register<EditableList, ICommand?>(nameof(MoveUpCommand));

    public static readonly StyledProperty<ICommand?> MoveDownCommandProperty =
        AvaloniaProperty.Register<EditableList, ICommand?>(nameof(MoveDownCommand));

    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<EditableList, ICommand?>(nameof(RemoveCommand));

    public static readonly StyledProperty<bool> ShowAddButtonProperty =
        AvaloniaProperty.Register<EditableList, bool>(nameof(ShowAddButton), true);

    public static readonly StyledProperty<bool> ShowMoveUpButtonProperty =
        AvaloniaProperty.Register<EditableList, bool>(nameof(ShowMoveUpButton), true);

    public static readonly StyledProperty<bool> ShowMoveDownButtonProperty =
        AvaloniaProperty.Register<EditableList, bool>(nameof(ShowMoveDownButton), true);

    public static readonly StyledProperty<bool> ShowRemoveButtonProperty =
        AvaloniaProperty.Register<EditableList, bool>(nameof(ShowRemoveButton), true);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public ICommand? AddCommand
    {
        get => GetValue(AddCommandProperty);
        set => SetValue(AddCommandProperty, value);
    }

    public ICommand? MoveUpCommand
    {
        get => GetValue(MoveUpCommandProperty);
        set => SetValue(MoveUpCommandProperty, value);
    }

    public ICommand? MoveDownCommand
    {
        get => GetValue(MoveDownCommandProperty);
        set => SetValue(MoveDownCommandProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public bool ShowAddButton
    {
        get => GetValue(ShowAddButtonProperty);
        set => SetValue(ShowAddButtonProperty, value);
    }

    public bool ShowMoveUpButton
    {
        get => GetValue(ShowMoveUpButtonProperty);
        set => SetValue(ShowMoveUpButtonProperty, value);
    }

    public bool ShowMoveDownButton
    {
        get => GetValue(ShowMoveDownButtonProperty);
        set => SetValue(ShowMoveDownButtonProperty, value);
    }

    public bool ShowRemoveButton
    {
        get => GetValue(ShowRemoveButtonProperty);
        set => SetValue(ShowRemoveButtonProperty, value);
    }
}