﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BDArmory.Competition.RemoteOrchestration;
using BDArmory.Modules;
using BDArmory.Core;
using BDArmory.UI;
using UnityEngine;
using BDArmory.Misc;
using System.IO;
using BDArmory.FX;
using BDArmory.Core.Module;

namespace BDArmory.Competition.OrchestrationStrategies
{
    public class WaypointFollowingStrategy : OrchestrationStrategy
    {
        public class Waypoint
        {
            public float latitude;
            public float longitude;
            public float altitude;
            public Waypoint(float latitude, float longitude, float altitude) //really, this should become the waypointmarker, and be a class that contains both a dataset (lat/long coords) and a togglable model
            {
                this.latitude = latitude;
                this.longitude = longitude;
                this.altitude = altitude;
            }
        }
        private List<Waypoint> waypoints;
        private List<BDModulePilotAI> pilots;
        public static List<BDModulePilotAI> activePilots;
        public static List<WayPointTracing> Ghosts = new List<WayPointTracing>();

        public static string ModelPath = "BDArmory/Models/WayPoint/model";

        public WaypointFollowingStrategy(List<Waypoint> waypoints)
        {
            this.waypoints = waypoints;
        }

        public IEnumerator Execute(BDAScoreClient client, BDAScoreService service)
        {
            if (BDArmorySettings.DRAW_DEBUG_LABELS) Debug.Log("[BDArmory.WaypointFollowingStrategy]: Started");
            pilots = LoadedVesselSwitcher.Instance.WeaponManagers.SelectMany(tm => tm.Value).Select(wm => wm.vessel).Where(v => v != null && v.loaded).Select(v => VesselModuleRegistry.GetBDModulePilotAI(v)).Where(p => p != null).ToList();
            if (pilots.Count > 1) //running multiple craft through the waypoints at the same time
                LoadedVesselSwitcher.Instance.MassTeamSwitch(true);
            else //increment team each heat
            {
                char T = (Char)(Convert.ToUInt16('A') + BDATournament.Instance.currentHeat);
                pilots[0].weaponManager.SetTeam(BDTeam.Get(T.ToString()));
            }
            PrepareCompetition();

            // Configure the pilots' waypoints.
            var mappedWaypoints = waypoints.Select(e => new Vector3(e.latitude, e.longitude, e.altitude)).ToList();
            BDACompetitionMode.Instance.competitionStatus.Add($"Starting waypoints competition {BDACompetitionMode.Instance.CompetitionID}.");
            if (BDArmorySettings.DRAW_DEBUG_LABELS) Debug.Log(string.Format("[BDArmory.WaypointFollowingStrategy]: Setting {0} waypoints", mappedWaypoints.Count));

            foreach (var pilot in pilots)
            {
                pilot.SetWaypoints(mappedWaypoints);
            }
            // Wait for the pilots to complete the course.
            var startedAt = Planetarium.GetUniversalTime();
            yield return new WaitWhile(() => pilots.Any(pilot => pilot != null && pilot.weaponManager != null && pilot.IsFlyingWaypoints && !(pilot.vessel.Landed || pilot.vessel.Splashed)));
            var endedAt = Planetarium.GetUniversalTime();

            BDACompetitionMode.Instance.competitionStatus.Add("Waypoints competition finished. Scores:");
            foreach (var player in BDACompetitionMode.Instance.Scores.Players)
            {
                var waypointScores = BDACompetitionMode.Instance.Scores.ScoreData[player].waypointsReached;
                var waypointCount = waypointScores.Count();
                var deviation = waypointScores.Sum(w => w.deviation);
                var elapsedTime = waypointCount == 0 ? 0 : waypointScores.Last().timestamp - waypointScores.First().timestamp;
                if (service != null) service.TrackWaypoint(player, (float)elapsedTime, waypointCount, deviation);

                var displayName = player;
                if (BDArmorySettings.ENABLE_HOS && BDArmorySettings.HALL_OF_SHAME_LIST.Contains(player) && !string.IsNullOrEmpty(BDArmorySettings.HOS_BADGE))
                {
                    displayName += " (" + BDArmorySettings.HOS_BADGE + ")";
                }
                BDACompetitionMode.Instance.competitionStatus.Add($"  - {displayName}: Time: {elapsedTime:F1}s, Waypoints reached: {waypointCount}, Deviation: {deviation}");
                Debug.Log(string.Format("[BDArmory.WaypointFollowingStrategy]: Finished {0}, elapsed={1:0.00}, count={2}, deviation={3:0.00}", player, elapsedTime, waypointCount, deviation));
            }

            CleanUp();
        }

