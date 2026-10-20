#if !RW17
using Newtonsoft.Json;
#endif
using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using HarmonyLib;
using Verse;

namespace RimThreaded
{
#pragma warning disable 649
    [Serializable]
    public class SerializableMethod
    {
        public string Name;
        public Type ClassType;
        public List<Type> ParametersType;
        public SerializableMethod()
        {
            ParametersType = new List<Type>();
        }
    }
#pragma warning restore 649
#if RW17
    public class SerializableMethodDto
    {
        public string Name;
        public string ClassType;
        public List<string> ParametersType = new List<string>();
    }
#endif
    public class AssemblyCache
    {
        private static readonly List<MethodBase> CacheList = new List<MethodBase>();
        private static List<SerializableMethod> CacheListS = new List<SerializableMethod>();
        private static string CurrentMethodPath;
        public static void SaveJson()
        {
#if RW17
            // System.Text.Json cannot write System.Type; types are stored by assembly-qualified name.
            var dto = new List<SerializableMethodDto>();
            foreach (SerializableMethod sm in CacheListS)
                dto.Add(new SerializableMethodDto { Name = sm.Name, ClassType = sm.ClassType?.AssemblyQualifiedName, ParametersType = sm.ParametersType.ConvertAll(t => t.AssemblyQualifiedName) });
            string jsonString = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions { IncludeFields = true, WriteIndented = true });
#else
            string jsonString = JsonConvert.SerializeObject(CacheListS, Formatting.Indented);
#endif
            File.WriteAllText(CurrentMethodPath, jsonString);
            CacheList.Clear();
            CacheListS.Clear();
        }
        public static bool TryGetFromCache(string ModuleVersionId, out List<MethodBase> ReturnMethodList)
        {
            if (Prefs.LogVerbose)
            {
                Log.Message("TryGetFromCache: " + ModuleVersionId);
            }
            string CacheFolder = Path.Combine(RimThreadedMod.replacementsFolder, "Caches");
            Directory.CreateDirectory(CacheFolder);
            CurrentMethodPath = Path.Combine(CacheFolder, ModuleVersionId + ".json");
            if (!File.Exists(CurrentMethodPath))

            {
                ReturnMethodList = null;
                return false;
            }
            if (Prefs.LogVerbose)
            {
                Log.Message("RimThreaded is loading Cached Field Replacements from: " + CurrentMethodPath);
            }
            string jsonstr = File.ReadAllText(CurrentMethodPath);
#if RW17
            CacheListS = new List<SerializableMethod>();
            try
            {
                foreach (SerializableMethodDto d in System.Text.Json.JsonSerializer.Deserialize<List<SerializableMethodDto>>(jsonstr, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }))
                {
                    var sm = new SerializableMethod { Name = d.Name, ClassType = Type.GetType(d.ClassType) };
                    foreach (string p in d.ParametersType) sm.ParametersType.Add(Type.GetType(p));
                    if (sm.ClassType == null || sm.ParametersType.Contains(null))
                        throw new InvalidDataException("unresolvable type in cache");
                    CacheListS.Add(sm);
                }
            }
            catch (Exception)
            {
                // unreadable or stale cache: rescan instead
                CacheListS = new List<SerializableMethod>();
                ReturnMethodList = null;
                return false;
            }
#else
            CacheListS = JsonConvert.DeserializeObject<List<SerializableMethod>>(jsonstr);
#endif
            foreach (SerializableMethod s in CacheListS)
            {
                if (s.Name == ".ctor")
                {
                    CacheList.Add(AccessTools.Constructor(s.ClassType, s.ParametersType.ToArray()));
                    continue;
                }
                if (s.Name == ".cctor")
                {
                    CacheList.Add(AccessTools.Constructor(s.ClassType, s.ParametersType.ToArray(), true));
                    continue;
                }
                CacheList.Add(AccessTools.Method(s.ClassType, s.Name, s.ParametersType.ToArray()));
            }
            ReturnMethodList = CacheList;
            return true;

        }
        public static void AddToCache(string AssemblyName, MethodBase method, Type type)
        {
            string CacheFolder = Path.Combine(RimThreadedMod.replacementsFolder, "Caches");
            SerializableMethod SMethod = new SerializableMethod
            {
                Name = method.Name,
                ClassType = type
            };
            foreach (ParameterInfo p in method.GetParameters())
            {
                SMethod.ParametersType.Add(p.ParameterType);
            }
            CacheListS.Add(SMethod);
            CurrentMethodPath = Path.Combine(CacheFolder, AssemblyName + ".json");
            CacheList.Add(method);
        }

    }
}