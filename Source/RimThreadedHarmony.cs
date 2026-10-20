using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using System.Reflection.Emit;
using System.Threading;
using RimThreaded.Mod_Patches;
using RimWorld;
using UnityEngine;
using Verse.Sound;
using static HarmonyLib.AccessTools;
using Verse.AI;
using System.IO;
#if !RW17
using Newtonsoft.Json;
#endif
using RimThreaded.RW_Patches;

namespace RimThreaded
{

    public class RimThreadedHarmony
	{
		public static Harmony harmony = new Harmony("majorhoff.rimthreaded");

		public static FieldInfo cachedStoreCell;
		public static HashSet<MethodInfo> nonDestructivePrefixes = new HashSet<MethodInfo>();
		public static List<Assembly> assemblies;

		static RimThreadedHarmony()
		{
			if (Prefs.LogVerbose)
			{
				Harmony.DEBUG = true;
			}
			Log.Message("RimThreaded " + Assembly.GetExecutingAssembly().GetName().Version + "  is patching methods...");

			// 1.6 port: every phase and every patch class is isolated. Before, one exception (Texture2D_Patch on Unity 2022.3) escaped this
			// static constructor after the field replacements were already live, so RimThreaded never initialized its thread-static fields
			// and vanilla code threw NullReferenceException every frame from the main menu on.
			Safe(LoadFieldReplacements, "LoadFieldReplacements");
			Safe(AddAdditionalReplacements, "AddAdditionalReplacements");
			Safe(ApplyFieldReplacements, "ApplyFieldReplacements");
			Safe(PatchDestructiveFixes, "PatchDestructiveFixes");
			Safe(PatchNonDestructiveFixes, "PatchNonDestructiveFixes");
			Safe(PatchModCompatibility, "PatchModCompatibility");
			if (startupFailures.Count > 0)
				Log.Error("RimThreaded: " + startupFailures.Count + " patch group(s) failed to apply; RimThreaded is running without them: " + string.Join(", ", startupFailures)
					+ ". Please report this with your Player.log.");
			//if (Prefs.LogVerbose)
			//{
				//RimThreadedMod.exportTranspiledMethods();
			//}

			Log.Message("RimThreaded patching is complete.");

			string potentialConflicts = RimThreadedMod.GetPotentialModConflicts();
			if (potentialConflicts.Length > 0)
			{
				Log.Warning("Potential RimThreaded mod conflicts :" + potentialConflicts);
			}
		}

		public static readonly List<string> startupFailures = new List<string>();

		// 1.6 port: per-thread "new T()" for every replaced field that is not SelfInitialized. This used to be IL emitted into the
		// RimThreadedReplacements dynamic assembly and attached as Harmony postfixes to RimThreaded.InitializeAllThreadStatics. Two problems:
		// (1) emitted IL naming private vanilla types (e.g. Dictionary<string, List<GrammarResolver.RuleEntry>>) or internal mod classes is
		// subject to the runtime's member-access checks, and (2) one throwing postfix skipped every postfix after it. Reflection is not
		// access-checked, sets the calling thread's value of a [ThreadStatic] field, and each field is isolated.
		static readonly List<KeyValuePair<FieldInfo, ConstructorInfo>> generatedThreadStaticInits = new List<KeyValuePair<FieldInfo, ConstructorInfo>>();
		static readonly List<KeyValuePair<FieldInfo, ConstructorInfo>> pendingInits = new List<KeyValuePair<FieldInfo, ConstructorInfo>>();
		static readonly HashSet<string> reportedInitFailures = new HashSet<string>();

		public static void InitializeGeneratedThreadStatics()
		{
			foreach (KeyValuePair<FieldInfo, ConstructorInfo> init in generatedThreadStaticInits)
			{
				try
				{
					init.Key.SetValue(null, init.Value.Invoke(null));
				}
				catch (Exception e)
				{
					string name = init.Key.DeclaringType?.Name + "." + init.Key.Name;
					lock (reportedInitFailures)
					{
						if (!reportedInitFailures.Add(name))
							continue;
					}
					Log.Error("RimThreaded: could not initialize thread-static " + name + ": " + e);
				}
			}
		}

		// Runs one registration step; an exception is logged and recorded instead of aborting RimThreaded's start-up.
		internal static void Safe(Action step, string name)
		{
			try
			{
				step();
			}
			catch (Exception e)
			{
				startupFailures.Add(name);
				Log.Error("RimThreaded PATCH FAILED [" + name + "]: " + e);
			}
		}

		private static void AddAdditionalReplacements()
		{
			Safe(SimplePool_Patch_RunNonDestructivePatches, "SimplePool_Patch_RunNonDestructivePatches");
			Safe(GraphicDatabase_Patch.RunNonDestructivePatches, "GraphicDatabase_Patch.RunNonDestructivePatches");
			Safe(FullPool_Patch_RunNonDestructivePatches, "FullPool_Patch_RunNonDestructivePatches");
			Safe(Dijkstra_Patch_RunNonDestructivePatches, "Dijkstra_Patch_RunNonDestructivePatches");
			Safe(Region_Patch.AddFieldReplacements, "Region_Patch.AddFieldReplacements");
			Safe(GenericCallSites.Register, "GenericCallSites.Register");
			replaceFields.Add(Method(typeof(Time), "get_realtimeSinceStartup"), Method(typeof(Time_Patch), nameof(Time_Patch.get_realtimeSinceStartup)));
			replaceFields.Add(Method(typeof(RenderTexture), nameof(RenderTexture.ReleaseTemporary)), Method(typeof(RenderTexture_Patch), nameof(RenderTexture_Patch.ReleaseTemporary)));
#if DEBUG
			Safe(Material_Patch.RunDestructivePatches, "Material_Patch.RunDestructivePatches");
			Safe(Transform_Patch.RunDestructivePatches, "Transform_Patch.RunDestructivePatches");
			Safe(UnityEngine_Object_Patch.RunDestructivePatches, "UnityEngine_Object_Patch.RunDestructivePatches");
			Safe(Texture_Patch.RunDestructivePatches, "Texture_Patch.RunDestructivePatches");
			Safe(GraphicsFormatUtility_Patch.RunDestructivePatches, "GraphicsFormatUtility_Patch.RunDestructivePatches");
			replaceFields.Add(Method(typeof(Time), "get_frameCount"), Method(typeof(Time_Patch), nameof(Time_Patch.get_frameCount)));
			replaceFields.Add(Method(typeof(Time), "get_time"), Method(typeof(Time_Patch), nameof(Time_Patch.get_time)));
			replaceFields.Add(Method(typeof(Component), "get_transform"), Method(typeof(Component_Patch), nameof(Component_Patch.get_transform)));
			replaceFields.Add(Method(typeof(Component), "get_gameObject"), Method(typeof(Component_Patch), nameof(Component_Patch.get_gameObject)));
			replaceFields.Add(Method(typeof(GameObject), "get_transform"), Method(typeof(GameObject_Patch), nameof(Component_Patch.get_transform)));
			//replaceFields.Add(Method(typeof(GameObject), "GetComponent", Type.EmptyTypes), Method(typeof(GameObject_Patch), "GetComponent"));
			//replaceFields.Add(Method(typeof(GameObject), "GetComponent", Type.EmptyTypes, new Type[] { typeof(AudioReverbFilter) }),
			//	Method(typeof(GameObject_Patch), "GetComponent", new Type[] { typeof(GameObject) }, new Type[] { typeof(AudioReverbFilter) }));
			//replaceFields.Add(Method(typeof(GameObject), "GetComponent", Type.EmptyTypes, new Type[] { typeof(AudioLowPassFilter) }),
			//	Method(typeof(GameObject_Patch), "GetComponent", new Type[] { typeof(GameObject) }, new Type[] { typeof(AudioLowPassFilter) }));
			//replaceFields.Add(Method(typeof(GameObject), "GetComponent", Type.EmptyTypes, new Type[] { typeof(AudioHighPassFilter) }),
			//	Method(typeof(GameObject_Patch), "GetComponent", new Type[] { typeof(GameObject) }, new Type[] { typeof(AudioHighPassFilter) }));
			//replaceFields.Add(Method(typeof(GameObject), "GetComponent", Type.EmptyTypes, new Type[] { typeof(AudioEchoFilter) }),
			//	Method(typeof(GameObject_Patch), "GetComponent", new Type[] { typeof(GameObject) }, new Type[] { typeof(AudioEchoFilter) }));
			//replaceFields.Add(Method(typeof(GameObject), "AddComponent", Type.EmptyTypes, new Type[] { typeof(AudioReverbFilter) }),
			//	Method(typeof(GameObject_Patch), "AddComponent", new Type[] { typeof(GameObject) }, new Type[] { typeof(AudioReverbFilter) }));
			//replaceFields.Add(Method(typeof(GameObject), "AddComponent", Type.EmptyTypes, new Type[] { typeof(AudioLowPassFilter) }),
			//	Method(typeof(GameObject_Patch), "AddComponent", new Type[] { typeof(GameObject) }, new Type[] { typeof(AudioLowPassFilter) }));
			//replaceFields.Add(Method(typeof(GameObject), "AddComponent", Type.EmptyTypes, new Type[] { typeof(AudioHighPassFilter) }),
			//	Method(typeof(GameObject_Patch), "AddComponent", new Type[] { typeof(GameObject) }, new Type[] { typeof(AudioHighPassFilter) }));
			//replaceFields.Add(Method(typeof(GameObject), "AddComponent", Type.EmptyTypes, new Type[] { typeof(AudioEchoFilter) }),
			//	Method(typeof(GameObject_Patch), "AddComponent", new Type[] { typeof(GameObject) }, new Type[] { typeof(AudioEchoFilter) }));
			//replaceFields.Add(Method(typeof(Transform), "set_parent"), Method(typeof(Transform_Patch), "set_parent"));
			//replaceFields.Add(Method(typeof(Transform), "set_localPosition"), Method(typeof(Transform_Patch), "set_localPosition"));
			//replaceFields.Add(Method(typeof(AudioSource), "set_clip"), Method(typeof(AudioSource_Patch), "set_clip"));
			//replaceFields.Add(Method(typeof(AudioSource), "set_volume"), Method(typeof(AudioSource_Patch), "set_volume"));
			//replaceFields.Add(Method(typeof(AudioSource), "set_pitch"), Method(typeof(AudioSource_Patch), "set_pitch"));
			//replaceFields.Add(Method(typeof(AudioSource), "set_minDistance"), Method(typeof(AudioSource_Patch), "set_minDistance"));
			//replaceFields.Add(Method(typeof(AudioSource), "set_maxDistance"), Method(typeof(AudioSource_Patch), "set_maxDistance"));
			//replaceFields.Add(Method(typeof(AudioSource), "set_spatialBlend"), Method(typeof(AudioSource_Patch), "set_spatialBlend"));
			//replaceFields.Add(Method(typeof(AudioSource), "set_loop"), Method(typeof(AudioSource_Patch), "set_loop"));
			//replaceFields.Add(Method(typeof(AudioSource), "set_mute"), Method(typeof(AudioSource_Patch), "set_mute"));
			//replaceFields.Add(Method(typeof(AudioSource), "Play", Type.EmptyTypes), Method(typeof(AudioSource_Patch), "Play"));
			//replaceFields.Add(Method(typeof(AudioSource), "get_volume"), Method(typeof(AudioSource_Patch), "get_volume"));
#endif
			//replaceFields.Add(Method(typeof(AudioLowPassFilter), "set_cutoffFrequency"), Method(typeof(AudioLowPassFilter_Patch), "set_cutoffFrequency"));
			//replaceFields.Add(Method(typeof(AudioLowPassFilter), "set_lowpassResonanceQ"), Method(typeof(AudioLowPassFilter_Patch), "set_lowpassResonanceQ"));
			//replaceFields.Add(Method(typeof(AudioHighPassFilter), "set_cutoffFrequency"), Method(typeof(AudioHighPassFilter_Patch), "set_cutoffFrequency"));
			//replaceFields.Add(Method(typeof(AudioHighPassFilter), "set_highpassResonanceQ"), Method(typeof(AudioHighPassFilter_Patch), "set_highpassResonanceQ"));
			replaceFields.Add(Method(typeof(MeshMakerPlanes), "NewPlaneMesh", new Type[] { typeof(Vector2), typeof(bool), typeof(bool), typeof(bool) }), Method(typeof(MeshMakerPlanes_Patch), "NewPlaneMesh"));
		}
#pragma warning disable 649
		[Serializable]
		class Replacements
		{
			public List<ClassReplacement> ClassReplacements;
		}

