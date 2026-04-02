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
        // ---- 카메라 런타임 / 프리뷰 상태 ----
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
        private int _recordDroppedByQueue = 0;
        private bool _hasUnsavedCapture = false;
        private readonly List<RawFrameChunk> _recordedFramesMemory = new List<RawFrameChunk>();
        private readonly object _recordedFramesLock = new object();

        // ---- 녹화 시간 / 프레임 관리 ----
        private bool _isRecording = false;
        private DateTime _recordStartTimeUtc;
        private bool _recordDurationStarted = false;
        private bool _recordFirstFrameLogged = false;
        private TimeSpan _recordDuration;
        private int _frameWidth;
        private int _frameHeight;
        private byte[]? _previewBuffer; // UI에 안전하게 표시하기 위한 관리 버퍼
        private bool _isUIPainting = false; // UI 표시 빈도 제어용 플래그
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
        private long _manualExposureValue = ExposureMinRaw;
        private FocusMode _selectedFocusMode = FocusMode.Auto;
        private FocusMode _focusModeBeforeRecording = FocusMode.Auto;
        private int _manualFocusValue = 80;
        private DateTime _lastPreviewUiUpdateUtc = DateTime.MinValue;
        private readonly TimeSpan _previewUiInterval = TimeSpan.FromMilliseconds(66); // 프리뷰 렌더링을 약 15fps로 제한
        private readonly object _logFileLock = new object();
        private readonly string _logFilePath;
        private const int MaxLogChars = 120000;
        // ---- Sony 대상 카메라 연결 상태 / 재연결 ----
        private bool _targetCameraReady = true;
        private bool _cameraReady = false;
        private int _lastInitHr = 0;
        private readonly DispatcherTimer _reconnectTimer;
        private bool _reconnectInProgress = false;
        private readonly DispatcherTimer _centerWarningTimer;
        private bool _resolutionChangeAllowedByUserStop = true;
        private bool _suppressResolutionSelectionChanged = false;
        private int _lastValidResolutionIndex = 0;

        public MainWindow()
        {
            InitializeComponent();
            _camera = new CameraManager();
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "camera_demo.log");
            _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _reconnectTimer.Tick += ReconnectTimer_Tick;
            _centerWarningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
            _centerWarningTimer.Tick += (s, e) =>
            {
                _centerWarningTimer.Stop();
                if (CenterWarningOverlay != null) {
                    CenterWarningOverlay.Visibility = Visibility.Collapsed;
                }
            };

            ConfigureExposureRangeForTargetFps();
            _manualExposureValue = (long)ExposureSlider.Value;

            GainSlider.ValueChanged += (s, e) => {
                int gain = (int)e.NewValue;
                GainValue.Text = gain.ToString();
                ApplyManualGainFromUi();
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
            _lastValidResolutionIndex = ResolutionCombo.SelectedIndex >= 0 ? ResolutionCombo.SelectedIndex : 0;

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
            StatusOnPreviewToggle.Checked += StatusOnPreviewToggle_Changed;
            StatusOnPreviewToggle.Unchecked += StatusOnPreviewToggle_Changed;

            if (!InitializeCamera()) {
                ShowInitFailureMessage(_lastInitHr);
                _reconnectTimer.Start();
            }
            UpdateFocusUiState();

            _statusTimer = new DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromMilliseconds(500);
            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();
            Log($"Log file: {_logFilePath}");
            UpdateStatusDisplayMode();
            UpdatePreviewStoppedOverlay();
            UpdateBatchSaveButtonState();
            UpdateResolutionControlState();

            if (_cameraReady && _targetCameraReady)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isRunning) ToggleCapture();
                }), DispatcherPriority.Background);
            }
        }

        private void TitleBarRegion_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }

            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaxRestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private bool InitializeCamera()
        {
            // 초기화/재초기화 경로마다 카메라 연결 상태를 다시 확인한다.
            _targetCameraReady = false;
            _cameraReady = false;
            _lastInitHr = 0;

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
                _lastInitHr = _camera.GetLastHRESULT();
                SetTargetCameraAlert($"Camera initialize failed (0x{_lastInitHr:X8}). Reconnect Sony camera and wait for auto-retry.");
                return false;
            }

            bool targetDetected = _camera.IsTargetCameraDetected();
            bool selectedTarget = _camera.IsSelectedCameraTarget();
            if (!targetDetected || !selectedTarget) {
                _targetCameraReady = false;
                _cameraReady = false;
                string alertMsg;
                if (!targetDetected) {
                    alertMsg = "Sony camera not detected. Check camera connection (USB cable/port/device manager).";
                } else {
                    alertMsg = "Wrong camera selected (likely internal camera). Please select/connect Sony camera.";
                }

                SetTargetCameraAlert(alertMsg);
                Log($"ERROR: {alertMsg}");
                Dispatcher.BeginInvoke(() =>
                {
                    System.Windows.MessageBox.Show(
                        (!targetDetected
                            ? "Sony camera was not detected.\n"
                            : "An internal camera is selected. Please select the Sony camera.\n") +
                        "Please check camera connection status.\n\n" +
                        "Checklist:\n" +
                        "1) Check Sony camera USB 3.0 cable/port\n" +
                        "2) Close other camera apps (Teams/Zoom/Camera app)\n" +
                        "3) Verify Sony/IMX258 detection in Device Manager",
                        "Camera Connection Check Required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
            } else {
                SetTargetCameraAlert(null);
                _targetCameraReady = true;
                _cameraReady = true;
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
            NegotiatedModeText.Text = $"{prefWidth} x {prefHeight}";
            if (nsub != Nv12Subtype || nfps < TargetFps - 0.5) {
                Log($"Warning: negotiated mode is {nw}x{nh} @ {nfps:F2} ({nsubName}), below NV12 {TargetFps:F0}fps target.");
            }
            if (ResolutionCombo != null && ResolutionCombo.SelectedIndex >= 0) {
                _lastValidResolutionIndex = ResolutionCombo.SelectedIndex;
            }
            return true;
        }

        private void ShowInitFailureMessage(int hr)
        {
            Log($"Error: Camera SDK failed to initialize. HRESULT=0x{hr:X8}");
            if (hr == unchecked((int)0x80070005)) {
                System.Windows.MessageBox.Show(
                    "Camera access was denied (0x80070005).\n" +
                    "1) Close Teams/Zoom/Camera app/browser camera tabs\n" +
                    "2) Turn ON Windows camera permission (including desktop apps)\n" +
                    "3) Reconnect Sony camera and wait briefly\n" +
                    "Auto-retry will continue.",
                    "Camera Occupied / Permission Issue",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            } else if (hr == unchecked((int)0xC00D36D5)) {
                System.Windows.MessageBox.Show(
                    "Camera device was not found (0xC00D36D5).\n" +
                    "Please check Sony camera USB-C connection.\n" +
                    "Auto-retry will continue.",
                    "Camera Not Detected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private async void ReconnectTimer_Tick(object? sender, EventArgs e)
        {
            // 녹화/저장 중이거나 재연결이 이미 진행 중이면 중복 진입을 막는다.
            if (_reconnectInProgress || _cameraReady || _isRunning || _isRecording || _isBatchSaving) {
                return;
            }

            _reconnectInProgress = true;
            try {
                Log("Auto-retry: trying to reconnect Sony camera...");
                await Task.Run(() =>
                {
                    try { _camera?.Dispose(); } catch { }
                });

                _camera = new CameraManager();
                bool ok = InitializeCamera();
                if (ok && _cameraReady && _targetCameraReady) {
                    Log("Auto-retry: Sony camera connected successfully.");
                    _reconnectTimer.Stop();
                }
            } finally {
                _reconnectInProgress = false;
            }
        }

        private void SetTargetCameraAlert(string? message)
        {
            bool show = !string.IsNullOrWhiteSpace(message);
            if (TargetCameraAlertBar != null) {
                TargetCameraAlertBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            if (TargetCameraAlertText != null) {
                TargetCameraAlertText.Text = show ? message! : string.Empty;
            }
        }

        private void ApplyUiValuesToCamera()
        {
            ApplyManualGainFromUi();
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
            FocusSlider.IsEnabled = manual && !_isRecording && !_isBatchSaving;
            FocusValue.IsEnabled = manual && !_isRecording && !_isBatchSaving;
            FocusModeCombo.IsEnabled = !_isRecording && !_isBatchSaving;
            FocusValue.Foreground = manual ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Gray;
        }

        private void UpdateRecordingPanelLockState()
        {
            // 녹화와 일괄 저장 중에는 동일한 잠금 규칙을 적용한다.
            bool locked = _isRecording || _isBatchSaving;
            CaptureDurationCombo.IsEnabled = !locked;

            AutoExposureCheck.IsEnabled = _autoExposureSupported && !locked;
            ExposureValue.IsEnabled = !locked;
            GainSlider.IsEnabled = !locked;
            GainValue.IsEnabled = !locked;
            FocusModeCombo.IsEnabled = !locked;
            FocusValue.IsEnabled = !locked;

            BrightnessSlider.IsEnabled = !locked;
            BrightnessValue.IsEnabled = !locked;
            ContrastSlider.IsEnabled = !locked;
            ContrastValue.IsEnabled = !locked;
            SaturationSlider.IsEnabled = !locked;
            SaturationValue.IsEnabled = !locked;
            SharpnessSlider.IsEnabled = !locked;
            SharpnessValue.IsEnabled = !locked;
            BacklightToggle.IsEnabled = _backlightSupported && !locked;

            if (locked) {
                ExposureSlider.IsEnabled = false;
                FocusSlider.IsEnabled = false;
            } else {
                UpdateAutoControlUiState();
                UpdateFocusUiState();
            }
            UpdateResolutionControlState();
        }

        private void LockFocusForRecording()
        {
            _focusModeBeforeRecording = _selectedFocusMode;
            _autoExposureBeforeRecording = AutoExposureCheck.IsChecked == true;
            _backlightOnBeforeRecording = BacklightToggle.IsChecked == true;

            // 녹화는 항상 수동 노출 조건으로 고정해 결과를 일정하게 유지한다.
            if (_autoExposureSupported) AutoExposureCheck.IsChecked = false;
            ApplyAutoControlsToCamera();

            AutoExposureCheck.IsEnabled = false;
            BacklightToggle.IsEnabled = false;

            if (_focusModeBeforeRecording == FocusMode.Auto) {
                // 녹화 직전 자동 초점 값을 읽어 온 뒤 수동으로 잠근다.
                _camera.SetFocusMode(FocusMode.Auto);
                Thread.Sleep(120);
                int afValue = _camera.GetFocus();
                if (afValue >= 0) {
                    _manualFocusValue = afValue;
                    FocusSlider.Value = _manualFocusValue;
                    FocusValue.Text = _manualFocusValue.ToString();
                }
            }

            _camera.SetFocusMode(FocusMode.Manual);
            _camera.SetFocus(_manualFocusValue);
            UpdateFocusUiState();
            Log("Focus locked to manual during recording. Auto exposure is disabled during recording.");
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
            UpdateRecordingPanelLockState();
            Log($"Focus mode restored: {_selectedFocusMode}");
        }

        private void UpdateActiveModeText()
        {
            ActiveModeText.Text = "NV12 -> NV12 (Fixed)";
            PanelActiveModeText.Text = ActiveModeText.Text;
        }

        private void StatusOnPreviewToggle_Changed(object sender, RoutedEventArgs e)
        {
            UpdateStatusDisplayMode();
        }

        private void UpdateStatusDisplayMode()
        {
            bool showOnPreview = StatusOnPreviewToggle.IsChecked == true;
            StatusOnPreviewToggle.Content = showOnPreview ? "ON" : "OFF";
            if (PreviewHudStatusContainer != null) {
                PreviewHudStatusContainer.Visibility = showOnPreview ? Visibility.Visible : Visibility.Collapsed;
            }
            if (PreviewBottomStatusCards != null) {
                PreviewBottomStatusCards.Visibility = showOnPreview ? Visibility.Visible : Visibility.Collapsed;
            }
            if (PanelStatusContainer != null) {
                PanelStatusContainer.Visibility = showOnPreview ? Visibility.Hidden : Visibility.Visible;
            }
        }

        private void UpdatePreviewStoppedOverlay()
        {
            if (PreviewStoppedOverlay != null) {
                PreviewStoppedOverlay.Visibility = _isRunning ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void UpdateSavingUiState(bool saving)
        {
            // 저장 중에는 경쟁 상태를 막기 위해 모든 상호작용 컨트롤을 잠근다.
            if (CaptureMenuBarBorder != null) {
                CaptureMenuBarBorder.IsEnabled = !saving;
            }
            if (RightMenuScroll != null) {
                RightMenuScroll.IsEnabled = !saving;
            }
            if (SaveSettingsPanel != null) {
                SaveSettingsPanel.IsEnabled = !saving;
            }
            if (SavingOverlay != null) {
                SavingOverlay.Visibility = saving ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SetSavingProgress(int done, int total)
        {
            if (SavingProgressText != null) {
                SavingProgressText.Text = $"{done} / {total}";
            }
        }

        private bool HasSavableFrames()
        {
            if (!_hasUnsavedCapture) {
                return false;
            }

            if (_recordedFrameCount <= 0) {
                return false;
            }

            if (_recordInMemoryOnly) {
                lock (_recordedFramesLock) {
                    return _recordedFramesMemory.Count > 0;
                }
            }

            return !string.IsNullOrWhiteSpace(_spoolRawPath)
                && File.Exists(_spoolRawPath)
                && _spoolTask != null
                && _spoolTask.IsCompleted;
        }

        private void UpdateBatchSaveButtonState()
        {
            if (BatchSaveBtn == null) return;
            BatchSaveBtn.IsEnabled = !_isRecording && !_isBatchSaving;
        }

        private bool CanChangeResolution()
        {
            return !_isRunning && !_isRecording && !_isBatchSaving && _resolutionChangeAllowedByUserStop;
        }

        private void UpdateResolutionControlState()
        {
            if (ResolutionCombo != null) {
                ResolutionCombo.IsEnabled = CanChangeResolution();
            }
        }

        private void ShowCenterWarning(string message)
        {
            if (CenterWarningText != null) {
                CenterWarningText.Text = message;
            }
            if (CenterWarningOverlay != null) {
                CenterWarningOverlay.Visibility = Visibility.Visible;
            }
            _centerWarningTimer.Stop();
            _centerWarningTimer.Start();
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
                NormalizeSigned32Range(ref min, ref max, ref step, ref def);

                if (min > max) {
                    // 드라이버가 비정상 범위를 반환해도 마지막으로 한 번 더 보정한다.
                    (min, max) = (max, min);
                }

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

        private static bool LooksLikeWrappedUInt32(long value)
        {
            return value >= 0 && value <= uint.MaxValue;
        }

        private static long ToSigned32(long value)
        {
            return unchecked((int)(uint)value);
        }

        private void NormalizeSigned32Range(ref long min, ref long max, ref long step, ref long def)
        {
            bool maybeWrapped = LooksLikeWrappedUInt32(min) && LooksLikeWrappedUInt32(max) && (min > int.MaxValue || max > int.MaxValue || min > max);
            if (maybeWrapped) {
                long origMin = min, origMax = max, origStep = step, origDef = def;
                min = ToSigned32(min);
                max = ToSigned32(max);
                if (LooksLikeWrappedUInt32(step)) step = ToSigned32(step);
                if (LooksLikeWrappedUInt32(def)) def = ToSigned32(def);
                Log($"ProcAmp range normalized (wrapped int32): min {origMin}->{min}, max {origMax}->{max}, step {origStep}->{step}, def {origDef}->{def}");
            }

            if (step <= 0) {
                step = 1;
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

            if (AutoExposureCheck.IsChecked != true) {
                _manualExposureValue = (long)ExposureSlider.Value;
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
            long targetExposure = autoExp ? (long)ExposureSlider.Value : _manualExposureValue;

            if (_autoExposureSupported) {
                _camera.SetCameraControlValue(CameraControlProperty.Exposure, targetExposure, autoExp);
            } else {
                _camera.SetExposure(targetExposure);
            }

            if (!autoExp) {
                ApplyManualGainFromUi();
            }
        }

        private void ApplyManualGainFromUi()
        {
            if (_autoExposureSupported && AutoExposureCheck.IsChecked == true) {
                return;
            }

            int gain = (int)GainSlider.Value;
            bool ok = _camera.SetGain(gain);
            GainValue.Foreground = ok ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.OrangeRed;
            if (!ok && !_gainSetWarningShown) {
                _gainSetWarningShown = true;
                Log("Warning: Gain control was rejected by camera/driver. Check UVC gain support.");
            }
        }

        private void UpdateAutoControlUiState()
        {
            bool autoExp = AutoExposureCheck.IsChecked == true && AutoExposureCheck.IsEnabled;

            ExposureSlider.IsEnabled = !autoExp && !_isRecording && !_isBatchSaving;
            ExposureValue.IsEnabled = !autoExp && !_isRecording && !_isBatchSaving;
            GainSlider.IsEnabled = !autoExp && !_isRecording && !_isBatchSaving;
            GainValue.IsEnabled = !autoExp && !_isRecording && !_isBatchSaving;
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
            string usbText = speed == 3 ? "USB 3.0 (SuperSpeed)" : (speed == 2 ? "USB 2.0 (HighSpeed)" : "Unknown");
            UsbSpeedText.Text = usbText;
            PanelUsbSpeedText.Text = usbText;
            UsbSpeedText.Foreground = speed == 3 ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Orange;
            PanelUsbSpeedText.Foreground = UsbSpeedText.Foreground;

            double fps = _camera.GetCurrentFPS();
            double tsFps = _camera.GetTimestampFPS();
            long dropped = _camera.GetEstimatedDroppedFrames();
            ActualFpsText.Text = tsFps > 0.0 ? $"{fps:F2} fps (ts {tsFps:F2}, drop {dropped})" : $"{fps:F2} fps";
            ActualFpsText.Foreground = fps >= TargetFps - 1.0 ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.White;
            PanelFpsText.Text = ActualFpsText.Text;
            PanelFpsText.Foreground = ActualFpsText.Foreground;

            long appliedExposureRaw = _camera.GetExposure();
            int appliedExposure = unchecked((int)appliedExposureRaw);
            int appliedGain = _camera.GetGain();
            bool autoExp = _autoExposureSupported && AutoExposureCheck.IsChecked == true;
            string appliedText = autoExp ? "Auto Exposure" : $"Exp {appliedExposure}, Gain {appliedGain}";
            AppliedAeText.Text = appliedText;
            PanelAppliedAeText.Text = appliedText;

            UpdateActiveModeText();

            if (_frameWidth > 0 && _frameHeight > 0) {
                CurrentResolutionText.Text = $"{_frameWidth} x {_frameHeight}";
            }
            NegotiatedModeText.Text = CurrentResolutionText.Text;
            PanelNegotiatedModeText.Text = CurrentResolutionText.Text;
        }

        private void RecordBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_cameraReady || !_targetCameraReady) {
                System.Windows.MessageBox.Show("Sony camera is not ready. Please check camera connection first.", "Camera Connection Check Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_isRunning) {
                System.Windows.MessageBox.Show("Please Start Preview first.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_isRecording && _autoExposureSupported && AutoExposureCheck.IsChecked == true) {
                Log("Recording blocked: Auto Exposure mode is enabled.");
                ShowCenterWarning("AUTO EXPOSURE ON\nRecording is blocked.");
                return;
            }

            if (!_isRecording) {
                bool autoFocusWarn = _selectedFocusMode == FocusMode.Auto;
                if (autoFocusWarn) {
                    string warning =
                        "Focus mode is Auto. Right before recording, autofocus will run once, then recording proceeds in manual focus.\n" +
                        $"\nManual values to apply:\nExposure={ExposureSlider.Value:0}, Gain={GainSlider.Value:0}, Focus={_manualFocusValue:0}\n\n" +
                        "Do you want to continue recording?";
                    var confirm = System.Windows.MessageBox.Show(
                        warning,
                        "Recording Confirmation",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) {
                        Log("Recording canceled by user after auto mode warning.");
                        return;
                    }
                }
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
                // 수동 녹화를 중지한다
                _isRecording = false;
                RestoreFocusAfterRecording();
                RecordBtn.Content = "● Start Recording";
                if (RecordingOverlay != null) RecordingOverlay.Visibility = Visibility.Collapsed;
                if (_recordInMemoryOnly) {
                    Log($"Recording stopped manually. Memory capture completed. Total {_recordedFrameCount} frames (queue drop {_recordDroppedByQueue}).");
                    _hasUnsavedCapture = _recordedFrameCount > 0;
                    UpdateBatchSaveButtonState();
                } else {
                    _rawQueue?.CompleteAdding(); // 백그라운드 스풀러가 남은 항목을 마저 처리하도록 둔다
                    Log($"Recording stopped manually. Spooling remaining frames to SSD... (queue drop {_recordDroppedByQueue})");
                }
            } else {
                ClearRecordedFramesMemory();
                _recordedFrameCount = 0;
                _recordDroppedByQueue = 0;
                _hasUnsavedCapture = false;
                UpdateBatchSaveButtonState();
                _camera.ResetPerfStats();
                UpdateRecordingPanelLockState();
                
                string sel = ((System.Windows.Controls.ComboBoxItem)CaptureDurationCombo.SelectedItem).Content.ToString() ?? "Continuous";
                if (sel == "Continuous") _recordDuration = TimeSpan.Zero;
                else _recordDuration = TimeSpan.FromSeconds(double.Parse(sel.Replace("s", "")));
                _recordInMemoryOnly = _recordDuration.TotalSeconds > 0 && _recordDuration.TotalSeconds <= 3.0;

                string tempRoot = Path.Combine(Path.GetTempPath(), "CameraSDK_Temp");
                if (!Directory.Exists(tempRoot)) Directory.CreateDirectory(tempRoot);
                _tempDirectory = Path.Combine(tempRoot, $"session_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
                Directory.CreateDirectory(_tempDirectory);

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
                            Log($"SSD Spooling finished. Total {_recordedFrameCount} frames saved to temp. Queue drop {_recordDroppedByQueue}.");
                            _hasUnsavedCapture = _recordedFrameCount > 0;
                            UpdateBatchSaveButtonState();
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
                UpdateRecordingPanelLockState();
                RecordBtn.Content = "■ Stop Recording";
                if (RecordingOverlay != null) RecordingOverlay.Visibility = Visibility.Visible;
                Log($"Recording armed ({sel}). Timer starts on first received frame.");
            }
        }

        private async void BatchSaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchSaving) {
                return;
            }

            if (!HasSavableFrames()) {
                Log("Batch save blocked: no captured images available to save.");
                ShowCenterWarning("No captured images available to save.");
                return;
            }

            // 저장 위치를 고르는 동안 상태가 바뀌지 않도록 먼저 모든 컨트롤을 잠근다.
            _isBatchSaving = true;
            UpdateBatchSaveButtonState();
            StartStopBtn.IsEnabled = false;
            RecordBtn.IsEnabled = false;
            UpdateRecordingPanelLockState();
            UpdateSavingUiState(true);

            string? folder = null;
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Select a folder to save images.";
                fbd.UseDescriptionForTitle = true;
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                    folder = fbd.SelectedPath;
                }
            }

            if (string.IsNullOrWhiteSpace(folder)) {
                _isBatchSaving = false;
                StartStopBtn.IsEnabled = true;
                RecordBtn.IsEnabled = true;
                UpdateBatchSaveButtonState();
                UpdateRecordingPanelLockState();
                UpdateSavingUiState(false);
                Log("Batch save canceled (folder selection canceled).");
                return;
            }

            string format = ((System.Windows.Controls.ComboBoxItem)FormatCombo.SelectedItem).Content.ToString()?.ToLower() ?? "png";
            int quality = (int)JpegQualitySlider.Value;
            int count = _recordedFrameCount;
            Log($"Selected save folder: {folder}");

            bool wasRunning = _isRunning;
            if (_isRunning) {
                // 저장은 안정된 버퍼 기준으로 처리하므로 UI/캡처 충돌을 막기 위해 먼저 프리뷰를 멈춘다.
                _resolutionChangeAllowedByUserStop = false;
                var stopTask = Task.Run(() => _camera.Stop());
                var stopDone = await Task.WhenAny(stopTask, Task.Delay(2500));
                if (stopDone == stopTask && stopTask.Result) {
                    _isRunning = false;
                    StartStopBtn.Content = "Start Preview";
                    UpdatePreviewStoppedOverlay();
                    Log("Preview paused for batch save.");
                } else {
                    Log("Error: Preview stop timeout during batch save. Save aborted to avoid app freeze.");
                    System.Windows.MessageBox.Show(
                        "Preview stop is delayed, so saving was aborted.\n" +
                        "Press Stop Preview again after a moment, then retry saving.",
                        "Save Aborted",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    _isBatchSaving = false;
                    StartStopBtn.IsEnabled = true;
                    RecordBtn.IsEnabled = true;
                    UpdateBatchSaveButtonState();
                    UpdateRecordingPanelLockState();
                    UpdateSavingUiState(false);
                    return;
                }
            }

            SetSavingProgress(0, count);
            Log($"Starting batch format encoding of {count} RAW frames to {folder} as {format.ToUpper()}...");
            var batchStartUtc = DateTime.UtcNow;

            try
            {
                // 비트맵 인코딩은 STA 스레드에서 수행해 UI 응답성을 유지한다.
                await RunOnStaThreadAsync(() =>
                {
                    var lastProgressAt = DateTime.UtcNow;
                    if (_recordInMemoryOnly) {
                        RawFrameChunk[] frames;
                        lock (_recordedFramesLock) {
                            frames = _recordedFramesMemory.ToArray();
                        }
                        for (int i = 0; i < frames.Length; i++) {
                            string path = Path.Combine(folder, $"frame_{i:D5}.{format}");
                            SaveFrameChunk(frames[i], path, format, quality);
                            ArrayPool<byte>.Shared.Return(frames[i].Buffer);

                            if ((DateTime.UtcNow - lastProgressAt).TotalMilliseconds >= 900 || i + 1 == frames.Length) {
                                int done = i + 1;
                                lastProgressAt = DateTime.UtcNow;
                                Dispatcher.BeginInvoke(() =>
                                {
                                    SetSavingProgress(done, frames.Length);
                                    Log($"Batch save progress: {done}/{frames.Length}");
                                });
                            }
                        }
                        lock (_recordedFramesLock) {
                            _recordedFramesMemory.Clear();
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

                                if ((DateTime.UtcNow - lastProgressAt).TotalMilliseconds >= 900 || i + 1 == count) {
                                    int done = i + 1;
                                    lastProgressAt = DateTime.UtcNow;
                                    Dispatcher.BeginInvoke(() =>
                                    {
                                        SetSavingProgress(done, count);
                                        Log($"Batch save progress: {done}/{count}");
                                    });
                                }
                            }
                        }
                    }
                });

                Log("Batch encoding & save completed.");
                Log($"Batch save elapsed: {(DateTime.UtcNow - batchStartUtc).TotalSeconds:F2}s");
                Log($"Saved to: {folder}");
                _hasUnsavedCapture = false;
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
                UpdateBatchSaveButtonState();
                UpdateResolutionControlState();
                UpdateRecordingPanelLockState();
                UpdateSavingUiState(false);

                if (wasRunning && !_isRunning) {
                    if (_camera.Start()) {
                        _isRunning = true;
                        _resolutionChangeAllowedByUserStop = false;
                        StartStopBtn.Content = "Stop Preview";
                        UpdatePreviewStoppedOverlay();
                        // Start/Stop 버튼 배경색 커스터마이즈 예시
                        Log("Preview resumed after batch save.");
                    }
                }
            }
        }

        private static Task RunOnStaThreadAsync(Action action)
        {
            var tcs = new TaskCompletionSource<object?>();
            var thread = new Thread(() =>
            {
                try {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception ex) {
                    tcs.SetException(ex);
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }

        private async void ToggleCapture()
        {
            if (!_cameraReady || !_targetCameraReady)
            {
                System.Windows.MessageBox.Show(
                    "Sony camera is not ready. Please check connection.\nThe app is auto-retrying.",
                    "Camera Connection Check Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                if (!_reconnectTimer.IsEnabled) _reconnectTimer.Start();
                return;
            }

            if (!_isRunning)
            {
                if (_camera.Start())
                {
                    _isRunning = true;
                    _resolutionChangeAllowedByUserStop = false;
                    ApplyAutoControlsToCamera();
                    StartStopBtn.Content = "Stop Preview";
                    UpdatePreviewStoppedOverlay();
                    // Start/Stop 버튼 배경색 커스터마이즈 예시 // Bootstrap Danger Red
                    UpdateResolutionControlState();
                    UpdateRecordingPanelLockState();
                    Log("Preview started.");
                }
            }
            else
            {
                StartStopBtn.IsEnabled = false;
                var stopTask = Task.Run(() => _camera.Stop());
                var finished = await Task.WhenAny(stopTask, Task.Delay(3000));
                if (finished != stopTask) {
                    StartStopBtn.IsEnabled = true;
                    Log("Warning: Camera stop timeout during hot-plug. Please retry after a moment.");
                    System.Windows.MessageBox.Show(
                        "Camera stop response is delayed.\n" +
                        "If this happened right after USB reconnect, please retry in a moment.",
                        "Camera Response Delay",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                bool stopped = stopTask.Result;
                StartStopBtn.IsEnabled = true;
                if (stopped)
                {
                    _isRunning = false;
                    _resolutionChangeAllowedByUserStop = true;
                    StartStopBtn.Content = "Start Preview";
                    UpdatePreviewStoppedOverlay();
                    // Start/Stop 버튼 배경색 커스터마이즈 예시
                    UpdateResolutionControlState();
                    UpdateRecordingPanelLockState();
                    Log("Preview stopped.");
                }
                else
                {
                    Log("Warning: Preview stop failed.");
                }
            }
        }

        private void UpdateFrame(IntPtr pBuffer, int width, int height, int step, int dataSize)
        {
            _frameWidth = width;
            _frameHeight = height;

            // 녹화 중에는 처리량을 우선해 프리뷰 렌더링을 생략한다.
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
                    Dispatcher.BeginInvoke(() => {
                        RestoreFocusAfterRecording();
                        RecordBtn.Content = "● Start Recording";
                        if (RecordingOverlay != null) RecordingOverlay.Visibility = Visibility.Collapsed;
                        if (_recordInMemoryOnly) {
                            Log($"Auto recording finished. Memory capture completed. Total {_recordedFrameCount} frames (queue drop {_recordDroppedByQueue}).");
                            _hasUnsavedCapture = _recordedFrameCount > 0;
                            UpdateBatchSaveButtonState();
                        } else {
                            _rawQueue?.CompleteAdding();
                            Log($"Auto recording finished. Spooling remaining frames... (queue drop {_recordDroppedByQueue})");
                        }
                    });
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
                        if (!queue.TryAdd(chunk)) {
                            Interlocked.Increment(ref _recordDroppedByQueue);
                            ArrayPool<byte>.Shared.Return(recCopy);
                        }
                    } else {
                        ArrayPool<byte>.Shared.Return(recCopy);
                    }
                }

                // 녹화 중에는 캡처 처리량을 높이기 위해 프리뷰 렌더링을 건너뛴다.
                return;
            }

            // 일반 프리뷰 경로에서는 UI 갱신 빈도를 줄여 콜백 부담을 낮춘다.
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
            if (_suppressResolutionSelectionChanged) {
                return;
            }

            if (!CanChangeResolution()) {
                int attempted = ResolutionCombo.SelectedIndex;
                _suppressResolutionSelectionChanged = true;
                ResolutionCombo.SelectedIndex = _lastValidResolutionIndex;
                _suppressResolutionSelectionChanged = false;
                if (attempted != _lastValidResolutionIndex) {
                    Log("Resolution change blocked: Preview must be in user-stopped state.");
                }
                return;
            }

            if (!TryGetSelectedResolution(out int width, out int height)) {
                Log("Warning: Failed to parse selected resolution.");
                _suppressResolutionSelectionChanged = true;
                ResolutionCombo.SelectedIndex = _lastValidResolutionIndex;
                _suppressResolutionSelectionChanged = false;
                return;
            }

            Log($"Resolution preference changed to {width}x{height}. Reinitializing camera...");
            if (!ReinitializeCamera()) {
                Log("Warning: Reinitialize failed after resolution change.");
                _suppressResolutionSelectionChanged = true;
                ResolutionCombo.SelectedIndex = _lastValidResolutionIndex;
                _suppressResolutionSelectionChanged = false;
                return;
            }
            _lastValidResolutionIndex = ResolutionCombo.SelectedIndex;
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
            if (AutoExposureCheck.IsChecked != true) {
                _manualExposureValue = val;
            }
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
                // 파일 로그 기록 실패는 동작을 막지 않고 무시한다
            }

            if (Dispatcher.CheckAccess()) {
                if (LogText.Text.Length > MaxLogChars) {
                    int trim = LogText.Text.Length - MaxLogChars + 8000;
                    if (trim > 0 && trim < LogText.Text.Length) {
                        LogText.Text = LogText.Text.Substring(trim);
                    }
                }
                LogText.AppendText(line + Environment.NewLine);
                LogText.ScrollToEnd();
            } else {
                Dispatcher.BeginInvoke(() => {
                    if (LogText.Text.Length > MaxLogChars) {
                        int trim = LogText.Text.Length - MaxLogChars + 8000;
                        if (trim > 0 && trim < LogText.Text.Length) {
                            LogText.Text = LogText.Text.Substring(trim);
                        }
                    }
                    LogText.AppendText(line + Environment.NewLine);
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
