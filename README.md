# AI Usage

Claude Code 구독 플랜(Pro/Max)의 **현재 세션**과 **주간 한도** 사용률을 바탕화면에 원형 게이지로 띄워 두는 Windows 가젯입니다.

```
AI Usage                  오전 2:56
   ╭──────╮         ╭──────╮
   │ 33%  │         │  5%  │
   │ 세션 │         │ 주간 │
   ╰──────╯         ╰──────╯
 3시간 23분 후    (수) 오후 12:00
```

> ⚠️ AI Usage는 Anthropic과 관련 없는 **비공식** 도구입니다. 아래 [면책](#면책)을 읽어 주세요.

## 특징

- **토큰을 쓰지 않습니다.** Claude Code CLI 내장 명령 `/usage`의 결과만 읽습니다.
- **계정 전체 사용률**을 보여줍니다. CLI, VS Code 확장, 데스크톱 앱, claude.ai 어디서 쓴 양이든 반영됩니다.
- **로그인 정보를 읽지 않습니다.** 인증은 Claude Code CLI가 스스로 처리합니다.
- **Claude Code 설정을 바꾸지 않습니다.** exe 실행만으로 동작합니다.
- 사용률이 70% 이상이면 주황, 90% 이상이면 빨강으로 표시됩니다.
- 항상 위에 표시, 트레이 아이콘, Windows 시작 시 자동 실행을 지원합니다.

## 요구 사항

- Windows 10 또는 11 (x64)
- [Claude Code CLI](https://code.claude.com/docs/en/setup) 설치 및 로그인
- Claude **Pro 또는 Max** 구독 (API 키 사용자는 플랜 사용량이 표시되지 않습니다)

## 설치

1. 릴리스에서 `AIUsage.exe`를 내려받아 원하는 폴더에 둡니다.
2. 실행합니다. 가젯이 화면 가운데에 나타납니다.
   - 서명되지 않은 파일이라 SmartScreen 경고가 뜰 수 있습니다. **추가 정보 → 실행**을 누르세요.
   - 처음 실행할 때 몇 초 걸릴 수 있습니다.
3. 가젯을 원하는 위치로 끌어다 놓습니다. 위치는 자동으로 저장됩니다.
4. 자동 실행을 원하면 가젯을 우클릭하고 **Windows 시작 시 실행**을 켭니다. exe를 다른 폴더로 옮겼다면 이 설정을 다시 켜야 합니다.

## 사용법

| 동작 | 결과 |
|---|---|
| 드래그 | 이동 |
| 더블클릭 | 지금 새로고침 |
| 우클릭 | 메뉴 |
| 마우스 올리기 | 조회 시각, 오류 내용 |
| 트레이 아이콘 클릭 | 숨기기 / 보이기 |

**메뉴:** 지금 새로고침 · 조회 간격(1/5/10분) · 항상 위에 표시 · Windows 시작 시 실행 · 화면 가운데로 이동 · 숨기기 · 정보 · 종료

**숨긴 가젯 다시 띄우기:** 작업표시줄 알림 영역의 AI Usage 아이콘을 클릭하거나, `AIUsage.exe`를 한 번 더 실행하세요.

## 동작 방식

설정한 간격(기본 5분)마다 다음 명령을 창 없이 실행하고, 출력에서 사용률과 재설정 시각을 읽습니다.

```
claude -p "/usage" --no-session-persistence
```

- `/usage`는 모델 요청을 보내지 않는 내장 명령이라 **토큰을 쓰지 않습니다.**
- `--no-session-persistence` 옵션을 붙여서 조회할 때마다 **세션 기록이 쌓이지 않습니다.**
- AI Usage 자체에는 통신 코드가 없습니다. 사용량 조회를 위한 통신은 Claude Code CLI가 합니다.

### 저장하는 데이터

| 항목 | 위치 |
|---|---|
| 설정 (창 위치, 조회 간격 등) | `%APPDATA%\AIUsage\settings.json` |
| 마지막 조회 값 | `%APPDATA%\AIUsage\last-usage.json` |
| 자동 실행 (켰을 때만) | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 의 `AIUsage` |

## 문제 해결

| 오른쪽 위 표시 | 원인과 해결 |
|---|---|
| **CLI 없음** (게이지 아래 "멈춤") | Claude Code CLI를 찾지 못했습니다. CLI를 설치한 뒤 AI Usage를 다시 실행하세요. |
| **사용량 없음** | 로그인이 안 되었거나, 구독 플랜이 아니거나, CLI 출력 형식이 바뀌었습니다. 터미널에서 `claude -p "/usage"`를 직접 실행해 보세요. |
| **시간 초과** | CLI가 60초 안에 응답하지 않았습니다. 네트워크 상태를 확인하세요. |

## 제거

1. 우클릭 메뉴에서 **Windows 시작 시 실행**을 끄고 **종료**를 누릅니다.
2. `AIUsage.exe`와 `%APPDATA%\AIUsage` 폴더를 지웁니다.

## 소스에서 빌드

.NET 9 SDK가 필요합니다.

- **Visual Studio:** **Visual Studio 2022 17.12 이상**에서 `AIUsage.sln`을 열고 F5로 실행합니다. .NET 9와 최신 C# 문법을 쓰기 때문에 Visual Studio 2019로는 빌드할 수 없습니다.
- 가젯이 실행 중이면 exe 파일이 잠겨 빌드에 실패할 수 있습니다. 먼저 가젯을 종료하세요.

```powershell
# 개발용 빌드
dotnet build AIUsage.sln -c Release

# 배포용 단일 exe
dotnet publish AIUsage -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

## 라이선스

[MIT License](LICENSE)

## 면책

- 이 프로그램은 MIT 라이선스에 따라 **"있는 그대로" 제공되며, 어떠한 보증도 하지 않습니다.** 사용으로 인한 문제에 대해 저작자는 책임지지 않습니다.
- AI Usage는 **Anthropic과 관련 없는 비공식 도구**이며, Anthropic이 만들거나 보증하지 않았습니다. "Claude"와 "Claude Code"는 Anthropic의 상표입니다.
- 사용량 값은 Claude Code CLI의 `/usage` 출력을 읽은 것입니다. CLI가 업데이트되어 출력 형식이 바뀌면 표시되지 않을 수 있고, 실제 한도와 차이가 날 수 있습니다. 정확한 값은 Claude Code의 `/usage`나 claude.ai 설정에서 확인하세요.
