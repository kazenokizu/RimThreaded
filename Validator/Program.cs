using System.Threading;
// RimThreaded patch validator: runs the mod's real patch registration (Harmony) against the real RimWorld
// Assembly-CSharp.dll on .NET 8, without launching the game. Harmony JIT-compiles every patched method when it
// installs it, so missing targets, wrong patch signatures and invalid transpiler IL all surface here as exceptions.
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using System.Reflection.Emit;

public static class Program
{
    public static readonly List<string> Failures = new List<string>();
    public static readonly List<string> GameErrors = new List<string>();
    public static readonly List<string> GameWarnings = new List<string>();
    static readonly object gate = new object();
    static string runDir;

    public static int Main(string[] args)
    {
        runDir = Path.GetFullPath(args[0]);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.WriteLine("start");
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string p = Path.Combine(runDir, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        Assembly game = Assembly.LoadFrom(Path.Combine(runDir, "Assembly-CSharp.dll"));
        Assembly mod = Assembly.LoadFrom(Path.Combine(runDir, "RimThreaded.dll"));
        var h = new Harmony("validator.hooks");

        Console.WriteLine("phase: log hooks");
        // 1. swallow game logging (Unity natives are not available) and keep the text
        Type logType = game.GetType("Verse.Log");
        foreach (MethodInfo m in logType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name is "Message" or "Warning" or "Error" or "ErrorOnce" or "WarningOnce" or "Clear")
            {
                try { Console.WriteLine("hook " + m); h.Patch(m, prefix: new HarmonyMethod(typeof(Program), nameof(LogPrefix))); }
                catch (Exception e) { Console.WriteLine("log hook failed " + m + ": " + e.Message); }
            }
        }

        Console.WriteLine("phase: state");
        // 2. minimal static state the mod reads during registration
        Type prefs = game.GetType("Verse.Prefs");
        Type prefsData = game.GetType("Verse.PrefsData");
        AccessTools.Field(prefs, "data").SetValue(null, Activator.CreateInstance(prefsData));
        Type modType = mod.GetType("RimThreaded.RimThreadedMod");
        string rwVersion = Environment.GetEnvironmentVariable("RW_VERSION") ?? "1.6";
        string asmDir = Path.Combine(runDir, rwVersion, "Assemblies");
        Directory.CreateDirectory(asmDir);
        File.Copy(Path.Combine(runDir, "replacements_" + rwVersion + ".json"), Path.Combine(asmDir, "replacements_" + rwVersion + ".json"), true);
        AccessTools.Field(modType, "replacementsFolder").SetValue(null, asmDir);
        AccessTools.Field(modType, "replacementsJsonPath").SetValue(null, Path.Combine(asmDir, "replacements_" + rwVersion + ".json"));

        Console.WriteLine("phase: finalizers");
        // 3. finalizers: turn every exception inside registration code into a recorded failure and continue
        Type rth = mod.GetType("RimThreaded.RimThreadedHarmony");
        var fin = new HarmonyMethod(typeof(Program), nameof(Finalizer));
        int hooked = 0;
        foreach (Type t in SafeTypes(mod))
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                bool isRegistration = m.Name.StartsWith("Run") && m.Name.Contains("Patches");
                bool isHelper = t == rth && m.Name is "Prefix" or "Postfix" or "Transpile" or "TranspileMethodLock" or "TranspileFieldReplacements"
                    or "TranspileLockAdd3" or "LoadFieldReplacements" or "AddAdditionalReplacements" or "ApplyFieldReplacements"
                    or "PatchDestructiveFixes" or "PatchNonDestructiveFixes" or "PatchModCompatibility";
                if (!(isRegistration || (t == rth && isHelper)) || m.IsAbstract || m.ContainsGenericParameters) continue;
                try { h.Patch(m, finalizer: fin); hooked++; }
                catch (Exception e) { Failures.Add("HOOK " + t.Name + "." + m.Name + ": " + e.Message); }
            }
        }
        Console.WriteLine("hooked " + hooked + " registration methods");

        Console.WriteLine("phase: recording hooks");
        // 3b. record every Harmony.Patch call the mod makes, including ones whose final JIT fails here (Unity ECall targets):
        //     those never reach Harmony.GetPatchInfo, so the signature and dropped-parameter checks used to skip them.
        foreach (MethodInfo pm in typeof(Harmony).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == "Patch"))
        {
            try { h.Patch(pm, prefix: new HarmonyMethod(typeof(Program), nameof(RecordPatch))); }
            catch (Exception e) { Console.WriteLine("record hook failed " + pm + ": " + e.Message); }
        }
        // 3c. record reflection lookups made from mod code that return null (a null MethodInfo fed to CreateDelegate killed startup in-game)
        int lookupHooks = 0;
        foreach (MethodInfo am in typeof(AccessTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsGenericMethodDefinition && LookupNames.Contains(m.Name) && typeof(MemberInfo).IsAssignableFrom(m.ReturnType)))
        {
            try { h.Patch(am, postfix: new HarmonyMethod(typeof(Program), nameof(RecordNullLookup))); lookupHooks++; }
            catch (Exception e) { Console.WriteLine("lookup hook failed " + am + ": " + e.Message); }
        }
        Console.WriteLine("hooked " + lookupHooks + " reflection lookup methods");

        // 3d. check every Delegate.CreateDelegate the mod makes against the target's real signature (return type included). For Unity ECall
        //     targets CoreCLR refuses to bind at all, so a stand-in delegate is returned to let the rest of the static initializer run.
        foreach (MethodInfo cd in typeof(Delegate).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreateDelegate" && m.GetParameters().Any(p => p.ParameterType == typeof(MethodInfo))))
        {
            try { h.Patch(cd, prefix: new HarmonyMethod(typeof(Program), nameof(CheckCreateDelegate))); Console.WriteLine("delegate hook " + cd); }
            catch (Exception e) { Console.WriteLine("delegate hook failed " + cd + ": " + e.Message); }
        }
        Console.WriteLine("phase: cctor");
        // 4. run the real static constructor: LoadFieldReplacements, ApplyFieldReplacements, all patches
        try { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(rth.TypeHandle); }
        catch (Exception e) { Failures.Add("CCTOR " + Unwrap(e)); }

        Console.WriteLine("phase: all static initializers");
        Type modClass = mod.GetType("RimThreaded.RimThreadedMod");
        AccessTools.Field(modClass, "Settings").SetValue(null, Activator.CreateInstance(mod.GetType("RimThreaded.RimThreadedSettings")));
        // 4b. Mono runs a beforefieldinit type's static initializers when one of its methods is first called; CoreCLR only when a static
        //     field is first read. So Texture2D_Patch's initializers ran (and threw) in the game but never here. Run every one explicitly.
        //     RimThreaded.RimThreaded runs too (it is what the game calls), with worker/monitor thread start-up stubbed out.
        Type rtMain = mod.GetType("RimThreaded.RimThreaded");
        h.Patch(AccessTools.Method(rtMain, "CreateWorkerThreads"), prefix: new HarmonyMethod(typeof(Program), nameof(SkipCctor)));
        h.Patch(AccessTools.Method(rtMain, "MonitorThreads"), prefix: new HarmonyMethod(typeof(Program), nameof(SkipCctor)));
        if (Environment.GetEnvironmentVariable("TRACE_CTOR") is string traceCtor && traceCtor.Length > 0)
        {
            Type tt = game.GetType(traceCtor) ?? AccessTools.TypeByName(traceCtor);
            h.Patch(AccessTools.Constructor(tt, Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Program), nameof(TraceCtor)));
        }
        int initRan = 0;
        foreach (Type t in SafeTypes(mod).Where(t => !t.ContainsGenericParameters).OrderBy(t => t.FullName))
        {
            try { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle); initRan++; }
            catch (Exception e)
            {
                string chain = "";
                for (Exception x = e; x != null; x = x.InnerException)
                {
                    string tr; try { tr = x.StackTrace ?? ""; } catch (Exception) { tr = "?"; }
                    chain += " || " + x.GetType().Name + ": " + string.Join(" | ", tr.Split('\n').Take(8).Select(l => l.Trim()));
                }
                Failures.Add("STATIC INIT " + t.FullName + " -> " + Unwrap(e, 14));
                File.AppendAllText("/tmp/validator_static_init_chains.txt", t.FullName + chain + "\n");
            }
        }
        Console.WriteLine("ran static initializers of " + initRan + " mod types");
        Assembly genAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RimThreadedReplacements");
        // Every thread-static initializer, one by one (in the game they run as one chain, so the first exception would hide the rest).
        // CoreCLR enforces member access from the generated (dynamic) assembly to private vanilla types; Mono does not, and the same
        // entries (e.g. GrammarResolver.rules) ran on Mono in 1.3/1.4. Those are counted separately.
        {
            var initMethods = SafeTypes(mod).Concat(genAsm != null ? genAsm.GetTypes() : new Type[0])
                .Where(t => !t.ContainsGenericParameters)
                .Select(t => t.GetMethod("InitializeThreadStatics", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null))
                .Where(m => m != null).ToList();
            int ok = 0, access = 0;
            foreach (MethodInfo im in initMethods)
            {
                try { im.Invoke(null, null); ok++; }
                catch (TargetInvocationException e) when (e.InnerException is MethodAccessException || e.InnerException is FieldAccessException || e.InnerException is TypeAccessException)
                { access++; Console.WriteLine("  access-blocked here (not on Mono): " + im.DeclaringType.Name + ": " + e.InnerException.Message.Split('\n')[0]); }
                catch (Exception e) { Failures.Add("THREAD-STATIC INIT " + im.DeclaringType.FullName + " -> " + Unwrap(e)); }
            }
            Console.WriteLine("=== thread-static initializers run individually: " + initMethods.Count + ", ok " + ok + ", blocked by CoreCLR access checks " + access);
        }
        if (genAsm != null)
        {
            var genFields = genAsm.GetTypes().SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (t, f))).ToList();
            var odd = genFields.Where(x => !x.f.FieldType.IsValueType && !System.Text.RegularExpressions.Regex.IsMatch(x.f.FieldType.FullName ?? "", @"^System\.Collections\.(Generic\.)?(List|Dictionary|HashSet|Queue|Stack|SortedList|LinkedList)`|^System\.Text\.StringBuilder")).ToList();
            Console.WriteLine("=== generated thread-static fields: " + genFields.Count + "; not plain collections: " + odd.Count);
            // RimThreaded's static constructor ran InitializeAllThreadStatics on this thread: every constructible field must now hold an object
            var unset = genFields.Where(x => !x.f.FieldType.IsValueType && x.f.FieldType.GetConstructor(Type.EmptyTypes) != null && x.f.GetValue(null) == null).ToList();
            Console.WriteLine("=== generated thread-static fields still null on the initializing thread: " + unset.Count + " (expected: only SelfInitialized entries)");
            foreach (var x in unset) Console.WriteLine("  " + x.t.Name + "." + x.f.Name);
            foreach (var x in odd) Console.WriteLine("  " + x.t.Name + "." + x.f.Name + " : " + x.f.FieldType.FullName?.Split(',')[0]);
        }

        if (args.Length > 2 && args[1] == "identity")
        {
            var idh = new Harmony("validator.identity");
            foreach (string spec in args.Skip(2))
            {
                string[] parts = spec.Split("::");
                Type ty = game.GetType(parts[0]) ?? AccessTools.TypeByName(parts[0]);
                MethodBase mb = AccessTools.Method(ty, parts[1]);
                try { idh.Patch(mb, transpiler: new HarmonyMethod(typeof(Program), nameof(IdentityTranspiler))); Console.WriteLine("identity OK  " + spec); }
                catch (Exception e) { Console.WriteLine("identity FAIL " + spec + " : " + e.GetType().Name); }
            }
        }
        if (args.Length > 2 && args[1] == "sig")
        {
            foreach (string spec in args.Skip(2))
            {
                string[] parts = spec.Split("::");
                Type ty = game.GetType(parts[0]) ?? AccessTools.TypeByName(parts[0]);
                if (ty == null) { Console.WriteLine("sig: type not found " + parts[0]); continue; }
                foreach (MethodBase m in ty.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => parts.Length < 2 || m.Name == parts[1]))
                    Console.WriteLine("sig: " + ty.FullName + " :: " + (m.IsStatic ? "static " : "") + ((MethodInfo)m).ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name)) + ")");
            }
        }
        if (args.Length > 1 && args[1] == "identity-scan")
        {
            var idh = new Harmony("validator.identityscan");
            int ok = 0, fail = 0, n = 0; var failed = new List<string>();
            foreach (Type t in SafeTypes(game).Where(t => t.Name.Contains(">d__")))
            {
                MethodBase mb = t.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (mb == null || Harmony.GetPatchInfo(mb) != null) continue;
                if (n++ > 150) break;
                try { idh.Patch(mb, transpiler: new HarmonyMethod(typeof(Program), nameof(IdentityTranspiler))); ok++; }
                catch (Exception e) { fail++; failed.Add(t.FullName + " " + e.GetType().Name); }
            }
            Console.WriteLine("identity-scan ok=" + ok + " fail=" + fail);
            foreach (string f in failed.Take(20)) Console.WriteLine("  " + f);
        }
        if (args.Length > 2 && args[1] == "dump")
        {
            foreach (string spec in args.Skip(2))
            {
                string[] parts = spec.Split("::");
                Type ty = game.GetType(parts[0]) ?? AccessTools.TypeByName(parts[0]);
                MethodBase mb = parts[1] == ".ctor" ? (MethodBase)AccessTools.GetDeclaredConstructors(ty).First(c => !c.IsStatic) : AccessTools.Method(ty, parts[1]);
                Console.WriteLine("-- " + spec);
                int n = 0;
                foreach (var ci in PatchProcessor.GetOriginalInstructions(mb))
                    Console.WriteLine("   [" + (n++) + "] " + ci.opcode + " " + (ci.operand is Label[] ? "labels" : ci.operand is MemberInfo mi ? mi.DeclaringType?.Name + "::" + mi : ci.operand));
            }
        }
        if (args.Length > 2 && args[1] == "inspect")
        {
            MethodInfo xf = AccessTools.Method(rth, "ReplaceFieldsTranspiler");
            foreach (string spec in args.Skip(2))
            {
                string[] parts = spec.Split("::");
                Type ty = game.GetType(parts[0]) ?? AccessTools.TypeByName(parts[0]);
                MethodBase mb = AccessTools.Method(ty, parts[1]) ?? (MethodBase)AccessTools.Constructor(ty, null);
                var before = PatchProcessor.GetOriginalInstructions(mb).ToList();
                var snapshot = before.Select(i => (i.opcode, i.operand)).ToList();
                var after = ((IEnumerable<CodeInstruction>)xf.Invoke(null, new object[] { PatchProcessor.GetOriginalInstructions(mb), null })).ToList();
                Console.WriteLine("-- " + spec + " (" + before.Count + " instrs)");
                for (int i = 0; i < before.Count && i < after.Count; i++)
                    if (!Equals(snapshot[i].operand, after[i].operand) || snapshot[i].opcode != after[i].opcode)
                        Console.WriteLine("   [" + i + "] " + snapshot[i].opcode + " " + snapshot[i].operand + "  =>  " + after[i].opcode + " " + after[i].operand);
            }
        }
        Console.WriteLine();
        if (args.Length > 2 && args[1] == "ildiff")
        {
            List<string> Dump(List<CodeInstruction> list)
            {
                var names = new Dictionary<Label, string>();
                int n = 0;
                foreach (var ci in list) if (ci.labels.Count > 0) { string nm = "L" + (n++); foreach (var l in ci.labels) names[l] = nm; }
                string Op(object o) => o is Label l ? (names.TryGetValue(l, out var s) ? s : "L?") : o is Label[] ls ? "[" + string.Join(",", ls.Select(x => names.TryGetValue(x, out var s2) ? s2 : "L?")) + "]" : o is MemberInfo mi ? mi.DeclaringType?.Name + "::" + mi : o?.ToString();
                return list.Select(ci => (ci.labels.Count > 0 ? names[ci.labels[0]] + ": " : "    ") + ci.opcode + " " + Op(ci.operand)).ToList();
            }
            foreach (string spec in args.Skip(2))
            {
                string[] parts = spec.Split("::");
                Type ty = game.GetType(parts[0]) ?? AccessTools.TypeByName(parts[0]);
                MethodBase mb = parts.Length > 2 ? AccessTools.GetDeclaredMethods(ty).Where(m => m.Name == parts[1]).ElementAt(int.Parse(parts[2])) : AccessTools.Method(ty, parts[1]);
                File.WriteAllLines("/tmp/ildiff_before.txt", Dump(PatchProcessor.GetOriginalInstructions(mb).ToList()));
                File.WriteAllLines("/tmp/ildiff_after.txt", Dump(PatchProcessor.GetCurrentInstructions(mb, 1000).ToList()));
                Console.WriteLine("=== ildiff written for " + spec);
            }
            return 0;
        }
        JsonFieldCheck(game, mod, Path.Combine(asmDir, "replacements_" + rwVersion + ".json"));
        // Harmony 2.3.3 on CoreCLR rejects even a do-nothing transpiler on many compiler-generated iterators (identity-scan: 84 of 151
        // untouched game iterators fail), so an InvalidProgramException on a ">d__" MoveNext says nothing about the mod's own transpiler.
        bool Unverifiable(string x) => x.Contains("ECall methods must be packaged") || (x.Contains(">d__") && x.Contains("InvalidProgramException"))
            || (x.StartsWith("STATIC INIT RimThreaded.RimThreaded ") && x.Contains("MethodAccessException") && x.Contains("_Replacement.InitializeThreadStatics"));
        File.WriteAllLines("/tmp/validator_failures_full.txt", GameErrors.Concat(Failures));
        var ecall = GameErrors.Concat(Failures).Where(Unverifiable).ToList();
        var real = GameErrors.Concat(Failures).Where(x => !Unverifiable(x)).Distinct().ToList();
        Console.WriteLine("=== REAL failures: " + real.Count);
        foreach (string s in real) Console.WriteLine("  " + s.Replace("\n", " ").Substring(0, Math.Min(600, s.Replace("\n", " ").Length)));
        Console.WriteLine("=== not JIT-verifiable on this runtime (Unity internal call in body, or compiler-generated iterator; the transpiler itself ran): " + ecall.Count);
        foreach (string s in ecall.Take(400)) Console.WriteLine("  " + s.Replace("\n", " ").Substring(0, Math.Min(200, s.Replace("\n", " ").Length)));
        var patched = Harmony.GetAllPatchedMethods().ToList();
        File.WriteAllLines("/tmp/patched_methods_detail.txt", patched.Select(m => { var pi = Harmony.GetPatchInfo(m); string P(IEnumerable<Patch> ps, string k) => string.Join(" ", ps.Where(x => x.owner == "majorhoff.rimthreaded").Select(x => k + ":" + x.PatchMethod.DeclaringType?.Name + "." + x.PatchMethod.Name)); return m.DeclaringType?.FullName + "::" + m.Name + "(" + string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name)) + ")\t" + P(pi.Prefixes, "pre") + " " + P(pi.Postfixes, "post") + " " + P(pi.Transpilers, "tr") + " " + P(pi.Finalizers, "fin"); }));
        File.WriteAllLines("/tmp/patched_methods.txt", patched.Select(m => m.DeclaringType?.FullName + "::" + m.Name + "(" + string.Join(",", m.GetParameters().Select(x => x.ParameterType.Name)) + ")")
            .Concat(Recorded.Select(r => r.original.DeclaringType?.FullName + "::" + r.original.Name + "(" + string.Join(",", r.original.GetParameters().Select(x => x.ParameterType.Name)) + ")")).Distinct().OrderBy(x => x));
        Console.WriteLine("=== methods patched in process: " + patched.Count);
        SignatureCheck(patched);
        NoOpTranspilerCheck(patched);
        if (Environment.GetEnvironmentVariable("DRIFT") == "1") DriftCheck(patched);
        DroppedParamCheck(patched);
        var unityTargets = Recorded.Where(r => r.original.DeclaringType?.Assembly.GetName().Name != "Assembly-CSharp").Select(r => r.original.DeclaringType?.FullName + "." + r.original.Name + "(" + string.Join(",", r.original.GetParameters().Select(x => x.ParameterType.Name)) + ") <- " + r.kind + " " + Name(r.patch)).Distinct().OrderBy(x => x).ToList();
        Console.WriteLine("=== patches on methods outside Assembly-CSharp (Unity/.NET): " + unityTargets.Count);
        foreach (string u in unityTargets) Console.WriteLine("  " + u);
        Console.WriteLine("=== delegate bindings checked: " + DelegatesChecked + ", signature mismatches or null targets: " + DelegateProblems.Distinct().Count());
        foreach (string s in DelegateProblems.Distinct().OrderBy(x => x)) Console.WriteLine("  " + s);
        Console.WriteLine("=== reflection lookups from mod code that returned null: " + NullLookups.Distinct().Count());
        foreach (string s in NullLookups.Distinct().OrderBy(x => x)) Console.WriteLine("  " + s);
        LockCoverageCheck(game);
        UnsetResultCheck(patched);
        Console.WriteLine("=== patched iterator/closure methods: " + string.Join(", ", patched.Where(m => m.DeclaringType.Name.Contains(">d__")).Select(m => m.DeclaringType.DeclaringType?.Name + "." + m.DeclaringType.Name)));
        return real.Count == 0 ? 0 : 1;
    }

    // Harmony does not check that prefix/postfix parameter types agree with the original; the JIT only catches some mismatches.
    // Every rimthreaded prefix/postfix: the installed ones (Harmony patch info) plus the ones Harmony could not finish installing in this
    // harness (Unity ECall targets), taken from the recorded Harmony.Patch calls.
    static List<(MethodBase original, MethodInfo pm, bool prefix, bool installed)> RtPatchPairs(List<MethodBase> patched)
    {
        var pairs = new List<(MethodBase, MethodInfo, bool, bool)>();
        var seen = new HashSet<(MethodBase, MethodInfo)>();
        foreach (MethodBase original in patched)
        {
            Patches info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            foreach (Patch p in info.Prefixes.Where(x => x.owner == "majorhoff.rimthreaded")) if (seen.Add((original, p.PatchMethod))) pairs.Add((original, p.PatchMethod, true, true));
            foreach (Patch p in info.Postfixes.Where(x => x.owner == "majorhoff.rimthreaded")) if (seen.Add((original, p.PatchMethod))) pairs.Add((original, p.PatchMethod, false, true));
        }
        foreach (var r in Recorded.Where(r => r.kind == "prefix" || r.kind == "postfix"))
            if (seen.Add((r.original, r.patch))) pairs.Add((r.original, r.patch, r.kind == "prefix", false));
        return pairs;
    }

    static void SignatureCheck(List<MethodBase> patched)
    {
        int checkedPatches = 0; var problems = new List<string>();
        var pairs = RtPatchPairs(patched);
        foreach (var pair in pairs)
        {
            MethodBase original = pair.original;
            var ops = original.GetParameters();
            {
                {
                    checkedPatches++;
                    MethodInfo pm = pair.pm;
                    foreach (ParameterInfo pp in pm.GetParameters())
                    {
                        string n = pp.Name;
                        Type pt = pp.ParameterType.IsByRef ? pp.ParameterType.GetElementType() : pp.ParameterType;
                        if (n == "__instance")
                        {
                            if (!pt.IsAssignableFrom(original.DeclaringType) && !original.DeclaringType.IsAssignableFrom(pt))
                                problems.Add(Name(original) + " <- " + Name(pm) + ": __instance " + pt + " vs " + original.DeclaringType);
                        }
                        else if (n == "__result")
                        {
                            Type rt = (original as MethodInfo)?.ReturnType;
                            if (rt == null || rt == typeof(void) || !(pt == rt || pt.IsAssignableFrom(rt)))
                                problems.Add(Name(original) + " <- " + Name(pm) + ": __result " + pt + " vs return " + rt);
                        }
                        else if (n.StartsWith("__") || n.StartsWith("___")) { }
                        else
                        {
                            ParameterInfo op = ops.FirstOrDefault(o => o.Name == n);
                            if (op == null) problems.Add(Name(original) + " <- " + Name(pm) + ": parameter '" + n + "' not in original");
                            else
                            {
                                Type ot = op.ParameterType.IsByRef ? op.ParameterType.GetElementType() : op.ParameterType;
                                bool refMismatch = pp.ParameterType.IsByRef && !op.ParameterType.IsByRef && !op.IsOut && false;
                                if (ot != pt && !pt.IsAssignableFrom(ot))
                                    problems.Add(Name(original) + " <- " + Name(pm) + ": parameter '" + n + "' " + pt + " vs original " + ot);
                            }
                        }
                    }
                }
            }
        }
        Console.WriteLine("=== signature check: " + checkedPatches + " rimthreaded prefix/postfix patches checked (" + pairs.Count(p => !p.installed) + " of them not installable in this harness, checked from the recorded Patch call), problems: " + problems.Count);
        foreach (string s in problems.Distinct()) Console.WriteLine("  " + s);
    }
    // Every PatchedClassName entry swaps a vanilla static for a field in a mod class; the types must be identical or the transpiled IL is wrong.
    static void JsonFieldCheck(Assembly game, Assembly mod, string path)
    {
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        int checkedFields = 0;
        foreach (var cls in doc.RootElement.GetProperty("ClassReplacements").EnumerateArray())
        {
            string cn = cls.GetProperty("ClassName").GetString();
            Type t = game.GetType(cn) ?? AccessTools.TypeByName(cn);
            if (t == null || !cls.TryGetProperty("ThreadStatics", out var tsl)) continue;
            foreach (var f in tsl.EnumerateArray())
            {
                string fn = f.GetProperty("FieldName").GetString();
                FieldInfo vf = AccessTools.Field(t, fn);
                if (vf == null) { Failures.Add("JSON field missing: " + cn + "." + fn); continue; }
                if (vf.IsInitOnly && false) { }
                checkedFields++;
                if (f.TryGetProperty("PatchedClassName", out var pcn))
                {
                    Type pt = mod.GetType(pcn.GetString());
                    FieldInfo pf = pt == null ? null : AccessTools.Field(pt, fn);
                    if (pf == null) { Failures.Add("JSON patched field missing: " + pcn.GetString() + "." + fn); continue; }
                    if (pf.FieldType != vf.FieldType) Failures.Add("JSON type mismatch: " + cn + "." + fn + " is " + vf.FieldType + " but " + pcn.GetString() + "." + fn + " is " + pf.FieldType);
                    if (pf.GetCustomAttribute<ThreadStaticAttribute>() == null) Failures.Add("JSON patched field is not [ThreadStatic]: " + pcn.GetString() + "." + fn);
                }
                else if (vf.FieldType.GetConstructor(Type.EmptyTypes) == null && !vf.FieldType.IsValueType)
                    GameErrors.Add("JSON field type has no parameterless constructor (stays null on worker threads): " + cn + "." + fn + " : " + vf.FieldType);
                else if (!(f.TryGetProperty("SelfInitialized", out var si) && si.GetBoolean()) && !vf.FieldType.IsValueType)
                    PerThreadConstructed.Add(vf.FieldType.FullName + "  <- " + cn + "." + fn);
                bool selfInit = f.TryGetProperty("SelfInitialized", out var si2) && si2.GetBoolean();
                if (!selfInit && !vf.FieldType.IsValueType && vf.FieldType.GetConstructor(Type.EmptyTypes) != null)
                {
                    // RimThreaded gives each thread "new T()" for this field. Vanilla must also start it as a new object, or code that
                    // tests it for null changes behaviour (a blank object instead of null).
                    bool vanillaNews = false;
                    var inits = vf.IsStatic ? new MethodBase[] { t.TypeInitializer } : t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (MethodBase init in inits.Where(x => x != null))
                    {
                        var ci = PatchProcessor.GetOriginalInstructions(init).ToList();
                        for (int i = 1; i < ci.Count; i++)
                            if ((ci[i].opcode == OpCodes.Stsfld || ci[i].opcode == OpCodes.Stfld) && Equals(ci[i].operand, vf) && ci[i - 1].opcode == OpCodes.Newobj) vanillaNews = true;
                    }
                    if (!vanillaNews) StartsNullInVanilla.Add(cn + "." + fn + " : " + vf.FieldType.Name);
                }
            }
        }
        Console.WriteLine("=== JSON field check: " + checkedFields + " fields examined");
        // every worker thread runs these constructors when it starts: anything that is not a plain collection/value holder is listed
        var unusual = PerThreadConstructed.Where(x => !System.Text.RegularExpressions.Regex.IsMatch(x, @"^System\.Collections\.(Generic\.)?(List|Dictionary|HashSet|Queue|Stack|SortedList|LinkedList)`|^System\.Text\.StringBuilder|^System\.Object ")).Distinct().OrderBy(x => x).ToList();
        Console.WriteLine("=== thread-static fields RimThreaded constructs per thread that vanilla does not initialize with new (starts null or set elsewhere): " + StartsNullInVanilla.Count);
        foreach (string u in StartsNullInVanilla.OrderBy(x => x)) Console.WriteLine("  " + u);
        Console.WriteLine("=== thread-static fields constructed per thread whose type is not a plain collection: " + unusual.Count);
        foreach (string u in unusual) Console.WriteLine("  " + u);
    }

    // A transpiler whose IL pattern no longer exists in 1.6 silently changes nothing. Compare the method body before and after.
    static void NoOpTranspilerCheck(List<MethodBase> patched)
    {
        int examined = 0; var noops = new List<string>();
        foreach (MethodBase m in patched)
        {
            Patches info = Harmony.GetPatchInfo(m);
            if (info == null) continue;
            var mine = info.Transpilers.Where(t => t.owner == "majorhoff.rimthreaded").ToList();
            if (mine.Count == 0) continue;
            // field replacement transpilers are applied to every method that mentions a replaced field, so a no-op there is a bug too
            examined++;
            try
            {
                string Dump(IEnumerable<CodeInstruction> ins) => string.Join("\n", ins.Select(i => i.opcode + " " + (i.operand is Label[] ? "labels" : i.operand is MemberInfo mi ? mi.DeclaringType?.FullName + "::" + mi : i.operand?.ToString())));
                string before = Dump(PatchProcessor.GetOriginalInstructions(m));
                string after = Dump(PatchProcessor.GetCurrentInstructions(m, 1000));
                if (before == after) noops.Add(Name(m) + " (transpilers: " + string.Join(",", mine.Select(t => t.PatchMethod.Name)) + ")");
            }
            catch (Exception e) { if (!(e is System.Security.SecurityException)) noops.Add(Name(m) + " could not be compared: " + e.GetType().Name); }
        }
        Console.WriteLine("=== transpiler no-op check: " + examined + " transpiled methods examined, unchanged bodies: " + noops.Count);
        foreach (string s in noops.Distinct()) Console.WriteLine("  " + s);
    }


    // For prefixes that skip the original (return false), list the members the vanilla 1.6 body touches that the replacement never touches.
    static void DriftCheck(List<MethodBase> patched)
    {
        var lines = new List<(int, string)>();
        HashSet<string> Keys(MethodBase m)
        {
            var set = new HashSet<string>();
            foreach (var ci in PatchProcessor.GetOriginalInstructions(m))
            {
                if (ci.operand is MemberInfo mi && (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Ldfld || ci.opcode == OpCodes.Stfld || ci.opcode == OpCodes.Ldsfld || ci.opcode == OpCodes.Stsfld || ci.opcode == OpCodes.Newobj || ci.opcode == OpCodes.Ldflda || ci.opcode == OpCodes.Ldsflda))
                {
                    string t = mi.DeclaringType?.Name ?? "";
                    if (t.Contains("<>") || t.StartsWith("Enumerator") || mi.Name == "get_Item" || mi.Name == "get_Count" || mi.Name == ".ctor") continue;
                    set.Add(t + "::" + mi.Name);
                }
            }
            return set;
        }
        foreach (MethodBase original in patched)
        {
            Patches info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            foreach (var pre in info.Prefixes.Where(x => x.PatchMethod.DeclaringType.Assembly.GetName().Name == "RimThreaded"))
            {
                var ins = PatchProcessor.GetOriginalInstructions(pre.PatchMethod).ToList();
                bool skips = false;
                for (int i = 0; i + 1 < ins.Count; i++) if (ins[i].opcode == OpCodes.Ldc_I4_0 && ins[i + 1].opcode == OpCodes.Ret) skips = true;
                if (!skips || pre.PatchMethod.ReturnType != typeof(bool)) continue;
                bool returnsTrue = false;
                for (int i = 0; i + 1 < ins.Count; i++) if (ins[i].opcode == OpCodes.Ldc_I4_1 && ins[i + 1].opcode == OpCodes.Ret) returnsTrue = true;
                // a prefix that can return true (or computes its return value) lets vanilla run on some path: a guard, not a replacement
                bool computed = ins.Any(ci => ci.opcode == OpCodes.Ret) && !returnsTrue && ins.Where((ci, i) => ci.opcode == OpCodes.Ret && i > 0 && ins[i - 1].opcode != OpCodes.Ldc_I4_0).Any();
                string kind = returnsTrue || computed ? "GUARD" : "REPLACEMENT";
                try
                {
                    var o = Keys(original); var pk = Keys(pre.PatchMethod);
                    var missing = o.Where(k => !pk.Contains(k)).OrderBy(k => k).ToList();
                    lines.Add((missing.Count, kind + " " + Name(original) + " <- " + pre.PatchMethod.DeclaringType.Name + " : vanilla touches " + o.Count + ", missing " + missing.Count + " : " + string.Join(", ", missing)));
                }
                catch (Exception e) { lines.Add((-1, Name(original) + " could not be analysed: " + e.GetType().Name)); }
            }
        }
        File.WriteAllLines("/tmp/drift.txt", lines.OrderByDescending(l => l.Item1).Select(l => l.Item2));
        Console.WriteLine("=== drift check: " + lines.Count + " destructive prefixes analysed -> /tmp/drift.txt");
    }


    // A destructive prefix that omits parameters of the original cannot reproduce them (e.g. new 1.6 arguments with defaults).
    static void DroppedParamCheck(List<MethodBase> patched)
    {
        var rows = new List<string>();
        foreach (var pair in RtPatchPairs(patched).Where(p => p.prefix))
        {
            MethodBase original = pair.original;
            var pre = new { PatchMethod = pair.pm };
            {
                var ins = PatchProcessor.GetOriginalInstructions(pre.PatchMethod).ToList();
                bool skips = false;
                for (int i = 0; i + 1 < ins.Count; i++) if (ins[i].opcode == OpCodes.Ldc_I4_0 && ins[i + 1].opcode == OpCodes.Ret) skips = true;
                if (!skips) continue;
                var names = original.GetParameters().Select(x => x.Name).ToHashSet();
                var pnames = pre.PatchMethod.GetParameters().Select(x => x.Name).ToHashSet();
                if (pnames.Contains("__args")) continue;   // receives every argument
                var dropped = names.Where(n => !pnames.Contains(n)).ToList();
                if (dropped.Count > 0) rows.Add(Name(original) + " <- " + pre.PatchMethod.DeclaringType.Name + " drops: " + string.Join(",", dropped));
            }
        }
        Console.WriteLine("=== destructive prefixes that omit original parameters: " + rows.Count);
        foreach (string r in rows.OrderBy(x => x)) Console.WriteLine("  " + r);
    }


    public static bool SkipCctor() => false;
    static int traced;
    public static void TraceCtor(MethodBase __originalMethod) { if (traced++ < 3) Console.WriteLine("TRACE " + __originalMethod.DeclaringType + " ctor:\n" + new System.Diagnostics.StackTrace(1, false)); }

    // Functional check of WrapMethodInInstanceLock on a real 1.6 method: it must wait for a monitor held by another thread and keep the return value.
    static void LockCoverageCheck(Assembly game)
    {
        try
        {
            Type rmType = game.GetType("Verse.AI.ReservationManager");
            new Harmony("validator.cctor").Patch(rmType.TypeInitializer, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(SkipCctor))));
            object rm = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(rmType);
            Type resType = game.GetType("Verse.AI.ReservationManager+Reservation");
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(resType));
            AccessTools.Field(rmType, "reservations").SetValue(rm, list);
            Type lti = game.GetType("Verse.LocalTargetInfo");
            Type iv3 = game.GetType("Verse.IntVec3");
            object cellA = Activator.CreateInstance(lti, Activator.CreateInstance(iv3, 3, 0, 4));
            object cellB = Activator.CreateInstance(lti, Activator.CreateInstance(iv3, 9, 0, 9));
            object res = Activator.CreateInstance(resType, null, null, 1, -1, cellA, null);
            list.Add(res);
            MethodInfo isReserved = AccessTools.Method(rmType, "IsReserved");
            foreach (var (label, arg, expected) in new[] { ("IsReserved(reserved cell)", cellA, true), ("IsReserved(free cell)", cellB, false) })
            {
                var held = new ManualResetEventSlim();
                var t = new Thread(() => { lock (rm) { held.Set(); Thread.Sleep(1500); } });
                t.Start(); held.Wait();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool got = (bool)isReserved.Invoke(rm, new object[] { arg });
                sw.Stop(); t.Join();
                Console.WriteLine("=== lock coverage: " + label + " waited " + sw.ElapsedMilliseconds + " ms (" + (sw.ElapsedMilliseconds > 1000 ? "LOCKED" : "NOT LOCKED") + "), returned " + got + " (" + (got == expected ? "correct" : "WRONG") + ")");
            }
        }
        catch (Exception e) { while (e.InnerException != null) e = e.InnerException; Console.WriteLine("=== lock coverage check failed: " + e.GetType().Name + ": " + e.Message); }
    }


    // Prefixes that skip the original must set __result on every path that returns false; otherwise the caller gets default(T)
    // (e.g. BoolUnknown.True). Walks the prefix's IL control flow; any use of the __result argument counts as setting it.
    static void UnsetResultCheck(List<MethodBase> patched)
    {
        var rows = new List<string>();
        var seenPatches = new HashSet<MethodInfo>();
        foreach (var pair in RtPatchPairs(patched).Where(p => p.prefix))
        {
            MethodBase original = pair.original;
            {
                MethodInfo pm = pair.pm;
                if (!seenPatches.Add(pm) || pm.ReturnType != typeof(bool)) continue;
                ParameterInfo resultParam = pm.GetParameters().FirstOrDefault(x => x.Name == "__result" && x.ParameterType.IsByRef);
                Type origReturn = (original as MethodInfo)?.ReturnType ?? typeof(void);
                if (resultParam == null)
                {
                    // a prefix that can skip a non-void original but has no __result at all: the caller always gets default(T)
                    var body = PatchProcessor.GetOriginalInstructions(pm).ToList();
                    bool canSkip = Enumerable.Range(0, Math.Max(0, body.Count - 1)).Any(i => body[i].opcode == OpCodes.Ldc_I4_0 && body[i + 1].opcode == OpCodes.Ret);
                    if (canSkip && origReturn != typeof(void)) rows.Add(Name(original) + " <- " + Name(pm) + ": skips a " + origReturn.Name + "-returning original with no __result parameter");
                    continue;
                }
                int argIndex = resultParam.Position + (pm.IsStatic ? 0 : 1);
                var ins = PatchProcessor.GetOriginalInstructions(pm).ToList();
                var labelAt = new Dictionary<Label, int>();
                for (int i = 0; i < ins.Count; i++) foreach (Label l in ins[i].labels) labelAt[l] = i;
                bool Touches(CodeInstruction ci)
                {
                    if (ci.opcode == OpCodes.Ldarg || ci.opcode == OpCodes.Ldarg_S || ci.opcode == OpCodes.Ldarga || ci.opcode == OpCodes.Ldarga_S || ci.opcode == OpCodes.Starg || ci.opcode == OpCodes.Starg_S)
                    {
                        int idx = ci.operand is ParameterInfo p2 ? p2.Position + (pm.IsStatic ? 0 : 1) : Convert.ToInt32(ci.operand);
                        return idx == argIndex;
                    }
                    return (argIndex == 0 && ci.opcode == OpCodes.Ldarg_0) || (argIndex == 1 && ci.opcode == OpCodes.Ldarg_1) || (argIndex == 2 && ci.opcode == OpCodes.Ldarg_2) || (argIndex == 3 && ci.opcode == OpCodes.Ldarg_3);
                }
                var visited = new HashSet<(int, bool)>();
                var stack = new Stack<(int, bool)>();
                stack.Push((0, false));
                bool bad = false;
                while (stack.Count > 0 && !bad)
                {
                    var (i, touched) = stack.Pop();
                    while (i < ins.Count)
                    {
                        if (!visited.Add((i, touched))) break;
                        CodeInstruction ci = ins[i];
                        if (Touches(ci)) touched = true;
                        if (ci.opcode == OpCodes.Ret)
                        {
                            if (!touched && i > 0 && ins[i - 1].opcode == OpCodes.Ldc_I4_0) bad = true;
                            break;
                        }
                        if (ci.opcode == OpCodes.Throw || ci.opcode == OpCodes.Rethrow) break;
                        var targets = new List<Label>();
                        if (ci.operand is Label lab) targets.Add(lab);
                        else if (ci.operand is Label[] labs) targets.AddRange(labs);
                        foreach (Label t in targets) if (labelAt.TryGetValue(t, out int ti)) stack.Push((ti, touched));
                        if (ci.opcode == OpCodes.Br || ci.opcode == OpCodes.Br_S || ci.opcode == OpCodes.Leave || ci.opcode == OpCodes.Leave_S) break;
                        i++;
                    }
                }
                if (bad) rows.Add(Name(original) + " <- " + pm.DeclaringType.Name + "." + pm.Name);
            }
        }
        Console.WriteLine("=== prefixes that can return false without setting __result: " + rows.Count);
        foreach (string r in rows.OrderBy(x => x)) Console.WriteLine("  " + r);
    }

    static string Name(MethodBase m) => m.DeclaringType?.Name + "." + m.Name;

    public static IEnumerable<CodeInstruction> IdentityTranspiler(IEnumerable<CodeInstruction> instructions) => instructions;
    static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
    }

    static string Unwrap(Exception e) => Unwrap(e, 3);
    static string Unwrap(Exception e, int frames)
    {
        while ((e is TargetInvocationException || e is TypeInitializationException) && e.InnerException != null) e = e.InnerException;
        string s = e.GetType().Name + ": " + e.Message;
        string trace;
        try { trace = e.StackTrace ?? ""; } catch (Exception) { trace = "(stack trace unavailable: frame in a Unity module not present here)"; }
        string[] lines = trace.Split('\n');
        return s + " @ " + string.Join(" | ", lines.Take(frames).Select(l => l.Trim()));
    }

    public static bool LogPrefix(MethodBase __originalMethod, object[] __args)
    {
        string text = __args != null && __args.Length > 0 ? __args[0]?.ToString() : "";
        lock (gate)
        {
            if (__originalMethod.Name.StartsWith("Error")) GameErrors.Add(text);
            else if (__originalMethod.Name.StartsWith("Warning")) GameWarnings.Add(text);
        }
        return false;
    }

    public static readonly List<(MethodBase original, MethodInfo patch, string kind)> Recorded = new List<(MethodBase, MethodInfo, string)>();
    public static readonly List<string> NullLookups = new List<string>();
    public static readonly List<string> PerThreadConstructed = new List<string>();
    public static readonly List<string> StartsNullInVanilla = new List<string>();
    static readonly HashSet<string> LookupNames = new HashSet<string> { "Method", "DeclaredMethod", "Field", "DeclaredField", "Constructor",
        "DeclaredConstructor", "Property", "DeclaredProperty", "PropertyGetter", "PropertySetter", "DeclaredPropertyGetter",
        "DeclaredPropertySetter", "TypeByName", "Inner", "FirstMethod", "FirstConstructor", "FirstProperty", "FirstInner" };

    public static void RecordPatch(Harmony __instance, MethodBase __originalMethod, object[] __args)
    {
        if (__instance.Id != "majorhoff.rimthreaded" || __args == null || __args.Length == 0 || !(__args[0] is MethodBase original)) return;
        ParameterInfo[] ps = __originalMethod.GetParameters();
        for (int i = 1; i < __args.Length && i < ps.Length; i++)
            if (__args[i] is HarmonyMethod hm && hm.method != null)
                lock (gate) Recorded.Add((original, hm.method, ps[i].Name));
    }

    public static void RecordNullLookup(object __result, MethodBase __originalMethod, object[] __args)
    {
        if (__result != null) return;
        foreach (var f in new System.Diagnostics.StackTrace(1, false).GetFrames())
        {
            MethodBase m = f.GetMethod();
            string an = m?.DeclaringType?.Assembly.GetName().Name;
            if (an == null || an == "Validator" || an.StartsWith("System")) continue;
            if (an != "RimThreaded") return;
            string Fmt(object a) => a is Type t ? t.FullName : a is Type[] ts ? "[" + string.Join(",", ts.Select(x => x?.Name)) + "]" : a is string s ? "\"" + s + "\"" : a?.ToString() ?? "null";
            lock (gate) NullLookups.Add(Name(m) + ": AccessTools." + __originalMethod.Name + "(" + string.Join(", ", (__args ?? new object[0]).Select(Fmt)) + ")");
            return;
        }
    }

    public static readonly List<string> DelegateProblems = new List<string>();
    public static int DelegatesChecked;
    static string CallerInMod()
    {
        foreach (var f in new System.Diagnostics.StackTrace(2, false).GetFrames())
        {
            MethodBase m = f.GetMethod();
            string an = m?.DeclaringType?.Assembly.GetName().Name;
            if (an == null || an == "Validator" || an.StartsWith("System")) continue;
            return an == "RimThreaded" ? Name(m) : null;   // first non-runtime frame must be mod code (Harmony's own bindings are not the mod's)
        }
        return null;
    }
    public static bool CheckCreateDelegate(ref Delegate __result, object[] __args)
    {
        Type dt = __args.OfType<Type>().FirstOrDefault();
        MethodInfo target = __args.OfType<MethodInfo>().FirstOrDefault();
        bool hasFirstArg = __args.Length >= 4 || (__args.Length == 3 && !(__args[2] is bool));
        string caller = CallerInMod();
        if (caller == null || dt == null) return true;
        lock (gate) DelegatesChecked++;
        if (target == null) { lock (gate) DelegateProblems.Add(caller + ": CreateDelegate(" + dt.Name + ") with a null method"); return true; }
        MethodInfo inv = dt.GetMethod("Invoke");
        var dps = inv.GetParameters().Select(p => p.ParameterType).ToList();
        var tps = target.GetParameters().Select(p => p.ParameterType).ToList();
        if (!target.IsStatic && !hasFirstArg) tps.Insert(0, target.DeclaringType);
        bool paramsOk = dps.Count == tps.Count && dps.Zip(tps).All(x => x.First == x.Second || (!x.First.IsValueType && x.First.IsAssignableFrom(x.Second)) || (!x.Second.IsValueType && x.Second.IsAssignableFrom(x.First)));
        bool retOk = inv.ReturnType == target.ReturnType || (!target.ReturnType.IsValueType && inv.ReturnType.IsAssignableFrom(target.ReturnType));
        if (!paramsOk || !retOk)
            lock (gate) DelegateProblems.Add(caller + ": " + dt.Name + "<" + string.Join(",", dps.Select(x => x.Name)) + "→" + inv.ReturnType.Name + "> vs " + target.DeclaringType?.Name + "." + target.Name + "(" + string.Join(",", tps.Select(x => x.Name)) + ")→" + target.ReturnType.Name);
        if ((target.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0)
        {
            // stand-in with the right type so the initializer can continue; never invoked in the harness
            __result = (Delegate)typeof(Program).GetMethod(nameof(StandIn), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(dt).Invoke(null, null);
            return false;
        }
        return true;
    }
    static Delegate StandIn<T>()
    {
        MethodInfo inv = typeof(T).GetMethod("Invoke");
        var ps = inv.GetParameters().Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType)).ToArray();
        var body = inv.ReturnType == typeof(void) ? (System.Linq.Expressions.Expression)System.Linq.Expressions.Expression.Empty() : System.Linq.Expressions.Expression.Default(inv.ReturnType);
        return System.Linq.Expressions.Expression.Lambda(typeof(T), body, ps).Compile();
    }

    public static Exception Finalizer(Exception __exception, MethodBase __originalMethod, object[] __args)
    {
        if (__exception != null)
        {
            string ctx = "";
            if (__originalMethod.DeclaringType.Name == "RimThreadedHarmony" && __args != null)
                ctx = " (" + string.Join(", ", __args.Take(3).Select(a => a is Type t ? t.Name : a is MethodBase mb ? mb.DeclaringType?.Name + "." + mb.Name : a?.ToString())) + ")";
            lock (gate) Failures.Add(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name + ctx + " -> " + Unwrap(__exception));
        }
        return null;
    }
}
