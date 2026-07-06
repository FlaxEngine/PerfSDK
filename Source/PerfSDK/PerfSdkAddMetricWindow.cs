#if FLAX_EDITOR && USE_PROFILER
using FlaxEditor;
using FlaxEditor.GUI.Docking;
using FlaxEngine;
using FlaxEngine.GUI;
using NVIDIA;
using System;
using System.Collections.Generic;

namespace FlaxEditor.Windows
{
    /// <summary>
    /// Floating picker that lets users browse and add NVIDIA PerfSDK metrics at runtime.
    /// </summary>
    public sealed class PerfSdkAddMetricWindow : EditorWindow
    {
        private static readonly Float2 WindowSize = new Float2(640, 520);

        private const float RowHeight = 28.0f;
        private const float ToolbarHeight = 30.0f;
        private const float FilterHeight = 24.0f;
        private const float StatusHeight = 18.0f;
        private const float Padding = 8.0f;

        private readonly PerfSdkMetricsWindow _metricsWindow;

        private Label _statusLabel;
        private TextBox _filterBox;
        private Button _loadButton;
        private Panel _listScroll;
        private Panel _listContent;

        private readonly List<MetricEntry> _allMetrics = new List<MetricEntry>();
        private readonly HashSet<string> _addedMetricNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _metricsLoaded;

        private struct MetricEntry
        {
            public string Name;
            public string Type;
            public string Description;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PerfSdkAddMetricWindow"/> class.
        /// </summary>
        /// <param name="editor">The editor.</param>
        /// <param name="metricsWindow">The main metrics window to refresh after adding a metric.</param>
        public PerfSdkAddMetricWindow(Editor editor, PerfSdkMetricsWindow metricsWindow)
        : base(editor, true, ScrollBars.None)
        {
            _metricsWindow = metricsWindow;
            Title = "Add PerfSDK Metric";
            Size = WindowSize;

            _statusLabel = new Label
            {
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(Padding, Padding, Padding, StatusHeight),
                HorizontalAlignment = TextAlignment.Near,
                TextColor = Style.Current.Foreground * 0.75f,
                Text = "Open a GPU profiler region, then load available metrics.",
                Parent = this,
            };

            float toolbarY = Padding + StatusHeight + 4.0f;
            _loadButton = new Button(Padding, toolbarY, 220.0f, ToolbarHeight)
            {
                Text = "Show all available metrics",
                Parent = this,
            };
            _loadButton.Clicked += OnLoadMetricsClicked;

            _filterBox = new TextBox
            {
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(Padding + 228.0f, Padding, toolbarY + 3.0f, FilterHeight),
                WatermarkText = "Filter metrics...",
                Enabled = false,
                Parent = this,
            };
            _filterBox.TextChanged += OnFilterChanged;

            _listScroll = new Panel(ScrollBars.Vertical)
            {
                AnchorPreset = AnchorPresets.StretchAll,
                AlwaysShowScrollbars = true,
                Parent = this,
            };

            _listContent = new Panel(ScrollBars.None)
            {
                Parent = _listScroll,
            };

            SizeChanged += OnWindowSizeChanged;
            PerformLayout();
        }

        /// <inheritdoc />
        protected override void PerformLayoutBeforeChildren()
        {
            base.PerformLayoutBeforeChildren();

            if (_statusLabel == null || _loadButton == null || _filterBox == null || _listScroll == null)
                return;

            float toolbarY = Padding + StatusHeight + 4.0f;
            _statusLabel.Offsets = new Margin(Padding, Padding, Padding, StatusHeight);
            _loadButton.Bounds = new Rectangle(Padding, toolbarY, 220.0f, ToolbarHeight);
            _filterBox.Offsets = new Margin(Padding + 228.0f, Padding, toolbarY + 3.0f, FilterHeight);

            float listTop = toolbarY + ToolbarHeight + 6.0f;
            _listScroll.Offsets = new Margin(Padding, Padding, listTop, Padding);
            if (_listContent != null)
                _listContent.Width = Math.Max(100.0f, _listScroll.Width - 4.0f);
        }

        void OnWindowSizeChanged(Control control)
        {
            if (_metricsLoaded)
                RebuildList();
        }

