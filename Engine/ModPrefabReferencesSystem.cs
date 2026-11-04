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
    public sealed class ModPrefabReferencesSystem : GameSystemBase
    {
        private CheckPrefabReferencesSystem m_CheckPrefabReferencesSystem;
        private EntityQuery m_RuntimeQuery;
        private EntityQuery m_SuitQuery;
        private EntityQuery m_BanQuery;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_CheckPrefabReferencesSystem = World.GetOrCreateSystemManaged<CheckPrefabReferencesSystem>();
            m_RuntimeQuery = GetEntityQuery(ComponentType.ReadWrite<PolicyRuntime>());
            m_SuitQuery = GetEntityQuery(ComponentType.ReadWrite<Lawsuit>());
            m_BanQuery = GetEntityQuery(ComponentType.ReadWrite<PolicyBan>());
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_RuntimeQuery.IsEmptyIgnoreFilter && m_SuitQuery.IsEmptyIgnoreFilter && m_BanQuery.IsEmptyIgnoreFilter)
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
            m_CheckPrefabReferencesSystem.AddPrefabReferencesUser(default);
        }

        [Preserve]
        public ModPrefabReferencesSystem()
        {
        }
    }
}