        void PrepareCompetition()
        {
            if (BDACompetitionMode.Instance.competitionIsActive) BDACompetitionMode.Instance.StopCompetition(); // Stop any currently active competition.
            BDACompetitionMode.Instance.competitionIsActive = true; // Set the competition as now active so the competition start type is correct.
            BDACompetitionMode.Instance.ResetCompetitionStuff(); // Reset a bunch of stuff related to competitions so they don't interfere.
            BDACompetitionMode.Instance.competitionType = CompetitionType.WAYPOINTS;
            BDACompetitionMode.Instance.Scores.ConfigurePlayers(pilots.Select(p => p.vessel).ToList());
            if (BDArmorySettings.AUTO_ENABLE_VESSEL_SWITCHING)
                LoadedVesselSwitcher.Instance.EnableAutoVesselSwitching(true);
            if (BDArmorySettings.TIME_OVERRIDE && BDArmorySettings.TIME_SCALE != 0)
            { Time.timeScale = BDArmorySettings.TIME_SCALE; }
            Debug.Log("[BDArmory.BDACompetitionMode:" + BDACompetitionMode.Instance.CompetitionID.ToString() + "]: Starting Competition");
            if (BDArmorySettings.WAYPOINTS_VISUALIZE)
            {
                Vector3 previousLocation = FlightGlobals.ActiveVessel.transform.position;
                //FlightGlobals.currentMainBody.GetLatLonAlt(FlightGlobals.ActiveVessel.transform.position, out previousLocation.x, out previousLocation.y, out previousLocation.z);
                //previousLocation.z = BDArmorySettings.WAYPOINTS_ALTITUDE;
                if (!string.IsNullOrEmpty(VesselSpawnerWindow.Instance.SelectedModel))
                    ModelPath = "BDArmory/Models/WayPoint/" + VesselSpawnerWindow.Instance.SelectedModel;
                for (int i = 0; i < waypoints.Count; i++)
                {
                    float terrainAltitude = (float)FlightGlobals.currentMainBody.TerrainAltitude(waypoints[i].latitude, waypoints[i].longitude);
                    Vector3d WorldCoords = VectorUtils.GetWorldSurfacePostion(new Vector3(waypoints[i].latitude, waypoints[i].longitude, waypoints[i].altitude + terrainAltitude), FlightGlobals.currentMainBody);
                    //FlightGlobals.currentMainBody.GetLatLonAlt(new Vector3(waypoints[i].latitude, waypoints[i].longitude, waypoints[i].altitude), out WorldCoords.x, out WorldCoords.y, out WorldCoords.z);
                    var direction = (WorldCoords - previousLocation).normalized;
                    WayPointMarker.CreateWaypoint(WorldCoords, direction, ModelPath, BDArmorySettings.WAYPOINTS_SCALE);
                    previousLocation = WorldCoords;
                    var location = string.Format("({0:##.###}, {1:##.###}, {2:####}", waypoints[i].latitude, waypoints[i].longitude, waypoints[i].altitude);
                    Debug.Log("[BDArmory.Waypoints]: Creating waypoint marker at  " + " " + location);
                }
            }

            if (BDArmorySettings.WAYPOINTS_MODE || (BDArmorySettings.RUNWAY_PROJECT && BDArmorySettings.RUNWAY_PROJECT_ROUND == 50))
            {
                float terrainAltitude = (float)FlightGlobals.currentMainBody.TerrainAltitude(waypoints[0].latitude, waypoints[0].longitude);
                Vector3d WorldCoords = VectorUtils.GetWorldSurfacePostion(new Vector3(waypoints[0].latitude, waypoints[0].longitude, waypoints[0].altitude + terrainAltitude), FlightGlobals.currentMainBody);
                foreach (var pilot in pilots)
                {
                    if (BDArmorySettings.RUNWAY_PROJECT && BDArmorySettings.RUNWAY_PROJECT_ROUND == 50) // S4R10 alt limiter
                    {
                        if (pilot == null) continue;
                        // Max Altitude must be 100.
                        pilot.maxAltitudeToggle = true;
                        pilot.maxAltitude = 100f;
                        pilot.minAltitude = Mathf.Min(pilot.minAltitude, 50f); // Waypoints are at 50, so anything higher than this is going to trigger gain alt all the time.
                        pilot.defaultAltitude = Mathf.Min(pilot.defaultAltitude, 100f);
                    }
                    /*
                    if (pilots.Count > 1) //running multiple craft through the waypoints at the same time
                    {
                        if (Ghosts.Count > 0)
                        {
                            foreach (var tracer in Ghosts)
                            {
                                if (tracer != null)
                                    tracer.gameObject.SetActive(false);
                            }
                        }
                        Ghosts.Clear(); //need to have Ghosts also clear every round start
                        WayPointTracing.CreateTracer(WorldCoords, pilot);
                    }
                    else
                    {
                        if (Ghosts.Count > 0)
                        {
                            foreach (var tracer in Ghosts)
                            {
                                if (tracer != null)
                                    tracer.resetRenderer();
                            }
                        }
                        WayPointTracing.CreateTracer(WorldCoords, pilots[0]);
                    }
                    */
                    if (BDArmorySettings.ENABLE_HOS && BDArmorySettings.HALL_OF_SHAME_LIST.Count > 0)
                    {
                        if (BDArmorySettings.HALL_OF_SHAME_LIST.Contains(pilot.vessel.GetName()))
                        {
                            using (List<Part>.Enumerator part = pilot.vessel.Parts.GetEnumerator())
                                while (part.MoveNext())
                                {
                                    if (BDArmorySettings.HOS_FIRE > 0)
                                    {
                                        BulletHitFX.AttachFire(part.Current.transform.position, part.Current, BDArmorySettings.HOS_FIRE * 50, "GM", BDArmorySettings.COMPETITION_DURATION * 60, 1, false);
                                        //internal fire instead of external, as external fires are extinguished at > 120m/s
                                    }
                                    if (BDArmorySettings.HOS_MASS != 0)
                                    {
                                        var MM = part.Current.FindModuleImplementing<ModuleMassAdjust>();
                                        if (MM == null)
                                        {
                                            MM = (ModuleMassAdjust)part.Current.AddModule("ModuleMassAdjust");
                                        }
                                        MM.duration = BDArmorySettings.COMPETITION_DURATION * 60;
                                        MM.massMod += (float)(BDArmorySettings.HOS_MASS / pilot.vessel.Parts.Count); //evenly distribute mass change across entire vessel
                                    }
                                    if (BDArmorySettings.HOS_DMG != 1)
                                    {
                                        var HPT = part.Current.FindModuleImplementing<HitpointTracker>();
                                        HPT.defenseMutator = (float)(1 / BDArmorySettings.HOS_DMG);
                                    }
                                    if (BDArmorySettings.HOS_THRUST != 100)
                                    {
                                        using (var engine = VesselModuleRegistry.GetModuleEngines(pilot.vessel).GetEnumerator())
                                            while (engine.MoveNext())
                                            {
                                                engine.Current.thrustPercentage = BDArmorySettings.HOS_THRUST;
                                            }
                                    }
                                }
                        }
                    }
                }
            }
        }

