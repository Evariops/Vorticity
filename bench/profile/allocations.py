"""Records every managed allocation of the Native AOT runner, with its type, its size and its stack.

It runs inside lldb, which launches the runner and stops on each entry point the Native AOT runtime
allocates through. Nothing is sampled: every object, array and string the rounds allocate is
counted, and its size is read from its MethodTable, so the totals can be checked against the
bytes the runner itself reports per round.

The rounds are delimited by the runner's `AllocatedSoFar`, which it calls once before the first
round and once after each: the breakpoints are only armed between the first and the last of those
calls, so the runtime's own start and exit are not recorded.

Usage, from lldb:

    command script import allocations.py
    allocations <output directory> <repository root> [depth]

with the runner and its arguments already set as the target, `--repeat` among them.
"""

import collections
import os
import shlex
import struct
import subprocess
import time

import lldb

# The entry points compiled code calls to allocate, and what their second argument is. The slow
# paths they fall into (RhpNewObject, RhpNewVariableSizeObject, RhpGcAlloc) are left out, so an
# allocation is counted once whichever path it takes.
HELPERS = {
    'RhpNewFast': 'object',
    'RhpNewFinalizable': 'object',
    'RhAllocateNewObject': 'object',
    'RhpNewArrayFast': 'array',
    'RhpNewPtrArrayFast': 'array',
    'RhAllocateNewArray': 'array',
    'RhNewString': 'array',
}

MARKER = 'Vorticity_Benchmarks_Runner_Vorticity_Bench_Runner_Program__AllocatedSoFar'

# A MethodTable begins with its flags, whose low half is the component size when the high bit says
# there is one, and its base size.
HAS_COMPONENT_SIZE = 0x80000000


def __lldb_init_module(debugger, internal_dict):
    debugger.HandleCommand('command script add -f allocations.command allocations')


# The assembly prefixes Native AOT puts in front of every symbol, and again inside generic arguments.
PREFIXES = (
    'Vorticity_Benchmarks_Scenarios_Vorticity_Bench_Scenarios_',
    'Vorticity_Benchmarks_Runner_Vorticity_Bench_Runner_',
    'Vorticity_Vorticity_',
    'System_IO_Compression_System_IO_Compression_',
    'S_P_CoreLib_',
)


def readable(name):
    for prefix in PREFIXES:
        name = name.replace(prefix, '')
    return name[1:] if name.startswith('_') else name


def read_symbol_table(target, wanted):
    """The addresses of the symbols named in `wanted`, and the type every MethodTable belongs to.

    Both come from the dSYM's symbol table, read with `nm`: lldb resolves an address to one of
    these names but not a name back to its address, and it does not see the MethodTables at all.
    Native AOT names a MethodTable like a C++ vtable, `__ZTV<length><type>`.
    """
    module = target.GetModuleAtIndex(0)
    listing = subprocess.run(
        ['nm', module.GetSymbolFileSpec().fullpath], capture_output=True, text=True, check=True).stdout
    found = {}
    types = {}
    for line in listing.splitlines():
        fields = line.split()
        if len(fields) != 3:
            continue
        address, symbol = int(fields[0], 16), fields[2]
        if symbol.startswith('__ZTV'):
            digits = len(symbol) - len(symbol[5:].lstrip('0123456789')) - 5
            if digits > 0:
                length = int(symbol[5:5 + digits])
                types[address] = readable(symbol[5 + digits:5 + digits + length])
        elif symbol[1:] in wanted:
            found[symbol[1:]] = target.ResolveFileAddress(address)
    return found, types


class Recorder:
    def __init__(self, process, depth):
        self.process = process
        self.depth = depth
        self.tables = {}
        self.hits = []

    def method_table(self, address):
        known = self.tables.get(address)
        if known is None:
            error = lldb.SBError()
            raw = self.process.ReadMemory(address, 8, error)
            flags, base = struct.unpack('<II', raw) if error.Success() and raw else (0, 0)
            component = flags & 0xFFFF if flags & HAS_COMPONENT_SIZE else 0
            known = (component, base)
            self.tables[address] = known
        return known

    def stack(self, lr, fp, sp):
        error = lldb.SBError()
        span = 2048
        block = self.process.ReadMemory(sp, span, error) or b''
        pcs = [lr]
        frame = fp
        while len(pcs) < self.depth:
            offset = frame - sp
            if 0 <= offset and offset + 16 <= len(block):
                previous, ret = struct.unpack_from('<QQ', block, offset)
            else:
                raw = self.process.ReadMemory(frame, 16, error)
                if not error.Success() or not raw:
                    break
                previous, ret = struct.unpack('<QQ', raw)
            if ret == 0:
                break
            pcs.append(ret)
            if previous <= frame:
                break
            frame = previous
        return tuple(pcs)

    def record(self, round_index, kind, frame):
        x0 = frame.FindRegister('x0').GetValueAsUnsigned()
        x1 = frame.FindRegister('x1').GetValueAsUnsigned()
        lr = frame.FindRegister('lr').GetValueAsUnsigned()
        fp = frame.FindRegister('fp').GetValueAsUnsigned()
        sp = frame.GetSP()
        component, base = self.method_table(x0)
        if kind == 'array':
            size = (base + (x1 & 0xFFFFFFFF) * component + 7) & ~7
        else:
            size = base
        self.hits.append((round_index, x0, size, self.stack(lr, fp, sp)))


