# WindowWise

**복사한 내용을 다시 찾고, 오디오 환경을 빠르게 바꾸는 Windows 데스크톱 유틸리티.**

WindowWise는 자주 사용하는 클립보드와 오디오 설정을 한곳에서 관리하는 WPF 애플리케이션입니다. 텍스트·링크 기록을 검색하고 분류하며, 출력 장치별 볼륨과 기본 출력 장치를 프리셋으로 저장해 다시 적용할 수 있습니다. 창을 닫은 뒤에도 시스템 트레이에서 실행됩니다.

`Windows` · `C#` · `WPF` · `.NET 10` · `SQLite` · `NAudio`

> 현재는 기능을 확장하고 다듬는 개발 단계입니다. Smart Clipboard와 Window Audio Manager가 구현되어 있으며, Settings는 메뉴 항목만 준비되어 있습니다.

## 주요 기능

### Smart Clipboard

- **텍스트·링크 기록** — 클립보드 변경을 감지하고 SQLite에 저장합니다. 같은 내용을 다시 복사하면 기존 기록의 시간과 출처를 갱신합니다.
- **검색과 필터** — 내용, 유형, 카테고리, 출처 앱으로 검색하고 전체·즐겨찾기·링크·텍스트 필터로 좁혀 볼 수 있습니다.
- **기록 관리** — 항목을 다시 복사하거나 즐겨찾기에 추가하고 삭제할 수 있습니다. 전체 삭제 시 즐겨찾기는 유지됩니다.
- **카테고리** — 이름과 색상을 지정한 카테고리를 만들고, 항목을 직접 카테고리에 넣거나 해제할 수 있습니다.
- **출처 확인** — 복사한 앱과, 확인 가능한 경우 원본 URL을 기록합니다.
- **민감정보 감지** — 비밀번호 필드, API 키·토큰, 인증번호 등의 단서를 검사하고 보호 기록에서 차단·보호 유지·일반 항목 처리를 지원합니다.

### Window Audio Manager

- **출력 장치 확인** — 현재 기본 출력 장치와 다른 출력 장치의 이름·상태를 표시하고 장치 변경을 감지합니다.
- **볼륨 조절** — 기본 출력 장치와 개별 장치의 볼륨을 슬라이더로 조절합니다.
- **프리셋 관리** — 장치별 볼륨과 기본 출력 장치를 저장하고, 기존 프리셋을 덮어쓰거나 적용·삭제할 수 있습니다. 기본 출력 장치 변경은 Windows의 Multimedia 역할을 대상으로 합니다.
- **전역 단축키** — 첫 9개 프리셋을 `Ctrl + Alt + 1`부터 `Ctrl + Alt + 9`까지 등록합니다.
- **변경 알림** — 기본 출력 장치가 바뀌면 화면 하단에 짧은 오버레이 알림을 표시합니다.

### Overview와 시스템 트레이

Overview에서 현재 시간과 최근 클립보드 활동을 확인하고 각 기능으로 이동할 수 있습니다. 창의 닫기 버튼은 앱을 트레이로 숨깁니다. 트레이 아이콘을 더블클릭하거나 **Open**을 선택하면 창이 다시 열리고, **Exit**을 선택하면 종료됩니다.

## 시작하기

### 표시 언어

오른쪽 상단의 **한국어 / English** 선택기를 사용하면 Overview, 클립보드, 오디오 관리, 로컬·원격 파일 공유, 계정 메뉴와 로그인 창의 표시 언어가 함께 바뀝니다. Drop의 언어 선택기도 같은 설정을 사용합니다. 열려 있는 화면과 진행 상태 문구에 즉시 적용되며 연결을 다시 시작하지 않습니다. 파일명, 이메일, 클립보드 내용과 사용자가 입력한 프리셋·카테고리 이름은 번역하지 않습니다.

선택한 언어는 `%LOCALAPPDATA%\WindowWise\language.json`에 저장되며 다음 실행에 복원됩니다. 처음 실행할 때의 기본 언어는 영어입니다. 공통 번역은 `src/WindowWise.App/Localization/App.en.json`과 `App.ko.json`에서 관리합니다. 원격 전송의 상태·오류 문구는 기존 `RemoteSharing.*.json`을 사용합니다.

