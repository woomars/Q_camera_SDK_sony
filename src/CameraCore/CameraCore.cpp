#include "CameraSDK.h"
#include <iostream>
#include <vector>
#include <thread>
#include <atomic>
#include <algorithm>
#include <windows.h>

// Mock implementation for the initial build without actual hardware connection

static std::atomic<bool> g_initialized{false};
static std::atomic<bool> g_running{false};
static long g_exposure = 1000; // default 1ms (1000 us)
static long g_gain = 0;
static int g_trigger_mode = 0; // default continuous
static FrameCallback g_callback = nullptr;

static std::thread g_capture_thread;

// Simulated frame loop
void CaptureLoop() {
    std::cout << "[CameraCore] Capture loop started." << std::endl;
    long width = 1280; // start with smaller size for smoother mock
    long height = 720;
    long step = width * 3;
    std::vector<unsigned char> buffer(width * height * 3, 0);

    int rectPos = 0;
    while (g_running) {
        if (g_trigger_mode == 0) { // Continuous
            // Draw a moving rectangle
            std::fill(buffer.begin(), buffer.end(), 50); // clear with dark gray
            
            for(int y = 100; y < 200; y++) {
                for(int x = rectPos; x < rectPos + 100; x++) {
                    if (x < width) {
                        int idx = (y * width + x) * 3;
                        buffer[idx] = 255;   // R
                        buffer[idx+1] = 0;   // G
                        buffer[idx+2] = 0;   // B
                    }
                }
            }
            rectPos = (rectPos + 10) % width;

            if (g_callback) {
                g_callback(buffer.data(), width, height, step);
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(40)); // ~25 fps
        } else {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }
    std::cout << "[CameraCore] Capture loop stopped." << std::endl;
}

extern "C" {

    CAMERASDK_API int Camera_Initialize() {
        if (g_initialized) return CAM_SUCCESS;
        // Search for CX3 UVC device here in the future
        std::cout << "[CameraCore] Initializing CX3 Camera..." << std::endl;
        g_initialized = true;
        return CAM_SUCCESS;
    }

    CAMERASDK_API void Camera_Deinitialize() {
        Camera_Stop();
        g_initialized = false;
        std::cout << "[CameraCore] Deinitialized." << std::endl;
    }

    CAMERASDK_API int Camera_SetExposure(long microseconds) {
        if (microseconds < 1) return CAM_ERROR_INVALID_PARAMETER;
        g_exposure = microseconds;
        std::cout << "[CameraCore] Setting exposure to " << g_exposure << " us" << std::endl;
        // In real impl, we use UVC Extension Unit or standard UVC control
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_GetExposure(long* pMicroseconds) {
        if (!pMicroseconds) return CAM_ERROR_INVALID_PARAMETER;
        *pMicroseconds = g_exposure;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_SetGain(long value) {
        g_gain = value;
        std::cout << "[CameraCore] Setting gain to " << g_gain << std::endl;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_GetGain(long* pValue) {
        if (!pValue) return CAM_ERROR_INVALID_PARAMETER;
        *pValue = g_gain;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_SetTriggerMode(int mode) {
        if (mode < 0 || mode > 2) return CAM_ERROR_INVALID_PARAMETER;
        g_trigger_mode = mode;
        std::cout << "[CameraCore] Trigger mode set to " << g_trigger_mode << std::endl;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_TriggerCapture() {
        if (!g_running) return CAM_ERROR_CAPTURE_FAILED;
        if (g_trigger_mode != 1) return CAM_ERROR_INVALID_PARAMETER; // Software trigger mode only
        
        std::cout << "[CameraCore] Software trigger received." << std::endl;
        if (g_callback) {
            // Capture one frame
            long width = 4208;
            long height = 3120;
            long step = width * 3;
            std::vector<unsigned char> buffer(width * height * 3, 128); // dummy gray
            g_callback(buffer.data(), width, height, step);
        }
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_SetFrameCallback(FrameCallback callback) {
        g_callback = callback;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_Start() {
        if (!g_initialized) return CAM_ERROR_DEVICE_NOT_FOUND;
        if (g_running) return CAM_SUCCESS;

        g_running = true;
        g_capture_thread = std::thread(CaptureLoop);
        std::cout << "[CameraCore] Capture started." << std::endl;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_Stop() {
        if (!g_running) return CAM_SUCCESS;
        g_running = false;
        if (g_capture_thread.joinable()) {
            g_capture_thread.join();
        }
        std::cout << "[CameraCore] Capture stopped." << std::endl;
        return CAM_SUCCESS;
    }
}