class Symbols:
    """Names and source lines for code addresses and MethodTables, cached.

    Addresses are taken back to the binary's own by the slide the loader applied, so that they
    still resolve once the process has exited.
    """

    def __init__(self, target, root, types, slide):
        self.target = target
        self.root = root
        self.types = types
        self.slide = slide
        self.code = {}

    def type_name(self, address):
        return self.types.get(address - self.slide, '0x%x' % address)

    def shorten(self, path):
        if self.root and path.startswith(self.root):
            return path[len(self.root):].lstrip('/')
        for marker, label in (
                ('/src/libraries/System.Private.CoreLib/src/', 'CoreLib/'),
                ('/src/libraries/', 'libraries/'),
                ('/src/coreclr/nativeaot/', 'nativeaot/'),
                ('/src/coreclr/', 'coreclr/')):
            at = path.find(marker)
            if at >= 0:
                return label + path[at + len(marker):]
        return os.path.basename(path)

    def frame(self, pc):
        """(function, source line, is ours) for a return address: the call is the byte before it."""
        known = self.code.get(pc)
        if known is None:
            address = self.target.ResolveFileAddress(pc - 1 - self.slide)
            context = self.target.ResolveSymbolContextForAddress(address, lldb.eSymbolContextEverything)
            symbol = context.GetSymbol()
            function = readable(symbol.GetName() or '') if symbol.IsValid() else '0x%x' % pc
            block = context.GetBlock().GetContainingInlinedBlock()
            if block.IsValid() and block.GetInlinedName():
                function = block.GetInlinedName() + ' <- ' + function
            entry = context.GetLineEntry()
            line = None
            mine = False
            if entry.IsValid() and entry.GetFileSpec().IsValid():
                path = entry.GetFileSpec().fullpath or ''
                mine = bool(self.root) and path.startswith(self.root)
                line = '%s:%d' % (self.shorten(path), entry.GetLine())
            known = (function, line, mine)
            self.code[pc] = known
        return known


def command(debugger, arguments, result, internal_dict):
    parts = shlex.split(arguments)
    if len(parts) < 2:
        result.SetError('usage: allocations <output directory> <repository root> [depth]')
        return
    directory = parts[0]
    root = parts[1].rstrip('/') + '/'
    depth = int(parts[2]) if len(parts) > 2 else 48

    debugger.SetAsync(False)
    target = debugger.GetSelectedTarget()
    found, types = read_symbol_table(target, list(HELPERS) + [MARKER])
    missing = [name for name in list(HELPERS) + [MARKER] if name not in found]
    if missing:
        result.SetError('symbols not found: %s (is the dSYM beside the runner?)' % ', '.join(missing))
        return

    marker = target.BreakpointCreateBySBAddress(found[MARKER])
    helpers = {}
    for name, kind in HELPERS.items():
        breakpoint = target.BreakpointCreateBySBAddress(found[name])
        breakpoint.SetEnabled(False)
        helpers[breakpoint.GetID()] = (name, kind, breakpoint)

    started = time.time()
    error = lldb.SBError()
    process = target.Launch(target.GetLaunchInfo(), error)
    if not error.Success():
        result.SetError('launch failed: %s' % error)
        return

    header = target.GetModuleAtIndex(0).GetObjectFileHeaderAddress()
    slide = header.GetLoadAddress(target) - header.GetFileAddress()
    recorder = Recorder(process, depth)
    marks = 0
    while process.GetState() == lldb.eStateStopped:
        for thread in process:
            if thread.GetStopReason() != lldb.eStopReasonBreakpoint:
                continue
            identifier = thread.GetStopReasonDataAtIndex(0)
            if identifier == marker.GetID():
                # The first call opens round 0 and call k + 1 closes round k. What is allocated
                # after the last call belongs to the exit, and the report drops it.
                marks += 1
                if marks == 1:
                    for _, _, breakpoint in helpers.values():
                        breakpoint.SetEnabled(True)
                continue
            entry = helpers.get(identifier)
            if entry is not None:
                recorder.record(marks - 1, entry[1], thread.GetFrameAtIndex(0))
        process.Continue()

    elapsed = time.time() - started
    symbols = Symbols(target, root, types, slide)
    report(directory, recorder.hits, symbols, marks, elapsed, process.GetExitStatus())
    result.AppendMessage('%d allocations recorded in %.1fs; report in %s' % (
        len(recorder.hits), elapsed, directory))


