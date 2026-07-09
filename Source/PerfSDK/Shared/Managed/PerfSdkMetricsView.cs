#if USE_PROFILER
using FlaxEngine;
using FlaxEngine.GUI;
using System;

namespace NVIDIA
{
    /// <summary>
    /// Shared metrics panel used by both editor and runtime windows.
    /// Layout matches the in-editor PerfSdkMetricsWindow exactly.
    /// </summary>
    internal sealed class PerfSdkMetricsView : ContainerControl
    {
        public static readonly Float2 DefaultSize = new Float2(460, 860);

        private const float StatusHeight = 18.0f;
        private const float AddButtonHeight = 28.0f;
        private const float AddButtonWidth = 160.0f;
        private const float HorizontalPadding = 8.0f;

        private Button _addMetricButton;
        private Label _statusLabel;
        private Panel _tableScroll;
        private PerfSdkChipMetricsTable _table;
        private float _contentTop;
        private string _lastStatusText = string.Empty;
        private uint _lastMetricsRevision;
        private float _lastLiveStatsRefreshTime;
        private const float LiveStatsRefreshInterval = 0.5f;

        public event Action AddMetricClicked;

        public PerfSdkMetricsView()
        {
            AnchorPreset = AnchorPresets.StretchAll;
            Offsets = Margin.Zero;

            _addMetricButton = new Button(0, 0, AddButtonWidth, AddButtonHeight)
            {
                Text = "+ Add metric",
                TooltipText = "Browse and add GPU metrics",
                Parent = this,
            };
            _addMetricButton.Clicked += OnAddMetricClicked;

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

            _table = new PerfSdkChipMetricsTable { Parent = _tableScroll };
        }

        protected override void PerformLayoutBeforeChildren()
        {
            base.PerformLayoutBeforeChildren();

            if (_addMetricButton == null || _statusLabel == null || _tableScroll == null || _table == null)
                return;

            float addButtonY = HorizontalPadding;
            _addMetricButton.Bounds = new Rectangle(HorizontalPadding, addButtonY, AddButtonWidth, AddButtonHeight);

            _contentTop = addButtonY + AddButtonHeight + 4.0f;
            _statusLabel.Offsets = new Margin(HorizontalPadding, HorizontalPadding, _contentTop, StatusHeight);
            _tableScroll.Offsets = new Margin(HorizontalPadding, HorizontalPadding, _contentTop + StatusHeight + 4.0f, HorizontalPadding);
            _table.Width = Math.Max(100.0f, _tableScroll.Width - 4.0f);
        }

        public void UpdateView(bool expandCustomGroup = false)
        {
            string statusText = RenderPerfTools.StatusText;
            if (!string.Equals(_lastStatusText, statusText, StringComparison.Ordinal))
            {
                _statusLabel.Text = statusText;
                _lastStatusText = statusText ?? string.Empty;
            }

            if (RenderPerfTools.IsSessionActive)
                RenderPerfTools.TickSession();

            uint revision = RenderPerfTools.MetricsRevision;
            if (!expandCustomGroup && revision == _lastMetricsRevision)
                return;
            _lastMetricsRevision = revision;

            string regionName = RenderPerfTools.RegionName;
            float gpuTimeMs = RenderPerfTools.RegionGpuTimeMs;
            long drawCalls = RenderPerfTools.RegionDrawCalls;
            long triangles = RenderPerfTools.RegionTriangles;
            long vertices = RenderPerfTools.RegionVertices;

            float now = Time.TimeSinceStartup;
            if (now - _lastLiveStatsRefreshTime >= LiveStatsRefreshInterval)
            {
                RenderPerfToolsFormatting.TryGetLiveRegionStats(regionName, ref gpuTimeMs, ref drawCalls, ref triangles, ref vertices);
                _lastLiveStatsRefreshTime = now;
            }

            _table.SetRegionStats(regionName, gpuTimeMs, drawCalls, triangles, vertices);
            string chipAreas = RenderPerfTools.ChipAreasText;
            _table.SetChipAreasText(chipAreas, expandCustomGroup);
            _table.SetEmptyMessage(string.IsNullOrEmpty(chipAreas) ? statusText : "Collecting GPU samples...");
        }

        public void ResetStatus()
        {
            _lastStatusText = string.Empty;
            _lastMetricsRevision = 0;
            _lastLiveStatsRefreshTime = 0.0f;
        }

        void OnAddMetricClicked()
        {
            AddMetricClicked?.Invoke();
        }
    }
}
#endif
