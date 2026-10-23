# WebRTC DataChannel

`WebRtcDataChannelService`는 지정한 상대 기기 및 방에 대해 하나의 reliable/ordered
DataChannel을 관리합니다. 시그널링 연결은 호출자가 소유하며 서비스보다 오래 유지합니다.
서비스를 생성하면 시그널링 수신 이벤트에 자동으로 등록하므로 별도로 메시지를 전달할 필요가 없습니다.

## 연결 순서

1. 시그널링 서버를 실행합니다.
2. 양쪽 앱을 서로 다른 기기 ID로 서버에 연결합니다.
3. 양쪽에서 동일한 방 ID와 상대 기기 ID를 사용해 서비스를 생성합니다.
4. 서비스를 양쪽 모두 생성한 뒤 한쪽만 `StartAsync()`를 호출합니다.
5. `WaitUntilOpenAsync()` 완료 후 텍스트 또는 바이너리를 보냅니다.
6. 서비스를 `DisposeAsync()`로 정리한 뒤 시그널링 클라이언트를 정리합니다.

저장소 루트에서 개발 서버를 실행합니다. 이 포트는 기존 http 실행 프로필과 같습니다.

```powershell
dotnet run --project src/WindowWise.Signaling --no-launch-profile -- --urls http://localhost:5047
```

송신 측 코드 예시입니다. 실제 앱에서는 서비스와 클라이언트를 화면 또는 세션 수명 동안 보관하세요.

```csharp
await using var signaling = new RemoteSignalingClient();
await signaling.ConnectAsync(new Uri("ws://localhost:5047/signal"), "device-a");
await using var rtc = new WebRtcDataChannelService(signaling, "device-b", "share-room-1");

rtc.TextReceived += (_, text) => Debug.WriteLine(text);
rtc.BinaryReceived += (_, bytes) => Debug.WriteLine($"Received {bytes.Length} bytes");
rtc.ErrorOccurred += (_, error) => Debug.WriteLine(error);

// 상대 기기의 서비스까지 준비되었을 때 한쪽에서만 실행합니다.
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await rtc.StartAsync(timeout.Token);
await rtc.WaitUntilOpenAsync(timeout.Token);
rtc.SendText("안녕하세요!");
rtc.SendBinary(new byte[] { 1, 2, 3 });

// 세션을 사용하는 작업을 이 범위 안에서 계속합니다.
```

수신 측은 `deviceId`를 `device-b`, 상대 ID를 `device-a`로 설정하고 동일한 방 ID를 사용합니다.
`StartAsync()`를 호출하지 않고 `WaitUntilOpenAsync()`로 기다리면 됩니다.
WPF UI 갱신은 이벤트 핸들러에서 `Dispatcher.InvokeAsync(...)`로 전환하세요.

## 처리 범위

- Offer/Answer 및 ICE를 자동 교환합니다. 로컬 ICE는 SDP 전송 후 보내고,
  먼저 도착한 원격 ICE는 원격 SDP 적용까지 보관합니다.
- 수신 채널이 이미 열린 상태로 전달되는 경우도 처리합니다.
- UTF-8 텍스트와 바이너리, 빈 메시지를 지원합니다.
- 한 메시지는 UTF-8 인코딩 기준 최대 16 KiB이며, 상대 SCTP 제한이 더 작으면 그 값을 사용합니다.
- 송신 버퍼가 1 MiB를 넘으면 전송을 거절합니다. 호출자는 나중에 재시도해야 합니다.
- 지정한 상대 및 방의 메시지만 처리합니다. 중복 Offer 및 서버 오류는 오류 이벤트로 보고합니다.
- 연결 실패 또는 시그널링 종료 시 세션은 종료되며 새 서비스로 다시 연결합니다.
  이벤트를 해제하고 네트워크 자원을 정리하려면 항상 `await using` 또는 `DisposeAsync()`를 사용하세요.
- `WaitUntilOpenAsync()`의 타임아웃은 대기만 취소합니다. 연결 시도도 중단하려면 서비스를 정리하세요.

기본 설정에는 외부 STUN/TURN 서버가 없습니다. 다른 네트워크의 기기에 연결하려면
생성자의 `RTCConfiguration`에 운영할 STUN/TURN 서버를 전달해야 합니다.
현재 시그널링 서버의 기기 ID와 방 ID에는 인증 기능이 없으므로 개발·통제된 환경에서 사용합니다.
파일 청크 프로토콜, 수신 확인, 진행률 및 공유 UI는 아직 연결하지 않았습니다.

## 통합 검증

```powershell
dotnet run --project tests/WindowWise.TransportTests
```

테스트 실행기는 임의의 로컬 포트에서 실제 시그널링 서버를 시작하고 두 WebRTC 피어를
루프백 주소로 연결합니다. 외부 STUN/TURN 서비스나 별도 테스트 프레임워크는 필요하지 않습니다.
종료 시 테스트 서버를 정리하며 실패하면 0이 아닌 종료 코드를 반환합니다.

검증 항목: 양방향 한글 텍스트, 빈 메시지, 16 KiB 바이너리 무결성, 순차 메시지,
메시지 크기 제한, 타임아웃 취소, 다른 상대·방 무시, 중복 시작 거부,
서버의 대상 없음 오류, 시그널링 종료 및 중복 정리.

이 검증은 한 PC의 실제 ICE/DTLS/SCTP 연결을 다룹니다. 서로 다른 기기 및 NAT 환경의
연결 성공 여부는 운영 STUN/TURN 구성으로 별도 확인해야 합니다.
