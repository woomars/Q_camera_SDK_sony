using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;
using System.Runtime.InteropServices;
using CameraSDK;

namespace CameraDemo
{
    public partial class MainWindow : Window
    {
        private CameraManager _camera;
        private WriteableBitmap? _bitmap;
        private bool _isRunning = false;
        private DispatcherTimer _statusTimer;
        private const double TargetFps = 24.0;
        private const long ExposureMinRaw = -11;
        private const long ExposureMaxRaw = -2;
        private const int Nv12Subtype = 1;

        private struct RawFrameChunk
        {
            public byte[] Buffer;
            public int Length;
            public int Width;
            public int Height;
            public int Step;
        }

        private BlockingCollection<RawFrameChunk>? _rawQueue;
        private Task? _spoolTask;
        private string _tempDirectory = string.Empty;
        private string _spoolRawPath = string.Empty;
        private int _recordedFrameCount = 0;
        private bool _recordInMemoryOnly = false;
        private readonly List<RawFrameChunk> _recordedFramesMemory = new List<RawFrameChunk>();
        private readonly object _recordedFramesLock = new object();

        private bool _isRecording = false;
        private DateTime _recordStartTimeUtc;
        private bool _recordDurationStarted = false;
        private bool _recordFirstFrameLogged = false;
        private TimeSpan _recordDuration;
        private int _frameWidth;
        private int _frameHeight;
        private byte[]? _previewBuffer; // Managed buffer for safe UI rendering
        private bool _isUIPainting = false; // Throttling flag for UI display
        private bool _gainSetWarningShown = false;
        private readonly long _maxExposureRawForTargetFps = (long)Math.Floor(Math.Log(1.0 / TargetFps, 2.0));
        private long _appliedExposureMaxRaw;
        private bool _isBatchSaving = false;
        private bool _syncingExposureSliders = false;
        private const long AutoFlag = 0x1;
        private bool _autoExposureSupported = false;
        private bool _backlightSupported = false;
        private bool _autoExposureBeforeRecording = false;
        private bool _backlightOnBeforeRecording = false;
        private FocusMode _selectedFocusMode = FocusMode.Auto;
        private FocusMode _focusModeBeforeRecording = FocusMode.Auto;
        private int _manualFocusValue = 80;
        private DateTime _lastPreviewUiUpdateUtc = DateTime.MinValue;
        private readonly TimeSpan _previewUiInterval = TimeSpan.FromMilliseconds(66); // cap preview rendering to ~15fps
        private readonly object _logFileLock = new object();
        private readonly string _logFilePath;

        public MainWindow()
        {
            InitializeComponent();
            _camera = new CameraManager();
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "camera_demo.log");

            ConfigureExposureRangeForTargetFps();

            GainSlider.ValueChanged += (s, e) => {
                int gain = (int)e.NewValue;
                GainValue.Text = gain.ToString();
                bool ok = _camera.SetGain(gain);
                GainValue.Foreground = ok ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.OrangeRed;
                if (!ok && !_gainSetWarningShown) {
                    _gainSetWarningShown = true;
                    Log("Warning: Gain control was rejected by camera/driver. Check UVC gain support.");
                }
            };

            FocusModeCombo.SelectionChanged += (s, e) =>
            {
                _selectedFocusMode = FocusModeCombo.SelectedIndex == 0 ? FocusMode.Auto : FocusMode.Manual;
                UpdateFocusUiState();
                if (_isRecording) return;

                if (_isRunning) {
                    ApplyFocusToCamera();
                } else {
                    Log($"Focus mode set to {_selectedFocusMode}. It will apply on preview start.");
                }
            };

            FocusSlider.ValueChanged += (s, e) =>
            {
                _manualFocusValue = (int)e.NewValue;
                FocusValue.Text = _manualFocusValue.ToString();
                if (_isRecording) return;
                if (_selectedFocusMode == FocusMode.Manual && _isRunning) {
                    _camera.SetFocus(_manualFocusValue);
                }
            };

            ResolutionCombo.SelectionChanged += ResolutionCombo_SelectionChanged;

            StartStopBtn.Click += (s, e) => ToggleCapture();
            RecordBtn.Click += RecordBtn_Click;
            BatchSaveBtn.Click += BatchSaveBtn_Click;
            ExposureSlider.ValueChanged += ExposureSlider_ValueChanged;
            JpegQualitySlider.ValueChanged += (s, e) => JpegQualityValue.Text = ((int)e.NewValue).ToString();

