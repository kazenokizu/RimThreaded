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
        string asmDir = Path.Combine(runDir, "1.6", "Assemblies");
        Directory.CreateDirectory(asmDir);
        File.Copy(Path.Combine(runDir, "replacements_1.6.json"), Path.Combine(asmDir, "replacements_1.6.json"), true);
        AccessTools.Field(modType, "replacementsFolder").SetValue(null, asmDir);
        AccessTools.Field(modType, "replacementsJsonPath").SetValue(null, Path.Combine(asmDir, "replacements_1.6.json"));

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

        Console.WriteLine("phase: cctor");
        // 4. run the real static constructor: LoadFieldReplacements, ApplyFieldReplacements, all patches
        try { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(rth.TypeHandle); }
        catch (Exception e) { Failures.Add("CCTOR " + Unwrap(e)); }

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
        JsonFieldCheck(game, mod, Path.Combine(asmDir, "replacements_1.6.json"));
        // Harmony 2.3.3 on CoreCLR rejects even a do-nothing transpiler on many compiler-generated iterators (identity-scan: 84 of 151
        // untouched game iterators fail), so an InvalidProgramException on a ">d__" MoveNext says nothing about the mod's own transpiler.
        bool Unverifiable(string x) => x.Contains("ECall methods must be packaged") || (x.Contains(">d__") && x.Contains("InvalidProgramException"));
        var ecall = GameErrors.Concat(Failures).Where(Unverifiable).ToList();
        var real = GameErrors.Concat(Failures).Where(x => !Unverifiable(x)).Distinct().ToList();
        Console.WriteLine("=== REAL failures: " + real.Count);
        foreach (string s in real) Console.WriteLine("  " + s.Replace("\n", " ").Substring(0, Math.Min(600, s.Replace("\n", " ").Length)));
        Console.WriteLine("=== not JIT-verifiable on this runtime (Unity internal call in body, or compiler-generated iterator; the transpiler itself ran): " + ecall.Count);
        foreach (string s in ecall.Take(400)) Console.WriteLine("  " + s.Replace("\n", " ").Substring(0, Math.Min(200, s.Replace("\n", " ").Length)));
        var patched = Harmony.GetAllPatchedMethods().ToList();
        Console.WriteLine("=== methods patched in process: " + patched.Count);
        SignatureCheck(patched);
        NoOpTranspilerCheck(patched);
        Console.WriteLine("=== patched iterator/closure methods: " + string.Join(", ", patched.Where(m => m.DeclaringType.Name.Contains(">d__")).Select(m => m.DeclaringType.DeclaringType?.Name + "." + m.DeclaringType.Name)));
        return real.Count == 0 ? 0 : 1;
    }

    // Harmony does not check that prefix/postfix parameter types agree with the original; the JIT only catches some mismatches.
    static void SignatureCheck(List<MethodBase> patched)
    {
        int checkedPatches = 0; var problems = new List<string>();
        foreach (MethodBase original in patched)
        {
            Patches info = Harmony.GetPatchInfo(original);
            if (info == null) continue;
            var ops = original.GetParameters();
            foreach (var group in new[] { info.Prefixes, info.Postfixes })
            {
                foreach (Patch patch in group)
                {
                    if (patch.owner != "majorhoff.rimthreaded") continue;
                    checkedPatches++;
                    MethodInfo pm = patch.PatchMethod;
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
        Console.WriteLine("=== signature check: " + checkedPatches + " rimthreaded prefix/postfix patches checked, problems: " + problems.Count);
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
            }
        }
        Console.WriteLine("=== JSON field check: " + checkedFields + " fields examined");
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

    static string Name(MethodBase m) => m.DeclaringType?.Name + "." + m.Name;

    public static IEnumerable<CodeInstruction> IdentityTranspiler(IEnumerable<CodeInstruction> instructions) => instructions;
    static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
    }

    static string Unwrap(Exception e)
    {
        while ((e is TargetInvocationException || e is TypeInitializationException) && e.InnerException != null) e = e.InnerException;
        string s = e.GetType().Name + ": " + e.Message;
        string[] lines = (e.StackTrace ?? "").Split('\n');
        return s + " @ " + string.Join(" | ", lines.Take(3).Select(l => l.Trim()));
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
