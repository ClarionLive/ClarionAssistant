; Clarion Assistant Beta Installer
; Inno Setup 6 Script
; Code-signed with Sectigo USB dongle

#define MyAppName "Clarion Assistant"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "ClarionLive"
#define MyAppURL "https://clarionlive.com"

; Source directories - adjust these to match your build output locations
#define SrcClarionAssistant "H:\DevLaptop\ClarionAssistant\ClarionAssistant\bin\Debug"
#define SrcClarionIndexer "H:\DevLaptop\ClarionLSP\indexer\bin\Debug"
#define SrcComForClarion "H:\DevLaptop\ClarionIdeCOMPane\ClarionCOMBrowser\bin\Debug"
#define SrcMarketplace "C:\Users\John Hickey\.claude\plugins\marketplaces\clarionassistant-marketplace"
#define SrcPlugin "C:\Users\John Hickey\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant"
#define SrcAgents "C:\Users\John Hickey\.claude\agents"
#define SrcBlankDct "C:\Users\John Hickey\AppData\Roaming\clarionassistant"
#define SrcDocs "H:\DevLaptop\ClarionAssistant\docs"
#define SrcTerminal "H:\DevLaptop\ClarionAssistant\ClarionAssistant\Terminal"
#define SrcTaskBoard "H:\DevLaptop\ClarionAssistant\ClarionAssistant\TaskLifecycleBoard"
; COMforClarion additional sources
#define SrcUltimateClasses "H:\Dev\Source\Classes"
#define SrcUltimateTemplates "H:\Dev\Source\SharedTemplates"
#define SrcTemplateDlls "C:\Clarion12\accessory\template\win"
#define SrcComDocs "C:\Clarion12\accessory\resources\ComForClarionDocumentation"
#define SrcClarionCOM "H:\DevLaptop\ClarionCOM\COMTemplate"
#define SrcFts5 "H:\DevLaptop\ClarionAssistant\ClarionAssistant\lib\sqlite-fts5"
#define SrcInstaller "H:\DevLaptop\ClarionAssistant\installer"

