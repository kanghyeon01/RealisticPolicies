using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using RealisticPolicies.Engine;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace RealisticPolicies
{
    /// <summary>
    /// The residents' grievance entry: a terrain colour entry (colour group 0, the ground) as the game's heatmaps; the game
    /// writes nothing for it, <see cref="ProtestGrievanceTintSystem"/> fills its channel. Empty cells stay as the map is.
    /// Built as the Realistic Aviation VFR chart's terrain tints (VfrChartTintSystem).
    /// </summary>
    internal sealed class GrievanceInfomodePrefab : GradientInfomodeBasePrefab
    {
        public override string infomodeTypeLocaleKey => "TerrainColor";

        public override int GetColorGroup(out int secondaryGroup)
        {
            secondaryGroup = -1;
            return 0;
        }

        public override void GetColors(out Color color0, out Color color1, out Color color2, out float steps, out float speed, out float tiling, out float fill)
        {
            base.GetColors(out color0, out color1, out color2, out steps, out speed, out tiling, out fill);
            color0 = new Color(color0.r, color0.g, color0.b, 0f);
        }
    }

    /// <summary>
    /// The "Protests" infoview (docs/PLAN.md §12 "시위 정보 보기", docs/previews/protest-infoview.html): a button in the game's
    /// infoview menu. Legend: residents' grievance (purple, painted into the ground, shown with or without protests), protest
    /// roads (orange), riot roads (red), police-held roads (light blue) as the game colours roads in its infoviews, and the
    /// control lines (white bars, drawn). Each entry can be turned off in the legend.
    /// </summary>
    public static class ProtestInfoview
    {
        public const string kInfoviewName = "RealisticPolicies.Protests";
        public const string kGrievanceName = "RealisticPolicies.Protests.Grievance";
        public const string kProtestName = "RealisticPolicies.Protests.Protest";
        public const string kRiotName = "RealisticPolicies.Protests.Riot";
        public const string kPoliceName = "RealisticPolicies.Protests.Police";
        public const string kLinesName = "RealisticPolicies.Protests.ControlLines";

        internal static readonly Color kGrievanceLow = new Color(0.67f, 0.55f, 1f);
        internal static readonly Color kGrievanceMid = new Color(0.67f, 0.55f, 1f);
        internal static readonly Color kGrievanceHigh = new Color(0.416f, 0.184f, 0.816f);
        internal static readonly Color kProtest = new Color(1f, 0.643f, 0.176f);
        internal static readonly Color kRiot = new Color(0.914f, 0.373f, 0.29f);
        internal static readonly Color kPolice = new Color(0.557f, 0.773f, 0.925f);
        internal static readonly Color kLine = Color.white;

        private static InfoviewPrefab s_Infoview;
        internal static InfomodePrefab Grievance;
        internal static InfomodePrefab Protest;
        internal static InfomodePrefab Riot;
        internal static InfomodePrefab Police;
        internal static InfomodePrefab Lines;

        internal static void Create(PrefabSystem prefabSystem)
        {
            GrievanceInfomodePrefab grievance = ScriptableObject.CreateInstance<GrievanceInfomodePrefab>();
            grievance.name = kGrievanceName;
            grievance.m_IncludeInGroups = new InfomodeGroupPrefab[0];
            grievance.m_Low = kGrievanceLow;
            grievance.m_Medium = kGrievanceMid;
            grievance.m_High = kGrievanceHigh;
            grievance.m_Steps = 11;
            grievance.m_LegendType = GradientLegendType.Gradient;
            prefabSystem.AddPrefab(grievance);
            Grievance = grievance;
            Protest = AddColor(prefabSystem, kProtestName, kProtest);
            Riot = AddColor(prefabSystem, kRiotName, kRiot);
            Police = AddColor(prefabSystem, kPoliceName, kPolice);
            Lines = AddColor(prefabSystem, kLinesName, kLine);

            InfoviewPrefab infoview = ScriptableObject.CreateInstance<InfoviewPrefab>();
            infoview.name = kInfoviewName;
            infoview.m_IconPath = "Media/Game/Icons/Police.svg";
            infoview.m_Group = 100;
            infoview.m_Priority = 3;
            infoview.m_WarningCategories = new IconCategory[0];
            infoview.m_Infomodes = new[]
            {
                new InfomodeInfo { m_Mode = Grievance, m_Priority = 1 },
                new InfomodeInfo { m_Mode = Protest, m_Priority = 2 },
                new InfomodeInfo { m_Mode = Riot, m_Priority = 3 },
                new InfomodeInfo { m_Mode = Police, m_Priority = 4 },
                new InfomodeInfo { m_Mode = Lines, m_Priority = 5 },
            };
            prefabSystem.AddPrefab(infoview);
            s_Infoview = infoview;
        }

        /// <summary>A colour entry the game colours nothing for (building type none): a swatch and a checkbox in the legend.</summary>
        private static BuildingInfomodePrefab AddColor(PrefabSystem prefabSystem, string name, Color color)
        {
            BuildingInfomodePrefab infomode = ScriptableObject.CreateInstance<BuildingInfomodePrefab>();
            infomode.name = name;
            infomode.m_Type = BuildingType.None;
            infomode.m_Color = color;
            infomode.m_IncludeInGroups = new InfomodeGroupPrefab[0];
            prefabSystem.AddPrefab(infomode);
            return infomode;
        }

        internal static InfoviewPrefab Prefab => s_Infoview;

        internal static bool IsActive(ToolSystem toolSystem) => s_Infoview != null && toolSystem.activeInfoview == s_Infoview;

        /// <summary>The colour index of a ticked legend entry, or -1.</summary>
        internal static int Index(EntityManager entityManager, PrefabSystem prefabSystem, InfomodePrefab infomode)
        {
            Entity entity = infomode != null ? prefabSystem.GetEntity(infomode) : Entity.Null;
            return entity != Entity.Null && entityManager.TryGetComponent(entity, out InfomodeActive active) ? active.m_Index : -1;
        }
    }

    /// <summary>
    /// Colours the protest roads in the protest infoview right after the game's own road colouring (NetColorSystem, every
    /// frame while an infoview is open), and draws the control lines (a white bar across the road at each closed end).
    /// </summary>
    public partial class ProtestRoadColorSystem : GameSystemBase
    {
        private ToolSystem m_ToolSystem;
        private PrefabSystem m_PrefabSystem;
        private ProtestSystem m_Protests;
        private OverlayRenderSystem m_OverlayRenderSystem;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Protests = World.GetOrCreateSystemManaged<ProtestSystem>();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!ProtestInfoview.IsActive(m_ToolSystem))
            {
                return;
            }
            int protest = ProtestInfoview.Index(EntityManager, m_PrefabSystem, ProtestInfoview.Protest);
            int riot = ProtestInfoview.Index(EntityManager, m_PrefabSystem, ProtestInfoview.Riot);
            int police = ProtestInfoview.Index(EntityManager, m_PrefabSystem, ProtestInfoview.Police);
            if (protest >= 0)
            {
                PaintEdges(m_Protests.LastProtestEdges, protest);
            }
            if (riot >= 0)
            {
                PaintEdges(m_Protests.LastRiotEdges, riot);
            }
            if (police >= 0)
            {
                PaintEdges(m_Protests.LastPoliceEdges.Keys, police);
            }
            // Nodes on a protest or riot road (two of its segments meet there) and closed nodes take the road's colour.
            PaintNodes(protest, riot, police);
            if (ProtestInfoview.Index(EntityManager, m_PrefabSystem, ProtestInfoview.Lines) >= 0)
            {
                DrawLines();
            }
        }

        private void PaintEdges(IEnumerable<Entity> edges, int index)
        {
            foreach (Entity edge in edges)
            {
                if (EntityManager.HasComponent<EdgeColor>(edge))
                {
                    EntityManager.SetComponentData(edge, new EdgeColor((byte)index, 0, 0));
                }
            }
        }

        private void PaintNodes(int protest, int riot, int police)
        {
            var count = new Dictionary<Entity, (int protest, int riot)>();
            void Add(IEnumerable<Entity> edges, bool isRiot)
            {
                foreach (Entity edge in edges)
                {
                    if (!EntityManager.TryGetComponent(edge, out Edge data))
                    {
                        continue;
                    }
                    foreach (Entity node in new[] { data.m_Start, data.m_End })
                    {
                        count.TryGetValue(node, out (int protest, int riot) c);
                        count[node] = isRiot ? (c.protest, c.riot + 1) : (c.protest + 1, c.riot);
                    }
                }
            }
            Add(m_Protests.LastProtestEdges, false);
            Add(m_Protests.LastRiotEdges, true);
            foreach (KeyValuePair<Entity, (int protest, int riot)> entry in count)
            {
                int index = -1;
                if (entry.Value.riot >= 2 && riot >= 0)
                {
                    index = riot;
                }
                else if (entry.Value.protest + entry.Value.riot >= 2 && protest >= 0)
                {
                    index = protest;
                }
                if (index >= 0 && EntityManager.HasComponent<NodeColor>(entry.Key))
                {
                    EntityManager.SetComponentData(entry.Key, new NodeColor((byte)index, 0));
                }
            }
            // Closed nodes under repression (police side) in the riot colour.
            if (riot >= 0)
            {
                foreach (Entity node in m_Protests.LastClosedNodes)
                {
                    if (EntityManager.HasComponent<NodeColor>(node))
                    {
                        EntityManager.SetComponentData(node, new NodeColor((byte)riot, 0));
                    }
                }
            }
        }

        /// <summary>
        /// Control lines: where a closed segment meets an open node (no police), at the far end of each police segment, and on
        /// the other open arms of a closed end node.
        /// </summary>
        private void DrawLines()
        {
            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();
            HashSet<Entity> closed = m_Protests.LastClosedEdges;
            HashSet<Entity> nodes = m_Protests.LastClosedNodes;
            Dictionary<Entity, Entity> police = m_Protests.LastPoliceEdges;
            foreach (Entity edge in closed)
            {
                if (!EntityManager.TryGetComponent(edge, out Edge data))
                {
                    continue;
                }
                if (!nodes.Contains(data.m_Start))
                {
                    Bar(buffer, edge, true);
                }
                if (!nodes.Contains(data.m_End))
                {
                    Bar(buffer, edge, false);
                }
            }
            foreach (KeyValuePair<Entity, Entity> entry in police)
            {
                if (EntityManager.TryGetComponent(entry.Key, out Edge data))
                {
                    // The far end, away from the riot side.
                    Bar(buffer, entry.Key, data.m_Start != entry.Value);
                }
            }
            foreach (Entity node in nodes)
            {
                if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<ConnectedEdge> connected))
                {
                    continue;
                }
                for (int i = 0; i < connected.Length; i++)
                {
                    Entity arm = connected[i].m_Edge;
                    if (closed.Contains(arm) || police.ContainsKey(arm) || !EntityManager.TryGetComponent(arm, out Edge data))
                    {
                        continue;
                    }
                    Bar(buffer, arm, data.m_Start == node);
                }
            }
            m_OverlayRenderSystem.AddBufferWriter(default);
        }

        /// <summary>A white bar with a red rim across the road, a few metres in from one end of the segment.</summary>
        private void Bar(OverlayRenderSystem.Buffer buffer, Entity edge, bool atStart)
        {
            if (!EntityManager.TryGetComponent(edge, out Curve curve) || curve.m_Length <= 0f)
            {
                return;
            }
            float width = 8f;
            if (EntityManager.TryGetComponent(edge, out Composition composition) && EntityManager.TryGetComponent(composition.m_Edge, out NetCompositionData net))
            {
                width = net.m_Width;
            }
            float t = math.saturate(4f / curve.m_Length);
            t = atStart ? t : 1f - t;
            float3 at = MathUtils.Position(curve.m_Bezier, t);
            float3 tangent = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, t));
            float3 across = new float3(-tangent.z, 0f, tangent.x) * (width * 0.5f);
            buffer.DrawLine(ProtestInfoview.kRiot, ProtestInfoview.kLine, 0.4f, OverlayRenderSystem.StyleFlags.Projected,
                new Line3.Segment(at - across, at + across), 2f, new float2(1f, 1f));
        }

        [Preserve]
        public ProtestRoadColorSystem()
        {
        }
    }

    /// <summary>
    /// Paints the residents' grievance into the ground while the protest infoview is open, as the game paints its heatmaps:
    /// an overlay texture over the playable area, the grievance entry's channel (its colour index − 1). 32 m cells, each the
    /// grievance of the households living in it (the city's grievance × each household's weight, the one protests start
    /// from), smoothed; written again every 4096 frames.
    /// </summary>
    public partial class ProtestGrievanceTintSystem : GameSystemBase
    {
        private const float kHalfExtent = 7168f;
        private const float kCellSize = 32f;
        private const int kCells = (int)(2f * kHalfExtent / kCellSize);

        private ToolSystem m_ToolSystem;
        private PrefabSystem m_PrefabSystem;
        private TerrainRenderSystem m_TerrainRenderSystem;
        private SimulationSystem m_SimulationSystem;
        private ProtestSystem m_Protests;
        private EntityQuery m_HouseholdQuery;
        private Texture2D m_Texture;
        private float[] m_Sum;
        private int[] m_Count;
        private byte[] m_Pixels;
        private uint m_LastFrame;
        private int m_LastChannel = -2;
        private bool m_Written;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_TerrainRenderSystem = World.GetOrCreateSystemManaged<TerrainRenderSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Protests = World.GetOrCreateSystemManaged<ProtestSystem>();
            m_HouseholdQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<HouseholdCitizen>(), ComponentType.ReadOnly<PropertyRenter>(), ComponentType.ReadOnly<Game.Economy.Resources>() },
                None = new[] { ComponentType.ReadOnly<TouristHousehold>(), ComponentType.ReadOnly<CommuterHousehold>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_Texture = new Texture2D(kCells, kCells, TextureFormat.RGBA32, false, true)
            {
                name = "RealisticPoliciesGrievance",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        [Preserve]
        protected override void OnDestroy()
        {
            Object.Destroy(m_Texture);
            base.OnDestroy();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!ProtestInfoview.IsActive(m_ToolSystem))
            {
                return;
            }
            int index = ProtestInfoview.Index(EntityManager, m_PrefabSystem, ProtestInfoview.Grievance);
            int channel = index - 1;
            if (channel < 0 || channel > 3)
            {
                return;
            }
            uint frame = m_SimulationSystem.frameIndex;
            if (!m_Written || channel != m_LastChannel || frame - m_LastFrame >= 4096u || frame < m_LastFrame)
            {
                Write(channel);
                m_LastChannel = channel;
                m_LastFrame = frame;
                m_Written = true;
            }
            m_TerrainRenderSystem.overrideOverlaymap = m_Texture;
        }

        private void Write(int channel)
        {
            int cells = kCells * kCells;
            if (m_Sum == null)
            {
                m_Sum = new float[cells];
                m_Count = new int[cells];
                m_Pixels = new byte[cells * 4];
            }
            System.Array.Clear(m_Sum, 0, cells);
            System.Array.Clear(m_Count, 0, cells);
            System.Array.Clear(m_Pixels, 0, m_Pixels.Length);
            float level = m_Protests.LastGrievanceLevel;
            if (level > 0f)
            {
                int poor = ServiceSeriesCensus.PoorWealthLimit(EntityManager);
                int comfortable = poor * 3;
                using (EntityQuery happiness = EntityManager.CreateEntityQuery(ComponentType.ReadOnly<CitizenHappinessParameterData>()))
                {
                    if (!happiness.IsEmptyIgnoreFilter)
                    {
                        comfortable = happiness.GetSingleton<CitizenHappinessParameterData>().m_WealthyMoneyAmount.z;
                    }
                }
                using (NativeArray<Entity> households = m_HouseholdQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int i = 0; i < households.Length; i++)
                    {
                        Entity property = EntityManager.GetComponentData<PropertyRenter>(households[i]).m_Property;
                        if (!EntityManager.TryGetComponent(property, out Game.Objects.Transform transform))
                        {
                            continue;
                        }
                        int2 cell = (int2)math.floor((transform.m_Position.xz + kHalfExtent) / kCellSize);
                        if (math.any(cell < 0) || math.any(cell >= kCells))
                        {
                            continue;
                        }
                        int c = cell.x + cell.y * kCells;
                        m_Sum[c] += m_Protests.HouseholdWeight(households[i], poor, comfortable);
                        m_Count[c]++;
                    }
                }
                for (int c = 0; c < cells; c++)
                {
                    if (m_Count[c] > 0)
                    {
                        // Mean weight × the city's grievance; 2 is the top of the scale.
                        m_Pixels[c * 4 + channel] = (byte)math.round(math.saturate(level * m_Sum[c] / m_Count[c] / 2f) * 255f);
                    }
                }
                Smooth(channel);
            }
            m_Texture.SetPixelData(m_Pixels, 0);
            m_Texture.Apply();
        }

        /// <summary>Fills empty cells next to lived-in ones with the neighbours' mean, so blocks read as areas, not dots.</summary>
        private void Smooth(int channel)
        {
            var copy = new byte[kCells * kCells];
            for (int c = 0; c < copy.Length; c++)
            {
                copy[c] = m_Pixels[c * 4 + channel];
            }
            for (int z = 1; z < kCells - 1; z++)
            {
                for (int x = 1; x < kCells - 1; x++)
                {
                    int c = x + z * kCells;
                    if (copy[c] != 0)
                    {
                        continue;
                    }
                    int sum = 0, n = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            byte v = copy[c + dx + dz * kCells];
                            if (v != 0)
                            {
                                sum += v;
                                n++;
                            }
                        }
                    }
                    if (n > 0)
                    {
                        m_Pixels[c * 4 + channel] = (byte)(sum / n);
                    }
                }
            }
        }

        [Preserve]
        public ProtestGrievanceTintSystem()
        {
        }
    }
}
