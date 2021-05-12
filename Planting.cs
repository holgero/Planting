using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Planting
{
    [BepInPlugin("holgero.Planting", "Planting", "0.1.0")]
    public class Planting : BaseUnityPlugin
    {
        private static readonly bool isDebug = true;
        private static Planting context;

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

        [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
        static class Player_UpdatePlacementGhost_Patch
        {
            static void Postfix(Player __instance, ref GameObject ___m_placementGhost, GameObject ___m_placementMarkerInstance)
            {
                if (___m_placementMarkerInstance != null && ___m_placementGhost?.GetComponent<Plant>() != null)
                {
                    Plant plant = ___m_placementGhost.GetComponent<Plant>();
                    if (snapPlantingPosition.Value)
                    {   // snapping should work as this:
                        // if no plant in an radius of 2*growRadius -> free placement, no snapping at all
                        // else if the distance between that plant and current intended placement position is < 0.5 growRadius, do nothing
                        // else draw a line from the other plant to the current intended placement position
                        //      snap that line to an angle that is a multiple of 15 degrees with respect to the global grid
                        //      along that line calculate the minimum distance between the two plants that is necessary for both to grow
                        //      snap the placement position to that point on the line
                        var currentPos = plant.transform.position;
                    }
                    if (plant.m_needCultivatedGround && !Heightmap.FindHeightmap(plant.transform.position).IsCultivated(plant.transform.position))
                    {
                        setPlacementStatus(__instance, Player.PlacementStatus.NeedCultivated);
                        return;
                    }
                    if (!HaveGrowSpace(plant))
                    {
                        setPlacementStatus(__instance, Player.PlacementStatus.MoreSpace);
                        return;
                    }
                }
            }
            static void setPlacementStatus(Player player, Player.PlacementStatus status)
            {   // one has to access the respective methods via reflection, it seems
                typeof(Player).GetField("m_placementStatus", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(player, status);
                typeof(Player).GetMethod("SetPlacementGhostValid", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(player, new object[] { status == Player.PlacementStatus.Valid });
            }
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
                    maximumRadius =  Math.Max(maximumRadius, grownCollider.radius);
                }
            }
            return maximumRadius;
        }
    }
}