[Setup]
AppId={{B7E2F4A1-8C3D-4E5F-9A1B-2C3D4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={code:GetClarionPath}\accessory\addins
DefaultGroupName={#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=ClarionAssistant-{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x86compatible
UsedUserAreasWarning=no
SetupIconFile={#SrcInstaller}\clarion-assistant.ico
UninstallDisplayIcon={app}\ClarionAssistant\ClarionAssistant.dll
; Code signing - configured via Inno Setup IDE or ISCC command line:
;   ISCC /Ssigntool="C:\path\to\signtool.exe sign /fd sha256 /tr http://timestamp.sectigo.com /td sha256 /a /d $qClarion Assistant$q $f"
; SignTool=signtool
LicenseFile={#SrcInstaller}\LICENSE.txt
InfoBeforeFile={#SrcInstaller}\PREINSTALL.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation"
Name: "compact"; Description: "Compact installation (addins only)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "clarionassistant"; Description: "Clarion Assistant IDE Addin"; Types: full compact custom; Flags: fixed
Name: "comforclarion"; Description: "COM for Clarion Browser Addin"; Types: full compact custom
Name: "comforclarion\addin"; Description: "IDE Addin (COM Browser)"; Types: full compact custom; Flags: fixed
Name: "comforclarion\templates"; Description: "UltimateCOM Templates and Class"; Types: full custom
Name: "comforclarion\docs"; Description: "COM for Clarion Documentation"; Types: full custom
Name: "comforclarion\tooling"; Description: "ClarionCOM Project Templates and Scripts"; Types: full custom
Name: "plugin"; Description: "Clarion Assistant Plugin (skills, hooks, docs)"; Types: full custom
Name: "plugin\skills"; Description: "Clarion Development Skills"; Types: full custom; Flags: fixed
Name: "plugin\hooks"; Description: "Safety Hooks"; Types: full custom
Name: "plugin\docs"; Description: "Plugin Documentation"; Types: full custom
Name: "agents"; Description: "Claude Code Quality Agents"; Types: full custom
Name: "docgraph"; Description: "Pre-loaded Documentation Database"; Types: full custom
Name: "docs"; Description: "User Guide"; Types: full custom

; ============================================================
; FILES
; ============================================================

[Files]
; --- Clarion Assistant Addin ---
Source: "{#SrcClarionAssistant}\ClarionAssistant.dll"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcClarionAssistant}\ClarionAssistant.pdb"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcClarionAssistant}\ClarionAssistant.addin"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
; WebView2
Source: "{#SrcClarionAssistant}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcClarionAssistant}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcClarionAssistant}\Microsoft.Web.WebView2.Wpf.dll"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcClarionAssistant}\WebView2Loader.dll"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcClarionAssistant}\runtimes\win-x86\native\WebView2Loader.dll"; DestDir: "{app}\ClarionAssistant\runtimes\win-x86\native"; Components: clarionassistant; Flags: ignoreversion
; SQLite FTS5
Source: "{#SrcFts5}\System.Data.SQLite.dll"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcFts5}\SQLite.Interop.dll"; DestDir: "{app}\ClarionAssistant\x86"; Components: clarionassistant; Flags: ignoreversion
; Terminal web content
Source: "{#SrcTerminal}\terminal.html"; DestDir: "{app}\ClarionAssistant\Terminal"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcTerminal}\header.html"; DestDir: "{app}\ClarionAssistant\Terminal"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcTerminal}\home.html"; DestDir: "{app}\ClarionAssistant\Terminal"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcTerminal}\settings.html"; DestDir: "{app}\ClarionAssistant\Terminal"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcTerminal}\cheatsheet.html"; DestDir: "{app}\ClarionAssistant\Terminal"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcTerminal}\diff.html"; DestDir: "{app}\ClarionAssistant\Terminal"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcTerminal}\clarion-assistant-prompt.md"; DestDir: "{app}\ClarionAssistant\Terminal"; Components: clarionassistant; Flags: ignoreversion
; Task Lifecycle Board
Source: "{#SrcTaskBoard}\lifecycle-board.html"; DestDir: "{app}\ClarionAssistant\TaskLifecycleBoard"; Components: clarionassistant; Flags: ignoreversion
; Clarion Indexer
Source: "{#SrcClarionIndexer}\clarion-indexer.exe"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion
Source: "{#SrcClarionIndexer}\clarion-indexer.pdb"; DestDir: "{app}\ClarionAssistant"; Components: clarionassistant; Flags: ignoreversion

; --- COM for Clarion: IDE Addin ---
Source: "{#SrcComForClarion}\ClarionCOMBrowser.dll"; DestDir: "{app}\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\ClarionCOMBrowser.pdb"; DestDir: "{app}\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\ClarionCOMBrowser.addin"; DestDir: "{app}\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\Microsoft.Web.WebView2.Wpf.dll"; DestDir: "{app}\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\WebView2Loader.dll"; DestDir: "{app}\ComForClarion"; Components: comforclarion\addin; Flags: ignoreversion
Source: "{#SrcComForClarion}\runtimes\win-x86\native\WebView2Loader.dll"; DestDir: "{app}\ComForClarion\runtimes\win-x86\native"; Components: comforclarion\addin; Flags: ignoreversion

; --- COM for Clarion: UltimateCOM Templates & Class ---
Source: "{#SrcUltimateClasses}\UltimateCOM.inc"; DestDir: "{code:GetClarionPath}\accessory\libsrc\win"; Components: comforclarion\templates; Flags: ignoreversion
Source: "{#SrcUltimateClasses}\UltimateCOM.clw"; DestDir: "{code:GetClarionPath}\accessory\libsrc\win"; Components: comforclarion\templates; Flags: ignoreversion
Source: "{#SrcUltimateTemplates}\UltimateCOM.tpl"; DestDir: "{code:GetClarionPath}\accessory\template\win"; Components: comforclarion\templates; Flags: ignoreversion
Source: "{#SrcTemplateDlls}\UCSelectCOM.dll"; DestDir: "{code:GetClarionPath}\accessory\template\win"; Components: comforclarion\templates; Flags: ignoreversion
Source: "{#SrcTemplateDlls}\UCSelectCOMProgID.dll"; DestDir: "{code:GetClarionPath}\accessory\template\win"; Components: comforclarion\templates; Flags: ignoreversion
Source: "{#SrcTemplateDlls}\UTFileCopy.dll"; DestDir: "{code:GetClarionPath}\accessory\template\win"; Components: comforclarion\templates; Flags: ignoreversion

