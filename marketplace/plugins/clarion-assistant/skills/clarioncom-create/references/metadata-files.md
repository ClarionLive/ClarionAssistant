# Rule 7: Clarion Template Metadata File Generation

Clarion templates require three metadata files that describe the COM component's interface for automatic code generation. These files use an INI-style format and should be auto-generated during the build process.

## Required Metadata Files

1. **`.methods` file** - Lists all methods and properties with descriptions
2. **`.events` file** - Lists all COM events with parameters
3. **`.details` file** - Contains component metadata (ProgID, friendly name, description)

## File Format Specifications

### .methods File Format

```
[Method]
MethodName
[MethodDescription]
Description of what this method does
[Parameter]
parameterName
[ParameterType]
STRING|LONG|BYTE|REAL
[ParameterDescription]
Description of the parameter

[Properties]
[Property]
PropertyName
[PropertyType]
STRING|LONG|BYTE|REAL
[PropertyDescription]
Description of the property
```

### .events File Format

```
[Event]
EventName
[EventDescription]
Description of when this event fires
[Parameter1]
parameterName
[Parameter1Type]
STRING|LONG|BYTE|REAL
[Parameter1Description]
Description of the parameter
[Parameter2]
secondParameter
[Parameter2Type]
STRING
[Parameter2Description]
Description of second parameter
```

### .details File Format

```
[FriendlyName]
User-Friendly Component Name
[ProgID]
Namespace.ClassName
[FilenameNoExtenstion]
ProjectName
[Description]
Brief description of the component's purpose and features
[ObjectName]
suggestedVariableName
```

## MSBuild Targets for Auto-Generation

Add these targets to your `.csproj` file after the `CopyToClarion` target to automatically generate metadata files during build:

### Generate .details File

```xml
<Target Name="GenerateDetailsFile" AfterTargets="CopyToClarion">
  <ItemGroup>
    <DetailsContent Include="[FriendlyName]" />
    <DetailsContent Include="Component Friendly Name" />
    <DetailsContent Include="[ProgID]" />
    <DetailsContent Include="$(AssemblyName).ClassName" />
    <DetailsContent Include="[FilenameNoExtenstion]" />
    <DetailsContent Include="$(AssemblyName)" />
    <DetailsContent Include="[Description]" />
    <DetailsContent Include="Brief description of component functionality" />
    <DetailsContent Include="[ObjectName]" />
    <DetailsContent Include="suggestedVarName" />
  </ItemGroup>
  <WriteLinesToFile File="$(ProjectDir)Clarion\$(AssemblyName).details"
                    Lines="@(DetailsContent)"
                    Overwrite="true" />
</Target>
```

### Generate .events File

```xml
<Target Name="GenerateEventsFile" AfterTargets="GenerateDetailsFile">
  <ItemGroup>
    <!-- First Event -->
    <EventsContent Include="[Event]" />
    <EventsContent Include="EventName1" />
    <EventsContent Include="[EventDescription]" />
    <EventsContent Include="Fired when something happens" />
    <EventsContent Include="[Parameter1]" />
    <EventsContent Include="param1Name" />
    <EventsContent Include="[Parameter1Type]" />
    <EventsContent Include="STRING" />
    <EventsContent Include="[Parameter1Description]" />
    <EventsContent Include="Description of parameter 1" />
    <EventsContent Include="" />

    <!-- Second Event -->
    <EventsContent Include="[Event]" />
    <EventsContent Include="EventName2" />
    <EventsContent Include="[EventDescription]" />
    <EventsContent Include="Fired when another thing happens" />
    <EventsContent Include="[Parameter1]" />
    <EventsContent Include="param1Name" />
    <EventsContent Include="[Parameter1Type]" />
    <EventsContent Include="LONG" />
    <EventsContent Include="[Parameter1Description]" />
    <EventsContent Include="Description of parameter 1" />
  </ItemGroup>
  <WriteLinesToFile File="$(ProjectDir)Clarion\$(AssemblyName).events"
                    Lines="@(EventsContent)"
                    Overwrite="true" />
</Target>
```

