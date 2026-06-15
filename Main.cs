using BepInEx.Logging;
using HarmonyLib;
using Polytopia.Data;
using UnityEngine;
using PolytopiaBackendBase.Common;
using Terratopia.Parser;
using UnityEngine.Rendering.Universal.Internal;


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

    // For playtest
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
            // modLogger.LogInfo("Attempting to spawn grassland!");
            
            // Only consider field tiles for patch centers
            if (tile.terrain != EnumCache<Polytopia.Data.TerrainData.Type>.GetType("field"))
                continue;
            // modLogger.LogInfo("A valid field tile is found!");

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
        
        // Skip tiles in any empire
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
        // __instance.Path.Count-1 to exclude starting tile
        for (int j = 0; j < __instance.Path.Count-1; j++) {
            WorldCoordinates pathCord = __instance.Path[j];
            TileData tile = gameState.Map.GetTile(pathCord);
            
            // Heal 6 for units with at least 15 max health, otherwise 4
            var maxHealth = unitState.GetMaxHealth(gameState);
	        var healAmount = maxHealth >= 150 ? 60 : 40;
            // Heal the unit if it passes through a Stage Station owned by the player
            if (tile.HasImprovement(EnumCache<ImprovementData.Type>.GetType("stagestation")) && tile.owner == __instance.PlayerId)
            {
                if (unitState.health < maxHealth)
                {
                    gameState.ActionStack.Add(new HealAction(__instance.PlayerId, targetCord, (ushort)healAmount));
                }
                // Add 1 xp to the unit too
                unitState.xp += 1;            
            }
        }

        // A unit moving into 3x3 area of a unit with guard ability triggers guard retaliation
        TileData[] areaSorted = gameState.Map.GetAreaSorted(targetCord, 2, true, true);
		foreach (TileData tileData in areaSorted)
        {
            if (tileData.unit != null
                && tileData.unit.owner != __instance.PlayerId
                && tileData.unit.HasAbility(EnumCache<UnitAbility.Type>.GetType("guard")))
            {
                // Own damage calculation logic :D
                var guardBase = 20 + (tileData.unit.GetDefence(gameState) * 20); 
                var guardBonus = unitState.GetMaxHealth(gameState) >= 150 ? 1.5f : 1;
                int guardDamage = (int)Math.Round(Math.Min(guardBase * guardBonus, unitState.health) / 10);
                gameState.ActionStack.Add(new AttackAction(__instance.PlayerId, tileData.coordinates, targetCord, guardDamage, false, AttackAction.AnimationType.Normal, 100));
            }
        }
    }

    // Thanks for klipi
    // A custom terrain that sits on field tile needs this to render the field sprite
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

    // Thanks for klipi
    // A convenient method to render the custom terrain sprite on the field tile
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
    private static void GameLogicData_CanBuild(GameLogicData __instance, GameState gameState, TileData tile, PlayerState playerState, ImprovementData improvement, ref bool __result)
    {
        if (tile.improvement != null) 
        {
            __result = false;
            return;
        }
        // Allow Stage Station to be built on only tile with road and connected to city
        if (improvement.HasAbility(EnumCache<ImprovementAbility.Type>.GetType("stage")))
        {
        // Check connected road until a city is found
        Queue<TileData> toCheck = new Queue<TileData>();
        HashSet<TileData> visited = new HashSet<TileData>();
        toCheck.Enqueue(tile);
            while (toCheck.Count > 0)
            {
                TileData current = toCheck.Dequeue();
                visited.Add(current);

                // Must be built on grassland with road (tile)
                // This custom logic is beyond vanilla logics so no use vanilla methods etc. MeetsRequirement
                if (tile.hasRoad && tile.terrain == EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland"))
                {
                    // And connected to a city of the same owner (current)
                    if (current.HasImprovement(EnumCache<ImprovementData.Type>.GetType("city")) && current.owner == tile.owner)
                    {
                        __result = true;
                        return;
                    }
                }

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<TileData> neighbors = 
                    MapDataExtensions.GetTileNeighborsSorted(gameState.Map, current.coordinates);
                foreach (TileData neighbor in neighbors)
                {
                    if ((neighbor.hasRoad || neighbor.HasImprovement(EnumCache<ImprovementData.Type>.GetType("city"))) && !visited.Contains(neighbor))
                    {
                        toCheck.Enqueue(neighbor);
                    }
                }
            }
        __result = false;
        return;
        }

        // Allow only one Ranch built on a single connected grassland patch from ALL players
        // "frontier" = allow only player to build this in a specific area
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

        // Allow herd only when there is grassland within city area
        if (improvement.type == EnumCache<ImprovementData.Type>.GetType("herding"))
        {
            __result = false;
            // Check if same city area has grassland tile
            if (gameState.GameLogicData.TryGetData(playerState.tribe, out TribeData tribeData))
            {
            // Calls original requirement function check
            if (tile.rulingCityCoordinates != new WorldCoordinates(-1, -1)
                    && tile.owner == playerState.Id
                    && __instance.MeetsRequirement(tile, improvement, playerState, gameState)
                    && __instance.MeetsAdjacencyRequirement(gameState.Map, tile, improvement.adjacencyRequirements))
                {
                TileData cityTile = gameState.Map.GetTile(tile.rulingCityCoordinates);             
                Il2CppSystem.Collections.Generic.List<TileData> cityAreaSorted = ActionUtils.GetCityAreaSorted(gameState, cityTile);
                for (int j = 0; j < cityAreaSorted.Count; j++)
                {
                    TileData tileData2 = cityAreaSorted[j];
                    if (tileData2.terrain == EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland")
                        && tileData2.improvement == null && tileData2.resource == null)
                    {
                        __result = true;
                        break;
                    }
                }
                }
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BuildAction), nameof(BuildAction.ExecuteDefault))]
    private static void BuildAction_ExecuteDefault(BuildAction __instance, GameState gameState)
    {
        TileData tile = gameState.Map.GetTile(__instance.Coordinates);
        ImprovementData improvementData;
        PlayerState playerState;

	    if (tile != null && gameState.GameLogicData.TryGetData(__instance.Type, out improvementData) && gameState.TryGetPlayer(__instance.PlayerId, out playerState))
        {   
            // Call the level updating function to update population reward (up/down) for city
            // For improvements that use custom population reward logics (etc.lord)
            if (improvementData.HasAbility(EnumCache<ImprovementAbility.Type>.GetType("lord")))
            {
                ActionUtils.UpdateImprovementLevel(gameState, __instance.PlayerId, tile);
            }
            // Herd ability relocates game to nearest grassland within city and turns it into a livestock resource
            if (improvementData.type == EnumCache<ImprovementData.Type>.GetType("herding"))
            {
            if (tile.rulingCityCoordinates != new WorldCoordinates(-1, -1))
                {             
                TileData cityTile = gameState.Map.GetTile(tile.rulingCityCoordinates);
                // Check if same city area has grassland tile
                
                    Il2CppSystem.Collections.Generic.List<TileData> cityAreaSorted = ActionUtils.GetCityAreaSorted(gameState, cityTile);
                    for (int j = 0; j < cityAreaSorted.Count; j++)
                    {
                        TileData tileData2 = cityAreaSorted[j];
                        if (tileData2.terrain == EnumCache<Polytopia.Data.TerrainData.Type>.GetType("grassland")
                            && tileData2.improvement == null && tileData2.resource == null)
                        {
                            tileData2.resource = new ResourceState
                            {
                                type = EnumCache<ResourceData.Type>.GetType("livestock")
                            };
                            break; // only place livestock once for each game herded
                        }
                    }
                }
            }
            if (improvementData.type == EnumCache<ImprovementData.Type>.GetType("slaughter"))
            {
                tile.unit.xp += 2;
            }
        }
    }

[HarmonyPrefix]
[HarmonyPatch(typeof(ActionUtils), nameof(ActionUtils.CalculateImprovementLevel))]
private static bool ActionUtils_CalculateImprovementLevel(GameState gameState, TileData tile, ref int __result)
{
    // Early exit - only handle improvements with "lord" ability
    if (!gameState.GameLogicData.TryGetData(tile.improvement.type, out ImprovementData improvementData))
        return true; // let original method run

    if (!improvementData.HasAbility(EnumCache<ImprovementAbility.Type>.GetType("lord")))
        return true; // let original method run

    // === Territory-based leveling ===
    if (Parse.territory.TryGetValue(tile.improvement.type, out var territoryReq) &&
        territoryReq != null && territoryReq.Length > 0)
    {
        // Count connected tiles of the required terrain type
        int territoryCount = 0;

        foreach (TerrainRequirements terrainRequirements in improvementData.terrainRequirements)
        {
            string typeName = terrainRequirements.terrain.type.GetName();
            Queue<TileData> toCheck = new Queue<TileData>();
            HashSet<TileData> visited = new HashSet<TileData>();

            toCheck.Enqueue(tile);

            while (toCheck.Count > 0)
            {
                TileData current = toCheck.Dequeue();
                if (!visited.Add(current)) continue;

                if (current.terrain == EnumCache<Polytopia.Data.TerrainData.Type>.GetType(typeName))
                {
                    territoryCount++;
                }

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<TileData> neighbors =
                    MapDataExtensions.GetTileNeighborsSorted(gameState.Map, current.coordinates);

                foreach (TileData neighbor in neighbors)
                {
                    if (!visited.Contains(neighbor) &&
                        neighbor.terrain == EnumCache<Polytopia.Data.TerrainData.Type>.GetType(typeName))
                    {
                        toCheck.Enqueue(neighbor);
                    }
                }
            }
        }

        // Calculate desired level
        int newLevel = 0;
        for (int i = 1; i <= territoryReq.Length; i++)
        {
            if (territoryCount < territoryReq[i - 1])
                break;
            newLevel = i;
        }

        int currentLevel = tile.improvement.level;

        // Only process if level actually needs to change
        if (newLevel != currentLevel)
        {
            if (Parse.customPopulation.TryGetValue(improvementData.type, out var popAtLevel) &&
                popAtLevel != null && popAtLevel.Length > 0)
            {
                int oldPop = (currentLevel > 0 && currentLevel <= popAtLevel.Length) ? popAtLevel[currentLevel - 1] : 0;
                int newPop = (newLevel > 0 && newLevel <= popAtLevel.Length) ? popAtLevel[newLevel - 1] : 0;

                int delta = newPop - oldPop;

                modLogger.LogInfo($"Lord improvement level changed: {currentLevel} → {newLevel} | Territory: {territoryCount} | Pop delta: {delta}");

                if (delta > 0)
                {
                    modLogger.LogInfo($"Adding {delta} population");
                    for (int j = 0; j < delta; j++)
                    {
                        gameState.ActionStack.Add(new IncreasePopulationAction(tile.owner, tile.coordinates, tile.rulingCityCoordinates, 60));
                    }
                }
                else if (delta < 0)
                {
                    modLogger.LogInfo($"Removing {-delta} population");
                    for (int j = 0; j < -delta; j++)
                    {
                        gameState.ActionStack.Add(new DecreasePopulationAction(tile.owner, tile.rulingCityCoordinates, 200));
                    }
                }
            }

            // Update level
            tile.improvement.level = (ushort)newLevel;
            modLogger.LogInfo("Final level prefix: " + tile.improvement.level);
            // tile.improvement.level += 1;
            // modLogger.LogInfo("Final level postfix: " + tile.improvement.level);
        }
    }

    __result = tile.improvement.level;

    // IMPORTANT: Return false to SKIP the original method
    return false;
}

    // Required for destroying an improvement using customPopulation
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ImprovementExtensions), nameof(ImprovementExtensions.CalculateImprovementPopulationAtLevel),
        new Type[] { typeof(ImprovementData), typeof(int) })]
    private static void ImprovementExtensions_CalculateImprovementPopulationAtLevel(ImprovementData improvementData, int level,
        ref int __result)
    {
        int num = (int)improvementData.GetPopulationReward();

        if (improvementData.maxLevel > 0 && level > 0)
        {
            modLogger.LogInfo("CalculateImprovementPopulationAtLevel running...");
                if (Parse.customPopulation.TryGetValue(improvementData.type, out var value))
                {
                    modLogger.LogInfo("customPopulation is located!");
                    int arrayIndex = level - 1;
                    if (arrayIndex >= 0 && arrayIndex < value.Length)
                    {
                    modLogger.LogInfo(value[arrayIndex]);
                        num += value[arrayIndex];
                    }
                }
                else
                {
                    modLogger.LogInfo("Population is located!");
                    foreach (GrowthRewards growthRewards in improvementData.growthRewards)
                    {
                        num += growthRewards.population * level;
                    }
                }
        }
        __result = num;        // This replaces the original return value
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TrainCommand), nameof(TrainCommand.IsValid))]
    private static void TrainCommand_IsValid(TrainCommand __instance, GameState state, ref bool __result, out string validationError)
    {
        if (!__instance.PassesBasicValidation(state, out validationError))
		{
			__result = false;
            return;
		}
        TileData tile = state.Map.GetTile(__instance.Coordinates);
        if (tile.HasImprovement(EnumCache<ImprovementData.Type>.GetType("ranch"))
            && tile.owner == __instance.PlayerId
            && tile.unit == null)
		{
            UnitData unitData;
            if (state.GameLogicData.TryGetData(__instance.Type, out unitData))
            {
                if (unitData.HasAbility(EnumCache<UnitAbility.Type>.GetType("mercenary")))
                {
                    bool flag = false;
                    for (int j = 0; j < state.Map.Tiles.Length; j++)
                    {
                        TileData tileData = state.Map.Tiles[j];
                        if (tileData.unit != null && tileData.unit.owner == __instance.PlayerId && tileData.unit.type == unitData.type)
                        {
                            flag = true;
                            break;
                        }
                    }
                    if (flag)
                    {
                        validationError = "Unique unit already trained by the player.";
                        __result = false;
                        return;
                    } else {
                        validationError = "Overrided by Ranch training logics.";
                        __result = true;
                        return;                        
                    }
                }
            }
		}
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CommandUtils), nameof(CommandUtils.GetTrainableUnits))]
    private static void CommandUtils_GetTrainableUnits(GameState gameState, PlayerState player, TileData tile, ref Il2CppSystem.Collections.Generic.List<TrainCommand> __result, bool includeUnavailable = false)
    {
        ImprovementData improvementData;
		Il2CppSystem.Collections.Generic.List<TrainCommand> list = new Il2CppSystem.Collections.Generic.List<TrainCommand>();
        if (tile.improvement != null
            && gameState.GameLogicData.TryGetData(tile.improvement.type, out improvementData)
            && tile.HasImprovement(EnumCache<ImprovementData.Type>.GetType("ranch")))
            {
                modLogger.LogInfo("Ranch is located for training!");
                /*foreach (UnitData unitData in gameState.GameLogicData.)
                {
                    if (unitData.HasAbility(EnumCache<UnitAbility.Type>.GetType("mercenary")) && CommandValidation.HasUnitTerrain(gameState, tile.coordinates, unitData))
                    {
                        TrainCommand trainCommand = new TrainCommand(player.Id, unitData.type, tile.coordinates);
                        if (!player.blockTrainUnits && (includeUnavailable || trainCommand.IsValid(gameState)))
                        {
                            list.Add(trainCommand);
                        }
                    }
                }*/
			    UnitData unitData;
                List<UnitData> units = new List<UnitData>();
                gameState.GameLogicData.TryGetData(EnumCache<UnitData.Type>.GetType("grassrider"), out unitData);
                units.Add(unitData);
                gameState.GameLogicData.TryGetData(EnumCache<UnitData.Type>.GetType("sentinel"), out unitData);
                units.Add(unitData);
                foreach (UnitData unit in units)
                {
                    TrainCommand trainCommand = new TrainCommand(player.Id, unit.type, tile.coordinates);
                            if (!player.blockTrainUnits && (includeUnavailable || trainCommand.IsValid(gameState)))
                            {
                                list.Add(trainCommand);
                                modLogger.LogInfo($"Added {unit.type} to train list on {tile.improvement.type}");
                            }
                }
                __result = list;
                return;
            }
    }
}