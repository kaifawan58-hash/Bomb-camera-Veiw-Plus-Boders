// WorldControlMod.cs (extended)
// SHVDN v2/v3, .NET Framework 4.6, GTA V build 1.0.350.1 safe — all natives used are pre-2015.
//
// EVERYTHING BELOW IS SCRIPT-ONLY (no new models/assets needed):
//   1. 3 zones (City/Desert/Mountain) - own police model, weather, traffic/ped density, speed limit
//   2. Border checkpoints - barrier + guards + weapon check + BANNED VEHICLE check
//   3. Curfew system - night hours = more aggressive patrol + extra spawns
//   4. Speed radar - checkpoints that ticket/wanted-flag speeders
//   5. Auto-roadblock - when wanted level high, spawns a blocking cop car ahead on road
//   6. Post office parcel delivery loop - simple fetch-deliver stub mission
//   7. Config file (WorldControlConfig.txt) - tune zone boxes/settings without recompiling
//
// STILL NEEDS MAP EDITOR (not possible via script alone):
//   physical border wall/gate props, real post office building, custom interiors.

using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using GTA;
using GTA.Math;
using GTA.Native;

public class WorldControlMod : Script
{
    class Zone
    {
        public string Name;
        public float MinX, MaxX, MinY, MaxY;
        public string PoliceModel;
        public string PoliceVehicle;
        public string Weather = "CLEAR";
        public float TrafficMult = 1.0f;
        public float PedMult = 1.0f;
        public float SpeedLimitMps = 25f;
        public List<string> BannedVehicles = new List<string>();

        public bool Contains(Vector3 pos) =>
            pos.X >= MinX && pos.X <= MaxX && pos.Y >= MinY && pos.Y <= MaxY;
    }

    List<Zone> zones = new List<Zone>();
    string currentZone = "";

    class Checkpoint
    {
        public Vector3 Pos;
        public bool Spawned = false;
        public List<Ped> Guards = new List<Ped>();
        public Prop Barrier;
        public bool IsSpeedRadar = false;
        public float ZoneSpeedLimit = 25f;
    }
    List<Checkpoint> checkpoints = new List<Checkpoint>();

    Vector3 postOfficePos = new Vector3(100f, -800f, 30f);
    List<Vector3> parcelPoints = new List<Vector3>
    {
        new Vector3(150f, -700f, 30f),
        new Vector3(50f,  -900f, 30f),
    };
    int currentParcelIndex = -1;
    Blip postOfficeBlip;
    Blip parcelBlip;

    Random rnd = new Random();
    string configPath = "scripts\\WorldControlConfig.txt";

    public WorldControlMod()
    {
        LoadDefaultZonesAndCheckpoints();
        TryLoadConfigOverrides();

        Tick += OnTick;
        Interval = 300;

        postOfficeBlip = World.CreateBlip(postOfficePos);
        postOfficeBlip.Sprite = BlipSprite.PoliceStation;
        postOfficeBlip.Name = "Post Office";
    }

    void LoadDefaultZonesAndCheckpoints()
    {
        zones.Add(new Zone {
            Name="City", MinX=-1200,MaxX=1200,MinY=-1800,MaxY=800,
            PoliceModel="s_m_y_cop_01", PoliceVehicle="police",
            Weather="CLEAR", TrafficMult=1.2f, PedMult=1.2f, SpeedLimitMps=20f,
            BannedVehicles = new List<string>{ "rhino", "barracks" }
        });
        zones.Add(new Zone {
            Name="Desert", MinX=800,MaxX=3000,MinY=800,MaxY=3600,
            PoliceModel="s_m_y_sheriff_01", PoliceVehicle="sheriff",
            Weather="CLEARING", TrafficMult=0.6f, PedMult=0.4f, SpeedLimitMps=35f,
            BannedVehicles = new List<string>()
        });
        zones.Add(new Zone {
            Name="Mountains", MinX=-3000,MaxX=-1200,MinY=800,MaxY=4500,
            PoliceModel="s_m_y_ranger_01", PoliceVehicle="parkranger",
            Weather="SMOG", TrafficMult=0.3f, PedMult=0.2f, SpeedLimitMps=30f,
            BannedVehicles = new List<string>()
        });

        checkpoints.Add(new Checkpoint{ Pos=new Vector3(800f,800f,30f) });
        checkpoints.Add(new Checkpoint{ Pos=new Vector3(-1200f,800f,30f) });
        checkpoints.Add(new Checkpoint{ Pos=new Vector3(0f,-200f,30f), IsSpeedRadar=true, ZoneSpeedLimit=20f });
    }