### Generate .methods File

```xml
<Target Name="GenerateMethodsFile" AfterTargets="GenerateEventsFile">
  <ItemGroup>
    <!-- First Method -->
    <MethodsContent Include="[Method]" />
    <MethodsContent Include="MethodName1" />
    <MethodsContent Include="[MethodDescription]" />
    <MethodsContent Include="Description of what this method does" />
    <MethodsContent Include="[Parameter]" />
    <MethodsContent Include="param1" />
    <MethodsContent Include="[ParameterType]" />
    <MethodsContent Include="STRING" />
    <MethodsContent Include="[ParameterDescription]" />
    <MethodsContent Include="Description of the parameter" />
    <MethodsContent Include="" />

    <!-- Second Method -->
    <MethodsContent Include="[Method]" />
    <MethodsContent Include="MethodName2" />
    <MethodsContent Include="[MethodDescription]" />
    <MethodsContent Include="Description of second method" />
    <MethodsContent Include="" />

    <!-- Properties Section -->
    <MethodsContent Include="[Properties]" />
    <MethodsContent Include="[Property]" />
    <MethodsContent Include="PropertyName1" />
    <MethodsContent Include="[PropertyType]" />
    <MethodsContent Include="STRING" />
    <MethodsContent Include="[PropertyDescription]" />
    <MethodsContent Include="Description of the property" />
    <MethodsContent Include="" />

    <MethodsContent Include="[Property]" />
    <MethodsContent Include="PropertyName2" />
    <MethodsContent Include="[PropertyType]" />
    <MethodsContent Include="LONG" />
    <MethodsContent Include="[PropertyDescription]" />
    <MethodsContent Include="Description of second property" />
  </ItemGroup>
  <WriteLinesToFile File="$(ProjectDir)Clarion\$(AssemblyName).methods"
                    Lines="@(MethodsContent)"
                    Overwrite="true" />
</Target>
```

## Data Type Mapping

Map C# types to Clarion types in metadata files:

| C# Type | Clarion Type | Notes |
|---------|--------------|-------|
| `string` | `STRING` | Text data |
| `int`, `long` | `LONG` | 32-bit integer |
| `short` | `SHORT` | 16-bit integer |
| `byte` | `BYTE` | 8-bit unsigned |
| `bool` | `BYTE` | Use 0/1 for false/true |
| `float`, `double` | `REAL` | Floating point |
| `decimal` | `DECIMAL` | Fixed precision decimal |

## Extraction Guidelines

When generating metadata files:

1. **Extract from XML comments** - Use `/// <summary>` tags from C# source for descriptions
2. **Method signatures** - Get from COM interface definitions
3. **Event signatures** - Get from event interface (`IYourComponentEvents`)
4. **Property types** - Extract from property declarations
5. **Parameter names** - Use exact names from method signatures (case matters)

## Complete Target Chain

The complete MSBuild target execution order should be:

```
Build
  -> CopyManifest
  -> CreateClarionFolder
  -> CopyToClarion
  -> CopyDependenciesToClarion (if needed)
  -> GenerateDetailsFile
  -> GenerateEventsFile
  -> GenerateMethodsFile
```

This ensures all files are deployed before metadata generation begins.

## Benefits

- **Automatic updates** - Metadata files regenerated on every build
- **Always current** - No manual synchronization needed
- **Template integration** - Clarion templates can parse these files for code generation
- **Documentation** - Serves as human-readable API documentation
- **Consistency** - Eliminates manual errors in metadata

## Example Output Files

After build, the `Clarion/` folder uses the `accessory/bin/resources` layout (see references/csproj-msbuild.md for the full folder diagram): DLLs in `accessory/bin/`, and manifest/metadata/docs/batch files in `accessory/resources/` (`ProjectName.manifest`, `ProjectName.header`, `ProgID.details`, `ProgID.events`, `ProgID.methods`, `readme_ProjectName.html`).
