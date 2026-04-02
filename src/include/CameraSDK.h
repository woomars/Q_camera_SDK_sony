#pragma once

#ifdef CAMERASDK_EXPORTS
#define CAMERASDK_API __declspec(dllexport)
#else
#define CAMERASDK_API __declspec(dllimport)
#endif

extern "C" {
    // Return codes
    #define CAM_SUCCESS 0
    #define CAM_ERROR_DEVICE_NOT_FOUND -1
    #define CAM_ERROR_INVALID_PARAMETER -2
    #define CAM_ERROR_CAPTURE_FAILED -3

    // Initialize the camera system
    CAMERASDK_API int Camera_Initialize();

    // Deinitialize and release resources
    CAMERASDK_API void Camera_Deinitialize();

    // Control parameters
    CAMERASDK_API int Camera_SetExposure(long value);
    CAMERASDK_API long Camera_GetExposure();

    CAMERASDK_API int Camera_SetGain(long value);
    CAMERASDK_API int Camera_GetGain(long* pValue);
    CAMERASDK_API int Camera_SetFocusMode(int mode); // 0 = Manual, 1 = Auto
    CAMERASDK_API int Camera_SetFocus(long value);
    CAMERASDK_API int Camera_GetFocus(long* pValue);

    CAMERASDK_API int Camera_GetUSBSpeed();
    CAMERASDK_API double Camera_GetCurrentFPS();
    CAMERASDK_API double Camera_GetNegotiatedFPS();
    CAMERASDK_API int Camera_GetNegotiatedWidth();
    CAMERASDK_API int Camera_GetNegotiatedHeight();
    CAMERASDK_API int Camera_GetNegotiatedSubtype(); // 0=Unknown, 1=NV12, 2=MJPG, 3=YUY2
    CAMERASDK_API double Camera_GetTimestampFPS();
    CAMERASDK_API long Camera_GetEstimatedDroppedFrames();
    CAMERASDK_API void Camera_ResetPerfStats();
    // 4K format mode: 0 = A(MJPEG->NV12), 1 = B(NV12 native)
    CAMERASDK_API int Camera_SetPreferred4KMode(int mode);
    CAMERASDK_API int Camera_GetActive4KMode();
    CAMERASDK_API int Camera_SetPreferredResolution(int width, int height);
    CAMERASDK_API int Camera_GetLastHRESULT();
    // 1 if Sony/target camera was detected during device enumeration, else 0
    CAMERASDK_API int Camera_IsTargetCameraDetected();
    // 1 if currently selected camera is the detected target camera, else 0
    CAMERASDK_API int Camera_IsSelectedCameraTarget();
    CAMERASDK_API int Camera_GetProcAmpRange(int property, long* pMin, long* pMax, long* pStep, long* pDefault, long* pCaps);
    CAMERASDK_API int Camera_GetProcAmpValue(int property, long* pValue, long* pFlags);
    CAMERASDK_API int Camera_SetProcAmpValue(int property, long value, int useAuto);
    CAMERASDK_API int Camera_GetCameraControlRange(int property, long* pMin, long* pMax, long* pStep, long* pDefault, long* pCaps);
    CAMERASDK_API int Camera_SetCameraControlValue(int property, long value, int useAuto);

    // Callback for frame capture
    typedef void (__stdcall *FrameCallback)(unsigned char* pBuffer, long width, long height, long step, long dataSize);
    CAMERASDK_API int Camera_SetFrameCallback(FrameCallback callback);

    // Start/Stop preview / capture pipeline
    CAMERASDK_API int Camera_Start();
    CAMERASDK_API int Camera_Stop();
}
