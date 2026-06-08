using BepInEx.Logging;
using HarmonyLib;
using Polytopia.Data;
using UnityEngine;

using Newtonsoft.Json.Linq;
using Il2CppSystem.Linq;

using pbb = PolytopiaBackendBase.Common;
using Steamworks.Data;
using MS.Internal.Xml.XPath;
using PolyMod;

using System;
using System.Reflection;
using System.Collections.Generic;

namespace Terratopia.Parser;
public static class Parse
{
    public static ManualLogSource modLogger;
    public static void Load(ManualLogSource logger)
    {
        modLogger = logger;
        Harmony.CreateAndPatchAll(typeof(Parse));
        logger.LogInfo("Parse Loaded!");
        Loader.AddPatchDataType("improvementData", typeof(ImprovementData.Type));
        //Loader.AddTypeHandler(typeof(ImprovementData.Type), HandleImprovements);
    }

    /*public static List<ModImprovementData> modImprovementDatas = new();

    static void HandleImprovements(JObject token, bool onCreatedEnumCache)
    {
        static ModImprovementData f() => new(); // PolibImprovementData Factory
        ParseUtils.ParseWithHandler<ImprovementData.Type, int[], ModImprovementData>(token, "customPopulation", modImprovementDatas, f);
    }*/

    public static Dictionary<ImprovementData.Type, int[]> customPopulation = new Dictionary<ImprovementData.Type, int[]>();
    
    [HarmonyPrefix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(GameLogicData), nameof(GameLogicData.AddGameLogicPlaceholders))]
    private static void GameLogicData_Parse(GameLogicData __instance, JObject rootObject)
    {
        ParseUtils.ParsePerEach(rootObject, "improvementData", "customPopulation", customPopulation);
    }
}