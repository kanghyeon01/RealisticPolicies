using Game;
using Game.Serialization;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine.Scripting;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Saved prefab references only survive a save when a system passes them through the game's PrefabReferences:
    /// on saving it marks the prefab as referenced, on loading it maps the save's prefab entity to the live one
    /// (as PrimaryPrefabReferencesSystem does for the vanilla Policy buffer). Without this, the policy references in
    /// the mod's runtime state, suits and bans point nowhere after loading (found in the stage 8 save test).
    /// Runs in SystemUpdatePhase.PrefabReferences, right after PrimaryPrefabReferencesSystem.
    /// </summary>
    public sealed partial class ModPrefabReferencesSystem : GameSystemBase
    {
        private CheckPrefabReferencesSystem m_CheckPrefabReferencesSystem;
        private EntityQuery m_RuntimeQuery;
        private EntityQuery m_SuitQuery;
        private EntityQuery m_BanQuery;
        private EntityQuery m_ProtestQuery;
        private EntityQuery m_PressureQuery;
        private EntityQuery m_ActionQuery;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_CheckPrefabReferencesSystem = World.GetOrCreateSystemManaged<CheckPrefabReferencesSystem>();
            m_RuntimeQuery = GetEntityQuery(ComponentType.ReadWrite<PolicyRuntime>());
            m_SuitQuery = GetEntityQuery(ComponentType.ReadWrite<Lawsuit>());
            m_BanQuery = GetEntityQuery(ComponentType.ReadWrite<PolicyBan>());
            m_ProtestQuery = GetEntityQuery(ComponentType.ReadWrite<Protest>());
            m_PressureQuery = GetEntityQuery(ComponentType.ReadWrite<ProtestPressure>());
            m_ActionQuery = GetEntityQuery(ComponentType.ReadWrite<IndustrialAction>());
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_RuntimeQuery.IsEmptyIgnoreFilter && m_SuitQuery.IsEmptyIgnoreFilter && m_BanQuery.IsEmptyIgnoreFilter
                && m_ProtestQuery.IsEmptyIgnoreFilter && m_PressureQuery.IsEmptyIgnoreFilter && m_ActionQuery.IsEmptyIgnoreFilter)
            {
                return;
            }
            // Only the city entity carries these buffers, so the main thread is enough.
            PrefabReferences references = m_CheckPrefabReferencesSystem.GetPrefabReferences(this, out JobHandle dependencies);
            dependencies.Complete();
            CompleteDependency();
            using (NativeArray<Entity> entities = m_RuntimeQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    DynamicBuffer<PolicyRuntime> buffer = EntityManager.GetBuffer<PolicyRuntime>(entity);
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        PolicyRuntime value = buffer[i];
                        if (value.m_Policy != Entity.Null)
                        {
                            references.Check(ref value.m_Policy);
                            buffer[i] = value;
                        }
                    }
                }
            }
            using (NativeArray<Entity> entities = m_SuitQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    DynamicBuffer<Lawsuit> buffer = EntityManager.GetBuffer<Lawsuit>(entity);
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        Lawsuit value = buffer[i];
                        if (value.m_Policy != Entity.Null)
                        {
                            references.Check(ref value.m_Policy);
                            buffer[i] = value;
                        }
                    }
                }
            }
            using (NativeArray<Entity> entities = m_BanQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    DynamicBuffer<PolicyBan> buffer = EntityManager.GetBuffer<PolicyBan>(entity);
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        PolicyBan value = buffer[i];
                        if (value.m_Policy != Entity.Null)
                        {
                            references.Check(ref value.m_Policy);
                            buffer[i] = value;
                        }
                    }
                }
            }
            using (NativeArray<Entity> entities = m_ProtestQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    DynamicBuffer<Protest> buffer = EntityManager.GetBuffer<Protest>(entity);
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        Protest value = buffer[i];
                        if (value.m_Policy != Entity.Null)
                        {
                            references.Check(ref value.m_Policy);
                            buffer[i] = value;
                        }
                    }
                }
            }
            using (NativeArray<Entity> entities = m_PressureQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    DynamicBuffer<ProtestPressure> buffer = EntityManager.GetBuffer<ProtestPressure>(entity);
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        ProtestPressure value = buffer[i];
                        if (value.m_Policy != Entity.Null)
                        {
                            references.Check(ref value.m_Policy);
                            buffer[i] = value;
                        }
                    }
                }
            }
            using (NativeArray<Entity> entities = m_ActionQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    DynamicBuffer<IndustrialAction> buffer = EntityManager.GetBuffer<IndustrialAction>(entity);
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        IndustrialAction value = buffer[i];
                        if (value.m_Policy != Entity.Null)
                        {
                            references.Check(ref value.m_Policy);
                            buffer[i] = value;
                        }
                    }
                }
            }
            m_CheckPrefabReferencesSystem.AddPrefabReferencesUser(default);
        }

        [Preserve]
        public ModPrefabReferencesSystem()
        {
        }
    }
}
