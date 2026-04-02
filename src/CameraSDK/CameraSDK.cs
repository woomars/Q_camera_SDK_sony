using System;
using System.Runtime.InteropServices;

namespace CameraSDK
{
    public class CameraManager : IDisposable
    {
        private const string DllPath = "CameraCore.dll";

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void NativeFrameCallback(IntPtr pBuffer, int width, int height, int step, int dataSize);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_Initialize();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Camera_Deinitialize();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetExposure(long value);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern long Camera_GetExposure();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetGain(int value);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetGain(out int value);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetFocusMode(int mode);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetFocus(int value);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetFocus(out int value);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetUSBSpeed();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern double Camera_GetCurrentFPS();
        
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern double Camera_GetNegotiatedFPS();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetNegotiatedWidth();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetNegotiatedHeight();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetNegotiatedSubtype();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern double Camera_GetTimestampFPS();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern long Camera_GetEstimatedDroppedFrames();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Camera_ResetPerfStats();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetPreferred4KMode(int mode);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetActive4KMode();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetPreferredResolution(int width, int height);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetLastHRESULT();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_IsTargetCameraDetected();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_IsSelectedCameraTarget();
        
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetProcAmpRange(int property, out long min, out long max, out long step, out long def, out long caps);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetProcAmpValue(int property, out long value, out long flags);
        
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetProcAmpValue(int property, long value, int useAuto);
        
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetCameraControlRange(int property, out long min, out long max, out long step, out long def, out long caps);
        
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetCameraControlValue(int property, long value, int useAuto);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetFrameCallback(NativeFrameCallback callback);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_Start();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_Stop();

        // 상위 수준 프레임 이벤트
        public event Action<IntPtr, int, int, int, int>? OnFrameReceived;
        private NativeFrameCallback _nativeCallback; // 네이티브 콜백 델리게이트가 해제되지 않도록 유지

        public CameraManager()
        {
            _nativeCallback = (IntPtr pBuffer, int width, int height, int step, int dataSize) =>
            {
                OnFrameReceived?.Invoke(pBuffer, width, height, step, dataSize);
            };
        }

        public bool Initialize()
        {
            int result = Camera_Initialize();
            if (result == 0)
            {
                Camera_SetFrameCallback(_nativeCallback);
                return true;
            }
            return false;
        }

        public void Deinitialize()
        {
            Camera_Deinitialize();
        }

        public bool Start()
        {
            return Camera_Start() == 0;
        }

        public bool Stop()
        {
            return Camera_Stop() == 0;
        }

        public bool SetExposure(long value)
        {
            return Camera_SetExposure(value) == 0;
        }

        public long GetExposure()
        {
            return Camera_GetExposure();
        }

        public bool SetGain(int value)
        {
            return Camera_SetGain(value) == 0;
        }

        public int GetGain()
        {
            Camera_GetGain(out int value);
            return value;
        }

        public bool SetFocusMode(FocusMode mode)
        {
            return Camera_SetFocusMode((int)mode) == 0;
        }

        public bool SetFocus(int value)
        {
            return Camera_SetFocus(value) == 0;
        }

        public int GetFocus()
        {
            Camera_GetFocus(out int value);
            return value;
        }

        public int GetUSBSpeed()
        {
            return Camera_GetUSBSpeed();
        }

        public double GetCurrentFPS()
        {
            return Camera_GetCurrentFPS();
        }

        public double GetNegotiatedFPS()
        {
            return Camera_GetNegotiatedFPS();
        }

        public int GetNegotiatedWidth()
        {
            return Camera_GetNegotiatedWidth();
        }

        public int GetNegotiatedHeight()
        {
            return Camera_GetNegotiatedHeight();
        }

        public int GetNegotiatedSubtype()
        {
            return Camera_GetNegotiatedSubtype();
        }

        public double GetTimestampFPS()
        {
            return Camera_GetTimestampFPS();
        }

        public long GetEstimatedDroppedFrames()
        {
            return Camera_GetEstimatedDroppedFrames();
        }

        public void ResetPerfStats()
        {
            Camera_ResetPerfStats();
        }

        public bool SetPreferred4KMode(Preferred4KMode mode)
        {
            return Camera_SetPreferred4KMode((int)mode) == 0;
        }

        public Preferred4KMode GetActive4KMode()
        {
            return (Preferred4KMode)Camera_GetActive4KMode();
        }

        public bool SetPreferredResolution(int width, int height)
        {
            return Camera_SetPreferredResolution(width, height) == 0;
        }

        public int GetLastHRESULT()
        {
            return Camera_GetLastHRESULT();
        }

        public bool IsTargetCameraDetected()
        {
            return Camera_IsTargetCameraDetected() != 0;
        }

        public bool IsSelectedCameraTarget()
        {
            return Camera_IsSelectedCameraTarget() != 0;
        }
        
        public bool TryGetProcAmpRange(ProcAmpProperty property, out long min, out long max, out long step, out long def, out long caps)
        {
            int rc = Camera_GetProcAmpRange((int)property, out min, out max, out step, out def, out caps);
            return rc == 0;
        }

        public bool TryGetProcAmpValue(ProcAmpProperty property, out long value, out long flags)
        {
            int rc = Camera_GetProcAmpValue((int)property, out value, out flags);
            return rc == 0;
        }
        
        public bool SetProcAmpValue(ProcAmpProperty property, long value, bool useAuto = false)
        {
            return Camera_SetProcAmpValue((int)property, value, useAuto ? 1 : 0) == 0;
        }
        
        public bool TryGetCameraControlRange(CameraControlProperty property, out long min, out long max, out long step, out long def, out long caps)
        {
            int rc = Camera_GetCameraControlRange((int)property, out min, out max, out step, out def, out caps);
            return rc == 0;
        }
        
        public bool SetCameraControlValue(CameraControlProperty property, long value, bool useAuto = false)
        {
            return Camera_SetCameraControlValue((int)property, value, useAuto ? 1 : 0) == 0;
        }

        public void Dispose()
        {
            Deinitialize();
        }
    }

    public enum Preferred4KMode
    {
        A_MjpegToNv12 = 0,
        B_Nv12Native = 1
    }

    public enum FocusMode
    {
        Manual = 0,
        Auto = 1
    }
    
    public enum ProcAmpProperty
    {
        Brightness = 0,
        Contrast = 1,
        Hue = 2,
        Saturation = 3,
        Sharpness = 4,
        Gamma = 5,
        WhiteBalance = 7,
        BacklightCompensation = 8,
        Gain = 9
    }

    public enum CameraControlProperty
    {
        Exposure = 4
    }
}
