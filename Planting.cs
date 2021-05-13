using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Planting
{
    [BepInPlugin("holgero.Planting", "Planting", "0.1.0")]
    public class Planting : BaseUnityPlugin
    {
        private const float SafetyMargin = 0.05f;
        private static readonly bool isDebug = true;
        private static Planting context;
        private static GameObject plantingLine;

        public static ConfigEntry<bool> modEnabled;
        public static ConfigEntry<bool> snapPlantingPosition;
        public static ConfigEntry<bool> autoPlantLine;

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
            autoPlantLine = Config.Bind<bool>("General", "AutoPlant", true, "Automatically plant if current position is valid and in a row with two existing plants of the same sort");
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
            renderer.material = new Material(Shader.Find("Standard"));
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
                HidePlantingLine();
                if (___m_placementMarkerInstance != null && ___m_placementGhost?.GetComponent<Plant>() != null)
                {
                    if (GetPlacementStatus(__instance) != Player.PlacementStatus.Valid)
                    {
                        return;
                    }
                    Plant plant = ___m_placementGhost.GetComponent<Plant>();
                    var anchor = FindAnchor(plant.transform.position, 2.0f * plant.m_growRadius);
                    if (snapPlantingPosition.Value && anchor)
                    {
                        var nextPosition = FindSnapPoint(plant, anchor);
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
                    if (autoPlantLine.Value && anchor && anchor.GetComponent<Plant>())
                    {
                        var anchorPlant = anchor.GetComponent<Plant>();
                        // Dbgl($"autoplanting {plant}, have anchor plant {anchorPlant}");
                        if (NameEqualsIgnoringClone(plant.m_name, anchorPlant.m_name))
                        {
                            // Dbgl($"names match");
                            var positionOfPossibleSecondPlant = 2.0f * anchorPlant.transform.position - plant.transform.position;
                            var height = ZoneSystem.instance.GetGroundHeight(positionOfPossibleSecondPlant);
                            positionOfPossibleSecondPlant.y = height;
                            foreach (var thing in Physics.OverlapSphere(positionOfPossibleSecondPlant, 0.05f, LayerMask.GetMask("piece_nonsolid")))
                            {
                                var secondPlant = thing.GetComponent<Plant>();
                                // Dbgl($"found a secondPlant {secondPlant}");
                                if (secondPlant && NameEqualsIgnoringClone(plant.m_name, secondPlant.m_name))
                                {
                                    // Dbgl($"conditions met for autoplanting");
                                    Player player = __instance;
                                    ItemDrop.ItemData cultivator = GetCultivator(player);
                                    if (cultivator.m_durability <= 0f)
                                    {
                                        return;
                                    }
                                    float staminaUsage = cultivator.m_shared.m_attack.m_attackStamina;
                                    bool hasStamina = player.HaveStamina(staminaUsage);
                                    // Dbgl($"has {staminaUsage} stamina: {hasStamina}");
                                    if (hasStamina)
                                    {
                                        GameObject prefab = cultivator.m_shared?.m_buildPieces?.GetSelectedPrefab();
                                        Piece piece = prefab.GetComponent<Piece>();
                                        bool hasResources = player.HaveRequirements(piece, Player.RequirementMode.CanBuild);
                                        // Dbgl($"checked resource {piece} available for {prefab}: {hasResources}");

                                        if (hasResources)
                                        {
                                            var position = plant.transform.position;
                                            var plantHeight = ZoneSystem.instance.GetGroundHeight(position);
                                            position.y = height;
                                            UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
                                            player.ConsumeResources(piece.m_resources, 1);
                                            cultivator.m_durability -= cultivator.m_shared.m_useDurabilityDrain;
                                            player.UseStamina(staminaUsage);
                                        }
                                    }
                                    else
                                    {
                                        Hud.instance.StaminaBarNoStaminaFlash();
                                    }
                                }
                            }
                        }
                    }
                }
            }

            private static ItemDrop.ItemData GetCultivator(Player player)
            {
                return (ItemDrop.ItemData)typeof(Player).GetField("m_rightItem", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(player);
            }
        }

        private static bool NameEqualsIgnoringClone(String name1, String name2)
        {
            return name1.Replace("(Clone)", "").Equals(name2.Replace("(Clone)", ""));
        }

        private static Vector3 FindSnapPoint(Plant plant, Collider anchor)
        {
            if (Vector3.Distance(plant.transform.position, anchor.transform.position) < plant.m_growRadius)
            {
                return Vector3.zero;
            }
            Vector3 fromAnchor = plant.transform.position - anchor.transform.position;
            var angle = Vector3.SignedAngle(fromAnchor, Vector3.forward, Vector3.up);
            int closestSnap = (int)Math.Round(angle / 22.5f);
            float snapAngle = closestSnap * 22.5f;
            float snapLength = plant.m_growRadius + GetMaximumColliderRadius(plant);
            if (anchor.GetComponent<Plant>())
            {
                var otherPlant = anchor.GetComponent<Plant>();
                snapLength = Math.Max(plant.m_growRadius + GetMaximumColliderRadius(otherPlant), otherPlant.m_growRadius + GetMaximumColliderRadius(plant));
            }
            snapLength += SafetyMargin;
            Vector3 snappedFromAnchorPoint = new Vector3((float)(-Math.Sin(Math.PI / 180 * snapAngle) * snapLength), fromAnchor.y, (float)(Math.Cos(Math.PI / 180 * snapAngle) * snapLength));
            Vector3 snappedPosition = anchor.transform.position + snappedFromAnchorPoint;
            DrawAnchorLine(anchor.transform.position, snappedPosition);

            return snappedPosition;
        }

        private static void DrawAnchorLine(Vector3 start, Vector3 end)
        {
            if (!plantingLine)
            {
                InitPlantingLine();
            }
            // Dbgl($"draw from {start} to {end}");
            LineRenderer renderer = plantingLine.GetComponent<LineRenderer>();
            renderer.SetPositions(new Vector3[] { start + new Vector3(0, 0.2f, 0), end + new Vector3(0, 0.2f, 0) });
            renderer.enabled = true;
        }

        private static Collider FindAnchor(Vector3 position, float radius)
        {
            // search for the nearest plant within a given radius
            var plantsMask = LayerMask.GetMask(new string[] { "piece_nonsolid" });
            Collider anchor = null;
            float minDistance = 2 * radius;

            Collider[] nearThings = Physics.OverlapSphere(position, radius, plantsMask);
            foreach (var thing in nearThings)
            {
                Plant plant = thing.GetComponent<Plant>();
                if (plant)
                {
                    float distance = Vector3.Distance(plant.transform.position, position);
                    // Dbgl($"found a plant: {plant} in distance {distance}");
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        anchor = thing;
                    }
                }
            }
            if (anchor)
            {
                return anchor;
            }
            // no plant found, try to find other snap points on the layer piece_nonsolid: this should match the grown up plants (Pickable_XXX and Pickable_SeedXXX).
            foreach (var thing in nearThings)
            {
                float distance = Vector3.Distance(thing.transform.position, position);
                // Dbgl($"found a thing: {thing} in distance {distance}");
                if (distance < minDistance)
                {
                    minDistance = distance;
                    anchor = thing;
                }
            }
            return anchor;
        }

        private static void SetPlacementStatus(Player player, Player.PlacementStatus status)
        {   // one has to access the respective methods via reflection, it seems
            typeof(Player).GetField("m_placementStatus", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(player, status);
            typeof(Player).GetMethod("SetPlacementGhostValid", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(player, new object[] { status == Player.PlacementStatus.Valid });
        }

        private static Player.PlacementStatus GetPlacementStatus(Player player)
        {   // one has to access the respective methods via reflection, it seems
            return (Player.PlacementStatus)typeof(Player).GetField("m_placementStatus", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(player);
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

                    if (distance < plant.m_growRadius + GetMaximumColliderRadius(collidingPlant) + SafetyMargin/2.0f)
                    {
                        // Dbgl($"distance {distance} to other plant {collidingPlant} is too low for this plant");
                        return false;
                    }
                    if (distance < GetMaximumColliderRadius(plant) + collidingPlant.m_growRadius + SafetyMargin / 2.0f)
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