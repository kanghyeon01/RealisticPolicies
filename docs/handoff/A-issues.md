# 작업 A의 남은 문제점

작업 A(`A-rent-cap-extension.md`, 임대료 상한 효과 두 개 추가)를 마치며 나온 문제입니다. 아래 항목은 모두 고치지 않은 상태입니다. 빌드는 경고 0, 오류 0이고, 게임은 실행하지 않았으며 커밋도 하지 않았습니다.

## 1. 논문 수치 불일치 (Kholodilin 2024)
- `Engine/PolicyDefinitions.cs:1658` 주석: "new construction falls in **11 of 16** studies". 지시서 문구를 그대로 넣은 것입니다.
- `docs/PLAN.md:1032`: "공급 ↓ **12/16**".
- **확인 결과 (2026-11-07): 둘 다 맞고, 서로 다른 효과입니다.** 학술지판 그림 2(출판·미출판 연구 합계)에서 신규 건설(construction)은 16편 중 감소 11, 효과 없음 4, 증가 1이고, 공급(supply, 기존 재고)은 16편 중 감소 12, 효과 없음 3, 증가 1입니다. 저자는 둘을 구분합니다(공급은 재고, 건설은 흐름).
- 레버 `m_FreeResidentialProportion`은 신규 건설을 줄이는 것이라 코드 주석의 "11 of 16"이 맞는 쪽입니다. 코드는 고칠 필요가 없습니다.
- `PLAN.md:1032`는 "공급 ↓ 12/16"만 적고 있어 신규 건설 11/16을 함께 적었습니다. 같은 줄의 "잘못된 배분 14/14"는 그림에서 13/13이라 고쳤습니다(문서만).
- 출처: 그림 이미지 https://ars.els-cdn.com/content/image/1-s2.0-S1051137724000020-gr2_lrg.jpg, 논문 https://doi.org/10.1016/j.jhe.2024.101983. 학술지판 PDF는 사용자가 사람 확인을 해 준 뒤 받아 논문 폴더에 넣었습니다. 2022년 근로문서판(DIW DP 2026)은 건설 7/12, 공급 7/7로 수치가 다릅니다.

## 2. 정수 필드 때문에 같은 값을 반복해서 씀
- 위치: `Engine/ParameterLevers.cs`의 `ScaledFloatsLever<T>.Apply`와 `kLookForHome` 레버의 `m_LookForHomeHomelessDivisor`.
- 원인: `Apply`는 현재값과 `원래값 × 배율`을 비교합니다. 이 필드는 setter에서 정수로 반올림하므로 둘이 달라질 수 있습니다. 예: 10 × 1.25 = 12.5인데 저장값은 12입니다. 이때 값이 바뀌었다고 판단해 적용할 때마다 `SetComponentData`를 호출합니다.
- 영향: 값은 같으므로 결과에는 영향이 없을 것으로 추정합니다. 실제로 돌려 보지는 않았습니다.
- 참고: `kCommuterRatioLimit`도 정수로 반올림하는 레버라 같은 현상이 있을 수 있습니다(`ScaledFloatLever`의 비교 코드는 확인하지 않았습니다).
- 수정안: 쓰기 전에 setter 결과를 비교하는 방식 등으로 바꿉니다. 지시서 범위 밖이라 손대지 않았습니다.

## 3. 바닐라 동작을 코드와 대조하지 않음
- `Engine/ParameterLevers.cs`의 `kLookForHome` 주석은 "두 값을 함께 올리면 노숙 가구의 집 찾기는 그대로다(`HouseholdBehaviorSystem`)"라고 적고 있습니다. 지시서 내용을 옮긴 것이고, 게임 코드와 직접 대조하지 않았습니다.
- `m_FreeResidentialProportion`, `m_LookForHomeChanceMultiplier`, `m_LookForHomeHomelessDivisor`의 기본값(0.1 / 0.5, 2 / 10)도 지시서 값이며 확인하지 않았습니다. 빌드로 확인된 것은 필드 이름과 형식이 맞다는 점까지입니다.
- 할 일: `HouseholdBehaviorSystem`을 디컴파일해 대조합니다.