Auth0 로그인 요청에도 선택한 언어를 `ui_locales`로 전달합니다. 브라우저의 Auth0 로그인 화면까지 적용하려면 [Auth0 Tenant Settings의 지원 언어](https://auth0.com/docs/customize/internationalization-and-localization/universal-login-internationalization)에 English (`en`)와 Korean (`ko`)를 활성화해야 합니다. Google 자체 로그인 화면은 해당 서비스의 언어 설정을 따릅니다.

### 개발 환경

- Windows 환경
- .NET 10 SDK — `global.json`의 기준 버전은 `10.0.100`이며, 이후 정식 .NET 10 SDK의 기능 밴드도 허용합니다.
- Visual Studio를 사용하는 경우 .NET 10과 `.slnx`를 지원하는 버전 및 **.NET 데스크톱 개발** 워크로드

### 빌드 및 실행

저장소 루트에서 실행합니다.

```powershell
dotnet restore .\WindowWise.slnx
dotnet build .\WindowWise.slnx
dotnet run --project .\src\WindowWise.App\WindowWise.App.csproj
```

Visual Studio에서는 `WindowWise.slnx`를 열고 `WindowWise.App`을 시작 프로젝트로 지정합니다. 프로젝트는 이미 `WinExe`로 구성되어 있습니다.

공통 빌드 설정에 따라 출력 파일은 `artifacts/bin/`, 중간 생성 파일은 `artifacts/obj/` 아래에 생성됩니다.

### Docker로 시그널링 서버 실행

서버 PC에 Docker Desktop을 설치하고 Linux 컨테이너 엔진을 시작합니다. Docker는 `WindowWise.Signaling`을 실행하며, Windows 앱은 각 PC에서 실행합니다.

저장소 루트에 `.env.server`를 만들고 서버용 Cloudflare 자격 증명을 한 번 저장합니다.

```dotenv
CLOUDFLARE_TURN_KEY_ID=YOUR_TURN_KEY_ID
CLOUDFLARE_TURN_API_TOKEN=YOUR_CLOUDFLARE_API_TOKEN
```

`.env.server`는 평문 비밀 설정입니다. `.gitignore`와 `.dockerignore`로 Git과 이미지 빌드에서 제외하고, 서버 PC에서만 관리합니다. Compose의 `env_file`이 컨테이너 실행 시 값을 전달하므로 토큰을 매번 입력할 필요가 없습니다.

같은 위치의 `.env.tunnel`에는 이름 있는 터널의 토큰을 저장합니다. 이 파일도 Git과 Docker 이미지 빌드에서 제외합니다.

```dotenv
TUNNEL_TOKEN=YOUR_TUNNEL_TOKEN
```

Docker 관련 파일은 다음과 같습니다.

| 파일 | 역할 |
| --- | --- |
| `src/WindowWise.Signaling/Dockerfile` | .NET 10 SDK로 서버를 빌드한 뒤 ASP.NET Core Runtime 이미지에서 DLL 실행 |
| `compose.yaml` | 서버 빌드, `.env.server`·`.env.tunnel` 로딩, 서버와 터널 실행 |
| `.dockerignore` | 자격 증명과 로컬 빌드 결과를 이미지 빌드 대상에서 제외 |

기존 5047 서버를 종료한 뒤 저장소 루트에서 실행합니다.

```powershell
docker version
docker compose up -d --build
docker compose ps
docker compose logs --tail 30 signaling
docker compose logs --tail 30 cloudflared
curl.exe -i http://localhost:5047/me
curl.exe -i https://drop.datbl.com/me
```

`docker version`에 Client와 Server가 모두 표시되어야 합니다. `/me`의 `401 Unauthorized` 응답은 서버가 요청을 받고 인증을 요구한다는 뜻이며, 실제 로그인과 TURN 중계는 별도로 확인합니다.

컨테이너는 `http://0.0.0.0:5047`에서 요청을 받습니다. `127.0.0.1:5047:5047` 포트 매핑으로 서버 PC에서는 `http://localhost:5047`을 사용합니다. 컨테이너 생성 후에는 Docker Desktop에서 `windowwise` 프로젝트를 시작하거나 중지할 수 있습니다. 코드나 `.env.server`를 수정했다면 `docker compose up -d --build`를 다시 실행합니다.

Windows 로그인 후 자동 시작을 원하면 Docker Desktop의 **Start Docker Desktop when you sign in**을 켭니다. Compose의 서버와 터널 서비스에는 `restart: unless-stopped`가 설정되어 있습니다.

서비스를 직접 Stop하면 다음 Docker 시작 시에도 중지 상태가 유지되므로 수동으로 Start합니다.

자주 발생하는 오류는 다음과 같습니다.

| 증상 | 확인할 부분 |
| --- | --- |
| `dockerDesktopLinuxEngine` 파이프를 찾을 수 없음 | Docker Desktop을 시작하고 Linux 엔진 실행을 기다림 |
| `COPY requires at least two arguments` | `COPY --from=build /app/publish .`에서 목적지 앞 공백 확인 |
| `docker.io/library/build:latest` 다운로드 실패 | 첫 줄의 `AS build`와 `COPY --from=build` 이름 일치 확인 |
| 5047 포트 사용 중 | 기존 시그널링 서버 또는 다른 컨테이너 종료 |

공개 주소는 `drop.datbl.com`이며, 이름 있는 Cloudflare Tunnel의 내부 목적지는 `http://signaling:5047`입니다. `127.0.0.1`은 각 컨테이너 자신을 가리킵니다. 서버는 `ReverseProxy__Address`에 지정된 터널 컨테이너의 HTTPS 전달 정보를 신뢰합니다.

앱의 기본 로그인 확인 주소는 `https://drop.datbl.com/me`, 룸 연결 주소는 `wss://drop.datbl.com/signal`입니다. 서버와 터널이 실행 중이면 양쪽 앱에서 로그인하고 룸 코드를 공유합니다. 개발용 환경 변수로 주소를 바꿨다면 해당 값을 해제하거나 원하는 서버 주소로 설정합니다. 상세 접속 흐름은 [Cloudflare TURN 설정](docs/cloudflare-turn-setup.md)을 참고하세요.

## 사용 흐름

### 클립보드 기록 활용

1. WindowWise를 실행한 상태에서 텍스트나 링크를 복사합니다.
2. **Smart Clipboard**에서 기록을 검색하거나 필터를 선택합니다.
3. 필요한 항목을 클릭해 다시 복사합니다. 자주 사용하는 내용은 즐겨찾기에 추가합니다.
4. 카테고리 규칙을 만들어 관련 내용을 모으고, 보호 기록에서 민감정보로 감지된 항목을 검토합니다.

### 오디오 프리셋 활용

1. **Window Audio Manager**에서 장치별 볼륨을 조절합니다. 저장할 기본 출력 장치는 Windows에서 먼저 선택합니다.
2. **Volume Presets**에 이름을 입력하고 **Save**를 눌러 현재 구성을 저장합니다.
3. 저장한 프리셋의 **Apply**로 볼륨과 기본 출력 장치를 복원합니다. 기존 프리셋의 **Save**는 현재 구성으로 덮어씁니다.
4. 등록된 프리셋은 `Ctrl + Alt + 숫자`로도 적용할 수 있습니다. 다른 앱이 같은 단축키를 사용하면 등록되지 않을 수 있습니다.

현재 단축키 등록은 프리셋 변경 시 기존 등록을 다시 연결하지 않으므로, 프리셋 삭제 등으로 목록이 바뀐 뒤에는 앱을 재시작해 매핑을 갱신하는 것이 좋습니다.

## 데이터 저장과 보호 동작

클립보드 기록, 카테고리 규칙, 오디오 프리셋은 다음 사용자별 로컬 DB에 저장됩니다.

```text
%LOCALAPPDATA%\WindowWise\windowwise.db
```

| 감지 결과 | 현재 동작 |
| --- | --- |
| 높은 확신 (`High`) | 시스템 클립보드를 비우고, 원문 대신 차단 표시와 감지 사유를 기록 |
| 검토 필요 (`Possible`) | 원문을 저장하고 검토 대상으로 표시; 사용자가 차단·보호 유지·일반 처리 선택 |
| 감지 없음 (`None`) | 일반 기록으로 저장 |

감지는 패턴과 출처 정보를 사용하는 휴리스틱이므로 오탐과 미탐이 있을 수 있습니다. 보호 표시는 UI 상태이며 DB 암호화를 의미하지 않습니다. 현재 DB에는 일반 기록과 검토 대상 원문이 평문으로 저장됩니다.

## 기술 구성

| 영역 | 구성 |
| --- | --- |
| 데스크톱 UI | C# / WPF / XAML, 공통 테마와 재사용 컨트롤 |
| 화면 상태 | 오디오 ViewModel 및 클립보드 서비스의 컬렉션 바인딩, 화면 이벤트 처리 |
| 로컬 저장 | Microsoft.Data.Sqlite |
| 오디오 | NAudio / Windows Core Audio, 기본 출력 장치 제어 |
| Windows 연동 | 클립보드 변경 메시지, 출처 정보 수집, 전역 단축키 |
| 시스템 트레이 | Windows Forms `NotifyIcon` |

## 프로젝트 구조

```text
WindowWise/
├── src/WindowWise.App/
│   ├── App.xaml / App.xaml.cs  앱 시작·종료, 서비스 연결, 단축키 등록
│   ├── Views/                 메인 창, Overview, 클립보드, 오디오, 알림 화면
│   ├── ViewModels/            Audio Manager 화면 상태
│   ├── Models/                클립보드·카테고리·오디오·시그널링 모델
│   ├── Services/              클립보드·SQLite·오디오·트레이·단축키 서비스
│   ├── Controls/              재사용 기능 카드
│   ├── Themes/                공통 색상과 스타일
│   └── Assets/                애플리케이션 아이콘
├── docs/                      초기 설계 및 개발 안내 문서
├── Directory.Build.props      공통 빌드 설정과 출력 경로
├── global.json                .NET SDK 기준
└── WindowWise.slnx            솔루션
```

앱에서 사용하는 메인 창은 `Views/MainWindow.xaml`입니다. 프로젝트 루트의 이전 `MainWindow.xaml`과 코드 비하인드는 빌드에서 제외되어 있습니다.

## 개발 현황

| 영역 | 상태 |
| --- | --- |
| Smart Clipboard | 기록·검색·즐겨찾기·분류·민감정보 처리 구현 |
| Window Audio Manager | 장치 표시·볼륨·프리셋·단축키·변경 알림 구현 |
| Overview / 시스템 트레이 | 기능 이동·최근 활동·창 숨기기·종료 구현 |
| Settings | 메뉴 항목만 준비, 기능 연결 예정 |
| 원격 공유 | 직접 P2P 연결, 수신 승인, 파일 분할 전송·진행률·취소·SHA-256 검증 및 원격 화면의 한국어·영어 전환 구현 |
| 배포 / 자동화 테스트 | WebRTC 통합 검증 실행기 구성; 설치 패키지 미구성 |

다음 정리 대상은 프리셋 변경 시 단축키 재등록, 클립보드 보관 개수 정책 통일, 오디오 환경별 동작 검증, 설치·배포 구성입니다. 현재 클립보드 서비스의 메모리 보관 기준은 일반 항목 1,000개, 저장소의 정리 기준은 일반 항목 300개이며 시작 시 조회도 최대 300개여서 통일이 필요합니다.

### 설계 문서

- [구조 원칙](docs/architecture.md)
- [초기 개발 로드맵](docs/roadmap.md)
- [초기 Visual Studio 설정 안내](docs/visual-studio-setup.md)
- [WebRTC DataChannel 사용 및 통합 검증](docs/webrtc-datachannel.md)
