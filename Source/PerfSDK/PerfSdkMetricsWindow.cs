#if FLAX_EDITOR && USE_PROFILER
using FlaxEditor;
using FlaxEditor.GUI;
using FlaxEditor.GUI.Docking;
using FlaxEngine;
using FlaxEngine.GUI;
using NVIDIA;
using System;
using System.Globalization;

namespace FlaxEditor.Windows
{
    /// <summary>
    /// Editor window that shows live NVIDIA PerfSDK GPU metrics in a grouped table layout.
    /// </summary>
    public sealed class PerfSdkMetricsWindow : EditorWindow
    {
        private static readonly Float2 MetricsWindowSize = new Float2(460, 860);

        private const float StatusHeight = 18.0f;
        private const float AddButtonHeight = 28.0f;
        private const float AddButtonWidth = 160.0f;
        private const float HorizontalPadding = 8.0f;

        private ToolStrip _toolStrip;
        private Button _addMetricButton;
        private Label _statusLabel;
        private Panel _tableScroll;
        private ChipMetricsTable _table;
        private float _contentTop;
        private string _lastStatusText = string.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="PerfSdkMetricsWindow"/> class.
        /// </summary>
        /// <param name="editor">The editor.</param>
        public PerfSdkMetricsWindow(Editor editor)
        : base(editor, true, ScrollBars.None)
        {
            Title = "Nvidia PerfSDK Metrics";
            Size = MetricsWindowSize;

            // ToolStrip uses Flax Margin (left, right, top, bottom) where bottom = height.
            _toolStrip = new ToolStrip(34.0f)
            {
                Parent = this,
            };
            _toolStrip.AddButton(Editor.Icons.Add64, "Add metric", OnAddMetricClicked).LinkTooltip("Browse and add GPU metrics");

            _statusLabel = new Label
            {
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                HorizontalAlignment = TextAlignment.Near,
                IsScrollable = false,
                TextColor = Style.Current.Foreground * 0.75f,
                Parent = this,
            };

            _tableScroll = new Panel(ScrollBars.Vertical)
            {
                AnchorPreset = AnchorPresets.StretchAll,
                Parent = this,
            };

            _table = new ChipMetricsTable
            {
                Parent = _tableScroll,
            };

            // Add last so it draws and hit-tests above the stretch table if margins overlap during layout.
            _addMetricButton = new Button(0, 0, AddButtonWidth, AddButtonHeight)
            {
                Text = "+ Add metric",
                Parent = this,
            };
            _addMetricButton.Clicked += OnAddMetricClicked;

            PerformLayout();
        }

        /// <inheritdoc />
        protected override void PerformLayoutBeforeChildren()
        {
            base.PerformLayoutBeforeChildren();

            if (_toolStrip == null || _addMetricButton == null || _statusLabel == null || _tableScroll == null || _table == null)
                return;

            float addButtonY = _toolStrip.Bottom + 2.0f;
            _addMetricButton.Bounds = new Rectangle(HorizontalPadding, addButtonY, AddButtonWidth, AddButtonHeight);

            _contentTop = addButtonY + AddButtonHeight + 4.0f;
            _statusLabel.Offsets = new Margin(HorizontalPadding, HorizontalPadding, _contentTop, StatusHeight);
            _tableScroll.Offsets = new Margin(HorizontalPadding, HorizontalPadding, _contentTop + StatusHeight + 4.0f, HorizontalPadding);
            _table.Width = Math.Max(100.0f, _tableScroll.Width - 4.0f);
        }

        /// <inheritdoc />
        public override Float2 DefaultSize => MetricsWindowSize;

        /// <summary>
        /// Shows metrics for the given GPU profiler region.
        /// </summary>
        /// <param name="regionName">The profiler region name.</param>
        public void ShowRegion(string regionName)
        {
            _lastStatusText = string.Empty;

            string apiName = FormatRendererType(GPUDevice.Instance != null ? GPUDevice.Instance.RendererType : RendererType.Unknown);
            Title = "Nvidia PerfSDK Metrics: " + regionName + " · " + apiName;
            UpdateView();
            ApplyWindowSize();
            PerformLayout();

            // Controls default to Visible=true before first show; use IsHidden so undocked windows still open.
            if (IsHidden)
            {
                ShowFloating(GetFloatingClientSize(), WindowStartPosition.CenterParent);
            }
            else
            {
                FocusOrShow(DockState.Float);
            }
        }