        public void CleanUp()
        {
            if (BDACompetitionMode.Instance.competitionIsActive) BDACompetitionMode.Instance.StopCompetition(); // Competition is done, so stop it and do the rest of the book-keeping.
        }
    }

    public class WayPointMarker : MonoBehaviour
    {
        //public static ObjectPool WaypointPool;
        public static Dictionary<string, ObjectPool> WaypointPools = new Dictionary<string, ObjectPool>();
        public Vector3 Position { get; set; }

        public bool disabled = false;
        static void CreateObjectPool(string ModelPath)
        {
            var key = ModelPath;
            if (!WaypointPools.ContainsKey(key) || WaypointPools[key] == null)
            {
                var WPTemplate = GameDatabase.Instance.GetModel(ModelPath);
                if (WPTemplate == null)
                {
                    Debug.LogError("[BDArmory.WayPointMarker]: " + ModelPath + " was not found, using the default model instead. Please fix your model.");
                    WPTemplate = GameDatabase.Instance.GetModel("BDArmory/Models/WayPoint/model");
                }
                WPTemplate.SetActive(false);
                WPTemplate.AddComponent<WayPointMarker>();
                WaypointPools[key] = ObjectPool.CreateObjectPool(WPTemplate, 10, true, true);
            }
        }
        public static void CreateWaypoint(Vector3 position, Vector3 direction, string ModelPath, float scale)
        {
            CreateObjectPool(ModelPath);

            GameObject newWayPoint = WaypointPools[ModelPath].GetPooledObject();
            Quaternion rotation = Quaternion.LookRotation(direction, -FlightGlobals.getGeeForceAtPosition(Vector3.zero).normalized); //this needed, so the model is aligned to the ground normal, not the body transform orientation


            newWayPoint.transform.SetPositionAndRotation(position, rotation);

            //newWayPoint.transform.SetPositionAndRotation(position, rotation);
            //rotation based on root(partTools) transform of the model, with Z+ forward, and Y+ up
            newWayPoint.transform.RotateAround(position, newWayPoint.transform.up, Vector3.Angle(newWayPoint.transform.forward, direction)); //rotate model on horizontal plane towards last gate
            newWayPoint.transform.RotateAround(position, newWayPoint.transform.right, Vector3.Angle(newWayPoint.transform.forward, direction)); //and on vertical plane if elevation change between the two

            float WPScale = scale / 500; //default ring/torii models scaled for 500m
            newWayPoint.transform.localScale = new Vector3(WPScale, WPScale, WPScale);
            WayPointMarker NWP = newWayPoint.GetComponent<WayPointMarker>();
            NWP.Position = position;
            newWayPoint.SetActive(true);
        }
        void Awake()
        {
            transform.parent = FlightGlobals.ActiveVessel.mainBody.transform; //FIXME need to update this to grab worldindex for non-kerbin spawns for custom track building
        }
        private void OnEnable()
        {
            disabled = false;
        }
        void Update()
        {
            if (!gameObject.activeInHierarchy) return;
            if (disabled || !BDACompetitionMode.Instance.competitionIsActive || !HighLogic.LoadedSceneIsFlight)
            {
                gameObject.SetActive(false);
                return;
            }
            //this.transform.LookAt(FlightCamera.fetch.mainCamera.transform); //Always face the camera
            //if adding Races! style waypoint customization for building custom tracks, have the camera follow be a toggle, to allow for different models? Aub's Torii, etc.
        }
    }
    public class WayPointTracing : MonoBehaviour
    {
        public static ObjectPool TracePool;
        public Vector3 Position { get; set; }
        public BDModulePilotAI vessel { get; set; }

