---
name: clarion-benchmark
description: Benchmark Clarion code generation quality by running test prompts and scoring the output. Measures improvement over time. Triggers on '/clarion-benchmark', 'benchmark clarion', 'test clarion code quality'.
version: 1.0.0
---

# Clarion Code Generation Benchmark

Tests Claude's Clarion code generation against known-good patterns and scores the output. Run before and after skill updates to measure improvement.

## Instructions

### Step 1: Run Test Cases

Generate Clarion code for each test case below. Do NOT look at the expected patterns first — generate naturally, then compare.

For each test case:
1. Generate the code as if a developer asked you to write it
2. Score it against the checklist
3. Record pass/fail for each check

### Test Cases

#### Test 1: Simple Procedure with Local Variables
**Prompt:** "Write a Clarion procedure called CalculateTotal that takes a QUEUE of items with Price and Quantity fields, loops through them, and returns the total as a DECIMAL."

**Score checklist:**
- [ ] PROCEDURE label starts in column 1
- [ ] Parameters use correct syntax (*QueueType for reference)
- [ ] Return type after parameters: `,DECIMAL`
- [ ] Local variables declared before CODE
- [ ] CODE on its own indented line
- [ ] LOOP uses correct syntax (LOOP i = 1 TO RECORDS())
- [ ] GET(queue, index) used correctly
- [ ] CLEAR not needed before read (only before ADD)
- [ ] RETURN with value at end
- [ ] No periods at end of statements
- [ ] Single quotes for any strings
- [ ] END for every LOOP and IF

#### Test 2: .clw File with CLASS Method Implementation
**Prompt:** "Write a .clw implementation file for a class called CustomerManager with methods Init, Kill, and FindByName that searches a FILE."

**Score checklist:**
- [ ] MEMBER is first line
- [ ] INCLUDE after MEMBER (with ONCE)
- [ ] Method names prefixed: CustomerManager.Init, CustomerManager.Kill, etc.
- [ ] Each method has its own PROCEDURE declaration
- [ ] CODE section in each method
- [ ] SELF used for class members
- [ ] No empty parentheses on parameterless procedures
- [ ] FILE access uses SET/NEXT pattern
- [ ] ERRORCODE() checked after file operations
- [ ] Labels in column 1

#### Test 3: Window with ACCEPT Loop
**Prompt:** "Write a Clarion window procedure with a button, a list box, and an ACCEPT loop that handles button click and list selection events."

**Score checklist:**
- [ ] WINDOW declaration with correct syntax
- [ ] Controls use USE(?ControlName) pattern
- [ ] Two ENDs for WINDOW (controls + structure)
- [ ] OPEN(Window) before ACCEPT
- [ ] ACCEPT (not LOOP) for event processing
- [ ] CASE EVENT() for event dispatch
- [ ] EVENT:Accepted with CASE FIELD() for button clicks
- [ ] EVENT:NewSelection for list changes
- [ ] EVENT:CloseWindow with BREAK
- [ ] CLOSE(Window) after ACCEPT loop END

#### Test 4: QUEUE Operations
**Prompt:** "Write Clarion code that declares a QUEUE, adds 3 records, sorts them by name, loops through to display each, then frees the queue."

**Score checklist:**
- [ ] QUEUE declaration with label in column 1
- [ ] CLEAR(queue) before each ADD
- [ ] ADD(queue) after setting fields
- [ ] SORT with field reference and +/- prefix
- [ ] LOOP with RECORDS() for count
- [ ] GET(queue, index) to retrieve
- [ ] FREE(queue) at the end
- [ ] No string-based SORT parameters

#### Test 5: COM Control Integration
**Prompt:** "Write Clarion code that creates a COM control using an OLE control, sets some properties, calls a method, and handles an event."

**Score checklist:**
- [ ] OLE control in WINDOW with HIDE
- [ ] SIGNED,STATIC variable for control reference
- [ ] ctrl = ?OLE for reference assignment
- [ ] ctrl{PROP:Create} = 'ProgId' for creation
- [ ] ctrl{'PropertyName'} for property access
- [ ] ctrl{'Method()'} for method calls (brace syntax)
- [ ] OCXREGISTEREVENTPROC for event handler
- [ ] Event handler has correct signature (*SHORT, SIGNED, LONG)
- [ ] PROP:LastEventName for event name
- [ ] OCXGETPARAM for event parameters

#### Test 6: CLASS Declaration (.inc file)
**Prompt:** "Write a .inc file declaring a Clarion CLASS called ReportBuilder with properties, methods, a constructor, and a destructor."

**Score checklist:**
- [ ] CLASS label in column 1
- [ ] CLASS attributes: TYPE, MODULE(), LINK()
- [ ] CONSTRUCT PROCEDURE declared
- [ ] DESTRUCT PROCEDURE declared
- [ ] Methods with parameters use correct syntax
- [ ] Return types after comma: PROCEDURE(),STRING
- [ ] VIRTUAL on overridable methods
- [ ] PROTECTED/PRIVATE for internal members
- [ ] Reference variables use & prefix
- [ ] END closes the CLASS

### Step 2: Calculate Score

Score = (total checks passed) / (total checks) * 100

**Rating:**
- 90-100%: Excellent — /clarion skill is working well
- 75-89%: Good — minor patterns to add
- 60-74%: Fair — significant gaps to address
- Below 60%: Poor — major skill revision needed

### Step 3: Log Results

Use the `log_skill_update` tool to record the benchmark:

```
log_skill_update(
  pattern_name="benchmark-run",
  action="benchmark",
  reason="Score: X% (Y/Z checks passed). Test 1: A/B, Test 2: C/D, ..."
)
```

### Step 4: Compare with Previous

Query previous benchmark results:

```sql
SELECT timestamp, code_snippet
FROM clarion_traces
WHERE trace_type = 'code_generation' AND tool_name = 'skill_update'
AND code_snippet LIKE '%benchmark-run%'
ORDER BY timestamp DESC
LIMIT 5
```

Report the trend: improving, stable, or regressing.

### Step 5: Recommendations

Based on which checks failed:
- Identify the top 3 most-failed checks across all tests
- Check if the /clarion skill covers these patterns
- If not, recommend running `/clarion-analyze` to generate fixes
- If yes, the skill wording may need strengthening

## Ratchet Mode

For autonomous improvement cycles, run this sequence repeatedly:

1. `/clarion-benchmark` — measure current score
2. `/clarion-analyze` — find and apply improvements
3. `/clarion-benchmark` — measure again
4. Compare: if score improved, keep changes. If regressed, revert.

To revert: use git to restore the previous version of the skill file:
```
git checkout HEAD~1 -- ~/.claude/skills/clarion/SKILL.md
```