            BindProcAmpSlider(BrightnessSlider, BrightnessValue, ProcAmpProperty.Brightness);
            BindProcAmpSlider(ContrastSlider, ContrastValue, ProcAmpProperty.Contrast);
            BindProcAmpSlider(SaturationSlider, SaturationValue, ProcAmpProperty.Saturation);
            BindProcAmpSlider(SharpnessSlider, SharpnessValue, ProcAmpProperty.Sharpness);
            BindSliderInput(GainSlider, GainValue);
            BindSliderInput(FocusSlider, FocusValue);
            BindSliderInput(JpegQualitySlider, JpegQualityValue);
            BindSliderInput(BrightnessSlider, BrightnessValue);
            BindSliderInput(ContrastSlider, ContrastValue);
            BindSliderInput(SaturationSlider, SaturationValue);
            BindSliderInput(SharpnessSlider, SharpnessValue);
            BindSliderInput(ExposureSlider, ExposureValue);
            AutoExposureCheck.Checked += AutoControlCheck_Changed;
            AutoExposureCheck.Unchecked += AutoControlCheck_Changed;
            BacklightToggle.Checked += BacklightToggle_Changed;
            BacklightToggle.Unchecked += BacklightToggle_Changed;

            if (!InitializeCamera()) {
                int hr = _camera.GetLastHRESULT();
                Log($"Error: Camera SDK failed to initialize. HRESULT=0x{hr:X8}");
                if (hr == unchecked((int)0x80070005)) {
                    System.Windows.MessageBox.Show(
                        "카메라 접근이 거부되었습니다 (0x80070005).\n" +
                        "1) 다른 카메라 앱(Teams/Zoom/카메라앱/기존 CameraDemo/CLI) 종료\n" +
                        "2) Windows 카메라 권한(데스크톱 앱 포함) ON\n" +
                        "3) USB 카메라 재연결 후 다시 실행",
                        "카메라 점유/권한 문제",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            UpdateFocusUiState();

            _statusTimer = new DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromMilliseconds(500);
            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();
            Log($"Log file: {_logFilePath}");
        }

        private bool InitializeCamera()
        {
            if (!TryGetSelectedResolution(out int prefWidth, out int prefHeight)) {
                prefWidth = 3840;
                prefHeight = 2160;
            }
            bool resOk = _camera.SetPreferredResolution(prefWidth, prefHeight);
            if (!resOk) {
                Log($"Warning: Failed to set preferred resolution to {prefWidth}x{prefHeight} before initialize.");
            }

            bool modeOk = _camera.SetPreferred4KMode(Preferred4KMode.B_Nv12Native);
            if (!modeOk) {
                Log("Warning: Failed to force preferred mode to NV12 before initialize.");
            }

            if (!_camera.Initialize()) {
                return false;
            }

            _camera.OnFrameReceived += UpdateFrame;
            RefreshProcAmpRangesFromCamera();
            RefreshAutoSupportFromCamera();
            ApplyUiValuesToCamera();
            UpdateActiveModeText();
            Log($"Camera SDK initialized. NV12 fixed pipeline. Preferred resolution: {prefWidth}x{prefHeight}");
            int nw = _camera.GetNegotiatedWidth();
            int nh = _camera.GetNegotiatedHeight();
            double nfps = _camera.GetNegotiatedFPS();
            int nsub = _camera.GetNegotiatedSubtype();
            string nsubName = nsub == 1 ? "NV12" : "Unknown";
            NegotiatedModeText.Text = $"{nw}x{nh} @ {nfps:F2} ({nsubName})";
            if (nsub != Nv12Subtype || nfps < TargetFps - 0.5) {
                Log($"Warning: negotiated mode is {nw}x{nh} @ {nfps:F2} ({nsubName}), below NV12 {TargetFps:F0}fps target.");
            }
            return true;
        }

        private void ApplyUiValuesToCamera()
        {
            _camera.SetGain((int)GainSlider.Value);
            ApplyAutoControlsToCamera();
            ApplyFocusToCamera();
            ApplyProcAmpValuesToCamera();
        }

        private void ApplyFocusToCamera()
        {
            if (_selectedFocusMode == FocusMode.Auto) {
                _camera.SetFocusMode(FocusMode.Auto);
            } else {
                _camera.SetFocusMode(FocusMode.Manual);
                _camera.SetFocus(_manualFocusValue);
            }
        }

        private void UpdateFocusUiState()
        {
            bool manual = _selectedFocusMode == FocusMode.Manual;
            FocusSlider.IsEnabled = manual && !_isRecording;
            FocusModeCombo.IsEnabled = !_isRecording;
            FocusValue.Foreground = manual ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Gray;
        }

        private void LockFocusForRecording()
        {
            _focusModeBeforeRecording = _selectedFocusMode;
            _autoExposureBeforeRecording = AutoExposureCheck.IsChecked == true;
            _backlightOnBeforeRecording = BacklightToggle.IsChecked == true;

            if (_autoExposureSupported) AutoExposureCheck.IsChecked = false;
            if (_backlightSupported) BacklightToggle.IsChecked = false;
            ApplyAutoControlsToCamera();
            if (_backlightSupported) {
                _camera.SetProcAmpValue(ProcAmpProperty.BacklightCompensation, 0);
            }

            AutoExposureCheck.IsEnabled = false;
            BacklightToggle.IsEnabled = false;

            _camera.SetFocusMode(FocusMode.Manual);
            _camera.SetFocus(_manualFocusValue);
            UpdateFocusUiState();
            Log("Focus and auto image controls locked to manual during recording.");
        }

        private void RestoreFocusAfterRecording()
        {
            if (_autoExposureSupported) AutoExposureCheck.IsChecked = _autoExposureBeforeRecording;
            if (_backlightSupported) BacklightToggle.IsChecked = _backlightOnBeforeRecording;

            AutoExposureCheck.IsEnabled = _autoExposureSupported;
            BacklightToggle.IsEnabled = _backlightSupported;
            ApplyAutoControlsToCamera();
            if (_backlightSupported) {
                _camera.SetProcAmpValue(ProcAmpProperty.BacklightCompensation, BacklightToggle.IsChecked == true ? 1 : 0);
            }

            _selectedFocusMode = _focusModeBeforeRecording;
            FocusModeCombo.SelectedIndex = _selectedFocusMode == FocusMode.Auto ? 0 : 1;
            ApplyFocusToCamera();
            UpdateFocusUiState();
            UpdateAutoControlUiState();
            Log($"Focus mode restored: {_selectedFocusMode}");
        }

        private void UpdateActiveModeText()
        {
            ActiveModeText.Text = "NV12 -> NV12 (Fixed)";
        }

        private void BindSliderInput(System.Windows.Controls.Slider slider, System.Windows.Controls.TextBox input)
        {
            bool syncing = false;
            void ApplyFromText()
            {
                if (syncing) return;
                if (!double.TryParse(input.Text, out double typed)) return;
                if (typed < slider.Minimum) typed = slider.Minimum;
                if (typed > slider.Maximum) typed = slider.Maximum;
                if (Math.Abs(slider.Value - typed) < 0.1) return;
                syncing = true;
                slider.Value = typed;
                input.Text = ((int)Math.Round(typed)).ToString();
                input.CaretIndex = input.Text.Length;
                syncing = false;
            }

            input.TextChanged += (s, e) => ApplyFromText();
            input.LostKeyboardFocus += (s, e) =>
            {
                ApplyFromText();
                if (!double.TryParse(input.Text, out _)) {
                    input.Text = ((int)Math.Round(slider.Value)).ToString();
                }
            };
            input.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) {
                    ApplyFromText();
                    if (!double.TryParse(input.Text, out _)) {
                        input.Text = ((int)Math.Round(slider.Value)).ToString();
                    }
                    e.Handled = true;
                }
            };
        }