		[Serializable]
		class ClassReplacement
		{
			public string ClassName;
			public bool IgnoreMissing;
			public List<ThreadStaticDetail> ThreadStatics;
		}
		[Serializable]
		class ThreadStaticDetail
		{
			public string FieldName;
			public string PatchedClassName;
			public bool SelfInitialized;
		}
#pragma warning restore 649

		static Replacements replacements;
		private static void LoadFieldReplacements()
		{
			assemblies = (from a in AppDomain.CurrentDomain.GetAssemblies()
						  where !a.FullName.StartsWith("Microsoft.VisualStudio")
						  select a).ToList();
			//string replacementsJsonPath = Path.Combine(((Mod)RimThreadedMod).intContent.RootDir, "replacements.json"); 

			string jsonString = File.ReadAllText(RimThreadedMod.replacementsJsonPath);
#if RW17
			// 1.7 redirects Unity.Newtonsoft.Json away (RimWorld.runtimeconfig.json), so the built-in reader is used.
			replacements = System.Text.Json.JsonSerializer.Deserialize<Replacements>(jsonString,
				new System.Text.Json.JsonSerializerOptions { IncludeFields = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
#else
			replacements = JsonConvert.DeserializeObject<Replacements>(jsonString);
#endif

			//IEnumerable<Assembly> source = from a in AppDomain.CurrentDomain.GetAssemblies()
			//                               where !a.FullName.StartsWith("Microsoft.VisualStudio")
			//                               select a;
			//foreach (Assembly a in source)
			//{
			//    Type[] b = GetTypesFromAssembly(a);
			//    if (a.ManifestModule.Name.Equals("Assembly-CSharp.dll"))
			//    {
			//        foreach (Type c in b)
			//        {
			//            if (c.FullName.Contains("BFSW"))
			//            {
			//                Log.Message(c.FullName);
			//                //Console.WriteLine(c.FullName);
			//            }
			//        }
			//    }
			//}
			ConstructorInfo threadStaticConstructor = typeof(ThreadStaticAttribute).GetConstructor(new Type[0]);
			CustomAttributeBuilder attributeBuilder = new CustomAttributeBuilder(threadStaticConstructor, new object[0]);
			AssemblyName aName = new AssemblyName("RimThreadedReplacements");
			AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(aName, AssemblyBuilderAccess.Run);
			ModuleBuilder modBuilder = ab.DefineDynamicModule(aName.Name);
			foreach (ClassReplacement classReplacement in replacements.ClassReplacements)
			{
				Type type = TypeByName(classReplacement.ClassName);
				if (type == null)
				{
					if(!classReplacement.IgnoreMissing)
						Log.Error("Cannot find class named: " + classReplacement.ClassName);
					continue;
				}
				if (classReplacement.ThreadStatics != null && classReplacement.ThreadStatics.Count > 0)
				{
					TypeBuilder tb = modBuilder.DefineType(type.Name + "_Replacement", TypeAttributes.Public);
					List<KeyValuePair<FieldInfo, string>> builderFields = new List<KeyValuePair<FieldInfo, string>>();
					foreach (ThreadStaticDetail threadStaticDetail in classReplacement.ThreadStatics)
					{
						FieldInfo fieldInfo = Field(type, threadStaticDetail.FieldName);
						if (fieldInfo == null)
						{
							Log.Error("Cannot find field named: " + classReplacement.ClassName + "." + threadStaticDetail.FieldName);
							continue;
						}
						FieldInfo replacementField;
						if (threadStaticDetail.PatchedClassName == null)
						{
							FieldBuilder fb = tb.DefineField(fieldInfo.Name, fieldInfo.FieldType, FieldAttributes.Public | FieldAttributes.Static);
							fb.SetCustomAttribute(attributeBuilder);
							replacementField = null; // resolved after CreateType below
							builderFields.Add(new KeyValuePair<FieldInfo, string>(fieldInfo, fb.Name));
						}
						else
						{
							Type replacementType = TypeByName(threadStaticDetail.PatchedClassName);
							if (replacementType == null)
							{
								Log.Error("Cannot find replacement class named: " + threadStaticDetail.PatchedClassName);
								continue;
							}
							replacementField = Field(replacementType, threadStaticDetail.FieldName);
							if (replacementField == null)
							{
								Log.Error("Cannot find replacement field named: " + threadStaticDetail.PatchedClassName + "." + threadStaticDetail.FieldName);
								continue;
							}
						}
						if (replacementField != null)
							replaceFields[fieldInfo] = replacementField;
						if (!threadStaticDetail.SelfInitialized)
						{
							ConstructorInfo constructor = fieldInfo.FieldType.GetConstructor(Type.EmptyTypes);
							if (constructor != null)
								pendingInits.Add(new KeyValuePair<FieldInfo, ConstructorInfo>(replacementField ?? fieldInfo, constructor));
						}
					}
					Type newFieldType = tb.CreateType();
					// FieldBuilder objects are only valid while the type is being emitted; transpilers need the real runtime fields
					foreach (KeyValuePair<FieldInfo, string> builderField in builderFields)
					{
						FieldInfo runtimeField = newFieldType.GetField(builderField.Value, BindingFlags.Public | BindingFlags.Static);
						replaceFields[builderField.Key] = runtimeField;
						for (int i = 0; i < pendingInits.Count; i++)
							if (pendingInits[i].Key == builderField.Key)
								pendingInits[i] = new KeyValuePair<FieldInfo, ConstructorInfo>(runtimeField, pendingInits[i].Value);
					}
					generatedThreadStaticInits.AddRange(pendingInits);
					pendingInits.Clear();
				}
			}
		}
		public static Dictionary<Type, HashSet<FieldInfo>> untouchedStaticFields = new Dictionary<Type, HashSet<FieldInfo>>();
		public static HashSet<string> fieldFullNames = new HashSet<string>();
		public static HashSet<string> allStaticFieldNames = new HashSet<string>();
		//public static bool intializersReady = false;
		private static string ReplacementSetHash()
		{
			List<string> keys = new List<string>();
			foreach (object k in replaceFields.Keys)
			{
				if (k is MemberInfo m)
					keys.Add(m.DeclaringType?.FullName + "::" + m.Name + (m is MethodBase mb ? "(" + string.Join(",", mb.GetParameters().Select(p => p.ParameterType.FullName)) + ")" : ""));
				else
					keys.Add(k?.ToString());
			}
			keys.Sort(StringComparer.Ordinal);
			keys.Add(typeof(RimThreadedHarmony).Assembly.ManifestModule.ModuleVersionId.ToString());
			using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
			{
				byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", keys)));
				return BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant();
			}
		}

		private static void ApplyFieldReplacements()
		{
			List<MethodBase> MethodsFromCache = new List<MethodBase>();
			// 1.6 port: the cache of "methods that touch a replaced field" was keyed only by the game assembly's MVID, so after a change to
			// replacements_*.json (or to fields added in code) a stale list was reused and methods touching the new fields were never
			// transpiled. The key now also covers the replaced-field set and this RimThreaded build.
			string cacheKeySuffix = ReplacementSetHash();
			HashSet<string> AssembliesToPatch = new HashSet<string>()
			{
				"Assembly-CSharp.dll",
				"VFECore.dll",
				"GiddyUpCore.dll",
				"SpeakUp.dll"
			};
			foreach (Assembly assembly in assemblies)
			{
				//Log.Message(assembly.ManifestModule.ScopeName);
				if (AssembliesToPatch.Contains(assembly.ManifestModule.ScopeName))
				{
					if (Prefs.LogVerbose)
					{
                        Log.Message("RimThreaded is attempting to load replacements from cache for assembly: " + assembly.FullName);
					}
					if (AssemblyCache.TryGetFromCache(assembly.ManifestModule.ModuleVersionId.ToString() + "_" + cacheKeySuffix, out MethodsFromCache))
					{
						foreach (MethodBase method in MethodsFromCache)
						{
							TranspileFieldReplacements(method);
						}
					}
					else
					{
						Type[] types = GetTypesFromAssembly(assembly);
						foreach (Type type in types)
						{
							//if(type.FullName.Contains("TileFinder"))
                            //{
							//	Log.Message("TileFinder");
                            //}
							List<MethodBase> allMethods = new List<MethodBase>();
							allMethods.AddRange(type.GetMethods(all | BindingFlags.DeclaredOnly));
							allMethods.AddRange(type.GetConstructors(all | BindingFlags.DeclaredOnly));

							foreach (MethodBase method in allMethods)
							{
								// Static constructors are left alone: transpiled, their "stsfld vanillaField" became a write to this thread's
								// replacement, so the vanilla field stayed null and code that cannot be transpiled (generic methods and
								// iterators of generic types) read null (1.7 Player.log: NullReferenceException in
								// ThingOwnerUtility.GetAllThingsRecursively<T> on every tick). Replacements get their per-thread values from
								// InitializeAllThreadStatics.
								if (method is ConstructorInfo && method.IsStatic) // note: MethodBase.IsConstructor is false for static constructors
									continue;
								if (method.IsDeclaredMember())
								{
									try
									{
										IEnumerable<KeyValuePair<OpCode, object>> codeInstructions = PatchProcessor.ReadMethodBody(method);
										foreach (KeyValuePair<OpCode, object> codeInstruction in codeInstructions)
										{
											if (method is ConstructorInfo && (codeInstruction.Key == OpCodes.Stfld || codeInstruction.Key == OpCodes.Stsfld))
												continue; // constructor stores stay vanilla (see ReplaceFieldsTranspiler)
											if (codeInstruction.Value is FieldInfo fieldInfo && replaceFields.ContainsKey(fieldInfo))
											{
												AssemblyCache.AddToCache(assembly.ManifestModule.ModuleVersionId.ToString() + "_" + cacheKeySuffix, method, type);
												TranspileFieldReplacements(method);
												break;
											}
											if (codeInstruction.Value is MethodInfo methodInfo && (replaceFields.ContainsKey(methodInfo) || IsReplacedGenericCall(methodInfo)))
											{
												AssemblyCache.AddToCache(assembly.ManifestModule.ModuleVersionId.ToString() + "_" + cacheKeySuffix, method, type);
												TranspileFieldReplacements(method);
												break;
											}
										}
									}
									catch (NotSupportedException) { }
									catch (TypeInitializationException) { }
									catch (Exception) { }
								}
							}
						}
						AssemblyCache.SaveJson();
					}
				}
			}
			Log.Message("RimThreaded Field Replacements Complete.");
		}


		public static List<CodeInstruction> EnterLock(LocalBuilder lockObject, LocalBuilder lockTaken, List<CodeInstruction> loadLockObjectInstructions, CodeInstruction currentInstruction)
		{
			List<CodeInstruction> codeInstructions = new List<CodeInstruction>();
			loadLockObjectInstructions[0].labels = currentInstruction.labels;
			for (int i = 0; i < loadLockObjectInstructions.Count; i++)
			{
				codeInstructions.Add(loadLockObjectInstructions[i]);
			}
			currentInstruction.labels = new List<Label>();
			codeInstructions.Add(new CodeInstruction(OpCodes.Stloc, lockObject.LocalIndex));
			codeInstructions.Add(new CodeInstruction(OpCodes.Ldc_I4_0));
			codeInstructions.Add(new CodeInstruction(OpCodes.Stloc, lockTaken.LocalIndex));
			CodeInstruction codeInstruction = new CodeInstruction(OpCodes.Ldloc, lockObject.LocalIndex);
			codeInstruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
			codeInstructions.Add(codeInstruction);
			codeInstructions.Add(new CodeInstruction(OpCodes.Ldloca_S, lockTaken.LocalIndex));
			codeInstructions.Add(new CodeInstruction(OpCodes.Call, Method(typeof(Monitor), "Enter",
				new Type[] { typeof(object), typeof(bool).MakeByRefType() })));
			return codeInstructions;
		}
		public static List<CodeInstruction> ExitLock(ILGenerator iLGenerator, LocalBuilder lockObject, LocalBuilder lockTaken, CodeInstruction currentInstruction)
		{
			List<CodeInstruction> codeInstructions = new List<CodeInstruction>();
			Label endHandlerDestination = iLGenerator.DefineLabel();
			codeInstructions.Add(new CodeInstruction(OpCodes.Leave_S, endHandlerDestination));
			CodeInstruction codeInstruction = new CodeInstruction(OpCodes.Ldloc, lockTaken.LocalIndex);
			codeInstruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock));
			codeInstructions.Add(codeInstruction);
			Label endFinallyDestination = iLGenerator.DefineLabel();
			codeInstructions.Add(new CodeInstruction(OpCodes.Brfalse_S, endFinallyDestination));
			codeInstructions.Add(new CodeInstruction(OpCodes.Ldloc, lockObject.LocalIndex));
			codeInstructions.Add(new CodeInstruction(OpCodes.Call, Method(typeof(Monitor), "Exit")));
			codeInstruction = new CodeInstruction(OpCodes.Endfinally);
			codeInstruction.labels.Add(endFinallyDestination);
			codeInstruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
			codeInstructions.Add(codeInstruction);
			currentInstruction.labels.Add(endHandlerDestination);
			return codeInstructions;
		}

