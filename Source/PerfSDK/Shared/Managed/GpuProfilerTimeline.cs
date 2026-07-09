#if USE_PROFILER

using FlaxEngine;
using FlaxEngine.GUI;
using System;
using System.Collections.Generic;

namespace NVIDIA
{
    /// <summary>
    /// GPU timeline with clickable event bars. Renders directly to avoid layout thrashing.
    /// Uses depth-based rows like the engine editor GPU profiler timeline.
    /// </summary>
    internal sealed class GpuProfilerTimeline : ContainerControl
    {
        private static readonly Color[] EventColors =
        {
            new Color(0.8f, 0.894f, 0.710f), new Color(0.125f, 0.698f, 0.667f),
            new Color(0.483f, 0.621f, 0.022f), new Color(0.383f, 0.289f, 0.524f),
            new Color(0.8f, 0.442f, 0f), new Color(0.449f, 0.408f, 0.050f),
            new Color(0.207f, 0.533f, 0.656f), new Color(0.775f, 0.637f, 0.025f),
            new Color(0.533f, 0.16f, 0.028f), new Color(0.941f, 0.502f, 0.502f),
        };

        public const float EventHeight = 22.0f;
        public const float EventSpacing = 2.0f;

        public event Action<string, int, float, long, long, long> EventDoubleClicked;

        private readonly List<TimelineEntry> _entries = new List<TimelineEntry>();
        private readonly List<HitRect> _hitRects = new List<HitRect>();
        private float _frameDurationMs = 16.0f;
        private int _lastMaxDepth = -1;

        private struct TimelineEntry
        {
            public string Name;
            public int Depth;
            public float StartMs;
            public float DurationMs;
            public float Time;
            public long DrawCalls;
            public long Triangles;
            public long Vertices;
            public int ColorIndex;
        }

        private struct HitRect
        {
            public Rectangle Rect;
            public int EntryIndex;
        }

        public unsafe void SetEvents(ProfilerGPU.Event[] events, float frameDurationMs)
        {
            _frameDurationMs = Math.Max(frameDurationMs, 0.001f);
            _entries.Clear();
            int maxDepth = 0;

            if (events != null && events.Length > 0)
            {
                var depthStack = new float[32];
                int colorCounter = 0;

                for (int i = 0; i < events.Length; i++)
                {
                    ref var e = ref events[i];
                    if (e.Name == null)
                        continue;

                    int depth = Math.Clamp(e.Depth, 0, 31);
                    float start = depthStack[depth];
                    float duration = Math.Max(e.Time, 0.001f);

                    _entries.Add(new TimelineEntry
                    {
                        Name = new string(e.Name),
                        Depth = depth,
                        StartMs = start,
                        DurationMs = duration,
                        Time = e.Time,
                        DrawCalls = e.Stats.DrawCalls + e.Stats.DispatchCalls,
                        Triangles = e.Stats.Triangles,
                        Vertices = e.Stats.Vertices,
                        ColorIndex = colorCounter++,
                    });

                    depthStack[depth] = start + duration;
                    maxDepth = Math.Max(maxDepth, depth);
                }
            }

            if (maxDepth != _lastMaxDepth)
            {
                _lastMaxDepth = maxDepth;
                UpdateContentHeight(maxDepth);
            }
        }

        void UpdateContentHeight(int maxDepth)
        {
            float rowSpan = EventHeight + EventSpacing;
            Height = Math.Max((maxDepth + 1) * rowSpan + 8.0f, 60.0f);
        }

        public override void Draw()
        {
            base.Draw();

            var style = Style.Current;
            float contentWidth = Math.Max(Width - 8.0f, 100.0f);
            _hitRects.Clear();

            if (_entries.Count == 0)
            {
                Render2D.DrawText(style.FontSmall, "No GPU events recorded. Ensure live recording is enabled.",
                    new Rectangle(4, 4, contentWidth, 30), style.ForegroundGrey, TextAlignment.Near, TextAlignment.Center);
                return;
            }

            float scale = contentWidth / _frameDurationMs;
            Float2 localMouse = PointFromWindow(Root.MousePosition);
            float rowSpan = EventHeight + EventSpacing;

            for (int i = 0; i < _entries.Count; i++)
            {
                TimelineEntry entry = _entries[i];
                float y = 4.0f + entry.Depth * rowSpan;
                float barX = 4.0f + entry.Depth * 12.0f + entry.StartMs * scale;
                float barWidth = Math.Max(entry.DurationMs * scale, 2.0f);
                var rect = new Rectangle(barX, y, barWidth, EventHeight);

                Color color = EventColors[entry.ColorIndex % EventColors.Length] * 0.8f;
                if (rect.Contains(localMouse))
                    color *= 1.1f;

                Render2D.FillRectangle(rect, color);
                Render2D.DrawRectangle(rect, color * 0.5f);

                if (barWidth > 30.0f)
                {
                    string label = entry.Name + "  " + entry.Time.ToString("0.000") + " ms";
                    Render2D.PushClip(rect);
                    Render2D.DrawText(style.FontSmall, label, rect, style.Foreground, TextAlignment.Near, TextAlignment.Center);
                    Render2D.PopClip();
                }

                _hitRects.Add(new HitRect { Rect = rect, EntryIndex = i });
            }
        }

        public override bool OnMouseDoubleClick(Float2 location, MouseButton button)
        {
            if (button != MouseButton.Left)
                return base.OnMouseDoubleClick(location, button);

            if (TryFindEntryAt(location, out TimelineEntry entry))
            {
                if (PerfSdkGpuProfilerFilter.IsExcludedRegionName(entry.Name))
                    return true;

                EventDoubleClicked?.Invoke(entry.Name, entry.Depth, entry.Time, entry.DrawCalls, entry.Triangles, entry.Vertices);
                return true;
            }

            return base.OnMouseDoubleClick(location, button);
        }

        bool TryFindEntryAt(Float2 location, out TimelineEntry entry)
        {
            entry = default;
            if (_entries.Count == 0)
                return false;

            float contentWidth = Math.Max(Width - 8.0f, 100.0f);
            float scale = contentWidth / _frameDurationMs;
            float rowSpan = EventHeight + EventSpacing;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                TimelineEntry candidate = _entries[i];
                float y = 4.0f + candidate.Depth * rowSpan;
                float barX = 4.0f + candidate.Depth * 12.0f + candidate.StartMs * scale;
                float barWidth = Math.Max(candidate.DurationMs * scale, 2.0f);
                var rect = new Rectangle(barX, y, barWidth, EventHeight);
                if (!rect.Contains(location))
                    continue;

                entry = candidate;
                return true;
            }

            return false;
        }
    }
}
#endif
