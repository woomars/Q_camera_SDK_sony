using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CameraSDK;

namespace CameraDemo
{
    public partial class MainWindow : Window
    {
        private CameraManager _camera;
        private WriteableBitmap? _bitmap;
        private bool _isRunning = false;

        public MainWindow()
        {
            InitializeComponent();
            _camera = new CameraManager();

            if (_camera.Initialize())
            {
                Log("Camera SDK initialized.");
            }
            else
            {
                Log("Error: Camera SDK failed to initialize.");
            }

            // Slider handlers
            ExposureSlider.ValueChanged += (s, e) => {
                _camera.SetExposure((int)e.NewValue);
                ExposureValue.Text = ((int)e.NewValue).ToString();
            };

            GainSlider.ValueChanged += (s, e) => {
                _camera.SetGain((int)e.NewValue);
                GainValue.Text = ((int)e.NewValue).ToString();
            };

            TriggerModeCombo.SelectionChanged += (s, e) => {
                var mode = (TriggerMode)TriggerModeCombo.SelectedIndex;
                _camera.SetTriggerMode(mode);
                SoftwareTriggerBtn.IsEnabled = (mode == TriggerMode.Software);
                Log($"Trigger Mode: {mode}");
            };

            SoftwareTriggerBtn.Click += (s, e) => _camera.SoftwareTrigger();

            StartStopBtn.Click += (s, e) => ToggleCapture();

            _camera.OnFrameReceived += UpdateFrame;
        }

        private void ToggleCapture()
        {
            if (!_isRunning)
            {
                if (_camera.Start())
                {
                    _isRunning = true;
                    StartStopBtn.Content = "Stop Capture";
                    StartStopBtn.Background = new SolidColorBrush(Color.FromRgb(220, 53, 69)); // Bootstrap Danger Red
                    Log("Capture started.");
                }
            }
            else
            {
                if (_camera.Stop())
                {
                    _isRunning = false;
                    StartStopBtn.Content = "Start Capture";
                    StartStopBtn.Background = new SolidColorBrush(Color.FromRgb(40, 167, 69)); // Bootstrap Success Green
                    Log("Capture stopped.");
                }
            }
        }

        private void UpdateFrame(IntPtr pBuffer, int width, int height, int step)
        {
            Dispatcher.Invoke(() =>
            {
                if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                {
                    _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                    PreviewImage.Source = _bitmap;
                }

                _bitmap.Lock();
                // Copy buffer to bitmap (BGR24 format expected by C++ mock)
                // Note: In real app, avoid excessive copies or use a direct buffer mapping
                unsafe
                {
                    Buffer.MemoryCopy((void*)pBuffer, (void*)_bitmap.BackBuffer, (long)(_bitmap.BackBufferStride * height), (long)(step * height));
                }
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _bitmap.Unlock();
            }, DispatcherPriority.Render);
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() => {
                LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
                LogScroll.ScrollToEnd();
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            _camera.Dispose();
            base.OnClosed(e);
        }
    }
}