        /// <inheritdoc />
        public override Float2 DefaultSize => WindowSize;

        /// <summary>
        /// Opens or focuses the add-metric picker.
        /// </summary>
        /// <param name="editor">The editor.</param>
        /// <param name="metricsWindow">The metrics window to refresh after changes.</param>
        public static void ShowPicker(Editor editor, PerfSdkMetricsWindow metricsWindow)
        {
            var window = new PerfSdkAddMetricWindow(editor, metricsWindow);
            window.ShowFloating(window.GetFloatingClientSize(), WindowStartPosition.CenterParent);
        }

        /// <inheritdoc />
        protected override void OnClose()
        {
            HideOnClose = false;
            base.OnClose();
        }

        /// <inheritdoc />
        public override void OnDestroy()
        {
            _listContent?.DisposeChildren();
            base.OnDestroy();
        }

        Float2 GetFloatingClientSize()
        {
            var dpi = (float)Platform.Dpi / 96.0f;
            return WindowSize * dpi * Platform.CustomDpiScale;
        }

        void OnLoadMetricsClicked()
        {
            if (!RenderPerfTools.IsSessionActive)
            {
                SetStatus("Open a GPU profiler region first (click a bar in Profiler → GPU).");
                return;
            }

            if (!RenderPerfTools.TryLoadAvailableMetrics())
            {
                SetStatus(RenderPerfTools.AvailableMetricsText);
                return;
            }

            ParseAvailableMetrics(RenderPerfTools.AvailableMetricsText);
            RefreshAddedMetricNames();
            _metricsLoaded = true;
            _filterBox.Enabled = true;
            _filterBox.Focus();
            SetStatus(string.Format("Loaded {0} metrics. Click a row to add it.", _allMetrics.Count));
            RebuildList();
        }

        void OnFilterChanged()
        {
            if (_metricsLoaded)
                RebuildList();
        }

        void ParseAvailableMetrics(string text)
        {
            _allMetrics.Clear();
            if (string.IsNullOrEmpty(text))
                return;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]))
                    continue;

