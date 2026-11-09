# 작업하며 기억할 것 (Realistic Policies)

이 파일은 작업 중에 사용자가 짚어 준 것, 다시 틀리면 안 되는 것을 적는다.
메모리(Claude 메모리)에 적지 말고 여기에 적는다 (2026-11-09 사용자 지시).
설계 결정 자체는 `docs/PLAN.md`에 적고, 여기에는 헷갈리기 쉬운 규칙과 주의할 점만 적는다.

## 말하기 (가장 먼저 지킬 것)

- **사용자에게 보이는 글은 모두 한국어 존댓말로 쓴다.** 마지막 답변뿐 아니라 작업 중 진행 문구, 마무리 상자(recap), 계획 목록까지 전부다. 영어로 쓰지 않는다. (2026-11-09, 영어로 답해서 두 번 지적받음)
- "패러독스"라고 쓴다.

## 개발 환경

- **빠진 도구나 제약이 있으면 바로 말한다 (2026-11-09 사용자 지적 "없었으면 말을 했어야지").** 공식 모딩 도구(Burst·병렬 작업)를 설치하지 않은 채 지금까지 만들어 왔는데, 그 사실과 영향(성능 개선 한계)을 성능 작업 전에 알리지 않았다.
- 2026-11-09 사용자 결정: 공식 모딩 도구를 설치한다 (사용자가 게임 옵션에서 설치 후 알려 주기로 함). 설치되면 csproj를 공식 방식으로 옮기고, 시위 시작·생계 지원·인구조사 같은 큰 반복문을 Burst 병렬 작업으로 바꾼다. 세 모드(Realistic Aviation, NoJaywalking 포함)가 같은 빌드 방식을 쓰므로 옮길 때 영향 범위를 먼저 확인한다.

- 공식 도구 설치 확인 (2026-11-09): 환경 변수 `CSII_*` 있음, Unity 2022.3.62f2, Entities 1.3.10, 게임은 D:\SteamLibrary. 공식 도구가 주는 것: 우리 코드의 Burst 컴파일·병렬 작업. 주지 않는 것: 바닐라 Burst 코드 수정, 게임이 읽는 값의 범위(도시 효과는 도시 전체). "레버가 없다"고 하기 전에, 우리 시스템이 개체마다 값을 직접 쓰는 우회가 되는지도 따져서 같이 말한다.

- **빌드는 공식 도구 방식 (2026-11-09 사용자 결정, Realistic Policies만. 다른 모드는 이 세션에서 만들지 않았으니 건드리지 않는다).** csproj가 `CSII_TOOLPATH`의 Mod.props·Mod.targets를 가져온다: 컴파일 → Entities 후처리 → Burst 컴파일 → Mods 폴더에 복사(폴더를 지우고 다시 넣음) → UI(mjs)는 DeployUI가 복사. 빌드 명령은 그대로 `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe build ... -c Release`, 배포 없이 빌드하려면 `-p:LocalModsPath=<임시 폴더>`.
  - 옮기며 고친 것: 시스템 클래스는 모두 `partial` (Entities 소스 생성기), 게임의 mscorlib만 쓰기(`NoStdLib`, `FrameworkPathOverride`=게임 Managed, 참조 어셈블리 패키지 끔 — .NET 4.8 표준 라이브러리에는 게임 API가 쓰는 Span이 없음), PDB 켬(없으면 Entities 후처리기가 실패).
  - 게임의 공식 모딩 도구 화면은 모든 구성 요소가 "설치됨"이다 (.NET SDK 8.0.427 포함, 2026-11-09 사용자 확인). 공식 도구가 된다고 하면 된다고 본다 — 내가 되고 안 되고를 정하지 않는다 (사용자 지적). 후처리기 파일(ModPostProcessor.runtimeconfig.json)이 net6.0으로 표시되어 그대로는 실행 오류가 났으므로, csproj의 RunModPostProcessor가 `DOTNET_ROLL_FORWARD=Major`로 설치된 .NET 8에서 돌린다. 사용자에게 따로 설치를 요구하지 않는다. 이전 csproj는 scratchpad의 RealisticPolicies.csproj.before-toolchain.

  - 2026-11-10: 배포 빌드 한 번이 Burst 후처리기에서 멈췄다. 게임의 Unity.Collections.dll을 해시하다 NullReferenceException이 났고, 400초 넘게 끝나지 않았다. 같은 코드로 다시 돌리니 성공했다. 일시적인 오류로 보이면 멈춘 빌드를 끄고 한 번 다시 돌린다.

