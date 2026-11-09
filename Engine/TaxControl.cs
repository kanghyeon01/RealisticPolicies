using System.Reflection;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticPolicies.Engine
{
    /// <summary>How the tax sliders are held when the city has no tax staff (docs/PLAN.md §5.1).</summary>
    public enum TaxLock : byte
    {
        None,

        /// <summary>The city never had tax staff since the mod came in: rates stay as they were, but cannot change.</summary>
        FreezeCurrent,

        /// <summary>The city had tax staff and lost them: every rate goes back to the vanilla default.</summary>
        Default,
    }

    /// <summary>
    /// Narrows the vanilla tax limits (<see cref="TaxParameterData"/>), which both the tax sliders and
    /// <see cref="TaxSystem"/>'s clamping read:
    /// - the room around the 10% default grows with tax staff per resident and reaches the vanilla limits at
    ///   0.18 per thousand (local tax officials 9,051 in 2012 against about 50.95 million people; docs/PLAN.md §5.1);
    /// - residential rates (area and per education level) cannot go below a floor that falls from 0% to the vanilla
    ///   minimum as social welfare staff per resident reach 0.4 per thousand: without welfare staff no residential
    ///   subsidy (negative rate);
    /// - without tax staff the rates are held (see <see cref="TaxLock"/>).
    /// The limits live on a prefab entity that survives loading another city, so the original is captured once
    /// per session and every write starts from it.
    /// </summary>
    public sealed class TaxControl
    {
        public const int kDefaultRate = 10;
        public const int kResidentialFloor = 0;
        public const float kWelfareStaffPerResident = 0.0004f;
        public const float kTaxStaffPerResident = 0.00018f;

        /// <summary>Share of the vanilla room around the default rate the tax staff allow at the last step (0..1).</summary>
        public float LastRoom { get; private set; } = 1f;

        private static readonly FieldInfo s_CachedLimits = typeof(TaxSystem).GetField("m_TaxParameterData", BindingFlags.NonPublic | BindingFlags.Instance);

        private readonly EntityManager m_EntityManager;
        private readonly TaxSystem m_TaxSystem;
        private readonly EntityQuery m_Query;
        private bool m_Captured;
        private TaxParameterData m_Original;
        private bool m_Written;
        private TaxParameterData m_LastWritten;

        public int LastFloor { get; private set; }

        public TaxLock LastLock { get; private set; }

        public TaxParameterData Original => m_Original;

        public TaxControl(EntityManager entityManager, TaxSystem taxSystem, EntityQuery query)
        {
            m_EntityManager = entityManager;
            m_TaxSystem = taxSystem;
            m_Query = query;
        }

        /// <param name="taxStaff">Workers in tax-series offices.</param>
        /// <param name="welfareStaff">Workers in social-welfare offices.</param>
        public void Apply(ref PolicyEngineState state, int taxStaff, int welfareStaff, int population)
        {
            if (m_Query.CalculateEntityCount() != 1)
            {
                return;
            }
            Entity entity = m_Query.GetSingletonEntity();
            if (!m_Captured)
            {
                m_Original = m_EntityManager.GetComponentData<TaxParameterData>(entity);
                m_Captured = true;
            }
            if (taxStaff > 0)
            {
                state.m_TaxStaffSeen = true;
            }

            // Room around the default rate from the tax staff (2026-11-08 user decision).
            float room = population > 0 ? math.saturate((float)taxStaff / population / kTaxStaffPerResident) : 1f;
            LastRoom = room;
            TaxParameterData desired = m_Original;
            desired.m_TotalTaxLimits = Narrow(m_Original.m_TotalTaxLimits, room);
            desired.m_ResidentialTaxLimits = Narrow(m_Original.m_ResidentialTaxLimits, room);
            desired.m_CommercialTaxLimits = Narrow(m_Original.m_CommercialTaxLimits, room);
            desired.m_IndustrialTaxLimits = Narrow(m_Original.m_IndustrialTaxLimits, room);
            desired.m_OfficeTaxLimits = Narrow(m_Original.m_OfficeTaxLimits, room);
            desired.m_JobLevelTaxLimits = Narrow(m_Original.m_JobLevelTaxLimits, room);
            desired.m_ResourceTaxLimits = Narrow(m_Original.m_ResourceTaxLimits, room);

            float ratio = population > 0 ? (float)welfareStaff / population : 0f;
            int floor = (int)math.round(kResidentialFloor - (kResidentialFloor - m_Original.m_ResidentialTaxLimits.x) * math.min(1f, ratio / kWelfareStaffPerResident));
            floor = math.max(floor, m_Original.m_ResidentialTaxLimits.x);
            desired.m_ResidentialTaxLimits.x = math.min(math.max(floor, desired.m_ResidentialTaxLimits.x), desired.m_ResidentialTaxLimits.y);
            desired.m_JobLevelTaxLimits.x = math.min(math.max(floor, desired.m_JobLevelTaxLimits.x), desired.m_JobLevelTaxLimits.y);

            TaxLock taxLock = taxStaff > 0 ? TaxLock.None : state.m_TaxStaffSeen ? TaxLock.Default : TaxLock.FreezeCurrent;
            if (taxLock == TaxLock.Default)
            {
                var fixedRate = new int2(kDefaultRate, kDefaultRate);
                desired.m_TotalTaxLimits = fixedRate;
                desired.m_ResidentialTaxLimits = fixedRate;
                desired.m_CommercialTaxLimits = fixedRate;
                desired.m_IndustrialTaxLimits = fixedRate;
                desired.m_OfficeTaxLimits = fixedRate;
                desired.m_JobLevelTaxLimits = fixedRate;
                desired.m_ResourceTaxLimits = fixedRate;
            }
            else if (taxLock == TaxLock.FreezeCurrent)
            {
                int total = m_TaxSystem.TaxRate;
                desired.m_TotalTaxLimits = new int2(total, total);
                desired.m_ResidentialTaxLimits = Point(m_TaxSystem.GetTaxRate(TaxAreaType.Residential));
                desired.m_CommercialTaxLimits = Point(m_TaxSystem.GetTaxRate(TaxAreaType.Commercial));
                desired.m_IndustrialTaxLimits = Point(m_TaxSystem.GetTaxRate(TaxAreaType.Industrial));
                desired.m_OfficeTaxLimits = Point(m_TaxSystem.GetTaxRate(TaxAreaType.Office));
                // One range is shared by all education levels and by all resources, so a frozen spread stays movable inside itself.
                desired.m_JobLevelTaxLimits = m_TaxSystem.GetTaxRateRange(TaxAreaType.Residential);
                int2 resources = m_TaxSystem.GetTaxRateRange(TaxAreaType.Commercial);
                resources = Union(resources, m_TaxSystem.GetTaxRateRange(TaxAreaType.Industrial));
                desired.m_ResourceTaxLimits = Union(resources, m_TaxSystem.GetTaxRateRange(TaxAreaType.Office));
            }

            LastFloor = taxLock == TaxLock.None ? floor : desired.m_ResidentialTaxLimits.x;
            LastLock = taxLock;
            if (m_Written && desired.Equals(m_LastWritten))
            {
                return;
            }
            Write(entity, desired);

            // The new limits only clamp on the next set; move the rates that are now outside.
            m_TaxSystem.Readers.Complete();
            if (taxLock == TaxLock.Default)
            {
                // Clamps every area, education level and resource rate to the fixed range.
                m_TaxSystem.TaxRate = kDefaultRate;
            }
            else if (taxLock == TaxLock.None)
            {
                // Setting the total rate again clamps it, every area, education level and resource rate to the new limits.
                m_TaxSystem.TaxRate = m_TaxSystem.TaxRate;
                if (m_TaxSystem.GetTaxRate(TaxAreaType.Residential) < desired.m_ResidentialTaxLimits.x)
                {
                    m_TaxSystem.SetTaxRate(TaxAreaType.Residential, desired.m_ResidentialTaxLimits.x);
                }
                for (int level = 0; level < 5; level++)
                {
                    if (m_TaxSystem.GetResidentialTaxRate(level) < desired.m_JobLevelTaxLimits.x)
                    {
                        m_TaxSystem.SetResidentialTaxRate(level, desired.m_JobLevelTaxLimits.x);
                    }
                }
            }
        }

        /// <summary>Puts the original limits back (another city is loading).</summary>
        public void Restore()
        {
            if (!m_Captured || !m_Written || m_Query.CalculateEntityCount() != 1)
            {
                return;
            }
            Write(m_Query.GetSingletonEntity(), m_Original);
            m_Written = false;
        }

        private void Write(Entity entity, TaxParameterData data)
        {
            m_EntityManager.SetComponentData(entity, data);
            // TaxSystem clamps with its own copy, refreshed only on its next update; refresh it now so the rates
            // moved below follow the new limits at once.
            s_CachedLimits?.SetValue(m_TaxSystem, data);
            m_LastWritten = data;
            m_Written = true;
        }

        private static int2 Point(int value) => new int2(value, value);

        /// <summary>A vanilla limit pair pulled toward the default rate, keeping <paramref name="room"/> of its distance.</summary>
        public static int2 Narrow(int2 limits, float room)
        {
            int low = (int)math.round(kDefaultRate - (kDefaultRate - limits.x) * room);
            int high = (int)math.round(kDefaultRate + (limits.y - kDefaultRate) * room);
            return new int2(math.min(low, high), math.max(low, high));
        }

        private static int2 Union(int2 a, int2 b) => new int2(math.min(a.x, b.x), math.max(a.y, b.y));
    }
}
