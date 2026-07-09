#if !FLAX_EDITOR && USE_PROFILER

using FlaxEngine;
using FlaxEngine.GUI;
using System;

namespace NVIDIA
{
    /// <summary>
    /// Mode 2: Runtime metrics window — uses the same PerfSdkMetricsView as the editor window.
    /// </summary>
    internal sealed class StandaloneMetricsWindow
    {
        private Window _window;
        private WindowRootControl _root;
        private PerfSdkMetricsView _view;
        private bool _isDisposing;

        public bool IsDisposing => _isDisposing;
        public bool IsAvailable => !_isDisposing && _window != null && _view != null;
        public bool IsVisible => _window != null && _window.IsVisible;

        public StandaloneMetricsWindow()
        {
            _window = PerfSDKPlatform.CreateOverlayWindow("Nvidia PerfSDK Metrics", PerfSdkMetricsView.DefaultSize);
            if (_window == null)
            {
                Debug.LogError("PerfSDK: failed to create metrics window.");
                return;
            }

            _root = _window.GUI;
            _root.BackgroundColor = Style.Current.Background;

            _view = new PerfSdkMetricsView
            {
                Parent = _root,
                AnchorPreset = AnchorPresets.StretchAll,
                Offsets = Margin.Zero,
            };
            _view.AddMetricClicked += OnAddMetricClicked;

            _window.Closing += OnWindowClosing;
        }

        void OnWindowClosing(ClosingReason reason, ref bool cancel)
        {
            _isDisposing = true;
            if (RenderPerfTools.IsSessionActive)
                RenderPerfTools.CloseRegionProfiler();
            RuntimeProfilerBridge.NotifyMetricsWindowClosed(this);
        }

        public void ShowRegion(string regionName)
        {
            if (_window == null || _view == null)
            {
                Debug.LogError("PerfSDK: metrics window is not available.");
                return;
            }

            _isDisposing = false;
            _view.ResetStatus();

            string apiName = RenderPerfToolsFormatting.FormatRendererType(GPUDevice.Instance != null ? GPUDevice.Instance.RendererType : RendererType.Unknown);
            _window.Title = "Nvidia PerfSDK Metrics: " + regionName + " · " + apiName;
            UpdateViewPublic();
            _window.Show();
            _window.BringToFront();
        }

        public void UpdateViewPublic(bool expandCustomGroup = false) => _view.UpdateView(expandCustomGroup);

        public void Hide()
        {
            if (_window != null)
                _window.Hide();
        }

        void OnAddMetricClicked()
        {
            Scripting.InvokeOnUpdate(() => StandaloneAddMetricWindow.ShowPicker(this));
        }
    }
}
#endif
