using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RTXLens.Plugins.WireView.Contracts.Common;
using Windows.Globalization.NumberFormatting;

namespace RTXLens.App.WireView.Windows;

internal sealed class WireViewNumberInput
{
    private readonly string _label;
    private readonly double _minimum;
    private readonly double _maximum;
    private readonly int _decimalPlaces;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private TextBox? _textBox;

    public NumberBox Control { get; }

    public WireViewNumberInput(string label, double value, double minimum, double maximum, int decimalPlaces)
    {
        _label = label;
        _minimum = minimum;
        _maximum = maximum;
        _decimalPlaces = decimalPlaces;
        var step = decimalPlaces == 0 ? 1d : 0.1d;

        var formatter = new DecimalFormatter
        {
            IntegerDigits = 1,
            FractionDigits = decimalPlaces,
            IsGrouped = false,
            IsDecimalPointAlwaysDisplayed = decimalPlaces != 0,
            NumberRounder = new IncrementNumberRounder { Increment = step, RoundingAlgorithm = RoundingAlgorithm.RoundHalfAwayFromZero }
        };

        Control = new NumberBox
        {
            NumberFormatter = formatter,
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = step,
            LargeChange = step * 10,
            AcceptsExpression = false,
            ValidationMode = NumberBoxValidationMode.Disabled,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = value
        };

        Control.Loaded += OnLoaded;
        Control.Unloaded += OnUnloaded;
    }

    public double Read()
    {
        var text = _textBox?.Text ?? Control.Text;
        if (IsEditableText(text) && Control.NumberFormatter is INumberParser parser
            && parser.ParseDouble(text) is { } value
            && WireViewSettingsValidation.IsNumberValid(value, _minimum, _maximum, _decimalPlaces))
        {
            return value;
        }

        throw new ArgumentException($"{_label} must be between {_minimum} and {_maximum}, with "
            + (_decimalPlaces == 0 ? "no decimal places." : "at most one decimal place."));
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_textBox is null && FindTextBox(Control) is { } input)
        {
            _textBox = input;
            _textBox.BeforeTextChanging += OnTextChanging;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_textBox is { } input)
        {
            input.BeforeTextChanging -= OnTextChanging;
            _textBox = null;
        }
    }

    private void OnTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args) =>
        args.Cancel = !IsEditableText(args.NewText);

    private bool IsEditableText(string text)
    {
        var separator = _culture.NumberFormat.NumberDecimalSeparator;
        var negativeSign = _culture.NumberFormat.NegativeSign;

        if (_minimum < 0 && text.StartsWith(negativeSign, StringComparison.Ordinal))
        {
            text = text[negativeSign.Length..];
        }

        var point = text.IndexOf(separator, StringComparison.Ordinal);
        if (point >= 0)
        {
            if (_decimalPlaces == 0 || text.Length - point - separator.Length > _decimalPlaces)
            {
                return false;
            }

            text = text.Remove(point, separator.Length);
        }

        return text.All(char.IsAsciiDigit);
    }

    private static TextBox? FindTextBox(DependencyObject parent)
    {
        if (parent is TextBox input)
        {
            return input;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            if (FindTextBox(VisualTreeHelper.GetChild(parent, index)) is { } child)
            {
                return child;
            }
        }

        return null;
    }
}

