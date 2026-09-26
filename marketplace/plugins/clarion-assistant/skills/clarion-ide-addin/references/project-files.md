# Project File Templates (.sln, .csproj, .addin, AssemblyInfo.cs)

Templates for the solution, project, addin manifest, and assembly info files. Replace placeholders per the Placeholder Reference at the bottom.

## {AddinName}.sln

```
Microsoft Visual Studio Solution File, Format Version 11.00
# Visual Studio 2010
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "{AddinName}", "{AddinName}\{AddinName}.csproj", "{{{GUID}}}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{{{GUID}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{{{GUID}}}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{{{GUID}}}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{{{GUID}}}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
```

## {AddinName}/{AddinName}.csproj

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="4.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <ProjectGuid>{{{GUID}}}</ProjectGuid>
    <OutputType>Library</OutputType>
    <RootNamespace>{AddinName}</RootNamespace>
    <AssemblyName>{AddinName}</AssemblyName>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <FileAlignment>512</FileAlignment>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ">
    <PlatformTarget>x86</PlatformTarget>
    <DebugSymbols>true</DebugSymbols>
    <DebugType>full</DebugType>
    <Optimize>false</Optimize>
    <OutputPath>bin\Debug\</OutputPath>
    <DefineConstants>DEBUG;TRACE</DefineConstants>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ">
    <PlatformTarget>x86</PlatformTarget>
    <DebugType>pdbonly</DebugType>
    <Optimize>true</Optimize>
    <OutputPath>bin\Release\</OutputPath>
    <DefineConstants>TRACE</DefineConstants>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System" />
    <Reference Include="System.Drawing" />
    <Reference Include="System.Web" />
    <Reference Include="System.Windows.Forms" />
    <Reference Include="ICSharpCode.Core">
      <HintPath>C:\Clarion12\bin\ICSharpCode.Core.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="ICSharpCode.SharpDevelop">
      <HintPath>C:\Clarion12\bin\ICSharpCode.SharpDevelop.dll</HintPath>
      <Private>False</Private>
    </Reference>
  </ItemGroup>
  <ItemGroup>
    <!-- INCLUDE IF: Pad or Both -->
    <Compile Include="{ShortName}Pad.cs" />
    <Compile Include="Show{ShortName}Command.cs" />

    <!-- INCLUDE IF: Window or Both -->
    <Compile Include="{ShortName}ViewContent.cs" />
    <Compile Include="Show{ShortName}WindowCommand.cs" />

    <!-- Always include - shared control -->
    <Compile Include="{ShortName}Control.cs">
      <SubType>UserControl</SubType>
    </Compile>
    <Compile Include="{ShortName}Control.Designer.cs">
      <DependentUpon>{ShortName}Control.cs</DependentUpon>
    </Compile>
    <Compile Include="Services\EditorService.cs" />
    <Compile Include="Services\SettingsService.cs" />
    <Compile Include="Properties\AssemblyInfo.cs" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="{AddinName}.addin">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
```

**Embeditor Button variant:** for that hosting type, see `embeditor-button.md` for the simplified Compile and Reference ItemGroups.

## {AddinName}/{AddinName}.addin

**Note:** The .addin file content varies based on hosting type:
- **Pad only**: Include Pads + Workspace/Tools paths
- **Window only**: Include MainMenu/View/Tools path
- **Both**: Include Pads + MainMenu/View/Tools paths (with suffixes)

When hosting is "Both":
- `{PadSuffix}` = " (Pad)"
- `{WindowSuffix}` = " (Window)"

When hosting is "Pad" or "Window" only:
- `{PadSuffix}` and `{WindowSuffix}` = "" (empty)

```xml
<AddIn name="{DisplayName}" author="{Author}" description="{Description}">
  <Manifest>
    <Identity name="{AddinName}" version="1.0.0"/>
  </Manifest>
  <Runtime>
    <Import assembly="{AddinName}.dll"/>
  </Runtime>

  <!-- INCLUDE IF: Pad or Both -->
  <Path name="/SharpDevelop/Workbench/Pads">
    <Pad id="{ShortName}" category="Tools" title="{DisplayName}{PadSuffix}"
         icon="PadIcons.ClassBrowser" shortcut="{Shortcut}"
         class="{AddinName}.{ShortName}Pad"/>
  </Path>

  <!-- INCLUDE IF: Window or Both -->
  <Path name="/SharpDevelop/Workbench/MainMenu/View/Tools">
    <MenuItem id="Show{ShortName}Window" label="{DisplayName}{WindowSuffix}"
              class="{AddinName}.Show{ShortName}WindowCommand"/>
  </Path>

  <!-- INCLUDE IF: Pad only (NOT Both) -->
  <Path name="/Workspace/Tools">
    <MenuItem id="Show{ShortName}" label="{DisplayName}"
              class="{AddinName}.Show{ShortName}Command"/>
  </Path>

  <!-- INCLUDE IF: Embeditor Button -->
  <Path name="/SoftVelocity/Clarion/ToolBar/EmbedEditor">
    <ToolbarItem id="{ShortName}Separator" type="Separator"/>
    <ToolbarItem id="{ShortName}"
                 label="{DisplayName}"
                 tooltip="{Description}"
                 class="{AddinName}.{ShortName}Command"/>
  </Path>
</AddIn>
```

## {AddinName}/Properties/AssemblyInfo.cs

```csharp
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("{AddinName}")]
[assembly: AssemblyDescription("{Description}")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("{AddinName}")]
[assembly: AssemblyCopyright("Copyright {Year}")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: Guid("{GUID_LOWERCASE}")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
```

## Placeholder Reference

| Placeholder | Example | Notes |
|-------------|---------|-------|
| `{AddinName}` | ClarionCodeFormatter | Full addin name |
| `{ShortName}` | CodeFormatter | Without "Clarion" prefix |
| `{DisplayName}` | Code Formatter | Human-readable |
| `{Description}` | Formats Clarion source code | |
| `{Author}` | Your Name | |
| `{GUID}` | 7AA3AF71-3EA0-4ED7-A0B8-296A9887FAD9 | |
| `{GUID_LOWERCASE}` | 7aa3af71-3ea0-4ed7-a0b8-296a9887fad9 | |
| `{Shortcut}` | Control\|Alt\|F | |
| `{Year}` | 2026 | |
| `{PadSuffix}` | " (Pad)" or "" | " (Pad)" when Both, empty otherwise |
| `{WindowSuffix}` | " (Window)" or "" | " (Window)" when Both, empty otherwise |
| `{HostingType}` | Pad, Window, Both, or Embeditor Button | User's hosting choice |
| `{ClarionRoot}` | C:\Clarion12 | Clarion installation path (for HintPath references) |
