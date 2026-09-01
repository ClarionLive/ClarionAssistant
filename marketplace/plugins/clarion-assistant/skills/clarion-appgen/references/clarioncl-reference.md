# ClarionCL Reference

## Switches

| Switch | Purpose |
|---|---|
| `/ai <app> <txa>` | **Creates** the `.app` from a TXA. Destroys an existing app — see below. |
| `/ax <app> <txa>` | Export app → TXA. Fails if the IDE has the app loaded. |
| `/ag <app>` | Template code generation → `.clw`/`.inc`. **Enterprise edition only.** |
| `/agc off` | Force full (non-conditional) generation. |
| `/di <dct> <dctx>` | Create a `.dct` from text. |
| `/dx <dct> <file>` | Export a dictionary; format chosen by extension. |
| `/au` | **Suppress the dictionary-upgrade modal. Use on every `/ax`, `/ai`, `/dx`, `/di`.** |
| `/win` | Force the Win32 redirection file. Reported as standard practice with `/ag` and `/aru`. |
| `/aru <app> "Utility(Template)"` | Run a `#UTILITY` template headlessly. |
| `/tr` / `/tu` | Register / unregister a template chain. **Machine-global state.** |
| `/tl` | List registered templates. |
| `/rt <file>` | Print which `.red` was consulted — run it from the app folder. |

`/aru` works **only** in the exact form `/win /aru App.app "Utility(Template)"`. Any other form **exits 0 having done nothing**.

## `/ai` is destructive

Against an existing `.app` it replaces rather than merges. Measured: a six-procedure app with a hand-added embed and four per-instance WindowResize prompts, given a fragment declaring one new procedure —

```
procedures    Main + 5 others  ->  Main + AboutBox
embed         present          ->  GONE
%AppStrategy  4 prompts        ->  0
%RangeField   1                ->  0
.app          245 KB           ->  41 KB
```

**Exit code 0. No warning, no confirmation, no backup.** `Main` survived as a name only — no window, no menubar, no additions — because it appeared in the fragment's `[APPLICATION]` header.

Re-tested with a fragment carrying **no** `[APPLICATION]` header, in case the header signalled "this is the whole app": same outcome, 245 KB → 38 KB. **There is no merge mode.**

Any script calling `/ai` must verify the target does not exist first.

*(Clarion Assistant's `import_txa` MCP tool routes through the IDE and exposes `clash_mode` rename/replace, which implies merge semantics — but that is an inference, not a measurement. Test it on a scratch app, never on one holding real work.)*

## Modal dialogs — the core headless problem

ClarionCL raises modal GUI dialogs mid-run that block until a human clicks. They are invisible to automation and there is **no quiet/unattended switch**. Known classes:

1. **Solution association** — a `.sln` with the same base name whose project association does not match: *"Clarion loaded the solution 'X.sln', but it does not contain the project 'X.cwproj'"* (Add / Create new solution / Ignore). Until answered, `/ag` fails with the misleading `error 0: The application <path>.app could not be open.` Clicking "Add" repeatedly appends duplicate project entries with fresh GUIDs.
2. **Dictionary upgrade** — C6/C7-era dictionaries. Cleared by `/au`.
3. **TXD format rejection** — *"This TXD file is a Report Writer only format"*.
4. **Template registry** — reachable through broken redirection.

**Avoidance beats detection:** build the `.cwproj` directly with MSBuild and keep **no `.sln`** in the folder. That removes class 1 entirely.

### Wrapper design

A robust ClarionCL wrapper needs five things:

1. **A CWD guard** — verify the working directory *and* that the target file resolves, before launch.
2. **A timeout**, with `WaitForExit(timeout)`. Never assume it returns.
3. **Dialog capture on timeout** — walk the child process tree for a modal and capture its title, text and buttons. This is what turns "it hung" into a diagnosis. A plain timeout-and-kill destroys the only evidence.
4. **Optional dismissal** — click the safest button (preference order Ignore > No > Cancel > OK > Close, with a `WM_CLOSE` fallback). In practice this converted a blocking hang into an honest exit 1 in 2.3 seconds.
5. **Artifact assertion** — check the output file exists **and is fresh**, turning silent no-ops into real failures.

Implementation notes worth reusing:

- **Clarion dialogs can be WinForms, not native Win32.** Child window classes are `WindowsForms10.STATIC...` / `WindowsForms10.BUTTON...`, not `Static` / `Button`. Match by uppercased substring or you capture a title with no body text.
- **Kill the whole child tree on timeout** — ClarionCL spawns children that otherwise orphan (three processes observed for one run).
- If `Get-CimInstance` is unavailable in a sandboxed shell, enumerate processes via `CreateToolhelp32Snapshot` P/Invoke rather than WMI/CIM.

## Redirection traps

1. **A local `.red` must carry the version-matched name.** `MyApp.red` in the app folder is **ignored**; renaming it to `Clarion120.red` makes it take precedence immediately. Verify with `ClarionCL /rt <file>` run from the app folder.
2. **Do not hand-write a minimal `.red`.** Derive it from the global one and change only the OUTPUT directories, leaving every search path intact. Specifically: the pattern `*.tp?` **also matches `.tpl`**, which cuts the app off from Clarion's template folder and produces the misleading *"no templates have been registered"*. Localize only entries that escape the build directory (those starting `..\` or a dot-dir); absolute paths, `%MACRO%` paths (ABC libsrc lives behind `%ROOT%`) and project-local `.\x` search paths must survive verbatim.
3. **`.` resolves against the process's current working directory**, not the project file's folder. Always set the working directory to the app folder.

## `.cwproj`

- **Compile items need real absolute paths.** The MSBuild CW task does not resolve bare `<Compile Include="X.CLW">` filenames through redirection under any `RedFile` setting, nor does it survive `subst` drives that outlive the build.
- **The error names only the FIRST unresolved item**, so "one file not found" usually means none resolved.
- Derive `ProjectGuid` deterministically from the app name so re-runs and any generated `.sln` agree.
- The TXA's `[PROJECT]` section is the source of truth for what the `.cwproj` should contain.

## Template registration (`/tr`, `/tu`)

Relevant here because a broken registration poisons every later template operation on the machine.

- **Snapshot `/tl` first.** That list is your baseline and the chain name is your undo.
- **A failed `/tr` registers nothing** — no partial state. Safe to iterate on parse errors.
- **`/tr` stores the template by BARE FILENAME, not the path you pass.** Register a `.tpl` outside the redirection search path and `/tr` exits 0, the chain appears in `/tl` — and then every later template operation fails with `GENE000: Could not open include file <name>.tpl` from any other working directory. **Fix:** deploy the `.tpl` to `<CLARION_ROOT>\accessory\template\win\` first, then `/tr` from there. Verify by running `/tl` from an unrelated directory such as `C:\Windows` — that is the real test.
- **`/tu <ChainName>` takes the chain name only, never a path.** It has returned exit 1 while actually succeeding — verify with `/tl`.
- A dangling registration (e.g. a template registered from a git worktree that was later pruned) can make even read-only `/tl` spin at 100% CPU indefinitely. Heal with `/tu <ChainName>` then re-register from a stable location.

See the `clarion-template` skill for template authoring itself.
