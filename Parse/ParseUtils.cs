using BepInEx.Logging;
using HarmonyLib;
using Polytopia.Data;
using Newtonsoft.Json.Linq;
using Il2CppSystem.Linq;
using UnityEngine;

using Il2Gen = Il2CppSystem.Collections.Generic;
using pbb = PolytopiaBackendBase.Common;
using Terratopia.Parser;
using Scriban;


namespace Terratopia;

public static class ParseUtils
{
    public static ManualLogSource modLogger;
    public static void Load(ManualLogSource logger)
    {
        modLogger = logger;
        Harmony.CreateAndPatchAll(typeof(Parse));
        logger.LogInfo("ParseUtils Loaded!");
    }

    public static void ParseToDictWithHandler<targetType, VT, PDataType>(JObject token, string fieldName, List<PDataType> list, Func<PDataType> factory)
    where targetType : struct, System.IConvertible
    {
        if (token[fieldName] != null)
        {
            var jt = token[fieldName].TryCast<JObject>();
            if (jt != null)
            {
                Dictionary<string, VT> dict = new Dictionary<string, VT>();

                foreach (JProperty property in jt.Properties().ToList())
                {
                    VT value = property.Value.ToObject<VT>();
                    dict[property.Name] = value;
                }

                if (EnumCache<targetType>.TryGetType(token.Path.Split('.').Last(), out var type))
                {
                    int idx = ModData.FindData<PDataType, targetType>(list, type);

                    if (idx == -1)
                    {
                        PDataType newone = factory();
                        list.Add(newone);
                        ModData.OverrideField(list, "type", list.Count - 1, type);
                        idx = list.Count - 1;
                    }
                    ModData.OverrideField(list, fieldName, idx, dict);
                }
            }
        }
    }
    public static void ParseWithHandler<targetType, T, PDataType>(JObject token, string fieldName, List<PDataType> list, Func<PDataType> factory)
    where targetType : struct, System.IConvertible
    {
        if (token[fieldName] != null)
        {
            T value = token[fieldName].ToObject<T>();
            if (EnumCache<targetType>.TryGetType(token.Path.Split('.').Last(), out var type))
            {
                int idx = ModData.FindData<PDataType, targetType>(list, type);
                if (idx >= 0)
                {
                    ModData.OverrideField<PDataType, T>(list, fieldName, idx, value);
                    Main.modLogger.LogInfo($"Added to existing class in list: {type.ToString()} because of value {value} in field {fieldName}");
                }
                else
                {
                    PDataType newone = factory();
                    list.Add(newone);
                    ModData.OverrideField<PDataType, targetType>(list, "type", list.Count - 1, type);
                    ModData.OverrideField<PDataType, T>(list, fieldName, list.Count - 1, value);
                    Main.modLogger.LogInfo($"Added a new class to list: {type.ToString()} because of value {value} in field {fieldName}");
                }
                token.Remove(fieldName);

            }
        }
    }

// ==================== Flexible & IL2CPP-safe ParsePerEach ====================
    public static void ParsePerEach<targetType, T>(
        JObject rootObject,
        string categoryName,
        string fieldName,
        Dictionary<targetType, T> dict,
        string[]? nestedContainers = null)
        where targetType : struct, System.IConvertible
    {
        modLogger.LogInfo($"ParsePerEach: Looking for {categoryName}.{fieldName}");

        // Safest way to get tokens without foreach
        var tokenEnumerable = rootObject.SelectTokens($"$.{categoryName}.*");
        var tokens = new List<JToken>();
        for (int i = 0; ; i++)
        {
            try
            {
                JToken? t = tokenEnumerable.ElementAt(i);
                if (t == null) break;
                tokens.Add(t);
            }
            catch { break; }
        }

        modLogger.LogInfo($"Found {tokens.Count} entries in {categoryName}");

        for (int i = 0; i < tokens.Count; i++)
        {
            JObject? token = tokens[i].TryCast<JObject>();
            if (token == null) continue;

            string name = token.Path.Split('.').Last();
            modLogger.LogInfo($"  Checking improvement: {name}");

            if (!EnumCache<targetType>.TryGetType(name, out var type))
            {
                modLogger.LogInfo($"    → EnumCache failed for {name}");
                continue;
            }

            modLogger.LogInfo($"    → Enum resolved: {type}");

            T? value = default;

            // Top level
            if (TryExtractAndRemove(token, fieldName, out value))
            {
                if (value != null)
                {
                    dict[type] = value;
                    modLogger.LogInfo($"    SUCCESS (top-level) for {type}");
                }
                continue;
            }

            // Nested
            if (nestedContainers != null)
            {
                for (int c = 0; c < nestedContainers.Length; c++)
                {
                    string container = nestedContainers[c];
                    modLogger.LogInfo($"    Checking nested container: {container}");

                    if (TryFindInNested(token, container, fieldName, out value))
                    {
                        if (value != null)
                        {
                            dict[type] = value;
                            modLogger.LogInfo($"    ✅ SUCCESS! Parsed {fieldName} for {type} = {value}");
                        }
                        break;
                    }
                }
            }
        }

        modLogger.LogInfo($"ParsePerEach finished. Total entries in dict: {dict.Count}");
    }

