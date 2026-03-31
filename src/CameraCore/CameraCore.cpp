#include "CameraSDK.h"
#include <iostream>
#include <vector>
#include <thread>
#include <atomic>
#include <windows.h>
#include <mfapi.h>
#include <mfplay.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <shlwapi.h>
#include <vidcap.h>
#include <ks.h>
#include <ksmedia.h>
#include <fstream>
#include <cmath>
#include <cstring>
#include <chrono>
#include <cstdlib>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "shlwapi.lib")

// Y800 (Monochrome 8-bit) GUID definition
static const GUID MFVideoFormat_Y800 = { 0x30303859, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };

static std::atomic<bool> g_initialized{false};
static std::atomic<bool> g_running{false};
static long g_exposure = 1000;
static long g_gain = 0;
static long g_focus = 0;
static int g_focus_mode = 1; // 0 manual, 1 auto
static int g_trigger_mode = 0; // 0: Continuous, 1: Software, 2: Hardware
static FrameCallback g_callback = nullptr;

static std::thread g_capture_thread;
static IMFSourceReader* g_pReader = nullptr;
static IMFMediaSource* g_pSource = nullptr;
static int g_usb_speed = 0; // 0: Unknown, 2: 2.0, 3: 3.0
static double g_current_fps = 0;
static long g_frame_count = 0;
static DWORD g_last_fps_time = 0;
static int g_preferred_4k_mode = 0; // fixed: 0 = MJPEG
static int g_active_4k_mode = 0;
static int g_preferred_width = 3840;
static int g_preferred_height = 2160;
static long g_frame_width = 0;
static long g_frame_height = 0;
static long g_frame_step = 0;
static long g_y_plane_size = 0;
static long g_last_hresult = S_OK;
static double g_negotiated_fps = 0.0;
static int g_negotiated_width = 0;
static int g_negotiated_height = 0;
static int g_negotiated_subtype = 0; // 0=Unknown, 1=NV12, 2=MJPG, 3=YUY2
static double g_timestamp_fps = 0.0;
static long g_estimated_dropped_frames = 0;
static LONGLONG g_prev_sample_ts = -1;
static LONGLONG g_ts_interval_acc = 0;
static long g_ts_interval_count = 0;

static void ResetPerfStatsInternal() {
    g_current_fps = 0;
    g_frame_count = 0;
    g_last_fps_time = GetTickCount();
    g_timestamp_fps = 0.0;
    g_estimated_dropped_frames = 0;
    g_prev_sample_ts = -1;
    g_ts_interval_acc = 0;
    g_ts_interval_count = 0;
}

template <class T> void SafeRelease(T** ppT) {
    if (*ppT) {
        (*ppT)->Release();
        *ppT = NULL;
    }
}