def report(directory, hits, symbols, marks, elapsed, status):
    rounds = collections.defaultdict(lambda: [0, 0])
    by_type = collections.defaultdict(lambda: collections.Counter())
    by_site = collections.defaultdict(lambda: collections.Counter())
    by_ours = collections.defaultdict(lambda: collections.Counter())
    stacks = collections.defaultdict(lambda: collections.Counter())
    rows = []

    # The rounds after the last marker are the exit's, and are dropped.
    last = marks - 1
    for round_index, table, size, pcs in hits:
        if round_index < 0 or round_index >= last:
            continue
        name = symbols.type_name(table)
        frames = [symbols.frame(pc) for pc in pcs]
        site = next((frame for frame in frames if frame[1] is not None), frames[0])
        mine = next((frame for frame in frames if frame[2]), None)
        phase = 'cold' if round_index == 0 else 'warm'
        rounds[round_index][0] += 1
        rounds[round_index][1] += size
        by_type[phase][(name,)] += size
        by_type[phase + '#'][(name,)] += 1
        site_key = (site[1] or site[0], site[0], name)
        by_site[phase][site_key] += size
        by_site[phase + '#'][site_key] += 1
        ours_key = ((mine[1], mine[0]) if mine else ('(no frame of ours)', site[0]))
        by_ours[phase][ours_key] += size
        by_ours[phase + '#'][ours_key] += 1
        stack_key = tuple('%s %s' % (frame[1] or '?', frame[0]) for frame in frames[:14])
        stacks[phase][(name,) + stack_key] += size
        stacks[phase + '#'][(name,) + stack_key] += 1
        rows.append((round_index, name, size, site_key[0], ours_key[0], ' | '.join(stack_key)))

    warm_rounds = max(last - 1, 0)
    out = ['# Allocations', '']
    out.append('%d rounds recorded (the first is cold), %.1fs under the debugger, exit status %d.' % (
        max(last, 0), elapsed, status))
    out.append('')
    out.append('| round | objects | bytes |')
    out.append('|---:|---:|---:|')
    for index in sorted(rounds):
        out.append('| %d | %d | %d |' % (index, rounds[index][0], rounds[index][1]))

    def section(title, table, counts, heads, scale, top=40):
        out.append('')
        out.append('## ' + title)
        out.append('')
        out.append('| bytes | objects | ' + ' | '.join(heads) + ' |')
        out.append('|---:|---:|' + '---|' * len(heads))
        for key, size in table.most_common(top):
            out.append('| %d | %s | %s |' % (
                size / scale, ('%.1f' % (counts[key] / scale)).rstrip('0').rstrip('.'),
                ' | '.join('`%s`' % part for part in key)))

    for phase, scale, label in (('cold', 1, 'round 0, cold'), ('warm', warm_rounds, 'per warm round')):
        if phase not in by_type or scale == 0:
            continue
        section('By type, %s' % label, by_type[phase], by_type[phase + '#'], ['type'], scale)
        section('By the line of ours that asked, %s' % label, by_ours[phase], by_ours[phase + '#'],
                ['line', 'function'], scale)
        section('By allocating line, %s' % label, by_site[phase], by_site[phase + '#'],
                ['line', 'function', 'type'], scale)
        out.append('')
        out.append('## Stacks, %s' % label)
        for key, size in stacks[phase].most_common(15):
            out.append('')
            out.append('%d bytes, %s objects of `%s`' % (
                size / scale, ('%.1f' % (stacks[phase + '#'][key] / scale)).rstrip('0').rstrip('.'), key[0]))
            out.append('')
            for frame in key[1:]:
                out.append('    ' + frame)

    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, 'allocations.md'), 'w') as handle:
        handle.write('\n'.join(out) + '\n')
    with open(os.path.join(directory, 'allocations.tsv'), 'w') as handle:
        handle.write('round\ttype\tbytes\tsite\tours\tstack\n')
        for row in rows:
            handle.write('%d\t%s\t%d\t%s\t%s\t%s\n' % row)
