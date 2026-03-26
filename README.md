# Q Camera SDK (Sony 13MP UVC)

본 프로젝트는 13MP UVC 호환 USB 카메라 모듈(Infineon CX3 기반)을 제어하기 위한 Windows 기반 SDK 및 데모 애플리케이션입니다.
머신 비전 장비 제어기의 Trigger 신호를 받아 논스톱 스냅샷(Flying Vision) 촬영을 제어하기 위해 설계되었습니다.

## 프로젝트 구조 (Architecture)

본 시스템은 두 개의 주요 계층으로 분리되어 있습니다:

- **src/CameraCore (C++ Native SDK):** 
  - UVC 드라이버(DirectShow / Media Foundation)를 통해 카메라 하드웨어에 접근하는 핵심 C++ 라이브러리입니다.
  - 노출(Exposure), 게인(Gain), 트리거 모드(Hardware/Software) 설정 API를 제공합니다.
  - `CameraCore.dll`로 빌드됩니다.

- **src/CameraSDK (C# Managed Wrapper):**
  - C++ `CameraCore.dll`의 함수들을 P/Invoke를 통해 C# 환경에서 쉽게 사용할 수 있도록 매핑한 .NET 라이브러리입니다.
  - `CameraSDK.dll`로 빌드됩니다.

- **src/CameraDemo (C# WPF 데모 앱):**
  - SDK 기능을 활용하여 카메라 실시간 스트리밍, 노출/게인 파라미터 제어, 트리거 캡처 동작을 테스트해 볼 수 있는 사용자 인터페이스(GUI) 프로그램입니다.

## 요구 사항 및 환경 (Prerequisites)

- **OS:** Windows 10 / 11 (x64)
- **개발 도구:** Visual Studio 2022 (Community 이상 권장)
  - 필요 워크로드: `.NET 데스크톱 개발`, `C++를 사용한 데스크톱 개발`
- **.NET SDK:** .NET 8.0 이상
- **CMake:** C++ Core 라이브러리 빌드 시 필요 (버전 3.10 이상)

## 빌드 및 실행 방법 (Build & Run)

### 1단계: C++ Core SDK 빌드
프로젝트 루트 폴더에서 CMake를 사용해 빌드 환경을 구성하고 컴파일합니다.
```bat
mkdir build
cmake -B build -S .
cmake --build build --config Release
```
성공 시 `build/bin/Release/CameraCore.dll` 파일이 생성됩니다.

### 2단계: C# WPF 데모 앱 실행
Visual Studio 2022에서 `src/CameraDemo/CameraDemo.csproj`를 열고 실행(F5) 하거나, .NET CLI를 통해 터미널에서 직접 실행할 수 있습니다.
```bat
cd src/CameraDemo
dotnet run -c Release
```

## 핵심 제어 API (예상 사양)
- **최소 노출 시간 (Exposure):** 1us (하드웨어 제약사항 검토 중)
- **최대 프레임레이트:** 25 fps
- **트리거 동기화:** 20~30mm/s 선형 스테이지 이동 속도에 맞춘 하드웨어 인터럽트 제어
