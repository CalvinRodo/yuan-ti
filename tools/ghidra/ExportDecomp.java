// Recover functions Ghidra's auto-analysis misses in DSUN_FLAT.EXE, apply
// curated names, and export decompiled C for every function.
//
// Run as a headless post-script (see `make decomp`):
//   analyzeHeadless <proj> <name> -import out/DSUN_FLAT.EXE \
//     -postScript ExportDecomp.java out/symbols.tsv out/decomp [SEG,...]
//
// The optional third argument limits decompilation to the listed EXE segments
// (hex), for quick checks of symbol changes.
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
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import ghidra.app.cmd.function.CreateFunctionCmd;
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.data.*;
import ghidra.program.model.lang.Register;
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

	// Name column of a func symbol may be a prototype:
	//   ret [near] [pascal] name(type name, ..., ...)
	private static final Pattern PROTOTYPE = Pattern.compile("(.*?)\\b(\\w+)\\s*\\((.*)\\)\\s*");

	private final Map<Address, String> origin = new HashMap<>();
	private final Map<String, DataType> types = new HashMap<>();
	private Set<Integer> exportSegs;
	private final List<String> seedLog = new ArrayList<>();
	private boolean logSeeding = true;

	@Override
	protected void run() throws Exception {
		String[] args = getScriptArgs();
		if (args.length < 2 || args.length > 3) {
			throw new IllegalArgumentException("usage: ExportDecomp.java <symbols.tsv> <out-dir> [SEG,...]");
		}
		if (args.length == 3) {
			exportSegs = new HashSet<>();
			for (String seg : args[2].split(",")) {
				exportSegs.add(Integer.parseInt(seg, 16));
			}
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
		defineTypes();
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
			String[] cols = Arrays.copyOf(line.split("\t", 4), 5);
			if (cols[2] == null) {
				throw new IOException("bad symbol line: " + line);
			}
			Matcher m = PROTOTYPE.matcher(cols[2]);
			if (m.matches()) {
				cols[4] = cols[2];
				cols[2] = m.group(2);
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
			String comment = s[3];
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
				if (s[4] != null) {
					applyPrototype(f, s[4]);
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

	// -- prototypes ---------------------------------------------------------

	/**
	 * Types used by the prototypes: C base types, Borland's FILE and dos.h /
	 * alloc.h structs, and far code pointers. Data pointers are near
	 * (2 bytes, medium model) unless written `far *` (4 bytes, seg:off).
	 */
	private void defineTypes() {
		DataTypeManager dtm = currentProgram.getDataTypeManager();
		CategoryPath cat = new CategoryPath("/borland");
		types.put("void", VoidDataType.dataType);
		types.put("char", CharDataType.dataType);
		types.put("signed char", SignedCharDataType.dataType);
		types.put("unsigned char", UnsignedCharDataType.dataType);
		types.put("int", IntegerDataType.dataType);
		types.put("short", ShortDataType.dataType);
		types.put("unsigned", UnsignedIntegerDataType.dataType);
		types.put("unsigned int", UnsignedIntegerDataType.dataType);
		types.put("long", LongDataType.dataType);
		types.put("unsigned long", UnsignedLongDataType.dataType);
		typedef(cat, "size_t", UnsignedIntegerDataType.dataType);
		typedef(cat, "time_t", LongDataType.dataType);
		typedef(cat, "va_list", new PointerDataType(VoidDataType.dataType, 2, dtm));

		DataType uchar = UnsignedCharDataType.dataType;
		DataType uint = UnsignedIntegerDataType.dataType;
		DataType sint = IntegerDataType.dataType;
		struct(cat, "FILE", "level", ShortDataType.dataType, "flags", uint, "fd", CharDataType.dataType,
			"hold", uchar, "bsize", ShortDataType.dataType, "buffer", new PointerDataType(uchar, 2, dtm),
			"curp", new PointerDataType(uchar, 2, dtm), "istemp", uint, "token", ShortDataType.dataType);
		struct(cat, "struct date", "da_year", sint, "da_day", CharDataType.dataType, "da_mon",
			CharDataType.dataType);
		struct(cat, "struct time", "ti_min", uchar, "ti_hour", uchar, "ti_hund", uchar, "ti_sec", uchar);
		struct(cat, "struct heapinfo", "ptr", new PointerDataType(VoidDataType.dataType, 2, dtm), "size",
			uint, "in_use", sint);
		struct(cat, "struct farheapinfo", "ptr", new PointerDataType(VoidDataType.dataType, 4, dtm), "size",
			UnsignedLongDataType.dataType, "in_use", sint);

		// Far code pointers are 4-byte seg:off values, not pointer types: a
		// constant far pointer to a function (push cs; push offset) makes the
		// decompiler fail with "Offset must be between 0x0 and 0x10ffef".
		for (String name : List.of("sighandler_t", "atexit_t", "cbrk_t", "isr_t")) {
			typedef(cat, name, DWordDataType.dataType);
		}
	}

	private void typedef(CategoryPath cat, String name, DataType dt) {
		types.put(name, new TypedefDataType(cat, name, dt, currentProgram.getDataTypeManager()));
	}

	private void struct(CategoryPath cat, String name, Object... fields) {
		DataTypeManager dtm = currentProgram.getDataTypeManager();
		StructureDataType st = new StructureDataType(cat, name.replace("struct ", ""), 0, dtm);
		for (int i = 0; i < fields.length; i += 2) {
			st.add((DataType) fields[i + 1], (String) fields[i], null);
		}
		types.put(name, st);
	}

	/** Parse a C type such as `const char far *`; `far *` is a 4-byte pointer. */
	private DataType parseType(String text) {
		DataTypeManager dtm = currentProgram.getDataTypeManager();
		String[] tokens = text.replace("*", " * ").trim().split("\\s+");
		List<String> base = new ArrayList<>();
		int i = 0;
		for (; i < tokens.length && !tokens[i].equals("*") && !tokens[i].equals("far"); i++) {
			if (!tokens[i].equals("const")) {
				base.add(tokens[i]);
			}
		}
		DataType dt = types.get(String.join(" ", base));
		if (dt == null) {
			throw new IllegalArgumentException("unknown type: " + text);
		}
		boolean far = false;
		for (; i < tokens.length; i++) {
			if (tokens[i].equals("far")) {
				far = true;
			}
			else if (tokens[i].equals("*")) {
				dt = new PointerDataType(dt, far ? 4 : 2, dtm);
				far = false;
			}
			else if (!tokens[i].equals("const")) {
				throw new IllegalArgumentException("bad type: " + text);
			}
		}
		return dtm.resolve(dt, DataTypeConflictHandler.REPLACE_HANDLER);
	}

	/**
	 * Apply a prototype with explicit storage. Arguments are on the stack in
	 * 2-byte slots from offset 4 (far call) or 2 (`near`); cdecl puts the first
	 * argument lowest, `pascal` the last, and pascal callees pop their
	 * arguments. Results are in AL, AX or DX:AX. Explicit storage is needed
	 * because the cspec's __cdecl16far has no DX:AX return.
	 */
	private void applyPrototype(Function f, String prototype) throws Exception {
		Matcher m = PROTOTYPE.matcher(prototype);
		m.matches();
		List<String> retWords = new ArrayList<>(Arrays.asList(m.group(1).trim().split("\\s+")));
		boolean near = retWords.remove("near");
		boolean pascal = retWords.remove("pascal");
		DataType ret = parseType(String.join(" ", retWords));

		List<String[]> args = new ArrayList<>();
		boolean varArgs = false;
		String list = m.group(3).trim();
		if (!list.equals("void")) {
			for (String arg : list.split(",")) {
				arg = arg.trim();
				if (arg.equals("...")) {
					varArgs = true;
					continue;
				}
				Matcher a = Pattern.compile("(.*?)(\\w+)").matcher(arg);
				if (!a.matches() || a.group(1).isBlank()) {
					throw new IllegalArgumentException("argument needs a type and name: " + prototype);
				}
				args.add(new String[] { a.group(1), a.group(2) });
			}
		}
		if (pascal) {
			Collections.reverse(args);
		}
		List<Parameter> params = new ArrayList<>();
		int offset = near ? 2 : 4;
		int first = offset;
		for (String[] arg : args) {
			DataType dt = parseType(arg[0]);
			params.add(new ParameterImpl(arg[1], dt,
				new VariableStorage(currentProgram, offset, dt.getLength()), currentProgram));
			offset += (dt.getLength() + 1) & ~1;
		}
		if (pascal) {
			Collections.reverse(params);
		}

		VariableStorage retStorage;
		int size = ret instanceof VoidDataType ? 0 : ret.getLength();
		if (size == 0) {
			retStorage = VariableStorage.VOID_STORAGE;
		}
		else if (size <= 2) {
			retStorage = new VariableStorage(currentProgram, register(size == 1 ? "AL" : "AX"));
		}
		else if (size == 4) {
			retStorage = new VariableStorage(currentProgram, register("DX"), register("AX"));
		}
		else {
			throw new IllegalArgumentException("unsupported return type: " + prototype);
		}

		String convention = (pascal ? "__stdcall16" : "__cdecl16") + (near ? "near" : "far");
		f.updateFunction(convention, new ReturnParameterImpl(ret, retStorage, currentProgram), params,
			Function.FunctionUpdateType.CUSTOM_STORAGE, true, SourceType.USER_DEFINED);
		f.setVarArgs(varArgs);
		if (pascal) {
			f.setStackPurgeSize(offset - first);
		}
	}

	private Register register(String name) {
		return currentProgram.getRegister(name);
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
				if (exportSegs != null && !exportSegs.contains(seg)) {
					continue;
				}
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
