---
name: clarion-analyze
description: Analyze Clarion code generation traces to find recurring failure patterns. Uses evidence-gating (2+ occurrences) to identify real issues. Suggests improvements for the /clarion skill. Triggers on '/clarion-analyze', 'analyze clarion traces', 'what mistakes am I making'.
version: 1.0.0
---

# Clarion Trace Analyzer

Analyzes the Clarion code generation trace database to find recurring failure patterns and suggest improvements to the `/clarion` skill.

## Instructions

### Step 1: Get Overview

Run `trace_stats` to see the current state of the trace database. Report:
- Total traces collected
- Build success/failure ratio
- If no traces exist, tell the user they need to generate and build some Clarion code first

### Step 2: Find Recurring Failures

Query the trace database for build failures with error details:

```sql
SELECT errors, error_count, target_file, timestamp, tool_name
FROM clarion_traces
WHERE trace_type = 'build_result' AND build_result = 'failure' AND errors != ''
ORDER BY timestamp DESC
LIMIT 50
```

### Step 3: Classify Error Patterns

Parse the error messages and group them by category:

1. **Syntax errors** — wrong END placement, missing periods, bad statement structure
2. **Declaration errors** — missing variables, wrong types, undeclared procedures
3. **Structure errors** — wrong MEMBER/INCLUDE order, missing MAP, bad CLASS structure
4. **Template errors** — wrong template usage, missing template attributes
5. **Reference errors** — wrong & vs = usage, null reference issues
6. **COM/OLE errors** — wrong property syntax, missing PROP:Create

For each category, count occurrences across all traces.

### Step 4: Apply Evidence-Gating

**Critical rule: Only report patterns seen 2 or more times.**

A pattern seen once could be a one-off mistake or unusual context. A pattern seen twice or more is evidence of a systematic gap in the /clarion skill.

For each qualifying pattern, prepare:
- **Pattern name** — short description (e.g., "Missing END after IF block")
- **Occurrence count** — how many times seen
- **Example errors** — 2-3 real error messages from traces
- **Root cause** — why Claude keeps making this mistake
- **Suggested fix** — exact text to add to the /clarion skill (wrong/right pair)

### Step 5: Check Against Existing Skill

Read the current `/clarion` skill file:
```
~/.claude/skills/clarion/SKILL.md
```

For each pattern found in Step 4:
- Check if the skill already has a rule covering this pattern
- If yes, the rule may need strengthening (more examples, clearer wording)
- If no, this is a new rule to add

### Step 6: Present Recommendations

Output a structured report:

```
## Clarion Code Generation Analysis

**Traces analyzed:** X total, Y failures (Z% failure rate)
**Period:** [earliest trace] to [latest trace]

### Recurring Failure Patterns (evidence-gated, 2+ occurrences)

#### Pattern 1: [Name] (seen X times)
**Example errors:**
- `error on line 42: Missing END statement`
- `error on line 88: Missing END statement`

**Already in /clarion skill?** Yes/No
**Suggested skill addition:**
```clarion
❌ Wrong:
IF condition
  DoSomething()
  ! missing END

✅ Correct:
IF condition
  DoSomething()
END
```

### Patterns Below Evidence Threshold (seen once — monitoring)
- [list any single-occurrence patterns for awareness]

### Skill Health
- Rules covering identified patterns: X/Y
- New rules needed: Z
- Rules that need strengthening: W
```

### Step 7: Offer to Apply

Ask the user if they want to apply any of the suggested fixes to the `/clarion` skill. If yes:

1. Read the current skill file
2. Find the "Common Mistakes to Avoid" section
3. Append new wrong/right pairs
4. Show the diff before applying
5. Log the change in the trace database:

```sql
INSERT INTO clarion_traces (trace_type, code_snippet, tool_name)
VALUES ('skill_update', 'Added pattern: [pattern name]', 'clarion-analyze')
```

## Evidence-Gating Rules

These rules prevent the analyzer from chasing noise:

1. **Minimum 2 occurrences** — Don't fix patterns seen only once
2. **Same error type** — Group by error message pattern, not exact text (strip file paths and line numbers)
3. **Track false positives** — If a previously-added rule is followed by the same error recurring, the rule needs strengthening not just addition
4. **No duplicate rules** — Check the skill before adding; strengthen existing rules instead
5. **Changelog** — Every skill modification is logged as a trace with `trace_type='skill_update'`

## Useful Queries

### Recent failures
```sql
SELECT timestamp, target_file, error_count, errors
FROM clarion_traces
WHERE build_result = 'failure'
ORDER BY timestamp DESC LIMIT 10
```

### Most common error patterns
```sql
SELECT errors, COUNT(*) as occurrences
FROM clarion_traces
WHERE build_result = 'failure' AND errors != ''
GROUP BY errors
HAVING COUNT(*) >= 2
ORDER BY occurrences DESC
```

### Success rate over time
```sql
SELECT date(timestamp) as day,
  COUNT(*) as builds,
  SUM(CASE WHEN build_result='success' THEN 1 ELSE 0 END) as successes,
  ROUND(100.0 * SUM(CASE WHEN build_result='success' THEN 1 ELSE 0 END) / COUNT(*), 1) as success_pct
FROM clarion_traces
WHERE trace_type = 'build_result'
GROUP BY date(timestamp)
ORDER BY day DESC
```

### Skill update history
```sql
SELECT timestamp, code_snippet
FROM clarion_traces
WHERE trace_type = 'skill_update'
ORDER BY timestamp DESC
```
