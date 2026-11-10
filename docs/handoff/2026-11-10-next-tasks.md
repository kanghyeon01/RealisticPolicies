# 다음 세션 작업 지시 (2026-11-10 작성)

> **끝남 (2026-11-10 23:15):** 작업 1~4와 시험을 마쳤습니다. 결과는 `docs/PLAN.md` §14.7b 끝, 지금 상태와 남은 일은 `docs/handoff/2026-11-10-main.md`에 있습니다. 아래는 기록으로 남깁니다.

이 파일은 다음 세션이 그대로 이어서 할 작업 네 가지와 시험 절차입니다. 네 가지 모두 **사용자가 승인했습니다**(2026-11-10). 다시 묻지 말고 진행합니다. 시험은 이 세션의 사용량 한도 때문에 다음 세션으로 넘겼고, 사용자가 "다른 세션에서 진행"하라고 했으므로 **시험까지 다음 세션이 합니다.**

먼저 읽을 것: `docs/MEMO.md`(작업 규칙, 맨 위), `docs/handoff/2026-11-10-main.md`(지금 상태), `docs/PLAN.md` §14.7b 끝부분(오늘 시험 결과).

공통 규칙 (MEMO 요약):
- 사용자에게 보이는 글은 모두 한국어 존댓말. 마무리 상자에 질문 버튼을 넣지 않음.
- 코드와 주석은 영어. 화면 문구는 KO·EN 둘 다. 개발용 내부 어휘를 화면 문구에 쓰지 않음.
- 커밋·푸시·업로드는 사용자가 말할 때만. **커밋할 때는 `2026-11-10-main.md`도 고쳐서 같이 커밋.**
- 빌드: `"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build RealisticPolicies.csproj -c Release` (Mods 폴더에 배포됨). 4분 넘게 멈추면 Burst 후처리기 멈춤이니 다시 돌림. dotnet 프로세스를 강제로 끄지 않음.

---

## 작업 1. 지하가 있는 공원 판정에서 장식 차로 빼기 (사용자 "오케이")

**문제:** 바닐라 성 공원 `OldCastle01`이 "지하가 있는 공원"으로 판정됐다. 근거는 장식용 산울타리 차로 `HedgeHigh01` 1개가 부지보다 4.4 m 낮았던 것뿐이다(경사지). 사람이 다니는 길이 아니다.

**고칠 곳:**
- `Engine/ProtestGathering.cs`의 `SurfacePaths(Entity park, List<Entity> into)`: 차로를 도는 반복문에서 `if (y < ground - kUndergroundDepth) { below = true; continue; }` 부분. 사람이나 차가 다니는 차로일 때만 `below = true`로 한다. 즉 `Game.Net.PedestrianLane`, `Game.Net.ConnectionLane`, `Game.Net.CarLane`, `Game.Net.TrackLane` 가운데 하나를 가진 차로만 센다. 출입구(`SpawnLocationElement`) 판정은 그대로 둔다.
- 같은 파일의 `DescribeUnderground(Entity park)`(판정 근거를 로그에 남기는 진단 함수)도 같은 기준으로 센다. 장식 차로는 "ignored decoration" 따위로 따로 적어도 좋다.

**확인:** 시험 로그 `Self-test: station park ...` 줄에 `OldCastle01`이 더 나오지 않아야 한다. 시험 도시(RiverPort)에 진짜 역 공원이 없으면 `Self-test: no station park within 6 segments of a lived-in road.`가 나오고, 역 공원 확인이 건너뛰어질 수 있다. 오전 시험에서는 기차역이 있는 공원(`Pack7-TrainStation03` 근처로 추정)을 골랐던 적이 있다.

---

## 작업 2. 생계 지원의 받는 기준을 재산에서 소득으로 (사용자 "안 가로 가자")

**근거:**
- 시험 도시 129,747가구의 재산은 거의 모든 가구가 비슷하다. 하위 3% ¢1,047, 중간값 ¢1,345, 75% ¢1,557이다. 그래서 게임의 '보통' 선 ¢2,500 아래가 86%다. 재산으로는 가난한 가구를 가려낼 수 없다.
- 소득은 차이가 크다. 하위 5%는 0, 그 위 5%는 실업급여 ¢800, 중간값은 ¢2,720이다.
- 한국 생계급여는 기준 중위소득의 32% 미만 가구가 받는다(2024년, 30%에서 상향). 이 기준이면 이 도시에서 13.4%가 받는다. 한국 전체 기초생활보장 수급자는 4.9%(2023년 말), 가장 높은 전북이 7.5%다. 실업률 20%인 도시라 더 높은 것이 맞다(사용자: "가난한 사람이 많으니까 다를 수 밖에").

**바꿀 것:**
1. `Engine/PolicyDefinitions.cs`의 생계 지원 정의(`PolicyCatalog.kLivelihoodSupport`)에서 `Second`를 바꾼다.
   - `Key = "LivelihoodIncome"`, `Range = new Bounds1(20f, 50f)`, `Step = 1f`, `Default = 32f`, `Unit = "percent"`.
   - 주석: 한국 생계급여 32%, 의료급여 40%, 주거급여 48%, 교육급여 50%.