    private static bool TryExtractAndRemove<TVal>(JObject token, string fieldName, out TVal? value)
    {
        value = default;
        JToken? fieldToken = token[fieldName];
        if (fieldToken == null) return false;

        value = fieldToken.ToObject<TVal>();
        token.Remove(fieldName);
        return true;
    }

    private static bool TryFindInNested<TVal>(JObject token, string containerName, string fieldName, out TVal? value)
    {
        value = default;
        JToken? container = token[containerName];
        if (container == null || container.Type != JTokenType.Array) 
            return false;

        JArray? array = container.TryCast<JArray>();
        if (array == null) return false;

        modLogger.LogInfo($"      JArray Count = {array.Count}");

        for (int j = 0; j < array.Count; j++)
        {
            JObject? obj = array[j]?.TryCast<JObject>();
            if (obj == null) continue;

            JToken? customField = obj[fieldName];
            if (customField == null) continue;

            modLogger.LogInfo($"        Found '{fieldName}' of type {customField.Type}");

            try
            {
                // === Handle Arrays (int[], float[], string[], etc.) ===
                if (typeof(TVal).IsArray)
                {
                    JArray? jarr = customField.TryCast<JArray>();
                    if (jarr != null)
                    {
                        // Simple manual conversion for common array types
                        if (typeof(TVal) == typeof(int[]))
                        {
                            int[] result = new int[jarr.Count];
                            for (int k = 0; k < jarr.Count; k++)
                                result[k] = jarr[k].ToObject<int>();
                            value = (TVal)(object)result;
                        }
                        else if (typeof(TVal) == typeof(float[]))
                        {
                            float[] result = new float[jarr.Count];
                            for (int k = 0; k < jarr.Count; k++)
                                result[k] = jarr[k].ToObject<float>();
                            value = (TVal)(object)result;
                        }
                        else if (typeof(TVal) == typeof(string[]))
                        {
                            string[] result = new string[jarr.Count];
                            for (int k = 0; k < jarr.Count; k++)
                                result[k] = jarr[k].ToObject<string>();
                            value = (TVal)(object)result;
                        }
                        else
                        {
                            // Fallback for other array types
                            value = customField.ToObject<TVal>();
                        }

                        obj.Remove(fieldName);
                        modLogger.LogInfo($"        ✅ Parsed array for {typeof(TVal)}");
                        return true;
                    }
                }
                // === Handle normal (non-array) types ===
                else
                {
                    value = customField.ToObject<TVal>();
                    obj.Remove(fieldName);
                    modLogger.LogInfo($"        ✅ Parsed single value for {typeof(TVal)}");
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                modLogger.LogInfo($"        Error parsing {fieldName} as {typeof(TVal)}: {ex.Message}");
            }
        }

        return false;
    }
}