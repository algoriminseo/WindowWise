# Cloudflare TURN 자동 연동 설정

Cloudflare TURN Token ID와 API 토큰은 시그널링 서버에만 보관합니다.
앱은 로그인 토큰으로 서버의 `/turn-credentials`를 호출하고 발급받은 임시 TURN 정보를 WebRTC 연결에 사용합니다.
연결과 **연결 환경 확인** 모두 새로 발급합니다.

## 1. 서버 PC에서 시작

기존 5047 서버를 Ctrl+C로 종료한 후 프로젝트 루트에서 실행합니다.

```powershell
cd C:\Users\minse\source\repos\WindowWise
docker compose up -d --build
docker compose ps
docker compose logs --tail 30 cloudflared
```

서버 PC의 저장소 루트에 다음 비밀 설정 파일을 준비합니다.

| 파일 | 설정 |
| --- | --- |
| `.env.server` | `CLOUDFLARE_TURN_KEY_ID`, `CLOUDFLARE_TURN_API_TOKEN` |
| `.env.tunnel` | Cloudflare의 이름 있는 터널에서 발급한 `TUNNEL_TOKEN` |

Compose가 설정을 자동으로 읽고 시그널링 서버와 터널을 함께 시작합니다. 두 파일은 서버 PC에서 관리하며 Git과 Docker 이미지 빌드에서 제외합니다. 토큰 값은 앱에 배포하지 않습니다.
Docker Desktop에서 `windowwise` 프로젝트를 시작할 수도 있습니다. 서버 PC와 Docker 엔진이 실행 중이어야 합니다.

## 2. 고정 공개 주소 확인

Cloudflare의 이름 있는 터널에 다음 Published application 경로를 등록합니다.

| 항목 | 설정 |
| --- | --- |
| 공개 호스트 | `drop.datbl.com` |
| Path | 비워 두기 |
| Service URL | `http://signaling:5047` |

Compose의 `cloudflared`가 같은 Docker 네트워크의 `signaling` 서비스로 요청을 전달합니다.
서버는 설정된 터널 프록시의 HTTPS 전달 정보를 처리합니다. 같은 고정 호스트를 로그인 확인, 시그널링, TURN 자격 증명 발급에 사용합니다.

```powershell
curl.exe -i https://drop.datbl.com/me
```

토큰 없이 요청한 `/me`가 `401 Unauthorized`를 반환하면 공개 주소에서 인증 엔드포인트까지 도달한 것입니다. 실제 로그인과 TURN 전송은 아래 단계에서 확인합니다.

설정 절차는 [Cloudflare Tunnel 가이드](https://developers.cloudflare.com/tunnel/get-started/)를 참고하세요.

## 3. 양쪽 앱에서 설정

앱의 기본 서버 주소는 `https://drop.datbl.com`과 `wss://drop.datbl.com/signal`입니다.
이전 개발용 주소를 현재 PowerShell에서 설정했다면 아래 명령으로 해제한 뒤 앱을 시작합니다. 설치된 앱은 바로 실행하고 각자 로그인하면 됩니다.

```powershell
Remove-Item Env:WINDOWWISE_API_URL -ErrorAction SilentlyContinue
Remove-Item Env:WINDOWWISE_SIGNAL_URL -ErrorAction SilentlyContinue
Remove-Item Env:WINDOWWISE_TURN_CREDENTIALS_URL -ErrorAction SilentlyContinue
dotnet run --project .\src\WindowWise.App
```

원격 파일 공유 화면에서 **고급 연결 설정**을 열어 아래 값을 확인합니다.

| 항목 | 설정 |
| --- | --- |
| 연결 서버 주소 | `wss://drop.datbl.com/signal` |
| 서버에서 접속 정보 자동 받기 | 켜기 — 기본값 |
| 접속 정보 발급 주소 | 비워 두기 — 연결 서버의 `/turn-credentials`로 자동 결정 |

양쪽에서 **연결 환경 확인**을 눌러 `relay` 후보를 확인합니다. 이 작업은 후보만 수집하며 상대 연결을 만들지는 않습니다.
한쪽은 **룸 만들기 → 코드 복사**를 누르고, 다른 쪽은 받은 코드를 입력해 **룸 참여**를 누릅니다.
앱은 STUN을 이용한 직접 연결을 먼저 시도합니다. 직접 연결이 실패하면 같은 룸에서 TURN으로 재시도합니다.
연결 후 화면에서 선택된 경로를 확인하고 작은 파일을 보내 SHA-256 검증까지 확인하세요.
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

- `turn-server-unavailable` + HTTP 530/Cloudflare 1033: 공개 호스트의 터널 연결 상태를 확인합니다.
  `docker compose logs --tail 30 cloudflared`와 Cloudflare의 터널 상태를 확인하고 `windowwise` 컨테이너를 시작하세요.
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
