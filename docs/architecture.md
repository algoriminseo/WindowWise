# 구조 원칙

PDF에서 제안한 단일 WPF 프로젝트와 MVVM 역할 분리를 그대로 사용합니다. 초기 포트폴리오 프로젝트에는 여러 클래스 라이브러리로 나눈 구조보다 이 구성이 이해하고 완성하기 쉽습니다.

## 역할

- `Views`: Clipboard, Audio Status, Quick Settings, Settings 화면
- `ViewModels`: 화면 상태, 검색어, 선택 항목, 명령
- `Models`: ClipboardItem, AudioDeviceInfo, UserSetting
- `Services`: 클립보드 감지, 오디오 장치 감지, 알림, 설정 실행, DB 접근
- `Data`: SQLite 파일과 향후 마이그레이션
- `Resources`: 공통 스타일, 색상, 아이콘

## 예정 파일

구현을 시작할 때 필요한 파일만 하나씩 추가합니다.

```text
WindowWise.App
|-- App.xaml
|-- MainWindow.xaml
|-- Views/
|   |-- ClipboardView.xaml
|   |-- AudioStatusView.xaml
|   |-- QuickSettingsView.xaml
|   `-- SettingsView.xaml
|-- ViewModels/
|-- Models/
|-- Services/
|-- Data/
`-- Resources/
```

DB 파일은 저장소에 커밋하지 않습니다. 실행 중 생성되는 데이터는 사용자별 앱 데이터 폴더에 두고, `src` 아래에는 넣지 않는 것을 권장합니다.