// Enumerate video capture devices and create a media source for the first one found
HRESULT CreateVideoCaptureDevice(IMFMediaSource** ppSource) {
    *ppSource = NULL;
    IMFAttributes* pConfig = NULL;
    IMFActivate** ppDevices = NULL;
    UINT32 count = 0;

    HRESULT hr = MFCreateAttributes(&pConfig, 1);
    if (SUCCEEDED(hr)) {
        hr = pConfig->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    }
    if (SUCCEEDED(hr)) {
        hr = MFEnumDeviceSources(pConfig, &ppDevices, &count);
    }
    if (SUCCEEDED(hr) && count > 0) {
        std::vector<UINT32> preferredHint;
        std::vector<UINT32> preferredExternal;
        std::vector<UINT32> fallback;
        char* hintEnvRaw = nullptr;
        size_t hintLen = 0;
        _dupenv_s(&hintEnvRaw, &hintLen, "CAM_DEVICE_HINT");
        const char* hintEnv = (hintEnvRaw && hintLen > 0) ? hintEnvRaw : nullptr;
        std::wstring hintW;
        if (hintEnv && hintEnv[0] != '\0') {
            int wlen = MultiByteToWideChar(CP_UTF8, 0, hintEnv, -1, NULL, 0);
            if (wlen > 1) {
                hintW.resize((size_t)wlen - 1);
                MultiByteToWideChar(CP_UTF8, 0, hintEnv, -1, &hintW[0], wlen);
                std::wcout << L"[CameraCore] CAM_DEVICE_HINT=" << hintW << std::endl;
            }
        }

        auto isInternalName = [](const std::wstring& name) {
            return
                (name.find(L"HP") != std::wstring::npos) ||
                (name.find(L"True Vision") != std::wstring::npos) ||
                (name.find(L"Integrated") != std::wstring::npos) ||
                (name.find(L"IR Camera") != std::wstring::npos) ||
                (name.find(L"Windows Hello") != std::wstring::npos) ||
                (name.find(L"Laptop") != std::wstring::npos) ||
                (name.find(L"Webcam") != std::wstring::npos);
        };

        for (UINT32 i = 0; i < count; i++) {
            WCHAR* szFriendlyName = NULL;
            UINT32 nameLength = 0;
            if (SUCCEEDED(ppDevices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &szFriendlyName, &nameLength))) {
                std::wcout << L"[CameraCore] Found Camera [" << i << L"]: " << szFriendlyName << std::endl;
                std::wstring name(szFriendlyName);
                bool isInternal = isInternalName(name);
                bool hintMatched = (!hintW.empty() && name.find(hintW) != std::wstring::npos);

                if (hintMatched) preferredHint.push_back(i);
                else if (!isInternal) preferredExternal.push_back(i);
                else fallback.push_back(i);
                CoTaskMemFree(szFriendlyName);
            } else {
                fallback.push_back(i);
            }
        }

        std::vector<UINT32> candidates;
        candidates.insert(candidates.end(), preferredHint.begin(), preferredHint.end());
        candidates.insert(candidates.end(), preferredExternal.begin(), preferredExternal.end());
        candidates.insert(candidates.end(), fallback.begin(), fallback.end());

        hr = MF_E_NOT_FOUND;
        HRESULT lastActivateHr = MF_E_NOT_FOUND;
        for (UINT32 idx : candidates) {
            std::cout << "[CameraCore] Trying Camera Index: " << idx << std::endl;
            HRESULT hrActivate = E_FAIL;
            const int activateRetry = 3;
            for (int attempt = 1; attempt <= activateRetry; attempt++) {
                hrActivate = ppDevices[idx]->ActivateObject(IID_PPV_ARGS(ppSource));
                if (SUCCEEDED(hrActivate) && *ppSource != NULL) {
                    break;
                }
                if (hrActivate == E_ACCESSDENIED && attempt < activateRetry) {
                    std::cout << "[CameraCore] Activate access denied for index " << idx
                              << " (attempt " << attempt << "/" << activateRetry
                              << "), retrying..." << std::endl;
                    Sleep(350);
                } else {
                    break;
                }
            }
            if (SUCCEEDED(hrActivate) && *ppSource != NULL) {
                hr = S_OK;
                std::cout << "[CameraCore] Selected Camera Index: " << idx << std::endl;
                break;
            }
            lastActivateHr = hrActivate;
            std::cout << "[CameraCore] Activate failed for index " << idx << ", hr=0x" << std::hex << hrActivate << std::dec << std::endl;
        }
        if (FAILED(hr) && FAILED(lastActivateHr)) {
            hr = lastActivateHr;
        }
        if (hintEnvRaw) {
            free(hintEnvRaw);
            hintEnvRaw = nullptr;
        }
    } else if (SUCCEEDED(hr) && count == 0) {
        std::cerr << "[CameraCore] No video capture devices found." << std::endl;
        hr = MF_E_NOT_FOUND;
    }

    for (DWORD i = 0; i < count; i++) {
        SafeRelease(&ppDevices[i]);
    }
    CoTaskMemFree(ppDevices);
    SafeRelease(&pConfig);

    return hr;
}