        private void BindProcAmpSlider(System.Windows.Controls.Slider slider, System.Windows.Controls.TextBox valueText, ProcAmpProperty property)
        {
            slider.ValueChanged += (s, e) =>
            {
                int value = (int)e.NewValue;
                valueText.Text = value.ToString();
                if (_camera == null) return;
                bool ok = _camera.SetProcAmpValue(property, value);
                valueText.Foreground = ok ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.OrangeRed;
            };
        }

        private void RefreshProcAmpRangesFromCamera()
        {
            ConfigureProcAmpRange(BrightnessSlider, BrightnessValue, ProcAmpProperty.Brightness);
            ConfigureProcAmpRange(ContrastSlider, ContrastValue, ProcAmpProperty.Contrast);
            ConfigureProcAmpRange(SaturationSlider, SaturationValue, ProcAmpProperty.Saturation);
            ConfigureProcAmpRange(SharpnessSlider, SharpnessValue, ProcAmpProperty.Sharpness);
        }

        private void ConfigureProcAmpRange(System.Windows.Controls.Slider slider, System.Windows.Controls.TextBox valueText, ProcAmpProperty property)
        {
            if (_camera.TryGetProcAmpRange(property, out long min, out long max, out long step, out long def, out _))
            {
                slider.Minimum = min;
                slider.Maximum = max;
                slider.TickFrequency = step > 0 ? step : 1;
                double target = Math.Clamp(def, min, max);
                slider.Value = target;
                valueText.Text = ((int)target).ToString();
                slider.IsEnabled = true;
                valueText.Foreground = System.Windows.Media.Brushes.White;
            }
            else
            {
                slider.IsEnabled = false;
                valueText.Text = "N/A";
                valueText.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }

        private void ApplyProcAmpValuesToCamera()
        {
            _camera.SetProcAmpValue(ProcAmpProperty.Brightness, (int)BrightnessSlider.Value);
            _camera.SetProcAmpValue(ProcAmpProperty.Contrast, (int)ContrastSlider.Value);
            _camera.SetProcAmpValue(ProcAmpProperty.Saturation, (int)SaturationSlider.Value);
            _camera.SetProcAmpValue(ProcAmpProperty.Sharpness, (int)SharpnessSlider.Value);
            if (_backlightSupported) {
                _camera.SetProcAmpValue(ProcAmpProperty.BacklightCompensation, BacklightToggle.IsChecked == true ? 1 : 0);
            }
        }

        private void RefreshAutoSupportFromCamera()
        {
            _autoExposureSupported = TryGetCameraAutoSupport(CameraControlProperty.Exposure);
            _backlightSupported = _camera.TryGetProcAmpRange(ProcAmpProperty.BacklightCompensation, out _, out _, out _, out _, out _);

            AutoExposureCheck.IsEnabled = _autoExposureSupported;

            if (!_autoExposureSupported) AutoExposureCheck.IsChecked = false;
            BacklightToggle.IsEnabled = _backlightSupported;
            BacklightToggle.Content = BacklightToggle.IsChecked == true ? "ON" : "OFF";

            Log($"Auto support: Exposure={_autoExposureSupported}, BacklightToggle={_backlightSupported}");
            UpdateAutoControlUiState();
        }

        private bool TryGetCameraAutoSupport(CameraControlProperty property)
        {
            return _camera.TryGetCameraControlRange(property, out _, out _, out _, out _, out long caps) && (caps & AutoFlag) != 0;
        }

        private void AutoControlCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_isRecording) {
                return;
            }
            if (_isRunning) {
                ApplyAutoControlsToCamera();
            } else {
                Log("Auto control selection updated. It will apply during preview.");
            }
            UpdateAutoControlUiState();
        }

