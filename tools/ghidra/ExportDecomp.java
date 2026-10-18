// Recover functions Ghidra's auto-analysis misses in DSUN_FLAT.EXE, apply
// curated names, and export decompiled C for every function.
//
// Run as a headless post-script (see `make decomp`):
//   analyzeHeadless <proj> <name> -import out/DSUN_FLAT.EXE \
//     -postScript ExportDecomp.java out/symbols.tsv out/decomp
//
// Output (all paths under the output directory):
//   seg_XXXX.c   decompiled functions of EXE segment XXXX, in address order
//   index.tsv    one row per function: address, name, size, origin, status
//   calls.tsv    caller -> callee edges
//   seeds.log    function-creation attempts that failed and why
//
// The Borland stack check that opens most functions is removed from the C.
//
// Addresses are written as EXE seg:off (Ghidra segment minus load segment 0x1000),
// and unnamed functions are called fn_<seg>_<off> in the same coordinates.
// Ghidra's DAT_/LAB_/s_ labels keep Ghidra segments (EXE seg + 0x1000).
//@category DarkSun

import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.regex.Pattern;

import ghidra.app.cmd.function.CreateFunctionCmd;
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.data.VoidDataType;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;

public class ExportDecomp extends GhidraScript {

	private static final int LOAD_SEG = 0x1000;
	private static final int DECOMPILE_TIMEOUT_S = 120;
	private static final int RETRY_TIMEOUT_S = 900;

	// Decompiled form of the stack check; it carries no game logic, so it is dropped.
	private static final Pattern STACK_CHECK_C = Pattern.compile(
		"\\n[ \\t]*if \\([^\\n]*<= (?:DAT_5356_009c|__brklvl)\\) \\{\\n(?:[^\\n{}]*\\n)*?[ \\t]*\\}");

	// Borland stack check that opens most functions: cmp [9Ch],sp ; jb
	private static final byte[] STACK_CHECK = { 0x39, 0x26, (byte) 0x9C, 0x00, 0x72 };
	private static final byte[] PUSH_BP_MOV_BP_SP = { 0x55, (byte) 0x8B, (byte) 0xEC };

	private final Map<Address, String> origin = new HashMap<>();
	private final List<String> seedLog = new ArrayList<>();
	private boolean logSeeding = true;

	@Override
	protected void run() throws Exception {
		String[] args = getScriptArgs();
		if (args.length != 2) {
			throw new IllegalArgumentException("usage: ExportDecomp.java <symbols.tsv> <out-dir>");
		}
		Path outDir = Paths.get(args[1]);
		Files.createDirectories(outDir);

		for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
			origin.put(f.getEntryPoint(), "ghidra");
		}
		List<String[]> symbols = readSymbols(Paths.get(args[0]));
		// Symbols are seeded twice: entries hidden behind a no-return call only
		// become reachable once applyNoReturn has trimmed the caller's body.
		logSeeding = false;
		seedFromSymbols(symbols);
		applyNoReturn(symbols);
		logSeeding = true;
		seedFromSymbols(symbols);
		seedFromPatterns();
		analyzeChanges(currentProgram);
		renameDefaults();
		applySymbols(symbols);

