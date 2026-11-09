# 작업 A: 임대료 상한 정책에 효과 두 개 추가 (코드)

## 배경 (이것만 알면 됩니다)
- 프로젝트: Cities: Skylines II 모드 "Realistic Policies". 폴더 `C:\Users\khkim\source\CS2Mods\RealisticPolicies`.
- 정책은 `Engine/PolicyDefinitions.cs`에 정의로 들어 있고, 각 정책의 효과(`Outputs`)는 바닐라 값에 배율을 거는 "레버"(`Engine/ParameterLevers.cs`)나 도시 효과로 나간다.
- 레버는 `ParameterLevers.RegisterBuiltIns()`에서 등록한다. 한 구조체의 여러 필드를 같은 배율로 바꿀 때는 이미 있는 `ScaledFloatsLever<T>`를 쓴다 (예: `kStructuralIntegrity` 등록부를 그대로 본뜰 것).
- 엔진은 레버마다 `원래값 × (1 + 효과 × 진행도)`를 쓴다. 효과 -0.3이면 최대일 때 원래값의 0.7배.

## 할 일
임대료 상한 정책(`PolicyCatalog.kRentCap`)에 두 효과를 더한다.

1. **신규 주택 공급 감소:** `Game.Prefabs.DemandParameterData.m_FreeResidentialProportion` (float3, 기본 0.1, 0.1, 0.1)의 x, y, z 세 값을 같은 배율로 바꾸는 레버.
   - 상수: `public const string kFreeResidential = "Demand.FreeResidentialProportion";`
   - 정책 출력: `PolicyOutput.Parameter(ParameterLevers.kFreeResidential, -0.3f)`
2. **이주 감소:** `Game.Prefabs.CitizenParametersData`의 `m_LookForHomeChanceMultiplier` (float2, 기본 0.5, 2)의 x, y와 `m_LookForHomeHomelessDivisor` (int, 기본 10)을 같은 배율로 바꾸는 레버. int 필드는 setter에서 `(int)Unity.Mathematics.math.round(v)`로, 최소 1.
   - 상수: `public const string kLookForHome = "Citizen.LookForHomeChance";`
   - 정책 출력: `PolicyOutput.Parameter(ParameterLevers.kLookForHome, 0.25f)`
   - 이유(주석에 영어로): 바닐라는 집 찾기 간격을 이 배율로 늘리고, 노숙 가구는 같은 값을 나눗수로 나누므로 둘을 함께 올리면 노숙 가구의 집 찾기는 그대로다 (`HouseholdBehaviorSystem`).
3. 두 출력은 `kRentCap` 정의의 `Outputs`에서 기존 `kResidentialLandValue` 줄 바로 아래에 넣고, 위에 근거 주석(영어)을 단다:
   `// Fewer new rental homes (new construction falls in 11 of 16 studies, Kholodilin 2024) and fewer moves (tenants 20% more likely to stay, Diamond, McQuade & Qian 2019).`
4. `Locale.cs`의 영어·한국어 사전 두 곳에 보고서 레버 이름을 추가한다. 키 형식은 `"RealisticPolicies.UI.Report.Lever.<레버 키의 마지막 점 뒤 부분>"`이다. 기존 `Report.Lever.ResidentialLandValue` 줄 바로 아래에 넣는다.
   - FreeResidentialProportion: EN "Vacancy target for new housing", KO "새 주택 건설의 빈집 목표"
   - LookForHomeChance: EN "Time between home searches", KO "집을 다시 찾는 간격"
5. `kRentCap` 정책 설명(EN·KO 각각)의 끝에 한 문장을 더한다.
   - EN: " Fewer new homes are built, and households move less often."
   - KO: " 새 주택이 덜 지어지고, 가구가 덜 이사합니다."

## 지킬 것
- 코드와 주석은 영어. 기존 코드 스타일(중괄호 줄바꿈, 4칸 들여쓰기)을 따른다.
- 위에 적은 것 외의 파일·코드는 바꾸지 않는다. 커밋하지 않는다. 게임을 실행하지 않는다.
- 빌드: `"/c/Users/khkim/AppData/Local/Microsoft/dotnet/dotnet.exe" build -c Release -p:CS2LocalModsPath="C:/Users/khkim/AppData/Local/Temp/rp-build"` (게임 Mods 폴더에 배포하지 않도록 경로를 바꿈).

## 완료 조건
- 빌드 경고 0, 오류 0.
- `Locale.cs`에서 새 키 두 개가 영어·한국어에 각각 한 번씩 있다.
- 마지막에 바꾼 파일 목록과 빌드 결과를 짧게 보고한다.
