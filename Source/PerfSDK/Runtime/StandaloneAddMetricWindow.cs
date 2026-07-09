#if !FLAX_EDITOR && USE_PROFILER

using FlaxEngine;
using FlaxEngine.GUI;
using System;
using System.Collections.Generic;

namespace NVIDIA
{
    /// <summary>
    /// Mode 2: Runtime metric picker for cooked build profiling.
    /// </summary>
    internal sealed class StandaloneAddMetricWindow
    {
        private static readonly List<StandaloneAddMetricWindow> OpenWindows = new List<StandaloneAddMetricWindow>();

        private static readonly Float2 WindowSize = new Float2(640, 520);
        private const float RowHeight = 28.0f;
        private const float ToolbarHeight = 30.0f;
        private const float FilterHeight = 24.0f;
        private const float StatusHeight = 18.0f;
        private const float Padding = 8.0f;

        private Window _window;
        private WindowRootControl _root;
        private LayoutPanel _layout;
        private StandaloneMetricsWindow _metricsWindow;
        private Label _statusLabel;
        private TextBox _filterBox;
        private Button _loadButton;
        private Panel _listScroll;
        private Panel _listContent;
        private readonly List<MetricEntry> _allMetrics = new List<MetricEntry>();
        private readonly HashSet<string> _addedMetricNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _metricsLoaded;
        private bool _catalogLoadPending;

        private struct MetricEntry
        {
            public string Name;
            public string Type;
            public string Description;
        }

        private StandaloneAddMetricWindow(StandaloneMetricsWindow metricsWindow)
        {
            _metricsWindow = metricsWindow;
            _window = PerfSDKPlatform.CreateOverlayWindow("Add PerfSDK Metric", WindowSize, topmost: false);
            if (_window == null)
            {
                Debug.LogError("PerfSDK: failed to create add-metric window.");
                return;
            }

            _root = _window.GUI;
            _root.BackgroundColor = Style.Current.Background;

            _layout = new LayoutPanel { Parent = _root };
            _layout.AnchorPreset = AnchorPresets.StretchAll;
            _layout.Offsets = Margin.Zero;

            _statusLabel = new Label
            {
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(Padding, Padding, Padding, StatusHeight),
                HorizontalAlignment = TextAlignment.Near,
                TextColor = Style.Current.Foreground * 0.75f,
                Text = "Open a GPU profiler region, then load available metrics.",
                Parent = _layout,
            };

            float toolbarY = Padding + StatusHeight + 4.0f;
            _loadButton = new Button(Padding, toolbarY, 220.0f, ToolbarHeight)
            {
                Text = "Show all available metrics",
                Parent = _layout,
            };
            _loadButton.Clicked += OnLoadMetricsClicked;

            _filterBox = new TextBox
            {
                AnchorPreset = AnchorPresets.HorizontalStretchTop,
                Offsets = new Margin(Padding + 228.0f, Padding, toolbarY + 3.0f, FilterHeight),
                WatermarkText = "Filter metrics...",
                Enabled = false,
                Parent = _layout,
            };
            _filterBox.TextChanged += OnFilterTextChanged;

            _listScroll = new Panel(ScrollBars.Vertical)
            {
                AnchorPreset = AnchorPresets.StretchAll,
                AlwaysShowScrollbars = true,
                Parent = _layout,
            };

            _listContent = new Panel(ScrollBars.None) { Parent = _listScroll };

            _layout.SizeChanged += OnLayoutSizeChanged;
            _layout.PerformLayout();

            _window.Closing += OnWindowClosing;
            OpenWindows.Add(this);
        }

        void OnWindowClosing(ClosingReason reason, ref bool cancel)
        {
            OpenWindows.Remove(this);
        }

        public static void CloseAll()
        {
            for (int i = OpenWindows.Count - 1; i >= 0; i--)
            {
                var window = OpenWindows[i]._window;
                if (window != null)
                    window.Close(ClosingReason.EngineExit);
            }
            OpenWindows.Clear();
        }

        public static void TickPendingCatalogLoads()
        {
            for (int i = 0; i < OpenWindows.Count; i++)
                OpenWindows[i].TryCompleteCatalogLoad();
        }

        void OnLayoutSizeChanged(Control control)
        {
            UpdateListContentWidth();
            if (_metricsLoaded)
                RebuildList();
        }

        void UpdateListContentWidth()
        {
            if (_listContent == null || _listScroll == null)
                return;

            _listContent.Width = Math.Max(100.0f, _listScroll.Width - 4.0f);
        }

        float GetRowWidth()
        {
            if (_listContent != null && _listContent.Width > 0)
                return _listContent.Width;
            if (_listScroll != null && _listScroll.Width > 0)
                return Math.Max(100.0f, _listScroll.Width - 4.0f);
            return Math.Max(100.0f, WindowSize.X - Padding * 2 - 4.0f);
        }

        public static void ShowPicker(StandaloneMetricsWindow metricsWindow)
        {
            if (metricsWindow == null)
                return;

            for (int i = 0; i < OpenWindows.Count; i++)
            {
                if (OpenWindows[i]._metricsWindow != metricsWindow)
                    continue;

                var existing = OpenWindows[i];
                if (existing._window != null)
                {
                    existing._window.Show();
                    existing._window.BringToFront();
                }
                return;
            }

            var picker = new StandaloneAddMetricWindow(metricsWindow);
            if (picker._window == null)
                return;

            picker._layout?.PerformLayout();
            picker.UpdateListContentWidth();
            picker._window.Show();
            picker._window.BringToFront();
            picker.BeginCatalogLoad();
        }

