using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.UI;
using System.Diagnostics.Contracts;

namespace Planting
{
    [BepInPlugin("holgero.Planting", "Planting", "0.1.0")]
    public class Planting : BaseUnityPlugin
    {
        private static readonly bool isDebug = true;
        private static Planting context;
        private static GameObject plantingLine;

        public static ConfigEntry<bool> modEnabled;
        public static ConfigEntry<bool> snapPlantingPosition;

        public static void Dbgl(string str = "", bool pref = true)
        {
            if (isDebug)
                Debug.Log((pref ? typeof(Planting).Namespace + " " : "") + str);
        }

        private void Awake()
        {
            context = this;

            modEnabled = Config.Bind<bool>("General", "Enabled", true, "Enable this mod");
            snapPlantingPosition = Config.Bind<bool>("General", "SnapPlanting", true, "Snap planting position to the next possible position");
            if (!modEnabled.Value)
                return;

            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), null);
        }

        private static void InitPlantingLine()
        {
            plantingLine = new GameObject("PlantingLine", typeof(LineRenderer));
            LineRenderer renderer = plantingLine.GetComponent<LineRenderer>();
            renderer.startColor = renderer.endColor = Color.grey;
            renderer.startWidth = renderer.endWidth = 0.02f;
            renderer.material = new Material(Shader.Find("Legacy Shaders/Particles/Alpha Blended"));
            renderer.positionCount = 2;
        }

        private static void HidePlantingLine()
        {
            if (plantingLine)
            {
                plantingLine.GetComponent<LineRenderer>().enabled = false;
            }
        }

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        static class Player_UpdatePlacementGhost_Patch
        {
            static void Postfix(Player __instance, ref GameObject ___m_placementGhost, GameObject ___m_placementMarkerInstance)
            {
                if (___m_placementMarkerInstance != null && ___m_placementGhost?.GetComponent<Plant>() != null)
                {
                    Plant plant = ___m_placementGhost.GetComponent<Plant>();
                    if (snapPlantingPosition.Value)
                    {
                        var currentPos = plant.transform.position;
                        var nextPosition = FindSnapPoint(currentPos, plant.m_growRadius);
                        if (!nextPosition.Equals(Vector3.zero))
                        {
                            plant.transform.position = nextPosition;
                        }
                    }
                    if (plant.m_needCultivatedGround && !Heightmap.FindHeightmap(plant.transform.position).IsCultivated(plant.transform.position))
                    {
                        SetPlacementStatus(__instance, Player.PlacementStatus.NeedCultivated);
                        return;
                    }
                    if (!HaveGrowSpace(plant))
                    {
                        SetPlacementStatus(__instance, Player.PlacementStatus.MoreSpace);
                        return;
                    }
                }
                else
                {
                    HidePlantingLine();
                }
            }
        }

        private static Vector3 FindSnapPoint(Vector3 position, float radius)
        {   // snapping should work as this:
            // if no plant in an radius of 2*growRadius -> free placement, no snapping at all
            Vector3 anchorPoint = FindAnchorPoint(position, 2*radius);
            // Dbgl($"got anchor {anchorPoint}");
            if (anchorPoint.Equals(Vector3.zero))
            {
                HidePlantingLine();
                return Vector3.zero;
            }
            // else if the distance between that plant and current intended placement position is < 0.5 growRadius, do nothing
            if (Vector3.Distance(position, anchorPoint) < radius/2.0f)
            {
                HidePlantingLine();
                return Vector3.zero;
            }
            // else draw a line from the other plant to the current intended placement position
            DrawAnchorLine(anchorPoint, position);
            Vector3 fromAnchor = position - anchorPoint;
            //      snap that line to an angle that is a multiple of 15 degrees with respect to the global grid
            //      along that line calculate the minimum distance between the two plants that is necessary for both to grow
            //      snap the placement position to that point on the line

            // improve case: there is a second plant behind the first one found: now only snap to multiples of 90 degrees w.r.t. that angle
            return Vector3.zero;
        }

        private static void DrawAnchorLine(Vector3 start, Vector3 end)
        {
            if (!plantingLine)
            {
                InitPlantingLine();
            }
            Dbgl($"draw from {start} to {end}");
            // Dbgl($"visible line is {visibleLine}");
            // Dbgl($"linerenderer is {visibleLine.GetComponent<LineRenderer>()}");
            LineRenderer renderer = plantingLine.GetComponent<LineRenderer>();
            renderer.SetPositions(new Vector3[] { start + new Vector3(0, 0.2f, 0), end + new Vector3(0, 0.2f, 0) });
            renderer.enabled = true;
        }

        private static Vector3 FindAnchorPoint(Vector3 position, float radius)
        {
            // search for the nearest plant within a given radius
            var plantsMask = LayerMask.GetMask(new string[] { "piece_nonsolid" });
            Vector3 anchorPosition = Vector3.zero;
            float minDistance = 2*radius;

            Collider[] nearThings = Physics.OverlapSphere(position, radius, plantsMask);
            foreach (var thing in nearThings)
            {
                Plant plant = thing.GetComponent<Plant>();
                if (plant)
                {
                    float distance = Vector3.Distance(plant.transform.position, position);
                    Dbgl($"found a plant: {plant} in distance {distance}");
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        anchorPosition = plant.transform.position;
                    }
                }
            }
            if (!anchorPosition.Equals(Vector3.zero))
            {
                return anchorPosition;
            }
            // no plant found, try to find other snap points on the layer piece_nonsolid: this should match the grown up plants (Pickable_XXX and Pickable_SeedXXX).
            foreach (var thing in nearThings)
            {
                float distance = Vector3.Distance(thing.transform.position, position);
                Dbgl($"found a thing: {thing} in distance {distance}");
                if (distance < minDistance)
                {
                    minDistance = distance;
                    anchorPosition = thing.transform.position;
                }
            }
            return anchorPosition;
        }

        private static void SetPlacementStatus(Player player, Player.PlacementStatus status)
        {   // one has to access the respective methods via reflection, it seems
            typeof(Player).GetField("m_placementStatus", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(player, status);
            typeof(Player).GetMethod("SetPlacementGhostValid", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(player, new object[] { status == Player.PlacementStatus.Valid });
        }

        private static bool HaveGrowSpace(Plant plant)
        {
            var spaceMask = LayerMask.GetMask(new string[] {
                "Default",
                "static_solid",
                "Default_small",
                "piece",
                "piece_nonsolid"
            });
            // fast check for things that will collide directly
            Collider[] array = Physics.OverlapSphere(plant.transform.position, plant.m_growRadius, spaceMask);
            for (int i = 0; i < array.Length; i++)
            {
                if (!array[i].GetComponent<Plant>() || array[i].GetComponent<Plant>() != plant)
                {
                    // Dbgl($"overlap with {array[i]}");
                    return false;
                }
            }
            // check possible collisions with other growing plants
            array = Physics.OverlapSphere(plant.transform.position, 2 * plant.m_growRadius, spaceMask);
            for (int i = 0; i < array.Length; i++)
            {
                Plant collidingPlant = array[i].GetComponent<Plant>();
                if (collidingPlant && collidingPlant != plant)
                {
                    float distance = Vector3.Distance(plant.transform.position, collidingPlant.transform.position);

                    if (distance < plant.m_growRadius + GetMaximumColliderRadius(collidingPlant))
                    {
                        // Dbgl($"distance {distance} to other plant {collidingPlant} is too low for this plant");
                        return false;
                    }
                    if (distance < GetMaximumColliderRadius(plant) + collidingPlant.m_growRadius)
                    {
                        // Dbgl($"distance {distance} to this plant {collidingPlant} is too low for other plant");
                        return false;
                    }
                }
            }
            return true;
        }

        private static float GetMaximumColliderRadius(Plant plant)
        {
            float maximumRadius = plant.GetComponent<CapsuleCollider>().radius;
            foreach (var prefab in plant.m_grownPrefabs)
            {
                CapsuleCollider grownCollider = prefab.GetComponent<CapsuleCollider>();
                if (grownCollider)
                {
                    // Dbgl($"previous maximum: {maximumRadius}, grown collider radius: {grownCollider.radius}");
                    maximumRadius = Math.Max(maximumRadius, grownCollider.radius);
                }
            }
            return maximumRadius;
        }
    }
}