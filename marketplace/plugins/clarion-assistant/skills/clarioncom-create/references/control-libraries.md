# Control Library Support

When creating a COM control, the user selects a UI control library. Add the appropriate NuGet packages to the generated .csproj based on their choice.

## Standard WinForms (default)
No additional packages needed. Uses built-in System.Windows.Forms.

## DevExpress WinForms
Add these packages to the .csproj:
```xml
<ItemGroup>
  <!-- Core DevExpress package - add specific control packages as needed -->
  <PackageReference Include="DevExpress.WindowsForms" Version="24.*" />
</ItemGroup>
```

**Common additional packages** (add based on specific controls used):
- `DevExpress.Data` - Data binding support
- `DevExpress.XtraEditors` - Editor controls (TextEdit, ButtonEdit, etc.)
- `DevExpress.XtraGrid` - Grid and data grid controls
- `DevExpress.XtraCharts` - Chart controls
- `DevExpress.XtraTreeList` - TreeList control
- `DevExpress.XtraScheduler` - Scheduler/calendar controls

**Using statements for DevExpress:**
```csharp
using DevExpress.XtraEditors;
using DevExpress.XtraGrid;
// Add specific namespaces based on controls used
```

## Telerik WinForms
Add these packages to the .csproj:
```xml
<ItemGroup>
  <PackageReference Include="Telerik.WinControls.All" Version="2024.*" />
</ItemGroup>
```

**Using statements for Telerik:**
```csharp
using Telerik.WinControls;
using Telerik.WinControls.UI;
```

## Syncfusion WinForms
Add these packages to the .csproj:
```xml
<ItemGroup>
  <PackageReference Include="Syncfusion.WinForms" Version="*" />
</ItemGroup>
```

**Note**: Free community license available for individuals and small businesses (revenue < $1M).

**Using statements for Syncfusion:**
```csharp
using Syncfusion.WinForms;
using Syncfusion.WinForms.Controls;
```

## Infragistics WinForms
Add these packages to the .csproj:
```xml
<ItemGroup>
  <PackageReference Include="Infragistics.WinForms" Version="*" />
</ItemGroup>
```

**Using statements for Infragistics:**
```csharp
using Infragistics.Win;
using Infragistics.Win.UltraWinEditors;
```

## Library Selection Guidelines

When the user selects a third-party library:
1. Add the appropriate NuGet PackageReference(s) to the .csproj file
2. Include the library-specific using statements in the control implementation
3. Use the library's control classes instead of standard WinForms controls
4. Follow the library's patterns for control initialization and event handling