        static Float2 GetFloatingClientSize(Float2 clientSize)
        {
            var dpi = (float)Platform.Dpi / 96.0f;
            return clientSize * dpi * Platform.CustomDpiScale;
        }

        Float2 GetFloatingClientSize()
        {
            return GetFloatingClientSize(Size);
        }

        void ApplyWindowSize()
        {
            Size = MetricsWindowSize;

            if (ParentDockPanel is FloatWindowDockPanel floatPanel && floatPanel.Window?.Window != null)
            {
                floatPanel.Window.Window.ClientSize = GetFloatingClientSize(MetricsWindowSize);
                floatPanel.Window.PerformLayout();
                PerformLayout();
            }
        }

        /// <summary>
        /// Refreshes the window contents from the active PerfSDK session.
        /// </summary>
        /// <param name="expandCustomGroup">When true, ensures the Custom group is expanded.</param>
        public void UpdateViewPublic(bool expandCustomGroup = false)
        {
            UpdateView(expandCustomGroup);
        }

        private void OnAddMetricClicked()
        {
            PerfSdkAddMetricWindow.ShowPicker(Editor, this);
        }

        /// <inheritdoc />
        public override void OnExit()
        {
            if (RenderPerfTools.IsSessionActive)
                RenderPerfTools.CloseRegionProfiler();
        }

        private void UpdateView(bool expandCustomGroup = false)
        {
            string statusText = RenderPerfTools.StatusText;
            if (!string.Equals(_lastStatusText, statusText, StringComparison.Ordinal))
            {
                _statusLabel.Text = statusText;
                _lastStatusText = statusText ?? string.Empty;
            }

            string regionName = RenderPerfTools.RegionName;
            float gpuTimeMs = RenderPerfTools.RegionGpuTimeMs;
            long drawCalls = RenderPerfTools.RegionDrawCalls;
            long triangles = RenderPerfTools.RegionTriangles;
            long vertices = RenderPerfTools.RegionVertices;
            TryGetLiveRegionStats(regionName, ref gpuTimeMs, ref drawCalls, ref triangles, ref vertices);

            _table.SetRegionStats(regionName, gpuTimeMs, drawCalls, triangles, vertices);

            string chipAreas = RenderPerfTools.ChipAreasText;
            _table.SetChipAreasText(chipAreas, expandCustomGroup);
            _table.SetEmptyMessage(string.IsNullOrEmpty(chipAreas) ? statusText : "Collecting GPU samples...");
        }

        private static unsafe void TryGetLiveRegionStats(string regionName, ref float gpuTimeMs, ref long drawCalls, ref long triangles, ref long vertices)
        {
            if (string.IsNullOrEmpty(regionName))
                return;

            var events = ProfilingTools.EventsGPU;
            if (events == null || events.Length == 0)
                return;

            for (int i = 0; i < events.Length; i++)
            {
                ref var e = ref events[i];
                if (e.Name == null)
                    continue;

                if (!String.Equals(regionName, new string(e.Name), StringComparison.Ordinal))
                    continue;

                gpuTimeMs = e.Time;
                drawCalls = e.Stats.DrawCalls + e.Stats.DispatchCalls;
                triangles = e.Stats.Triangles;
                vertices = e.Stats.Vertices;
                return;
            }
        }

        private static string FormatRendererType(RendererType type)
        {
            switch (type)
            {
            case RendererType.DirectX10: return "DirectX 10";
            case RendererType.DirectX10_1: return "DirectX 10.1";
            case RendererType.DirectX11: return "DirectX 11";
            case RendererType.DirectX12: return "DirectX 12";
            case RendererType.Vulkan: return "Vulkan";
            case RendererType.OpenGL4_1: return "OpenGL 4.1";
            case RendererType.OpenGL4_4: return "OpenGL 4.4";
            case RendererType.OpenGLES3: return "OpenGL ES 3";
            case RendererType.OpenGLES3_1: return "OpenGL ES 3.1";
            case RendererType.Null: return "Null";
            case RendererType.WebGPU: return "WebGPU";
            default: return type.ToString();
            }
        }

