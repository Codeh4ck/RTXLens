using Avalonia;
using Avalonia.Media;

namespace RTXLens.App.Controls.Charts;

internal static class XySeriesPath
{
    public static StreamGeometry Build(ReadOnlySpan<XySample> samples, XyPlotLayout layout, double renderScale)
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();

        var start = Math.Max(0, LowerBound(samples, layout.X.Minimum) - 1);
        var end = Math.Min(samples.Length, UpperBound(samples, layout.X.Maximum) + 1);
        var writer = new PathWriter(path);
        var bucket = new SampleBucket();

        for (var index = start; index < end; index++)
        {
            var sample = samples[index];
            if (sample.Y is not { } value || !double.IsFinite(value) || !double.IsFinite(sample.X))
            {
                bucket.Flush(ref writer);
                writer.EndSegment();
                continue;
            }

            var x = layout.ToScreenX(sample.X);
            var y = layout.ToScreenY(value);
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                bucket.Flush(ref writer);
                writer.EndSegment();
                continue;
            }

            var point = new Point(x, y);
            var column = Math.Floor(Math.Clamp(x, layout.Plot.Left - 1, layout.Plot.Right + 1) * renderScale);

            if (bucket.HasValue && bucket.Column != column)
            {
                bucket.Flush(ref writer);
            }

            bucket.Add(index, point, column);
        }

        bucket.Flush(ref writer);
        writer.EndSegment();
        return geometry;
    }

    public static XySample? FindSample(ReadOnlySpan<XySample> samples, double x, XyRange visible, bool atOrBefore)
    {
        if (samples.IsEmpty || x < samples[0].X || (!atOrBefore && x > samples[^1].X))
        {
            return null;
        }

        var after = UpperBound(samples, x);
        var bestIndex = after - 1;

        if (!atOrBefore && after < samples.Length && visible.Contains(samples[after].X)
            && (!visible.Contains(samples[bestIndex].X) || samples[after].X - x < x - samples[bestIndex].X))
        {
            bestIndex = after;
        }

        var sample = samples[bestIndex];
        return visible.Contains(sample.X) ? sample : null;
    }

    private static int LowerBound(ReadOnlySpan<XySample> samples, double value)
    {
        var low = 0;
        var high = samples.Length;

        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].X < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int UpperBound(ReadOnlySpan<XySample> samples, double value)
    {
        var low = 0;
        var high = samples.Length;

        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].X <= value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private struct SampleBucket
    {
        private IndexedPoint _first;
        private IndexedPoint _last;
        private IndexedPoint _top;
        private IndexedPoint _bottom;

        public double Column { get; private set; }
        public bool HasValue { get; private set; }

        public void Add(int index, Point point, double column)
        {
            var item = new IndexedPoint(index, point);
            if (!HasValue)
            {
                HasValue = true;
                Column = column;
                _first = _top = _bottom = item;
            }

            _last = item;
            if (point.Y < _top.Point.Y)
            {
                _top = item;
            }

            if (point.Y > _bottom.Point.Y)
            {
                _bottom = item;
            }
        }

        public void Flush(ref PathWriter writer)
        {
            if (!HasValue)
            {
                return;
            }

            // Keep both extremes in sample order so one-pixel-wide peaks and dips remain visible.
            var earlier = _top.Index < _bottom.Index ? _top : _bottom;
            var later = _top.Index < _bottom.Index ? _bottom : _top;
            writer.Add(_first);
            writer.Add(earlier);
            writer.Add(later);
            writer.Add(_last);
            HasValue = false;
        }
    }

    private readonly record struct IndexedPoint(int Index, Point Point);

    private struct PathWriter(StreamGeometryContext path)
    {
        private bool _hasSegment;
        private int _pointCount;
        private IndexedPoint _last;

        public void Add(IndexedPoint point)
        {
            if (!_hasSegment)
            {
                path.BeginFigure(point.Point, isFilled: false);
                _hasSegment = true;
                _pointCount = 1;
            }
            else if (point.Index > _last.Index)
            {
                path.LineTo(point.Point);
                _pointCount++;
            }

            _last = point;
        }

        public void EndSegment()
        {
            if (!_hasSegment)
            {
                return;
            }

            if (_pointCount == 1)
            {
                path.LineTo(_last.Point.WithX(_last.Point.X + 0.01));
            }

            path.EndFigure(isClosed: false);
            _hasSegment = false;
        }
    }
}