                _allMetrics.Add(new MetricEntry
                {
                    Name = parts[0],
                    Type = parts[1],
                    Description = parts.Length >= 3 ? parts[2] : string.Empty,
                });
            }
        }

        void RefreshAddedMetricNames()
        {
            _addedMetricNames.Clear();

            string activeMetrics = RenderPerfTools.ActiveMetricNamesText;
            if (!string.IsNullOrEmpty(activeMetrics))
            {
                string[] lines = activeMetrics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!string.IsNullOrEmpty(lines[i]))
                        _addedMetricNames.Add(lines[i]);
                }
            }
        }

        void RebuildList()
        {
            _listContent.DisposeChildren();
            _listContent.LockChildrenRecursive();

            string filter = _filterBox?.Text ?? string.Empty;
            float y = 0.0f;
            float rowWidth = Math.Max(100.0f, _listContent.Width - 4.0f);
            int visibleCount = 0;

            for (int i = 0; i < _allMetrics.Count; i++)
            {
                MetricEntry entry = _allMetrics[i];
                if (!MatchesFilter(entry, filter))
                    continue;

                bool alreadyAdded = _addedMetricNames.Contains(entry.Name);
                var row = new MetricRowControl(entry, alreadyAdded, rowWidth, RowHeight)
                {
                    Bounds = new Rectangle(0, y, rowWidth, RowHeight),
                    Parent = _listContent,
                };
                row.AddClicked += OnMetricAddClicked;
                y += RowHeight + 2.0f;
                visibleCount++;
            }

            if (visibleCount == 0)
            {
                var empty = new Label
                {
                    Location = Float2.Zero,
                    Size = new Float2(rowWidth, RowHeight),
                    HorizontalAlignment = TextAlignment.Center,
                    Text = _metricsLoaded ? "No metrics match the filter." : "Click \"Show all available metrics\" to browse.",
                    Parent = _listContent,
                };
                y = RowHeight;
            }

            _listContent.Height = Math.Max(y, RowHeight);
            _listContent.UnlockChildrenRecursive();
            _listScroll.PerformLayout();
        }

        static bool MatchesFilter(MetricEntry entry, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;

            return entry.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   entry.Type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (!string.IsNullOrEmpty(entry.Description) && entry.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void OnMetricAddClicked(string metricName)
        {
            if (string.IsNullOrEmpty(metricName))
                return;

            if (!RenderPerfTools.TryAddUserMetric(metricName))
            {
                SetStatus(RenderPerfTools.ErrorText);
                return;
            }

            _addedMetricNames.Add(metricName);
            _metricsWindow?.UpdateViewPublic(expandCustomGroup: true);
            SetStatus("Added \"" + metricName + "\" — see Custom group in the main window.");
            RebuildList();
        }

        void SetStatus(string text)
        {
            if (_statusLabel != null)
                _statusLabel.Text = text ?? string.Empty;
        }

        private sealed class MetricRowControl : Control
        {
            private const float ActionColumnWidth = 56.0f;

            public event Action<string> AddClicked;

            private readonly MetricEntry _entry;
            private readonly bool _alreadyAdded;
            private bool _leftMouseDown;

            public MetricRowControl(MetricEntry entry, bool alreadyAdded, float width, float height)
            : base(0, 0, width, height)
            {
                AutoFocus = false;
                _entry = entry;
                _alreadyAdded = alreadyAdded;
            }

            /// <inheritdoc />
            public override void Draw()
            {
                base.Draw();

                var style = Style.Current;
                var rowRect = new Rectangle(Float2.Zero, Size);

                if (_alreadyAdded)
                    Render2D.FillRectangle(rowRect, style.LightBackground * 0.35f);
                else if (IsMouseOver)
                    Render2D.FillRectangle(rowRect, style.BackgroundHighlighted * 0.45f);

                float textWidth = Math.Max(0.0f, Width - ActionColumnWidth - 8.0f);
                string title = _entry.Name;
                if (!string.IsNullOrEmpty(_entry.Type))
                    title += "  [" + _entry.Type + "]";

                Render2D.DrawText(style.FontMedium, title, new Rectangle(4, 0, textWidth, Height * 0.55f), style.Foreground, TextAlignment.Near, TextAlignment.Center);

                if (!string.IsNullOrEmpty(_entry.Description))
                {
                    string detail = _entry.Description;
                    if (detail.Length > 96)
                        detail = detail.Substring(0, 93) + "...";
                    Render2D.DrawText(style.FontSmall, detail, new Rectangle(4, Height * 0.45f, textWidth, Height * 0.55f), style.ForegroundGrey, TextAlignment.Near, TextAlignment.Center);
                }

                var actionRect = new Rectangle(Width - ActionColumnWidth - 2.0f, 2.0f, ActionColumnWidth, Height - 4.0f);
                if (_alreadyAdded)
                {
                    Render2D.FillRectangle(actionRect, style.LightBackground);
                    Render2D.DrawText(style.FontMedium, "Added", actionRect, style.ForegroundGrey, TextAlignment.Center, TextAlignment.Center);
                }
                else
                {
                    var fill = IsMouseOver ? style.BackgroundHighlighted : style.LightBackground;
                    Render2D.FillRectangle(actionRect, fill * 1.1f);
                    Render2D.DrawRectangle(actionRect, style.Foreground * 0.2f);
                    Render2D.DrawText(style.FontMedium, "+ Add", actionRect, style.Foreground, TextAlignment.Center, TextAlignment.Center);
                }
            }

            /// <inheritdoc />
            public override bool OnMouseDown(Float2 location, MouseButton button)
            {
                if (button == MouseButton.Left && !_alreadyAdded)
                    _leftMouseDown = true;

                return true;
            }

            /// <inheritdoc />
            public override bool OnMouseUp(Float2 location, MouseButton button)
            {
                if (button == MouseButton.Left && _leftMouseDown && !_alreadyAdded)
                {
                    _leftMouseDown = false;
                    AddClicked?.Invoke(_entry.Name);
                    return true;
                }

                return base.OnMouseUp(location, button);
            }

            /// <inheritdoc />
            public override void OnMouseLeave()
            {
                _leftMouseDown = false;
                base.OnMouseLeave();
            }
        }
    }
}
#endif
