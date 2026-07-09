#if FLAX_EDITOR && USE_PROFILER
using FlaxEditor;
using FlaxEditor.GUI.Docking;
using FlaxEngine;
using FlaxEngine.GUI;
using NVIDIA;
using System;

namespace FlaxEditor.Windows
{
    /// <summary>
    /// Mode 1: Editor window showing live NVIDIA PerfSDK GPU metrics in a grouped table layout.
    /// </summary>
    public sealed class PerfSdkMetricsWindow : EditorWindow
    {
        private PerfSdkMetricsView _view;

        public PerfSdkMetricsWindow(Editor editor)
        : base(editor, true, ScrollBars.None)
        {
            Title = "Nvidia PerfSDK Metrics";
            Size = PerfSdkMetricsView.DefaultSize;

            _view = new PerfSdkMetricsView { Parent = this };
            _view.AddMetricClicked += OnAddMetricClicked;

            PerformLayout();
        }

        public override Float2 DefaultSize => PerfSdkMetricsView.DefaultSize;

        public void ShowRegion(string regionName)
        {
            _view.ResetStatus();

            string apiName = RenderPerfToolsFormatting.FormatRendererType(GPUDevice.Instance != null ? GPUDevice.Instance.RendererType : RendererType.Unknown);
            Title = "Nvidia PerfSDK Metrics: " + regionName + " · " + apiName;
            _view.UpdateView();
            ApplyWindowSize();
            PerformLayout();

            if (IsHidden)
                ShowFloating(GetFloatingClientSize(), WindowStartPosition.CenterParent);
            else
                FocusOrShow(DockState.Float);
        }

        static Float2 GetFloatingClientSize(Float2 clientSize)
        {
            var dpi = (float)Platform.Dpi / 96.0f;
            return clientSize * dpi * Platform.CustomDpiScale;
        }

        Float2 GetFloatingClientSize() => GetFloatingClientSize(Size);

        void ApplyWindowSize()
        {
            Size = PerfSdkMetricsView.DefaultSize;
            if (ParentDockPanel is FloatWindowDockPanel floatPanel && floatPanel.Window?.Window != null)
            {
                floatPanel.Window.Window.ClientSize = GetFloatingClientSize(PerfSdkMetricsView.DefaultSize);
                floatPanel.Window.PerformLayout();
                PerformLayout();
            }
        }

        public void UpdateViewPublic(bool expandCustomGroup = false) => _view.UpdateView(expandCustomGroup);

        private void OnAddMetricClicked() => PerfSdkAddMetricWindow.ShowPicker(Editor, this);

        protected override void OnClose()
        {
            PerfSdkAddMetricWindow.CloseAll();
            if (RenderPerfTools.IsSessionActive)
                RenderPerfTools.CloseRegionProfiler();
            base.OnClose();
        }

        public override void OnExit()
        {
            PerfSdkAddMetricWindow.CloseAll();
            if (RenderPerfTools.IsSessionActive)
                RenderPerfTools.CloseRegionProfiler();
        }
    }
}
#endif
