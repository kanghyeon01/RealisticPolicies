using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Events;
using Game.Net;
using Edge = Game.Net.Edge;
using SubLane = Game.Net.SubLane;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticPolicies.Engine
{
    /// <summary>
    /// Road closures and police posts of the protests (docs/PLAN.md §12 "도로 폐쇄", "진압은 경찰차 출동으로"):
    /// - a riot or a protest of 1,000+ people closes its segments: car lanes become public-transport-only
    ///   (<see cref="Game.Net.CarLaneFlags.PublicOnly"/>), which the pathfinder reads as forbidden private traffic; police,
    ///   fire, ambulance and garbage trucks still enter. Nodes are left open unless every arm is closed;
    /// - under repression the police stand beyond the end nodes, between nodes, on the way in, starting at the riot side;
    ///   those segments are closed in the entering direction only, and the end nodes are closed too;
    /// - each police car is a post: an accident site whose event targets one point on the road, with a stand-in "involved"
    ///   entity so the game keeps the site and keeps asking for the police; one car per post, sent one after another;
    /// - parked police cars are taken off other calls while they stand there.
    /// </summary>
    public sealed partial class ProtestSystem
    {
        private const float kCarSpacing = 7f;
        private const int kMaxPoliceEnds = 4;
        private const float kPeoplePerCar = 20f;

        private EntityQuery m_PoliceCarQuery;
        private EntityQuery m_PathQuery;

        // Police cars standing at our posts, with the duties they had before.
        private readonly Dictionary<Entity, PolicePurpose> m_HeldCars = new Dictionary<Entity, PolicePurpose>();

        private void CreateRoadQueries()
        {
            m_PoliceCarQuery = GetEntityQuery(ComponentType.ReadWrite<Game.Vehicles.PoliceCar>(), ComponentType.ReadOnly<ServiceDispatch>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
            m_PathQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<PathOwner>(), ComponentType.ReadOnly<PathElement>(), ComponentType.ReadOnly<Game.Vehicles.Car>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Vehicles.PoliceCar>(), ComponentType.ReadOnly<Game.Vehicles.FireEngine>(), ComponentType.ReadOnly<Game.Vehicles.Ambulance>(),
                    ComponentType.ReadOnly<Game.Vehicles.GarbageTruck>(), ComponentType.ReadOnly<ParkedCar>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        /// <summary>Road state at the last update, for the protest infoview.</summary>
        public HashSet<Entity> LastClosedEdges { get; private set; } = new HashSet<Entity>();
        public HashSet<Entity> LastClosedNodes { get; private set; } = new HashSet<Entity>();
        public Dictionary<Entity, Entity> LastPoliceEdges { get; private set; } = new Dictionary<Entity, Entity>();
        public HashSet<Entity> LastProtestEdges { get; private set; } = new HashSet<Entity>();
        public HashSet<Entity> LastRiotEdges { get; private set; } = new HashSet<Entity>();

        private void ResetRoads()
        {
            m_HeldCars.Clear();
            LastClosedEdges = new HashSet<Entity>();
            LastClosedNodes = new HashSet<Entity>();
            LastPoliceEdges = new Dictionary<Entity, Entity>();
            LastProtestEdges = new HashSet<Entity>();
            LastRiotEdges = new HashSet<Entity>();
        }

        private void UpdateRoads(Entity city, uint frame)
        {
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city, true);
            var closedEdges = new HashSet<Entity>();
            var protestEdges = new Dictionary<int, List<Entity>>();
            DynamicBuffer<ProtestEdge> stored = EntityManager.GetBuffer<ProtestEdge>(city, true);
            for (int i = 0; i < stored.Length; i++)
            {
                if (stored[i].m_Role != (byte)ProtestEdgeRole.Protest)
                {
                    continue;
                }
                if (!protestEdges.TryGetValue(stored[i].m_Protest, out List<Entity> list))
                {
                    list = new List<Entity>();
                    protestEdges[stored[i].m_Protest] = list;
                }
                list.Add(stored[i].m_Edge);
            }
            var closedProtests = new HashSet<int>();
            for (int p = 0; p < protests.Length; p++)
            {
                Protest protest = protests[p];
                if ((protest.m_Riot || protest.m_Participants >= kClosureParticipants) && protestEdges.TryGetValue(protest.m_Id, out List<Entity> list))
                {
                    closedProtests.Add(protest.m_Id);
                    foreach (Entity edge in list)
                    {
                        if (EntityManager.Exists(edge))
                        {
                            closedEdges.Add(edge);
                        }
                    }
                }
            }

            // Repression: police segments beyond the end nodes of a closed area, and the end nodes themselves.
            var policeLanes = new Dictionary<Entity, Entity>();   // edge -> node the police face (riot side)
            var closedNodes = new HashSet<Entity>();
            var postPoints = new Dictionary<int, List<(Entity edge, float t)>>();
            for (int p = 0; p < protests.Length; p++)
            {
                Protest protest = protests[p];
                var response = (ProtestResponse)protest.m_Response;
                if (response != ProtestResponse.Watch && response != ProtestResponse.Disperse || !protestEdges.TryGetValue(protest.m_Id, out List<Entity> own))
                {
                    continue;
                }
                int needed = (int)math.ceil(protest.m_Participants * protest.m_Intensity / kPeoplePerCar * (response == ProtestResponse.Disperse ? 1f : 0.5f));
                needed = math.max(needed, 1);
                var points = new List<(Entity edge, float t)>();
                if (closedProtests.Contains(protest.m_Id))
                {
                    List<(Entity edge, Entity from)> segments = PoliceSegments(own, closedEdges, needed);
                    foreach ((Entity edge, Entity from) in segments)
                    {
                        policeLanes[edge] = from;
                        closedNodes.Add(from);
                    }
                    points = PointsAlong(segments, needed);
                }
                else
                {
                    // Not closed: the police spread along the protest's own segments.
                    var segments = new List<(Entity edge, Entity from)>();
                    foreach (Entity edge in own)
                    {
                        if (EntityManager.TryGetComponent(edge, out Edge data))
                        {
                            segments.Add((edge, data.m_Start));
                        }
                    }
                    points = PointsAlong(segments, needed);
                }
                postPoints[protest.m_Id] = points;
            }

            // Nodes inside a closed area (every arm closed) close with it; other nodes stay open.
            foreach (Entity edge in closedEdges)
            {
                if (!EntityManager.TryGetComponent(edge, out Edge data))
                {
                    continue;
                }
                foreach (Entity node in new[] { data.m_Start, data.m_End })
                {
                    if (AllArmsClosed(node, closedEdges))
                    {
                        closedNodes.Add(node);
                    }
                }
            }

            ApplyClosures(city, closedEdges, closedNodes, policeLanes);
            UpdatePosts(city, frame, postPoints);

            // For the protest infoview.
            LastClosedEdges = closedEdges;
            LastClosedNodes = closedNodes;
            LastPoliceEdges = policeLanes;
            var protestEdgeSet = new HashSet<Entity>();
            var riotEdgeSet = new HashSet<Entity>();
            for (int p = 0; p < protests.Length; p++)
            {
                if (protestEdges.TryGetValue(protests[p].m_Id, out List<Entity> list))
                {
                    foreach (Entity edge in list)
                    {
                        (protests[p].m_Riot ? riotEdgeSet : protestEdgeSet).Add(edge);
                    }
                }
            }
            LastProtestEdges = protestEdgeSet;
            LastRiotEdges = riotEdgeSet;
        }

        private bool AllArmsClosed(Entity node, HashSet<Entity> closedEdges)
        {
            if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<ConnectedEdge> connected) || connected.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < connected.Length; i++)
            {
                if (!closedEdges.Contains(connected[i].m_Edge))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Police segments (2026-11-08 user decisions): the open road segments beyond the closed area's end nodes, up to four
        /// ends with the most residents and shops beyond them; when the cars do not fit, one more segment further out on the
        /// same road. Each comes with the node on the riot side.
        /// </summary>
        private List<(Entity edge, Entity from)> PoliceSegments(List<Entity> own, HashSet<Entity> closedEdges, int needed)
        {
            var candidates = new List<(Entity edge, Entity from, float weight)>();
            foreach (Entity edge in own)
            {
                if (!EntityManager.TryGetComponent(edge, out Edge data))
                {
                    continue;
                }
                foreach (Entity node in new[] { data.m_Start, data.m_End })
                {
                    if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<ConnectedEdge> connected))
                    {
                        continue;
                    }
                    for (int i = 0; i < connected.Length; i++)
                    {
                        Entity arm = connected[i].m_Edge;
                        if (closedEdges.Contains(arm) || !EntityManager.HasComponent<Game.Net.Road>(arm) || candidates.Exists(c => c.edge == arm))
                        {
                            continue;
                        }
                        candidates.Add((arm, node, 1f + EdgeResidents(arm) / 50f + Shops(arm)));
                    }
                }
            }
            candidates.Sort((a, b) => b.weight.CompareTo(a.weight));
            var result = new List<(Entity edge, Entity from)>();
            int capacity = 0;
            for (int i = 0; i < candidates.Count && i < kMaxPoliceEnds; i++)
            {
                result.Add((candidates[i].edge, candidates[i].from));
                capacity += Capacity(candidates[i].edge, candidates[i].from);
            }
            // Overflow: the next segment out along each chosen end, once.
            int ends = result.Count;
            for (int i = 0; i < ends && capacity < needed; i++)
            {
                (Entity edge, Entity from) = result[i];
                if (!EntityManager.TryGetComponent(edge, out Edge data))
                {
                    continue;
                }
                Entity far = data.m_Start == from ? data.m_End : data.m_Start;
                Entity next = Entity.Null;
                float best = -1f;
                if (EntityManager.TryGetBuffer(far, true, out DynamicBuffer<ConnectedEdge> connected))
                {
                    for (int c = 0; c < connected.Length; c++)
                    {
                        Entity arm = connected[c].m_Edge;
                        if (arm == edge || closedEdges.Contains(arm) || !EntityManager.HasComponent<Game.Net.Road>(arm) || result.Exists(r => r.edge == arm))
                        {
                            continue;
                        }
                        float straight = Straightness(edge, far, arm);
                        if (straight > best)
                        {
                            best = straight;
                            next = arm;
                        }
                    }
                }
                if (next != Entity.Null)
                {
                    result.Add((next, far));
                    capacity += Capacity(next, far);
                }
            }
            return result;
        }

        /// <summary>How straight the road goes on from <paramref name="edge"/> through <paramref name="node"/> into <paramref name="next"/> (cosine).</summary>
        private float Straightness(Entity edge, Entity node, Entity next)
        {
            float3 at = EntityManager.GetComponentData<Node>(node).m_Position;
            float3 a = Far(edge, node) - at;
            float3 b = Far(next, node) - at;
            return -math.dot(math.normalizesafe(a.xz), math.normalizesafe(b.xz));
        }

        private float3 Far(Entity edge, Entity node)
        {
            Edge data = EntityManager.GetComponentData<Edge>(edge);
            Entity other = data.m_Start == node ? data.m_End : data.m_Start;
            return EntityManager.GetComponentData<Node>(other).m_Position;
        }

        /// <summary>Police cars a segment holds: lanes leading toward <paramref name="from"/> (one left free when there are two or more) × length ÷ 7 m.</summary>
        private int Capacity(Entity edge, Entity from)
        {
            int lanes = EntryLanes(edge, from).Count;
            if (lanes >= 2)
            {
                lanes--;
            }
            float length = EntityManager.TryGetComponent(edge, out Curve curve) ? curve.m_Length : 0f;
            return math.max(1, (int)(math.max(1, lanes) * length / kCarSpacing));
        }

        /// <summary>Driving lanes of a segment that lead toward <paramref name="toward"/> (their curve ends nearer that node).</summary>
        private List<Entity> EntryLanes(Entity edge, Entity toward)
        {
            var lanes = new List<Entity>();
            if (!EntityManager.TryGetBuffer(edge, true, out DynamicBuffer<SubLane> subLanes) || !EntityManager.TryGetComponent(toward, out Node node))
            {
                return lanes;
            }
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;
                if (!EntityManager.HasComponent<Game.Net.CarLane>(lane) || EntityManager.HasComponent<MasterLane>(lane) || !EntityManager.TryGetComponent(lane, out Curve curve))
                {
                    continue;
                }
                float toEnd = math.distancesq(curve.m_Bezier.d.xz, node.m_Position.xz);
                float toStart = math.distancesq(curve.m_Bezier.a.xz, node.m_Position.xz);
                if (toEnd < toStart)
                {
                    lanes.Add(lane);
                }
            }
            return lanes;
        }

        /// <summary>
        /// Post points: along each segment from its riot-side node outward, 7 m apart, taken round-robin over the segments so
        /// every end fills (the next request always gets the next free place, 2026-11-08 user decision).
        /// </summary>
        private List<(Entity edge, float t)> PointsAlong(List<(Entity edge, Entity from)> segments, int count)
        {
            var points = new List<(Entity edge, float t)>();
            var perSegment = new List<List<float>>();
            foreach ((Entity edge, Entity from) in segments)
            {
                var list = new List<float>();
                if (EntityManager.TryGetComponent(edge, out Curve curve) && EntityManager.TryGetComponent(edge, out Edge data) && curve.m_Length > 0f)
                {
                    bool fromStart = data.m_Start == from;
                    int capacity = Capacity(edge, from);
                    int rows = math.max(1, (int)(curve.m_Length / kCarSpacing));
                    for (int k = 0; k < capacity; k++)
                    {
                        float d = (k % rows + 0.5f) * kCarSpacing;
                        float t = math.saturate(d / curve.m_Length);
                        list.Add(fromStart ? t : 1f - t);
                    }
                }
                perSegment.Add(list);
            }
            for (int round = 0; points.Count < count; round++)
            {
                bool any = false;
                for (int s = 0; s < segments.Count && points.Count < count; s++)
                {
                    if (round < perSegment[s].Count)
                    {
                        points.Add((segments[s].edge, perSegment[s][round]));
                        any = true;
                    }
                }
                if (!any)
                {
                    break;
                }
            }
            return points;
        }

        /// <summary>Whether this protest's police stand on <paramref name="beyond"/>, the segment past <paramref name="edge"/>.</summary>
        private bool PoliceHold(Entity city, int protestId, Entity edge, Entity beyond)
        {
            DynamicBuffer<PolicePost> posts = EntityManager.GetBuffer<PolicePost>(city, true);
            for (int i = 0; i < posts.Length; i++)
            {
                if (posts[i].m_Protest == protestId && EntityManager.TryGetComponent(posts[i].m_Point, out Attached attached) && attached.m_Parent == beyond)
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ lane flags

        private void ApplyClosures(Entity city, HashSet<Entity> closedEdges, HashSet<Entity> closedNodes, Dictionary<Entity, Entity> policeLanes)
        {
            var wanted = new HashSet<Entity>();
            foreach (Entity edge in closedEdges)
            {
                AddCarLanes(edge, wanted);
            }
            foreach (Entity node in closedNodes)
            {
                AddCarLanes(node, wanted);
            }
            foreach (KeyValuePair<Entity, Entity> entry in policeLanes)
            {
                foreach (Entity lane in EntryLanes(entry.Key, entry.Value))
                {
                    wanted.Add(lane);
                }
                // Slave lanes carry the traffic; their master (the pathfinding lane of the group) follows them.
                AddMasters(entry.Key, entry.Value, wanted);
            }

            DynamicBuffer<ClosedLane> closed = EntityManager.GetBuffer<ClosedLane>(city);
            var have = new HashSet<Entity>();
            var newlyClosed = new HashSet<Entity>();
            for (int i = closed.Length - 1; i >= 0; i--)
            {
                Entity lane = closed[i].m_Lane;
                if (!EntityManager.Exists(lane) || !EntityManager.HasComponent<Game.Net.CarLane>(lane))
                {
                    closed.RemoveAt(i);
                    continue;
                }
                if (!wanted.Contains(lane))
                {
                    SetPublicOnly(lane, false);
                    // Tagging the lane is a structural change: fetch the buffer again.
                    closed = EntityManager.GetBuffer<ClosedLane>(city);
                    closed.RemoveAt(i);
                    continue;
                }
                // Loading a city (and the game rebuilding a lane) can reset its flags: close it again.
                if ((EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.PublicOnly) == 0)
                {
                    SetPublicOnly(lane, true);
                    closed = EntityManager.GetBuffer<ClosedLane>(city);
                    newlyClosed.Add(lane);
                }
                have.Add(lane);
            }
            foreach (Entity lane in wanted)
            {
                if (have.Contains(lane))
                {
                    continue;
                }
                Game.Net.CarLane carLane = EntityManager.GetComponentData<Game.Net.CarLane>(lane);
                if ((carLane.m_Flags & Game.Net.CarLaneFlags.PublicOnly) != 0)
                {
                    // Already a bus lane of its own: nothing to close or give back.
                    continue;
                }
                SetPublicOnly(lane, true);
                closed = EntityManager.GetBuffer<ClosedLane>(city);
                closed.Add(new ClosedLane { m_Lane = lane });
                newlyClosed.Add(lane);
            }
            if (newlyClosed.Count > 0)
            {
                RerouteThrough(newlyClosed);
            }
        }

        private void AddCarLanes(Entity owner, HashSet<Entity> into)
        {
            if (!EntityManager.TryGetBuffer(owner, true, out DynamicBuffer<SubLane> subLanes))
            {
                return;
            }
            for (int i = 0; i < subLanes.Length; i++)
            {
                if (EntityManager.HasComponent<Game.Net.CarLane>(subLanes[i].m_SubLane))
                {
                    into.Add(subLanes[i].m_SubLane);
                }
            }
        }

        private void AddMasters(Entity edge, Entity toward, HashSet<Entity> into)
        {
            if (!EntityManager.TryGetBuffer(edge, true, out DynamicBuffer<SubLane> subLanes) || !EntityManager.TryGetComponent(toward, out Node node))
            {
                return;
            }
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;
                if (EntityManager.HasComponent<MasterLane>(lane) && EntityManager.HasComponent<Game.Net.CarLane>(lane) && EntityManager.TryGetComponent(lane, out Curve curve)
                    && math.distancesq(curve.m_Bezier.d.xz, node.m_Position.xz) < math.distancesq(curve.m_Bezier.a.xz, node.m_Position.xz))
                {
                    into.Add(lane);
                }
            }
        }

        /// <summary>Sets or clears the lane's public-only flag and tells the pathfinder (the game's own tag for lane changes).</summary>
        private void SetPublicOnly(Entity lane, bool on)
        {
            Game.Net.CarLane carLane = EntityManager.GetComponentData<Game.Net.CarLane>(lane);
            if (on)
            {
                carLane.m_Flags |= Game.Net.CarLaneFlags.PublicOnly;
            }
            else
            {
                carLane.m_Flags &= ~Game.Net.CarLaneFlags.PublicOnly;
            }
            EntityManager.SetComponentData(lane, carLane);
            if (!EntityManager.HasComponent<PathfindUpdated>(lane))
            {
                EntityManager.AddComponent<PathfindUpdated>(lane);
            }
        }

        /// <summary>
        /// Cars whose planned route still runs through a newly closed lane plan again (the game only re-plans when a lane
        /// disappears): those outside go round, those inside drive out the cheapest way.
        /// </summary>
        private void RerouteThrough(HashSet<Entity> lanes)
        {
            // Chunk by chunk over every car with a route (performance review 2026-11-09), marking routes through the lanes.
            int rerouted = 0;
            m_PathQuery.CompleteDependency();
            ComponentTypeHandle<PathOwner> ownerType = GetComponentTypeHandle<PathOwner>(false);
            BufferTypeHandle<PathElement> pathType = GetBufferTypeHandle<PathElement>(true);
            using (NativeArray<ArchetypeChunk> chunks = m_PathQuery.ToArchetypeChunkArray(Allocator.Temp))
            {
                foreach (ArchetypeChunk chunk in chunks)
                {
                    NativeArray<PathOwner> owners = chunk.GetNativeArray(ref ownerType);
                    BufferAccessor<PathElement> paths = chunk.GetBufferAccessor(ref pathType);
                    for (int v = 0; v < chunk.Count; v++)
                    {
                        PathOwner owner = owners[v];
                        DynamicBuffer<PathElement> path = paths[v];
                        for (int e = owner.m_ElementIndex; e < path.Length; e++)
                        {
                            if (lanes.Contains(path[e].m_Target))
                            {
                                owner.m_State |= PathFlags.Obsolete;
                                owners[v] = owner;
                                rerouted++;
                                break;
                            }
                        }
                    }
                }
            }
            if (rerouted > 0)
            {
                Mod.Log.Info($"Road closure: {lanes.Count} lanes closed, {rerouted} vehicles plan again.");
            }
        }

        // ------------------------------------------------------------------ police posts

        private void UpdatePosts(Entity city, uint frame, Dictionary<int, List<(Entity edge, float t)>> postPoints)
        {
            DynamicBuffer<PolicePost> posts = EntityManager.GetBuffer<PolicePost>(city);
            // Posts of protests no longer repressed (or ended) go; so do posts that lost a part (after loading a city the
            // game may have cleared the site), which are then asked for again.
            for (int i = posts.Length - 1; i >= 0; i--)
            {
                if (!postPoints.ContainsKey(posts[i].m_Protest) || !PostIntact(posts[i]))
                {
                    DestroyPost(posts[i]);
                    posts = EntityManager.GetBuffer<PolicePost>(city);
                    posts.RemoveAt(i);
                }
            }

            // Cars standing at each post; they are taken off other calls while there.
            var sites = new Dictionary<Entity, int>();
            for (int i = 0; i < posts.Length; i++)
            {
                sites[posts[i].m_Site] = posts[i].m_Protest;
            }
            var arrived = new Dictionary<int, int>();
            var atSite = new HashSet<Entity>();
            var standing = new HashSet<Entity>();
            using (NativeArray<Entity> cars = m_PoliceCarQuery.ToEntityArray(Allocator.Temp))
            {
                for (int c = 0; c < cars.Length; c++)
                {
                    Game.Vehicles.PoliceCar car = EntityManager.GetComponentData<Game.Vehicles.PoliceCar>(cars[c]);
                    DynamicBuffer<ServiceDispatch> dispatches = EntityManager.GetBuffer<ServiceDispatch>(cars[c], true);
                    if ((car.m_State & (PoliceCarFlags.AtTarget | PoliceCarFlags.AccidentTarget)) != (PoliceCarFlags.AtTarget | PoliceCarFlags.AccidentTarget)
                        || dispatches.Length == 0 || !EntityManager.TryGetComponent(dispatches[0].m_Request, out PoliceEmergencyRequest request)
                        || !sites.TryGetValue(request.m_Site, out int protestId))
                    {
                        continue;
                    }
                    arrived.TryGetValue(protestId, out int n);
                    arrived[protestId] = n + 1;
                    atSite.Add(request.m_Site);
                    standing.Add(cars[c]);
                    if (!m_HeldCars.ContainsKey(cars[c]))
                    {
                        // A car loaded from a save while it stood here was saved without duties: give back its station's.
                        m_HeldCars[cars[c]] = car.m_PurposeMask != 0 ? car.m_PurposeMask : StationPurpose(cars[c]);
                    }
                    if (car.m_PurposeMask != 0)
                    {
                        car.m_PurposeMask = 0;
                        EntityManager.SetComponentData(cars[c], car);
                    }
                }
            }
            // Cars that left (post gone, shift over) get their duties back.
            var release = new List<Entity>();
            foreach (KeyValuePair<Entity, PolicePurpose> held in m_HeldCars)
            {
                if (!standing.Contains(held.Key))
                {
                    release.Add(held.Key);
                }
            }
            foreach (Entity car in release)
            {
                if (EntityManager.Exists(car) && EntityManager.TryGetComponent(car, out Game.Vehicles.PoliceCar data))
                {
                    data.m_PurposeMask = m_HeldCars[car];
                    EntityManager.SetComponentData(car, data);
                }
                m_HeldCars.Remove(car);
            }

            // A secured post whose car has gone (shift over) asks again.
            posts = EntityManager.GetBuffer<PolicePost>(city);
            for (int i = 0; i < posts.Length; i++)
            {
                if (!atSite.Contains(posts[i].m_Site) && EntityManager.TryGetComponent(posts[i].m_Site, out AccidentSite site) && (site.m_Flags & AccidentSiteFlags.Secured) != 0)
                {
                    site.m_Flags &= ~AccidentSiteFlags.Secured;
                    EntityManager.SetComponentData(posts[i].m_Site, site);
                }
            }

            // One request at a time per protest (2026-11-08 user decision): the next post only once the last has a car.
            DynamicBuffer<Protest> protests = EntityManager.GetBuffer<Protest>(city);
            for (int p = 0; p < protests.Length; p++)
            {
                Protest protest = protests[p];
                if (!postPoints.TryGetValue(protest.m_Id, out List<(Entity edge, float t)> points))
                {
                    continue;
                }
                var response = (ProtestResponse)protest.m_Response;
                int needed = (int)math.ceil(protest.m_Participants * protest.m_Intensity / kPeoplePerCar * (response == ProtestResponse.Disperse ? 1f : 0.5f));
                needed = math.max(1, needed);
                posts = EntityManager.GetBuffer<PolicePost>(city);
                int have = 0;
                bool pending = false;
                int last = -1;
                for (int i = 0; i < posts.Length; i++)
                {
                    if (posts[i].m_Protest != protest.m_Id)
                    {
                        continue;
                    }
                    have++;
                    last = i;
                    if (!Assigned(posts[i]) && frame - posts[i].m_CreatedFrame < 4096u)
                    {
                        pending = true;
                    }
                }
                if (have > needed && last >= 0)
                {
                    DestroyPost(posts[last]);
                    posts = EntityManager.GetBuffer<PolicePost>(city);
                    posts.RemoveAt(last);
                    have--;
                }
                else if (have < needed && !pending && have < points.Count)
                {
                    (Entity edge, float t) = points[have];
                    PolicePost post = CreatePost(protest, edge, t, frame);
                    if (post.m_Site != Entity.Null)
                    {
                        EntityManager.GetBuffer<PolicePost>(city).Add(post);
                    }
                }
                arrived.TryGetValue(protest.m_Id, out int inPlace);
                protest.m_CarsNeeded = needed;
                protest.m_CarsArrived = inPlace;
                protests = EntityManager.GetBuffer<Protest>(city);
                protests[p] = protest;
            }
        }

        /// <summary>Whether a post still has its accident site, its event with the target and the marked point.</summary>
        internal bool PostIntact(PolicePost post)
        {
            return post.m_Site != Entity.Null && EntityManager.HasComponent<AccidentSite>(post.m_Site)
                && post.m_Point != Entity.Null && EntityManager.HasComponent<InvolvedInAccident>(post.m_Point)
                && post.m_Event != Entity.Null && EntityManager.TryGetBuffer(post.m_Event, true, out DynamicBuffer<TargetElement> targets) && targets.Length > 0;
        }

        /// <summary>Police cars standing at our posts now (self-test).</summary>
        internal int HeldCarCount => m_HeldCars.Count;

        /// <summary>The duties a car gets when its station sends it out: the station's purposes within the car's own.</summary>
        private PolicePurpose StationPurpose(Entity car)
        {
            PolicePurpose purpose = PolicePurpose.Patrol | PolicePurpose.Emergency;
            if (EntityManager.TryGetComponent(car, out Owner owner) && EntityManager.TryGetComponent(owner.m_Owner, out Game.Buildings.PoliceStation station))
            {
                purpose = station.m_PurposeMask;
            }
            if (EntityManager.TryGetComponent(car, out PrefabRef prefab) && EntityManager.TryGetComponent(prefab.m_Prefab, out PoliceCarData data))
            {
                purpose &= data.m_PurposeMask;
            }
            return purpose;
        }

        /// <summary>Whether the game has given the post's request to a police car (or a car stands there already).</summary>
        private bool Assigned(PolicePost post)
        {
            if (!EntityManager.TryGetComponent(post.m_Site, out AccidentSite site))
            {
                return true;
            }
            if ((site.m_Flags & AccidentSiteFlags.Secured) != 0)
            {
                return true;
            }
            return site.m_PoliceRequest != Entity.Null && EntityManager.HasComponent<Dispatched>(site.m_PoliceRequest);
        }

        /// <summary>
        /// A post: a point attached to the road at <paramref name="t"/> (the place the car drives to), an event that targets it,
        /// and an accident site the game sends one police car to. The point carries the "involved in accident" mark so the game
        /// keeps the site and asks for the police (no traffic accident or crime flags: no other icons or behaviour).
        /// </summary>
        private PolicePost CreatePost(Protest protest, Entity edge, float t, uint frame)
        {
            if (!EntityManager.TryGetComponent(edge, out Curve curve))
            {
                return default;
            }
            Entity prefab = protest.m_Policy;
            if (prefab == Entity.Null)
            {
                foreach (PolicyDefinition definition in PolicyDefinitions.All)
                {
                    if (definition.Prefab != Entity.Null)
                    {
                        prefab = definition.Prefab;
                        break;
                    }
                }
            }
            float3 position = MathUtils.Position(curve.m_Bezier, t);
            Entity eventEntity = EntityManager.CreateEntity();
            Entity point = EntityManager.CreateEntity(typeof(Attached), typeof(Game.Objects.Transform), typeof(InvolvedInAccident));
            EntityManager.SetComponentData(point, new Attached(edge, Entity.Null, t));
            EntityManager.SetComponentData(point, new Game.Objects.Transform(position, quaternion.identity));
            EntityManager.SetComponentData(point, new InvolvedInAccident(eventEntity, 1f, frame));
            EntityManager.AddBuffer<TargetElement>(eventEntity).Add(new TargetElement(point));
            Entity site = EntityManager.CreateEntity(typeof(AccidentSite), typeof(PrefabRef));
            EntityManager.SetComponentData(site, new AccidentSite(eventEntity, 0, frame));
            EntityManager.SetComponentData(site, new PrefabRef(prefab));
            return new PolicePost { m_Protest = protest.m_Id, m_Site = site, m_Event = eventEntity, m_Point = point, m_CreatedFrame = frame };
        }

        private void DestroyPost(PolicePost post)
        {
            foreach (Entity entity in new[] { post.m_Site, post.m_Point, post.m_Event })
            {
                if (entity != Entity.Null && EntityManager.Exists(entity))
                {
                    EntityManager.DestroyEntity(entity);
                }
            }
        }

        private void RemovePosts(Entity city, int protestId)
        {
            if (!EntityManager.HasBuffer<PolicePost>(city))
            {
                return;
            }
            DynamicBuffer<PolicePost> posts = EntityManager.GetBuffer<PolicePost>(city);
            for (int i = posts.Length - 1; i >= 0; i--)
            {
                if (posts[i].m_Protest == protestId)
                {
                    PolicePost post = posts[i];
                    DestroyPost(post);
                    posts = EntityManager.GetBuffer<PolicePost>(city);
                    posts.RemoveAt(i);
                }
            }
        }
    }
}