        private sealed class ChipMetricsTable : ContainerControl
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

            private enum RowKind
            {
                GroupHeader,
                Metric,
            }

            private enum MetricDisplayFormat
            {
                Percent = 0,
                Warps = 1,
                Pixels = 2,
                Bandwidth = 3,
                Time = 4,
                Counter = 5,
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
            private readonly System.Collections.Generic.HashSet<string> _collapsedGroups = new System.Collections.Generic.HashSet<string>();
            private readonly System.Collections.Generic.List<GroupHeaderHit> _groupHits = new System.Collections.Generic.List<GroupHeaderHit>();

            public void SetRegionStats(string regionName, float gpuTimeMs, long drawCalls, long triangles, long vertices)
            {
                regionName ??= string.Empty;
                string statsLine = string.Format(
                    "GPU time: {0:0.000} ms   Draw calls: {1:#,0}   Triangles: {2:#,0}   Vertices: {3:#,0}",
                    gpuTimeMs,
                    drawCalls,
                    triangles,
                    vertices);

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
                        rows[count++] = new RowData
                        {
                            Kind = RowKind.GroupHeader,
                            Title = parts[1],
                        };
                        continue;
                    }

                    if (parts.Length < 3 || !double.TryParse(parts[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double target))
                        continue;

                    bool higherIsBetter = parts.Length >= 4 && parts[3] == "1";
                    var format = MetricDisplayFormat.Percent;
                    if (parts.Length >= 5 && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int formatCode) &&
                        Enum.IsDefined(typeof(MetricDisplayFormat), formatCode))
                    {
                        format = (MetricDisplayFormat)formatCode;
                    }

                    int metricIndex = -1;
                    if (parts.Length >= 6)
                        int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out metricIndex);

                    rows[count++] = new RowData
                    {
                        Kind = RowKind.Metric,
                        Title = parts[0],
                        MetricIndex = metricIndex,
                        Target = target,
                        Display = target,
                        Detail = parts[2],
                        HigherIsBetter = higherIsBetter,
                        Format = format,
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
                if (_rows.Length == 0)
                    return false;

                string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                int rowIndex = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length < 2)
                        continue;

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
                    if (metricIndex >= 0)
                    {
                        if (row.MetricIndex != metricIndex)
                            return false;
                    }
                    else if (row.Title != parts[0])
                    {
                        return false;
                    }

                    bool higherIsBetter = parts.Length >= 4 && parts[3] == "1";
                    var format = MetricDisplayFormat.Percent;
                    if (parts.Length >= 5 && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int formatCode) &&
                        Enum.IsDefined(typeof(MetricDisplayFormat), formatCode))
                    {
                        format = (MetricDisplayFormat)formatCode;
                    }

