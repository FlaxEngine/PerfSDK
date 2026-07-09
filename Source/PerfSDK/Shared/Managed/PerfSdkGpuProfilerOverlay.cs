#if USE_PROFILER

using FlaxEngine;
using FlaxEngine.GUI;
using System;
using System.Collections.Generic;

namespace NVIDIA
{
    /// <summary>
    /// Stable GPU profiler overlay shared by editor and runtime (F1 toggle).
    /// Draw-based charts, timeline, and table avoid the engine profiler layout flicker.
    /// </summary>
    internal static class PerfSdkGpuProfilerOverlay
    {
        private static Window _window;
        private static WindowRootControl _root;
        private static ProfilerPanel _panel;
        private static bool _visible;
        private static Action<string, int, float, long, long, long> _openRegion;
        private static Action _shutdownMetrics;
        private static string _subtitle = "double-click a region to inspect chip metrics";

        public static bool IsVisible => _visible;

        public static void Configure(
            Action<string, int, float, long, long, long> openRegion,
            string subtitle = null,
            Action shutdownMetrics = null)
        {
            _openRegion = openRegion ?? throw new ArgumentNullException(nameof(openRegion));
            if (!string.IsNullOrEmpty(subtitle))
                _subtitle = subtitle;
            _shutdownMetrics = shutdownMetrics;
        }

        public static void Toggle()
        {
            if (_visible)
                Hide();
            else
                Show();
        }

        public static void Show()
        {
            if (_openRegion == null)
            {
                Debug.LogError("PerfSDK: GPU profiler overlay is not configured.");
                return;
            }

            if (_window == null)
                CreateWindow();

            ProfilingTools.Enabled = true;
            string api = RenderPerfToolsFormatting.FormatRendererType(GPUDevice.Instance != null ? GPUDevice.Instance.RendererType : RendererType.Unknown);
            _window.Title = "PerfSDK GPU Profiler · " + api;
            _window.Show();
            _window.BringToFront();
            _visible = true;
        }

        public static void Hide()
        {
            if (_window != null)
                _window.Hide();
            _visible = false;
        }

        public static void Shutdown()
        {
            Hide();
            _shutdownMetrics?.Invoke();
            _panel = null;
            _root = null;
            if (_window != null)
            {
                _window.Close(ClosingReason.EngineExit);
                _window = null;
            }
        }

        static void CreateWindow()
        {
            _window = PerfSDKPlatform.CreateOverlayWindow("PerfSDK GPU Profiler", new Float2(900, 700));
            if (_window == null)
            {
                Debug.LogError("PerfSDK: failed to create GPU profiler window.");
                return;
            }

            _root = _window.GUI;
            _root.BackgroundColor = Style.Current.Background;
            _panel = new ProfilerPanel(_subtitle) { Parent = _root };
            _panel.AnchorPreset = AnchorPresets.StretchAll;
            _panel.Offsets = Margin.Zero;

            _window.Closing += OnWindowClosing;
        }

        static void OnWindowClosing(ClosingReason reason, ref bool cancel)
        {
            _visible = false;
        }

        private sealed class ProfilerPanel : Panel
        {
            private const float ChartHeight = 80.0f;
            private const float TimelineHeight = 200.0f;
            private const float RowHeight = 22.0f;
            private const float HeaderHeight = 68.0f;

            private readonly string _subtitle;
            private Label _titleLabel;
            private Label _statsLabel;
            private Button _recordButton;
            private Button _clearButton;
            private ChartPanel _chartPanel;
            private Panel _timelineScroll;
            private GpuProfilerTimeline _timeline;
            private Panel _tableScroll;
            private TableContentPanel _tableContent;
            private bool _recording = true;
            private float _updateTimer;