    void TryLoadConfigOverrides()
    {
        try
        {
            if (!File.Exists(configPath)) return;
            foreach (var raw in File.ReadAllLines(configPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split('=');
                if (parts.Length != 2) continue;
                var keyParts = parts[0].Split('.');
                if (keyParts.Length != 2) continue;
                string zoneName = keyParts[0];
                string field = keyParts[1];
                string val = parts[1].Trim();

                var zone = zones.Find(z => z.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase));
                if (zone == null) continue;

                float f;
                switch (field)
                {
                    case "MinX": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) zone.MinX = f; break;
                    case "MaxX": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) zone.MaxX = f; break;
                    case "MinY": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) zone.MinY = f; break;
                    case "MaxY": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) zone.MaxY = f; break;
                    case "Weather": zone.Weather = val; break;
                    case "SpeedLimitMps": if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) zone.SpeedLimitMps = f; break;
                }
            }
        }
        catch (Exception ex)
        {
            UI.Notify("Config load error: " + ex.Message);
        }
    }

    void OnTick(object sender, EventArgs e)
    {
        var player = Game.Player.Character;
        var pos = player.Position;

        Zone zone = HandleZoneDetectionAndAmbience(pos);
        HandleZonePatrolSpawn(pos, zone);
        HandleCurfew(zone);
        HandleCheckpoints(pos, player, zone);
        HandleAutoRoadblock(player, pos);
        HandlePostOffice(pos, player);
    }

    Zone HandleZoneDetectionAndAmbience(Vector3 pos)
    {
        foreach (var z in zones)
        {
            if (z.Contains(pos))
            {
                if (currentZone != z.Name)
                {
                    currentZone = z.Name;
                    UI.Notify("Entering zone: " + z.Name);
                    Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, z.Weather);
                }

                Function.Call(Hash.SET_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, z.TrafficMult);
                Function.Call(Hash.SET_PED_DENSITY_MULTIPLIER_THIS_FRAME, z.PedMult);
                Function.Call(Hash.SET_RANDOM_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, z.TrafficMult);
                Function.Call(Hash.SET_PARKED_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, z.TrafficMult);

                return z;
            }
        }
        return null;
    }

    float lastPatrolSpawnCheck = 0f;
    void HandleZonePatrolSpawn(Vector3 pos, Zone z)
    {
        if (z == null) return;
        if (Game.GameTime - lastPatrolSpawnCheck < 15000) return;
        lastPatrolSpawnCheck = Game.GameTime;

        var nearby = World.GetNearbyPeds(pos, 80f);
        if (nearby.Length > 15) return;

        Vector3 spawnPos = pos + new Vector3(rnd.Next(-60, 60), rnd.Next(-60, 60), 0);
        spawnPos.Z = World.GetGroundHeight(spawnPos);

        Model pedModel = new Model(z.PoliceModel);
        Model vehModel = new Model(z.PoliceVehicle);
        pedModel.Request(1000);
        vehModel.Request(1000);

        if (pedModel.IsLoaded && vehModel.IsLoaded)
        {
            Vehicle v = World.CreateVehicle(vehModel, spawnPos);
            if (v != null)
            {
                Ped cop = v.CreatePedOnSeat(VehicleSeat.Driver, pedModel);
                if (cop != null)
                {
                    cop.Task.CruiseWithVehicle(v, 15f, DrivingStyle.Normal);
                    Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, cop, 0.3f);
                }
            }
        }
    }

    bool curfewActive = false;
    void HandleCurfew(Zone z)
    {
        if (z == null) return;
        var time = World.CurrentTimeOfDay;
        bool night = time.Hours >= 22 || time.Hours < 5;

        if (night && !curfewActive)
        {
            curfewActive = true;
            UI.Notify(z.Name + " curfew active: extra patrols, police more alert.");
        }
        else if (!night && curfewActive)
        {
            curfewActive = false;
            UI.Notify(z.Name + " curfew lifted.");
        }

        if (curfewActive)
        {
            if (rnd.NextDouble() < 0.02)
            {
                lastPatrolSpawnCheck = 0f;
            }
        }
    }

    void HandleCheckpoints(Vector3 pos, Ped player, Zone z)
    {
        foreach (var cp in checkpoints)
        {
            float dist = pos.DistanceTo(cp.Pos);

            if (dist < 150f && !cp.Spawned) SpawnCheckpoint(cp);
            if (dist < 8f && cp.Spawned)
            {
                if (cp.IsSpeedRadar) DoSpeedCheck(player, cp);
                else DoSecurityCheck(player, cp, z);
            }
            if (dist > 250f && cp.Spawned) CleanupCheckpoint(cp);
        }
    }

    void SpawnCheckpoint(Checkpoint cp)
    {
        cp.Spawned = true;

        if (!cp.IsSpeedRadar)
        {
            Model barrierModel = new Model("prop_roadwork_barrier02");
            barrierModel.Request(1000);
            if (barrierModel.IsLoaded)
                cp.Barrier = World.CreateProp(barrierModel, cp.Pos, false, false);

            for (int i = 0; i < 2; i++)
            {
                Model guardModel = new Model("s_m_y_cop_01");
                guardModel.Request(1000);
                if (guardModel.IsLoaded)
                {
                    Vector3 offset = cp.Pos + new Vector3(i * 2f, 0, 0);
                    Ped guard = World.CreatePed(guardModel, offset);
                    if (guard != null)
                    {
                        guard.Task.StandStill(-1);
                        cp.Guards.Add(guard);
                    }
                }
            }
        }
    }

    float lastCheckTime = 0f;
    void DoSecurityCheck(Ped player, Checkpoint cp, Zone z)
    {
        if (Game.GameTime - lastCheckTime < 8000) return;
        lastCheckTime = Game.GameTime;

        UI.Notify("Checkpoint: identification check in progress...");

        bool hasWeaponOut = player.Weapons.Current.Hash != WeaponHash.Unarmed;
        bool bannedVehicle = false;

        if (player.IsInVehicle() && z != null)
        {
            string vehModelName = player.CurrentVehicle.DisplayName.ToLower();
            bannedVehicle = z.BannedVehicles.Exists(b => vehModelName.Contains(b));
        }

        if (bannedVehicle)
        {
            UI.Notify("Security: vehicle type restricted in this zone — turn back or be flagged.");
            if (rnd.NextDouble() < 0.7)
            {
                Game.Player.Wanted.SetWantedLevel(2, false);
                Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
            }
        }
        else if (hasWeaponOut)
        {
            if (rnd.NextDouble() < 0.6)
            {
                UI.Notify("Security: weapon detected — you are now wanted!");
                Game.Player.Wanted.SetWantedLevel(2, false);
                Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
            }
            else
            {
                UI.Notify("Security: warning issued, holster your weapon.");
            }
        }
        else
        {
            UI.Notify("Checkpoint clear. Proceed.");
        }
    }

    float lastSpeedCheckTime = 0f;
    void DoSpeedCheck(Ped player, Checkpoint cp)
    {
        if (Game.GameTime - lastSpeedCheckTime < 5000) return;
        lastSpeedCheckTime = Game.GameTime;

        if (!player.IsInVehicle()) return;
        float speed = player.CurrentVehicle.Speed;

        if (speed > cp.ZoneSpeedLimit)
        {
            UI.Notify(string.Format("RADAR: speeding detected ({0:0} > {1:0} limit).", speed, cp.ZoneSpeedLimit));
            if (rnd.NextDouble() < 0.5)
            {
                Game.Player.Wanted.SetWantedLevel(1, false);
                Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
            }
        }
    }

    void CleanupCheckpoint(Checkpoint cp)
    {
        cp.Spawned = false;
        if (cp.Barrier != null && cp.Barrier.Exists()) cp.Barrier.Delete();
        foreach (var g in cp.Guards) if (g != null && g.Exists()) g.Delete();
        cp.Guards.Clear();
    }

    float lastRoadblockTime = 0f;
    void HandleAutoRoadblock(Ped player, Vector3 pos)
    {
        int wanted = Game.Player.Wanted.WantedLevel;
        if (wanted < 2) return;
        if (Game.GameTime - lastRoadblockTime < 20000) return;
        lastRoadblockTime = Game.GameTime;
        if (!player.IsInVehicle()) return;

        Vector3 forward = player.CurrentVehicle.ForwardVector;
        Vector3 aheadPos = pos + forward * 80f;

        OutputArgument nodePos = new OutputArgument();
        bool found = Function.Call<bool>(Hash.GET_CLOSEST_VEHICLE_NODE, aheadPos.X, aheadPos.Y, aheadPos.Z, nodePos, 1, 3.0, 0);
        if (!found) return;
        Vector3 roadPos = nodePos.GetResult<Vector3>();

        Model vehModel = new Model("police");
        Model pedModel = new Model("s_m_y_cop_01");
        vehModel.Request(1000);
        pedModel.Request(1000);
        if (vehModel.IsLoaded && pedModel.IsLoaded)
        {
            Vehicle block = World.CreateVehicle(vehModel, roadPos);
            if (block != null)
            {
                block.Heading = player.CurrentVehicle.Heading + 90f;
                Ped cop = block.CreatePedOnSeat(VehicleSeat.Driver, pedModel);
                if (cop != null)
                {
                    cop.Task.LeaveVehicle();
                    cop.Task.FightAgainst(player);
                }
                UI.Notify("Police roadblock ahead!");
            }
        }
    }

    bool postOfficePromptShown = false;
    void HandlePostOffice(Vector3 pos, Ped player)
    {
        float distOffice = pos.DistanceTo(postOfficePos);

        if (distOffice < 3f)
        {
            if (currentParcelIndex == -1)
            {
                if (!postOfficePromptShown)
                {
                    UI.Notify("Press E at Post Office to accept a delivery job");
                    postOfficePromptShown = true;
                }
                if (Game.IsControlJustPressed(GTA.Control.Context))
                {
                    currentParcelIndex = rnd.Next(parcelPoints.Count);
                    if (parcelBlip != null && parcelBlip.Exists()) parcelBlip.Remove();
                    parcelBlip = World.CreateBlip(parcelPoints[currentParcelIndex]);
                    parcelBlip.Sprite = BlipSprite.Package;
                    parcelBlip.Name = "Deliver Parcel";
                    UI.Notify("Delivery job started — follow the blip.");
                }
            }
            else
            {
                UI.Notify("Delivery already in progress — go to the marked point.");
            }
        }
        else
        {
            postOfficePromptShown = false;
        }

        if (currentParcelIndex != -1)
        {
            float distParcel = pos.DistanceTo(parcelPoints[currentParcelIndex]);
            if (distParcel < 3f)
            {
                UI.Notify("Parcel delivered! (stub — hook in reward logic here, e.g. cash/stat native)");
                if (parcelBlip != null && parcelBlip.Exists()) parcelBlip.Remove();
                currentParcelIndex = -1;
            }
        }
    }
}