        private void BacklightToggle_Changed(object sender, RoutedEventArgs e)
        {
            BacklightToggle.Content = BacklightToggle.IsChecked == true ? "ON" : "OFF";
            if (_isRecording || !_backlightSupported) {
                return;
            }
            bool ok = _camera.SetProcAmpValue(ProcAmpProperty.BacklightCompensation, BacklightToggle.IsChecked == true ? 1 : 0);
            if (!ok) {
                Log("Warning: Failed to set backlight toggle.");
            }
        }

        private void ApplyAutoControlsToCamera()
        {
            bool autoExp = AutoExposureCheck.IsChecked == true && _autoExposureSupported;

            if (_autoExposureSupported) {
                _camera.SetCameraControlValue(CameraControlProperty.Exposure, (long)ExposureSlider.Value, autoExp);
            } else {
                _camera.SetExposure((long)ExposureSlider.Value);
            }
        }

        private void UpdateAutoControlUiState()
        {
            bool autoExp = AutoExposureCheck.IsChecked == true && AutoExposureCheck.IsEnabled;

            ExposureSlider.IsEnabled = !autoExp;
        }

        private void UpdateExposureDisplay(long raw)
        {
            ExposureValue.Text = raw.ToString();
            double ms = Math.Pow(2.0, raw) * 1000.0;
            ExposureMsValue.Text = $"{ms:F3} ms";
        }

        private void ConfigureExposureRangeForTargetFps()
        {
            _appliedExposureMaxRaw = Math.Min(ExposureMaxRaw, _maxExposureRawForTargetFps);
            if (_appliedExposureMaxRaw < ExposureMinRaw) {
                _appliedExposureMaxRaw = ExposureMinRaw;
            }

            ExposureSlider.Minimum = ExposureMinRaw;
            ExposureSlider.Maximum = _appliedExposureMaxRaw;
            if (ExposureSlider.Value > _appliedExposureMaxRaw) {
                ExposureSlider.Value = _appliedExposureMaxRaw;
            }
            UpdateExposureDisplay((long)ExposureSlider.Value);

            double maxExposureMs = Math.Pow(2.0, _appliedExposureMaxRaw) * 1000.0;
            Log($"Exposure range: {ExposureMinRaw} .. {_appliedExposureMaxRaw} (max {maxExposureMs:F3} ms) for {TargetFps:F0} fps target.");
        }

