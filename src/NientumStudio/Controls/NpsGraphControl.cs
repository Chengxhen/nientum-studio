using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using NientumStudio.Services;

namespace NientumStudio.Controls
{
    /// <summary>每秒物量（NPS）迷你曲线。</summary>
    public class NpsGraphControl : FrameworkElement
    {
        private IReadOnlyList<double> _values = Array.Empty<double>();

        public void SetValues(IReadOnlyList<double> values)
        {
            _values = values ?? Array.Empty<double>();
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            var bgRect = new Rect(0, 0, w, h);
            var borderPen = new Pen(new SolidColorBrush(ThemeManager.TimelineDivider), 1);
            borderPen.Freeze();
            dc.DrawRoundedRectangle(new SolidColorBrush(ThemeManager.GraphBg), borderPen, bgRect, 6, 6);

            // 网格
            var gridPen = new Pen(new SolidColorBrush(ThemeManager.GraphGrid), 1);
            gridPen.Freeze();
            for (int i = 1; i < 4; i++)
                dc.DrawLine(gridPen, new Point(2, h * i / 4), new Point(w - 2, h * i / 4));

            if (_values.Count < 2) return;
            double max = 1;
            double peak = 0;
            foreach (var v in _values)
            {
                max = Math.Max(max, v);
                peak = Math.Max(peak, v);
            }

            var pts = new List<Point>();
            for (int i = 0; i < _values.Count; i++)
            {
                double x = 2 + (w - 4) * i / (_values.Count - 1.0);
                double y = (h - 2) - (h - 8) * (_values[i] / max);
                pts.Add(new Point(x, y));
            }

            var lineColor = ThemeManager.GraphLine;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(pts[0].X, h - 2), true, true);
                g.LineTo(pts[0], true, false);
                foreach (var p in pts) g.LineTo(p, true, false);
                g.LineTo(new Point(pts[^1].X, h - 2), true, false);
            }
            var fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(120, lineColor.R, lineColor.G, lineColor.B), 0),
                    new GradientStop(Color.FromArgb(12, lineColor.R, lineColor.G, lineColor.B), 1)
                }, 90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, geo);

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(pts[0], false, false);
                for (int i = 1; i < pts.Count; i++) g.LineTo(pts[i], true, false);
            }
            var linePen = new Pen(new SolidColorBrush(lineColor), 1.6);
            linePen.Freeze();
            dc.DrawGeometry(null, linePen, line);

            if (peak > 0.05 && w > 80)
            {
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var ft = new FormattedText($"Peak {peak:F1} NPS", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Consolas"), 9.5,
                    new SolidColorBrush(Color.FromArgb(190, lineColor.R, lineColor.G, lineColor.B)), dpi);
                dc.DrawText(ft, new Point(w - ft.Width - 6, 4));
            }
        }
    }
}