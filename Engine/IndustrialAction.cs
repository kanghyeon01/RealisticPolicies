using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game.City;
using Game.Common;
using Game.Policies;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticPolicies.Engine
{
    /// <summary>Strike (workers over their pay or jobs) or collective closure (companies over a burden on their sector).</summary>
    public enum ActionKind : byte
    {
        Strike,
        Closure,
    }

    /// <summary>Who stops: industry and offices (city-wide efficiency), public servants (policy work), shops (each shop's efficiency).</summary>
    public enum ActionSector : byte
    {
        Industrial,
        Office,
        Public,
        Commercial,
    }

    /// <summary>One strike or collective closure, saved with the city (docs/PLAN.md §12 "파업").</summary>
    [InternalBufferCapacity(0)]
    public struct IndustrialAction : IBufferElementData, ISerializable
    {
        private const byte kVersion = 1;

        public int m_Id;
        public Entity m_Policy;
        public byte m_Kind;
        public byte m_Sector;

        /// <summary>Share of the sector taking part, 0..1.</summary>
        public float m_Share;
        public float m_AgeHours;

        /// <summary>Policy level they want back (what they were used to) and the level when it began.</summary>
        public float m_DemandIntensity;
        public float m_StartIntensity;
        public uint m_StartFrame;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Id);
            writer.Write(m_Policy);
            writer.Write(m_Kind);
            writer.Write(m_Sector);
            writer.Write(m_Share);
            writer.Write(m_AgeHours);
            writer.Write(m_DemandIntensity);
            writer.Write(m_StartIntensity);
            writer.Write(m_StartFrame);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Id);
            reader.Read(out m_Policy);
            reader.Read(out m_Kind);
            reader.Read(out m_Sector);
            reader.Read(out m_Share);
            reader.Read(out m_AgeHours);
            reader.Read(out m_DemandIntensity);
            reader.Read(out m_StartIntensity);
            reader.Read(out m_StartFrame);
        }
    }

    /// <summary>
    /// Strikes and collective closures (2026-11-09 user decisions, option 가): a strike when a policy takes from workers
    /// (minimum wage cut, civil service pay cut, more contracting out), a collective closure when a policy's burden falls
    /// on one sector (industrial, office or commercial stake 0.6 or more, the policy raised). People keep going to work; only
    /// output falls, by the share taking part × 0.2 (a sector loses little, Neumann &amp; Reder 1984). A minimum wage cut
    /// brings out industrial and shop workers alike. Like protests, the change
    /// against what people were used to (the same 12-day reference) drives the start, at 1.4 × (e^(0.21 P) − 1) a year with
    /// P = 10 × change; the share moves toward the change (at most half) and fades 4% an hour, and it ends when the change
    /// is given back or the share drops under 2%. Industrial and office losses go through a policy kept out of the policy
    /// list, whose city modifiers the game applies as for any policy; public servants on strike leave less staff for policies;
    /// shops have no city-wide efficiency, so <see cref="BuildingEffectSystem"/> lowers each shop's own efficiency.
    /// </summary>
    public sealed partial class ProtestSystem
    {
        public const string kActionPolicyName = "RealisticPolicies.IndustrialAction";
        public static Entity ActionPolicy = Entity.Null;

        private const float kActionLossPerShare = 0.2f;
        private const float kActionMaxShare = 0.5f;
        private const float kActionStartShare = 0.05f;
        private const float kActionMinChange = 0.05f;
        private const float kActionEndChange = 0.01f;
        private const float kActionPullPerHour = 0.2f;
        private const float kActionDecayPerHour = 0.04f;
        private const float kActionClosureStake = 0.6f;

        /// <summary>Share of public servants' policy work lost to strikes (the engine scales its staff by 1 − this).</summary>
        public static float PublicStaffLoss { get; private set; }

        /// <summary>Share of shops' output lost to strikes and closures (BuildingEffectSystem scales each shop by 1 − this).</summary>
        public static float CommercialLoss { get; private set; }

        /// <summary>Industrial and office efficiency lost to strikes and closures at the last update (city modifiers, negative).</summary>
        public float LastIndustrialLoss { get; private set; }

        public float LastOfficeLoss { get; private set; }

        /// <summary>Self-test: actions to start at the next update, and a change to hold instead of the measured one.</summary>
        internal static readonly List<(Entity policy, ActionKind kind, ActionSector sector)> TestStartActions = new List<(Entity, ActionKind, ActionSector)>();
        internal static float TestHeldChange = -1f;

        private readonly struct ActionCause
        {
            public readonly PolicyDefinition Definition;
            public readonly ActionKind Kind;
            public readonly ActionSector Sector;
            public readonly bool Up;
            public readonly float Stake;

            public ActionCause(PolicyDefinition definition, ActionKind kind, ActionSector sector, bool up, float stake)
            {
                Definition = definition;
                Kind = kind;
                Sector = sector;
                Up = up;
                Stake = stake;
            }
        }

        private List<ActionCause> m_Causes;

        /// <summary>The policies that can bring workers or a sector out, and which way the policy has to move.</summary>
        private List<ActionCause> Causes()
        {
            if (m_Causes != null)
            {
                return m_Causes;
            }
            m_Causes = new List<ActionCause>();
            foreach (PolicyDefinition definition in PolicyDefinitions.All)
            {
                switch (definition.Id)
                {
                    case PolicyCatalog.kMinimumWage:
                        m_Causes.Add(new ActionCause(definition, ActionKind.Strike, ActionSector.Industrial, false, 1f));
                        m_Causes.Add(new ActionCause(definition, ActionKind.Strike, ActionSector.Commercial, false, 1f));
                        break;
                    case PolicyCatalog.kCivilServicePay:
                        m_Causes.Add(new ActionCause(definition, ActionKind.Strike, ActionSector.Public, false, 1f));
                        break;
                    case PolicyCatalog.kContractingOut:
                        m_Causes.Add(new ActionCause(definition, ActionKind.Strike, ActionSector.Public, true, 1f));
                        break;
                }
                if (definition.Groups == null)
                {
                    continue;
                }
                foreach (GroupStake stake in definition.Groups)
                {
                    if (stake.Burden < kActionClosureStake)
                    {
                        continue;
                    }
                    if (stake.Group == TargetGroup.IndustrialCompanies)
                    {
                        m_Causes.Add(new ActionCause(definition, ActionKind.Closure, ActionSector.Industrial, true, stake.Burden));
                    }
                    else if (stake.Group == TargetGroup.OfficeCompanies)
                    {
                        m_Causes.Add(new ActionCause(definition, ActionKind.Closure, ActionSector.Office, true, stake.Burden));
                    }
                    else if (stake.Group == TargetGroup.CommercialCompanies)
                    {
                        m_Causes.Add(new ActionCause(definition, ActionKind.Closure, ActionSector.Commercial, true, stake.Burden));
                    }
                }
            }
            return m_Causes;
        }

        /// <summary>The move against the workers or the sector since what they were used to, times the stake (0 when none).</summary>
        private float ActionChange(DynamicBuffer<ProtestPressure> pressures, PolicyDefinition definition, bool up, float stake, out float now, out float reference)
        {
            now = m_Engine.LastIntensity(definition);
            int p = FindPressure(pressures, definition.Prefab);
            reference = p >= 0 ? pressures[p].m_IntensityRef : now;
            if (TestHeldChange >= 0f)
            {
                return TestHeldChange;
            }
            return math.max(0f, up ? now - reference : reference - now) * stake;
        }

        private void UpdateIndustrialActions(Entity city, uint frame, float dtDays, float dtHours)
        {
            DynamicBuffer<ProtestPressure> pressures = EntityManager.GetBuffer<ProtestPressure>(city, true);
            List<ActionCause> causes = Causes();
            var changes = new Dictionary<(Entity, ActionKind, ActionSector), float>();
            var references = new Dictionary<(Entity, ActionKind, ActionSector), (float now, float reference)>();
            foreach (ActionCause cause in causes)
            {
                if (cause.Definition.Prefab == Entity.Null)
                {
                    continue;
                }
                var key = (cause.Definition.Prefab, cause.Kind, cause.Sector);
                changes[key] = ActionChange(pressures, cause.Definition, cause.Up, cause.Stake, out float now, out float reference);
                references[key] = (now, reference);
            }

            // Ongoing ones move toward the change and end when it is given back or few are left.
            DynamicBuffer<IndustrialAction> actions = EntityManager.GetBuffer<IndustrialAction>(city);
            for (int i = actions.Length - 1; i >= 0; i--)
            {
                IndustrialAction action = actions[i];
                var key = (action.m_Policy, (ActionKind)action.m_Kind, (ActionSector)action.m_Sector);
                float change = changes.TryGetValue(key, out float c) ? c : 0f;
                action.m_AgeHours += dtHours;
                float target = math.min(kActionMaxShare, change);
                action.m_Share = math.saturate(action.m_Share + dtHours * (kActionPullPerHour * (target - action.m_Share) - kActionDecayPerHour * action.m_Share));
                string why = change < kActionEndChange ? "the demand was met" : action.m_Share < kDissolveSize && action.m_AgeHours > 1f ? "too few are left" : null;
                if (why != null)
                {
                    Mod.Log.Info($"{(ActionKind)action.m_Kind} {action.m_Id} ({(ActionSector)action.m_Sector}) ends: {why} (after {action.m_AgeHours:0.0} h).");
                    actions.RemoveAt(i);
                    continue;
                }
                actions[i] = action;
            }

            // New ones: forced by the self-test, or by chance as for protests.
            var random = Unity.Mathematics.Random.CreateFromIndex(frame ^ 0x5717A1u);
            foreach (ActionCause cause in causes)
            {
                var key = (cause.Definition.Prefab, cause.Kind, cause.Sector);
                if (cause.Definition.Prefab == Entity.Null || HasAction(actions, key))
                {
                    continue;
                }
                bool forced = TestStartActions.Contains(key);
                float change = changes[key];
                if (!forced)
                {
                    // The self-test holds one change for every policy: only its forced ones start then.
                    if (change < kActionMinChange || TestHeldChange >= 0f)
                    {
                        continue;
                    }
                    float yearly = kBaseRate * (math.exp(kRateSlope * 10f * change) - 1f);
                    if (random.NextFloat() >= 1f - math.exp(-yearly * dtDays / 12f))
                    {
                        continue;
                    }
                }
                ProtestCityState state = EntityManager.GetComponentData<ProtestCityState>(city);
                int id = state.m_NextId++;
                EntityManager.SetComponentData(city, state);
                actions = EntityManager.GetBuffer<IndustrialAction>(city);
                actions.Add(new IndustrialAction
                {
                    m_Id = id,
                    m_Policy = cause.Definition.Prefab,
                    m_Kind = (byte)cause.Kind,
                    m_Sector = (byte)cause.Sector,
                    m_Share = kActionStartShare,
                    m_DemandIntensity = references[key].reference,
                    m_StartIntensity = references[key].now,
                    m_StartFrame = frame,
                });
                Mod.Log.Info($"{cause.Kind} {id} starts in {cause.Sector} over {cause.Definition.Id}: change {change:0.00}{(forced ? " (self-test)" : "")}.");
            }
            TestStartActions.Clear();
            ApplyActionEffects(city);
        }

        private static bool HasAction(DynamicBuffer<IndustrialAction> actions, (Entity policy, ActionKind kind, ActionSector sector) key)
        {
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i].m_Policy == key.policy && actions[i].m_Kind == (byte)key.kind && actions[i].m_Sector == (byte)key.sector)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Efficiency lost per sector: industry and offices through the hidden policy, public servants through the engine, shops building by building.</summary>
        private void ApplyActionEffects(Entity city)
        {
            float industrial = 0f, office = 0f, publicLoss = 0f, commercial = 0f;
            DynamicBuffer<IndustrialAction> actions = EntityManager.GetBuffer<IndustrialAction>(city, true);
            for (int i = 0; i < actions.Length; i++)
            {
                float loss = actions[i].m_Share * kActionLossPerShare;
                switch ((ActionSector)actions[i].m_Sector)
                {
                    case ActionSector.Industrial: industrial += loss; break;
                    case ActionSector.Office: office += loss; break;
                    case ActionSector.Commercial: commercial += loss; break;
                    default: publicLoss += loss; break;
                }
            }
            LastIndustrialLoss = math.min(kActionMaxShare, industrial);
            LastOfficeLoss = math.min(kActionMaxShare, office);
            PublicStaffLoss = math.min(kActionMaxShare, publicLoss);
            CommercialLoss = math.min(kActionMaxShare, commercial);
            SetActionModifiers(city, -LastIndustrialLoss, -LastOfficeLoss, LastIndustrialLoss > 0f || LastOfficeLoss > 0f);
        }

        /// <summary>Writes the hidden policy's modifier values and switches it on (the policy panel's way) the first time it is needed.</summary>
        private void SetActionModifiers(Entity city, float industrial, float office, bool needed)
        {
            if (ActionPolicy == Entity.Null || !EntityManager.HasBuffer<CityModifierData>(ActionPolicy))
            {
                return;
            }
            DynamicBuffer<CityModifierData> modifiers = EntityManager.GetBuffer<CityModifierData>(ActionPolicy);
            for (int m = 0; m < modifiers.Length; m++)
            {
                CityModifierData data = modifiers[m];
                float value = data.m_Type == CityModifierType.IndustrialEfficiency ? industrial : data.m_Type == CityModifierType.OfficeEfficiency ? office : 0f;
                if (math.abs(data.m_Range.min - value) > 1e-5f || math.abs(data.m_Range.max - value) > 1e-5f)
                {
                    data.m_Range = new Colossal.Mathematics.Bounds1(value, value);
                    modifiers[m] = data;
                }
            }
            bool active = IsActionPolicyActive(city);
            if (needed != active)
            {
                EntityCommandBuffer buffer = m_EndFrameBarrier.CreateCommandBuffer();
                Entity e = buffer.CreateEntity(m_ModifyArchetype);
                buffer.SetComponent(e, new Modify(city, ActionPolicy, needed, 0f));
            }
        }

        internal bool IsActionPolicyActive(Entity city)
        {
            if (ActionPolicy == Entity.Null || !EntityManager.TryGetBuffer(city, true, out DynamicBuffer<Policy> policies))
            {
                return false;
            }
            for (int i = 0; i < policies.Length; i++)
            {
                if (policies[i].m_Policy == ActionPolicy)
                {
                    return (policies[i].m_Flags & PolicyFlags.Active) != 0;
                }
            }
            return false;
        }

        /// <summary>Before the mod is taken out: every strike and closure ends and the hidden policy goes off.</summary>
        private void EndIndustrialActions(Entity city)
        {
            if (EntityManager.HasBuffer<IndustrialAction>(city))
            {
                EntityManager.GetBuffer<IndustrialAction>(city).Clear();
                ApplyActionEffects(city);
            }
        }
    }
}