2. `Engine/LivelihoodSupportSystem.cs`의 `Select`:
   - 지금은 `wealth < limit`(재산 상한)으로 고르고 재산이 적은 순으로 뽑는다. 이를 **이 정책이 준 돈을 뺀 가구 소득 < 중위소득 × 상한%**로 고르고, **소득이 적은 순**으로 뽑게 바꾼다.
   - 중위소득은 같은 쿼리(`ServiceSeriesCensus.PoorHouseholdQueryDesc`, 이름과 달리 모든 가구)의 가구 소득 중간값이다. 이 정책이 준 돈을 뺀 값으로 계산한다.
   - 주의: `AddIncome`이 지원 가구의 `Household.m_Income`에 `m_MonthlyEach`를 더한다. `Select`가 맨 처음 `Clear()`로 `m_Supported`를 비우므로, 비우기 전에 이전 지원 가구 집합을 복사해 두고 그 가구는 소득에서 `m_MonthlyEach`를 뺀다. 이렇게 하지 않으면 받은 가구가 다음 선정에서 빠졌다가 다시 들어오기를 되풀이한다.
   - 정렬 키는 지금의 `(wealth << 32) | slot` 방식 그대로 쓰되, wealth 자리에 소득을 넣는다(음수가 되지 않게 0 이상으로 자름).
   - `LastPoor`는 "받을 수 있는 가구 수"의 뜻으로 그대로 쓴다. 엔진의 필요 인원 계산(`PolicyEngineSystem`의 `m_Livelihood.LastPoor`)이 이 값을 쓴다.
3. `PolicyEngineSystem.ResolveGameDefaults()`: 재산 상한 기본값을 게임 선에서 읽는 코드다. 더는 필요 없으니 지우거나 비운다. 호출부도 함께 정리한다.
4. `UI/RealisticPolicies.mjs`의 `SecondSection`: `unit === "percent"`일 때 `${Math.round(value)}%`로 보이게 한다.
5. `Locale.cs`의 KO·EN 문구를 고친다.
   - `RealisticPolicies.UI.Second.LivelihoodIncome`: KO "받을 수 있는 소득 상한 (중위소득 대비)", EN "Income limit (share of the median)".
   - `.Hint`: KO "가구 소득이 도시 중위소득의 이 비율보다 적으면 받을 수 있습니다. 한국 생계급여는 32%입니다. 올리면 받는 가구가 늘고, 필요한 사회복지직도 늘어납니다." EN도 같은 뜻으로 쓴다.
   - 기존 `Second.LivelihoodWealth`, `.Hint` 키는 지운다.
   - 정책 설명(`PolicyDescription(kLivelihoodSupport)`, KO·EN)에서 "재산이 상한(세부 설정, …)보다 적은 가구" 부분을 소득 기준으로 고친다. 예: KO "소득이 도시 중위소득의 일정 비율(세부 설정, 처음에는 32%)보다 적은 가구에".
6. `SelfTestSystem.cs`의 `CheckSecondSettings` 안 생계 지원 확인을 바꾼다. 상한이 32인지, 받을 수 있는 가구 비율이 대략 5~25%인지(이 도시 예상 약 13%) 확인한다. `LogHouseholdDistribution()`은 그대로 둔다.
7. `docs/PLAN.md` §14.7b와 §14.21(생계 지원)에 바뀐 기준과 근거를 적는다. `docs/REFERENCES.md`에 보건복지부 기준 중위소득·생계급여 선정기준(2024)을 넣는다.

---

## 작업 3. 성 앞 도로로 가는 길찾기 실패의 원인 찾기 (사용자 "오케이")

**지금까지 본 것 (`;protests`, 21:16):**
- 시험은 성 공원(`OldCastle01`, 공원 엔티티 1769071)에서 4구간 떨어진, 주민 145명이 사는 도로를 시위대의 집 도로로 골랐다(`ProtestSystem.FindStationPark`).
- 시위대 14명이 공원 안 길로 가다 실패했다. 4명이 실패한 뒤 안 ㄱ이 동작해 공원 앞 보도로 바꿨다(로그 "4 of 14 could not find a way onto the park's paths").
- 공원 앞 보도로 가는 길도 실패했다. 모두 자기 집 앞에 서 있었고, 설 자리까지 262 m 남아 있었다.
  - 집: `NL_ResidentialMediumRow13_L5_1x2`(2171978)의 `Walking Area` 1704608, `Connection Lane` 2092860, 위치 (-147, 536, 2533).
  - 걷는 사람의 상태: `divert PathFailed`, `path CachedObsolete` 또는 `Failed`.
- 사진: `ModsData/RealisticPolicies/selftest/protest-crowd-stopped.png`, `protest-crowd-stopped-wide.png`(17:30 시험분). 앨더브룩 거리의 붉은 지붕 연립주택이다.