        private void StatusTimer_Tick(object? sender, EventArgs e)
        {
            if (_camera == null) return;
            
            int speed = _camera.GetUSBSpeed();
            UsbSpeedText.Text = speed == 3 ? "USB 3.0 (SuperSpeed)" : (speed == 2 ? "USB 2.0 (HighSpeed)" : "Unknown");
            UsbSpeedText.Foreground = speed == 3 ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Orange;

            double fps = _camera.GetCurrentFPS();
            double tsFps = _camera.GetTimestampFPS();
            long dropped = _camera.GetEstimatedDroppedFrames();
            ActualFpsText.Text = tsFps > 0.0 ? $"{fps:F2} fps (ts {tsFps:F2}, drop {dropped})" : $"{fps:F2} fps";
            ActualFpsText.Foreground = fps >= TargetFps - 1.0 ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.White;

            long appliedExposureRaw = _camera.GetExposure();
            int appliedExposure = unchecked((int)appliedExposureRaw);
            int appliedGain = _camera.GetGain();
            AppliedAeText.Text = $"Exp {appliedExposure}, Gain {appliedGain}";

            UpdateActiveModeText();

            if (_frameWidth > 0 && _frameHeight > 0) {
                CurrentResolutionText.Text = $"{_frameWidth} x {_frameHeight}";
            }
        }

