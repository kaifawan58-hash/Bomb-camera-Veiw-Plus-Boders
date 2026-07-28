// MenyooMapForceLoader.cs
// SHVDN v2/v3, .NET Framework 4.6, GTA V build 1.0.350.1 safe.
//
// WHAT THIS DOES:
// Generic loader for standard Menyoo "Object Spooner" XML map files.
// Reads placement data (model hash/name, position, rotation) from any .xml file
// built by Menyoo's map export, spawns the objects as permanent world props,
// and re-checks every few seconds so nothing despawns from distance streaming.
//
// This works on the standard Menyoo XML schema (Placement/Object nodes) —
// point it at any Menyoo xml map file you own, e.g.:
//   wall.xml, DUI_Checkpoint_ymap.xml, USCANADA1_1.xml, Paleto_Highway_DUI.xml, frontiere.xml
//
// INSTALL:
// 1. Put this .cs in your "scripts" folder.
// 2. Put your Menyoo .xml map file(s) in "scripts\MenyooMaps\" (create that folder).
// 3. List the filenames in the mapFiles array below.
// 4. Requires ScriptHookV + ScriptHookVDotNet.

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Globalization;
using GTA;
using GTA.Math;
using GTA.Native;

public class MenyooMapForceLoader : Script
{
    // list the Menyoo xml filenames you want force-loaded (relative to scripts\MenyooMaps\)
    string[] mapFiles = new string[]
    {
        "wall.xml",
        "DUI_Checkpoint_ymap.xml",
        "USCANADA1_1.xml",
        "Paleto_Highway_DUI.xml",
        "frontiere.xml",
    };

    class PlacedObject
    {
        public string ModelName;
        public Vector3 Pos;
        public Vector3 Rot;
        public Prop SpawnedProp;
    }

    List<PlacedObject> allObjects = new List<PlacedObject>();
    bool loaded = false;

    public MenyooMapForceLoader()
    {
        Tick += OnTick;
        Interval = 1000;
    }

    void OnTick(object sender, EventArgs e)
    {
        if (!loaded)
        {
            LoadAllMaps();
            loaded = true;
        }

        SpawnMissingObjects();
    }

    void LoadAllMaps()
    {
        string baseDir = "scripts\\MenyooMaps\\";
        foreach (var file in mapFiles)
        {
            string path = baseDir + file;
            if (!File.Exists(path))
            {
                UI.Notify("Map file not found: " + path);
                continue;
            }
            ParseMenyooXml(path);
        }
        UI.Notify("Loaded " + allObjects.Count + " placements from " + mapFiles.Length + " map file(s).");
    }

    // Parses standard Menyoo Object Spooner XML format:
    // <Placement> or <Object> nodes containing Model hash/name and Position/Rotation X,Y,Z
    void ParseMenyooXml(string path)
    {
        try
        {
            var doc = new XmlDocument();
            doc.Load(path);

            // Menyoo format varies slightly by version - check common node names
            var nodes = doc.SelectNodes("//Placement");
            if (nodes.Count == 0) nodes = doc.SelectNodes("//Object");

            foreach (XmlNode node in nodes)
            {
                string modelName = GetChildText(node, "ModelHash");
                if (string.IsNullOrEmpty(modelName)) modelName = GetChildText(node, "Model");

                var posNode = node.SelectSingleNode("Position");
                if (posNode == null || string.IsNullOrEmpty(modelName)) continue;

                float x = ParseFloatAttr(posNode, "X");
                float y = ParseFloatAttr(posNode, "Y");
                float z = ParseFloatAttr(posNode, "Z");

                var rotNode = node.SelectSingleNode("Rotation");
                float rx = 0, ry = 0, rz = 0;
                if (rotNode != null)
                {
                    rx = ParseFloatAttr(rotNode, "X");
                    ry = ParseFloatAttr(rotNode, "Y");
                    rz = ParseFloatAttr(rotNode, "Z");
                }

                allObjects.Add(new PlacedObject
                {
                    ModelName = modelName,
                    Pos = new Vector3(x, y, z),
                    Rot = new Vector3(rx, ry, rz)
                });
            }
        }
        catch (Exception ex)
        {
            UI.Notify("Error parsing " + path + ": " + ex.Message);
        }
    }

    string GetChildText(XmlNode node, string childName)
    {
        var child = node.SelectSingleNode(childName);
        return child != null ? child.InnerText.Trim() : null;
    }

    float ParseFloatAttr(XmlNode node, string attrName)
    {
        if (node.Attributes != null && node.Attributes[attrName] != null)
        {
            float.TryParse(node.Attributes[attrName].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result);
            return result;
        }
        // some Menyoo versions use child elements instead of attributes
        var child = node.SelectSingleNode(attrName);
        if (child != null)
        {
            float.TryParse(child.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out float result);
            return result;
        }
        return 0f;
    }

    // Every tick, check if any placed object's prop got unloaded/deleted and respawn it.
    // This is the "force permanent, never unload" behavior.
    void SpawnMissingObjects()
    {
        var playerPos = Game.Player.Character.Position;

        foreach (var obj in allObjects)
        {
            if (obj.SpawnedProp != null && obj.SpawnedProp.Exists()) continue; // already there

            // only actively (re)spawn objects reasonably close to avoid overloading world;
            // increase this radius if you want the whole map force-loaded regardless of distance
            if (obj.Pos.DistanceTo(playerPos) > 500f) continue;

            uint hash;
            if (!uint.TryParse(obj.ModelName, out hash))
                hash = (uint)Game.GenerateHash(obj.ModelName);

            Model model = new Model((int)hash);
            model.Request(500);
            if (model.IsLoaded)
            {
                Prop p = World.CreateProp(model, obj.Pos, obj.Rot, false, false);
                if (p != null)
                {
                    p.FreezePosition = true; // stop physics moving it, keeps map-editor placement exact
                    obj.SpawnedProp = p;
                }
            }
        }
    }
}
