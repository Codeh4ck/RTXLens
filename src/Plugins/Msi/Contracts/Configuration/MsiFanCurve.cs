using System.Text.Json.Serialization;

namespace RTXLens.Plugins.Msi.Contracts.Configuration;

public sealed record MsiFanCurve
{
    public MsiFanCurve() : this(points: null) { }

    [JsonConstructor]
    public MsiFanCurve(MsiFanCurvePoint[]? points, int responseDelaySeconds = 2, int? decreaseDelaySeconds = null)
    {
        Points = points ?? [new(30, 50), new(75, 100)];
        ResponseDelaySeconds = decreaseDelaySeconds ?? responseDelaySeconds;
    }

    // Editable starting points, not a manufacturer cooling profile. Inactive until selected.
    public MsiFanCurvePoint[] Points { get; init; } = [new(30, 50), new(75, 100)];
    public int ResponseDelaySeconds { get; init; } = 2;

    // System.Text.Json binds the legacy constructor parameter to this instance property.
    // Keep it null so the obsolete field is omitted when saving.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DecreaseDelaySeconds { get; }

    public static int EffectiveDutyPercent(int duty) => duty is >= 0 and < 25 ? 0 : duty;

    public void Validate()
    {
        if (Points is null || Points.Length is < 2 or > 6 || ResponseDelaySeconds is < 0 or > 60)
        {
            throw new ArgumentException("A fan curve needs 2-6 points and a response delay of 0-60 seconds.");
        }

        for (var index = 0; index < Points.Length; index++)
        {
            var point = Points[index];

            if (point is null || !double.IsFinite(point.TemperatureC) || point.TemperatureC is < 0 or > 100
                || point.TemperatureC != Math.Truncate(point.TemperatureC)
                || point.DutyPercent is < 0 or > 100 || point.DutyPercent is > 0 and < 25)
            {
                throw new ArgumentException("Fan curve points must use whole temperatures from 0-100°C and 0% for Automatic or whole duty percentages from 25 to 100%.");
            }

            if (index > 0 && point.TemperatureC <= Points[index - 1].TemperatureC)
            {
                throw new ArgumentException("Curve temperatures must be unique and ordered from lowest to highest.");
            }
        }
    }

    public bool Matches(MsiFanCurve other) => ResponseDelaySeconds == other.ResponseDelaySeconds
        && Points.SequenceEqual(other.Points);
}
