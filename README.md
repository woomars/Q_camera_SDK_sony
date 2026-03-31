# Q Camera SDK (Sony 13MP UVC)

Windows 기반 UVC 카메라 제어/촬영 프로젝트입니다.  
현재 기준 운영 정책은 **NV12 고정 + 24fps 목표**입니다.

## 1. 현재 정책 (중요)
- 포맷: **NV12 고정 파이프라인**
- 목표 성능: **24fps**
- 기본 해상도: **3840x2160**
- 녹화 시작 가드: Negotiated 값이 `NV12` 이고 `>= 23.5fps` 일 때만 Record 허용
- 노출(Exposure): UI 제한 `-11 ~ -5` (UVC log2)

## 2. 프로젝트 구조
- `src/CameraCore` (C++): Media Foundation/UVC 네이티브 코어 (`CameraCore.dll`)
- `src/CameraSDK` (C#): P/Invoke 래퍼 (`CameraSDK.dll`)
- `src/CameraDemo` (C# WPF): 미리보기/녹화/일괄 저장 UI
- `src/CameraCli` (C#): FPS/파라미터 검증용 CLI

## 3. CameraDemo UI 반영 사항

### 3.1 오른쪽 패널 구성 순서
- `Image Settings`
- `Control Panel`
- `Camera Controls`

### 3.2 Image Settings
- `Auto Exposure (AEC)` 체크박스
- `Exposure`/`Gain`/`Focus Mode`/`Manual Focus`
- 슬라이더/값 입력 시 **즉시 반영**

### 3.3 Focus 동작
- `Auto Focus` / `Manual Focus` 모드 지원
- 녹화 중에는 포커스/자동보정 잠금(촬영 안정화 목적)

### 3.4 Control Panel
- Brightness / Contrast / Saturation / Sharpness
- Backlight ON/OFF 토글
- 각 값은 즉시 반영

### 3.5 Camera Controls
- Connection, Performance, Resolution
- Negotiated mode, Active pipeline
- Applied(Exp/Gain) 표시

### 3.6 로그 UI
- 하단 로그는 `TextBox(ReadOnly)` 기반
- 마우스 선택/복사 가능

### 3.7 Trigger 기능 상태
- `Trigger Mode`, `Software Trigger`, `Hardware Trigger` UI 제거
- Trigger 관련 이벤트 및 SDK API 제거 (현재 프로젝트 범위에서 미사용)

## 4. 로그 파일 위치
- 런타임 로그: `src/CameraDemo/bin/Debug/net8.0-windows/camera_demo.log`
- 하드웨어 capability 로그(도구 출력):  
  `src/CameraDemo/bin/Debug/net8.0-windows/hardware_capabilities.log`

## 5. 빌드/실행

## 5.1 권장 스크립트
```bat
scripts\windows\doctor.cmd
scripts\windows\build_native.cmd
scripts\windows\build_managed.cmd
scripts\windows\build_all.cmd
```

## 5.2 CameraDemo 실행
```bat
dotnet run --project src/CameraDemo/CameraDemo.csproj -c Debug
```

## 5.3 CameraCli 예시
```bat
dotnet run --project src/CameraCli/CameraCli.csproj -c Debug -- --mode nv12 --width 3840 --height 2160 --runs 5 --seconds 2 --warmup 1
```

## 6. 24fps 실측 참고
- 해상도별 24fps 실측 요약 문서:
  - `reports/24fps_resolution_summary_2026-03-31.md`

## 7. 주의사항
- `0x80070005`가 발생하면 카메라 점유/권한 문제일 가능성이 큼
  - Teams/Zoom/카메라앱/기존 Demo/CLI 종료 후 재시도
- USB 3.0 연결 상태 권장
- Visual Studio에서 실행이 잘 되어도, CLI/터미널 실행 시 점유 프로세스 때문에 실패할 수 있음
