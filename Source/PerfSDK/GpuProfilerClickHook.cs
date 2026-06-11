#if FLAX_EDITOR && USE_PROFILER

using FlaxEditor;
using FlaxEditor.GUI;
using FlaxEditor.GUI.Tabs;
using FlaxEditor.Windows;
using FlaxEditor.Windows.Profiler;
using FlaxEngine;
using FlaxEngine.GUI;

namespace NVIDIA
{
    /// <summary>
    /// Detects GPU profiler timeline/table clicks from the plugin when engine click wiring is unavailable.
    /// </summary>
    internal static class GpuProfilerClickHook
    {
#if false
        private static bool _registered;
        private static bool _leftMouseDown;

        public static void Enable()
        {
            if (_registered || Editor.Instance == null)
                return;

            Editor.Instance.UI.MasterPanel.Root.UpdateCallbacksToAdd.Add(Update);
            _registered = true;
        }

        public static void Disable()
        {
            if (!_registered || Editor.Instance == null)
                return;

            Editor.Instance.UI.MasterPanel?.Root.UpdateCallbacksToRemove.Add(Update);
            _registered = false;
            _leftMouseDown = false;
        }

        static void Update(float deltaTime)
        {
            var editor = Editor.Instance;
            if (editor?.Windows?.ProfilerWin == null)
                return;

            var profiler = editor.Windows.ProfilerWin;
            if (!profiler.Visible)
            {
                _leftMouseDown = false;
                return;
            }

            var root = profiler.Root;
            if (root == null)
                return;

            if (root.GetMouseButtonDown(MouseButton.Left))
                _leftMouseDown = true;

            if (!_leftMouseDown || !root.GetMouseButtonUp(MouseButton.Left))
                return;

            _leftMouseDown = false;

            if (!IsGpuTabSelected(profiler))
                return;

            Float2 local = profiler.PointFromWindow(root.MousePosition);
            if (!profiler.RayCast(ref local, out Control hit) || hit == null)
                return;

            if (!TryGetRegionFromControl(hit, out string regionName, out int regionDepth, out float gpuTimeMs, out long drawCalls, out long triangles, out long vertices))
                return;

            Editor.Log("PerfSDK: GPU profiler click on '" + regionName + "' (depth " + regionDepth + ").");
            GpuProfilerBridge.OpenRegion(regionName, regionDepth, gpuTimeMs, drawCalls, triangles, vertices);
        }

        static bool IsGpuTabSelected(ProfilerWindow profiler)
        {
            Tabs tabs = FindChild<Tabs>(profiler);
            return tabs?.SelectedTab != null && tabs.SelectedTab.Text == "GPU";
        }

        static bool TryGetRegionFromControl(Control hit, out string regionName, out int regionDepth, out float gpuTimeMs, out long drawCalls, out long triangles, out long vertices)
        {
            regionName = null;
            regionDepth = -1;
            gpuTimeMs = 0;
            drawCalls = 0;
            triangles = 0;
            vertices = 0;

            for (Control control = hit; control != null; control = control.Parent)
            {
                if (control is Timeline.Event timelineEvent)
                {
                    regionName = timelineEvent.Name;
                    /*regionDepth = timelineEvent.Depth;
                    gpuTimeMs = timelineEvent.GpuTimeMs;
                    drawCalls = timelineEvent.DrawCalls;
                    triangles = timelineEvent.Triangles;
                    vertices = timelineEvent.Vertices;*/
                    return !string.IsNullOrEmpty(regionName);
                }

                if (control is ClickableRow row && row.Values != null && row.Values.Length >= 6)
                {
                    regionName = row.Values[0] as string;
                    regionDepth = row.Depth;
                    gpuTimeMs = row.Values[2] is float ms ? ms : 0;
                    drawCalls = row.Values[3] is long dc ? dc : 0;
                    triangles = row.Values[4] is long tr ? tr : 0;
                    vertices = row.Values[5] is long v ? v : 0;
                    return !string.IsNullOrEmpty(regionName);
                }
            }

            return false;
        }

        static T FindChild<T>(Control root) where T : Control
        {
            if (root == null)
                return null;

            if (root is T match)
                return match;

            if (root is ContainerControl container)
            {
                for (int i = 0; i < container.ChildrenCount; i++)
                {
                    if (container.Children[i] is Control child)
                    {
                        T found = FindChild<T>(child);
                        if (found != null)
                            return found;
                    }
                }
            }

            return null;
        }
#else
        public static void Enable()
        {
        }

        public static void Disable()
        {
        }
#endif
    }
}

#endif
