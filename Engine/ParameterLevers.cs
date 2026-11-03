using System.Collections.Generic;
using Unity.Entities;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// A field of a vanilla parameter singleton that policies change. Parameter singletons live on prefab entities,
    /// which survive loading another city, so the original value is captured once per session and every write
    /// starts from it: the field is always original × factor, and factor 1 restores it.
    /// </summary>
    public interface IParameterLever
    {
        string Id { get; }

        /// <summary>Writes original × factor. Returns false while the singleton does not exist yet.</summary>
        bool Apply(EntityManager entityManager, float factor);

        /// <summary>Writes the original back, if it was captured.</summary>
        void Restore(EntityManager entityManager);
    }

    /// <summary>A float field of a singleton component, scaled relative to its original value.</summary>
    public sealed class ScaledFloatLever<T> : IParameterLever where T : unmanaged, IComponentData
    {
        public delegate float Getter(in T data);

        public delegate void Setter(ref T data, float value);

        private readonly Getter m_Get;
        private readonly Setter m_Set;
        private EntityQuery m_Query;
        private bool m_HasQuery;
        private bool m_Captured;
        private float m_Original;

        public string Id { get; }

        public ScaledFloatLever(string id, Getter get, Setter set)
        {
            Id = id;
            m_Get = get;
            m_Set = set;
        }

        public bool Apply(EntityManager entityManager, float factor)
        {
            if (!TryGet(entityManager, out Entity entity, out T data))
            {
                return false;
            }
            if (!m_Captured)
            {
                m_Original = m_Get(data);
                m_Captured = true;
            }
            float value = m_Original * factor;
            if (m_Get(data) != value)
            {
                m_Set(ref data, value);
                entityManager.SetComponentData(entity, data);
            }
            return true;
        }

        public void Restore(EntityManager entityManager)
        {
            if (m_Captured)
            {
                Apply(entityManager, 1f);
            }
        }

        private bool TryGet(EntityManager entityManager, out Entity entity, out T data)
        {
            if (!m_HasQuery)
            {
                m_Query = entityManager.CreateEntityQuery(ComponentType.ReadWrite<T>());
                m_HasQuery = true;
            }
            if (m_Query.CalculateEntityCount() != 1)
            {
                entity = Entity.Null;
                data = default;
                return false;
            }
            entity = m_Query.GetSingletonEntity();
            data = entityManager.GetComponentData<T>(entity);
            return true;
        }
    }

    /// <summary>Levers policy outputs can name by id. Policies that need a new field register it here.</summary>
    public static class ParameterLevers
    {
        /// <summary>Base demand for new industrial (and office) companies, before resource and tax factors.</summary>
        public const string kIndustrialBaseDemand = "Demand.IndustrialBase";

        private static readonly Dictionary<string, IParameterLever> s_Levers = new Dictionary<string, IParameterLever>();

        /// <summary>Levers the mod's own policies use.</summary>
        public static void RegisterBuiltIns()
        {
            Register(new ScaledFloatLever<Game.Prefabs.DemandParameterData>(kIndustrialBaseDemand,
                (in Game.Prefabs.DemandParameterData d) => d.m_IndustrialBaseDemand,
                (ref Game.Prefabs.DemandParameterData d, float v) => d.m_IndustrialBaseDemand = v));
        }

        public static IReadOnlyDictionary<string, IParameterLever> All => s_Levers;

        public static void Register(IParameterLever lever)
        {
            s_Levers[lever.Id] = lever;
        }

        public static bool TryGet(string id, out IParameterLever lever) => s_Levers.TryGetValue(id, out lever);

        public static void Unregister(string id)
        {
            s_Levers.Remove(id);
        }
    }
}
