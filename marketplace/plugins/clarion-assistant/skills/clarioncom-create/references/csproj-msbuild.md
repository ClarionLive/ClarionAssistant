# .csproj Configuration and MSBuild Deployment Targets

## Rule 6: Project Configuration

**CRITICAL:** The .csproj file must have these settings:

**IMPORTANT:** Do NOT include `RegisterForComInterop=true` or `EnableComInterop=true`. These settings cause the build to attempt registry registration, which is NOT used in registration-free COM. The manifest file provides all necessary COM activation information.

```xml
<PropertyGroup>
  <TargetFramework>net472</TargetFramework>
  <PlatformTarget>x86</PlatformTarget>
  <UseWindowsForms>true</UseWindowsForms>
  <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
</PropertyGroup>

<!-- CRITICAL: Exclude Template folder from compilation -->
<!-- This prevents duplicate assembly attributes when COMTemplate is copied to a project -->
<ItemGroup>
  <Compile Remove="Template\**\*" />
  <None Remove="Template\**\*" />
  <Content Remove="Template\**\*" />
  <EmbeddedResource Remove="Template\**\*" />
</ItemGroup>

<ItemGroup>
  <Reference Include="System.Windows.Forms" />
  <Reference Include="System.Drawing" />
</ItemGroup>
```

### Template Folder Exclusion (CRITICAL)

The .csproj file MUST include the Template folder exclusion ItemGroup shown above. This is already present in the template .csproj file (`Template/ClarionCOMTemplate.csproj`), but it's important to understand why it's required:

**Why this is needed:**
- When COMTemplate folder is copied to create a new project, the Template/ subfolder comes along as a reference
- Without this exclusion, MSBuild attempts to compile .cs files from Template/ folder
- This causes duplicate assembly attribute errors (duplicate AssemblyInfo.cs, etc.)
- The exclusion ensures only the project's own source files are compiled

**What it does:**
- `<Compile Remove="Template\**\*" />` - Excludes all .cs files in Template/ from compilation
- `<None Remove="Template\**\*" />` - Excludes Template/ files from None item group
- `<Content Remove="Template\**\*" />` - Excludes Template/ files from Content item group
- `<EmbeddedResource Remove="Template\**\*" />` - Excludes Template/ files from embedded resources

**When to use:**
- This exclusion is already in the template .csproj and will be automatically included when copying from Template/
- If creating a .csproj from scratch (not recommended), you must manually add this ItemGroup

## Basic Deployment Targets (flat Clarion/ folder)

```xml
<!-- Automatic deployment to Clarion folder -->
<Target Name="CreateClarionFolder" BeforeTargets="CopyToClarion">
  <MakeDir Directories="$(ProjectDir)Clarion" Condition="!Exists('$(ProjectDir)Clarion')" />
</Target>

<Target Name="CopyManifest" AfterTargets="Build">
  <Copy SourceFiles="$(ProjectDir)$(AssemblyName).manifest"
        DestinationFiles="$(OutputPath)$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)$(AssemblyName).manifest')" />
</Target>

<Target Name="CopyToClarion" AfterTargets="CopyManifest">
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll"
        DestinationFiles="$(ProjectDir)Clarion\$(AssemblyName).dll"
        SkipUnchangedFiles="true" />
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).manifest"
        DestinationFiles="$(ProjectDir)Clarion\$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(OutputPath)$(AssemblyName).manifest')" />
  <Message Text="Deployed to Clarion folder: $(ProjectDir)Clarion\" Importance="high" />
</Target>
```

**What these targets do:**
- `CreateClarionFolder` - Creates `Clarion/` folder if it doesn't exist
- `CopyManifest` - Copies manifest from project root to output folder after build (runs automatically after each build; copies `YourProject.manifest` from the project root to `bin\Release\net472\`; only copies if the file exists and has changed). Keep the manifest file in the project root (same directory as .csproj).
- `CopyToClarion` - Automatically copies DLL and manifest to `Clarion/` folder after every successful build

This means DLL and manifest files are **automatically deployed** after each build!

## Rule 8: Managing Additional Dependencies

If your COM component uses NuGet packages or additional DLLs (SQLite, JSON libraries, etc.), these dependencies must also be deployed to the Clarion folder.

### Adding NuGet Packages

Use standard NuGet package management:
```xml
<ItemGroup>
  <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  <PackageReference Include="System.Data.SQLite.Core" Version="1.0.118" />
</ItemGroup>
```

### Auto-Copying Dependencies to Clarion Folder

Add this MSBuild target after the `CopyToClarion` target to automatically copy all dependency DLLs:

```xml
<Target Name="CopyDependenciesToClarion" AfterTargets="CopyToClarion">
  <!-- Copy all DLLs from output folder except the main assembly -->
  <ItemGroup>
    <DependencyDlls Include="$(OutputPath)*.dll" Exclude="$(OutputPath)$(AssemblyName).dll" />
  </ItemGroup>
  <Copy SourceFiles="@(DependencyDlls)"
        DestinationFolder="$(ProjectDir)Clarion"
        SkipUnchangedFiles="true" />
  <Message Text="Copied $(DependencyDlls->Count()) dependency DLL(s) to Clarion folder" Importance="high" />
</Target>
```

**What this does:**
- Runs automatically after `CopyToClarion` target
- Copies ALL DLL files from the output folder to `Clarion/` folder
- Excludes your main COM DLL (already copied by `CopyToClarion`)
- Only copies changed files for faster builds
- Includes NuGet package DLLs, native DLLs, and any referenced assemblies

### Including Sample Data Files

For database files, config files, or other data files needed by your component:

**Option 1: Manual placement** (recommended for large files)
- Place sample files directly in `ProjectName/Clarion/` folder
- They will be preserved across builds

**Option 2: Auto-copy from project**
Create a `SampleData/` folder in your project and add this target:

```xml
<Target Name="CopySampleDataToClarion" AfterTargets="CopyDependenciesToClarion">
  <ItemGroup>
    <SampleFiles Include="$(ProjectDir)SampleData\**\*.*" />
  </ItemGroup>
  <Copy SourceFiles="@(SampleFiles)"
        DestinationFolder="$(ProjectDir)Clarion\%(RecursiveDir)"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)SampleData')" />
  <Message Text="Copied sample data files to Clarion folder" Importance="high" Condition="Exists('$(ProjectDir)SampleData')" />
