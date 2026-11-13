using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DC.App;

public enum LabChartKind
{
    Completion, Throughput, Capacity, Workers, PuzzleTimes, Histogram, Speedup
}

public sealed class LabChart : FrameworkElement
{
    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(37, 99, 235));
    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(217, 119, 6));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(100, 116, 139));
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(51, 65, 85));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1);
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush[] OccupancyBrushes = Enumerable.Range(0, 21).Select(index =>
    {
        double fraction = index / 20.0;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(255 - 210 * fraction),
            (byte)(255 - 150 * fraction), 245));
        brush.Freeze();
        return (Brush)brush;
    }).ToArray();
    private readonly List<(Rect Area, string Text)> _hits = [];
    private RunVisuals? _state;
    private Rect _plot;
    private double _xMax;
    public LabChartKind Kind { get; set; }

    internal void Display(RunVisuals state)
    {
        _state = state;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        _hits.Clear();
        drawing.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (ActualWidth < 120 || ActualHeight < 100) return;
        _plot = new Rect(52, 33, ActualWidth - 67, ActualHeight - 75);
        if (_state is null) { Empty(drawing, "Start a run to collect measurements."); return; }
        if (Kind == LabChartKind.Speedup) { DrawSpeedup(drawing, _state); return; }
        if (!_state.Key.Instrumented)
        {
            Empty(drawing, "Live measurements are disabled for this run.");
            return;
        }
        if (_state.Latest is not { Started: true })
        {
            Empty(drawing, _state.Finished ? "The solver supplied no search measurements." : "Waiting for search measurements…");
            return;
        }
        if (Kind == LabChartKind.PuzzleTimes) { DrawPuzzleTimes(drawing, _state); return; }
        if (Kind == LabChartKind.Histogram) { DrawHistogram(drawing, _state); return; }
        if (Kind == LabChartKind.Workers) { DrawWorkers(drawing, _state); return; }
        DrawTimeSeries(drawing, _state);
    }

    private void DrawTimeSeries(DrawingContext drawing, RunVisuals state)
    {
        double duration = Math.Max(0.1, state.Samples[^1].Seconds);
        if (Kind == LabChartKind.Completion)
        {
            RecordedRun? reference = state.Reference;
            _xMax = Math.Max(duration, reference?.Seconds ?? 0);
            Axes(drawing, "elapsed seconds", "completed", _xMax, 100);
            if (reference is not null && reference.Completion.Count > 1)
                Line(drawing, reference.Completion, _xMax, 100, Gray, dashed: true, step: true);
            Line(drawing, state.Samples.Select(sample => new PlotPoint(sample.Seconds, sample.Completed)), _xMax, 100, Blue, step: true);
            Legend(drawing, ("Current", Blue), ("Latest 1-worker run", Gray));
            return;
        }

        var first = new List<PlotPoint>();
        var second = new List<PlotPoint>();
        for (int index = 1; index < state.Samples.Count; index++)
        {
            TraceSample previous = state.Samples[index - 1];
            TraceSample current = state.Samples[index];
            double seconds = current.Seconds - previous.Seconds;
            if (seconds <= 0) continue;
            if (Kind == LabChartKind.Throughput)
                first.Add(new PlotPoint(current.Seconds, Math.Max(0, current.Attempts - previous.Attempts) / seconds / 1_000_000));
            else
            {
                double assigned = current.AssignedSeconds.Sum() - previous.AssignedSeconds.Sum();
                first.Add(new PlotPoint(current.Seconds, Math.Max(0, assigned / seconds)));
                if (current.CpuSeconds is double cpu && previous.CpuSeconds is double previousCpu)
                    second.Add(new PlotPoint(current.Seconds, Math.Max(0, (cpu - previousCpu) / seconds)));
            }
        }
        _xMax = duration;
        if (Kind == LabChartKind.Throughput)
        {
            double referenceRate = (state.Reference?.HashRate ?? 0) / 1_000_000;
            double maximum = Math.Max(0.01, Math.Max(referenceRate, first.Select(point => point.Y).DefaultIfEmpty().Max())) * 1.12;
            Axes(drawing, "elapsed seconds", "million hashes/s", duration, maximum);
            Line(drawing, first, duration, maximum, Blue);
            if (referenceRate > 0)
                Line(drawing, [new(0, referenceRate), new(duration, referenceRate)], duration, maximum, Gray, dashed: true);
            Legend(drawing, ("Interval throughput", Blue), ("1-worker average", Gray));
        }
        else
        {
            double maximum = Math.Max(state.LogicalProcessors, state.RequestedWorkers);
            maximum = Math.Max(maximum, second.Select(point => point.Y).DefaultIfEmpty().Max()) * 1.08;
            Axes(drawing, "elapsed seconds", "worker / CPU slots", duration, maximum);
            Line(drawing, first, duration, maximum, Blue);
            Line(drawing, second, duration, maximum, Orange);
            Line(drawing, [new(0, state.RequestedWorkers), new(duration, state.RequestedWorkers)], duration, maximum, Gray, dashed: true);
            Legend(drawing, ("Assigned", Blue), ("Process CPU", Orange), ("Worker limit", Gray));
        }
    }

    private void DrawWorkers(DrawingContext drawing, RunVisuals state)
    {
        _xMax = Math.Max(0.1, state.Samples[^1].Seconds);
        double rowHeight = _plot.Height / state.RequestedWorkers;
        drawing.DrawRectangle(Brushes.WhiteSmoke, GridPen, _plot);
        int stride = Math.Max(1, (int)Math.Ceiling((state.Samples.Count - 1) / Math.Max(1, _plot.Width / 3)));
        for (int start = 0; start < state.Samples.Count - 1; start += stride)
        {
            int end = Math.Min(start + stride, state.Samples.Count - 1);
            TraceSample a = state.Samples[start];
            TraceSample b = state.Samples[end];
            double seconds = b.Seconds - a.Seconds;
            if (seconds <= 0) continue;
            for (int worker = 0; worker < state.RequestedWorkers; worker++)
            {
                double fraction = Math.Clamp((b.AssignedSeconds[worker] - a.AssignedSeconds[worker]) / seconds, 0, 1);
                Brush brush = OccupancyBrushes[Math.Clamp((int)Math.Round(fraction * 20), 0, 20)];
                drawing.DrawRectangle(brush, null, new Rect(_plot.Left + a.Seconds / _xMax * _plot.Width,
                    _plot.Top + worker * rowHeight, Math.Max(0.5, seconds / _xMax * _plot.Width), Math.Max(0.5, rowHeight - 1)));
            }
        }
        int labelStep = Math.Max(1, (int)Math.Ceiling(state.RequestedWorkers / 12.0));
        for (int worker = 0; worker < state.RequestedWorkers; worker += labelStep)
            Text(drawing, $"W{worker + 1}", new Point(15, _plot.Top + worker * rowHeight + Math.Max(0, (rowHeight - 11) / 2)), 10);
        XTicks(drawing, _xMax);
        Text(drawing, "elapsed seconds", new Point(_plot.Left + _plot.Width / 2 - 37, _plot.Bottom + 24), 10);
        Text(drawing, "Light = idle; dark = assigned search work", new Point(_plot.Left, 6), 10);
    }

    private void DrawPuzzleTimes(DrawingContext drawing, RunVisuals state)
    {
        var puzzles = state.Latest?.Puzzles;
        double maximum = Math.Max(0.001, puzzles?.Select(puzzle => puzzle.ElapsedSeconds).DefaultIfEmpty().Max() ?? 0) * 1.12;
        Axes(drawing, "puzzle ID", "elapsed seconds", 100, maximum);
        Legend(drawing, ("Solved", Blue), ("Unfinished: lower bound", Orange));
        if (puzzles is null) return;
        foreach (var puzzle in puzzles.Where(puzzle => puzzle.Started))
        {
            Rect bar = Bar(puzzle.TaskId, puzzle.ElapsedSeconds, 100, maximum);
            drawing.DrawRectangle(puzzle.Solved ? Blue : Orange, null, bar);
            _hits.Add((bar, $"Puzzle {puzzle.TaskId}: {(puzzle.Solved ? "solved" : "unfinished")}\n"
                + $"Elapsed since first assignment: {puzzle.ElapsedSeconds:F4} s\n"
                + $"Summed worker assignment time: {puzzle.AssignedSeconds:F4} s\nHash attempts: {puzzle.HashAttempts:N0}"));
        }
    }

    private void DrawHistogram(DrawingContext drawing, RunVisuals state)
    {
        var puzzles = state.Latest?.Puzzles.Where(puzzle => puzzle.Solved).ToArray() ?? [];
        double[] values = puzzles.Select(puzzle => state.HistogramByAttempts ? (double)puzzle.HashAttempts : puzzle.ElapsedSeconds).ToArray();
        double maximum = Math.Max(state.HistogramByAttempts ? 1 : 0.001, values.DefaultIfEmpty().Max()) * 1.001;
        const int bins = 10;
        var counts = new int[bins];
        foreach (double value in values) counts[Math.Min(bins - 1, (int)(value / maximum * bins))]++;
        double yMaximum = Math.Max(4, Math.Ceiling(counts.Max() / 4.0) * 4);
        Axes(drawing, state.HistogramByAttempts ? "hash attempts" : "elapsed seconds", "solved puzzles", maximum, yMaximum);
        for (int bin = 0; bin < bins; bin++)
        {
            if (counts[bin] == 0) continue;
            Rect bar = Bar(bin, counts[bin], bins, yMaximum);
            drawing.DrawRectangle(Blue, null, bar);
            _hits.Add((bar, $"[{Number(bin * maximum / bins)}, {Number((bin + 1) * maximum / bins)})\n{counts[bin]} solved puzzle(s)"));
        }
        Text(drawing, $"{values.Length}/100 solved; bins rescale as data arrives", new Point(_plot.Left, 6), 10);
    }

    private void DrawSpeedup(DrawingContext drawing, RunVisuals state)
    {
        RecordedRun[] runs = state.MatchingRuns.ToArray();
        double maximumWorkers = Math.Max(4, Math.Ceiling(runs.Select(run => run.Workers)
            .DefaultIfEmpty(state.RequestedWorkers).Max() / 4.0) * 4);
        double baselineTime = RunVisuals.Median(runs.Where(run => run.Workers == 1).Select(run => run.Seconds));
        double baselineRate = RunVisuals.Median(runs.Where(run => run.Workers == 1).Select(run => run.HashRate));
        var times = new List<PlotPoint>();
        var rates = new List<PlotPoint>();
        if (baselineTime > 0 && baselineRate > 0)
        {
            foreach (var group in runs.GroupBy(run => run.Workers).OrderBy(group => group.Key))
            {
                times.Add(new PlotPoint(group.Key, baselineTime / RunVisuals.Median(group.Select(run => run.Seconds))));
                rates.Add(new PlotPoint(group.Key, RunVisuals.Median(group.Select(run => run.HashRate)) / baselineRate));
            }
        }
        double maximum = Math.Max(maximumWorkers, times.Concat(rates).Select(point => point.Y).DefaultIfEmpty().Max()) * 1.08;
        Axes(drawing, "requested workers", "speedup", maximumWorkers, maximum);
        Line(drawing, [new(0, 0), new(maximumWorkers, maximumWorkers)], maximumWorkers, maximum, Gray, dashed: true);
        Line(drawing, times, maximumWorkers, maximum, Blue, dots: true);
        Line(drawing, rates, maximumWorkers, maximum, Orange, dots: true);
        Legend(drawing, ("Time", Blue), ("Hashes/s", Orange), ("Ideal p×", Gray));
        if (baselineTime == 0) Empty(drawing, "Complete a matching 1-worker run first.");
        foreach (PlotPoint point in times)
        {
            Point location = Map(point, maximumWorkers, maximum);
            _hits.Add((new Rect(location.X - 7, location.Y - 7, 14, 14),
                $"{point.X:N0} workers\nTime speedup: {point.Y:F2}×\nTime efficiency: {point.Y / point.X:P1}\nMedians of matching recorded runs; validate results separately."));
        }
    }

    private void Axes(DrawingContext drawing, string xLabel, string yLabel, double xMaximum, double yMaximum)
    {
        _xMax = xMaximum;
        for (int tick = 0; tick <= 4; tick++)
        {
            double y = _plot.Bottom - _plot.Height * tick / 4;
            drawing.DrawLine(GridPen, new Point(_plot.Left, y), new Point(_plot.Right, y));
            Text(drawing, Number(yMaximum * tick / 4), new Point(_plot.Left - 7, y - 7), 10, right: true);
        }
        XTicks(drawing, xMaximum);
        Text(drawing, xLabel, new Point(_plot.Left + _plot.Width / 2 - xLabel.Length * 2.5, _plot.Bottom + 24), 10);
        drawing.PushTransform(new RotateTransform(-90, 9, _plot.Top + _plot.Height / 2));
        Text(drawing, yLabel, new Point(9 - yLabel.Length * 2.5, _plot.Top + _plot.Height / 2 - 6), 10);
        drawing.Pop();
    }

    private void XTicks(DrawingContext drawing, double maximum)
    {
        drawing.DrawLine(GridPen, _plot.BottomLeft, _plot.BottomRight);
        for (int tick = 0; tick <= 4; tick++)
        {
            double x = _plot.Left + _plot.Width * tick / 4;
            Text(drawing, Number(maximum * tick / 4), new Point(tick == 4 ? x : x - 5, _plot.Bottom + 5), 10, right: tick == 4);
        }
    }

    private Rect Bar(int index, double height, int count, double maximum)
    {
        double width = _plot.Width / count;
        double pixels = Math.Clamp(height / maximum, 0, 1) * _plot.Height;
        return new Rect(_plot.Left + index * width + 0.5, _plot.Bottom - pixels,
            Math.Max(0.5, width - 1), Math.Max(0.5, pixels));
    }

    private Point Map(PlotPoint point, double xMaximum, double yMaximum) => new(
        _plot.Left + Math.Clamp(point.X / Math.Max(0.000001, xMaximum), 0, 1) * _plot.Width,
        _plot.Bottom - Math.Clamp(point.Y / Math.Max(0.000001, yMaximum), 0, 1) * _plot.Height);

    private void Line(DrawingContext drawing, IEnumerable<PlotPoint> data, double xMaximum,
        double yMaximum, Brush brush, bool dashed = false, bool step = false, bool dots = false)
    {
        PlotPoint[] points = data.Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y)).ToArray();
        if (points.Length == 0) return;
        var pen = new Pen(brush, 1.8);
        if (dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            Point previous = Map(points[0], xMaximum, yMaximum);
            context.BeginFigure(previous, false, false);
            foreach (PlotPoint point in points.Skip(1))
            {
                Point next = Map(point, xMaximum, yMaximum);
                if (step) context.LineTo(new Point(next.X, previous.Y), true, false);
                context.LineTo(next, true, false);
                previous = next;
            }
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
        if (dots || points.Length == 1)
            foreach (PlotPoint point in points) drawing.DrawEllipse(brush, null, Map(point, xMaximum, yMaximum), 3, 3);
    }

    private void Legend(DrawingContext drawing, params (string Text, Brush Brush)[] items)
    {
        double x = _plot.Left;
        foreach (var item in items)
        {
            drawing.DrawLine(new Pen(item.Brush, 2), new Point(x, 12), new Point(x + 12, 12));
            Text(drawing, item.Text, new Point(x + 17, 5), 10);
            x += 30 + item.Text.Length * 5.2;
        }
    }

    private void Empty(DrawingContext drawing, string message) =>
        Text(drawing, message, new Point(_plot.Left, _plot.Top + _plot.Height / 2), 11);

    private void Text(DrawingContext drawing, string value, Point location, double size, bool right = false)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            LabelTypeface, size, Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (right) location.X -= text.Width;
        drawing.DrawText(text, location);
    }

    private static string Number(double value) => value >= 1_000_000 ? $"{value / 1_000_000:0.#}M"
        : value >= 1000 ? $"{value / 1000:0.#}k" : value.ToString(value < 1 && value > 0 ? "0.###" : "0.#", CultureInfo.CurrentCulture);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point location = e.GetPosition(this);
        string? tip = _hits.FirstOrDefault(hit => hit.Area.Contains(location)).Text;
        if (Kind == LabChartKind.Workers && _state is { } state && _plot.Contains(location))
        {
            int worker = Math.Clamp((int)((location.Y - _plot.Top) / _plot.Height * state.RequestedWorkers), 0, state.RequestedWorkers - 1);
            double seconds = (location.X - _plot.Left) / _plot.Width * _xMax;
            int next = state.Samples.FindIndex(sample => sample.Seconds >= seconds);
            if (next > 0)
            {
                TraceSample a = state.Samples[next - 1];
                TraceSample b = state.Samples[next];
                double duration = b.Seconds - a.Seconds;
                double fraction = duration > 0 ? Math.Clamp((b.AssignedSeconds[worker] - a.AssignedSeconds[worker]) / duration, 0, 1) : 0;
                tip = $"Worker lane {worker + 1}\n{a.Seconds:F2}–{b.Seconds:F2} s: {fraction:P0} assigned\nWall-time occupancy, not CPU utilization or a physical core.";
            }
        }
        if (!Equals(ToolTip, tip)) ToolTip = tip;
    }
}