void CaptureLoop() {
    CoInitializeEx(NULL, COINIT_MULTITHREADED);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    std::cout << "[CameraCore] Capture loop started." << std::endl;
    HRESULT hr = S_OK;
    DWORD streamIndex = (DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM;
    DWORD flags = 0;
    LONGLONG llTimeStamp = 0;
    IMFSample* pSample = NULL;

    while (g_running) {
        if (g_trigger_mode == 0 || g_trigger_mode == 1) { 
            // Read sample synchronously
            hr = g_pReader->ReadSample(streamIndex, 0, NULL, &flags, &llTimeStamp, &pSample);
            
            if (FAILED(hr)) {
                std::cerr << "[CameraCore] ReadSample failed." << std::endl;
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
                continue;
            }
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
                std::cout << "[CameraCore] End of stream." << std::endl;
                break;
            }

            if (pSample) {
                if (g_prev_sample_ts >= 0) {
                    LONGLONG delta = llTimeStamp - g_prev_sample_ts;
                    if (delta > 0) {
                        g_ts_interval_acc += delta;
                        g_ts_interval_count++;
                        if (g_ts_interval_count >= 8) {
                            double avgDeltaSec = ((double)g_ts_interval_acc / (double)g_ts_interval_count) / 10000000.0;
                            if (avgDeltaSec > 0.0) {
                                g_timestamp_fps = 1.0 / avgDeltaSec;
                            }
                            g_ts_interval_acc = 0;
                            g_ts_interval_count = 0;
                        }

                        double refFps = g_negotiated_fps > 1.0 ? g_negotiated_fps : 30.0;
                        LONGLONG expected = (LONGLONG)(10000000.0 / refFps);
                        if (delta > (expected + (expected / 2))) {
                            long missed = (long)(delta / expected) - 1;
                            if (missed > 0) {
                                g_estimated_dropped_frames += missed;
                            }
                        }
                    }
                }
                g_prev_sample_ts = llTimeStamp;

                if (g_trigger_mode == 0 || (g_trigger_mode == 1 /* && g_software_trigger_flag */)) {
                    IMFMediaBuffer* pBuffer = NULL;
                    hr = pSample->GetBufferByIndex(0, &pBuffer);
                    if (FAILED(hr)) {
                        hr = pSample->ConvertToContiguousBuffer(&pBuffer);
                    }
                    if (SUCCEEDED(hr) && pBuffer) {
                        BYTE* pData = NULL;
                        DWORD cbData = 0;
                        hr = pBuffer->Lock(&pData, NULL, &cbData);
                        if (SUCCEEDED(hr) && pData) {
                            if (g_frame_width > 0 && g_frame_height > 0 && g_callback) {
                                long payloadSize = 0;
                                if (g_negotiated_subtype == 2) {
                                    payloadSize = (long)cbData; // MJPEG compressed frame size
                                } else {
                                    payloadSize = g_y_plane_size > 0 ? g_y_plane_size : (g_frame_width * g_frame_height);
                                    if ((DWORD)payloadSize > cbData) {
                                        payloadSize = (long)cbData;
                                    }
                                }
                                g_callback(pData, g_frame_width, g_frame_height, g_frame_step, payloadSize);
                            }
                            pBuffer->Unlock();
                        }
                        SafeRelease(&pBuffer);
                    }
                }
                SafeRelease(&pSample);
            }
            
            // Update FPS counter
            g_frame_count++;
            DWORD now = GetTickCount();
            if (now - g_last_fps_time >= 1000) {
                g_current_fps = (double)g_frame_count * 1000.0 / (now - g_last_fps_time);
                g_frame_count = 0;
                g_last_fps_time = now;
            }
        } else {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }
    std::cout << "[CameraCore] Capture loop stopped." << std::endl;
}

static void UpdateFrameLayoutFromCurrentType() {
    g_frame_width = 0;
    g_frame_height = 0;
    g_frame_step = 0;
    g_y_plane_size = 0;

    IMFMediaType* pCurrentType = NULL;
    if (FAILED(g_pReader->GetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, &pCurrentType))) {
        return;
    }

    UINT32 width = 0, height = 0;
    if (SUCCEEDED(MFGetAttributeSize(pCurrentType, MF_MT_FRAME_SIZE, &width, &height))) {
        g_frame_width = (long)width;
        g_frame_height = (long)height;
    }

    UINT32 strideAttr = 0;
    if (SUCCEEDED(pCurrentType->GetUINT32(MF_MT_DEFAULT_STRIDE, &strideAttr))) {
        g_frame_step = (long)strideAttr;
    }

    if (g_frame_step <= 0 && g_frame_width > 0) {
        g_frame_step = g_frame_width;
    }
    if (g_frame_step < 0) {
        g_frame_step = -g_frame_step;
    }

    if (g_frame_width > 0 && g_frame_height > 0 && g_frame_step > 0) {
        g_y_plane_size = g_frame_step * g_frame_height;
    }

    SafeRelease(&pCurrentType);
}

