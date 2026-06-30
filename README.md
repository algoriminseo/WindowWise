# WindowWise

Windows에서 반복되는 작은 불편을 줄이는 시스템 트레이 유틸리티입니다.

이 저장소는 `WindowWise.pdf`의 제안 내용을 바탕으로 만든 **소스 코드 없는 프로젝트 골격**입니다. 현재 단계에는 C# 및 XAML 구현 파일이 없습니다.

## 기술 방향

- C# / WPF
- .NET 10 (`net10.0-windows`)
- MVVM
- SQLite 로컬 저장소(구현 단계에서 패키지 선택)
- Windows Clipboard API / Core Audio API
- MSIX 또는 exe 설치 파일

## 폴더

```text
WindowWise/
|-- assets/                 아이콘, 스크린샷, 데모 자료
|-- docs/                   구조, 개발 단계, Visual Studio 안내
|-- src/
|   `-- WindowWise.App/
|       |-- Views/          WPF 화면
|       |-- ViewModels/     화면 상태와 명령
|       |-- Models/         데이터 모델
|       |-- Services/       Windows API 및 저장소 기능
|       |-- Data/           SQLite DB 및 마이그레이션
|       `-- Resources/      스타일과 아이콘
|-- tests/                  향후 테스트 프로젝트
|-- Directory.Build.props  공통 빌드 규칙
|-- global.json            .NET SDK 기준
`-- WindowWise.slnx        Visual Studio 솔루션
```

## 현재 상태에서 빌드 확인

```powershell
dotnet restore .\WindowWise.slnx
dotnet build .\WindowWise.slnx
```

현재 `WindowWise.App`은 소스 코드 없이도 구성 자체를 검사할 수 있도록 일시적으로 `Library` 출력 형식을 사용합니다. 첫 WPF 화면을 만들 때 [docs/visual-studio-setup.md](docs/visual-studio-setup.md)의 절차에 따라 `WinExe`로 바꾸면 됩니다.

## 구현 순서

1. WPF 시작 파일 및 System Tray
2. Clipboard Manager MVP
3. 검색 / 고정 / 삭제
4. 민감정보 필터
5. Quick Settings
6. Audio Device Status
7. UI 정리 및 배포

세부 범위는 [docs/roadmap.md](docs/roadmap.md)를 참고하세요.