        private void RecordBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning) {
                System.Windows.MessageBox.Show("Please Start Preview first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int nw = _camera.GetNegotiatedWidth();
            int nh = _camera.GetNegotiatedHeight();
            double nfps = _camera.GetNegotiatedFPS();
            int nsub = _camera.GetNegotiatedSubtype();
            if (nsub != Nv12Subtype || nfps < TargetFps - 0.5) {
                string nsubName = nsub == 1 ? "NV12" : "Unknown";
                System.Windows.MessageBox.Show(
                    $"24fps target guard:\nCurrent negotiated mode is {nw}x{nh} @ {nfps:F2} ({nsubName}).\nRecord is blocked until NV12 @ >=23.5fps.",
                    "24fps Guard",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Log($"Record blocked by 24fps guard. Current negotiated mode: {nw}x{nh} @ {nfps:F2} ({nsubName})");
                return;
            }

            if (_isRecording) {
                // Stop manual recording
                _isRecording = false;
                RestoreFocusAfterRecording();
                RecordBtn.Content = "● Record (Memory Queue)";
                if (_recordInMemoryOnly) {
                    Log($"Recording stopped manually. Memory capture ready. Total {_recordedFrameCount} frames.");
                    BatchSaveBtn.IsEnabled = _recordedFrameCount > 0;
                } else {
                    _rawQueue?.CompleteAdding(); // Let the background spooler finish remaining items
                    Log($"Recording stopped manually. Spooling remaining frames to SSD...");
                }
            } else {
                ClearRecordedFramesMemory();
                _recordedFrameCount = 0;
                BatchSaveBtn.IsEnabled = false;
                _camera.ResetPerfStats();
                
                string sel = ((System.Windows.Controls.ComboBoxItem)CaptureDurationCombo.SelectedItem).Content.ToString() ?? "Continuous";
                if (sel == "Continuous") _recordDuration = TimeSpan.Zero;
                else _recordDuration = TimeSpan.FromSeconds(double.Parse(sel.Replace("s", "")));
                _recordInMemoryOnly = _recordDuration.TotalSeconds > 0 && _recordDuration.TotalSeconds <= 3.0;

                _tempDirectory = Path.Combine(Path.GetTempPath(), "CameraSDK_Temp");
                if (!Directory.Exists(_tempDirectory)) Directory.CreateDirectory(_tempDirectory);
                foreach (var file in Directory.GetFiles(_tempDirectory)) File.Delete(file); // clear old

                if (_recordInMemoryOnly) {
                    _rawQueue = null;
                    _spoolTask = Task.CompletedTask;
                    _spoolRawPath = string.Empty;
                    Log("Recording mode: memory-only (short duration, max throughput).");
                } else {
                    _spoolRawPath = Path.Combine(_tempDirectory, "capture_nv12.frames");
                    _rawQueue = new BlockingCollection<RawFrameChunk>(600);
                    _spoolTask = Task.Run(() => {
                        using (var fs = new FileStream(_spoolRawPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4 * 1024 * 1024, FileOptions.SequentialScan)) {
                            using var bw = new BinaryWriter(fs);
                            foreach (var frame in _rawQueue.GetConsumingEnumerable()) {
                                bw.Write(frame.Length);
                                bw.Write(frame.Width);
                                bw.Write(frame.Height);
                                bw.Write(frame.Step);
                                bw.Write(frame.Buffer, 0, frame.Length);
                                Interlocked.Increment(ref _recordedFrameCount);
                                ArrayPool<byte>.Shared.Return(frame.Buffer);
                            }
                            bw.Flush();
                        }
                        Dispatcher.BeginInvoke(() => {
                            Log($"SSD Spooling finished. Total {_recordedFrameCount} frames saved to temp.");
                            BatchSaveBtn.IsEnabled = _recordedFrameCount > 0;
                        });
                    });
                }

                _recordStartTimeUtc = DateTime.MinValue;
                _recordDurationStarted = false;
                _recordFirstFrameLogged = false;
                bool gainOk = _camera.SetGain((int)GainSlider.Value);
                ApplyProcAmpValuesToCamera();
                Log(
                    $"Capture controls applied: Gain={GainSlider.Value:0} (ok={gainOk}), " +
                    $"Brightness={BrightnessSlider.Value:0}, Contrast={ContrastSlider.Value:0}, " +
                    $"Saturation={SaturationSlider.Value:0}, Sharpness={SharpnessSlider.Value:0}");
                _isRecording = true;
                LockFocusForRecording();
                RecordBtn.Content = "■ Stop Recording";
                Log($"Recording armed for {sel}. Duration timer starts on first frame.");
            }
        }

        private async void BatchSaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchSaving) {
                return;
            }

            if (_recordedFrameCount == 0) {
                System.Windows.MessageBox.Show("No recorded frames.", "Wait");
                return;
            }
            if (!_recordInMemoryOnly && (string.IsNullOrWhiteSpace(_spoolRawPath) || !File.Exists(_spoolRawPath) || _spoolTask == null || !_spoolTask.IsCompleted)) {
                System.Windows.MessageBox.Show("Please wait for SSD Spooling to finish.", "Wait");
                return;
            }

            using var fbd = new System.Windows.Forms.FolderBrowserDialog();
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string folder = fbd.SelectedPath;
                string format = ((System.Windows.Controls.ComboBoxItem)FormatCombo.SelectedItem).Content.ToString()?.ToLower() ?? "png";
                int quality = (int)JpegQualitySlider.Value;
                int count = _recordedFrameCount;

                bool wasRunning = _isRunning;
                if (_isRunning) {
                    _camera.Stop();
                    _isRunning = false;
                    StartStopBtn.Content = "Start Preview";
                    StartStopBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
                    Log("Capture paused for batch save.");
                }

                _isBatchSaving = true;
                BatchSaveBtn.IsEnabled = false;
                StartStopBtn.IsEnabled = false;
                RecordBtn.IsEnabled = false;
                Log($"Starting batch format encoding of {count} RAW frames to {folder} as {format.ToUpper()}...");

                try
                {
                    await Task.Run(() =>
                    {
                        if (_recordInMemoryOnly) {
                            RawFrameChunk[] frames;
                            lock (_recordedFramesLock) {
                                frames = _recordedFramesMemory.ToArray();
                            }
                            for (int i = 0; i < frames.Length; i++) {
                                string path = Path.Combine(folder, $"frame_{i:D5}.{format}");
                                SaveFrameChunk(frames[i], path, format, quality);

                                if ((i + 1) % 10 == 0 || i + 1 == frames.Length) {
                                    int done = i + 1;
                                    Dispatcher.BeginInvoke(() => Log($"Batch save progress: {done}/{frames.Length}"));
                                }
                            }
                        } else {
                            using (var rawFs = new FileStream(_spoolRawPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.SequentialScan))
                            {
                                using var br = new BinaryReader(rawFs);
                                for (int i = 0; i < count; i++)
                                {
                                    if (rawFs.Position >= rawFs.Length) break;
                                    int frameLen = br.ReadInt32();
                                    int frameWidth = br.ReadInt32();
                                    int frameHeight = br.ReadInt32();
                                    int frameStep = br.ReadInt32();
                                    if (frameLen <= 0 || frameLen > 100 * 1024 * 1024) break;
                                    byte[] frameBuffer = br.ReadBytes(frameLen);
                                    if (frameBuffer.Length < frameLen) break;

                                    string path = Path.Combine(folder, $"frame_{i:D5}.{format}");
                                    SaveFrameChunk(new RawFrameChunk
                                    {
                                        Buffer = frameBuffer,
                                        Length = frameLen,
                                        Width = frameWidth,
                                        Height = frameHeight,
                                        Step = frameStep
                                    }, path, format, quality);

                                    if ((i + 1) % 10 == 0 || i + 1 == count) {
                                        int done = i + 1;
                                        Dispatcher.BeginInvoke(() => Log($"Batch save progress: {done}/{count}"));
                                    }
                                }
                            }
                        }
                    });

                    Log("Batch encoding & save completed.");
                }
                catch (Exception ex)
                {
                    Log($"Error during batch save: {ex.Message}");
                    System.Windows.MessageBox.Show($"Batch save failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    _isBatchSaving = false;
                    StartStopBtn.IsEnabled = true;
                    RecordBtn.IsEnabled = true;
                    BatchSaveBtn.IsEnabled = _recordedFrameCount > 0;
                    ResolutionCombo.IsEnabled = !_isRunning && !_isRecording && !_isBatchSaving;

                    if (wasRunning && !_isRunning) {
                        if (_camera.Start()) {
                            _isRunning = true;
                            StartStopBtn.Content = "Stop Preview";
                            StartStopBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 53, 69));
                            Log("Capture resumed after batch save.");
                        }
                    }
                }
            }
        }

        private void ToggleCapture()
        {
            if (!_isRunning)
            {
                if (_camera.Start())
                {
                    _isRunning = true;
                    ApplyAutoControlsToCamera();
                    StartStopBtn.Content = "Stop Preview";
                    StartStopBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 53, 69)); // Bootstrap Danger Red
                    ResolutionCombo.IsEnabled = false;
                    Log("Preview started.");
                }
            }
            else
            {
                if (_camera.Stop())
                {
                    _isRunning = false;
                    StartStopBtn.Content = "Start Preview";
                    StartStopBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69)); // Bootstrap Success Green
                    ResolutionCombo.IsEnabled = !_isRecording && !_isBatchSaving;
                    Log("Preview stopped.");
                }
            }
        }

        private void UpdateFrame(IntPtr pBuffer, int width, int height, int step, int dataSize)
        {
            _frameWidth = width;
            _frameHeight = height;

            // Recording path is prioritized for throughput.
            if (_isRecording)
            {
                if (!_recordDurationStarted) {
                    _recordStartTimeUtc = DateTime.UtcNow;
                    _recordDurationStarted = true;
                    if (!_recordFirstFrameLogged) {
                        _recordFirstFrameLogged = true;
                        Dispatcher.BeginInvoke(() => Log("First frame received. Recording timer started."));
                    }
                }

                var elapsed = DateTime.UtcNow - _recordStartTimeUtc;
                if (_recordDuration.TotalSeconds > 0 && elapsed >= _recordDuration)
                {
                    _isRecording = false;
                    Dispatcher.BeginInvoke(() => RestoreFocusAfterRecording());
                    if (_recordInMemoryOnly) {
                        Dispatcher.BeginInvoke(() => {
                            RecordBtn.Content = "● Record (Memory Queue)";
                            Log($"Recording finished auto. Memory capture ready. Total {_recordedFrameCount} frames.");
                            BatchSaveBtn.IsEnabled = _recordedFrameCount > 0;
                        });
                    } else {
                        _rawQueue?.CompleteAdding();
                        Dispatcher.BeginInvoke(() => {
                            RecordBtn.Content = "● Record (Memory Queue)";
                            Log($"Recording finished auto. Spooling remaining frames...");
                        });
                    }
                }
                else
                {
                    byte[] recCopy = ArrayPool<byte>.Shared.Rent(dataSize);
                    Marshal.Copy(pBuffer, recCopy, 0, dataSize);
                    var chunk = new RawFrameChunk
                    {
                        Buffer = recCopy,
                        Length = dataSize,
                        Width = width,
                        Height = height,
                        Step = step
                    };
                    if (_recordInMemoryOnly) {
                        lock (_recordedFramesLock) {
                            _recordedFramesMemory.Add(chunk);
                        }
                        Interlocked.Increment(ref _recordedFrameCount);
                    } else if (_rawQueue is { IsAddingCompleted: false } queue) {
                        queue.Add(chunk);
                    } else {
                        ArrayPool<byte>.Shared.Return(recCopy);
                    }
                }

                // During recording, skip preview rendering to maximize capture throughput.
                return;
            }

            // Preview path (non-recording): throttle UI updates to reduce callback overhead.
            var nowUtc = DateTime.UtcNow;
            if (nowUtc - _lastPreviewUiUpdateUtc < _previewUiInterval) {
                return;
            }
            _lastPreviewUiUpdateUtc = nowUtc;

            if (_previewBuffer == null || _previewBuffer.Length != dataSize)
            {
                _previewBuffer = new byte[dataSize];
            }
            Marshal.Copy(pBuffer, _previewBuffer, 0, dataSize);

            if (!_isUIPainting)
            {
                _isUIPainting = true;
                Dispatcher.BeginInvoke(() =>
                {
                    try {
                        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
                        {
                            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, null);
                            PreviewImage.Source = _bitmap;
                        }

                        _bitmap.Lock();
                        unsafe
                        {
                            long targetStride = _bitmap.BackBufferStride;
                            long sourceStride = step;
                            long rowCount = Math.Min(height, _previewBuffer.Length / Math.Max(1, sourceStride));
                            
                            fixed (byte* pSrcBase = _previewBuffer) {
                                byte* pDst = (byte*)_bitmap.BackBuffer;
                                for (int y = 0; y < rowCount; y++) {
                                    Buffer.MemoryCopy(pSrcBase + (y * sourceStride), pDst + (y * targetStride), targetStride, Math.Min(sourceStride, targetStride));
                                }
                            }
                        }
                        _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                        _bitmap.Unlock();
                    } finally {
                        _isUIPainting = false;
                    }
                }, DispatcherPriority.Render);
            }
        }

        private static void SaveFrameChunk(RawFrameChunk frame, string path, string format, int quality)
        {
            SaveRawGrayFrame(frame.Buffer, frame.Length, frame.Width, frame.Height, frame.Step, path, format, quality);
        }

        private static void SaveRawGrayFrame(byte[] buffer, int length, int width, int height, int step, string path, string format, int quality)
        {
            if (width <= 0 || height <= 0) return;
            int safeStep = step > 0 ? step : width;
            int needed = safeStep * height;
            if (length < needed) return;

            byte[] gray = buffer;
            int grayStride = safeStep;
            if (safeStep != width) {
                gray = new byte[width * height];
                for (int y = 0; y < height; y++) {
                    Buffer.BlockCopy(buffer, y * safeStep, gray, y * width, width);
                }
                grayStride = width;
            }

            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, gray, grayStride);
            source.Freeze();

            BitmapEncoder encoder = format == "jpeg" ? new JpegBitmapEncoder { QualityLevel = quality } :
                                    format == "bmp" ? (BitmapEncoder)new BmpBitmapEncoder() : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var outFs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(outFs);
        }

        private bool TryGetSelectedResolution(out int width, out int height)
        {
            width = 3840;
            height = 2160;
            if (ResolutionCombo?.SelectedItem is not System.Windows.Controls.ComboBoxItem item) {
                return false;
            }
            string text = item.Content?.ToString() ?? string.Empty;
            string[] parts = text.Split('x');
            if (parts.Length != 2) {
                return false;
            }
            if (!int.TryParse(parts[0].Trim(), out width)) {
                return false;
            }
            if (!int.TryParse(parts[1].Trim(), out height)) {
                return false;
            }
            return true;
        }

        private void ResolutionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isRunning || _isRecording || _isBatchSaving) {
                Log("Resolution change is disabled while preview/record/save is active.");
                return;
            }

            if (!TryGetSelectedResolution(out int width, out int height)) {
                Log("Warning: Failed to parse selected resolution.");
                return;
            }

            Log($"Resolution preference changed to {width}x{height}. Reinitializing camera...");
            if (!ReinitializeCamera()) {
                Log("Warning: Reinitialize failed after resolution change.");
            }
        }

        private bool ReinitializeCamera()
        {
            _camera.OnFrameReceived -= UpdateFrame;
            _camera.Dispose();
            _camera = new CameraManager();
            return InitializeCamera();
        }

        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncingExposureSliders) return;
            long val = (long)e.NewValue;
            if (val > _appliedExposureMaxRaw) {
                ExposureSlider.Value = _appliedExposureMaxRaw;
                return;
            }
            if (val < ExposureMinRaw) {
                ExposureSlider.Value = ExposureMinRaw;
                return;
            }

            _syncingExposureSliders = true;
            _syncingExposureSliders = false;
            UpdateExposureDisplay(val);
            bool ok = _autoExposureSupported
                ? _camera.SetCameraControlValue(CameraControlProperty.Exposure, val, AutoExposureCheck.IsChecked == true)
                : _camera.SetExposure(val);
            if (!ok) {
                Log($"Warning: Failed to set exposure value {val}.");
            }
        }

        private void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            try {
                lock (_logFileLock) {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
            } catch {
                // ignore file logging failure
            }

            if (Dispatcher.CheckAccess()) {
                LogText.Text += line + "\n";
                LogText.ScrollToEnd();
            } else {
                Dispatcher.BeginInvoke(() => {
                    LogText.Text += line + "\n";
                    LogText.ScrollToEnd();
                });
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            ClearRecordedFramesMemory();
            _rawQueue?.CompleteAdding();
            _camera.Dispose();
            base.OnClosed(e);
        }

        private void ClearRecordedFramesMemory()
        {
            lock (_recordedFramesLock) {
                for (int i = 0; i < _recordedFramesMemory.Count; i++) {
                    ArrayPool<byte>.Shared.Return(_recordedFramesMemory[i].Buffer);
                }
                _recordedFramesMemory.Clear();
            }
        }
    }
}