            public ProfilerPanel(string subtitle)
            {
                _subtitle = subtitle ?? "double-click a region to inspect chip metrics";

                float chartBlockHeight = ChartHeight * 2 + 4;
                float tableTop = HeaderHeight + chartBlockHeight + 8 + TimelineHeight + 4;

                _titleLabel = new Label
                {
                    Text = "PerfSDK GPU Profiler — " + _subtitle,
                    AnchorPreset = AnchorPresets.HorizontalStretchTop,
                    Offsets = new Margin(8, 8, 8, 24),
                    Parent = this,
                };

                _recordButton = new Button(8, 36, 100, 26) { Text = "● Recording", Parent = this };
                _recordButton.Clicked += () => { _recording = !_recording; _recordButton.Text = _recording ? "● Recording" : "○ Paused"; ProfilingTools.Enabled = _recording; };

                _clearButton = new Button(116, 36, 80, 26) { Text = "Clear", Parent = this };
                _clearButton.Clicked += OnClearClicked;

                _statsLabel = new Label
                {
                    AnchorPreset = AnchorPresets.HorizontalStretchTop,
                    Offsets = new Margin(204, 8, 40, 24),
                    HorizontalAlignment = TextAlignment.Far,
                    Parent = this,
                };

                _chartPanel = new ChartPanel
                {
                    AnchorPreset = AnchorPresets.HorizontalStretchTop,
                    Offsets = new Margin(8, 8, HeaderHeight, chartBlockHeight),
                    Parent = this,
                };

                _timelineScroll = new Panel(ScrollBars.Vertical)
                {
                    AnchorPreset = AnchorPresets.HorizontalStretchTop,
                    Offsets = new Margin(8, 8, HeaderHeight + chartBlockHeight + 8, TimelineHeight),
                    Parent = this,
                };

                _timeline = new GpuProfilerTimeline
                {
                    AnchorPreset = AnchorPresets.HorizontalStretchTop,
                    Offsets = Margin.Zero,
                    Parent = _timelineScroll,
                };
                _timeline.EventDoubleClicked += OnEventDoubleClicked;

                _tableScroll = new Panel(ScrollBars.Vertical)
                {
                    AnchorPreset = AnchorPresets.StretchAll,
                    Offsets = new Margin(8, 8, tableTop, 8),
                    Parent = this,
                };

                _tableContent = new TableContentPanel { Parent = _tableScroll };
            }

            void OnClearClicked()
            {
                _chartPanel.Clear();
                _tableContent.Clear();
                _timeline.SetEvents(Array.Empty<ProfilerGPU.Event>(), 16);
            }

            protected override void PerformLayoutBeforeChildren()
            {
                base.PerformLayoutBeforeChildren();

                if (_timeline != null)
                    _timeline.Width = Math.Max(_timelineScroll.Width - 4.0f, 100.0f);

                if (_tableContent != null)
                    _tableContent.Width = Math.Max(_tableScroll.Width - 4.0f, 400.0f);
            }

            public override void Update(float deltaTime)
            {
                base.Update(deltaTime);

                _updateTimer += deltaTime;
                if (_updateTimer < 0.033f) return;
                _updateTimer = 0;

                if (!_recording) return;

                var stats = ProfilingTools.Stats;
                var events = ProfilingTools.EventsGPU;
                float gpuMs = PerfSdkGpuProfilerFilter.GetAdjustedDrawGpuTimeMs(stats.DrawGPUTimeMs, events);
                float cpuMs = PerfSdkGpuProfilerFilter.GetAdjustedDrawCpuTimeMs(stats.DrawCPUTimeMs);
                _chartPanel.AddSample(gpuMs, cpuMs);

                _statsLabel.Text = string.Format("FPS: {0}  GPU: {1:0.0} ms  CPU: {2:0.0} ms", stats.FPS, gpuMs, cpuMs);

                var filteredEvents = PerfSdkGpuProfilerFilter.FilterGpuEvents(events);
                float frameDuration = Math.Max(gpuMs, 0.001f);
                _timeline.SetEvents(filteredEvents, frameDuration);
                _tableContent.SetEvents(filteredEvents, frameDuration);
            }

