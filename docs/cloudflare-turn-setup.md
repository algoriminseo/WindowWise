# Cloudflare TURN 자동 연동 설정

Cloudflare TURN Token ID와 API 토큰은 시그널링 서버에만 보관합니다.
앱은 로그인 토큰으로 서버의 `/turn-credentials`를 호출하고 발급받은 임시 TURN 정보를 WebRTC 연결에 사용합니다.
연결과 **연결 환경 확인** 모두 새로 발급합니다.

## 1. 미국 서버 PC에서 시작

기존 5047 서버를 Ctrl+C로 종료한 후 프로젝트 루트에서 실행합니다.

```powershell
cd C:\Users\minse\source\repos\WindowWise
.\scripts\Start-TurnSignaling.ps1
```

스크립트가 요청하는 값 두 개만 입력합니다.

1. Cloudflare TURN Token ID
2. Cloudflare API Token — 숨김 입력. 앞서 노출한 토큰은 폐기하고 재발급한 값을 사용합니다.

스크립트는 Cloudflare 자격 증명을 환경변수로 서버 프로세스에 전달하고, 서버 종료 시 환경변수를 이전 값으로 복구합니다.

스크립트 실행 정책에 의해 차단된다면 본인 정책에서 허용된 PowerShell 실행 방식을 사용하거나,
아래 환경변수 방식으로 직접 서버를 실행할 수 있습니다. 머신 전체 정책 변경은 필요하지 않습니다.

## 2. 미국–한국 테스트용 HTTPS 주소 만들기

이미 HTTPS/WSS로 배포된 서버가 있으면 그 주소를 사용합니다. 로컬 PC에서 테스트하는 경우,
[공식 다운로드 안내](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/downloads/)에 따라
`cloudflared`를 설치한 뒤 **새 터미널**에서 실행합니다.

```powershell
cloudflared tunnel --url http://127.0.0.1:5047
```

터미널에 표시된 `https://...trycloudflare.com` 주소를 사용합니다. 예시 주소를 그대로 입력하지 마세요.
양쪽 앱의 **연결 서버 주소**는 이 주소의 `https://`를 `wss://`로 바꾸고 `/signal`을 붙입니다.

```text
wss://실제발급된호스트.trycloudflare.com/signal
```

같은 터널을 시그널링과 자격 증명 발급에 사용합니다. 이 방식은 집 공유기 포트포워딩 없이 테스트할 수 있습니다.
터널은 개발 테스트용이며 재시작할 때 호스트가 바뀌고, 터미널을 닫으면 접속이 끝납니다.
메일 인증 같은 브라우저 로그인 옵션은 이 앱의 비대화형 요청과 맞지 않으므로 사용하지 않습니다.
운영 배포에는 고정 HTTPS 주소와 사용자별 인증이 필요합니다.

참고: [Cloudflare Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/).

## 3. 양쪽 앱에서 설정

양쪽 PC의 PowerShell에서 실제 터널 호스트명을 넣고 앱을 시작한 뒤 각각 로그인합니다.

```powershell
$serverHost = '실제발급된호스트.trycloudflare.com'
$env:WINDOWWISE_API_URL = "https://$serverHost"
$env:WINDOWWISE_SIGNAL_URL = "wss://$serverHost/signal"
dotnet run --project .\src\WindowWise.App
```

원격 파일 공유 화면에서 **서버를 통한 연결 (TURN)**을 선택하고 **고급 연결 설정**을 열어 아래 값을 확인합니다.

| 항목 | 설정 |
| --- | --- |
| 연결 서버 주소 | 같은 `wss://실제호스트/signal` |
| 연결 방식 | 서버를 통한 연결 (TURN 로고) |
| 서버에서 접속 정보 자동 받기 | 켜기 — 기본값 |
| 접속 정보 발급 주소 | 비워 두기 — 연결 서버의 `/turn-credentials`로 자동 결정 |
| 항상 중계 서버 사용 | 켜기 — 실제 TURN 경로 검증 |

양쪽에서 **연결 환경 확인**을 눌러 `relay` 후보를 확인합니다. 이 작업은 후보만 수집하며 상대 연결을 만들지는 않습니다.
한쪽은 **룸 만들기 → 코드 복사**를 누르고, 다른 쪽은 받은 코드를 입력해 **룸 참여**를 누릅니다.
연결 후 **TURN 중계** 경로를 확인하고 작은 파일을 보내 SHA-256 검증까지 확인하세요.
후보가 나온 것과 실제 미국–한국 전송 성공은 별도로 확인해야 합니다.

