# ATP 테스트 문서

## 1. 목적
- 13M 카메라 SDK의 기능/성능/안정성 검증
- 목표 정책(NV12, 24fps) 만족 여부 확인

## 2. 시험 범위
- 초기화/종료
- Preview Start/Stop
- Record 및 Batch Save
- Exposure/Gain/Focus 제어
- Procamp(Brightness/Contrast/Saturation/Sharpness/Backlight)
- 해상도 전환
- 성능(FPS) 측정

## 3. 시험 환경
- OS: Windows 10/11 x64
- .NET: 8.0+
- 카메라: Sony 13M UVC 모듈
- 인터페이스: USB 3.0

## 4. 합격 기준
- 앱 비정상 종료 없음
- Preview/Record/Save 기본 동작 정상
- 제어 파라미터 적용 정상
- NV12 기준 24fps 목표(>=23.5fps guard) 동작

## 5. 시험 항목
1. 앱 실행/종료
2. Preview 반복 Start/Stop
3. 2초 녹화 후 프레임 저장
4. Exposure/Gain 값 변경 반영
5. Focus 모드 전환 및 수동값 반영
6. Procamp 각 항목 반영
7. 해상도 전환 후 재초기화
8. 로그 파일 생성 확인
9. 24fps guard 동작 확인

## 6. 산출물
- 시험 로그
- 캡처 이미지
- FPS 측정 결과
- ATP 결과 보고서