; --- COM for Clarion: Documentation ---
Source: "{#SrcComDocs}\*"; DestDir: "{code:GetClarionPath}\accessory\resources\ComForClarionDocumentation"; Components: comforclarion\docs; Flags: ignoreversion recursesubdirs createallsubdirs

; --- COM for Clarion: ClarionCOM Tooling (project templates, scripts) ---
Source: "{#SrcClarionCOM}\Template\*"; DestDir: "{userappdata}\ClarionCOM\Templates"; Components: comforclarion\tooling; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcClarionCOM}\.claude\scripts\*"; DestDir: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\GenerateClarionMetadata.ps1"; DestDir: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\GenerateReadmeHTML.ps1"; DestDir: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\ParseCOMInterface.ps1"; DestDir: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\install-skills.bat"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\install-skills.ps1"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\install-env.bat"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\install-env.ps1"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion
Source: "{#SrcClarionCOM}\version.txt"; DestDir: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling; Flags: ignoreversion

; --- Clarion Assistant Plugin ---
; Marketplace metadata (required for Claude Code plugin discovery)
Source: "{#SrcMarketplace}\.claude-plugin\marketplace.json"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\.claude-plugin"; Components: plugin; Flags: ignoreversion
; Plugin metadata
Source: "{#SrcPlugin}\.claude-plugin\plugin.json"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\.claude-plugin"; Components: plugin; Flags: ignoreversion
; Plugin root (CLAUDE.md)
Source: "{#SrcPlugin}\CLAUDE.md"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant"; Components: plugin; Flags: ignoreversion

; Plugin Skills
Source: "{#SrcPlugin}\skills\clarion\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarion"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarion-analyze\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarion-analyze"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarion-benchmark\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarion-benchmark"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarion-convert-driver\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarion-convert-driver"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarion-ide-addin\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarion-ide-addin"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\ClarionCOM\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\ClarionCOM"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-build\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-build"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-config\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-config"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-control\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-control"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-create\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-create"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-deploy\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-deploy"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-get\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-get"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-github-init\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-github-init"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-marketplace-submit\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-marketplace-submit"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-validate\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-validate"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-webview2-build\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-webview2-build"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-webview2-create\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-webview2-create"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-webview2-deploy\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-webview2-deploy"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\clarioncom-webview2-validate\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\clarioncom-webview2-validate"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\evaluate-code\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\evaluate-code"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcPlugin}\skills\jfiles\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills\jfiles"; Components: plugin\skills; Flags: ignoreversion recursesubdirs createallsubdirs

; Plugin Hooks
Source: "{#SrcPlugin}\hooks\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\hooks"; Components: plugin\hooks; Flags: ignoreversion

; Plugin Docs
Source: "{#SrcPlugin}\docs\*"; DestDir: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\docs"; Components: plugin\docs; Flags: ignoreversion recursesubdirs createallsubdirs

; --- Blank dictionary template (for driver conversion skill) ---
Source: "{#SrcBlankDct}\blank.dct"; DestDir: "{userappdata}\clarionassistant"; Components: plugin\skills; Flags: ignoreversion

; --- Claude Code Quality Agents ---
; Agents use onlyifdoesntexist — don't overwrite user's customized agents
Source: "{#SrcAgents}\code-reviewer.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\verifier.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\debugger.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\security-auditor.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\test-designer.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist
Source: "{#SrcAgents}\devils-advocate.md"; DestDir: "{%USERPROFILE}\.claude\agents"; Components: agents; Flags: onlyifdoesntexist