        private List<Vector3> pathPoints = new List<Vector3>();

        LineRenderer tracerRenderer;

        public bool disabled = false;
        public bool replayGhost = false;
        static void CreateObjectPool()
        {
            GameObject ghost = GameDatabase.Instance.GetModel("BDArmory/Models/shell/model"); //could have just done this as a gameObject instead of a tiny model.
            ghost.SetActive(false);
            ghost.AddComponent<WayPointTracing>();
            TracePool = ObjectPool.CreateObjectPool(ghost, 120, true, true);
        }

        public static void CreateTracer(Vector3 position, BDModulePilotAI vessel)
        {
            if (TracePool == null)
            {
                CreateObjectPool();
            }
            GameObject newTrace = TracePool.GetPooledObject();
            newTrace.transform.position = position;

            WayPointTracing NWP = newTrace.GetComponent<WayPointTracing>();
            NWP.Position = position;
            NWP.vessel = vessel;
            NWP.setupRenderer();
            newTrace.SetActive(true);
            WaypointFollowingStrategy.Ghosts.Add(NWP);
        }

        void Awake()
        {
            transform.parent = FlightGlobals.ActiveVessel.mainBody.transform; //FIXME need to update this to grab worldindex for non-kerbin spawns for custom track building
        }
        void Start()
        {
            setupRenderer(); //one linerenderer per vessel
            nodes = 0;
            timer = 0;
        }
        private void OnEnable()
        {
            disabled = false;
            setupRenderer();
            pathPoints.Clear();
            nodes = 0;
            timer = 0;
        }
        void setupRenderer()
        {
            if (tracerRenderer == null)
            {
                tracerRenderer = new LineRenderer();
            }
            Debug.Log("[WayPointTracer] setting up Renderer");
            Transform tf = this.transform;
            tracerRenderer = tf.gameObject.AddOrGetComponent<LineRenderer>();
            Color Color = BDTISetup.Instance.ColorAssignments[vessel.weaponManager.Team.Name]; //hence the incrementing teams in One-at-a-Time mode
            tracerRenderer.material = new Material(Shader.Find("KSP/Particles/Alpha Blended"));
            tracerRenderer.material.SetColor("_TintColor", Color);
            tracerRenderer.material.mainTexture = GameDatabase.Instance.GetTexture("BDArmory/Textures/laser", false);
            tracerRenderer.material.SetTextureScale("_MainTex", new Vector2(0.01f, 1));
            tracerRenderer.textureMode = LineTextureMode.Tile;
            tracerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; //= false;
            tracerRenderer.receiveShadows = false;
            tracerRenderer.startWidth = 5;
            tracerRenderer.endWidth = 5;
            tracerRenderer.positionCount = 2;
            tracerRenderer.SetPosition(0, Vector3.zero);
            tracerRenderer.SetPosition(1, Vector3.zero);
            tracerRenderer.useWorldSpace = false;
            tracerRenderer.enabled = false;
        }
        public void resetRenderer()
        {//reset things for ghost mode replay while the current vessel races
            if (tracerRenderer == null)
            {
                return;
            }
            Transform tf = this.transform;
            tracerRenderer = tf.gameObject.AddOrGetComponent<LineRenderer>();
            tracerRenderer.positionCount = 2;
            tracerRenderer.SetPosition(0, Vector3.zero);
            tracerRenderer.SetPosition(1, Vector3.zero);
            tracerRenderer.useWorldSpace = false;
            tracerRenderer.enabled = false;
            timer = 0;
            nodes = 0;
            replayGhost = true;
        }
        private float timer = 0;
        private int nodes = 0;
        void FixedUpdate()
        {
            if (HighLogic.LoadedSceneIsFlight)
            {
                if (BDArmorySetup.GameIsPaused) return;
                if (vessel.vessel == null) return;
                if (vessel.vessel.situation == Vessel.Situations.ORBITING || vessel.vessel.situation == Vessel.Situations.ESCAPING) return;

                if ((!replayGhost && vessel.GetWaypointIndex() > 0) || (replayGhost && WaypointFollowingStrategy.activePilots[0].GetWaypointIndex() > 0))
                {    //don't record before first WP
                    timer += Time.fixedDeltaTime;
                    if (timer > 1)
                    {
                        timer = 0;
                        nodes++;
                        //Vector3d WorldCoords = VectorUtils.GetWorldSurfacePostion(wm.Current.vessel.transform.position, FlightGlobals.currentMainBody);                       
                        if (!replayGhost)
                        {
                            pathPoints.Add(vessel.vessel.transform.position);
                        }

                        //if (BDArmorySettings.DRAW_VESSEL_TRAILS)
                        {
                            if (tracerRenderer == null)
                            {
                                setupRenderer();
                            }
                            tracerRenderer.enabled = true;
                            tracerRenderer.positionCount = nodes; //clamp count to elapsed time, for replaying ghosts from prior heats 
                            for (int i = 0; i < nodes - 1; i++)
                            {
                                tracerRenderer.SetPosition(i, pathPoints[i]); //add Linerender positions for all but last position
                            } //this is working, to a point, at which the render diverges from where the vessel has gone. Need a krakenbane offset?
                              //renderer was attached to a WayPointTrace class so positions would always remain consistant relative the tracer, not the ship
                        }
                    }
                    if (!replayGhost && nodes > 1) tracerRenderer.SetPosition(tracerRenderer.positionCount - 1, vessel.vessel.CoM); //have last position update real-time with vessel position
                }
                else
                {
                    if (tracerRenderer != null)
                    {
                        tracerRenderer.enabled = false;
                        tracerRenderer = null;
                    }
                }
            }
        }
    }
}