앱에는 Cloudflare API 토큰이나 TURN Token ID를 넣지 않습니다. 더 이상 curl로 임시 비밀번호를 복사할 필요도 없습니다.
자동 받기를 끄면 기존 수동 주소·아이디·비밀번호 입력도 사용할 수 있습니다.

## 환경변수로 직접 운영할 때

Cloudflare를 사용할 때는 서버에 다음 값을 설정합니다. 저장소나 `appsettings.json`에 비밀 값을 커밋하지 마세요.

| 서버 환경변수 | 값 |
| --- | --- |
| `CLOUDFLARE_TURN_KEY_ID` | Cloudflare TURN Token ID |
| `CLOUDFLARE_TURN_API_TOKEN` | Cloudflare API 토큰 |
| `CLOUDFLARE_TURN_TTL_SECONDS` | 선택. 기본 3600초, 허용 60~86400초 |

Cloudflare 대신 직접 운영하는 coturn에서 발급하려면 다음 값을 설정합니다.
앱의 설정과 동작은 같으며, 서버가 Cloudflare를 호출하지 않고 발급합니다.

| 서버 환경변수 | 값 |
| --- | --- |
| `TURN_PROVIDER` | `coturn` |
| `TURN_SHARED_SECRET` | coturn의 `static-auth-secret`과 같은 값 |
| `TURN_URLS` | 쉼표로 구분한 TURN 주소 |
| `TURN_TTL_SECONDS` | 선택. 기본 3600초, 허용 60~86400초 |

`WINDOWWISE_TURN_CREDENTIALS_URL` 환경변수를 설정하면 앱의 발급 주소 입력란에 반영됩니다.
외부 발급 주소는 HTTPS만 허용하고, 같은 PC의 localhost 발급 주소만 HTTP를 허용합니다.
백엔드는 로그인 토큰 검증, 분당 30회 발급 API 요청 제한, 응답 `no-store`, upstream 오류 본문 차단을 적용합니다.
HTTPS를 프록시에서 종료할 경우 origin을 루프백으로 연결하거나 신뢰하는 프록시에 대한 HTTPS 전달 설정이 필요합니다.

각 연결·후보 수집 시 새로 발급하며 캐시하지 않습니다. 진행 중인 채널의 자격 증명 자동 갱신은 지원하지 않습니다.
기본 1시간을 넘는 세션은 만료 전에 재연결하거나 서버 TTL을 예상 세션 길이에 맞게 설정하세요.

## 오류 구분

- `turn-server-unavailable` + HTTP 530/Cloudflare 1033: 임시 터널에 도달할 수 없는 상태입니다.
  `cloudflared` 창을 확인하고 재시작했다면 양쪽 앱을 새 터널 주소로 바꾸세요.
- `turn-cloudflare-auth`: 발급 서버에서 Cloudflare가 반환한 401/403입니다. 서버의 API 토큰·권한·TURN Token ID 조합을 확인합니다.
- `turn-cloudflare-key-not-found`: Cloudflare가 반환한 404입니다. TURN Token ID를 확인합니다.
- `turn-endpoint-not-found`: 앱이 호출한 발급 경로가 404/405를 반환했습니다. 최신 서버 실행 여부와 `/turn-credentials` 주소를 확인합니다.

앱 진단은 발급 서버 HTTP 상태와 반환된 Cloudflare HTTP 상태를 구분합니다.
서버는 Cloudflare 실패 상태 번호만 로그에 기록하며 API 토큰·비밀번호·upstream 응답 본문은 출력하지 않습니다.

- `turn-unauthorized`: 앱 로그인 상태와 서버의 인증 설정을 확인합니다.
- 서버 미설정: 서버의 Cloudflare 환경변수를 확인합니다.
- Cloudflare 발급 실패: Token ID·API 토큰·권한 및 서버의 Cloudflare HTTPS 접근을 확인합니다.
- 외부 HTTPS 필요: 공인 IP의 `http://` 대신 HTTPS 터널 또는 배포 주소를 사용합니다.
- 발급 후에도 상대 연결 실패: 양쪽 시그널링 주소와 룸 참여 상태를 확인합니다.

실제 Cloudflare 발급과 중계는 본인 계정 설정 후 검증해야 합니다. 저장소 자동 테스트는 실제 비밀 값 없이
모의 Cloudflare를 통해 발급 API·권한·변환·오류·취소를 검증하고 기존 로컬 채널과 파일 전송을 확인합니다.