            static void OnEventDoubleClicked(string name, int depth, float timeMs, long drawCalls, long triangles, long vertices)
            {
                if (PerfSdkGpuProfilerFilter.IsExcludedRegionName(name))
                    return;

                _openRegion?.Invoke(name, depth, timeMs, drawCalls, triangles, vertices);
            }
        }

        private sealed class ChartPanel : Panel
        {
            private const int MaxSamples = 300;
            private const float ChartHeight = 80.0f;

            private readonly List<float> _gpuTimes = new List<float>();
            private readonly List<float> _cpuTimes = new List<float>();

            public ChartPanel()
            : base(ScrollBars.None)
            {
            }

            public void AddSample(float gpuMs, float cpuMs)
            {
                _gpuTimes.Add(gpuMs);
                _cpuTimes.Add(cpuMs);
                if (_gpuTimes.Count > MaxSamples) _gpuTimes.RemoveAt(0);
                if (_cpuTimes.Count > MaxSamples) _cpuTimes.RemoveAt(0);
            }

            public void Clear()
            {
                _gpuTimes.Clear();
                _cpuTimes.Clear();
            }

            public override void Draw()
            {
                base.Draw();

                var style = Style.Current;
                float w = Width;
                DrawSingleChart(new Rectangle(0, 0, w, ChartHeight), "Draw (GPU)", _gpuTimes, style);
                DrawSingleChart(new Rectangle(0, ChartHeight + 2, w, ChartHeight), "Draw (CPU)", _cpuTimes, style);
            }

            static void DrawSingleChart(Rectangle bounds, string title, List<float> samples, Style style)
            {
                Render2D.FillRectangle(bounds, style.LightBackground * 0.5f);
                Render2D.DrawText(style.FontSmall, title, new Rectangle(bounds.X + 4, bounds.Y + 2, 100, 16), style.Foreground, TextAlignment.Near, TextAlignment.Center);

                if (samples.Count < 2) return;

                float maxVal = 0.001f;
                for (int i = 0; i < samples.Count; i++)
                    maxVal = Math.Max(maxVal, samples[i]);

                float graphTop = bounds.Y + 20;
                float graphHeight = bounds.Height - 24;
                float graphWidth = bounds.Width - 8;
                float xStep = graphWidth / (samples.Count - 1);

                for (int i = 1; i < samples.Count; i++)
                {
                    float x0 = bounds.X + 4 + (i - 1) * xStep;
                    float x1 = bounds.X + 4 + i * xStep;
                    float y0 = graphTop + graphHeight - (samples[i - 1] / maxVal) * graphHeight;
                    float y1 = graphTop + graphHeight - (samples[i] / maxVal) * graphHeight;
                    Render2D.DrawLine(new Float2(x0, y0), new Float2(x1, y1), style.ProgressNormal, 1.5f);
                }
            }
        }

        private sealed class TableContentPanel : Panel
        {
            private const float RowHeight = 22.0f;

            private struct TableRow
            {
                public string Name;
                public int Depth;
                public float Time;
                public float Percent;
                public long DrawCalls;
                public long Triangles;
                public long Vertices;
                public Rectangle HitRect;
            }

            private readonly List<TableRow> _rows = new List<TableRow>();
            private int _lastRowCount = -1;

            public TableContentPanel()
            : base(ScrollBars.None)
            {
            }

            public void Clear()
            {
                _rows.Clear();
                _lastRowCount = -1;
                Height = 60.0f;
            }

            public unsafe void SetEvents(ProfilerGPU.Event[] events, float frameDuration)
            {
                _rows.Clear();
                if (events != null)
                {
                    for (int i = 0; i < events.Length; i++)
                    {
                        ref var e = ref events[i];
                        if (e.Name == null || e.Depth >= 3)
                            continue;

                        float pct = frameDuration > 0 ? (e.Time / frameDuration) * 100.0f : 0;
                        _rows.Add(new TableRow
                        {
                            Name = new string(e.Name),
                            Depth = e.Depth,
                            Time = e.Time,
                            Percent = pct,
                            DrawCalls = e.Stats.DrawCalls + e.Stats.DispatchCalls,
                            Triangles = e.Stats.Triangles,
                            Vertices = e.Stats.Vertices,
                        });
                    }
                }

                float contentHeight = Math.Max(_rows.Count * RowHeight + RowHeight + 4.0f, 60.0f);
                if (_rows.Count != _lastRowCount || Math.Abs(Height - contentHeight) > 0.5f)
                {
                    _lastRowCount = _rows.Count;
                    Height = contentHeight;
                }
            }