		public static List<CodeInstruction> GetLockCodeInstructions(
			ILGenerator iLGenerator, List<CodeInstruction> instructionsList, int currentInstructionIndex,
			int searchInstructionsCount, List<CodeInstruction> loadLockObjectInstructions,
			LocalBuilder lockObject, LocalBuilder lockTaken)
		{
			List<CodeInstruction> finalCodeInstructions = new List<CodeInstruction>();
			loadLockObjectInstructions[0].labels = instructionsList[currentInstructionIndex].labels;
			for (int i = 0; i < loadLockObjectInstructions.Count; i++)
			{
				finalCodeInstructions.Add(loadLockObjectInstructions[i]);
			}
			instructionsList[currentInstructionIndex].labels = new List<Label>();
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Stloc, lockObject.LocalIndex));
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Ldc_I4_0));
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Stloc, lockTaken.LocalIndex));
			CodeInstruction codeInstruction = new CodeInstruction(OpCodes.Ldloc, lockObject.LocalIndex);
			codeInstruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
			finalCodeInstructions.Add(codeInstruction);
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Ldloca_S, lockTaken.LocalIndex));
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Call, Method(typeof(Monitor), "Enter",
				new Type[] { typeof(object), typeof(bool).MakeByRefType() })));
			for (int i = 0; i < searchInstructionsCount; i++)
			{
				finalCodeInstructions.Add(instructionsList[currentInstructionIndex]);
				currentInstructionIndex++;
			}
			Label endHandlerDestination = iLGenerator.DefineLabel();
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Leave_S, endHandlerDestination));
			codeInstruction = new CodeInstruction(OpCodes.Ldloc, lockTaken.LocalIndex);
			codeInstruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock));
			finalCodeInstructions.Add(codeInstruction);
			Label endFinallyDestination = iLGenerator.DefineLabel();
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Brfalse_S, endFinallyDestination));
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Ldloc, lockObject.LocalIndex));
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Call, Method(typeof(Monitor), "Exit")));
			codeInstruction = new CodeInstruction(OpCodes.Endfinally);
			codeInstruction.labels.Add(endFinallyDestination);
			codeInstruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
			finalCodeInstructions.Add(codeInstruction);
			instructionsList[currentInstructionIndex].labels.Add(endHandlerDestination);
			return finalCodeInstructions;
		}
		public static List<CodeInstruction> GetLockCodeInstructions(
			ILGenerator iLGenerator, List<CodeInstruction> instructionsList, int currentInstructionIndex,
			int searchInstructionsCount, List<CodeInstruction> loadLockObjectInstructions, Type lockObjectType)
		{
			LocalBuilder lockObject = iLGenerator.DeclareLocal(lockObjectType);
			LocalBuilder lockTaken = iLGenerator.DeclareLocal(typeof(bool));
			return GetLockCodeInstructions(iLGenerator, instructionsList, currentInstructionIndex,
				searchInstructionsCount, loadLockObjectInstructions, lockObject, lockTaken);
		}

		public static bool IsCodeInstructionsMatching(List<CodeInstruction> searchInstructions, List<CodeInstruction> instructionsList, int instructionIndex)
		{
			bool instructionsMatch = false;
			if (instructionIndex + searchInstructions.Count < instructionsList.Count)
			{
				instructionsMatch = true;
				for (int searchIndex = 0; searchIndex < searchInstructions.Count; searchIndex++)
				{
					CodeInstruction searchInstruction = searchInstructions[searchIndex];
					CodeInstruction originalInstruction = instructionsList[instructionIndex + searchIndex];
					object searchOperand = searchInstruction.operand;
					object orginalOperand = originalInstruction.operand;
					if (searchInstruction.opcode != originalInstruction.opcode)
					{
						instructionsMatch = false;
						break;
					}
					else
					{
						if (orginalOperand != null &&
							searchOperand != null &&
							orginalOperand != searchOperand)
						{
							if (orginalOperand.GetType() != typeof(LocalBuilder))
							{
								instructionsMatch = false;
								break;
							}
							else
							{
								if (((LocalBuilder)orginalOperand).LocalIndex != (int)searchOperand)
								{
									instructionsMatch = false;
									break;
								}
							}
						}
					}
				}
			}
			return instructionsMatch;
		}
		public static void AddBreakDestination(List<CodeInstruction> instructionsList, int currentInstructionIndex, Label breakDestination)
		{
			//Since we are going to break inside of some kind of loop, we need to find out where to jump/break to
			//The destination should be one line after the closing bracket of the loop when the exception/break occurs			
			HashSet<Label> labels = new HashSet<Label>();

			//gather all labels that exist at or above currentInstructionIndex. the start of our loop is going to be one of these...
			for (int i = 0; i <= currentInstructionIndex; i++)
			{
				foreach (Label label in instructionsList[i].labels)
				{
					labels.Add(label);
				}
			}

			//find first branch that jumps to label above currentInstructionIndex. the first branch opcode found is likely the closing bracket
			for (int i = currentInstructionIndex + 1; i < instructionsList.Count; i++)
			{
				if (instructionsList[i].operand is Label label)
				{
					if (labels.Contains(label))
					{
						instructionsList[i + 1].labels.Add(breakDestination);
						break;
					}
				}
			}
		}

		public static void StartTryAndAddBreakDestinationLabel(List<CodeInstruction> instructionsList, ref int currentInstructionIndex, Label breakDestination)
		{
			AddBreakDestination(instructionsList, currentInstructionIndex, breakDestination);
			instructionsList[currentInstructionIndex].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
		}

		public static List<CodeInstruction> EndTryStartCatchArgumentExceptionOutOfRange(List<CodeInstruction> instructionsList, ref int currentInstructionIndex, ILGenerator iLGenerator, Label breakDestination)
		{
			List<CodeInstruction> codeInstructions = new List<CodeInstruction>();
			Label handlerEnd = iLGenerator.DefineLabel();
			codeInstructions.Add(new CodeInstruction(OpCodes.Leave_S, handlerEnd));
			CodeInstruction pop = new CodeInstruction(OpCodes.Pop);
			pop.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginCatchBlock, typeof(ArgumentOutOfRangeException)));
			codeInstructions.Add(pop);
			CodeInstruction leaveLoopEnd = new CodeInstruction(OpCodes.Leave, breakDestination);
			leaveLoopEnd.blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
			codeInstructions.Add(leaveLoopEnd);
			instructionsList[currentInstructionIndex].labels.Add(handlerEnd);
			return codeInstructions;
		}

		public static List<CodeInstruction> UpdateTryCatchCodeInstructions(ILGenerator iLGenerator,
			List<CodeInstruction> instructionsList, int currentInstructionIndex, int searchInstructionsCount)
		{
			Label breakDestination = iLGenerator.DefineLabel();
			AddBreakDestination(instructionsList, currentInstructionIndex, breakDestination);
			List<CodeInstruction> finalCodeInstructions = new List<CodeInstruction>();
			instructionsList[currentInstructionIndex].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
			for (int i = 0; i < searchInstructionsCount; i++)
			{
				finalCodeInstructions.Add(instructionsList[currentInstructionIndex]);
				currentInstructionIndex++;
			}
			Label handlerEnd = iLGenerator.DefineLabel();
			finalCodeInstructions.Add(new CodeInstruction(OpCodes.Leave_S, handlerEnd));
			CodeInstruction pop = new CodeInstruction(OpCodes.Pop);
			pop.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginCatchBlock, typeof(ArgumentOutOfRangeException)));
			finalCodeInstructions.Add(pop);
			CodeInstruction leaveLoopEnd = new CodeInstruction(OpCodes.Leave, breakDestination);
			leaveLoopEnd.blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
			finalCodeInstructions.Add(leaveLoopEnd);
			instructionsList[currentInstructionIndex].labels.Add(handlerEnd);
			return finalCodeInstructions;
		}

		// Wraps the whole method body in lock(this). Every ret (early returns and value returns included) is rewritten into
		// "store return value; leave <end>" because a ret is illegal inside a try block. The 1.4 version only handled
		// void methods that end in a single ret, anything else produced invalid IL.
		public static IEnumerable<CodeInstruction> WrapMethodInInstanceLock(IEnumerable<CodeInstruction> instructions, ILGenerator iLGenerator, MethodBase __originalMethod)
		{
			List<CodeInstruction> instructionsList = instructions.ToList();
			if (__originalMethod.IsStatic)
			{
				Log.Error("RimThreaded: instance lock transpiler applied to static method " + __originalMethod.DeclaringType + "." + __originalMethod.Name + ", skipping.");
				foreach (CodeInstruction untouched in instructionsList)
					yield return untouched;
				yield break;
			}
			Type returnType = (__originalMethod as MethodInfo)?.ReturnType ?? typeof(void);
			bool hasValue = returnType != typeof(void);
			LocalBuilder returnValue = hasValue ? iLGenerator.DeclareLocal(returnType) : null;
			Label endLabel = iLGenerator.DefineLabel();
			LocalBuilder lockObject = iLGenerator.DeclareLocal(typeof(object));
			LocalBuilder lockTaken = iLGenerator.DeclareLocal(typeof(bool));
			List<CodeInstruction> loadLockObjectInstructions = new List<CodeInstruction>
			{
				new CodeInstruction(OpCodes.Ldarg_0)
			};
			foreach (CodeInstruction ci in EnterLock(lockObject, lockTaken, loadLockObjectInstructions, instructionsList[0]))
				yield return ci;
			foreach (CodeInstruction instruction in instructionsList)
			{
				if (instruction.opcode == OpCodes.Ret)
				{
					CodeInstruction first = hasValue ? new CodeInstruction(OpCodes.Stloc, returnValue.LocalIndex) : new CodeInstruction(OpCodes.Nop);
					first.labels = instruction.labels;
					first.blocks = instruction.blocks;
					yield return first;
					yield return new CodeInstruction(OpCodes.Leave, endLabel);
				}
				else
				{
					yield return instruction;
				}
			}
			CodeInstruction endInstruction = hasValue ? new CodeInstruction(OpCodes.Ldloc, returnValue.LocalIndex) : new CodeInstruction(OpCodes.Ret);
			endInstruction.labels.Add(endLabel);
			foreach (CodeInstruction ci in ExitLock(iLGenerator, lockObject, lockTaken, endInstruction))
				yield return ci;
			yield return endInstruction;
			if (hasValue)
				yield return new CodeInstruction(OpCodes.Ret);
		}

		public static readonly Dictionary<object, object> replaceFields = new Dictionary<object, object>();

		// Generic vanilla methods that use replaced static fields cannot be field-replaced themselves (their instantiations run shared code
		// that Harmony does not reach on the 1.7 runtime; prefixes on instantiations were ignored in-game). Their call sites are redirected
		// to thread-safe generic versions in the mod instead: generic definition -> replacement generic definition (same type parameters;
		// an instance method maps to a static one taking the instance first).
		public static readonly Dictionary<MethodInfo, MethodInfo> replaceGenericMethods = new Dictionary<MethodInfo, MethodInfo>();

		internal static bool IsReplacedGenericCall(MethodInfo m)
		{
			return m.IsGenericMethod && !m.IsGenericMethodDefinition && replaceGenericMethods.Count > 0 && replaceGenericMethods.ContainsKey(m.GetGenericMethodDefinition());
		}

		public static HashSet<object> notifiedObjects = new HashSet<object>();

		public static IEnumerable<CodeInstruction> ReplaceFieldsTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator iLGenerator, MethodBase original)
		{
			notifiedObjects.Clear();
			// In constructors the vanilla field keeps its own initial value: a store there is left alone (reads are still replaced).
			// Redirecting "this.field = new ..." to a per-thread replacement left the vanilla field null for code that cannot be
			// transpiled (generic methods, iterators of generic types), e.g. Room.ContainedThings<T> reading uniqueContainedThingsOfDef.
			bool isConstructor = original is ConstructorInfo;
			foreach (CodeInstruction codeInstruction in instructions)
			{
				if (Prefs.LogVerbose)
				{
					Log.messageCount = 0; //prevents logging to stop from spam
				}
				object operand = codeInstruction.operand;
				if (operand is MethodInfo calledGeneric && IsReplacedGenericCall(calledGeneric))
				{
					MethodInfo replacement = replaceGenericMethods[calledGeneric.GetGenericMethodDefinition()].MakeGenericMethod(calledGeneric.GetGenericArguments());
					if (replacement.IsStatic && codeInstruction.opcode == OpCodes.Callvirt)
						codeInstruction.opcode = OpCodes.Call; // instance method -> static taking the instance as first argument
					codeInstruction.operand = replacement;
					yield return codeInstruction;
					continue;
				}
				if (isConstructor && operand is FieldInfo && (codeInstruction.opcode == OpCodes.Stfld || codeInstruction.opcode == OpCodes.Stsfld))
				{
					yield return codeInstruction;
					continue;
				}
				if (operand != null && replaceFields.TryGetValue(operand, out object newObjectInfo))
				{
					switch (operand)
					{
						case FieldInfo fieldInfo:
							{
								switch (newObjectInfo)
								{
									case FieldInfo newFieldInfo:
										if (Prefs.LogVerbose)
										{
											if (!notifiedObjects.Contains(newFieldInfo))
											{
												notifiedObjects.Add(newFieldInfo);
												Log.Message("RimThreaded is replacing field: " +
															fieldInfo.DeclaringType + "." + fieldInfo.Name +
															" with field: " + newFieldInfo.DeclaringType + "." + newFieldInfo.Name);
											}
										}
										codeInstruction.operand = newFieldInfo;
										break;
									case Dictionary<OpCode, MethodInfo> newMethodInfoDict:
										MethodInfo newMethodInfo = newMethodInfoDict[codeInstruction.opcode];
										if (Prefs.LogVerbose)
										{
											if (!notifiedObjects.Contains(newMethodInfo))
											{
												notifiedObjects.Add(newMethodInfo);
												Log.Message("RimThreaded is replacing field: " +
													fieldInfo.DeclaringType + "." + fieldInfo.Name +
													" with CALL method: " + newMethodInfo.DeclaringType + "." +
													newMethodInfo.Name);
											}
										}
										codeInstruction.opcode = OpCodes.Call;
										codeInstruction.operand = newMethodInfo;
										break;
								}

								break;
							}
						case MethodInfo methodInfo:
							{
								switch (newObjectInfo)
								{
									case FieldInfo newFieldInfo:
										if (Prefs.LogVerbose)
										{
											if (!notifiedObjects.Contains(newFieldInfo))
											{
												notifiedObjects.Add(newFieldInfo);
												Log.Message("RimThreaded is replacing method: " +
													methodInfo.DeclaringType + "." + methodInfo.Name +
													" with STATIC field: " + newFieldInfo.DeclaringType + "." +
													newFieldInfo.Name);
											}
										}
										codeInstruction.opcode = OpCodes.Ldsfld;
										codeInstruction.operand = newFieldInfo;
										break;
									case MethodInfo newMethodInfo:
										if (Prefs.LogVerbose)
										{
											if (!notifiedObjects.Contains(newMethodInfo))
											{
												notifiedObjects.Add(newMethodInfo);
												Log.Message("RimThreaded is replacing method: " +
													methodInfo.DeclaringType + "." + methodInfo.Name +
													" with method: " + newMethodInfo.DeclaringType + "." +
													newMethodInfo.Name);
											}
										}
										codeInstruction.operand = newMethodInfo;
										break;
								}

								break;
							}
					}
				}
				yield return codeInstruction;
			}
		}

		public static IEnumerable<CodeInstruction> Add3Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator iLGenerator)
		{
			List<CodeInstruction> instructionsList = instructions.ToList();
			int i = 0;
			while (i < instructionsList.Count)
			{
				if (i + 3 < instructionsList.Count && instructionsList[i + 3].opcode == OpCodes.Callvirt)
				{
					if (instructionsList[i + 3].operand is MethodInfo methodInfo)
					{
						if (methodInfo.Name.Equals("Add") && methodInfo.DeclaringType.FullName.Contains("System.Collections"))
						{
							LocalBuilder lockObject = iLGenerator.DeclareLocal(typeof(object));
							LocalBuilder lockTaken = iLGenerator.DeclareLocal(typeof(bool));
							List<CodeInstruction> loadLockObjectInstructions = new List<CodeInstruction>()
							{
								new CodeInstruction(OpCodes.Ldarg_0)
							};
							foreach (CodeInstruction lockInstruction in EnterLock(lockObject, lockTaken, loadLockObjectInstructions, instructionsList[i]))
							{
								yield return lockInstruction;
							}
							yield return instructionsList[i++];
							yield return instructionsList[i++];
							yield return instructionsList[i++];
							yield return instructionsList[i++];
							foreach (CodeInstruction lockInstruction in ExitLock(iLGenerator, lockObject, lockTaken, instructionsList[i]))
							{
								yield return lockInstruction;
							}
							continue;
						}
					}
				}
				yield return instructionsList[i++];
			}
		}


		public static readonly HarmonyMethod replaceFieldsHarmonyTranspiler = new HarmonyMethod(Method(typeof(RimThreadedHarmony), nameof(ReplaceFieldsTranspiler)));
		public static readonly HarmonyMethod methodLockTranspiler = new HarmonyMethod(Method(typeof(RimThreadedHarmony), nameof(WrapMethodInInstanceLock)));
		public static readonly HarmonyMethod add3Transpiler = new HarmonyMethod(Method(typeof(RimThreadedHarmony), nameof(Add3Transpiler)));
		public static void TranspileFieldReplacements(MethodBase original)
		{
			if (Prefs.LogVerbose)
			{
				Log.Message("RimThreaded is TranspilingFieldReplacements for method: " + original.DeclaringType.FullName + "." + original.Name);
				if(original.DeclaringType.FullName.Equals("Verse.Root"))
				{
                    Log.Message("RT Verse.Root");
					return;
                }
				if (original.Name.Equals("RemoveAll"))
				{
					Log.Message("RemoveAll");
				}
				Log.Message("RimThreaded is done TranspilingFieldReplacements for method: " + original.DeclaringType.FullName + "." + original.Name);
			}
			try
			{
				harmony.Patch(original, transpiler: replaceFieldsHarmonyTranspiler);
			}
			catch (Exception e)
			{
				LogPatchFailure("FieldReplacements", original?.DeclaringType, null, original?.Name, e);
			}
		}

		public static int patchFailureCount;
		internal static string DescribeException(Exception e)
		{
			while ((e is TargetInvocationException || e is TypeInitializationException) && e.InnerException != null)
				e = e.InnerException;
			string message = e.Message ?? "";
			int newline = message.IndexOf('\n');
			if (newline > 0) message = message.Substring(0, newline);
			string[] frames = (e.StackTrace ?? "").Split('\n');
			return e.GetType().Name + ": " + message + (frames.Length > 0 ? " @ " + string.Join(" <- ", frames.Take(4).Select(f => f.Trim())) : "");
		}
		internal static void LogPatchFailure(string kind, Type original, Type patched, string methodName, Exception e)
		{
			Interlocked.Increment(ref patchFailureCount);
			Log.Error("RimThreaded PATCH FAILED [" + kind + "] " + (original != null ? original.FullName : "?") + "." + methodName
				+ (patched != null ? " using " + patched.Name : "") + " : " + DescribeException(e));
		}

		public static void TranspileLockAdd3(Type original, string methodName, Type[] origType = null)
		{
			try
			{
				harmony.Patch(Method(original, methodName, origType), transpiler: add3Transpiler);
			}
			catch (Exception e)
			{
				LogPatchFailure("LockAdd3", original, null, methodName, e);
			}
		}
		public static void Prefix(Type original, Type patched, string methodName, Type[] origType = null, bool destructive = true, int priority = 0, string finalizer = null, string PatchMethod = null, bool NullPatchType = false)
		{
			try
			{
				PrefixInternal(original, patched, methodName, origType, destructive, priority, finalizer, PatchMethod, NullPatchType);
			}
			catch (Exception e)
			{
				LogPatchFailure("Prefix", original, patched, methodName, e);
			}
		}
		private static void PrefixInternal(Type original, Type patched, string methodName, Type[] origType, bool destructive, int priority, string finalizer, string PatchMethod, bool NullPatchType)
		{
			MethodInfo oMethod = Method(original, methodName, origType);
			if (oMethod == null)
				throw new MissingMethodException("target method not found in this RimWorld version");

			Type[] patch_type = null;
			if (origType != null)
			{
				patch_type = new Type[origType.Length];
				Array.Copy(origType, patch_type, origType.Length);

				if (!oMethod.ReturnType.Name.Equals("Void"))
				{
					Type[] temp_type = patch_type;
					patch_type = new Type[temp_type.Length + 1];
					patch_type[0] = oMethod.ReturnType.MakeByRefType();
					Array.Copy(temp_type, 0, patch_type, 1, temp_type.Length);
				}
				if (!oMethod.IsStatic)
				{
					Type[] temp_type = patch_type;
					patch_type = new Type[temp_type.Length + 1];
					patch_type[0] = original;
					Array.Copy(temp_type, 0, patch_type, 1, temp_type.Length);
				}
			}
			if (NullPatchType)
            {
				patch_type = null;
			}
			MethodInfo pMethod = Method(patched, methodName, patch_type);

			if (PatchMethod != null)
            {
				pMethod = Method(patched, PatchMethod, patch_type);
			}
			if (pMethod == null)
				throw new MissingMethodException("patch method " + (PatchMethod ?? methodName) + " not found in " + patched.Name);

			MethodInfo Finalizer;
			HarmonyMethod FinalizerH = null;
			if (finalizer != null)
            {
				Finalizer = Method(patched, finalizer);
				FinalizerH = new HarmonyMethod(Finalizer, priority);
			}

			harmony.Patch(oMethod, prefix: new HarmonyMethod(pMethod, priority), finalizer: FinalizerH);
			if (!destructive)
			{
				nonDestructivePrefixes.Add(pMethod);
			}
		}

		public static void Postfix(Type original, Type patched, string originalMethodName, string patchedMethodName = null)
		{
			try
			{
				MethodInfo oMethod = Method(original, originalMethodName);
				if (oMethod == null)
					throw new MissingMethodException("target method not found in this RimWorld version");
				if (patchedMethodName == null)
					patchedMethodName = originalMethodName;
				MethodInfo pMethod = Method(patched, patchedMethodName);
				if (pMethod == null)
					throw new MissingMethodException("patch method " + patchedMethodName + " not found in " + patched.Name);
				harmony.Patch(oMethod, postfix: new HarmonyMethod(pMethod));
			}
			catch (Exception e)
			{
				LogPatchFailure("Postfix", original, patched, originalMethodName, e);
			}
		}

		public static void Transpile(Type original, Type patched, string methodName, Type[] origType = null, string[] harmonyAfter = null, int priority = 0, string patchMethod = null)
		{
			TranspileInternal(original, patched, methodName, methodName, origType, harmonyAfter, priority, patchMethod);
		}
		public static void Transpile(Type original, Type patched, string methodName, string newMethodName, Type[] origType = null, string[] harmonyAfter = null, int priority = 0, string patchMethod = null)
		{
			TranspileInternal(original, patched, methodName, newMethodName, origType, harmonyAfter, priority, patchMethod);
		}
		private static void TranspileInternal(Type original, Type patched, string methodName, string newMethodName, Type[] origType, string[] harmonyAfter, int priority, string patchMethod)
		{
			try
			{
				MethodInfo oMethod = Method(original, methodName, origType);
				if (oMethod == null)
					throw new MissingMethodException("target method not found in this RimWorld version");
				MethodInfo pMethod = Method(patched, patchMethod ?? newMethodName);
				if (pMethod == null)
					throw new MissingMethodException("transpiler " + (patchMethod ?? newMethodName) + " not found in " + patched.Name);
				HarmonyMethod transpilerMethod = new HarmonyMethod(pMethod, priority)
				{
					after = harmonyAfter
				};
				harmony.Patch(oMethod, transpiler: transpilerMethod);
			}
			catch (Exception e)
			{
				LogPatchFailure("Transpile", original, patched, methodName, e);
			}
		}
		public static void TranspileMethodLock(Type original, string methodName, Type[] origType = null)
		{
			try
			{
				MethodInfo oMethod = Method(original, methodName, origType);
				if (oMethod == null)
					throw new MissingMethodException("target method not found in this RimWorld version");
				harmony.Patch(oMethod, transpiler: methodLockTranspiler);
			}
			catch (Exception e)
			{
				LogPatchFailure("MethodLock", original, null, methodName, e);
			}
		}


		private static void PatchNonDestructiveFixes()
		{
			//---REQUIRED---
			Safe(GameObject_Patch.RunNonDestructivePatches, "GameObject_Patch.RunNonDestructivePatches"); //Prevents Game Crash from wooden floors and others
			Safe(LongEventHandler_Patch.RunNonDestructivePatches, "LongEventHandler_Patch.RunNonDestructivePatches"); //replaced concurrentqueue and run init on new threads

			

			//---Simple---
			Safe(AlertsReadout_Patch.RunNonDestructivesPatches, "AlertsReadout_Patch.RunNonDestructivesPatches"); //this will disable alert checks on ultrafast speed for an added speed boost
			Safe(Area_Patch.RunNonDestructivePatches, "Area_Patch.RunNonDestructivePatches"); //added for sowing area speedup
			Safe(CompSpawnSubplant_Transpile.RunNonDestructivePatches, "CompSpawnSubplant_Transpile.RunNonDestructivePatches"); //fixes bug with royalty spawning subplants
			Safe(Designator_Haul_Patch.RunNonDestructivePatches, "Designator_Haul_Patch.RunNonDestructivePatches"); //add thing to hauling list when user specifically designates it via UI
			Safe(CompForbiddable_Patch.RunNonDestructivePatches, "CompForbiddable_Patch.RunNonDestructivePatches"); //add thing to hauling list when user specifically unforbids it via UI
			Safe(TimeControls_Patch.RunNonDestructivePatches, "TimeControls_Patch.RunNonDestructivePatches"); //allow speed 4
			Safe(ZoneManager_Patch.RunNonDestructivePatches, "ZoneManager_Patch.RunNonDestructivePatches"); //recheck growing zone when upon zone grid add
			Safe(Zone_Patch.RunNonDestructivePatches, "Zone_Patch.RunNonDestructivePatches"); //recheck growing zone when upon check haul destination call
			//HediffGiver_Hypothermia_Transpile.RunNonDestructivePatches(); //speed up for comfy temperature //1.4 reexamine
			Safe(BattleLog_Transpile.RunNonDestructivePatches, "BattleLog_Transpile.RunNonDestructivePatches"); //if still causing issues, rewrite using ThreadSafeLinkedLists
															//GrammarResolver_Transpile.RunNonDestructivePatches();//reexamine complexity
															//GrammarResolverSimple_Transpile.RunNonDestructivePatches();//reexamine complexity
			Safe(HediffSet_Patch.RunNonDestructivePatches, "HediffSet_Patch.RunNonDestructivePatches"); //TODO - replace 270 instances with ThreadSafeLinkedList
			Safe(ThingOwnerThing_Patch.RunNonDestructivePatches, "ThingOwnerThing_Patch.RunNonDestructivePatches"); //reexamine complexity?
			Safe(PathFinder_Patch.RunNonDestructivePatches, "PathFinder_Patch.RunNonDestructivePatches"); //1.6: locks for PathFinder.PushRequest / PawnPathPool
			Safe(TickList_Patch.RunNonDestructivePatches, "TickList_Patch.RunNonDestructivePatches"); //allows multithreaded calls of thing.tick longtick raretick
													   //WorkGiver_ConstructDeliverResources_Transpile.RunNonDestructivePatches(); //reexamine complexity Jobs Of Oppurtunity?
													   //WorkGiver_DoBill_Transpile.RunNonDestructivePatches(); //better way to find bills with cache
													   //Pawn_RelationsTracker_Patch.RunNonDestructivePatches(); //transpile not needed with new threadsafe simplepools
			Safe(PawnCapacitiesHandler_Patch.RunNonDestructivePatches, "PawnCapacitiesHandler_Patch.RunNonDestructivePatches"); //TODO fix transpile for 1 of 2 methods try inside of try perhaps?
			Safe(Zone_Growing_Patch.RunNonDestructivePatches, "Zone_Growing_Patch.RunNonDestructivePatches");
			
			Safe(CellFinder_Patch.RunNonDestructivePatches, "CellFinder_Patch.RunNonDestructivePatches"); //explosion fix
			
			Safe(GenGrid_Patch.RunNonDestructivePatches, "GenGrid_Patch.RunNonDestructivePatches"); //explosion fix
			Safe(ThinkNode_JoinVoluntarilyJoinableLord_Patch.RunDestructivePatches, "ThinkNode_JoinVoluntarilyJoinableLord_Patch.RunDestructivePatches"); //explosion fix
			Safe(TransportShipManager_Patch.RunNonDestructivePatches, "TransportShipManager_Patch.RunNonDestructivePatches");
			//RestUtility_Patch.RunNonDestructivePatches(); // 1.3 explosion fix - not sure why this causes bug with sleeping
			Safe(GrammarResolver_Patch.RunNonDestructivePatches, "GrammarResolver_Patch.RunNonDestructivePatches"); //TODO 1.4 reexamine
			Safe(Sustainer_Patch.RunNonDestructivePatches, "Sustainer_Patch.RunNonDestructivePatches");
			//RegionAndRoomQuery_Patch.RunNonDestructivePatches();
			Safe(MemoryUtility_Patch.RunNonDestructivePatches, "MemoryUtility_Patch.RunNonDestructivePatches"); //Mod memory cleanup on game load, or main menu
			//PawnCapacityUtility_Patch.RunNonDestructivePatches(); //1.4 needed?


            Postfix(typeof(SlotGroup), typeof(HaulingCache), nameof(HaulingCache.Notify_AddedCell)); //recheck growing zone when upon stockpile zone grid add
			Postfix(typeof(ListerHaulables), typeof(HaulingCache), nameof(HaulingCache.Notify_SlotGroupChanged)); //recheck growing zone when upon other actions
			
		}

		private static void PatchDestructiveFixes()
		{
			//---REQUIRED---
			Safe(TickManager_Patch.RunDestructivePatches, "TickManager_Patch.RunDestructivePatches"); //Redirects DoSingleTick to RimThreaded

			//---Main-thread-only calls--- TODO - low priority. These can likely be made more uniform
			Safe(AudioSource_Patch.RunDestructivePatches, "AudioSource_Patch.RunDestructivePatches");
			Safe(AudioSourceMaker_Patch.RunDestructivePatches, "AudioSourceMaker_Patch.RunDestructivePatches");
			Safe(ContentFinder_Texture2D_Patch.RunDestructivePatches, "ContentFinder_Texture2D_Patch.RunDestructivePatches");
			Safe(GenDraw_Patch.RunDestructivePatches, "GenDraw_Patch.RunDestructivePatches"); // DrawMeshNowOrLater (HotSprings and Kijin Race)
			Safe(GraphicDatabaseHeadRecords_Patch.RunDestructivePatches, "GraphicDatabaseHeadRecords_Patch.RunDestructivePatches");
			Safe(Graphics_Patch.RunDestructivePatches, "Graphics_Patch.RunDestructivePatches");//Graphics (Giddy-Up and others)
			Safe(GUIStyle_Patch.RunDestructivePatches, "GUIStyle_Patch.RunDestructivePatches");
			Safe(LightningBoltMeshMaker_Patch.RunDestructivePatches, "LightningBoltMeshMaker_Patch.RunDestructivePatches");
			Safe(MapGenerator_Patch.RunDestructivePatches, "MapGenerator_Patch.RunDestructivePatches");//MapGenerator (Z-levels)
			Safe(MaterialPool_Patch.RunDestructivePatches, "MaterialPool_Patch.RunDestructivePatches");
			Safe(MeshMakerPlanes_Patch.RunDestructivePatches, "MeshMakerPlanes_Patch.RunDestructivePatches");
			Safe(MeshMakerShadows_Patch.RunDestructivePatches, "MeshMakerShadows_Patch.RunDestructivePatches");
			Safe(MoteBubble_Patch.RunDestructivePatches, "MoteBubble_Patch.RunDestructivePatches"); //initial 1.3 patch (MaterialPropertyBlock) - main thread UnityEngine.MaterialPropertyBlock
			Safe(RenderTexture_Patch.RunDestructivePatches, "RenderTexture_Patch.RunDestructivePatches");//RenderTexture (Giddy-Up)
			Safe(SectionLayer_Patch.RunDestructivePatches, "SectionLayer_Patch.RunDestructivePatches");
			Safe(Texture2D_Patch.RunDestructivePatches, "Texture2D_Patch.RunDestructivePatches");//Graphics (Giddy-Up)
			Safe(Text_Patch.RunDestructivePatches, "Text_Patch.RunDestructivePatches"); //unity get_CurFontStyle on main thread

			//---Multithreaded Ticking---
			Safe(FactionManager_Patch.RunDestructivePatches, "FactionManager_Patch.RunDestructivePatches"); //allows multithreaded ticking of factions
			Safe(WildPlantSpawner_Patch.RunDestructivePatches, "WildPlantSpawner_Patch.RunDestructivePatches"); //allows multithreaded icking of WildPlantSpawner
			Safe(WindManager_Patch.RunDestructivePatches, "WindManager_Patch.RunDestructivePatches");//allows multithreaded icking of WindManager													  
			Safe(WorldComponentUtility_Patch.RunDestructivePatches, "WorldComponentUtility_Patch.RunDestructivePatches"); //allows multithreaded icking of WorldComponentUtility
			Safe(WorldObjectsHolder_Patch.RunDestructivePatches, "WorldObjectsHolder_Patch.RunDestructivePatches"); //Class was largely overhauled to allow multithreaded ticking for WorldPawns.Tick()
			Safe(WorldPawns_Patch.RunDestructivePatches, "WorldPawns_Patch.RunDestructivePatches"); //Class was largely overhauled to allow multithreaded ticking for WorldPawns.Tick()
			Safe(Archive_Patch.RunDestructivePatches, "Archive_Patch.RunDestructivePatches"); //explosions fix
			Safe(Alert_ColonistLeftUnburied_Patch.RunDestructivePatches, "Alert_ColonistLeftUnburied_Patch.RunDestructivePatches"); //1.3 explosions fix
			Safe(Alert_MinorBreakRisk_Patch.RunDestructivePatches, "Alert_MinorBreakRisk_Patch.RunDestructivePatches"); //performance rewrite
			Safe(AttackTargetsCache_Patch.RunDestructivesPatches, "AttackTargetsCache_Patch.RunDestructivesPatches"); //TODO: write ExposeData and change concurrentdictionary
			Safe(Battle_Patch.RunDestructivePatches, "Battle_Patch.RunDestructivePatches"); //added lock for battle - could use linkedlist
			Safe(BeautyUtility_Patch.RunDestructivePatches, "BeautyUtility_Patch.RunDestructivePatches"); // 1.3 explosion fix null ref
			
			Safe(Building_Door_Patch.RunDestructivePatches, "Building_Door_Patch.RunDestructivePatches"); //strange bug
			Safe(Building_PlantGrower_Patch.RunNonDestructivePatches, "Building_PlantGrower_Patch.RunNonDestructivePatches");
			Safe(CompCauseGameCondition_Patch.RunDestructivePatches, "CompCauseGameCondition_Patch.RunDestructivePatches"); //TODO - ThreadSafeLinkedList
			Safe(CompSpawnSubplant_Transpile.RunDestructivePatches, "CompSpawnSubplant_Transpile.RunDestructivePatches"); //could use interlock instead
			Safe(Corpse_Patch.RunDestructivePatches, "Corpse_Patch.RunDestructivePatches"); // 1.3 explosion fix
			Safe(DesignationManager_Patch.RunDestructivePatches, "DesignationManager_Patch.RunDestructivePatches"); //added for development build
			Safe(District_Patch.RunDestructivePatches, "District_Patch.RunDestructivePatches"); // 1.3 fix for cachedOpenRoofState null ref - TODO - optimize locks
			Safe(DrugAIUtility_Patch.RunDestructivePatches, "DrugAIUtility_Patch.RunDestructivePatches"); //vanilla bug
			Safe(DynamicDrawManager_Patch.RunDestructivePatches, "DynamicDrawManager_Patch.RunDestructivePatches"); //TODO - candidate for ThreadSafeLinkedList?
			Safe(FilthMaker_Patch.RunDestructivePatches, "FilthMaker_Patch.RunDestructivePatches"); //replacing a few LINQ queries could possibly improve perf 
			Safe(FireUtility_Patch.RunDestructivePatches, "FireUtility_Patch.RunDestructivePatches");
			Safe(FleckStatic_Patch.RunDestructivePatches, "FleckStatic_Patch.RunDestructivePatches"); // 1.3 explosion fix
			Safe(FleckSystemBase_Patch.RunDestructivePatches, "FleckSystemBase_Patch.RunDestructivePatches"); // 1.3 explosion fix
			//FleckSystemBaseFleckThrown_Patch.RunDestructivePatches(); // 1.3 explosion fix
			Safe(FoodUtility_Patch.RunDestructivePatches, "FoodUtility_Patch.RunDestructivePatches"); //1.3 GetMeatSourceCategory Human from NutrientPasteDispenser

			//GenClosest_Patch.RunDestructivePatches(); replaces RegionwiseBFSWorker - no diff noticable
			//GenCollection_Patch.RunDestructivePatches(); may be fixed now that simplepools work

			//GenPlace_Patch.RunDestructivePatches(); // 1.3 TryPlaceThing null thing after kill

			Safe(GenSpawn_Patch.RunDestructivePatches, "GenSpawn_Patch.RunDestructivePatches"); //fixes null.destroy - commonly caused by gysers
			Safe(GenTypes_Patch.RunDestructivePatches, "GenTypes_Patch.RunDestructivePatches"); //possibly occurs with combat extended
			Safe(GlobalControlsUtility_Patch.RunDestructivePatches, "GlobalControlsUtility_Patch.RunDestructivePatches"); //Adds TPS indicator
			Safe(GoodwillSituationManager_Patch.RunDestructivePatches, "GoodwillSituationManager_Patch.RunDestructivePatches"); //initial 1.3 patch
			Safe(GoodwillSituationWorker_MemeCompatibility_Patch.RunDestructivePatches, "GoodwillSituationWorker_MemeCompatibility_Patch.RunDestructivePatches"); //initial 1.3 patch
			Safe(GridsUtility_Patch.RunDestructivePatches, "GridsUtility_Patch.RunDestructivePatches"); // 1.3 explosion fix
			
			//GrammarResolver_Patch.RunDestructivePatches();
            
			Safe(HediffSet_Patch.RunDestructivePatches, "HediffSet_Patch.RunDestructivePatches");
			Safe(HistoryEventsManager_Patch.RunDestructivePatches, "HistoryEventsManager_Patch.RunDestructivePatches"); // 1.3 explosion fix
			Safe(ImmunityHandler_Patch.RunDestructivePatches, "ImmunityHandler_Patch.RunDestructivePatches");			
			Safe(JobDriver_TendPatient_Patch.RunDestructivePatches, "JobDriver_TendPatient_Patch.RunDestructivePatches"); // 1.3 explosion fix
			Safe(JobGiver_Work_Patch.RunDestructivePatches, "JobGiver_Work_Patch.RunDestructivePatches");
			Safe(JobGiver_WanderNearDutyLocation_Patch.RunDestructivePatches, "JobGiver_WanderNearDutyLocation_Patch.RunDestructivePatches");
			Safe(ListerBuildingsRepairable_Patch.RunDestructivePatches, "ListerBuildingsRepairable_Patch.RunDestructivePatches");// 1.3 explosion fix
			Safe(ListerThings_Patch.RunDestructivePatches, "ListerThings_Patch.RunDestructivePatches");			
			//JobMaker_Patch.RunDestructivePatches(); should be fixed by the simplepool patch
			Safe(LongEventHandler_Patch.RunDestructivePatches, "LongEventHandler_Patch.RunDestructivePatches"); //TODO - could use field replacement for conncurrentqueue
			Safe(Lord_Patch.RunDestructivePatches, "Lord_Patch.RunDestructivePatches");
			Safe(LordToil_Siege_Patch.RunDestructivePatches, "LordToil_Siege_Patch.RunDestructivePatches"); //TODO does locks around clears and adds. ThreadSafeLinkedList
			Safe(Map_Patch.RunDestructivePatches, "Map_Patch.RunDestructivePatches"); //TODO - discover root cause
			Safe(MemoryThoughtHandler_Patch.RunDestructivePatches, "MemoryThoughtHandler_Patch.RunDestructivePatches");
			Safe(Messages_Patch.RunDestructivePatches, "Messages_Patch.RunDestructivePatches");// 1.3 explosion fix
			Safe(OverlayDrawer_Patch.RunDestructivePatches, "OverlayDrawer_Patch.RunDestructivePatches"); // 1.3 explosion fix
			Safe(Pawn_ApparelTracker_Patch.RunDestructivePatches, "Pawn_ApparelTracker_Patch.RunDestructivePatches"); //explosions fix			
			Safe(Pawn_HealthTracker_Patch.RunDestructivePatches, "Pawn_HealthTracker_Patch.RunDestructivePatches"); //TODO replace with ThreadSafeLinkedList
			Safe(Pawn_PathFollower_Patch.RunDestructivePatches, "Pawn_PathFollower_Patch.RunDestructivePatches"); // 1.3 null ref pawn?.jobs?.curDriver?.locomotionUrgencySameAs
			Safe(Pawn_RelationsTracker_Patch.RunDestructivePatches, "Pawn_RelationsTracker_Patch.RunDestructivePatches");
			Safe(Pawn_RotationTracker_Patch.RunDestructivePatches, "Pawn_RotationTracker_Patch.RunDestructivePatches");
			Safe(PawnCapacitiesHandler_Patch.RunDestructivePatches, "PawnCapacitiesHandler_Patch.RunDestructivePatches");
			Safe(PawnDestinationReservationManager_Patch.RunDestructivePatches, "PawnDestinationReservationManager_Patch.RunDestructivePatches");			
			Safe(PathFinder_Patch.RunDestructivePatches, "PathFinder_Patch.RunDestructivePatches"); //1.6: FindPathNow runs jobs -> main thread only
			Safe(PawnTextureAtlas_Patch.RunDestructivePatches, "PawnTextureAtlas_Patch.RunDestructivePatches");
			Safe(PawnUtility_Patch.RunDestructivePatches, "PawnUtility_Patch.RunDestructivePatches");
			Safe(Plant_Patch.RunNonDestructivePatches, "Plant_Patch.RunNonDestructivePatches");
			Safe(PlayLog_Patch.RunDestructivePatches, "PlayLog_Patch.RunDestructivePatches");
			Safe(PhysicalInteractionReservationManager_Patch.RunDestructivePatches, "PhysicalInteractionReservationManager_Patch.RunDestructivePatches"); //TODO: write ExposeData and change concurrent dictionary
			Safe(Rand_Patch.RunDestructivePatches, "Rand_Patch.RunDestructivePatches"); //Simple
			Safe(Reachability_Patch.RunDestructivePatches, "Reachability_Patch.RunDestructivePatches");
			Safe(ReachabilityCache_Patch.RunDestructivePatches, "ReachabilityCache_Patch.RunDestructivePatches"); //TODO simplfy instance.fields
			Safe(RealtimeMoteList_Patch.RunDestructivePatches, "RealtimeMoteList_Patch.RunDestructivePatches");
            Safe(RecordWorker_TimeGettingJoy_Patch.RunDestructivePatches, "RecordWorker_TimeGettingJoy_Patch.RunDestructivePatches");
			Safe(RegionAndRoomUpdater_Patch.RunDestructivePatches, "RegionAndRoomUpdater_Patch.RunDestructivePatches");
			Safe(RegionDirtyer_Patch.RunDestructivePatches, "RegionDirtyer_Patch.RunDestructivePatches");
			
			Safe(RegionGrid_Patch.RunDestructivePatches, "RegionGrid_Patch.RunDestructivePatches");
			Safe(RegionLink_Patch.RunDestructivePatches, "RegionLink_Patch.RunDestructivePatches");
			Safe(ResourceCounter_Patch.RunDestructivePatches, "ResourceCounter_Patch.RunDestructivePatches");
			//RoofGrid_Patch is causing problems to the roof notification in 1.3 a fix is also inside the Patch in case  this is needed for something else I am commenting this for now -Senior
			Safe(RulePackDef_Patch.RunDestructivePatches, "RulePackDef_Patch.RunDestructivePatches"); //explosions fix - grammar
			Safe(SteadyEnvironmentEffects_Patch.RunDestructivePatches, "SteadyEnvironmentEffects_Patch.RunDestructivePatches");
			Safe(StoreUtility_Patch.RunDestructivePatches, "StoreUtility_Patch.RunDestructivePatches"); // 1.3 explosion
			Safe(StoryState_Patch.RunDestructivePatches, "StoryState_Patch.RunDestructivePatches"); //WrapMethodInInstanceLock
			Safe(TaleManager_Patch.RunDestructivePatches, "TaleManager_Patch.RunDestructivePatches");
			Safe(ThingGrid_Patch.RunDestructivePatches, "ThingGrid_Patch.RunDestructivePatches");
			Safe(Thing_Patch.RunDestructivePatches, "Thing_Patch.RunDestructivePatches"); //Thing_Patch.TakeDamage is a good candidate for transpile
			Safe(ThinkNode_SubtreesByTag_Patch.RunDestructivePatches, "ThinkNode_SubtreesByTag_Patch.RunDestructivePatches");
			Safe(ThinkNode_ForbidOutsideFlagRadius_Patch.RunDestructivePatches, "ThinkNode_ForbidOutsideFlagRadius_Patch.RunDestructivePatches"); //base method override is double destructive 

			Safe(TileTemperaturesComp_Patch.RunDestructivePatches, "TileTemperaturesComp_Patch.RunDestructivePatches"); //TODO - good simple transpile candidate
			Safe(UniqueIDsManager_Patch.RunDestructivePatches, "UniqueIDsManager_Patch.RunDestructivePatches"); // Simple use of Interlocked.Increment
			Safe(Verb_Patch.RunDestructivePatches, "Verb_Patch.RunDestructivePatches"); // TODO: why is this causing null?
			Safe(WealthWatcher_Patch.RunDestructivePatches, "WealthWatcher_Patch.RunDestructivePatches");
			//WorkGiver_GrowerSow_Patch.RunDestructivePatches();
			
			

			//complex methods that need further review for simplification
			Safe(AttackTargetReservationManager_Patch.RunDestructivePatches, "AttackTargetReservationManager_Patch.RunDestructivePatches");
			Safe(BiomeDef_Patch.RunDestructivePatches, "BiomeDef_Patch.RunDestructivePatches");
			Safe(FloodFiller_Patch.RunDestructivePatches, "FloodFiller_Patch.RunDestructivePatches");//FloodFiller - inefficient global lock - threadstatics might help do these concurrently?
			Safe(JobQueue_Patch.RunDestructivePatches, "JobQueue_Patch.RunDestructivePatches");
			Safe(MapPawns_Patch.RunDestructivePatches, "MapPawns_Patch.RunDestructivePatches"); //TODO: Affects Animal Master Assignment
			Safe(MeditationFocusTypeAvailabilityCache_Patch.RunDestructivePatches, "MeditationFocusTypeAvailabilityCache_Patch.RunDestructivePatches");
			Safe(Pawn_JobTracker_Patch.RunDestructivePatches, "Pawn_JobTracker_Patch.RunDestructivePatches");
			Safe(Pawn_Patch.RunDestructivePatches, "Pawn_Patch.RunDestructivePatches"); // 1) causes strange crash to desktop without error log. 2) not efficient base replacement for PostApplyDamage 
			Safe(Region_Patch.RunDestructivePatches, "Region_Patch.RunDestructivePatches");
			Safe(ReservationManager_Patch.RunDestructivePatches, "ReservationManager_Patch.RunDestructivePatches");
			Safe(Room_Patch.RunDestructivePatches, "Room_Patch.RunDestructivePatches");
			Safe(SituationalThoughtHandler_Patch.RunDestructivePatches, "SituationalThoughtHandler_Patch.RunDestructivePatches"); //TODO replace cachedThoughts with ThreadSafeLinkedList
			Safe(ThingOwnerUtility_Patch.RunDestructivePatches, "ThingOwnerUtility_Patch.RunDestructivePatches"); //TODO fix method reference by index
			
			
															 //-----SOUND-----
			Safe(SampleSustainer_Patch.RunDestructivePatches, "SampleSustainer_Patch.RunDestructivePatches"); // TryMakeAndPlay works better than set_cutoffFrequency, which seems buggy for echo pass filters
			Safe(SoundSizeAggregator_Patch.RunDestructivePatches, "SoundSizeAggregator_Patch.RunDestructivePatches");
			Safe(SoundStarter_Patch.RunDestructivePatches, "SoundStarter_Patch.RunDestructivePatches"); //disabling this patch stops sounds
			Safe(SustainerManager_Patch.RunDestructivePatches, "SustainerManager_Patch.RunDestructivePatches");
															//-----END SOUND-----

			Safe(Building_Bed_Patch.RunDestructivePatches, "Building_Bed_Patch.RunDestructivePatches");//this patch hides a race condition coming from Room.Map
			Safe(RitualObligationTargetWorker_AnyEmptyGrave_Patch.RunDestructivePatches, "RitualObligationTargetWorker_AnyEmptyGrave_Patch.RunDestructivePatches");
			Safe(RitualObligationTargetWorker_GraveWithTarget_Patch.RunDestructivePatches, "RitualObligationTargetWorker_GraveWithTarget_Patch.RunDestructivePatches");
			Safe(PortraitsCache_Patch.RunDestructivePatches, "PortraitsCache_Patch.RunDestructivePatches");
			Safe(RegionLinkDatabase_Patch.RunDestructivePatches, "RegionLinkDatabase_Patch.RunDestructivePatches");
			//RestUtility_Patch.RunDestructivePatches(); // find bed optimization
			Safe(StatWorker_Patch.RunDestructivePatches, "StatWorker_Patch.RunDestructivePatches");
			Safe(BodyDef_Patch.RunDestructivePatches, "BodyDef_Patch.RunDestructivePatches");
			Safe(SubSoundDef_Patch.RunDestructivePatches, "SubSoundDef_Patch.RunDestructivePatches");
        }

        private static void PatchModCompatibility()
		{
			Safe(AndroidTiers_Patch.Patch, "AndroidTiers_Patch.Patch");
			Safe(AwesomeInventory_Patch.Patch, "AwesomeInventory_Patch.Patch");
			Safe(Children_Patch.Patch, "Children_Patch.Patch");
			Safe(CombatExteneded_Patch.Patch, "CombatExteneded_Patch.Patch");
			Safe(Dubs_Skylight_Patch.Patch, "Dubs_Skylight_Patch.Patch");
			Safe(GiddyUpCore_Patch.Patch, "GiddyUpCore_Patch.Patch");
			Safe(Hospitality_Patch.Patch, "Hospitality_Patch.Patch");
			Safe(JobsOfOppurtunity_Patch.Patch, "JobsOfOppurtunity_Patch.Patch");
			Safe(MapReroll_Patch.Patch, "MapReroll_Patch.Patch");
			Safe(PawnRules_Patch.Patch, "PawnRules_Patch.Patch");
			Safe(ZombieLand_Patch.Patch, "ZombieLand_Patch.Patch");
			Safe(VEE_Patch.Patch, "VEE_Patch.Patch");
			Safe(SOS2_Patch.Patch, "SOS2_Patch.Patch");
			Safe(SpeakUp_Patch.Patch, "SpeakUp_Patch.Patch");
			Safe(RimWar_Patch.Patch, "RimWar_Patch.Patch");
			Safe(TD_Enhancement_Patch.Patch, "TD_Enhancement_Patch.Patch");
			Safe(Fluffy_Breakdowns_Patch.Patch, "Fluffy_Breakdowns_Patch.Patch");
			Safe(Better_Message_Placement_Patch.Patch, "Better_Message_Placement_Patch.Patch");
			Safe(TurnItOnAndOff_Patch.Patch, "TurnItOnAndOff_Patch.Patch");
			Safe(AlienRace_Patch.Patch, "AlienRace_Patch.Patch");
			Safe(DubsBadHygiene_Patch.Patch, "DubsBadHygiene_Patch.Patch");
		}
		private static void FullPool_Patch_RunNonDestructivePatches()
		{
			Type original = typeof(FullPool<PawnStatusEffecters.LiveEffecter>);
			Type patched = typeof(FullPool_Patch<PawnStatusEffecters.LiveEffecter>);
			replaceFields.Add(Method(original, "Get"),
				Method(patched, "Get"));
			replaceFields.Add(Method(original, "Return"),
				Method(patched, "Return"));

		}
		private static void SimplePool_Patch_RunNonDestructivePatches()
        {
            replaceFields.Add(Method(typeof(SimplePool<List<float>>), "Get"),
                Method(typeof(SimplePool_Patch<List<float>>), "Get"));
            replaceFields.Add(Method(typeof(SimplePool<List<float>>), "Return"),
                Method(typeof(SimplePool_Patch<List<float>>), "Return"));
            replaceFields.Add(Method(typeof(SimplePool<List<float>>), "get_FreeItemsCount"),
                Method(typeof(SimplePool_Patch<List<float>>), "get_FreeItemsCount"));

            replaceFields.Add(Method(typeof(SimplePool<List<Pawn>>), "Get"),
				Method(typeof(SimplePool_Patch<List<Pawn>>), "Get"));
			replaceFields.Add(Method(typeof(SimplePool<List<Pawn>>), "Return"),
				Method(typeof(SimplePool_Patch<List<Pawn>>), "Return"));
			replaceFields.Add(Method(typeof(SimplePool<List<Pawn>>), "get_FreeItemsCount"),
				Method(typeof(SimplePool_Patch<List<Pawn>>), "get_FreeItemsCount"));

			replaceFields.Add(Method(typeof(SimplePool<List<Sustainer>>), "Get"),
				Method(typeof(SimplePool_Patch<List<Sustainer>>), "Get"));
			replaceFields.Add(Method(typeof(SimplePool<List<Sustainer>>), "Return"),
				Method(typeof(SimplePool_Patch<List<Sustainer>>), "Return"));
			replaceFields.Add(Method(typeof(SimplePool<List<Sustainer>>), "get_FreeItemsCount"),
				Method(typeof(SimplePool_Patch<List<Sustainer>>), "get_FreeItemsCount"));

			replaceFields.Add(Method(typeof(SimplePool<List<IntVec3>>), "Get"),
				Method(typeof(SimplePool_Patch<List<IntVec3>>), "Get"));
			replaceFields.Add(Method(typeof(SimplePool<List<IntVec3>>), "Return"),
				Method(typeof(SimplePool_Patch<List<IntVec3>>), "Return"));
			replaceFields.Add(Method(typeof(SimplePool<List<IntVec3>>), "get_FreeItemsCount"),
				Method(typeof(SimplePool_Patch<List<IntVec3>>), "get_FreeItemsCount"));

			replaceFields.Add(Method(typeof(SimplePool<List<Thing>>), "Get"),
				Method(typeof(SimplePool_Patch<List<Thing>>), "Get"));
			replaceFields.Add(Method(typeof(SimplePool<List<Thing>>), "Return"),
				Method(typeof(SimplePool_Patch<List<Thing>>), "Return"));
			replaceFields.Add(Method(typeof(SimplePool<List<Thing>>), "get_FreeItemsCount"),
				Method(typeof(SimplePool_Patch<List<Thing>>), "get_FreeItemsCount"));

			replaceFields.Add(Method(typeof(SimplePool<List<Gizmo>>), "Get"),
				Method(typeof(SimplePool_Patch<List<Gizmo>>), "Get"));
			replaceFields.Add(Method(typeof(SimplePool<List<Gizmo>>), "Return"),
				Method(typeof(SimplePool_Patch<List<Gizmo>>), "Return"));
			replaceFields.Add(Method(typeof(SimplePool<List<Gizmo>>), "get_FreeItemsCount"),
				Method(typeof(SimplePool_Patch<List<Gizmo>>), "get_FreeItemsCount"));

			replaceFields.Add(Method(typeof(SimplePool<List<Hediff>>), "Get"),
				Method(typeof(SimplePool_Patch<List<Hediff>>), "Get"));
			replaceFields.Add(Method(typeof(SimplePool<List<Hediff>>), "Return"),
				Method(typeof(SimplePool_Patch<List<Hediff>>), "Return"));
			replaceFields.Add(Method(typeof(SimplePool<List<Hediff>>), "get_FreeItemsCount"),
				Method(typeof(SimplePool_Patch<List<Hediff>>), "get_FreeItemsCount"));

			replaceFields.Add(Method(typeof(SimplePool<HashSet<IntVec3>>), "Get"),
				Method(typeof(SimplePool_Patch<HashSet<IntVec3>>), "Get"));
			replaceFields.Add(Method(typeof(SimplePool<HashSet<IntVec3>>), "Return"),
				Method(typeof(SimplePool_Patch<HashSet<IntVec3>>), "Return"));
			replaceFields.Add(Method(typeof(SimplePool<HashSet<IntVec3>>), "get_FreeItemsCount"),
				Method(typeof(SimplePool_Patch<HashSet<IntVec3>>), "get_FreeItemsCount"));

			replaceFields.Add(Method(typeof(SimplePool<HashSet<Pawn>>), "Get"),
				Method(typeof(SimplePool_Patch<HashSet<Pawn>>), "Get"));
			replaceFields.Add(Method(typeof(SimplePool<HashSet<Pawn>>), "Return"),
				Method(typeof(SimplePool_Patch<HashSet<Pawn>>), "Return"));
			replaceFields.Add(Method(typeof(SimplePool<HashSet<Pawn>>), "get_FreeItemsCount"),
				Method(typeof(SimplePool_Patch<HashSet<Pawn>>), "get_FreeItemsCount"));

            replaceFields.Add(Method(typeof(SimplePool<Job>), "Get"),
                Method(typeof(SimplePool_Patch<Job>), "Get"));
            replaceFields.Add(Method(typeof(SimplePool<Job>), "Return"),
                Method(typeof(SimplePool_Patch<Job>), "Return"));
            replaceFields.Add(Method(typeof(SimplePool<Job>), "get_FreeItemsCount"),
                Method(typeof(SimplePool_Patch<Job>), "get_FreeItemsCount"));

            replaceFields.Add(Method(typeof(SimplePool<Toil>), "Get"),
                Method(typeof(SimplePool_Patch<Toil>), "Get"));
            replaceFields.Add(Method(typeof(SimplePool<Toil>), "Return"),
                Method(typeof(SimplePool_Patch<Toil>), "Return"));
            replaceFields.Add(Method(typeof(SimplePool<Toil>), "get_FreeItemsCount"),
                Method(typeof(SimplePool_Patch<Toil>), "get_FreeItemsCount"));

            replaceFields.Add(Method(typeof(SimplePool<RegionProcessorClosestThingReachable>), "Get"),
                Method(typeof(SimplePool_Patch<RegionProcessorClosestThingReachable>), "Get"));
            replaceFields.Add(Method(typeof(SimplePool<RegionProcessorClosestThingReachable>), "Return"),
                Method(typeof(SimplePool_Patch<RegionProcessorClosestThingReachable>), "Return"));
            replaceFields.Add(Method(typeof(SimplePool<RegionProcessorClosestThingReachable>), "get_FreeItemsCount"),
                Method(typeof(SimplePool_Patch<RegionProcessorClosestThingReachable>), "get_FreeItemsCount"));

        }


		private static void Dijkstra_Patch_RunNonDestructivePatches()
		{
			//replaceFields.Add(Method(typeof(Dijkstra<Region>), "Run", new Type[] {
			//	typeof(Region),
			//	typeof(Func<Region, IEnumerable<Region>>),
			//	typeof(Func<Region, Region, float>),
			//	typeof(List<KeyValuePair<Region, float>>),
			//	typeof(Dictionary<Region, Region>)
			//}),
			//	Method(typeof(Dijkstra_Patch<Region>), "Run", new Type[] {
			//	typeof(Region),
			//	typeof(Func<Region, IEnumerable<Region>>),
			//	typeof(Func<Region, Region, float>),
			//	typeof(List<KeyValuePair<Region, float>>),
			//	typeof(Dictionary<Region, Region>)
			//	}));

			replaceFields.Add(Method(typeof(Dijkstra<IntVec3>), "Run", new Type[] {
				typeof(IEnumerable<IntVec3>),
				typeof(Func<IntVec3, IEnumerable<IntVec3>>),
				typeof(Func<IntVec3, IntVec3, float>),
				typeof(List<KeyValuePair<IntVec3, float>>),
				typeof(Dictionary<IntVec3, IntVec3>)
			}),
				Method(typeof(Dijkstra_Patch<IntVec3>), "Run", new Type[] {
				typeof(IEnumerable<IntVec3>),
				typeof(Func<IntVec3, IEnumerable<IntVec3>>),
				typeof(Func<IntVec3, IntVec3, float>),
				typeof(List<KeyValuePair<IntVec3, float>>),
				typeof(Dictionary<IntVec3, IntVec3>) }
				));

			replaceFields.Add(Method(typeof(Dijkstra<IntVec3>), "Run", new Type[] {
				typeof(IntVec3),
				typeof(Func<IntVec3, IEnumerable<IntVec3>>),
				typeof(Func<IntVec3, IntVec3, float>),
				typeof(Dictionary<IntVec3, float>),
				typeof(Dictionary<IntVec3, IntVec3>)
			}),
				Method(typeof(Dijkstra_Patch<IntVec3>), "Run", new Type[] {
				typeof(IntVec3),
				typeof(Func<IntVec3, IEnumerable<IntVec3>>),
				typeof(Func<IntVec3, IntVec3, float>),
				typeof(Dictionary<IntVec3, float>),
				typeof(Dictionary<IntVec3, IntVec3>) }
				));

			replaceFields.Add(Method(typeof(Dijkstra<Region>), "Run", new Type[] {
				typeof(IEnumerable<Region>),
				typeof(Func<Region, IEnumerable<Region>>),
				typeof(Func<Region, Region, float>),
				typeof(Dictionary<Region, float>),
				typeof(Dictionary<Region, Region>)
			}),
				Method(typeof(Dijkstra_Patch<Region>), "Run", new Type[] {
				typeof(IEnumerable<Region>),
				typeof(Func<Region, IEnumerable<Region>>),
				typeof(Func<Region, Region, float>),
				typeof(Dictionary<Region, float>),
				typeof(Dictionary<Region, Region>) }
				));

			replaceFields.Add(Method(typeof(Dijkstra<int>), "Run", new Type[] {
				typeof(IEnumerable<int>),
				typeof(Func<int, IEnumerable<int>>),
				typeof(Func<int, int, float>),
				typeof(Dictionary<int, float>),
				typeof(Dictionary<int, int>)
			}),
				Method(typeof(Dijkstra_Patch<int>), "Run", new Type[] {
				typeof(IEnumerable<int>),
				typeof(Func<int, IEnumerable<int>>),
				typeof(Func<int, int, float>),
				typeof(Dictionary<int, float>),
				typeof(Dictionary<int, int>) }
				));

		}

	}

}