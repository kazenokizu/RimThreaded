# Static check over the mod source: every main-thread dispatch request must carry a Func<object[], object> or Action<object[]>
# (RimThreaded.RespondToSafeFunctionRequests runs nothing else). Func<object[], T> with a reference type T also matches by variance.
import re, glob, sys, os
root = sys.argv[1]
bad = []; total = 0
for p in glob.glob(os.path.join(root, '**', '*.cs'), recursive=True):
    if 'removed_1.6' in p: continue
    code = re.sub(r'//[^\n]*', '', open(p, encoding='utf-8-sig', errors='ignore').read())
    for m in re.finditer(r'safeFunctionRequest\s*=\s*new\s+object\[\]\s*\{\s*([A-Za-z_]\w*)', code):
        total += 1
        ident = m.group(1)
        d = re.search(r'((?:Func|Action)\s*<[^=;]*?>)\s+' + re.escape(ident) + r'\s*=', code)
        t = re.sub(r'\s+', '', d.group(1)) if d else None
        ok = t in ('Func<object[],object>', 'Action<object[]>') or (t is not None and re.fullmatch(r'Func<object\[\],UnityEngine\.Object>', t))
        if not ok: bad.append(os.path.relpath(p, root) + ': ' + ident + ' is ' + str(t))
print('=== main-thread dispatch sites: %d, with a delegate type the dispatcher does not run: %d' % (total, len(bad)))
for b in bad: print('  ' + b)