; --- Pre-loaded DocGraph Database ---
; NOTE: docgraph.db must be placed in the installer/ directory before building.
;       Run ingest_docs() in Clarion Assistant, then copy the DB here.
;       Always overwrites — this is a maintained reference DB; personal docs go in personal.docgraph.db
#ifexist SrcInstaller + "\docgraph.db"
Source: "{#SrcInstaller}\docgraph.db"; DestDir: "{userappdata}\ClarionAssistant"; Components: docgraph; Flags: ignoreversion
#endif

; --- User Guide ---
Source: "{#SrcDocs}\ClarionAssistant-Guide.html"; DestDir: "{app}\ClarionAssistant"; Components: docs; Flags: ignoreversion

; --- Post-install configuration script ---
Source: "{#SrcInstaller}\configure.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall

; --- CLAUDE.md reference (not installed as global CLAUDE.md — just a reference copy) ---
Source: "{#SrcInstaller}\CLAUDE.md"; DestDir: "{%USERPROFILE}\.claude"; DestName: "clarion-assistant-reference.md"; Flags: ignoreversion

; ============================================================
; DIRECTORIES (created even if no files target them)
; ============================================================

[Dirs]
Name: "{localappdata}\ClarionAssistant"
Name: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\.claude-plugin"; Components: plugin
Name: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\.claude-plugin"; Components: plugin
Name: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant"; Components: plugin
Name: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\skills"; Components: plugin\skills
Name: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\hooks"; Components: plugin\hooks
Name: "{%USERPROFILE}\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant\docs"; Components: plugin\docs
Name: "{%USERPROFILE}\.claude\agents"; Components: agents
Name: "{userappdata}\clarionassistant"; Components: plugin\skills
Name: "{userappdata}\ClarionCOM"; Components: comforclarion\tooling
Name: "{userappdata}\ClarionCOM\Templates"; Components: comforclarion\tooling
Name: "{userappdata}\ClarionCOM\scripts"; Components: comforclarion\tooling

; ============================================================
; POST-INSTALL
; ============================================================

[Run]
; Run configure.ps1 to merge Claude Code settings
Filename: "powershell.exe"; \
  Parameters: "-ExecutionPolicy Bypass -File ""{tmp}\configure.ps1"" -ClarionRoot ""{code:GetClarionPath}"" -DocGraphDb ""{localappdata}\ClarionAssistant\docgraph.db"""; \
  StatusMsg: "Configuring Claude Code settings..."; \
  Flags: runhidden waituntilterminated

; Run install-env.bat to set up ClarionCOM environment
Filename: "{userappdata}\ClarionCOM\install-env.bat"; \
  Parameters: """{code:GetClarionPath}"""; \
  Components: comforclarion\tooling; \
  StatusMsg: "Configuring ClarionCOM environment..."; \
  Flags: runhidden waituntilterminated

; Register UltimateCOM template with Clarion IDE (user prompted)
Filename: "{code:GetClarionPath}\bin\ClarionCL.exe"; \
  Parameters: "/tr ""{code:GetClarionPath}\accessory\template\win\UltimateCOM.tpl"""; \
  Components: comforclarion\templates; \
  Description: "Register UltimateCOM template with the Clarion IDE"; \
  Flags: postinstall waituntilterminated runhidden unchecked

; Optionally open the user guide
Filename: "{app}\ClarionAssistant\ClarionAssistant-Guide.html"; \
  Description: "View the User Guide"; \
  Components: docs; \
  Flags: nowait postinstall skipifsilent shellexec unchecked

[UninstallDelete]
; Clean up generated files (but NOT the docgraph.db - user may have customized it)
Type: files; Name: "{app}\ClarionAssistant\*.codegraph.db"
Type: files; Name: "{app}\ClarionAssistant\*.codegraph.db-shm"
Type: files; Name: "{app}\ClarionAssistant\*.codegraph.db-wal"

; ============================================================
; PASCAL SCRIPT
; ============================================================

[Code]
var
  ClarionPath: string;
  ClarionPathPage: TInputDirWizardPage;

// Check if DocGraph DB already exists (don't overwrite user's customized DB)
function DocGraphDbExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\ClarionAssistant\docgraph.db'));
end;

// Auto-detect Clarion installation from registry
function DetectClarionPath: string;
var
  Path: string;
