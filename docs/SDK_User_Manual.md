# SDK User Manual

## 1. 개요
- 대상: Sony 13MP UVC 카메라 모듈
- 운영 정책: NV12 고정, 24fps 목표
- 구성:
  - Native SDK(C++): `CameraCore.dll`
  - Managed SDK(C#): `CameraSDK.dll`
  - Demo App(WPF): `CameraDemo`

## 2. 빌드
```bat
scripts\windows\doctor.cmd
scripts\windows\build_native.cmd
scripts\windows\build_managed.cmd
```

## 3. 실행
```bat
dotnet run --project src/CameraDemo/CameraDemo.csproj -c Debug
```

## 4. 주요 기능
- Preview 시작/정지
- 단기 메모리 큐 기반 녹화
- 일괄 포맷 저장(JPEG/PNG/BMP)
- 노출/게인/포커스 수동 제어
- AEC(자동 노출) 지원
- Brightness/Contrast/Saturation/Sharpness/Backlight 제어

## 5. UI 사용 방법
- `Start Preview`: 미리보기 시작
- `Record (Memory Queue)`: 녹화 시작/중지
- `Convert & Save All to Folder`: 녹화 프레임 저장
- Exposure/Gain/Focus 및 Control Panel 파라미터는 즉시 적용

## 6. 로그
- UI 로그: 하단 패널(선택/복사 가능)
- 파일 로그: `src/CameraDemo/bin/Debug/net8.0-windows/camera_demo.log`

## 7. 문제 해결
- `0x80070005`:
  - 다른 카메라 앱(Teams/Zoom/카메라 앱/기존 프로세스) 종료
  - Windows 카메라 권한 확인
  - USB 재연결 후 재시도
