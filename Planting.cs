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

        public static void Dbgl(string str = "", bool pref = true)
        {
            if (isDebug)
                Debug.Log((pref ? typeof(Planting).Namespace + " " : "") + str);
        }

        private void Awake()
        {
            context = this;

            modEnabled = Config.Bind<bool>("General", "Enabled", true, "Enable this mod");

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
                    if (!HaveGrowSpace(plant))
                    {
                        typeof(Player).GetField("m_placementStatus", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, 5);
                        typeof(Player).GetMethod("SetPlacementGhostValid", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(__instance, new object[] { false });
                    }
                }
            }
        }

        private static bool HaveGrowSpace(Plant plant)
        {
            if (plant.m_needCultivatedGround && !Heightmap.FindHeightmap(plant.transform.position).IsCultivated(plant.transform.position))
                return false;

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
            CapsuleCollider grownCollider = plant.m_grownPrefabs[0].GetComponent<CapsuleCollider>();
            if (grownCollider)
            {   // grown plant might be bigger or smaller than the sapling
                return Math.Max(grownCollider.radius, plant.GetComponent<CapsuleCollider>().radius);
            }
            return plant.GetComponent<CapsuleCollider>().radius;
        }
    }
}