begin
  Result := '';
  // Check Clarion 12 first (most common)
  if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion12', 'root', Path) then
  begin
    if DirExists(Path) then
    begin
      Result := Path;
      Exit;
    end;
  end;
  // Clarion 11.1
  if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion11.1', 'root', Path) then
  begin
    if DirExists(Path) then
    begin
      Result := Path;
      Exit;
    end;
  end;
  // Clarion 11
  if RegQueryStringValue(HKLM32, 'SOFTWARE\SoftVelocity\Clarion11', 'root', Path) then
  begin
    if DirExists(Path) then
    begin
      Result := Path;
      Exit;
    end;
  end;
  // Fallback: check common paths
  if DirExists('C:\Clarion12') then Result := 'C:\Clarion12'
  else if DirExists('C:\Clarion11') then Result := 'C:\Clarion11'
  else if DirExists('C:\Clarion12d') then Result := 'C:\Clarion12d';
end;

function GetClarionPath(Param: string): string;
begin
  Result := ClarionPath;
end;

// Check if Claude Code CLI is installed
function IsClaudeCodeInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/c claude --version >nul 2>&1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := Result and (ResultCode = 0);
end;

// Check if WebView2 Runtime is installed
function IsWebView2Installed: Boolean;
var
  Version: string;
begin
  Result := RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
  if not Result then
    Result := RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
end;

procedure InitializeWizard;
begin
  // Create Clarion path selection page after the license page
  ClarionPathPage := CreateInputDirPage(wpLicense,
    'Select Clarion Installation',
    'Where is Clarion installed?',
    'Select the folder where Clarion is installed, then click Next.',
    False, '');
  ClarionPathPage.Add('');

  // Auto-detect and set default
  ClarionPath := DetectClarionPath;
  if ClarionPath = '' then
    ClarionPath := 'C:\Clarion12';
  ClarionPathPage.Values[0] := ClarionPath;
  // Set {app} immediately so it resolves correctly even with DisableDirPage
  WizardForm.DirEdit.Text := ClarionPath + '\accessory\addins';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SelectedPath: string;
begin
  Result := True;

  if CurPageID = ClarionPathPage.ID then
  begin
    SelectedPath := ClarionPathPage.Values[0];

    // Validate Clarion installation
    if not DirExists(SelectedPath) then
    begin
      MsgBox('The selected directory does not exist: ' + SelectedPath, mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not DirExists(SelectedPath + '\bin') then
    begin
      MsgBox('This does not appear to be a valid Clarion installation.' + #13#10 +
             'Could not find the "bin" directory in: ' + SelectedPath, mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not DirExists(SelectedPath + '\accessory\addins') then
    begin
      MsgBox('The addins directory was not found: ' + SelectedPath + '\accessory\addins' + #13#10 +
             'Please verify your Clarion installation.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    ClarionPath := SelectedPath;
    // Update {app} so all file destinations resolve correctly
    WizardForm.DirEdit.Text := ClarionPath + '\accessory\addins';
  end;
end;

function InitializeSetup: Boolean;
var
  Msg: string;
begin
  Result := True;
  Msg := '';

  // Check prerequisites
  if not IsWebView2Installed then
    Msg := Msg + '- Microsoft Edge WebView2 Runtime is required but not installed.' + #13#10 +
           '  Download from: https://developer.microsoft.com/en-us/microsoft-edge/webview2/' + #13#10#13#10;

  if not IsClaudeCodeInstalled then
    Msg := Msg + '- Claude Code CLI is required but was not detected.' + #13#10 +
           '  Install from: https://claude.ai/download' + #13#10#13#10;

  if Msg <> '' then
  begin
    Msg := 'The following prerequisites were not found:' + #13#10#13#10 + Msg +
           'You can continue the installation, but Clarion Assistant will not' + #13#10 +
           'function until these are installed.' + #13#10#13#10 +
           'Continue anyway?';
    Result := (MsgBox(Msg, mbConfirmation, MB_YESNO) = IDYES);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Update DefaultDirName with selected Clarion path
    // (already handled via {code:GetClarionPath})
  end;
end;
