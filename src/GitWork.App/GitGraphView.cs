using System.Windows;
using System.Windows.Media;
using GitWork.Core;

namespace GitWork.App;

public sealed class GitGraphView : FrameworkElement
{
    private static readonly Brush[] s_laneBrushes =
    [
        GitGraphView.CreateBrush(Color.FromRgb(55, 190, 161)),
        GitGraphView.CreateBrush(Color.FromRgb(99, 153, 255)),
        GitGraphView.CreateBrush(Color.FromRgb(222, 158, 74)),
        GitGraphView.CreateBrush(Color.FromRgb(190, 124, 224)),
        GitGraphView.CreateBrush(Color.FromRgb(224, 106, 126)),
        GitGraphView.CreateBrush(Color.FromRgb(84, 183, 202)),
        GitGraphView.CreateBrush(Color.FromRgb(159, 183, 94)),
        GitGraphView.CreateBrush(Color.FromRgb(231, 135, 74))
    ];

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(GitGraphView.Row),
        typeof(GitGraphRow),
        typeof(GitGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SurfaceBrushProperty = DependencyProperty.Register(
        nameof(GitGraphView.SurfaceBrush),
        typeof(Brush),
        typeof(GitGraphView),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public GitGraphRow? Row
    {
        get => (GitGraphRow?)this.GetValue(GitGraphView.RowProperty);
        set => this.SetValue(GitGraphView.RowProperty, value);
    }

    public Brush SurfaceBrush
    {
        get => (Brush)this.GetValue(GitGraphView.SurfaceBrushProperty);
        set => this.SetValue(GitGraphView.SurfaceBrushProperty, value);
    }

    public double RowHeight { get; set; } = 44;

    public static readonly DependencyProperty LaneCountProperty = DependencyProperty.Register(
        nameof(GitGraphView.LaneCount), typeof(int), typeof(GitGraphView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int LaneCount
    {
        get => (int)this.GetValue(GitGraphView.LaneCountProperty);
        set => this.SetValue(GitGraphView.LaneCountProperty, value);
    }

    public GitGraphView()
    {
        this.SetResourceReference(GitGraphView.SurfaceBrushProperty, "SurfaceBrush");
    }

    protected override Size MeasureOverride(Size availableSize) => new(120, this.RowHeight);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (this.Row is not { } row)
        {
            return;
        }

        var laneWidth = this.LaneCount > 1 ? Math.Min(14, Math.Max(1, this.ActualWidth - 24) / (this.LaneCount - 1)) : 14;
        const double leftPadding = 12;
        var centerY = this.RowHeight / 2;
        double LaneX(int lane) => leftPadding + lane * laneWidth;

        foreach (var line in row.Lines)
        {
            var pen = new Pen(GitGraphView.GetLaneBrush(line.ColorIndex), 2.25);
            pen.Freeze();
            var start = new Point(LaneX(line.FromLane), line.FromY * this.RowHeight);
            var end = new Point(LaneX(line.ToLane), line.ToY * this.RowHeight);
            var middleY = (start.Y + end.Y) / 2;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(start, false, false);
                context.BezierTo(new Point(start.X, middleY), new Point(end.X, middleY), end, true, false);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        var hintPen = new Pen(this.TryFindResource("MutedBrush") as Brush ?? Brushes.Gray, 1.5) { DashStyle = DashStyles.Dot };
        if (row.Commit.DisplayParents is not null && row.Commit.Parents.Count > 1
            && row.Commit.DisplayParents.Count < row.Commit.Parents.Count)
        {
            var hint = new StreamGeometry();
            using (var context = hint.Open())
            {
                context.BeginFigure(new Point(LaneX(row.NodeLane) + 20, centerY - 12), false, false);
                context.BezierTo(new Point(LaneX(row.NodeLane) + 20, centerY),
                    new Point(LaneX(row.NodeLane) + 8, centerY), new Point(LaneX(row.NodeLane), centerY), true, false);
            }

            drawingContext.DrawGeometry(null, hintPen, hint);
        }

        if (row.HasContinuation)
        {
            drawingContext.DrawLine(hintPen, new Point(LaneX(row.NodeLane), centerY + 7),
                new Point(LaneX(row.NodeLane), this.RowHeight - 3));
        }

        var node = new Point(LaneX(row.NodeLane), centerY);
        var color = GitGraphView.GetLaneBrush(row.NodeColorIndex);
        drawingContext.DrawEllipse(this.SurfaceBrush, new Pen(color, 2.5), node, 5, 5);
        drawingContext.DrawEllipse(color, null, node, row.Commit.IsHead ? 2.5 : 1.7, row.Commit.IsHead ? 2.5 : 1.7);
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush GetLaneBrush(int colorIndex) => GitGraphView.s_laneBrushes[Math.Abs(colorIndex % GitGraphView.s_laneBrushes.Length)];
}