                    row.Target = target;
                    row.Display = target;
                    row.Detail = parts[2];
                    row.HigherIsBetter = higherIsBetter;
                    row.Format = format;
                    if (metricIndex >= 0)
                        row.MetricIndex = metricIndex;
                    _rows[rowIndex] = row;
                    rowIndex++;
                }

                if (rowIndex != _rows.Length)
                    return false;

                return true;
            }

            float GetValueColumnWidth(float contentWidth)
            {
                return contentWidth * BarTrackRatio + PercentLabelWidth + BarPercentGap;
            }

            float GetBarTrackWidth(float contentWidth, float valueColumnWidth)
            {
                return Math.Max(0.0f, valueColumnWidth - PercentLabelWidth - BarPercentGap);
            }

            void UpdateLayoutHeight()
            {
                float newHeight = ComputeContentHeight();
                if (Math.Abs(newHeight - _lastLayoutHeight) < 0.5f)
                    return;

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

                bool currentGroupCollapsed = false;
                for (int i = 0; i < _rows.Length; i++)
                {
                    RowData row = _rows[i];
                    if (row.Kind == RowKind.GroupHeader)
                    {
                        currentGroupCollapsed = _collapsedGroups.Contains(row.Title);
                        y += GroupHeaderHeight;
                        continue;
                    }

                    if (currentGroupCollapsed)
                        continue;

                    y += RowHeight;
                }

                return Math.Max(y + 10.0f, 100.0f);
            }

            /// <inheritdoc />
            public override void Update(float deltaTime)
            {
                base.Update(deltaTime);
            }

            /// <inheritdoc />
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
                    var emptyRect = new Rectangle(0, HeaderRowHeight, contentWidth, RowHeight);
                    Render2D.DrawText(style.FontMedium, _emptyMessage, emptyRect, style.ForegroundGrey, TextAlignment.Near, TextAlignment.Center);
                    DrawFooter(style, HeaderRowHeight + RowHeight, contentWidth);
                }
                else
                {
                    _groupHits.Clear();
                    bool currentGroupCollapsed = false;
                    for (int i = 0; i < _rows.Length; i++)
                    {
                        RowData row = _rows[i];
                        if (row.Kind == RowKind.GroupHeader)
                        {
                            currentGroupCollapsed = _collapsedGroups.Contains(row.Title);
                            DrawGroupHeader(style, row.Title, y, contentWidth, currentGroupCollapsed);
                            _groupHits.Add(new GroupHeaderHit { Title = row.Title, Rect = new Rectangle(0, y, contentWidth, GroupHeaderHeight) });
                            y += GroupHeaderHeight;
                            continue;
                        }

                        // Collapsed groups keep their metric rows in the data set but consume no layout space.
                        if (currentGroupCollapsed)
                            continue;

                        if (IsMouseOver)
                        {
                            var hoverRect = new Rectangle(0, y, contentWidth, RowHeight);
                            Render2D.FillRectangle(hoverRect, style.BackgroundHighlighted * 0.35f);
                        }

                        if (row.ShowBar)
                            DrawThroughputBar(style, xBar, y, barTrackWidth, row);
                        else
                            DrawCounterValue(style, xBar, y, valueColumnWidth, row);

                        var detailRect = new Rectangle(xDetail, y, Math.Max(detailWidth, MeasureDetailTextWidth(style, row.Detail) + 4.0f), RowHeight);
                        Render2D.DrawText(style.FontMedium, row.Detail, detailRect, style.Foreground * 0.85f, TextAlignment.Near, TextAlignment.Center);
                        y += RowHeight;
                    }

                    //DrawFooter(style, y, contentWidth);
                }
            }

            /// <inheritdoc />
            public override bool OnMouseDown(Float2 location, MouseButton button)
            {
                if (button == MouseButton.Left)
                {
                    for (int i = 0; i < _groupHits.Count; i++)
                    {
                        if (_groupHits[i].Rect.Contains(location))
                        {
                            string title = _groupHits[i].Title;
                            if (!_collapsedGroups.Remove(title))
                                _collapsedGroups.Add(title);
                            UpdateLayoutHeight();
                            return true;
                        }
                    }
                }

                return base.OnMouseDown(location, button);
            }

            private static void DrawGroupHeader(Style style, string title, float y, float width, bool collapsed)
            {
                var rect = new Rectangle(0, y, width, GroupHeaderHeight);
                Render2D.FillRectangle(rect, style.LightBackground * 1.15f);

                const float arrowSize = 12.0f;
                var arrowRect = new Rectangle(GroupHeaderPadding, y + (GroupHeaderHeight - arrowSize) * 0.5f, arrowSize, arrowSize);
                SpriteHandle arrow = collapsed ? style.ArrowRight : style.ArrowDown;
                if (arrow.IsValid)
                    Render2D.DrawSprite(arrow, arrowRect, style.Foreground);

                float textX = GroupHeaderPadding + arrowSize + 4.0f;
                Render2D.DrawText(
                    style.FontMedium,
                    title,
                    new Rectangle(textX, y, width - textX - GroupHeaderPadding, GroupHeaderHeight),
                    style.Foreground,
                    TextAlignment.Near,
                    TextAlignment.Center);
            }

            private float DrawFooter(Style style, float contentBottom, float contentWidth)
            {
                if (string.IsNullOrEmpty(_regionName) && string.IsNullOrEmpty(_regionStatsLine))
                    return contentBottom;

                float footerY = contentBottom + FooterTopGap;
                var separatorRect = new Rectangle(0, footerY - 4.0f, contentWidth, 1.0f);
                Render2D.FillRectangle(separatorRect, style.Foreground * 0.12f);

                var regionRect = new Rectangle(0, footerY, contentWidth, FooterLineHeight);
                Render2D.DrawText(style.FontMedium, _regionName, regionRect, style.Foreground, TextAlignment.Near, TextAlignment.Center);

                var statsRect = new Rectangle(0, footerY + FooterLineHeight, contentWidth, FooterLineHeight);
                Render2D.DrawText(style.FontMedium, _regionStatsLine, statsRect, style.Foreground * 0.85f, TextAlignment.Near, TextAlignment.Center);

                return statsRect.Bottom + FooterBottomGap;
            }

            private static void DrawHeader(Style style, float top, float xBar, float valueColumnWidth, float xDetail, float detailWidth, float contentWidth)
            {
                var headerBg = new Rectangle(0, top, contentWidth, HeaderRowHeight);
                Render2D.FillRectangle(headerBg, style.LightBackground);

                Render2D.DrawText(style.FontMedium, "Throughput", new Rectangle(xBar, top, valueColumnWidth, HeaderRowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                Render2D.DrawText(style.FontMedium, "Metrics", new Rectangle(xDetail, top, detailWidth, HeaderRowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
            }

            private static float MeasureDetailTextWidth(Style style, string detail)
            {
                if (string.IsNullOrEmpty(detail))
                    return 0.0f;

                return style.FontMedium.MeasureText(detail).X;
            }

            private static void DrawThroughputBar(Style style, float x, float y, float barTrackWidth, RowData row)
            {
                float barY = y + (RowHeight - BarHeight) * 0.5f;
                var bgRect = new Rectangle(x, barY, barTrackWidth, BarHeight);
                Render2D.FillRectangle(bgRect, style.Background * 1.2f);

                float fill = Mathf.Clamp((float)row.Display, 0.0f, 100.0f) * 0.01f;
                if (fill > 0.001f)
                {
                    var fillRect = new Rectangle(x, barY, barTrackWidth * fill, BarHeight);
                    Render2D.FillRectangle(fillRect, GetBarColor(fill * 100.0f, row.HigherIsBetter, style));
                }

                Render2D.DrawRectangle(bgRect, style.Foreground * 0.15f);

                var percentRect = new Rectangle(x + barTrackWidth + BarPercentGap, y, PercentLabelWidth, RowHeight);
                Render2D.DrawText(style.FontMedium, Mathf.RoundToInt((float)row.Display) + "%", percentRect, style.Foreground, TextAlignment.Far, TextAlignment.Center);
            }

            private static void DrawCounterValue(Style style, float x, float y, float width, RowData row)
            {
                var valueRect = new Rectangle(x, y, width, RowHeight);
                Render2D.DrawText(style.FontMedium, FormatCounterValue(row.Display), valueRect, style.Foreground, TextAlignment.Near, TextAlignment.Center);
            }

            private static string FormatCounterValue(double value)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    return "n/a";

                if (Math.Abs(value) >= 1000.0 || Math.Abs(value - Math.Round(value)) < 0.01)
                    return value.ToString("#,0", CultureInfo.InvariantCulture);

                return value.ToString("0.####", CultureInfo.InvariantCulture);
            }

            private static Color GetBarColor(float value, bool higherIsBetter, Style style)
            {
                if (higherIsBetter)
                {
                    if (value >= 75.0f)
                        return style.ProgressNormal;
                    if (value >= 55.0f)
                        return Color.Lerp(style.ProgressNormal, Color.Yellow, 0.45f);
                    return Color.Lerp(Color.OrangeRed, Color.Yellow, 0.35f);
                }

                if (value >= 90.0f)
                    return Color.Lerp(Color.OrangeRed, style.ProgressNormal, 0.25f);
                if (value >= 70.0f)
                    return Color.Lerp(Color.Yellow, style.ProgressNormal, 0.35f);
                return style.ProgressNormal;
            }
        }
    }
}
#endif