            public override void Draw()
            {
                base.Draw();

                var style = Style.Current;
                float w = Width;
                float y = 0.0f;

                var headerRect = new Rectangle(0, y, w, RowHeight);
                Render2D.FillRectangle(headerRect, style.LightBackground);
                Render2D.DrawText(style.FontSmall, "Event", new Rectangle(4, y, w * 0.35f, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                Render2D.DrawText(style.FontSmall, "Total", new Rectangle(w * 0.35f, y, w * 0.1f, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                Render2D.DrawText(style.FontSmall, "GPU ms", new Rectangle(w * 0.45f, y, w * 0.12f, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                Render2D.DrawText(style.FontSmall, "Draws", new Rectangle(w * 0.57f, y, w * 0.12f, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                Render2D.DrawText(style.FontSmall, "Tris", new Rectangle(w * 0.69f, y, w * 0.15f, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                y += RowHeight;

                if (_rows.Count == 0)
                    return;

                Float2 localMouse = PointFromWindow(Root.MousePosition);

                for (int i = 0; i < _rows.Count; i++)
                {
                    TableRow row = _rows[i];
                    var rowRect = new Rectangle(0, y, w, RowHeight);
                    row.HitRect = rowRect;
                    _rows[i] = row;

                    if (rowRect.Contains(localMouse))
                        Render2D.FillRectangle(rowRect, style.BackgroundHighlighted * 0.4f);

                    float indent = row.Depth * 14.0f;
                    Render2D.DrawText(style.FontSmall, row.Name, new Rectangle(4 + indent, y, w * 0.35f - indent, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                    Render2D.DrawText(style.FontSmall, row.Percent.ToString("0.0") + "%", new Rectangle(w * 0.35f, y, w * 0.1f, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                    Render2D.DrawText(style.FontSmall, row.Time.ToString("0.000"), new Rectangle(w * 0.45f, y, w * 0.12f, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                    Render2D.DrawText(style.FontSmall, row.DrawCalls.ToString("#,0"), new Rectangle(w * 0.57f, y, w * 0.12f, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                    Render2D.DrawText(style.FontSmall, row.Triangles.ToString("#,0"), new Rectangle(w * 0.69f, y, w * 0.15f, RowHeight), style.Foreground, TextAlignment.Near, TextAlignment.Center);
                    y += RowHeight;
                }
            }

            public override bool OnMouseDoubleClick(Float2 location, MouseButton button)
            {
                if (button != MouseButton.Left)
                    return base.OnMouseDoubleClick(location, button);

                if (TryFindRowAt(location, out TableRow row))
                {
                    if (PerfSdkGpuProfilerFilter.IsExcludedRegionName(row.Name))
                        return true;

                    _openRegion?.Invoke(row.Name, row.Depth, row.Time, row.DrawCalls, row.Triangles, row.Vertices);
                    return true;
                }

                return base.OnMouseDoubleClick(location, button);
            }

            bool TryFindRowAt(Float2 location, out TableRow row)
            {
                row = default;
                if (_rows.Count == 0)
                    return false;

                float w = Width;
                float y = RowHeight;

                for (int i = 0; i < _rows.Count; i++)
                {
                    var rowRect = new Rectangle(0, y, w, RowHeight);
                    if (rowRect.Contains(location))
                    {
                        row = _rows[i];
                        return true;
                    }

                    y += RowHeight;
                }

                return false;
            }
        }
    }
}
#endif
