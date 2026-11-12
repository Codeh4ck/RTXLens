using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace RTXLens.App.Controls;

internal sealed class ValidatedNumberInput : NumericUpDown
{
    private readonly string _label;
    private readonly int _places;
    private bool _templateApplied;

    public ValidatedNumberInput(string label, decimal? value, decimal minimum, decimal maximum, int places = 0)
    {
        _label = label;
        _places = places;
        Minimum = minimum;
        Maximum = maximum;
        FormatString = places == 0 ? "0" : "0." + new string('0', places);
        Increment = places == 0 ? 1 : 0.1m;
        Value = value;
    }

    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _templateApplied = true;
    }

    public decimal Read() => ReadOptional() ?? throw new ArgumentException($"Enter a value for {_label}.");

    public decimal? ReadOptional()
    {
        var text = Text;
        if (text is null && !_templateApplied)
        {
            // An editor on a tab that has never been opened may only have its initial Value.
            text = Value?.ToString("G29", CultureInfo.CurrentCulture);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var point = text.IndexOf(separator, StringComparison.Ordinal);
        var validPrecision = point < 0 || _places > 0 && text.Length - point - separator.Length <= _places;
        if (validPrecision && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.CurrentCulture, out var value) && value >= Minimum && value <= Maximum)
        {
            return value;
        }

        var precision = _places == 0 ? "a whole number" : $"a number with up to {_places} decimal place";
        throw new ArgumentException($"{_label} must be {precision} from {Minimum} to {Maximum}.");
    }
}
