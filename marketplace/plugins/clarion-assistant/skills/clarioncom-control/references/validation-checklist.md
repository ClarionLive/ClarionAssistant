# COM Control Validation Process

Full checklist for creating or reviewing a COM control.

1. **Check AssemblyInfo.cs**
   - [ ] ComVisible(true) at assembly level
   - [ ] Unique GUID for type library

2. **Check Main Interface**
   - [ ] ComVisible(true)
   - [ ] InterfaceIsDual
   - [ ] Unique GUID
   - [ ] All members defined

3. **Check Event Interface**
   - [ ] ComVisible(true)
   - [ ] InterfaceIsIDispatch (NOT Dual!)
   - [ ] Unique GUID
   - [ ] DispId on all event methods (sequential from 1)

4. **Check Implementation Class**
   - [ ] ComVisible(true)
   - [ ] ClassInterface(None)
   - [ ] Unique GUID
   - [ ] ComSourceInterfaces links to event interface
   - [ ] ProgId matches namespace.classname
   - [ ] Delegates and events declared
   - [ ] Event raising methods have null checks and try-catch
   - [ ] Methods have error handling (never throw to COM)
   - [ ] Strings never return null
   - [ ] About() method defined in interface with [DispId]
   - [ ] About() method implemented in class
   - [ ] .env file exists for version management (if building)

5. **Check Project File**
   - [ ] TargetFramework: net472
   - [ ] PlatformTarget: x86
   - [ ] NO EnableComInterop (using RegFree COM)
   - [ ] NO RegisterForComInterop (using RegFree COM)
   - [ ] ComVisible: true

6. **Check GUID Uniqueness**
   - [ ] All 4 GUIDs are different
   - [ ] No GUIDs copied from other projects

7. **Check Manifest File** (YourControl.manifest)
   - [ ] clsid matches Class GUID
   - [ ] tlbid matches TypeLib GUID
   - [ ] progid matches namespace.classname
   - [ ] File is in project root (gets copied to Clarion folder on build)

8. **Check Build Output** (Clarion folder after build)
   - [ ] AssemblyName.dll exists
   - [ ] AssemblyName.manifest exists
   - [ ] AssemblyName.header exists
   - [ ] ProgID.details exists
   - [ ] ProgID.events exists
   - [ ] ProgID.methods exists
   - [ ] readme_AssemblyName.html exists
