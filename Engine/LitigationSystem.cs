using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game;
using Game.City;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Administrative litigation (docs/PLAN.md §7): groups aggrieved by a regulation or a cut benefit sue; the city
    /// answers with a lawyer or its own officials (or not at all); the court may suspend the policy, then rules on
    /// proportionality, legitimate expectation and procedure. A lost suit bans the policy for a while and costs
    /// damages and the plaintiffs' costs, paid in daily instalments that never take the city below a reserve.
    /// Runs every 1024 frames (about 1.4 game hours); filing decisions use the engine's latest step.
    /// </summary>
    public sealed class LitigationSystem : GameSystemBase
    {
        public const int kFramesPerDay = PolicyEngineSystem.kFramesPerDay;
        public const string kLawyerFeeRow = "RealisticPolicies.Court.LawyerFees";
        public const string kDebtRow = "RealisticPolicies.Court.Damages";

        // Self-test hooks: shorter suits and bans, forced filing, injunction requests and outcomes.
        public static float TestTimeScale = 1f;
        public static bool TestDisableRandomFiling;
        public static bool TestForceInjunctionRequest;

        /// <summary>When set, this policy is sued at the next engine step, grievance or not.</summary>
        public static Entity TestFilingPolicy = Entity.Null;

        /// <summary>-1 draws the verdict; 0 city wins, 1 city loses fully, 2 city loses partly.</summary>
        public static int TestForcedVerdict = -1;

        /// <summary>Lets the self-test make settlement offers back to back.</summary>
        public static bool TestSkipSettlementWait;

        /// <summary>Makes the plaintiffs refuse every offer, so the self-test can reach the offer limits.</summary>
        public static bool TestRefuseSettlement;

        /// <summary>Overrides the "default response" setting while set (the self-test answers by hand).</summary>
        public static DefaultSuitResponse? TestDefaultResponse;

        // Filing: daily logit on the plaintiff group's grievance (McFadden; only "none" and "suit" are on).
        private const float kFilingIntercept = -4f;
        private const float kFilingSlope = 2f;

        // Share of a group that joins the suit per unit of grievance (households sue together, companies alone).
        private const float kHouseholdShare = 0.002f;
        private const float kCompanyShare = 0.01f;
        private const float kMaxShare = 0.5f;

        // A plaintiff's loss per day at full burden: this share of what a member of its group pays in tax per day.
        private const float kLossShareOfTax = 0.5f;

        // Fallback when the group pays no tax yet.
        private const float kHouseholdLossPerDay = 20f;
        private const float kCompanyLossPerDay = 2000f;

        // Proportionality: effect must reach this share of the harm.
        private const float kProportionalityThreshold = 0.8f;

        // Verdict logit (§7 "판결"), lawyer bonus, partial losses (사법연감 2026: 27.7% of lost judgments).
        private const float kVerdictIntercept = -2.944f;
        private const float kVerdictSlope = 6.7f;
        private const float kLawyerBonus = 0.3f;
        private const float kPartialShare = 0.28f;

        // Injunction: requested in ~14.7% of suits (사법연감 2026), granted ~44% on average.
        private const float kInjunctionRequest = 0.147f;
        private const float kInjunctionIntercept = -1.2f;
        private const float kInjunctionUnanswered = 1f;
        private const int kInjunctionDecisionFrames = kFramesPerDay / 48;
        private const int kInjunctionEffectFrames = kFramesPerDay / 24;

        // Share of the grievance that saved costs can count for in a settlement.
        private const float kSettlementSavingCap = 0.3f;

        // Settlement offers per suit, and the wait after a refused one (2026-11-04 user proposal: 3 offers, 3 hours apart).
        public const int kMaxSettlementOffers = 3;
        public const int kSettlementWaitFrames = 3 * kFramesPerDay / 24;

        // Costs (§7 "소송 대응", "소송 비용 부담").
        private const float kLawyerShareOfIncome = 0.005f;
        private const float kStandardWorkload = 66f;
        private const float kLateLawyerSurcharge = 1.5f;
        private const float kLateShare = 0.3f;

        // Damages (§7 "패소하면").
        private const float kDamagesCeiling = 2f;
        private const float kImmediateShare = 0.05f;
        private const float kDailyCapBase = 0.04f;
        private const float kDailyCapSeverity = 0.04f;

        private PolicyEngineSystem m_Engine;
        private CitySystem m_CitySystem;
        private SimulationSystem m_SimulationSystem;
        private CityServiceBudgetSystem m_BudgetSystem;
        private BudgetInjectionSystem m_Budget;
        private TimeSystem m_TimeSystem;
        private EndFrameBarrier m_EndFrameBarrier;
        private EntityArchetype m_ModifyArchetype;
        private EntityQuery m_EconomyQuery;
        private CourtNotifications m_Notifications;
        private int m_LastEngineStep = -1;
        private uint m_LastFrame;
        private bool m_HasLastFrame;

        /// <summary>Acceptance limit of the last settlement offer as a share of the grievance (self-test).</summary>
        public float LastSettlementLimitShare { get; private set; }

        /// <summary>Suits the city has not answered yet (badge on the City Info button).</summary>
        public int UnansweredCount { get; private set; }

        /// <summary>General administration staff the city's officials need for suits right now.</summary>
        public float OfficialStaff { get; private set; }

        /// <summary>Outcome of the last settlement offer.</summary>
        public SettlementResult LastSettlementResult { get; private set; }

        /// <summary>Changes whenever suits, bans or debts may look different to the player (UI refresh).</summary>
        public int Version { get; private set; }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1024;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Engine = World.GetOrCreateSystemManaged<PolicyEngineSystem>();
            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_BudgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            m_Budget = World.GetOrCreateSystemManaged<BudgetInjectionSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_ModifyArchetype = EntityManager.CreateArchetype(ComponentType.ReadWrite<Event>(), ComponentType.ReadWrite<Modify>());
            m_EconomyQuery = GetEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            m_Notifications = new CourtNotifications(EntityManager, World.GetOrCreateSystemManaged<Game.Notifications.IconCommandSystem>(),
                World.GetOrCreateSystemManaged<Game.Triggers.CreateChirpSystem>(), GetEntityQuery(CourtNotifications.OfficeQueryDesc));
        }

        [Preserve]
        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_HasLastFrame = false;
            m_LastEngineStep = -1;
            OfficialStaff = 0f;
            UnansweredCount = 0;
            m_Notifications.Reset();
        }

        // ------------------------------------------------------------------ queries for the engine and the UI

        /// <summary>Index of the pending suit against a policy, or -1.</summary>
        public int FindSuit(Entity policy)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<Lawsuit>(city))
            {
                return -1;
            }
            DynamicBuffer<Lawsuit> suits = EntityManager.GetBuffer<Lawsuit>(city, true);
            for (int i = 0; i < suits.Length; i++)
            {
                if (suits[i].m_Policy == policy)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>The policy's effect is suspended by an injunction in effect.</summary>
        public bool IsSuspended(Entity policy)
        {
            int i = FindSuit(policy);
            return i >= 0 && EntityManager.GetBuffer<Lawsuit>(m_CitySystem.City, true)[i].m_Injunction == (byte)InjunctionStage.InEffect;
        }

        public bool IsBanned(Entity policy) => FindBan(policy) >= 0;

        /// <summary>The player's chance to win as the court would see it now (without the hint noise).</summary>
        public float WinChance(int suitIndex)
        {
            Lawsuit suit = EntityManager.GetBuffer<Lawsuit>(m_CitySystem.City, true)[suitIndex];
            SuitResponse response = (SuitResponse)suit.m_Response;
            // Before answering, show the chance with the city's own officials.
            return 1f - LoseProbability(suit, response == SuitResponse.None ? SuitResponse.Official : response);
        }

        /// <summary>Hint shown to the player: 0 low (&lt; 35%), 1 medium, 2 high (&gt; 65%), with the suit's fixed noise.</summary>
        public int WinHint(int suitIndex)
        {
            Lawsuit suit = EntityManager.GetBuffer<Lawsuit>(m_CitySystem.City, true)[suitIndex];
            float chance = WinChance(suitIndex) + suit.m_HintNoise;
            return chance < 0.35f ? 0 : chance > 0.65f ? 2 : 1;
        }

        /// <summary>Game hours until the verdict (at least 0).</summary>
        public float RemainingHours(Lawsuit suit)
        {
            uint age = m_SimulationSystem.frameIndex - suit.m_FiledFrame;
            float frames = suit.m_DurationFrames - math.min(age, suit.m_DurationFrames);
            return frames * 24f / (kFramesPerDay * TestTimeScale);
        }

        /// <summary>Share of the suit already over, 0..1.</summary>
        public float Progress(Lawsuit suit)
        {
            uint age = m_SimulationSystem.frameIndex - suit.m_FiledFrame;
            return suit.m_DurationFrames > 0 ? math.saturate(age / (float)suit.m_DurationFrames) : 1f;
        }

        /// <summary>Lawyer fee if the city hired a lawyer now (fixed once chosen).</summary>
        public float LawyerFeeNow(Lawsuit suit)
        {
            if (suit.m_Response == (byte)SuitResponse.Lawyer)
            {
                return suit.m_LawyerFee;
            }
            float remaining = RemainingShare(suit, m_SimulationSystem.frameIndex);
            return AnnualIncome() * kLawyerShareOfIncome * suit.m_Workload / kStandardWorkload * (remaining <= kLateShare ? kLateLawyerSurcharge : 1f);
        }

        /// <summary>True when hiring a lawyer now costs the late surcharge.</summary>
        public bool IsLate(Lawsuit suit) => RemainingShare(suit, m_SimulationSystem.frameIndex) <= kLateShare;

        /// <summary>General administration staff the suit needs if officials take it now: workload over the remaining hours.</summary>
        public int OfficialsNeeded(Lawsuit suit)
        {
            float hours = math.max(1f, math.ceil(RemainingHours(suit)));
            return (int)math.ceil(suit.m_Workload / hours);
        }

        /// <summary>Game hours until the injunction granted takes effect (0 when not pending).</summary>
        public float InjunctionHours(Lawsuit suit)
        {
            if (suit.m_Injunction != (byte)InjunctionStage.Granted)
            {
                return 0f;
            }
            uint age = m_SimulationSystem.frameIndex - suit.m_FiledFrame;
            return math.max(0f, kInjunctionEffectFrames * TestTimeScale - age) * 24f / (kFramesPerDay * TestTimeScale);
        }

        /// <summary>Frames before the next settlement offer may be made (0 = now).</summary>
        public uint SettlementWaitFrames(Lawsuit suit, uint frame)
        {
            if (TestSkipSettlementWait || suit.m_Offers == 0)
            {
                return 0u;
            }
            uint wait = (uint)(kSettlementWaitFrames * TestTimeScale);
            uint since = frame - suit.m_LastOfferFrame;
            return since >= wait ? 0u : wait - since;
        }

        /// <summary>Game hours before the next settlement offer may be made.</summary>
        public float SettlementWaitHours(Lawsuit suit) =>
            SettlementWaitFrames(suit, m_SimulationSystem.frameIndex) * 24f / (kFramesPerDay * TestTimeScale);

        /// <summary>Game days left on a ban.</summary>
        public float BanDaysLeft(PolicyBan ban)
        {
            uint frame = m_SimulationSystem.frameIndex;
            return ban.m_UntilFrame > frame ? (ban.m_UntilFrame - frame) / (kFramesPerDay * TestTimeScale) : 0f;
        }

        /// <summary>Current month as year × 12 + month (0-based), the unit of <see cref="Lawsuit.m_ActMonth"/>.</summary>
        public int CurrentMonth() => m_TimeSystem.year * 12 + math.min(11, (int)math.floor(m_TimeSystem.normalizedDate * 12f));

        // ------------------------------------------------------------------ player actions

        /// <summary>Answers a suit (also the objection to an injunction granted before answering).</summary>
        public bool Respond(int suitIndex, SuitResponse response)
        {
            Entity city = m_CitySystem.City;
            DynamicBuffer<Lawsuit> suits = EntityManager.GetBuffer<Lawsuit>(city);
            if (suitIndex < 0 || suitIndex >= suits.Length || response == SuitResponse.None)
            {
                return false;
            }
            Lawsuit suit = suits[suitIndex];
            if (suit.m_Response != (byte)SuitResponse.None)
            {
                return false;
            }
            uint frame = m_SimulationSystem.frameIndex;
            suit.m_Response = (byte)response;
            suit.m_ResponseFrame = frame;
            if (response == SuitResponse.Lawyer)
            {
                float remaining = RemainingShare(suit, frame);
                suit.m_LawyerFee = AnnualIncome() * kLawyerShareOfIncome * suit.m_Workload / kStandardWorkload
                    * (remaining <= kLateShare ? kLateLawyerSurcharge : 1f);
            }
            if (suit.m_DecidedUnanswered && (suit.m_Injunction == (byte)InjunctionStage.Granted || suit.m_Injunction == (byte)InjunctionStage.InEffect))
            {
                // One review now that the city has answered: keep the injunction with the probability it would have had.
                float before = suit.m_InjunctionProbability;
                float after = InjunctionProbability(city, suit, false);
                float cancel = before > 0f ? math.saturate((before - after) / before) : 0f;
                var random = new Random(suit.m_Seed ^ 0x9E3779B9u);
                if (random.NextFloat() < cancel)
                {
                    suit.m_Injunction = (byte)InjunctionStage.Cancelled;
                    Mod.Log.Info($"Case {suit.m_CaseNumber}: injunction cancelled on review ({before:0.00} -> {after:0.00}).");
                }
            }
            suit.m_DecidedUnanswered = false;
            suits[suitIndex] = suit;
            Version++;
            Mod.Log.Info($"Case {suit.m_CaseNumber}: city answers with {response}{(response == SuitResponse.Lawyer ? $", fee {suit.m_LawyerFee:0}" : "")}.");
            return true;
        }

        /// <summary>
        /// Offers a settlement: a new intensity (0..1) for the policy and a compensation payment. The plaintiffs accept
        /// when what is left of their grievance is no more than they expect from court, less the costs they save
        /// (Posner 1973, Priest–Klein 1984). Accepted: the suit ends without ban or damages.
        /// </summary>
        public bool ProposeSettlement(int suitIndex, float newIntensity, int compensation)
        {
            LastSettlementResult = SettlementResult.Invalid;
            Entity city = m_CitySystem.City;
            DynamicBuffer<Lawsuit> suits = EntityManager.GetBuffer<Lawsuit>(city);
            if (suitIndex < 0 || suitIndex >= suits.Length)
            {
                return false;
            }
            Lawsuit suit = suits[suitIndex];
            PolicyDefinition definition = Definition(suit.m_Policy);
            if (definition == null)
            {
                return false;
            }
            // Three offers per suit, and a refused offer must wait three hours, so the player cannot probe the limit.
            uint frame = m_SimulationSystem.frameIndex;
            if (suit.m_Offers >= kMaxSettlementOffers)
            {
                LastSettlementResult = SettlementResult.NoOffersLeft;
                return false;
            }
            if (SettlementWaitFrames(suit, frame) > 0)
            {
                LastSettlementResult = SettlementResult.Waiting;
                return false;
            }
            newIntensity = math.saturate(newIntensity);
            // What is left of the grievance shrinks with the contested change; compensation buys off the rest.
            float gapNow = math.max(1e-3f, suit.m_AgainstCut ? suit.m_PriorIntensity - suit.m_ContestedIntensity : suit.m_ContestedIntensity - suit.m_PriorIntensity);
            float gapNew = suit.m_AgainstCut ? math.max(0f, suit.m_PriorIntensity - newIntensity) : math.max(0f, newIntensity - suit.m_PriorIntensity);
            float perMoney = suit.m_Grievance / AnnualPlaintiffLoss(definition, suit);
            float remaining = suit.m_Grievance * math.min(1f, gapNew / gapNow) - perMoney * compensation;
            float plaintiffWin = LoseProbability(suit, suit.m_Response == (byte)SuitResponse.None ? SuitResponse.Official : (SuitResponse)suit.m_Response);
            // Settling saves the plaintiffs their costs, which makes them accept more (Priest–Klein); the saving
            // counts for at most 30% of the grievance (2026-11-04 user decision, to be tuned).
            float saving = math.min(perMoney * PlaintiffCosts(suit), kSettlementSavingCap * suit.m_Grievance);
            float limit = (1f - plaintiffWin * 0.8f) * suit.m_Grievance + saving;
            bool accepted = !TestRefuseSettlement && remaining <= limit;
            LastSettlementLimitShare = suit.m_Grievance > 0f ? limit / suit.m_Grievance : 0f;
            LastSettlementResult = accepted ? SettlementResult.Accepted : SettlementResult.Refused;
            suit.m_Offers++;
            suit.m_LastOfferFrame = frame;
            suits[suitIndex] = suit;
            Version++;
            Mod.Log.Info($"Case {suit.m_CaseNumber}: settlement offer intensity {newIntensity:0.00}, compensation {compensation} -> remaining grievance {remaining:0.000}, " +
                $"limit {limit:0.000} (plaintiff win {plaintiffWin:0.00}, saving {saving:0.000}), {(accepted ? "accepted" : "refused")}.");
            if (!accepted)
            {
                return false;
            }
            SetIntensity(city, definition, newIntensity);
            if (compensation > 0)
            {
                Pay(city, compensation);
            }
            // A settlement costs little legitimacy (§7).
            PolicyEngineState state = EntityManager.GetComponentData<PolicyEngineState>(city);
            state.m_Legitimacy = math.saturate(state.m_Legitimacy - 0.01f);
            EntityManager.SetComponentData(city, state);
            suits.RemoveAt(suitIndex);
            return true;
        }

        // ------------------------------------------------------------------ update

        [Preserve]
        protected override void OnUpdate()
        {
            long start = Perf.Start();
            try
            {
                Step();
            }
            finally
            {
                Perf.Stop("Litigation", start);
            }
        }

        private void Step()
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !PolicyCatalog.IsRegistered || !EntityManager.HasComponent<PolicyEngineState>(city))
            {
                return;
            }
            EnsureState(city);
            uint frame = m_SimulationSystem.frameIndex;
            float dtDays = (m_HasLastFrame ? frame - m_LastFrame : 1024u) / (float)kFramesPerDay;
            m_LastFrame = frame;
            m_HasLastFrame = true;
            dtDays = math.min(dtDays, 1f);

            if (m_Engine.StepCount != m_LastEngineStep)
            {
                float engineDays = m_LastEngineStep < 0 ? 0f : PolicyEngineSystem.UpdateIntervalFrames / (float)kFramesPerDay;
                m_LastEngineStep = m_Engine.StepCount;
                if (engineDays > 0f)
                {
                    ConsiderFiling(city, frame, engineDays);
                }
            }

            UpdateSuits(city, frame, dtDays);
            UpdateBans(city, frame);
            UpdateDebts(city, dtDays);

            DynamicBuffer<Lawsuit> suits = EntityManager.GetBuffer<Lawsuit>(city, true);
            int unanswered = 0;
            for (int i = 0; i < suits.Length; i++)
            {
                unanswered += suits[i].m_Response == (byte)SuitResponse.None ? 1 : 0;
            }
            UnansweredCount = unanswered;
            m_Notifications.Refresh(suits);
            Version++;
        }

        private void EnsureState(Entity city)
        {
            if (!EntityManager.HasBuffer<Lawsuit>(city))
            {
                EntityManager.AddBuffer<Lawsuit>(city);
            }
            if (!EntityManager.HasBuffer<PolicyBan>(city))
            {
                EntityManager.AddBuffer<PolicyBan>(city);
            }
            if (!EntityManager.HasBuffer<CourtDebt>(city))
            {
                EntityManager.AddBuffer<CourtDebt>(city);
            }
            if (!EntityManager.HasComponent<LitigationState>(city))
            {
                EntityManager.AddComponentData(city, new LitigationState { m_NextCase = 1001 });
            }
        }

        // ------------------------------------------------------------------ filing

        private void ConsiderFiling(Entity city, uint frame, float days)
        {
            PolicyDefinition[] all = PolicyDefinitions.All;
            for (int i = 0; i < all.Length; i++)
            {
                PolicyDefinition definition = all[i];
                // Only dispositions can be challenged: regulation and money, not information or organization (§7).
                if ((definition.Instrument != Instrument.Authority && definition.Instrument != Instrument.Treasure)
                    || definition.Prefab == Entity.Null || FindSuit(definition.Prefab) >= 0 || IsBanned(definition.Prefab))
                {
                    continue;
                }
                PolicyStepResult result = m_Engine.LastResults[i];
                bool forced = TestFilingPolicy != Entity.Null && TestFilingPolicy == definition.Prefab;
                if (!forced && (TestDisableRandomFiling || TestFilingPolicy != Entity.Null))
                {
                    continue;
                }
                if (!PickPlaintiffs(i, definition, out GroupStake stake, out float grievance, out bool againstCut))
                {
                    if (!forced)
                    {
                        continue;
                    }
                    // Forced test suit without grievance: the most burdened group, grievance 1.
                    stake = definition.Groups[0];
                    foreach (GroupStake candidate in definition.Groups)
                    {
                        stake = candidate.Burden > stake.Burden ? candidate : stake;
                    }
                    grievance = 1f;
                    againstCut = false;
                }
                float daily = 1f / (1f + math.exp(-(kFilingIntercept + kFilingSlope * grievance)));
                float chance = 1f - math.pow(1f - daily, days);
                var random = new Random(math.max(1u, frame * 747796405u + (uint)i * 2891336453u));
                if (!forced && random.NextFloat() >= chance)
                {
                    continue;
                }
                if (forced)
                {
                    TestFilingPolicy = Entity.Null;
                }
                File(city, frame, i, definition, result, stake, grievance, againstCut, ref random);
            }
        }

        /// <summary>The group with the most grievance about the policy at the engine's last step.</summary>
        private bool PickPlaintiffs(int index, PolicyDefinition definition, out GroupStake best, out float grievance, out bool againstCut)
        {
            best = default;
            grievance = 0f;
            againstCut = false;
            foreach (GroupStake stake in definition.Groups)
            {
                float g = m_Engine.GroupGrievance(index, stake.Group);
                if (g > grievance && m_Engine.Census.TargetCount(stake.Group) > 0f)
                {
                    grievance = g;
                    best = stake;
                    // A benefit group aggrieved means the benefit was cut.
                    againstCut = stake.Benefit > stake.Burden;
                }
            }
            return grievance > 0f;
        }

        private void File(Entity city, uint frame, int index, PolicyDefinition definition, PolicyStepResult result, GroupStake stake, float grievance, bool againstCut, ref Random random)
        {
            DynamicBuffer<PolicyRuntime> runtimes = EntityManager.GetBuffer<PolicyRuntime>(city, true);
            PolicyRuntime runtime = default;
            for (int r = 0; r < runtimes.Length; r++)
            {
                if (runtimes[r].m_Policy == definition.Prefab)
                {
                    runtime = runtimes[r];
                }
            }
            bool companies = stake.Group != TargetGroup.Households;
            float members = m_Engine.Census.TargetCount(stake.Group);
            float share = math.min(kMaxShare, (companies ? kCompanyShare : kHouseholdShare) * grievance);
            int plaintiffs = math.max(1, (int)math.round(members * share));

            var suit = new Lawsuit
            {
                m_Policy = definition.Prefab,
                m_FiledFrame = frame,
                m_Group = (byte)stake.Group,
                m_Plaintiffs = plaintiffs,
                m_Representative = m_Engine.Census.RandomMember(stake.Group, ref random),
                m_AgainstCut = againstCut,
                m_ContestedIntensity = result.Intensity,
                m_PriorIntensity = runtime.m_Reference,
                m_Grievance = grievance,
                m_HintNoise = Normal(ref random) * 0.1f,
                m_Seed = random.NextUInt(),
            };

            // Grounds the plaintiffs can argue now; with none they still argue proportionality.
            Violations(city, definition, suit, result.Effectiveness, out float proportionality, out float expectation, out float procedure);
            SuitGrounds grounds = (proportionality > 0f ? SuitGrounds.Proportionality : 0)
                | (expectation > 0f ? SuitGrounds.LegitimateExpectation : 0)
                | (procedure > 0f ? SuitGrounds.Procedure : 0);
            if (grounds == SuitGrounds.None)
            {
                grounds = SuitGrounds.Proportionality;
            }
            suit.m_Grounds = (byte)grounds;
            int issues = math.countbits((int)grounds);

            bool injunction = TestForceInjunctionRequest || random.NextFloat() < kInjunctionRequest * (1f + math.min(1f, grievance / 2f));
            suit.m_Injunction = (byte)(injunction ? InjunctionStage.Requested : InjunctionStage.NotRequested);

            // Bottom-up workload (§7, staff-hours).
            float workload = 12f + 6f * issues
                + (companies ? 6f * math.sqrt(plaintiffs) : 8f * math.log10(plaintiffs + 1f))
                + 6f * definition.ClearancePoints
                + (injunction ? 8f : 0f);
            suit.m_Workload = workload * 1.15f;

            float durationDays = 1f + math.min(0.5f, 0.05f * math.log10(plaintiffs + 1f) + 0.05f * definition.ClearancePoints);
            suit.m_DurationFrames = (uint)(durationDays * kFramesPerDay * TestTimeScale);

            LitigationState state = EntityManager.GetComponentData<LitigationState>(city);
            int year = m_TimeSystem.GetCurrentDateTime().Year;
            if (state.m_CaseYear != year)
            {
                state.m_CaseYear = year;
                state.m_NextCase = 1001;
            }
            suit.m_Year = year;
            suit.m_CaseNumber = state.m_NextCase++;
            EntityManager.SetComponentData(city, state);

            // Damages cover the plaintiffs' losses since the policy took effect, not only the suit (§7: loss × time in force).
            suit.m_Claim = plaintiffs * LossPerDay(stake.Group) * Stake(definition, suit)
                * (againstCut ? math.max(0f, suit.m_PriorIntensity - result.Intensity) : result.Intensity) * runtime.m_AgeDays;
            suit.m_IconBuilding = m_Notifications.PickIconBuilding(suit.m_Representative);
            suit.m_ActMonth = CurrentMonth() - (int)math.floor(runtime.m_AgeDays * 12f / math.max(1, m_TimeSystem.daysPerYear));
            DynamicBuffer<Lawsuit> suits = EntityManager.GetBuffer<Lawsuit>(city);
            suits.Add(suit);
            m_Notifications.PostChirp(definition.Id, suit);
            // The "default response" setting answers at once, so no late-lawyer surcharge applies (§7).
            DefaultSuitResponse automatic = TestDefaultResponse ?? (Mod.Setting != null ? Mod.Setting.DefaultResponse : DefaultSuitResponse.AskEachTime);
            if (automatic != DefaultSuitResponse.AskEachTime)
            {
                Respond(suits.Length - 1, automatic == DefaultSuitResponse.AlwaysLawyer ? SuitResponse.Lawyer : SuitResponse.Official);
            }
            Mod.Log.Info($"Case {year}-{suit.m_CaseNumber}: {plaintiffs} {stake.Group} sue over {definition.Id}{(againstCut ? " (benefit cut)" : "")}, grievance {grievance:0.00}, " +
                $"grounds {grounds}, workload {suit.m_Workload:0.0}, {durationDays:0.00} days, injunction requested {injunction}.");
        }

        // ------------------------------------------------------------------ pending suits

        private void UpdateSuits(Entity city, uint frame, float dtDays)
        {
            DynamicBuffer<Lawsuit> suits = EntityManager.GetBuffer<Lawsuit>(city);
            float officials = 0f;
            float feeRow = 0f;
            for (int i = suits.Length - 1; i >= 0; i--)
            {
                Lawsuit suit = suits[i];
                PolicyDefinition definition = Definition(suit.m_Policy);
                if (definition == null)
                {
                    suits.RemoveAt(i);
                    continue;
                }
                uint age = frame - suit.m_FiledFrame;
                float intensity = CurrentIntensity(definition);

                // Damages claimed accumulate while the contested burden (or cut) is in force.
                float burden = suit.m_AgainstCut ? math.max(0f, suit.m_PriorIntensity - intensity) : intensity;
                if (suit.m_Injunction != (byte)InjunctionStage.InEffect)
                {
                    suit.m_Claim += suit.m_Plaintiffs * LossPerDay((TargetGroup)suit.m_Group) * burden * Stake(definition, suit) * dtDays;
                }

                if (suit.m_Injunction == (byte)InjunctionStage.Requested && age >= kInjunctionDecisionFrames * TestTimeScale)
                {
                    bool unanswered = suit.m_Response == (byte)SuitResponse.None;
                    suit.m_InjunctionProbability = InjunctionProbability(city, suit, unanswered);
                    suit.m_DecidedUnanswered = unanswered;
                    var random = new Random(suit.m_Seed ^ 0x85EBCA6Bu);
                    bool granted = random.NextFloat() < suit.m_InjunctionProbability;
                    suit.m_Injunction = (byte)(granted ? InjunctionStage.Granted : InjunctionStage.Denied);
                    Mod.Log.Info($"Case {suit.m_CaseNumber}: injunction {(granted ? "granted (takes effect in 30 minutes)" : "denied")}, p {suit.m_InjunctionProbability:0.00}{(unanswered ? " before the city answered" : "")}.");
                }
                if (suit.m_Injunction == (byte)InjunctionStage.Granted && age >= kInjunctionEffectFrames * TestTimeScale)
                {
                    suit.m_Injunction = (byte)InjunctionStage.InEffect;
                    Mod.Log.Info($"Case {suit.m_CaseNumber}: injunction in effect; {definition.Id} is suspended until the verdict.");
                }

                float remainingDays = math.max(0f, (suit.m_DurationFrames - math.min(age, suit.m_DurationFrames)) / (float)kFramesPerDay);
                if (suit.m_Response == (byte)SuitResponse.Official)
                {
                    // Workload over the remaining hours, at least one hour (§7).
                    float hours = math.max(1f, math.ceil(remainingDays * 24f / TestTimeScale));
                    officials += suit.m_Workload / hours;
                }
                else if (suit.m_Response == (byte)SuitResponse.Lawyer && suit.m_FeePaid < suit.m_LawyerFee)
                {
                    float perDay = (suit.m_LawyerFee - suit.m_FeePaid) / math.max(remainingDays, 1f / 24f);
                    feeRow += perDay;
                    suit.m_FeePaid = math.min(suit.m_LawyerFee, suit.m_FeePaid + perDay * dtDays);
                }

                if (age >= suit.m_DurationFrames)
                {
                    suits[i] = suit;
                    Verdict(city, i, definition);
                    suits = EntityManager.GetBuffer<Lawsuit>(city);
                    continue;
                }
                suits[i] = suit;
            }
            OfficialStaff = officials;
            m_Budget.SetRow(kLawyerFeeRow, false, (int)ExpenseSource.ServiceUpkeep, (int)math.round(feeRow));
        }

        // ------------------------------------------------------------------ verdict

        private void Verdict(Entity city, int index, PolicyDefinition definition)
        {
            DynamicBuffer<Lawsuit> suits = EntityManager.GetBuffer<Lawsuit>(city);
            Lawsuit suit = suits[index];
            suits.RemoveAt(index);

            SuitResponse response = (SuitResponse)suit.m_Response;
            float effectiveness = m_Engine.LastResults[System.Array.IndexOf(PolicyDefinitions.All, definition)].Effectiveness;
            Violations(city, definition, suit, effectiveness, out float proportionality, out float expectation, out float procedure);
            SuitGrounds claimed = (SuitGrounds)suit.m_Grounds;
            float lose = response == SuitResponse.None ? 1f : LoseProbability(suit, response);
            var random = new Random(suit.m_Seed ^ 0xC2B2AE35u);
            bool cityLoses = TestForcedVerdict >= 0 ? TestForcedVerdict > 0 : random.NextFloat() < lose;
            float annual = AnnualIncome();

            if (!cityLoses)
            {
                int recovered = 0;
                if (response == SuitResponse.Lawyer)
                {
                    // Loser pays, within the court-cost schedule (1억원 = 1% of annual income) and the fee actually paid.
                    recovered = (int)math.round(math.min(suit.m_LawyerFee, RecoverableFee(suit.m_Claim, annual)));
                    if (recovered > 0)
                    {
                        Receive(city, recovered);
                    }
                }
                Mod.Log.Info($"Case {suit.m_Year}-{suit.m_CaseNumber}: the city wins (lose chance {lose:0.00}); costs recovered {recovered}.");
                return;
            }

            bool defaultJudgment = response == SuitResponse.None;
            bool partial = !defaultJudgment && (TestForcedVerdict == 2 || (TestForcedVerdict < 0 && random.NextFloat() < kPartialShare));
            // Severity from the grounds the plaintiffs won on (§7 "패소하면" 2).
            float worst = 0f;
            int won = 0;
            SuitGrounds wonGrounds = SuitGrounds.None;
            if ((claimed & SuitGrounds.Proportionality) != 0 && proportionality > 0f) { worst = math.max(worst, proportionality); won++; wonGrounds |= SuitGrounds.Proportionality; }
            if ((claimed & SuitGrounds.LegitimateExpectation) != 0 && expectation > 0f) { worst = math.max(worst, expectation); won++; wonGrounds |= SuitGrounds.LegitimateExpectation; }
            if ((claimed & SuitGrounds.Procedure) != 0 && procedure > 0f) { worst = math.max(worst, procedure); won++; wonGrounds |= SuitGrounds.Procedure; }
            float severity = math.min(1f, worst + 0.15f * math.max(0, won - 1));
            float k = partial ? 0.3f + 0.5f * severity : 1f;

            // The disposition is revoked: a burden goes off, a cut benefit comes back; people's reference returns to before it.
            float restored = suit.m_AgainstCut ? suit.m_PriorIntensity : 0f;
            SetIntensity(city, definition, restored);
            DynamicBuffer<PolicyRuntime> runtimes = EntityManager.GetBuffer<PolicyRuntime>(city);
            for (int r = 0; r < runtimes.Length; r++)
            {
                if (runtimes[r].m_Policy == definition.Prefab)
                {
                    PolicyRuntime runtime = runtimes[r];
                    runtime.m_Reference = suit.m_AgainstCut ? suit.m_PriorIntensity : 0f;
                    runtimes[r] = runtime;
                }
            }

            // Ban: 2 + 3 × severity days (× k when partial); toggles are locked, sliders get conditions.
            float banDays = (2f + 3f * severity) * k;
            uint until = m_SimulationSystem.frameIndex + (uint)(banDays * kFramesPerDay * TestTimeScale);
            float limit = ProportionalLimit(definition, suit, effectiveness);
            EntityManager.GetBuffer<PolicyBan>(city).Add(new PolicyBan
            {
                m_Policy = definition.Prefab,
                m_UntilFrame = until,
                m_Grounds = (byte)(wonGrounds == SuitGrounds.None ? claimed : wonGrounds),
                m_ProportionalLimit = limit,
                m_Year = suit.m_Year,
                m_CaseNumber = suit.m_CaseNumber,
                m_Partial = partial,
            });
            if (!definition.IsSlider || suit.m_AgainstCut)
            {
                SetLocked(definition.Prefab, true);
            }

            // Damages and the plaintiffs' costs (half after a default judgment), immediate share now, the rest daily.
            float punitive = Mod.Setting != null && Mod.Setting.PunitiveDamages ? Setting.kPunitiveMultiplier : 1f;
            float damages = math.min(suit.m_Claim * k * punitive, annual * kDamagesCeiling);
            float costs = PlaintiffCosts(suit) * k * (defaultJudgment ? 0.5f : 1f);
            float total = damages + costs;
            float immediate = math.min(damages, annual * kImmediateShare);
            float paidNow = PayWithinReserve(city, immediate, annual);
            float rest = total - paidNow;
            if (rest > 0f && !Unlimited(city))
            {
                EntityManager.GetBuffer<CourtDebt>(city).Add(new CourtDebt
                {
                    m_CaseNumber = suit.m_CaseNumber,
                    m_Year = suit.m_Year,
                    m_Remaining = rest,
                    m_DailyCap = annual * (kDailyCapBase + kDailyCapSeverity * severity),
                    m_Ceiling = annual * kDamagesCeiling,
                });
            }
            Mod.Log.Info($"Case {suit.m_Year}-{suit.m_CaseNumber}: the city loses{(defaultJudgment ? " by default judgment" : partial ? " in part" : "")} " +
                $"(lose chance {lose:0.00}, violations P {proportionality:0.00} E {expectation:0.00} Pr {procedure:0.00}, severity {severity:0.00}, k {k:0.00}); " +
                $"{definition.Id} banned {banDays:0.00} days, damages {damages:0}, plaintiff costs {costs:0}, paid now {paidNow:0}.");
        }

        private float LoseProbability(Lawsuit suit, SuitResponse response)
        {
            if (response == SuitResponse.None)
            {
                return 1f;
            }
            PolicyDefinition definition = Definition(suit.m_Policy);
            int index = System.Array.IndexOf(PolicyDefinitions.All, definition);
            float effectiveness = index >= 0 ? m_Engine.LastResults[index].Effectiveness : 1f;
            Violations(m_CitySystem.City, definition, suit, effectiveness, out float proportionality, out float expectation, out float procedure);
            SuitGrounds claimed = (SuitGrounds)suit.m_Grounds;
            float sum = ((claimed & SuitGrounds.Proportionality) != 0 ? proportionality : 0f)
                + ((claimed & SuitGrounds.LegitimateExpectation) != 0 ? expectation : 0f)
                + ((claimed & SuitGrounds.Procedure) != 0 ? procedure : 0f);
            float z = kVerdictIntercept + kVerdictSlope * sum - (response == SuitResponse.Lawyer ? kLawyerBonus : 0f);
            return 1f / (1f + math.exp(-z));
        }

        /// <summary>
        /// Violations as the court measures them now (§7 "패소하면" 2): proportionality from effect against harm,
        /// legitimate expectation from the size of the contested change beyond the policy's band, procedure from legitimacy.
        /// </summary>
        private void Violations(Entity city, PolicyDefinition definition, Lawsuit suit, float effectiveness, out float proportionality, out float expectation, out float procedure)
        {
            float intensity = suit.m_AgainstCut ? suit.m_PriorIntensity - CurrentIntensity(definition) : CurrentIntensity(definition);
            proportionality = 0f;
            if (!suit.m_AgainstCut)
            {
                float limit = ProportionalLimit(definition, suit, effectiveness);
                proportionality = definition.IsSlider
                    ? (intensity > limit && limit > 0f ? (intensity - limit) / limit : 0f)
                    : math.max(0f, kProportionalityThreshold * Stake(definition, suit) / math.max(effectiveness, 1e-3f) - 1f);
            }
            // Toggles have no gradual path, so legitimate expectation is argued only against sliders (assumption).
            float change = math.abs(suit.m_ContestedIntensity - suit.m_PriorIntensity);
            expectation = definition.IsSlider ? math.max(0f, change - definition.ChangeBand) : 0f;
            float legitimacy = EntityManager.GetComponentData<PolicyEngineState>(city).m_Legitimacy;
            procedure = math.max(0f, (EngineParameters.kLegitimacyBase - legitimacy) / EngineParameters.kLegitimacyBase);
        }

        /// <summary>Highest intensity whose effect still reaches the threshold share of the harm: effect s·E, harm stake·s².</summary>
        private float ProportionalLimit(PolicyDefinition definition, Lawsuit suit, float effectiveness)
        {
            float stake = math.max(Stake(definition, suit), 1e-3f);
            return math.min(1f, effectiveness / (kProportionalityThreshold * stake));
        }

        private float InjunctionProbability(Entity city, Lawsuit suit, bool unanswered)
        {
            PolicyDefinition definition = Definition(suit.m_Policy);
            float urgency = math.min(1f, suit.m_Grievance / 2f);
            float plaintiffWin = LoseProbability(suit, SuitResponse.Official);
            float legitimacy = EntityManager.GetComponentData<PolicyEngineState>(city).m_Legitimacy;
            float publicBenefit = 0f;
            foreach (GroupStake stake in definition.Groups)
            {
                publicBenefit = math.max(publicBenefit, stake.Benefit);
            }
            float z = kInjunctionIntercept + urgency + 2f * plaintiffWin + (1f - legitimacy) - publicBenefit
                + (unanswered ? kInjunctionUnanswered : 0f);
            return 1f / (1f + math.exp(-z));
        }

        // ------------------------------------------------------------------ bans

        private void UpdateBans(Entity city, uint frame)
        {
            DynamicBuffer<PolicyBan> bans = EntityManager.GetBuffer<PolicyBan>(city);
            for (int i = bans.Length - 1; i >= 0; i--)
            {
                PolicyBan ban = bans[i];
                PolicyDefinition definition = Definition(ban.m_Policy);
                if (definition == null || frame >= ban.m_UntilFrame)
                {
                    if (definition != null)
                    {
                        SetLocked(definition.Prefab, false);
                        Mod.Log.Info($"Ban on {definition.Id} ended.");
                    }
                    bans.RemoveAt(i);
                    continue;
                }
                if (!definition.IsSlider)
                {
                    continue;
                }
                // Sliders may come back under the conditions of the grounds the city lost on (§7 "패소하면" 3).
                float allowed = 1f;
                SuitGrounds grounds = (SuitGrounds)ban.m_Grounds;
                if ((grounds & SuitGrounds.Procedure) != 0
                    && EntityManager.GetComponentData<PolicyEngineState>(city).m_Legitimacy < EngineParameters.kLegitimacyBase)
                {
                    allowed = 0f;
                }
                if ((grounds & SuitGrounds.Proportionality) != 0)
                {
                    allowed = math.min(allowed, 0.9f * ban.m_ProportionalLimit);
                }
                if ((grounds & SuitGrounds.LegitimateExpectation) != 0)
                {
                    // From zero, only steps within the band from what was last accepted.
                    allowed = math.min(allowed, ban.m_Accepted + definition.ChangeBand);
                }
                float intensity = CurrentIntensity(definition);
                if (intensity > allowed + 1e-4f)
                {
                    SetIntensity(city, definition, allowed);
                    intensity = allowed;
                }
                ban.m_Accepted = math.max(ban.m_Accepted, math.min(intensity, allowed));
                bans[i] = ban;
            }
        }

        // ------------------------------------------------------------------ money

        private void UpdateDebts(Entity city, float dtDays)
        {
            DynamicBuffer<CourtDebt> debts = EntityManager.GetBuffer<CourtDebt>(city);
            if (debts.Length == 0)
            {
                m_Budget.SetRow(kDebtRow, false, (int)ExpenseSource.ServiceUpkeep, 0);
                return;
            }
            if (Unlimited(city))
            {
                debts.Clear();
                m_Budget.SetRow(kDebtRow, false, (int)ExpenseSource.ServiceUpkeep, 0);
                return;
            }
            float annual = AnnualIncome();
            int money = EntityManager.GetComponentData<PlayerMoney>(city).money;
            // Never below a month of income (§7: negative money stops all construction).
            double room = math.max(0f, money - annual / 12f);
            double planned = 0;
            for (int i = 0; i < debts.Length; i++)
            {
                planned += math.min(debts[i].m_DailyCap, debts[i].m_Remaining + debts[i].m_Arrears) * dtDays;
            }
            double scale = planned > room && planned > 0 ? room / planned : 1;
            float rate = LoanRate(city);
            double row = 0;
            for (int i = debts.Length - 1; i >= 0; i--)
            {
                CourtDebt debt = debts[i];
                double due = math.min(debt.m_DailyCap, debt.m_Remaining + debt.m_Arrears) * dtDays;
                double paid = due * scale;
                row += paid / dtDays;
                // Arrears first, then the remaining balance; what could not be paid becomes arrears with interest.
                double fromArrears = System.Math.Min(paid, debt.m_Arrears);
                debt.m_Arrears -= fromArrears;
                double fromRemaining = System.Math.Min(paid - fromArrears, debt.m_Remaining);
                debt.m_Remaining -= fromRemaining;
                double unpaid = due - paid;
                double moved = System.Math.Min(unpaid, debt.m_Remaining);
                debt.m_Remaining -= moved;
                debt.m_Arrears += moved;
                debt.m_Arrears += debt.m_Arrears * rate / 12f * dtDays;
                double total = debt.m_Remaining + debt.m_Arrears;
                if (total > debt.m_Ceiling)
                {
                    debt.m_Arrears = System.Math.Max(0, debt.m_Ceiling - debt.m_Remaining);
                }
                if (debt.m_Remaining + debt.m_Arrears < 1)
                {
                    debts.RemoveAt(i);
                    continue;
                }
                debts[i] = debt;
            }
            m_Budget.SetRow(kDebtRow, false, (int)ExpenseSource.ServiceUpkeep, (int)System.Math.Round(row));
        }

        /// <summary>A member's loss per day at full burden: a share of the tax its group pays per member per day.</summary>
        private float LossPerDay(TargetGroup group)
        {
            float members = m_Engine.Census.TargetCount(group);
            float tax;
            switch (group)
            {
                case TargetGroup.Households:
                    tax = m_BudgetSystem.GetIncome(IncomeSource.TaxResidential);
                    break;
                case TargetGroup.IndustrialCompanies:
                    // The pollution tax is booked under industrial tax; it is not part of what companies earn from.
                    tax = m_BudgetSystem.GetIncome(IncomeSource.TaxIndustrial) - m_Budget.GetRow(BudgetInjectionSystem.kIncomeRowId);
                    break;
                case TargetGroup.CommercialCompanies:
                    tax = m_BudgetSystem.GetIncome(IncomeSource.TaxCommercial);
                    break;
                case TargetGroup.OfficeCompanies:
                    tax = m_BudgetSystem.GetIncome(IncomeSource.TaxOffice);
                    break;
                default:
                    tax = 0f;
                    break;
            }
            if (members <= 0f || tax <= 0f)
            {
                return group == TargetGroup.Households ? kHouseholdLossPerDay : kCompanyLossPerDay;
            }
            return kLossShareOfTax * tax / members;
        }

        /// <summary>What the plaintiffs lose in a year (12 days) at full burden; turns money into grievance for settlements.</summary>
        private float AnnualPlaintiffLoss(PolicyDefinition definition, Lawsuit suit) =>
            math.max(1f, suit.m_Plaintiffs * LossPerDay((TargetGroup)suit.m_Group) * math.max(Stake(definition, suit), 0.05f) * 12f);

        /// <summary>Plaintiffs' costs: their lawyer, by the same workload rule as the city's (§7).</summary>
        private float PlaintiffCosts(Lawsuit suit) => AnnualIncome() * kLawyerShareOfIncome * suit.m_Workload / kStandardWorkload;

        /// <summary>
        /// Court-cost schedule for lawyer fees (변호사보수의 소송비용 산입에 관한 규칙, 2020 개정, 별표) with 1억원 = 1% of annual income.
        /// </summary>
        public static float RecoverableFee(float claim, float annualIncome)
        {
            if (annualIncome <= 0f || claim <= 0f)
            {
                return 0f;
            }
            float unit = annualIncome * 0.01f / 1e8f;
            double won = claim / unit;
            double fee;
            if (won <= 3e6) fee = 3e5;
            else if (won <= 2e7) fee = 3e5 + (won - 3e6) * 0.10;
            else if (won <= 5e7) fee = 2e6 + (won - 2e7) * 0.08;
            else if (won <= 1e8) fee = 4.4e6 + (won - 5e7) * 0.06;
            else if (won <= 1.5e8) fee = 7.4e6 + (won - 1e8) * 0.04;
            else if (won <= 2e8) fee = 9.4e6 + (won - 1.5e8) * 0.02;
            else if (won <= 5e8) fee = 1.04e7 + (won - 2e8) * 0.01;
            else fee = 1.34e7 + (won - 5e8) * 0.005;
            return (float)(fee * unit);
        }

        private float AnnualIncome() => math.max(0, m_BudgetSystem.GetTotalIncome()) * 12f;

        private bool Unlimited(Entity city) => EntityManager.GetComponentData<PlayerMoney>(city).m_Unlimited;

        private float LoanRate(Entity city)
        {
            if (m_EconomyQuery.IsEmptyIgnoreFilter || !EntityManager.TryGetComponent(city, out Creditworthiness credit) || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<CityModifier> modifiers))
            {
                return 0.05f;
            }
            float2 range = m_EconomyQuery.GetSingleton<EconomyParameterData>().m_LoanMinMaxInterestRate;
            return Game.Tools.LoanSystem.GetTargetInterest(0, credit.m_Amount, modifiers, range);
        }

        /// <summary>Pays up to the reserve (a month of income); returns what was paid.</summary>
        private float PayWithinReserve(Entity city, float amount, float annual)
        {
            if (amount <= 0f || Unlimited(city))
            {
                return Unlimited(city) ? amount : 0f;
            }
            PlayerMoney money = EntityManager.GetComponentData<PlayerMoney>(city);
            float paid = math.min(amount, math.max(0f, money.money - annual / 12f));
            money.Subtract((int)paid);
            EntityManager.SetComponentData(city, money);
            return paid;
        }

        private void Pay(Entity city, int amount)
        {
            PlayerMoney money = EntityManager.GetComponentData<PlayerMoney>(city);
            money.Subtract(amount);
            EntityManager.SetComponentData(city, money);
        }

        private void Receive(Entity city, int amount)
        {
            PlayerMoney money = EntityManager.GetComponentData<PlayerMoney>(city);
            money.Add(amount);
            EntityManager.SetComponentData(city, money);
        }

        // ------------------------------------------------------------------ helpers

        public static PolicyDefinition Definition(Entity policy)
        {
            foreach (PolicyDefinition definition in PolicyDefinitions.All)
            {
                if (definition.Prefab == policy)
                {
                    return definition;
                }
            }
            return null;
        }

        /// <summary>Concentration of the plaintiff group's stake in the policy (burden, or benefit for a cut).</summary>
        private static float Stake(PolicyDefinition definition, Lawsuit suit)
        {
            foreach (GroupStake stake in definition.Groups)
            {
                if ((byte)stake.Group == suit.m_Group)
                {
                    return suit.m_AgainstCut ? stake.Benefit : stake.Burden;
                }
            }
            return 0f;
        }

        private float CurrentIntensity(PolicyDefinition definition) => m_Engine.LastIntensity(definition);

        private static float RemainingShare(Lawsuit suit, uint frame)
        {
            uint age = frame - suit.m_FiledFrame;
            return suit.m_DurationFrames > 0 ? 1f - math.saturate((float)age / suit.m_DurationFrames) : 0f;
        }

        private int FindBan(Entity policy)
        {
            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasBuffer<PolicyBan>(city))
            {
                return -1;
            }
            DynamicBuffer<PolicyBan> bans = EntityManager.GetBuffer<PolicyBan>(city, true);
            for (int i = 0; i < bans.Length; i++)
            {
                if (bans[i].m_Policy == policy)
                {
                    return i;
                }
            }
            return -1;
        }

        // Policies changed by a verdict, a settlement or a ban condition, with the frame; the engine does not
        // count these as the city relaxing a rule (no credibility loss).
        private readonly System.Collections.Generic.Dictionary<Entity, uint> m_CourtChanges = new System.Collections.Generic.Dictionary<Entity, uint>();

        /// <summary>True once if the policy's last change came from the court or a settlement (within a day).</summary>
        public bool ConsumeCourtChange(Entity policy)
        {
            if (!m_CourtChanges.TryGetValue(policy, out uint frame))
            {
                return false;
            }
            m_CourtChanges.Remove(policy);
            return m_SimulationSystem.frameIndex - frame < kFramesPerDay;
        }

        /// <summary>Sets a policy to an intensity 0..1 through the policy panel's event path (0 switches it off).</summary>
        private void SetIntensity(Entity city, PolicyDefinition definition, float intensity)
        {
            m_CourtChanges[definition.Prefab] = m_SimulationSystem.frameIndex;
            float adjustment = definition.IsSlider
                ? definition.SliderRange.min + intensity * (definition.SliderRange.max - definition.SliderRange.min)
                : 0f;
            if (definition.IsSlider && definition.SliderStep > 0f)
            {
                adjustment = math.floor(adjustment / definition.SliderStep + 1e-4f) * definition.SliderStep;
            }
            EntityCommandBuffer buffer = m_EndFrameBarrier.CreateCommandBuffer();
            Entity e = buffer.CreateEntity(m_ModifyArchetype);
            buffer.SetComponent(e, new Modify(city, definition.Prefab, intensity > 0f, adjustment));
        }

        private void SetLocked(Entity prefab, bool locked)
        {
            if (!EntityManager.HasComponent<Locked>(prefab))
            {
                if (!locked)
                {
                    return;
                }
                EntityManager.AddComponent<Locked>(prefab);
            }
            EntityManager.SetComponentEnabled<Locked>(prefab, locked);
        }

        private static float Normal(ref Random random)
        {
            float u1 = math.max(1e-6f, random.NextFloat());
            float u2 = random.NextFloat();
            return math.sqrt(-2f * math.log(u1)) * math.cos(2f * math.PI * u2);
        }

        [Preserve]
        public LitigationSystem()
        {
        }
    }
}
