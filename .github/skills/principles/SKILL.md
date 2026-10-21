---
name: principles
description: 'Use when implementing, debugging, refactoring, or reviewing yuan-ti parsers, the Flask viewer, or the Godot C# engine. Prefer smaller, simpler solutions, fix root causes, and verify observable behavior.'
---

# Engineering Principles

Read this skill in full when directed here by `AGENTS.md`. Apply only the
principles relevant to the task. These are adapted from
[pstack](https://github.com/cursor/plugins/tree/main/pstack), not its Cursor
orchestration, model selection, autonomy rules, or plugin dependencies. All rules
needed here are inline; upstream links are attribution, not runtime dependencies.
The upstream MIT notice is retained in [LICENSE](./LICENSE).

## Workflow

1. Identify the requested observable result and the nearest code that owns it.
   For a defect, reproduce it and trace the first incorrect decision or state.
2. Choose the smallest complete correction. Look for unnecessary logic to remove
   before introducing a new layer, state field, dependency, or special case.
3. Make one coherent change and run its cheapest meaningful check before moving
   to the next. Use existing tools and checks; do not add a framework or quality
   gate merely to satisfy this workflow.
4. Report the changed behavior, evidence, and anything not verified. Do not treat
   fewer lines, a successful build, or a passing proxy check as proof of correctness.

## Simplicity

### Laziness Protocol

[Source](https://github.com/cursor/plugins/blob/main/pstack/skills/principle-laziness-protocol/SKILL.md)

Make the smallest change that fully solves the problem. Keep each decision in
one authoritative place instead of copying it into callers. Reuse the current
parser, route, configuration, or engine abstraction before inventing another.
Small means less complexity, not compressed or cryptic code.

### Subtract Before You Add

[Source](https://github.com/cursor/plugins/blob/main/pstack/skills/principle-subtract-before-you-add/SKILL.md)

Remove redundant logic, dead paths, and obsolete references in the touched slice
before building on it. Do not retain a compatibility wrapper with no remaining
consumer. Design for observed requirements, not speculative formats or use cases.
Keep unrelated cleanup out of the change, and preserve required game behavior.

### Minimize Reader Load

[Source](https://github.com/cursor/plugins/blob/main/pstack/skills/principle-minimize-reader-load/SKILL.md)

Reduce both indirection and hidden mutable state. A pass-through wrapper or
single-use abstraction must hide a real decision to earn its place. Prefer local
values, explicit inputs and returned results; derive values instead of keeping
duplicate state synchronized. A reader should readily find where a value comes
from and what can change it.

## Structure

### Model the Domain

[Source](https://github.com/cursor/plugins/blob/main/pstack/skills/principle-model-the-domain/SKILL.md)

Use the simplest data shape that expresses the real rules. Existing format
records, lookup tables, game configuration, and explicit GPL or combat state
are preferable to scattered conditionals and booleans that must agree. Introduce
a structure only when it removes duplicated rules, branches, or invalid states.
Do not build a generic format framework to serve one known record.

### Boundary Discipline

[Source](https://github.com/cursor/plugins/blob/main/pstack/skills/principle-boundary-discipline/SKILL.md)

Put necessary parsing, validation, and error handling where external bytes,
requests, configuration, or saves enter the system. Do not repeat checks for
invariants already established there. Keep Flask/Godot wiring thin and domain
logic framework-independent. This is not permission to assume arbitrary binary
data is valid, nor a requirement to add validation libraries or speculative guards.

## Diagnosis and Verification

### Fix Root Causes

[Source](https://github.com/cursor/plugins/blob/main/pstack/skills/principle-fix-root-causes/SKILL.md)

Reproduce the fault, follow the data, and fix the first incorrect decision.
Inspect real state or add focused instrumentation when the cause is unclear.
If a state is legitimate, handling it at the failure point may be the fix. If
it should never exist, correct its producer rather than hiding it with a guard,
retry, swallowed error, clamped value, or discarded record. Check nearby uses of
the same faulty rule without expanding into unrelated repairs.

For this product, distinguish a decoder defect from an original-game quirk or
a deliberate optional fix. Use known format/game evidence before changing
compatibility behavior; do not silently "correct" the original game's rules.

### Prove It Works

[Source](https://github.com/cursor/plugins/blob/main/pstack/skills/principle-prove-it-works/SKILL.md)

Check the real output through the affected interface, not only compilation or
another agent's report. Use the checks specified in `AGENTS.md` and the relevant
subsystem docs. For format/image changes, use `make engine-compare`; for viewer
behavior, exercise the affected route or browser path. Documentation changes
need content and reference checks, not an unrelated engine build.

### Test Behavior, Not Implementation

[Source](https://github.com/cursor/plugins/blob/main/pstack/skills/principle-test-behavior-not-implementation/SKILL.md)

Where tests are permitted, assert observable results from concrete inputs,
not private helper calls or expected values generated by the code under test.
A check should fail when behavior breaks and survive an internal refactor.
Prefer existing engine tests and comparison tools. For Flask work, respect the
manual-check/no-new-unit-tests policy in `.github/instructions/terse.md`; do not
introduce pytest to implement this principle.

### Sequence Work into Verifiable Units

[Source](https://github.com/cursor/plugins/blob/main/pstack/skills/principle-sequence-verifiable-units/SKILL.md)

Break multi-step work into small coherent units. Verify the current unit before
building the next so failures remain local. For migrations, update consumers and
remove obsolete paths as a complete unit. This principle does not authorize
rebasing, committing, creating branches, or changing user-owned work.