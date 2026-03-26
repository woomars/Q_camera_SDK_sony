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
    CAMERASDK_API int Camera_SetExposure(long microseconds);
    CAMERASDK_API int Camera_GetExposure(long* pMicroseconds);

    CAMERASDK_API int Camera_SetGain(long value);
    CAMERASDK_API int Camera_GetGain(long* pValue);

    // Trigger modes: 0 = Continuous, 1 = Software Trigger, 2 = Hardware Trigger (CX3 GPIO)
    CAMERASDK_API int Camera_SetTriggerMode(int mode);

    // Manual software trigger
    CAMERASDK_API int Camera_TriggerCapture();

    // Callback for frame capture
    typedef void (__stdcall *FrameCallback)(unsigned char* pBuffer, long width, long height, long step);
    CAMERASDK_API int Camera_SetFrameCallback(FrameCallback callback);

    // Start/Stop preview / capture pipeline
    CAMERASDK_API int Camera_Start();
    CAMERASDK_API int Camera_Stop();
}
