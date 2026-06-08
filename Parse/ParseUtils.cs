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
    public static void ParsePerEach<targetType, T>(JObject rootObject, string categoryName, string fieldName, Dictionary<targetType, T> dict)
        where targetType : struct, System.IConvertible
    {
        foreach (JToken jtoken in rootObject.SelectTokens($"$.{categoryName}.*").ToList())
        {
            JObject token = jtoken.TryCast<JObject>();
            if (token != null)
            {
                if (EnumCache<targetType>.TryGetType(token.Path.Split('.').Last(), out var type))
                {
                    if (token[fieldName] != null)
                    {
                        T v = token[fieldName]!.ToObject<T>();
                        dict[type] = v;
                        token.Remove(fieldName);
                    }
                }
            }
        }
    }
}