**볼 것:**
- 성 공원의 도로(`Building.m_RoadEdge`)에 보도가 있는지, 그 보도가 이 주택가의 보행망과 이어져 있는지 본다. 오솔길, 비포장길, 높이 차이, 다른 망일 가능성이 있다. `Sidewalks(edge)`가 고른 차로의 소유자 프리팹 이름을 로그에 남기면 알 수 있다.
- 이 집들에서 다른 곳(예: 자기 도로의 보도 설 자리)으로 가는 길은 되는지 본다. 공원이 없는 평범한 시위(`TestNoPark`)를 이 도로에서 시작해 보면 된다. 집 쪽 문제(이 연립주택 에셋의 보행 구역)인지, 목적지 쪽 문제인지 가를 수 있다.
- 실제 게임에서는 시위 공원을 거리로 고른다(`PickPark`). 그러니 이 문제가 시험이 억지로 고른 조합에서만 생기는지도 판단해서 사용자에게 보고한다.
- 원인과 고칠 안은 **보고만 하고**, 고치는 것은 사용자 승인 뒤에 한다(MEMO 규칙).

---

## 작업 4. 폭동 범죄가 폭동 현장에서 멀리 일어나는 원인 찾기 (사용자 "오케이")

**지금까지 본 것:**
- 확인 "A rioter's crime happens near the riot (within 300 m of its roads)"가 오후 세 번 연속 실패했다(6,007 m, 2,691 m, 6,167 m). 오전 `;changed`에서는 14 m로 통과했다.
- 매번 `crime trips pointed at riot buildings`는 1~2건이었다.

**관련 코드:**
- `Engine/RiotCrimeTargetSystem.cs`: 게임이 대상 없이 만든 범죄 이동(`TripNeeded`, Purpose Crime)에 폭동 건물을 넣는다. `TripNeededSystem` 앞에 16프레임마다 돈다.
- `Engine/ProtestSystem.cs`: `RiotCrimes`, `RiotCriminals`, `RiotCrimeTargetSystem.Add`.
- `SelfTestSystem.cs`: `RiotCrimeSceneDistance(Protest riot)`는 폭동 참가 범죄자의 `Criminal.m_Event`에 해당하는 `AccidentSite` 가운데 가장 가까운 것까지의 거리를 잰다. `UpdateCleanupRiot`이 이 확인을 한다.

**가설 (확인 안 됨):**
- (가) `RiotCriminals`에 저장 전(`m_CrimesBeforeSave`)이나 폭동 전부터 범죄자였던 시민이 섞여, 그 사람의 다른 사건 현장이 잡힌다.
- (나) 게임이 대상을 우리가 넣기 전에 정했거나, `TripNeeded`를 거치지 않는 경로로 범죄를 시작했다.
- (다) 불러오기 뒤 `RiotCrimeTargetSystem`의 대상 목록(세션 전용)이 비어 있었다.

**볼 것:** 폭동 범죄마다 시작 프레임, 범죄자, 넣은 대상 건물, 실제 `AccidentSite` 위치, 그 사이의 대상 변화를 로그에 남겨 비교한다. 원인과 고칠 안을 사용자에게 **보고만 하고**, 고치는 것은 승인 뒤에 한다.

---

## 시험 절차 (다음 세션이 진행)

1. 작업 1·2를 고치고, 작업 3·4의 진단 로그를 넣은 뒤 빌드·배포한다.
2. 게임이 꺼진 상태에서 `ModsData/RealisticPolicies/selftest.flag`에 `save:RP SelfTest RiverPort;changed`를 쓰고 띄운다.
   - 실행: `Start-Process explorer.exe 'steam://rungameid/949230'`.
   - Paradox Launcher의 PLAY는 agent-desktop으로 직접 누른다(`desktop_find` app "Paradox Launcher.exe", role button, name PLAY, exact → `desktop_click`).
   - 약 14분 걸린다. 생계 지원 확인과 세부 설정 확인을 본다.
3. 이어서 `;protests`를 돌린다(약 10~19분). 작업 1(역 공원 판정), 작업 3, 작업 4의 로그를 본다.
4. 감시는 로그 `Logs/RealisticPolicies.log`의 `[FAIL]`·`Result:`로 한다. **감시 명령에 `| head`를 붙이지 않는다**(버퍼 때문에 알림이 오지 않음).
5. 직전 결과(비교용):
   - `;changed` 76 통과·3 실패(14:55, 실패는 모두 역 공원 시위).
   - `;protests` 46 통과·2 실패(21:16, 역 공원 시위와 폭동 범죄 위치).
6. 결과는 `docs/PLAN.md` §14.7b에 적고, 사용자에게 한국어로 보고한다.

## 그 밖에 남은 것 (이번 작업 범위 밖, 연계 파일에도 있음)
- 세부 설정 UI를 게임 화면에서 아직 보지 않았다.
- 임대료 지원 최대 개월은 실제로 확인되지 않았다(지원받은 가구 0).
- 소송 판결이 세부 설정을 되돌리는지 시험하지 않았다.
- `REFERENCES.md`에 빠진 문헌 7건, `CONCEPTS.md` 보충.
- 건물 기준 안 3(기존 건물 보강 의무)은 다음 업데이트로 미룸.
- 쓰레기 종량제를 구역 정책으로 옮기기.
- 올리기 전에 전체 시험.