</Target>
```

## Complete MSBuild Targets Example (accessory layout - preferred)

Here's a complete set of targets that handles everything. Uses `accessory/bin` and `accessory/resources` structure to mirror the Clarion installation:

```xml
<!-- Automatic deployment to Clarion folder with accessory structure -->
<Target Name="CreateClarionFolders" BeforeTargets="CopyToClarion">
  <MakeDir Directories="$(ProjectDir)Clarion\accessory\bin" Condition="!Exists('$(ProjectDir)Clarion\accessory\bin')" />
  <MakeDir Directories="$(ProjectDir)Clarion\accessory\resources" Condition="!Exists('$(ProjectDir)Clarion\accessory\resources')" />
</Target>

<Target Name="CopyManifest" AfterTargets="Build">
  <Copy SourceFiles="$(ProjectDir)$(AssemblyName).manifest"
        DestinationFiles="$(OutputPath)$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)$(AssemblyName).manifest')" />
</Target>

<Target Name="CopyToClarion" AfterTargets="CopyManifest">
  <!-- Copy main DLL to accessory/bin -->
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll"
        DestinationFiles="$(ProjectDir)Clarion\accessory\bin\$(AssemblyName).dll"
        SkipUnchangedFiles="true" />
  <!-- Copy manifest to accessory/resources -->
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).manifest"
        DestinationFiles="$(ProjectDir)Clarion\accessory\resources\$(AssemblyName).manifest"
        SkipUnchangedFiles="true"
        Condition="Exists('$(OutputPath)$(AssemblyName).manifest')" />
  <Message Text="Deployed to Clarion folder: $(ProjectDir)Clarion\accessory\" Importance="high" />
</Target>

<Target Name="CopyDependenciesToClarion" AfterTargets="CopyToClarion">
  <!-- Copy all dependency DLLs to accessory/bin -->
  <ItemGroup>
    <DependencyDlls Include="$(OutputPath)*.dll" Exclude="$(OutputPath)$(AssemblyName).dll" />
  </ItemGroup>
  <Copy SourceFiles="@(DependencyDlls)"
        DestinationFolder="$(ProjectDir)Clarion\accessory\bin"
        SkipUnchangedFiles="true" />
  <Message Text="Copied dependency DLLs to Clarion\accessory\bin" Importance="high" Condition="@(DependencyDlls->Count()) > 0" />
</Target>

<Target Name="CopySampleDataToClarion" AfterTargets="CopyDependenciesToClarion">
  <!-- Copy sample data to accessory/resources -->
  <ItemGroup>
    <SampleFiles Include="$(ProjectDir)SampleData\**\*.*" />
  </ItemGroup>
  <Copy SourceFiles="@(SampleFiles)"
        DestinationFolder="$(ProjectDir)Clarion\accessory\resources\%(RecursiveDir)"
        SkipUnchangedFiles="true"
        Condition="Exists('$(ProjectDir)SampleData')" />
  <Message Text="Copied sample data files to Clarion\accessory\resources" Importance="high" Condition="Exists('$(ProjectDir)SampleData')" />
</Target>
```

## Accessory Folder Layout

After build, the `Clarion/` folder uses the `accessory/bin/resources` layout:
- **DLLs** go to `accessory/bin/`
- **Resources** (manifest, metadata, docs, batch files) go to `accessory/resources/`
- **wwwroot** (WebView2 only) goes to `accessory/resources/wwwroot/`

This layout mirrors the Clarion installation `accessory` folder structure, enabling drag & drop deployment.

```
ProjectName/Clarion/
+-- accessory/
    +-- bin/                        <- DLLs
    |   +-- ProjectName.dll
    |   +-- [dependency DLLs]
    +-- resources/                  <- Metadata and docs
        +-- ProjectName.manifest
        +-- ProjectName.header
        +-- ProgID.details
        +-- ProgID.events
        +-- ProgID.methods
        +-- readme_ProjectName.html
        +-- [sample data files]
```

## Dependencies Checklist

When adding dependencies to your COM component:
- [ ] Add NuGet packages via PackageReference in .csproj
- [ ] Add `CopyDependenciesToClarion` MSBuild target to auto-copy DLLs
- [ ] If using sample data, create `SampleData/` folder in project
- [ ] Add `CopySampleDataToClarion` target if needed
- [ ] Document all dependencies in README.md (handled by clarioncom-deploy skill)
- [ ] Test that all DLLs are present in `Clarion/accessory/bin/` folder after build
- [ ] Verify Clarion application can find all required DLLs

**Important:** All dependency DLLs must be placed in the same directory as your COM DLL for Clarion to load them correctly.
