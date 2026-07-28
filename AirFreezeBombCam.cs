// AirFreezeBombCam.cs
// SHVDN v2/v3, .NET Framework 4.6, GTA V build 1.0.350.1 safe.
//
// WHAT THIS DOES (combined, one file):
// - Press F6: toggle Air Freeze. Every airborne entity (jets, planes - yours and
//   enemy) within range gets frozen in place. Ground stays completely normal.
// - Bombs/rockets/missiles/grenades/projectiles are ALWAYS excluded from freeze,
//   so when you drop one it keeps falling normally even while jets are frozen.
// - As soon as a bomb is detected in the world, camera automatically switches to
//   live chase view following it through the air.
// - On impact: visible explosion + ground destruction damage at that point, camera
//   holds on the blast 2.5 seconds, then automatically returns to normal player view.
// - Press F6 again any time to unfreeze all jets/planes.
//
// INSTALL: drop in "scripts" folder. Needs ScriptHookV + ScriptHookVDotNet.

using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

public class AirFreezeBombCam : Script
{
    // ---------------- shared config ----------------
    string[] bombKeywords = new string[] { "bomb", "projectile", "grenade", "rocket", "missile" };
    float airHeightThreshold = 5.0f;

    // ---------------- air freeze state ----------------
    bool freezeActive = false;
    List<Entity> frozenEntities = new List<Entity>();

    // ---------------- bomb cam state ----------------
    Prop trackedBomb = null;
    Camera bombCamera = null;
    bool camActive = false;
    int impactTime = 0;

    public AirFreezeBombCam()
    {
        Tick += OnTick;
        KeyDown += OnKeyDown;
        Interval = 0; // run every frame - needed for smooth bomb cam movement
    }

    void OnKeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
    {
        if (e.KeyCode == System.Windows.Forms.Keys.F6)
        {
            freezeActive = !freezeActive;

            if (freezeActive)
            {
                UI.Notify("Air Freeze: ON");
            }
            else
            {
                foreach (var ent in frozenEntities)
                {
                    if (ent != null && ent.Exists())
                        ent.FreezePosition = false;
                }
                frozenEntities.Clear();
                UI.Notify("Air Freeze: OFF");
            }
        }
    }

    void OnTick(object sender, EventArgs e)
    {
        if (freezeActive) RunAirFreeze();
        RunBombCam(); // always watch for bombs, independent of freeze toggle
    }

    // ================= AIR FREEZE =================
    void RunAirFreeze()
    {
        var playerPos = Game.Player.Character.Position;

        foreach (Vehicle v in World.GetNearbyVehicles(playerPos, 500f))
            TryFreezeIfAirborne(v);

        foreach (Prop p in World.GetNearbyProps(playerPos, 500f))
            TryFreezeIfAirborne(p);

        foreach (Ped ped in World.GetNearbyPeds(playerPos, 500f))
            TryFreezeIfAirborne(ped);
    }

    void TryFreezeIfAirborne(Entity ent)
    {
        if (ent == null || !ent.Exists()) return;
        if (ent.FreezePosition) return;
        if (IsBombKeyword(ent)) return; // bombs never freeze - must keep falling

        float heightAboveGround = Function.Call<float>(Hash.GET_ENTITY_HEIGHT_ABOVE_GROUND, ent);
        if (heightAboveGround > airHeightThreshold)
        {
            ent.FreezePosition = true;
            frozenEntities.Add(ent);
        }
    }

    bool IsBombKeyword(Entity ent)
    {
        try
        {
            string name = ent.Model.ToString().ToLower();
            foreach (var kw in bombKeywords)
                if (name.Contains(kw)) return true;
        }
        catch { }
        return false;
    }

    // ================= BOMB CAM =================
    void RunBombCam()
    {
        if (trackedBomb == null)
        {
            FindNewBomb();
            return;
        }

        if (!trackedBomb.Exists())
        {
            ReleaseCamera();
            return;
        }

        UpdateCamera();
        CheckImpact();
    }

    void FindNewBomb()
    {
        var playerPos = Game.Player.Character.Position;
        foreach (Prop p in World.GetNearbyProps(playerPos, 500f))
        {
            if (p == null || !p.Exists()) continue;
            if (IsBombKeyword(p))
            {
                StartTracking(p);
                return;
            }
        }
    }

    void StartTracking(Prop bomb)
    {
        trackedBomb = bomb;
        bombCamera = World.CreateCamera(bomb.Position, Vector3.Zero, 50f);
        bombCamera.PointAt(bomb);
        World.RenderingCamera = bombCamera;
        camActive = true;
        UI.Notify("Bomb cam engaged.");
    }

    void UpdateCamera()
    {
        if (bombCamera == null || !camActive) return;

        Vector3 bombPos = trackedBomb.Position;
        Vector3 bombVelocity = trackedBomb.Velocity;

        Vector3 dir = bombVelocity;
        if (dir.Length() < 0.1f) dir = new Vector3(0, 0, -1);
        dir = dir.Normalized;

        Vector3 camOffset = new Vector3(-dir.X, -dir.Y, -dir.Z) * 8f + new Vector3(0, 0, 3f);
        bombCamera.Position = bombPos + camOffset;
        bombCamera.PointAt(trackedBomb);
    }

    void CheckImpact()
    {
        float heightAboveGround = Function.Call<float>(Hash.GET_ENTITY_HEIGHT_ABOVE_GROUND, trackedBomb);

        if (heightAboveGround < 0.5f && impactTime == 0)
        {
            Vector3 pos = trackedBomb.Position;
            Function.Call(Hash.ADD_EXPLOSION, pos.X, pos.Y, pos.Z, 2, 1.0f, true, false, 1.0f);
            impactTime = Game.GameTime;
            UI.Notify("Impact!");
        }

        if (impactTime != 0 && Game.GameTime - impactTime > 2500)
        {
            if (trackedBomb != null && trackedBomb.Exists()) trackedBomb.Delete();
            ReleaseCamera();
        }
    }

    void ReleaseCamera()
    {
        World.RenderingCamera = null;
        if (bombCamera != null && bombCamera.Exists()) bombCamera.Delete();
        bombCamera = null;
        trackedBomb = null;
        camActive = false;
        impactTime = 0;
    }
}