        void BeginCatalogLoad()
        {
            if (!RenderPerfTools.IsSessionActive)
            {
                SetStatus("Open a GPU profiler region first (double-click a bar in the GPU profiler).");
                return;
            }

            if (TryCompleteCatalogLoad())
                return;

            _catalogLoadPending = true;
            if (!RenderPerfTools.TryLoadAvailableMetrics())
            {
                SetStatus(RenderPerfTools.AvailableMetricsText);
                return;
            }

            FinishCatalogLoad();
        }

        void OnFilterTextChanged()
        {
            if (_metricsLoaded)
                RebuildList();
        }

        void OnLoadMetricsClicked()
        {
            if (!RenderPerfTools.IsSessionActive)
            {
                SetStatus("Open a GPU profiler region first (double-click a bar in the GPU profiler).");
                return;
            }

            if (TryCompleteCatalogLoad())
                return;

            _catalogLoadPending = true;
            if (!RenderPerfTools.TryLoadAvailableMetrics())
            {
                SetStatus(RenderPerfTools.AvailableMetricsText);
                return;
            }

            FinishCatalogLoad();
        }

        bool TryCompleteCatalogLoad()
        {
            if (!_catalogLoadPending)
                return false;

            if (!RenderPerfTools.TryLoadAvailableMetrics())
            {
                SetStatus(RenderPerfTools.AvailableMetricsText);
                return true;
            }

            FinishCatalogLoad();
            return true;
        }

        void FinishCatalogLoad()
        {
            _catalogLoadPending = false;
            ParseAvailableMetrics(RenderPerfTools.AvailableMetricsText);
            RefreshAddedMetricNames();
            _metricsLoaded = true;
            _filterBox.Enabled = true;
            SetStatus(string.Format("Loaded {0} metrics. Click a row to add it.", _allMetrics.Count));
            RebuildList();
        }

        void ParseAvailableMetrics(string text)
        {
            _allMetrics.Clear();
            if (string.IsNullOrEmpty(text)) return;

            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length < 2 || string.IsNullOrEmpty(parts[0])) continue;
                _allMetrics.Add(new MetricEntry { Name = parts[0], Type = parts[1], Description = parts.Length >= 3 ? parts[2] : string.Empty });
            }
        }

        void RefreshAddedMetricNames()
        {
            _addedMetricNames.Clear();
            string activeMetrics = RenderPerfTools.ActiveMetricNamesText;
            if (string.IsNullOrEmpty(activeMetrics)) return;
            string[] lines = activeMetrics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
                if (!string.IsNullOrEmpty(lines[i])) _addedMetricNames.Add(lines[i]);
        }

        void RebuildList()
        {
            _layout?.PerformLayout();
            UpdateListContentWidth();

            _listContent.DisposeChildren();
            _listContent.LockChildrenRecursive();

            string filter = _filterBox?.Text ?? string.Empty;
            float y = 0.0f;
            float rowWidth = GetRowWidth();
            int visibleCount = 0;

            for (int i = 0; i < _allMetrics.Count; i++)
            {
                MetricEntry entry = _allMetrics[i];
                if (!MatchesFilter(entry, filter)) continue;

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
            _listContent.Width = rowWidth;
            _listContent.UnlockChildrenRecursive();
            _listScroll.PerformLayout();
        }

        static bool MatchesFilter(MetricEntry entry, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return entry.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   entry.Type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void OnMetricAddClicked(string metricName)
        {
            if (!RenderPerfTools.TryAddUserMetric(metricName))
            {
                SetStatus(RenderPerfTools.ErrorText);
                return;
            }
            _addedMetricNames.Add(metricName);
            _metricsWindow?.UpdateViewPublic(expandCustomGroup: true);
            SetStatus("Queued \"" + metricName + "\" — applies on the next GPU frame.");
            RebuildList();
        }

        void SetStatus(string text)
        {
            if (_statusLabel != null) _statusLabel.Text = text ?? string.Empty;
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

            public override void Draw()
            {
                base.Draw();
                var style = Style.Current;
                var rowRect = new Rectangle(Float2.Zero, Size);
                if (_alreadyAdded) Render2D.FillRectangle(rowRect, style.LightBackground * 0.35f);
                else if (IsMouseOver) Render2D.FillRectangle(rowRect, style.BackgroundHighlighted * 0.45f);

                string title = _entry.Name;
                if (!string.IsNullOrEmpty(_entry.Type)) title += "  [" + _entry.Type + "]";

                float textWidth = Math.Max(0.0f, Width - ActionColumnWidth - 8.0f);
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

            public override void OnMouseLeave()
            {
                _leftMouseDown = false;
                base.OnMouseLeave();
            }

            public override bool OnMouseDown(Float2 location, MouseButton button)
            {
                if (button == MouseButton.Left && !_alreadyAdded) _leftMouseDown = true;
                return true;
            }

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
        }

        private sealed class LayoutPanel : Panel
        {
            protected override void PerformLayoutBeforeChildren()
            {
                base.PerformLayoutBeforeChildren();

                float toolbarY = Padding + StatusHeight + 4.0f;
                float listTop = toolbarY + ToolbarHeight + 6.0f;

                foreach (var child in Children)
                {
                    if (child is Panel scroll && scroll.ScrollBars != ScrollBars.None)
                        scroll.Offsets = new Margin(Padding, Padding, listTop, Padding);
                }
            }
        }
    }
}
#endif