## 시험

- **시험은 몰아서 한다 (2026-11-09 사용자 지적 "테스트는 몰아서 하지?").** 고칠 것을 다 고친 뒤 한 번에 돌린다. 기능 하나 고칠 때마다 게임을 띄우지 않는다.
- **전체 시험 띄우는 법 (2026-11-09 정리):**
  1. 게임이 꺼진 상태에서 `scratchpad/scripts/copy_save.py "<Saves>/76561199507274511/RiverPort State 3a7.cok" "RP SelfTest RiverPort"`로 사본을 만든다. 스크립트는 C--/a34d9cab… 세션 scratchpad에 있다. 개체 id를 새로 바꾼다.
  2. `ModsData/RealisticPolicies/selftest.flag`에 `save:RP SelfTest RiverPort`를 쓴다. 시위만 볼 때는 뒤에 `;protests`를 붙인다.
     - **보통은 `;changed`로 돌린다 (2026-11-10 사용자 결정 "이미 수 차례 완벽하게 통과한 테스트는 제외하고 바뀌었거나 문제가 있을 만한 곳만").** 3~10번 묶음(새 정책 포함), 시위, 파업, 폭동(저장·다시 불러오기), 모드 제거 준비만 돈다. 약 12~15분(추정). 전체 시험은 올리기 전이나 큰 변경 때만 돌린다.
     - 새로 바뀐 곳이 `;changed`에 빠진 단계에 있으면, 돌리기 전에 그 단계를 넣을지 정한다.
  3. `Start-Process explorer.exe 'steam://rungameid/949230'`으로 띄운다.
  4. **Paradox 런처의 PLAY는 내가 누른다 (2026-11-09 사용자 지적 "너가 플레이 누르는거지?").** agent-desktop으로 `Paradox Launcher` 창에서 `PLAY` 버튼을 찾아 누른다. 시험이 메인 메뉴에서 세이브를 직접 불러오므로 RESUME가 아니라 PLAY다. 사용자에게 누르라고 하지 않는다.
  5. 결과는 `ModsData/RealisticPolicies/selftest/report.txt`에 나온다.
  - 시험 중 카메라가 한쪽으로 밀리던 문제 (2026-11-10 사용자 지적): 시험하는 동안 커서는 다른 모니터에 있는데, 게임의 가장자리 스크롤이 이를 화면 끝으로 읽었다. 이제 자체 시험이 가장자리 스크롤을 끄고, 끝나면 되돌린다(`HoldEdgeScrolling`).
  - 결과 파일은 시험이 끝날 때 한 번에 써진다. 진행 중에는 `Logs/RealisticPolicies.log`의 `[FAIL]`·`Result:` 줄을 본다. 전체 시험은 약 25~30분 걸린다.
- 시험 사진이 1920×1080인 것은 모드가 아니라 게임이 그 해상도로 뜨기 때문이다 (Player.log "Screen resolution: 1920x1080"). 게임 설정 파일(Settings.coc)에 해상도 가로·세로가 저장되어 있지 않았다. 사용자 모니터는 WQHD. → 2026-11-09 사용자 승인으로 Settings.coc의 resolution에 width 2560, height 1440을 넣었다 (원본은 scratchpad의 Settings.coc.before-wqhd). → 먹히지 않았다: 게임이 설정 파일을 다시 쓰면서 width·height를 지웠고, 로그는 계속 "Screen resolution: 1920x1080x164". 게임(Unity)이 화면을 1920×1080으로 보고 있다는 뜻이라, Windows 디스플레이 배율(2560×1440에 133%면 1920×1080으로 보임) 때문일 수 있다 (추정, 사용자에게 게임 옵션의 해상도 목록 확인을 부탁).

## 시위·폭동

- **성질과 강도는 다른 것이다 (2026-11-09 사용자 정정).**
  - 평화·일반·강경·극단은 시위의 **성질(특성)**이다. 시위가 생길 때 정해지고 끝날 때까지 바뀌지 않는다.
  - **강도**에 따라 달라지는 것은 지금까지 **시위/폭동 구분 하나뿐**이다 (강도 0.6 이상이면 폭동).
  - "평화 시위 = 강도가 낮은 시위"처럼 섞어 쓰지 않는다. 효과를 나눌 때는 "시위인가 폭동인가" 또는 "어떤 성질인가"로 나눈다.
