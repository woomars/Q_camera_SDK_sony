using System;
using System.Runtime.InteropServices;

namespace CameraSDK
{
    public class CameraManager : IDisposable
    {
        private const string DllPath = "CameraCore.dll";

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void NativeFrameCallback(IntPtr pBuffer, int width, int height, int step);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_Initialize();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Camera_Deinitialize();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetExposure(int microseconds);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetExposure(out int microseconds);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetGain(int value);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_GetGain(out int value);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetTriggerMode(int mode);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_TriggerCapture();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_SetFrameCallback(NativeFrameCallback callback);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_Start();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Camera_Stop();

        // High level event
        public event Action<IntPtr, int, int, int> OnFrameReceived;
        private NativeFrameCallback _nativeCallback; // Keep delegate alive

        public CameraManager()
        {
            _nativeCallback = (IntPtr pBuffer, int width, int height, int step) =>
            {
                OnFrameReceived?.Invoke(pBuffer, width, height, step);
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

        public bool SetExposure(int microseconds)
        {
            return Camera_SetExposure(microseconds) == 0;
        }

        public int GetExposure()
        {
            Camera_GetExposure(out int value);
            return value;
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

        public bool SetTriggerMode(TriggerMode mode)
        {
            return Camera_SetTriggerMode((int)mode) == 0;
        }

        public bool SoftwareTrigger()
        {
            return Camera_TriggerCapture() == 0;
        }

        public void Dispose()
        {
            Deinitialize();
        }
    }

    public enum TriggerMode
    {
        Continuous = 0,
        Software = 1,
        Hardware = 2
    }
}
