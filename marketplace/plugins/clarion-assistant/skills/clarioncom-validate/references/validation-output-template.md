# Validation Output Template

When validating a COM control, provide:

```
## COM Control Validation Report

### Control: [ControlName]

#### 1. Assembly Configuration
- [ ] ComVisible(true) at assembly level
- [ ] Assembly GUID present
Status: [PASS/FAIL]

#### 2. Main Interface ([InterfaceName])
- [ ] ComVisible(true)
- [ ] InterfaceType is InterfaceIsDual
- [ ] Unique GUID
Status: [PASS/FAIL]

#### 3. Event Interface ([EventInterfaceName])
- [ ] ComVisible(true)
- [ ] InterfaceType is InterfaceIsIDispatch
- [ ] All events have DispId
- [ ] Sequential DispIds
Status: [PASS/FAIL]

#### 3a. About Method (Version Display)
- [ ] About() method defined in interface with [DispId]
- [ ] About() method implemented in class with [ComVisible(true)]
- [ ] .env file exists with valid MAJOR_VERSION, MINOR_VERSION, BUILD_NUMBER (for projects being built)
Status: [PASS/FAIL]

#### 4. Implementation Class ([ClassName])
- [ ] ComVisible(true)
- [ ] ClassInterface(None)
- [ ] ComSourceInterfaces present
- [ ] ProgId format correct
Status: [PASS/FAIL]

#### 5. Project Configuration
- [ ] PlatformTarget x86
- [ ] No EnableComInterop
- [ ] No RegisterForComInterop
Status: [PASS/FAIL]

#### 6. Manifest File
- [ ] Manifest exists
- [ ] Uses clrClass (not comClass)
- [ ] GUIDs match source code
Status: [PASS/FAIL]

#### 7. Constructor Pattern (CRITICAL)
- [ ] No Controls.Add() in constructor
- [ ] No child control creation in constructor
- [ ] Uses OnHandleCreated for control initialization
- [ ] No data operations in constructor
Status: [PASS/FAIL]

### Summary
Total Issues: [N]
Critical: [N]
Warnings: [N]

### Remediation Steps
1. [First fix needed]
2. [Second fix needed]
...
```
