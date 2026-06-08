using BepInEx.Logging;
using HarmonyLib;
using Polytopia.Data;
using UnityEngine;
using PolytopiaBackendBase.Common;
using Terratopia.Parser;


namespace Terratopia;

public static class Main
{
    public static ManualLogSource modLogger;
    public static void Load(ManualLogSource logger)
    {
        modLogger = logger;
        Harmony.CreateAndPatchAll(typeof(Main));
        logger.LogInfo("Main Loaded!");

    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.addStartingResourcesToCapital))]
    private static void MapGenerator_AddStartingResourcesToCapital(MapData map, GameState gameState, PlayerState player, Il2CppSystem.Collections.Generic.List<ResourceData> startingResources, int minResourcesCount)
    {
        TileData tile = gameState.Map.GetTile(player.startTile);
        PlayerState playerState;
        gameState.TryGetPlayer(player.Id, out playerState);
		gameState.ActionStack.Add(new IncreaseCurrencyAction(player.Id, tile.coordinates, 1000, 10));
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.AddTerrain))]
    private static void MapGenerator_AddTerrain(MapData map, List<PlayerState> playerStates, int version, MapGeneratorSettings settings, List<int> landTileIndices)
    {
        modLogger.LogInfo("AddTerrain called!");

        System.Random rand = new System.Random();

        const float START_CHANCE = 0.15f;           // Chance to start a new patch
        const float SPREAD_CHANCE = 0.85f;           // Chance to spread inside patch
        const int MIN_DISTANCE_BETWEEN_PATCHES = 2;  // Prevents overcrowding

        List<TileData> patchCenters = new List<TileData>();

        foreach (TileData tile in map.tiles)
        {
            modLogger.LogInfo("Attempting to spawn grassland!");
            
            // Only consider field tiles for patch centers
            if (tile.terrain != EnumCache<Polytopia.Data.TerrainData.Type>.GetType("field"))
                continue;
            modLogger.LogInfo("A valid field tile is found!");

            if (rand.NextDouble() < START_CHANCE)
            {
                CreateGrasslandPatch(map, tile, rand, SPREAD_CHANCE, patchCenters, MIN_DISTANCE_BETWEEN_PATCHES);
                patchCenters.Add(tile);
            }
        }
    }

    private static bool CreateGrasslandPatch(MapData map, TileData center, System.Random rand,
        float spreadChance, List<TileData> patchCenters, int minDistance)
    {
        // Check distance to other patches
        /*var cenPos = center.coordinates;
        foreach (TileData existing in patchCenters)
        {
            var exisPos = existing.coordinates;
            int dist = MapDataExtensions.ChebyshevDistance(cenPos, exisPos);
            if (dist < minDistance) return false;
            modLogger.LogInfo("Tile " + center + " is " + dist + " tiles away from patch center " + existing);
        }*/

        modLogger.LogInfo("A new grassland patch is attempted at " + center);
        modLogger.LogInfo("Current number of grassland patches: " + patchCenters.Count);

        // Place center
        PlaceGrassland(map, center, "center");

        // Spread to nearby tiles (3x3)
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<TileData> neighbors = 
        MapDataExtensions.GetTileNeighborsSorted(map, center.coordinates);
        
        foreach (TileData neighbor in neighbors)
        {
                // Only spread to valid field tiles
                if (neighbor.terrain != EnumCache<Polytopia.Data.TerrainData.Type>.GetType("field"))
                    continue;

                if (rand.NextDouble() < spreadChance)
                {
                    PlaceGrassland(map, neighbor, "spread");
                }
        }

        return true;
    }

    private static void PlaceGrassland(MapData map, TileData tile, string type)
    {
        if (tile == null) return;

        // Skip tiles too close to any village (3x3 area)
                var tilePos = tile.coordinates;
                WorldCoordinates cityPos = map.ClosestCity(tilePos, 0);
                var dist = MapDataExtensions.ChebyshevDistance(tilePos, cityPos);
                if (dist < 2) return;
        
        // Skip tiles in capital
        if (tile.owner != 0) return;

        // Set terrain to grassland
        tile.terrain = EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland");
        
        if (type == "center") {modLogger.LogInfo("A new grassland center is placed at " + tile);}
        if (type == "spread") {modLogger.LogInfo("A new grassland spread is placed at " + tile);}
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MoveAction), nameof(MoveAction.ExecuteDefault))]
    private static void MoveAction_ExecuteDefault(MoveAction __instance, GameState gameState)
    {
        WorldCoordinates targetCord = __instance.Path[0];
        TileData targetTile = gameState.Map.GetTile(targetCord);
        
        UnitState unitState;
	    PlayerState playerState;
	    UnitData unitData;

        // Disembark naval units on grassland tiles
	    if (gameState.TryGetUnit(__instance.UnitId, out unitState) && gameState.TryGetPlayer(__instance.PlayerId, out playerState) && gameState.GameLogicData.TryGetData(unitState.type, out unitData))
        if (targetTile.terrain == EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland") && unitData.IsVehicle())
        {
            gameState.ActionStack.Add(new DisembarkAction(__instance.PlayerId, targetCord));
        }

        // Heals unit that passes through a Stage Station on their path
        for (int j = 0; j < __instance.Path.Count; j++) {
            WorldCoordinates pathCord = __instance.Path[j];
            TileData tile = gameState.Map.GetTile(pathCord);
            
            // Heal 6 for units with at least 15 health cap, otherwise 4
            gameState.GameLogicData.TryGetData(unitState.type, out unitData);
	        var healAmount = unitData.health >= 15 ? 6 : 4;
            // Heal the unit if it passes through a Stage Station owned by the player
            if (tile.HasImprovement(EnumCache<ImprovementData.Type>.GetType("stagestation")) && tile.owner == __instance.PlayerId)
            {
                gameState.ActionStack.Add(new HealAction(__instance.PlayerId, targetCord, (ushort)healAmount));
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TerrainRenderer), nameof(TerrainRenderer.UpdateGraphics))]
    private static void TerrainRenderer_UpdateGraphics(TerrainRenderer __instance, Tile tile, TribeType climate, SkinType skin, bool shouldDesaturate)
    {
        SpriteAtlasManager.SpriteLookupResult spriteLookupResult = GameManager.GetSpriteAtlasManager().DoSpriteLookup("ground", climate, skin);
        
        TileData tileData = tile.Data;
        if (tileData.terrain != EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland"))
        return; // Only modify grassland tiles

        if (spriteLookupResult.HasSprite())
        {
            __instance.spriteRenderer.Sprite = spriteLookupResult.sprite;
        }
        if (shouldDesaturate != __instance.isDesaturated)
        {
            TerrainMaterialHelper.SetSpriteSaturated(__instance.spriteRenderer, shouldDesaturate);
            __instance.isDesaturated = shouldDesaturate;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Tile), nameof(Tile.RenderTerrain))]
    private static void Tile_RenderTerrain(Tile __instance, MapRenderContext ctx, SkinVisualsTransientData transientSkinningData)
    {
        bool num = __instance.Data.owner != 0 && __instance.Data.owner != ctx.localPlayerId;
        bool flag = num && !__instance.Data.IsWater && __instance.Data.terrain != Polytopia.Data.TerrainData.Type.Ice && !ctx.hideRenderBorders;
        
        if (__instance.Data.terrain != EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland"))
        return; // Only modify grassland tiles

        Sprite grasslandSprite = PolyMod.Registry.GetSprite("grassland"); // get the sprite

        __instance.forestRenderer.Sprite = grasslandSprite;
        modLogger.LogInfo("Rendering grassland tile at " + __instance.Data.coordinates);
         __instance.forestRenderer.gameObject.SetActive(true);
        TerrainMaterialHelper.SetSpriteSaturated(__instance.forestRenderer, flag);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameLogicData), nameof(GameLogicData.CanBuild))]
    private static void GameLogicData_CanBuild(ref bool __result, GameState gameState, TileData tile, PlayerState playerState, ImprovementData improvement)
    {
        // Allow Stage Station to be built only tile connected by road
        if (improvement.HasAbility(EnumCache<ImprovementAbility.Type>.GetType("stage")))
        {
            if (!tile.hasRoad)
            {
                __result = false;
                return;
            }
        }

        // Allow only one Ranch built on a single connected grassland patch from ALL players
        if (improvement.HasAbility(EnumCache<ImprovementAbility.Type>.GetType("frontier"))) {
            // Check connected grassland tiles for existing ranch
            Queue<TileData> toCheck = new Queue<TileData>();
            HashSet<TileData> visited = new HashSet<TileData>();
            toCheck.Enqueue(tile);
            while (toCheck.Count > 0)
            {
                TileData current = toCheck.Dequeue();
                visited.Add(current);

                if (current.HasImprovement(EnumCache<ImprovementData.Type>.GetType("ranch")))
                {
                    __result = false;
                    return;
                }

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<TileData> neighbors = 
                    MapDataExtensions.GetTileNeighborsSorted(gameState.Map, current.coordinates);
                foreach (TileData neighbor in neighbors)
                {
                    if (neighbor.terrain == EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland") && !visited.Contains(neighbor))
                    {
                        toCheck.Enqueue(neighbor);
                    }
                }
            }
        }

    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(BuildAction), nameof(BuildAction.ExecuteDefault))]
    private static void BuildAction_ExecuteDefault(BuildAction __instance, GameState gameState)
    {
        // Upgrade ranch level appropriately
        TileData tile = gameState.Map.GetTile(__instance.Coordinates);
	    
        if (tile.HasImprovement(EnumCache<ImprovementData.Type>.GetType("ranch")))
        {
            // Count connected grassland tiles
            int grasslandCount = 0;
            Queue<TileData> toCheck = new Queue<TileData>();
            HashSet<TileData> visited = new HashSet<TileData>();
            toCheck.Enqueue(tile);
            while (toCheck.Count > 0)
            {
                TileData current = toCheck.Dequeue();
                visited.Add(current);

                if (current.terrain == EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland"))
                {
                    grasslandCount++;
                }

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<TileData> neighbors = 
                    MapDataExtensions.GetTileNeighborsSorted(gameState.Map, current.coordinates);
                foreach (TileData neighbor in neighbors)
                {
                    if (!visited.Contains(neighbor) && neighbor.terrain == EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland"))
                    {
                        toCheck.Enqueue(neighbor);
                    }
                }
            }

            // Upgrade ranch if patch is larger than 1 tile / 3 tiles / 5 tiles
            modLogger.LogInfo("Grassland patch size: " + grasslandCount)
            if (grasslandCount >= 5)
                tile.improvement.level = 3;
                modLogger.LogInfo("Level 3 Ranch")
            else if (grasslandCount >= 3)
                tile.improvement.level = 2;
                modLogger.LogInfo("Level 2 Ranch")
            else
                tile.improvement.level = 1;
                modLogger.LogInfo("Level 1 Ranch")
            
            }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ImprovementExtensions), nameof(ImprovementExtensions.CalculateImprovementPopulationAtLevel),
        new Type[] { typeof(ImprovementData), typeof(int) }
    )]
    private static void ImprovementExtensions_CalculateImprovementPopulationAtLevel(ImprovementData improvementData, int level,
        ref int __result)
    {
        int num = (int)improvementData.GetPopulationReward();

        if (improvementData.maxLevel > 0 && level > 0)
        {
            modLogger.LogInfo("LevelRewardCalculation running!")
            foreach (GrowthRewards growthRewards in improvementData.growthRewards)
            {
                if (Parse.customPopulation.TryGetValue(improvementData.type, out var value))
                    modLogger.LogInfo("customPopulation found!")
                {
                    int arrayIndex = level - 1;
                    if (arrayIndex >= 0 && arrayIndex < value.Length)
                    {
                    modLogger.LogInfo(value[arrayIndex])
                        num += value[arrayIndex];
                    }
                }
                else
                {
                    modLogger.LogInfo("Population used!")
                    num += growthRewards.population * level;
                }
            }
        }

        __result = num;        // This replaces the original return value
    }
}