extern "C" {

    CAMERASDK_API int Camera_Initialize() {
        if (g_initialized) return CAM_SUCCESS;
        g_last_hresult = S_OK;

        HRESULT hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
        g_last_hresult = (long)hr;
        hr = MFStartup(MF_VERSION);
        g_last_hresult = (long)hr;
        if (FAILED(hr)) return CAM_ERROR_DEVICE_NOT_FOUND;

        const int initRetry = 3;
        for (int attempt = 1; attempt <= initRetry; attempt++) {
            hr = CreateVideoCaptureDevice(&g_pSource);
            g_last_hresult = (long)hr;
            if (SUCCEEDED(hr) && g_pSource) {
                break;
            }
            if (hr == E_ACCESSDENIED && attempt < initRetry) {
                std::cout << "[CameraCore] Camera initialize access denied (attempt "
                          << attempt << "/" << initRetry << "), retrying..." << std::endl;
                Sleep(500);
                continue;
            }
            break;
        }
        if (FAILED(hr) || !g_pSource) {
            MFShutdown();
            CoUninitialize();
            return CAM_ERROR_DEVICE_NOT_FOUND;
        }

        IMFAttributes* pAttributes = NULL;
        MFCreateAttributes(&pAttributes, 4);
        pAttributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, FALSE);
        pAttributes->SetUINT32(MF_READWRITE_DISABLE_CONVERTERS, TRUE);
        pAttributes->SetUINT32(MF_LOW_LATENCY, TRUE);

        hr = MFCreateSourceReaderFromMediaSource(g_pSource, pAttributes, &g_pReader);
        g_last_hresult = (long)hr;
        SafeRelease(&pAttributes);

        if (FAILED(hr)) {
            SafeRelease(&g_pSource);
            MFShutdown();
            CoUninitialize();
            return CAM_ERROR_DEVICE_NOT_FOUND;
        }

        // --- Negotiate preferred resolution @ 30FPS ---
        IMFMediaType* pPreferredMjpeg = NULL;
        IMFMediaType* pPreferredNv12 = NULL;
        DWORD typeIndex = 0;
        IMFMediaType* pType = NULL;
        double bestMjpegFps = 0;
        double bestNv12Fps = 0;

        while (SUCCEEDED(g_pReader->GetNativeMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, typeIndex, &pType))) {
            UINT32 width = 0, height = 0;
            MFGetAttributeSize(pType, MF_MT_FRAME_SIZE, &width, &height);
            
            UINT32 num = 0, den = 0;
            MFGetAttributeRatio(pType, MF_MT_FRAME_RATE, &num, &den);
            double fps = (den > 0) ? (double)num / den : 0.0;

            GUID subtype = GUID_NULL;
            pType->GetGUID(MF_MT_SUBTYPE, &subtype);

            if ((int)width == g_preferred_width && (int)height == g_preferred_height) {
                if (subtype == MFVideoFormat_MJPG && fps >= bestMjpegFps) {
                    bestMjpegFps = fps;
                    SafeRelease(&pPreferredMjpeg);
                    pPreferredMjpeg = pType;
                    pPreferredMjpeg->AddRef();
                } else if (subtype == MFVideoFormat_NV12 && fps >= bestNv12Fps) {
                    bestNv12Fps = fps;
                    SafeRelease(&pPreferredNv12);
                    pPreferredNv12 = pType;
                    pPreferredNv12->AddRef();
                }
            }
            SafeRelease(&pType);
            typeIndex++;
        }

        IMFMediaType* pChosenNative = (g_preferred_4k_mode == 1) ? pPreferredNv12 : pPreferredMjpeg;
        const char* chosenName = (g_preferred_4k_mode == 1) ? "NV12" : "MJPG";
        double chosenFps = (g_preferred_4k_mode == 1) ? bestNv12Fps : bestMjpegFps;

        if (pChosenNative) {
            std::cout << "[CameraCore] Forced Lock: " << g_preferred_width << "x" << g_preferred_height << " @ " << chosenFps << " fps (" << chosenName << ")." << std::endl;

            // Force selected native mode.
            MFSetAttributeSize(pChosenNative, MF_MT_FRAME_SIZE, (UINT32)g_preferred_width, (UINT32)g_preferred_height);
            MFSetAttributeRatio(pChosenNative, MF_MT_FRAME_RATE, 30, 1);
            HRESULT hrSetNative = g_pReader->SetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, NULL, pChosenNative);
            g_last_hresult = (long)hrSetNative;
            if (FAILED(hrSetNative)) {
                std::cout << "[CameraCore] WARNING: Failed to set chosen native 4K type. hr=" << std::hex << hrSetNative << std::dec << std::endl;
            }

            if (SUCCEEDED(hrSetNative)) {
                g_active_4k_mode = g_preferred_4k_mode;
            }

            IMFMediaType* pCurrent = NULL;
            UINT32 curW = 0, curH = 0, curNum = 0, curDen = 0;
            GUID curSubtype = GUID_NULL;
            if (SUCCEEDED(g_pReader->GetCurrentMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, &pCurrent))) {
                MFGetAttributeSize(pCurrent, MF_MT_FRAME_SIZE, &curW, &curH);
                MFGetAttributeRatio(pCurrent, MF_MT_FRAME_RATE, &curNum, &curDen);
                pCurrent->GetGUID(MF_MT_SUBTYPE, &curSubtype);
                const char* subtypeName = "OTHER";
                if (curSubtype == MFVideoFormat_NV12) subtypeName = "NV12";
                else if (curSubtype == MFVideoFormat_MJPG) subtypeName = "MJPG";
                else if (curSubtype == MFVideoFormat_YUY2) subtypeName = "YUY2";
                double curFps = (curDen > 0) ? (double)curNum / (double)curDen : 0.0;
                g_negotiated_width = (int)curW;
                g_negotiated_height = (int)curH;
                g_negotiated_fps = curFps;
                g_negotiated_subtype = (curSubtype == MFVideoFormat_NV12) ? 1 : ((curSubtype == MFVideoFormat_MJPG) ? 2 : ((curSubtype == MFVideoFormat_YUY2) ? 3 : 0));
                std::cout << "[CameraCore] Negotiated current type: " << curW << "x" << curH
                          << " @ " << curFps << " fps (" << subtypeName << ")" << std::endl;
                SafeRelease(&pCurrent);
            }
        } else {
            std::cout << "[CameraCore] WARNING: Target " << g_preferred_width << "x" << g_preferred_height << " format not found. Falling back to driver default." << std::endl;
            g_active_4k_mode = -1;
        }
        SafeRelease(&pPreferredMjpeg);
        SafeRelease(&pPreferredNv12);
        UpdateFrameLayoutFromCurrentType();

        // --- USB Speed Detection (Heuristic via Bandwidth) ---
        // USB 2.0 max bandwidth is ~60MB/s (theoretically). 
        // 4K @ 10fps YUY2 (Uncompressed) requires ~160MB/s.
        // If the native reader reports high-bandwidth uncompressed modes, it's USB 3.0.
        g_usb_speed = 2; // Default to 2.0
        DWORD checkIdx = 0;
        IMFMediaType* pCheckType = NULL;
        while (SUCCEEDED(g_pReader->GetNativeMediaType((DWORD)MF_SOURCE_READER_FIRST_VIDEO_STREAM, checkIdx++, &pCheckType))) {
            UINT32 w = 0, h = 0, num = 0, den = 0;
            GUID subtype;
            if (SUCCEEDED(pCheckType->GetGUID(MF_MT_SUBTYPE, &subtype)) && subtype == MFVideoFormat_YUY2) {
                if (SUCCEEDED(MFGetAttributeSize(pCheckType, MF_MT_FRAME_SIZE, &w, &h)) && 
                    SUCCEEDED(MFGetAttributeRatio(pCheckType, MF_MT_FRAME_RATE, &num, &den))) {
                    double fps = (den > 0) ? (double)num / den : 0.0;
                    double bw = (double)w * h * 2.0 * fps / (1024.0 * 1024.0);
                    if (bw > 100.0) { // If mode > 100MB/s, it's definitely USB 3.0
                        g_usb_speed = 3;
                        SafeRelease(&pCheckType);
                        break;
                    }
                }
            }
            SafeRelease(&pCheckType);
        }

        g_initialized = true;
        ResetPerfStatsInternal();
        std::cout << "[CameraCore] MF Camera Initialized." << std::endl;
        return CAM_SUCCESS;
    }

    CAMERASDK_API void Camera_Deinitialize() {
        if (!g_initialized) return;
        Camera_Stop();
        
        SafeRelease(&g_pReader);
        SafeRelease(&g_pSource);
        
        MFShutdown();
        CoUninitialize();
        g_initialized = false;
        std::cout << "[CameraCore] MF Camera Deinitialized." << std::endl;
    }

    CAMERASDK_API int Camera_SetExposure(long value) {
        if (!g_initialized || !g_pSource) return CAM_ERROR_DEVICE_NOT_FOUND;
        IAMCameraControl* pControl = NULL;
        int result = CAM_ERROR_CAPTURE_FAILED;
        if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pControl)))) {
            // value is log2(seconds), e.g. -11 is ~0.48ms
            if (SUCCEEDED(pControl->Set(CameraControl_Exposure, value, CameraControl_Flags_Manual))) {
                g_exposure = value;
                result = CAM_SUCCESS;
            }
            pControl->Release();
        }
        return result;
    }

    CAMERASDK_API int Camera_SetGain(long value) {
        if (!g_initialized || !g_pSource) return CAM_ERROR_DEVICE_NOT_FOUND;
        IAMVideoProcAmp* pProcAmp = NULL;
        int result = CAM_ERROR_CAPTURE_FAILED;
        if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pProcAmp)))) {
            long min = 0, max = 0, step = 0, def = 0, caps = 0;
            long setValue = value;
            if (SUCCEEDED(pProcAmp->GetRange(VideoProcAmp_Gain, &min, &max, &step, &def, &caps))) {
                if (setValue < min) setValue = min;
                if (setValue > max) setValue = max;
            }
            if (SUCCEEDED(pProcAmp->Set(VideoProcAmp_Gain, setValue, VideoProcAmp_Flags_Manual))) {
                g_gain = setValue;
                result = CAM_SUCCESS;
            }
            pProcAmp->Release();
        }
        return result;
    }

    CAMERASDK_API int Camera_SetFocusMode(int mode) {
        if (!g_initialized || !g_pSource) return CAM_ERROR_DEVICE_NOT_FOUND;
        if (mode < 0 || mode > 1) return CAM_ERROR_INVALID_PARAMETER;

        IAMCameraControl* pControl = NULL;
        int result = CAM_ERROR_CAPTURE_FAILED;
        if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pControl)))) {
            long flags = mode == 1 ? CameraControl_Flags_Auto : CameraControl_Flags_Manual;
            if (SUCCEEDED(pControl->Set(CameraControl_Focus, g_focus, flags))) {
                g_focus_mode = mode;
                result = CAM_SUCCESS;
            }
            pControl->Release();
        }
        return result;
    }

    CAMERASDK_API int Camera_SetFocus(long value) {
        if (!g_initialized || !g_pSource) return CAM_ERROR_DEVICE_NOT_FOUND;
        IAMCameraControl* pControl = NULL;
        int result = CAM_ERROR_CAPTURE_FAILED;
        if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pControl)))) {
            long min = 0, max = 0, step = 0, def = 0, caps = 0;
            long setValue = value;
            if (SUCCEEDED(pControl->GetRange(CameraControl_Focus, &min, &max, &step, &def, &caps))) {
                if (setValue < min) setValue = min;
                if (setValue > max) setValue = max;
            }
            if (SUCCEEDED(pControl->Set(CameraControl_Focus, setValue, CameraControl_Flags_Manual))) {
                g_focus = setValue;
                g_focus_mode = 0;
                result = CAM_SUCCESS;
            }
            pControl->Release();
        }
        return result;
    }

    CAMERASDK_API int Camera_GetFocus(long* pValue) {
        if (!pValue) return CAM_ERROR_INVALID_PARAMETER;
        if (g_initialized && g_pSource) {
            IAMCameraControl* pControl = NULL;
            if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pControl)))) {
                long value = g_focus;
                long flags = 0;
                if (SUCCEEDED(pControl->Get(CameraControl_Focus, &value, &flags))) {
                    g_focus = value;
                    g_focus_mode = (flags & CameraControl_Flags_Auto) ? 1 : 0;
                }
                pControl->Release();
            }
        }
        *pValue = g_focus;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_GetProcAmpRange(int property, long* pMin, long* pMax, long* pStep, long* pDefault, long* pCaps) {
        if (!g_initialized || !g_pSource) return CAM_ERROR_DEVICE_NOT_FOUND;
        if (!pMin || !pMax || !pStep || !pDefault || !pCaps) return CAM_ERROR_INVALID_PARAMETER;

        IAMVideoProcAmp* pProcAmp = NULL;
        int result = CAM_ERROR_CAPTURE_FAILED;
        if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pProcAmp)))) {
            if (SUCCEEDED(pProcAmp->GetRange((VideoProcAmpProperty)property, pMin, pMax, pStep, pDefault, pCaps))) {
                result = CAM_SUCCESS;
            }
            pProcAmp->Release();
        }
        return result;
    }

    CAMERASDK_API int Camera_SetProcAmpValue(int property, long value, int useAuto) {
        if (!g_initialized || !g_pSource) return CAM_ERROR_DEVICE_NOT_FOUND;

        IAMVideoProcAmp* pProcAmp = NULL;
        int result = CAM_ERROR_CAPTURE_FAILED;
        if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pProcAmp)))) {
            long min = 0, max = 0, step = 0, def = 0, caps = 0;
            long setValue = value;
            if (SUCCEEDED(pProcAmp->GetRange((VideoProcAmpProperty)property, &min, &max, &step, &def, &caps))) {
                if (setValue < min) setValue = min;
                if (setValue > max) setValue = max;
            }
            long flags = useAuto ? VideoProcAmp_Flags_Auto : VideoProcAmp_Flags_Manual;
            if (SUCCEEDED(pProcAmp->Set((VideoProcAmpProperty)property, setValue, flags))) {
                result = CAM_SUCCESS;
            }
            pProcAmp->Release();
        }
        return result;
    }

    CAMERASDK_API int Camera_GetCameraControlRange(int property, long* pMin, long* pMax, long* pStep, long* pDefault, long* pCaps) {
        if (!g_initialized || !g_pSource) return CAM_ERROR_DEVICE_NOT_FOUND;
        if (!pMin || !pMax || !pStep || !pDefault || !pCaps) return CAM_ERROR_INVALID_PARAMETER;

        IAMCameraControl* pControl = NULL;
        int result = CAM_ERROR_CAPTURE_FAILED;
        if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pControl)))) {
            if (SUCCEEDED(pControl->GetRange((CameraControlProperty)property, pMin, pMax, pStep, pDefault, pCaps))) {
                result = CAM_SUCCESS;
            }
            pControl->Release();
        }
        return result;
    }

    CAMERASDK_API int Camera_SetCameraControlValue(int property, long value, int useAuto) {
        if (!g_initialized || !g_pSource) return CAM_ERROR_DEVICE_NOT_FOUND;

        IAMCameraControl* pControl = NULL;
        int result = CAM_ERROR_CAPTURE_FAILED;
        if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pControl)))) {
            long min = 0, max = 0, step = 0, def = 0, caps = 0;
            long setValue = value;
            if (SUCCEEDED(pControl->GetRange((CameraControlProperty)property, &min, &max, &step, &def, &caps))) {
                if (setValue < min) setValue = min;
                if (setValue > max) setValue = max;
            }
            long flags = useAuto ? CameraControl_Flags_Auto : CameraControl_Flags_Manual;
            if (SUCCEEDED(pControl->Set((CameraControlProperty)property, setValue, flags))) {
                result = CAM_SUCCESS;
            }
            pControl->Release();
        }
        return result;
    }

    CAMERASDK_API int Camera_GetUSBSpeed() {
        return g_usb_speed;
    }

    CAMERASDK_API double Camera_GetCurrentFPS() {
        return g_current_fps;
    }

    CAMERASDK_API double Camera_GetNegotiatedFPS() {
        return g_negotiated_fps;
    }

    CAMERASDK_API int Camera_GetNegotiatedWidth() {
        return g_negotiated_width;
    }

    CAMERASDK_API int Camera_GetNegotiatedHeight() {
        return g_negotiated_height;
    }

    CAMERASDK_API int Camera_GetNegotiatedSubtype() {
        return g_negotiated_subtype;
    }

    CAMERASDK_API double Camera_GetTimestampFPS() {
        return g_timestamp_fps;
    }

    CAMERASDK_API long Camera_GetEstimatedDroppedFrames() {
        return g_estimated_dropped_frames;
    }

    CAMERASDK_API void Camera_ResetPerfStats() {
        ResetPerfStatsInternal();
    }

    CAMERASDK_API int Camera_SetPreferred4KMode(int mode) {
        if (g_running) return CAM_ERROR_CAPTURE_FAILED;
        if (mode != 0 && mode != 1) return CAM_ERROR_INVALID_PARAMETER;
        g_preferred_4k_mode = mode;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_GetActive4KMode() {
        return g_active_4k_mode;
    }

    CAMERASDK_API int Camera_SetPreferredResolution(int width, int height) {
        if (g_running) return CAM_ERROR_CAPTURE_FAILED;
        if (width <= 0 || height <= 0) return CAM_ERROR_INVALID_PARAMETER;
        g_preferred_width = width;
        g_preferred_height = height;
        return CAM_SUCCESS;
    }

    CAMERASDK_API long Camera_GetExposure() {
        if (g_initialized && g_pSource) {
            IAMCameraControl* pControl = NULL;
            if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pControl)))) {
                long value = g_exposure;
                long flags = 0;
                if (SUCCEEDED(pControl->Get(CameraControl_Exposure, &value, &flags))) {
                    g_exposure = value;
                }
                pControl->Release();
            }
        }
        return g_exposure;
    }

    CAMERASDK_API int Camera_GetGain(long* pValue) {
        if (!pValue) return CAM_ERROR_INVALID_PARAMETER;
        if (g_initialized && g_pSource) {
            IAMVideoProcAmp* pProcAmp = NULL;
            if (SUCCEEDED(g_pSource->QueryInterface(IID_PPV_ARGS(&pProcAmp)))) {
                long value = g_gain;
                long flags = 0;
                if (SUCCEEDED(pProcAmp->Get(VideoProcAmp_Gain, &value, &flags))) {
                    g_gain = value;
                }
                pProcAmp->Release();
            }
        }
        *pValue = g_gain;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_GetLastHRESULT() {
        return (int)g_last_hresult;
    }

    CAMERASDK_API int Camera_SetFrameCallback(FrameCallback callback) {
        g_callback = callback;
        return CAM_SUCCESS;
    }

    CAMERASDK_API int Camera_Start() {
        if (!g_initialized) return CAM_ERROR_DEVICE_NOT_FOUND;
        if (g_running) return CAM_SUCCESS;

        ResetPerfStatsInternal();
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