		Files.write(outDir.resolve("seeds.log"), seedLog);
		export(outDir);
	}

	// -- symbols ------------------------------------------------------------

	private Address exeAddress(String segOff) {
		String[] parts = segOff.split(":");
		int seg = Integer.parseInt(parts[0], 16) + LOAD_SEG;
		int off = Integer.parseInt(parts[1], 16);
		SegmentedAddressSpace space =
			(SegmentedAddressSpace) currentProgram.getAddressFactory().getDefaultAddressSpace();
		return space.getAddress(seg, off);
	}

	private static String exeName(Address a) {
		SegmentedAddress s = (SegmentedAddress) a;
		return String.format("%04X:%04X", s.getSegment() - LOAD_SEG, s.getSegmentOffset());
	}

	private List<String[]> readSymbols(Path path) throws IOException {
		List<String[]> rows = new ArrayList<>();
		for (String line : Files.readAllLines(path)) {
			if (line.isBlank() || line.startsWith("#")) {
				continue;
			}
			String[] cols = line.split("\t", 4);
			if (cols.length < 3) {
				throw new IOException("bad symbol line: " + line);
			}
			rows.add(cols);
		}
		return rows;
	}

	/** Kind column: func[+noreturn][+noargs], data or label. */
	private static boolean hasFlag(String[] s, String flag) {
		return Arrays.asList(s[1].split("\\+")).contains(flag);
	}

	private static boolean isFunc(String[] s) {
		return s[1].startsWith("func");
	}

	private void seedFromSymbols(List<String[]> symbols) throws Exception {
		for (String[] s : symbols) {
			if (isFunc(s)) {
				ensureFunction(exeAddress(s[0]), "symbol", true);
			}
		}
	}

	/**
	 * Mark no-return functions and end the flow at every call to them, then
	 * recompute the callers' bodies. Without this, code after the call (often
	 * data or an unrelated routine) is absorbed into the caller.
	 */
	private void applyNoReturn(List<String[]> symbols) throws Exception {
		Set<Function> callers = new HashSet<>();
		for (String[] s : symbols) {
			Function f = isFunc(s) && hasFlag(s, "noreturn") ? getFunctionAt(exeAddress(s[0])) : null;
			if (f == null) {
				continue;
			}
			f.setNoReturn(true);
			for (Reference ref : getReferencesTo(f.getEntryPoint())) {
				Instruction call = getInstructionAt(ref.getFromAddress());
				if (call == null || !ref.getReferenceType().isCall()) {
					continue;
				}
				call.setFlowOverride(FlowOverride.CALL_RETURN);
				Function caller = getFunctionContaining(call.getAddress());
				if (caller != null) {
					callers.add(caller);
				}
			}
		}
		for (Function caller : callers) {
			CreateFunctionCmd.fixupFunctionBody(currentProgram, caller, monitor);
		}
	}

	private void applySymbols(List<String[]> symbols) throws Exception {
		SymbolTable table = currentProgram.getSymbolTable();
		for (String[] s : symbols) {
			Address a = exeAddress(s[0]);
			String comment = s.length > 3 ? s[3] : null;
			Function f = getFunctionAt(a);
			if (isFunc(s) && f != null) {
				f.setName(s[2], SourceType.USER_DEFINED);
				if (hasFlag(s, "noargs")) {
					// Stops the decompiler inventing arguments and register
					// clobbers at every call site.
					f.replaceParameters(Function.FunctionUpdateType.DYNAMIC_STORAGE_ALL_PARAMS, true,
						SourceType.USER_DEFINED);
					f.setReturnType(VoidDataType.dataType, SourceType.USER_DEFINED);
				}
				if (comment != null) {
					f.setComment(comment);
				}
			}
			else {
				if (isFunc(s)) {
					seedLog.add(s[0] + "\t" + s[2] + "\tno function; applied as label");
				}
				table.createLabel(a, s[2], SourceType.USER_DEFINED);
				if (comment != null) {
					setPlateComment(a, comment);
				}
			}
		}
	}

	/**
	 * Ghidra's default FUN_ names carry its load segment (EXE seg + 0x1000);
	 * rename them to fn_<exe seg>_<off> so names match the seg:off used in
	 * docs and headers. Thunk names follow their targets automatically.
	 */
	private void renameDefaults() throws Exception {
		for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
			if (!f.isThunk() && f.getSymbol().getSource() == SourceType.DEFAULT) {
				String name = "fn_" + exeName(f.getEntryPoint()).replace(':', '_').toLowerCase();
				f.setName(name, SourceType.ANALYSIS);
			}
		}
	}

	// -- function recovery --------------------------------------------------

	/**
	 * Create a function at a, unless one exists. Pattern hits that fall inside
	 * an existing function or instruction are skipped: they are more likely to
	 * be bytes of that code than a missed entry. A forced (symbol) entry may
	 * clear a stray instruction that no function owns.
	 */
	private boolean ensureFunction(Address a, String why, boolean force) throws Exception {
		if (getFunctionAt(a) != null) {
			return true;
		}
		Instruction covering = getInstructionContaining(a);
		if (covering != null && !covering.getAddress().equals(a)) {
			if (!force || getFunctionContaining(covering.getAddress()) != null) {
				if (force) {
					logSeed(exeName(a) + "\t" + why + "\tinside instruction at " +
						exeName(covering.getAddress()));
				}
				return false;
			}
			clearListing(covering.getAddress(), covering.getMaxAddress());
			covering = null;
		}
		Function containing = getFunctionContaining(a);
		if (containing != null && !force) {
			return false;
		}
		if (covering == null && !disassemble(a)) {
			logSeed(exeName(a) + "\t" + why + "\tdisassembly failed");
			return false;
		}
		Function f = createFunction(a, null);
		if (f == null) {
			logSeed(exeName(a) + "\t" + why + "\tcreateFunction failed" +
				(containing != null ? " (inside " + containing.getName() + ")" : ""));
			return false;
		}
		origin.put(a, why);
		return true;
	}

	private void logSeed(String line) {
		if (logSeeding) {
			seedLog.add(line);
		}
	}

	private void seedFromPatterns() throws Exception {
		Memory mem = currentProgram.getMemory();
		for (MemoryBlock block : mem.getBlocks()) {
			if (!block.isExecute() || !block.isInitialized()) {
				continue;
			}
			byte[] bytes = new byte[(int) block.getSize()];
			block.getBytes(block.getStart(), bytes);
			for (int i = 0; i < bytes.length; i++) {
				monitor.checkCancelled();
				if (matches(bytes, i, PUSH_BP_MOV_BP_SP)) {
					// Borland overlay-aware frame: inc bp ; push bp ; mov bp,sp
					int start = (i > 0 && bytes[i - 1] == 0x45) ? i - 1 : i;
					ensureFunction(block.getStart().add(start), "prologue", false);
				}
				else if ((bytes[i] & 0xFF) == 0xC8 && i + 3 < bytes.length && bytes[i + 3] == 0 &&
					i > 0 && endsCode(bytes[i - 1])) {
					// enter N,0 right after a return or padding
					ensureFunction(block.getStart().add(i), "enter", false);
				}
				else if (matches(bytes, i, STACK_CHECK)) {
					ensureFunction(block.getStart().add(i), "stackcheck", false);
				}
			}
		}
	}

	private static boolean matches(byte[] bytes, int at, byte[] pattern) {
		if (at + pattern.length > bytes.length) {
			return false;
		}
		for (int j = 0; j < pattern.length; j++) {
			if (bytes[at + j] != pattern[j]) {
				return false;
			}
		}
		return true;
	}

	private static boolean endsCode(byte b) {
		int v = b & 0xFF;
		// ret, retf, ret n, retf n (last byte of imm16 is usually 0), nop, padding
		return v == 0xC3 || v == 0xCB || v == 0x90 || v == 0x00;
	}

	// -- export -------------------------------------------------------------

	private void export(Path outDir) throws Exception {
		DecompInterface ifc = new DecompInterface();
		DecompileOptions options = new DecompileOptions();
		options.grabFromProgram(currentProgram);
		ifc.setOptions(options);
		ifc.toggleCCode(true);
		ifc.toggleSyntaxTree(false);
		if (!ifc.openProgram(currentProgram)) {
			throw new IllegalStateException("decompiler: " + ifc.getLastMessage());
		}

		Map<Integer, PrintWriter> files = new TreeMap<>();
		try (PrintWriter index = new PrintWriter(Files.newBufferedWriter(outDir.resolve("index.tsv")));
				PrintWriter calls = new PrintWriter(Files.newBufferedWriter(outDir.resolve("calls.tsv")))) {
			index.println("addr\tlinear\tname\tsize\torigin\tstatus\tcallers\tcallees\tfile");
			calls.println("caller\tcallee\tcallee_name");

			FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
			int done = 0;
			int total = currentProgram.getFunctionManager().getFunctionCount();
			while (it.hasNext()) {
				monitor.checkCancelled();
				Function f = it.next();
				Address entry = f.getEntryPoint();
				int seg = ((SegmentedAddress) entry).getSegment() - LOAD_SEG;
				String file = String.format("seg_%04X.c", seg);
				PrintWriter out = files.get(seg);
				if (out == null) {
					out = new PrintWriter(Files.newBufferedWriter(outDir.resolve(file)));
					out.printf("/* DSUN.EXE segment %04X - Ghidra %s decompiler output, not original source. */%n%n",
						seg, getGhidraVersion());
					files.put(seg, out);
				}

				Set<Function> callers = f.getCallingFunctions(monitor);
				Set<Function> callees = f.getCalledFunctions(monitor);
				for (Function c : callees) {
					calls.printf("%s\t%s\t%s%n", exeName(entry), exeName(c.getEntryPoint()), c.getName());
				}

				String status;
				String code;
				if (f.isThunk()) {
					Function target = f.getThunkedFunction(true);
					status = "thunk";
					code = String.format("/* thunk -> %s %s */%n",
						target == null ? "?" : exeName(target.getEntryPoint()),
						target == null ? "" : target.getName());
				}
				else {
					DecompileResults res = ifc.decompileFunction(f, DECOMPILE_TIMEOUT_S, monitor);
					if (res.isTimedOut()) {
						res = ifc.decompileFunction(f, RETRY_TIMEOUT_S, monitor);
					}
					if (res.decompileCompleted() && res.getDecompiledFunction() != null) {
						status = "ok";
						code = STACK_CHECK_C.matcher(res.getDecompiledFunction().getC()).replaceAll("");
					}
					else {
						status = res.isTimedOut() ? "timeout" : "fail";
						code = "/* decompile failed: " + res.getErrorMessage() + " */\n";
					}
				}

				out.printf("/* %s %s | linear 0x%05x | %d bytes | origin %s | callers: %s */%n",
					exeName(entry), f.getName(), entry.getOffset(), f.getBody().getNumAddresses(),
					origin.getOrDefault(entry, "ghidra"), names(callers));
				out.println(code);

				index.printf("%s\t0x%05x\t%s\t%d\t%s\t%s\t%d\t%d\t%s%n", exeName(entry),
					entry.getOffset(), f.getName(), f.getBody().getNumAddresses(),
					origin.getOrDefault(entry, "ghidra"), status, callers.size(), callees.size(), file);

				if (++done % 250 == 0) {
					println(String.format("decompiled %d/%d", done, total));
				}
			}
		}
		finally {
			for (PrintWriter w : files.values()) {
				w.close();
			}
			ifc.dispose();
		}
	}

	private static String names(Set<Function> fs) {
		if (fs.isEmpty()) {
			return "-";
		}
		List<String> out = new ArrayList<>();
		for (Function f : fs) {
			out.add(f.getName());
		}
		Collections.sort(out);
		if (out.size() > 8) {
			int more = out.size() - 8;
			out = new ArrayList<>(out.subList(0, 8));
			out.add("+" + more + " more");
		}
		return String.join(", ", out);
	}
}
