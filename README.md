# Q Camera SDK (Sony 13MP UVC)

Windows 기반 UVC 카메라 제어/촬영 프로젝트입니다.  
현재 운영 정책은 **NV12 고정 + 24fps 목표**입니다.

## 1. 현재 정책
- 포맷: **NV12 -> NV12 (Fixed)**
- 목표 성능: **24fps**
- 기본 해상도: **3840x2160**
- 녹화 시작 가드: `NV12` + `>= 23.5fps` 조건에서만 녹화 허용
- 노출(Exposure): UI 제한 `-11 ~ -5` (UVC log2)

## 2. 프로젝트 구조
- `src/CameraCore` (C++): Media Foundation/UVC 네이티브 코어 (`CameraCore.dll`)
- `src/CameraSDK` (C#): P/Invoke 래퍼 (`CameraSDK.dll`)
- `src/CameraDemo` (C# WPF): 미리보기/녹화/저장 UI
- `src/CameraCli` (C#): FPS/파라미터 검증용 CLI

## 3. CameraDemo 확정 UI/동작
- 상단 타이틀: `Q-Camera : Sony 13MP - IMX258`
- 저장 중: 모든 메뉴 잠금 + 프리뷰 중앙 `SAVING...`/진행률 표시
- 녹화 중: 프리뷰 중앙 `REC` 표시
- Preview Off: 프리뷰 중앙 `PREVIEW STOPPED` 표시
- 중앙 경고 오버레이: Recording/Save 차단 사유를 중앙 화면에 표시
- Camera Controls 표시 항목: Connection / Resolution / Pipeline / Applied
- Exposure/Gain/Focus 및 Control Panel 값은 슬라이더/텍스트 입력 시 즉시 반영
- AEC/AF는 Preview 기준 동작, Recording 시에는 수동값으로 잠금
- Auto Exposure ON 상태에서는 Recording 시작 차단
- Preview가 사용자 `Stop Preview` 상태가 아니면 Resolution 변경 차단

## 4. 저장 동작
- `Convert & Save All to Folder` 실행 시 저장 위치 선택 창 표시
- 저장 가능 조건: Recording 완료 후 미저장 캡처 데이터가 존재할 때
- 저장할 이미지가 없으면 중앙 경고 표시 + 로그 기록 후 저장 차단
- 저장 진행/완료/소요시간은 로그에 기록

## 5. 로그 파일
- 런타임 로그: `src/CameraDemo/bin/Debug/net8.0-windows/camera_demo.log`
- 하드웨어 capability 로그: `src/CameraDemo/bin/Debug/net8.0-windows/hardware_capabilities.log`

## 6. 빌드/실행
```bat
scripts\windows\build_native.cmd
scripts\windows\build_managed.cmd
dotnet run --project src/CameraDemo/CameraDemo.csproj -c Debug
```

## 7. 참고 문서
- 개발 변경 정리: `docs/개발_변경_정리_2026-04-01.md`
- 성과물 목록: `docs/성과물_목록.md`
- 기능 검증 브랜치: `codex/feature-validation` (생성 예정)

## 8. 필수 문서
- SDK User manual: `docs/SDK_User_Manual.md`
- ATP 문서: `docs/ATP_테스트_문서.md`
- ATP 결과 보고서: `docs/ATP_결과_보고서_2026-03-31.md`
