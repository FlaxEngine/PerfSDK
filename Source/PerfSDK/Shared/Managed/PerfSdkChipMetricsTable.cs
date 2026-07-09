#if USE_PROFILER
using FlaxEngine;
using FlaxEngine.GUI;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NVIDIA
{
    /// <summary>
    /// Shared metrics table with grouped throughput bars. Used by both editor and runtime metrics windows.
    /// </summary>
    internal sealed class PerfSdkChipMetricsTable : ContainerControl
    {
        private const float RowHeight = 30.0f;
        private const float GroupHeaderHeight = 24.0f;
        private const float HeaderRowHeight = 22.0f;
        private const float FooterTopGap = 10.0f;
        private const float FooterBottomGap = 10.0f;
        private const float FooterLineHeight = 18.0f;
        private const float BarHeight = 14.0f;
        private const float ColumnGap = 6.0f;
        private const float GroupHeaderPadding = 8.0f;
        private const float ValueColumnStart = GroupHeaderPadding;
        private const float BarTrackRatio = 0.11f;
        private const float PercentLabelWidth = 40.0f;
        private const float BarPercentGap = 4.0f;

        private enum RowKind { GroupHeader, Metric }

        private enum MetricDisplayFormat
        {
            Percent = 0, Warps = 1, Pixels = 2, Bandwidth = 3, Time = 4, Counter = 5,
        }

        private struct RowData
        {
            public RowKind Kind;
            public string Title;
            public int MetricIndex;
            public double Target;
            public double Display;
            public string Detail;
            public bool HigherIsBetter;
            public MetricDisplayFormat Format;
            public bool ShowBar => Format != MetricDisplayFormat.Counter;
        }

        private struct GroupHeaderHit
        {
            public string Title;
            public Rectangle Rect;
        }

        private RowData[] _rows = Array.Empty<RowData>();
        private string _emptyMessage = "Collecting GPU samples...";
        private string _regionName = string.Empty;
        private string _regionStatsLine = string.Empty;
        private string _lastRegionName = string.Empty;
        private string _lastRegionStatsLine = string.Empty;
        private float _lastLayoutHeight = -1.0f;
        private readonly HashSet<string> _collapsedGroups = new HashSet<string>();
        private readonly List<GroupHeaderHit> _groupHits = new List<GroupHeaderHit>();

        public void SetRegionStats(string regionName, float gpuTimeMs, long drawCalls, long triangles, long vertices)
        {
            regionName ??= string.Empty;
            string statsLine = string.Format(
                "GPU time: {0:0.000} ms   Draw calls: {1:#,0}   Triangles: {2:#,0}   Vertices: {3:#,0}",
                gpuTimeMs, drawCalls, triangles, vertices);

            if (regionName == _lastRegionName && statsLine == _lastRegionStatsLine)
                return;

            _regionName = regionName;
            _regionStatsLine = statsLine;
            _lastRegionName = regionName;
            _lastRegionStatsLine = statsLine;
            UpdateLayoutHeight();
        }

        public void SetEmptyMessage(string message)
        {
            if (!string.IsNullOrEmpty(message))
                _emptyMessage = message;
        }

        public void SetChipAreasText(string text, bool expandCustomGroup = false)
        {
            if (string.IsNullOrEmpty(text))
            {
                _rows = Array.Empty<RowData>();
                UpdateLayoutHeight();
                return;
            }

            if (!expandCustomGroup && TryApplyValueUpdates(text))
                return;

            if (expandCustomGroup)
                _collapsedGroups.Remove("Custom");

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var rows = new RowData[lines.Length];
            int count = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length < 2)
                    continue;

                if (parts[0] == "#GROUP")
                {
                    rows[count++] = new RowData { Kind = RowKind.GroupHeader, Title = parts[1] };
                    continue;
                }

                if (parts.Length < 3 || !double.TryParse(parts[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double target))
                    continue;

                bool higherIsBetter = parts.Length >= 4 && parts[3] == "1";
                var format = MetricDisplayFormat.Percent;
                if (parts.Length >= 5 && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int formatCode) &&
                    Enum.IsDefined(typeof(MetricDisplayFormat), formatCode))
                    format = (MetricDisplayFormat)formatCode;

                int metricIndex = -1;
                if (parts.Length >= 6)
                    int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out metricIndex);

                rows[count++] = new RowData
                {
                    Kind = RowKind.Metric, Title = parts[0], MetricIndex = metricIndex,
                    Target = target, Display = target, Detail = parts[2],
                    HigherIsBetter = higherIsBetter, Format = format,
                };
            }

            if (count == lines.Length)
                _rows = rows;
            else
            {
                var trimmed = new RowData[count];
                Array.Copy(rows, trimmed, count);
                _rows = trimmed;
            }

            UpdateLayoutHeight();
        }

        bool TryApplyValueUpdates(string text)
        {
            if (_rows.Length == 0) return false;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int rowIndex = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length < 2) continue;

                if (parts[0] == "#GROUP")
                {
                    if (rowIndex >= _rows.Length || _rows[rowIndex].Kind != RowKind.GroupHeader || _rows[rowIndex].Title != parts[1])
                        return false;
                    rowIndex++;
                    continue;
                }

                if (parts.Length < 3 || !double.TryParse(parts[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double target))
                    return false;
                if (rowIndex >= _rows.Length || _rows[rowIndex].Kind != RowKind.Metric)
                    return false;

                int metricIndex = -1;
                if (parts.Length >= 6)
                    int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out metricIndex);

                RowData row = _rows[rowIndex];
                if (metricIndex >= 0) { if (row.MetricIndex != metricIndex) return false; }
                else if (row.Title != parts[0]) return false;

                row.Target = target;
                row.Display = target;
                row.Detail = parts[2];
                row.HigherIsBetter = parts.Length >= 4 && parts[3] == "1";
                if (parts.Length >= 5 && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int formatCode) &&
                    Enum.IsDefined(typeof(MetricDisplayFormat), formatCode))
                    row.Format = (MetricDisplayFormat)formatCode;
                if (metricIndex >= 0) row.MetricIndex = metricIndex;
                _rows[rowIndex] = row;
                rowIndex++;
            }

            return rowIndex == _rows.Length;
        }

        float GetValueColumnWidth(float contentWidth) => contentWidth * BarTrackRatio + PercentLabelWidth + BarPercentGap;
        float GetBarTrackWidth(float contentWidth, float valueColumnWidth) => Math.Max(0.0f, valueColumnWidth - PercentLabelWidth - BarPercentGap);

        void UpdateLayoutHeight()
        {
            float newHeight = ComputeContentHeight();
            if (Math.Abs(newHeight - _lastLayoutHeight) < 0.5f) return;
            _lastLayoutHeight = newHeight;
            Height = newHeight;
            Width = Math.Max(Width, 100.0f);
        }

        float ComputeContentHeight()
        {
            float y = 10.0f;
            if (!string.IsNullOrEmpty(_regionName) || !string.IsNullOrEmpty(_regionStatsLine))
                y += FooterTopGap + FooterLineHeight * 2.0f + FooterBottomGap;
            y += HeaderRowHeight;
            if (_rows.Length == 0)
                return Math.Max(y + RowHeight + 10.0f, 100.0f);

            bool collapsed = false;
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i].Kind == RowKind.GroupHeader)
                { collapsed = _collapsedGroups.Contains(_rows[i].Title); y += GroupHeaderHeight; continue; }
                if (!collapsed) y += RowHeight;
            }
            return Math.Max(y + 10.0f, 100.0f);
        }

        public override void Draw()
        {
            base.Draw();
            var style = Style.Current;
            float contentWidth = Width;
            float valueColumnWidth = GetValueColumnWidth(contentWidth);
            float barTrackWidth = GetBarTrackWidth(contentWidth, valueColumnWidth);
            float xBar = ValueColumnStart;
            float xDetail = xBar + valueColumnWidth + ColumnGap;
            float detailWidth = Math.Max(0.0f, contentWidth - xDetail);

            float y = 10;
            y = DrawFooter(style, y, contentWidth);
            float headerTop = y;
            DrawHeader(style, headerTop, xBar, valueColumnWidth, xDetail, detailWidth, contentWidth);
            y = headerTop + HeaderRowHeight;

            if (_rows.Length == 0)
            {
                Render2D.DrawText(style.FontMedium, _emptyMessage, new Rectangle(0, HeaderRowHeight, contentWidth, RowHeight), style.ForegroundGrey, TextAlignment.Near, TextAlignment.Center);
            }
            else
            {
                _groupHits.Clear();
                bool collapsed = false;
                for (int i = 0; i < _rows.Length; i++)
                {
                    RowData row = _rows[i];
                    if (row.Kind == RowKind.GroupHeader)
                    {
                        collapsed = _collapsedGroups.Contains(row.Title);
                        DrawGroupHeader(style, row.Title, y, contentWidth, collapsed);
                        _groupHits.Add(new GroupHeaderHit { Title = row.Title, Rect = new Rectangle(0, y, contentWidth, GroupHeaderHeight) });
                        y += GroupHeaderHeight;
                        continue;
                    }
                    if (collapsed) continue;

                    if (IsMouseOver)
                        Render2D.FillRectangle(new Rectangle(0, y, contentWidth, RowHeight), style.BackgroundHighlighted * 0.35f);

                    if (row.ShowBar) DrawThroughputBar(style, xBar, y, barTrackWidth, row);
                    else DrawCounterValue(style, xBar, y, valueColumnWidth, row);

                    Render2D.DrawText(style.FontMedium, row.Detail, new Rectangle(xDetail, y, Math.Max(detailWidth, MeasureDetailTextWidth(style, row.Detail) + 4.0f), RowHeight), style.Foreground * 0.85f, TextAlignment.Near, TextAlignment.Center);
                    y += RowHeight;
                }
            }
        }

        public override bool OnMouseDown(Float2 location, MouseButton button)
        {
            if (button == MouseButton.Left)
            {
                for (int i = 0; i < _groupHits.Count; i++)
                {
                    if (_groupHits[i].Rect.Contains(location))
                    {
                        string title = _groupHits[i].Title;
                        if (!_collapsedGroups.Remove(title)) _collapsedGroups.Add(title);
                        UpdateLayoutHeight();
                        return true;
                    }
                }
            }
            return base.OnMouseDown(location, button);
        }

        static void DrawGroupHeader(Style style, string title, float y, float width, bool collapsed)
        {
            var rect = new Rectangle(0, y, width, GroupHeaderHeight);
            Render2D.FillRectangle(rect, style.LightBackground * 1.15f);
            const float arrowSize = 12.0f;
            var arrowRect = new Rectangle(GroupHeaderPadding, y + (GroupHeaderHeight - arrowSize) * 0.5f, arrowSize, arrowSize);
            SpriteHandle arrow = collapsed ? style.ArrowRight : style.ArrowDown;
            if (arrow.IsValid) Render2D.DrawSprite(arrow, arrowRect, style.Foreground);
            float textX = GroupHeaderPadding + arrowSize + 4.0f;
            Render2D.DrawText(style.FontMedium, title, new Rectangle(textX, y, width - textX - GroupHeaderPadding, GroupHeaderHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
        }

        float DrawFooter(Style style, float contentBottom, float contentWidth)
        {
            if (string.IsNullOrEmpty(_regionName) && string.IsNullOrEmpty(_regionStatsLine))
                return contentBottom;
            float footerY = contentBottom + FooterTopGap;
            Render2D.FillRectangle(new Rectangle(0, footerY - 4.0f, contentWidth, 1.0f), style.Foreground * 0.12f);
            Render2D.DrawText(style.FontMedium, _regionName, new Rectangle(0, footerY, contentWidth, FooterLineHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
            var statsRect = new Rectangle(0, footerY + FooterLineHeight, contentWidth, FooterLineHeight);
            Render2D.DrawText(style.FontMedium, _regionStatsLine, statsRect, style.Foreground * 0.85f, TextAlignment.Near, TextAlignment.Center);
            return statsRect.Bottom + FooterBottomGap;
        }

        static void DrawHeader(Style style, float top, float xBar, float valueColumnWidth, float xDetail, float detailWidth, float contentWidth)
        {
            Render2D.FillRectangle(new Rectangle(0, top, contentWidth, HeaderRowHeight), style.LightBackground);
            Render2D.DrawText(style.FontMedium, "Throughput", new Rectangle(xBar, top, valueColumnWidth, HeaderRowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
            Render2D.DrawText(style.FontMedium, "Metrics", new Rectangle(xDetail, top, detailWidth, HeaderRowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
        }

        static float MeasureDetailTextWidth(Style style, string detail) => string.IsNullOrEmpty(detail) ? 0.0f : style.FontMedium.MeasureText(detail).X;

        static void DrawThroughputBar(Style style, float x, float y, float barTrackWidth, RowData row)
        {
            float barY = y + (RowHeight - BarHeight) * 0.5f;
            var bgRect = new Rectangle(x, barY, barTrackWidth, BarHeight);
            Render2D.FillRectangle(bgRect, style.Background * 1.2f);
            float fill = Mathf.Clamp((float)row.Display, 0.0f, 100.0f) * 0.01f;
            if (fill > 0.001f)
                Render2D.FillRectangle(new Rectangle(x, barY, barTrackWidth * fill, BarHeight), GetBarColor(fill * 100.0f, row.HigherIsBetter, style));
            Render2D.DrawRectangle(bgRect, style.Foreground * 0.15f);
            Render2D.DrawText(style.FontMedium, Mathf.RoundToInt((float)row.Display) + "%", new Rectangle(x + barTrackWidth + BarPercentGap, y, PercentLabelWidth, RowHeight), style.Foreground, TextAlignment.Far, TextAlignment.Center);
        }

        static void DrawCounterValue(Style style, float x, float y, float width, RowData row)
        {
            Render2D.DrawText(style.FontMedium, FormatCounterValue(row.Display), new Rectangle(x, y, width, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
        }

        static string FormatCounterValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "n/a";
            if (Math.Abs(value) >= 1000.0 || Math.Abs(value - Math.Round(value)) < 0.01)
                return value.ToString("#,0", CultureInfo.InvariantCulture);
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        static Color GetBarColor(float value, bool higherIsBetter, Style style)
        {
            if (higherIsBetter)
            {
                if (value >= 75.0f) return style.ProgressNormal;
                if (value >= 55.0f) return Color.Lerp(style.ProgressNormal, Color.Yellow, 0.45f);
                return Color.Lerp(Color.OrangeRed, Color.Yellow, 0.35f);
            }
            if (value >= 90.0f) return Color.Lerp(Color.OrangeRed, style.ProgressNormal, 0.25f);
            if (value >= 70.0f) return Color.Lerp(Color.Yellow, style.ProgressNormal, 0.35f);
            return style.ProgressNormal;
        }
    }
}
#endif
