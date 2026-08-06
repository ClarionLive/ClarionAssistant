#TEMPLATE(CAStamp,'Clarion Assistant - Build Stamp'),FAMILY('ABC')
#!----------------------------------------------------------------------------
#! CAStamp - Build Stamp
#!
#! Stamps version / company / application metadata into the generated program
#! as global variables, with an optional startup banner.
#!
#! The values are plain globals, so any procedure can read them:
#!     MESSAGE('Running ' & CLIP(BuildStamp:Version))
#!
#! Part of Clarion Assistant.  MIT licensed.
#!
#! NOTE: the template language exposes no generation date/time symbol (checked
#! against the shipped corpus), so the version is developer-supplied rather
#! than an automatic build timestamp.
#!----------------------------------------------------------------------------
#EXTENSION(BuildStamp,'Stamp build and version info into this application'),APPLICATION
#SHEET,ADJUST
  #TAB('&General')
    #BOXED('Build stamp')
      #PROMPT('&Disable this template',CHECK),%StampDisable,DEFAULT(0),AT(10)
      #PROMPT('&Version:',@s20),%StampVersion,DEFAULT('1.0.0'),REQ
#! A developer-entered value is emitted inside quotes, so an apostrophe would
#! terminate the string literal and break the generated source.  Reject it at
#! prompt time rather than emit code that cannot compile.
      #VALIDATE(INSTRING('<39>',%StampVersion,1,1)=0,'The version cannot contain an apostrophe')
      #PROMPT('&Company:',@s40),%StampCompany
      #VALIDATE(INSTRING('<39>',%StampCompany,1,1)=0,'The company name cannot contain an apostrophe')
      #PROMPT('Variable &prefix:',@s30),%StampObject,DEFAULT('BuildStamp'),REQ
    #ENDBOXED
    #BOXED('Startup banner')
      #PROMPT('Show a &banner when the program starts',CHECK),%StampShow,DEFAULT(0),AT(10)
      #ENABLE(%StampShow)
        #PROMPT('Banner &title:',@s40),%StampTitle,DEFAULT('About')
      #ENDENABLE
    #ENDBOXED
  #ENDTAB
#ENDSHEET
#!
#!----------------------------------------------------------------------------
#! Global declarations.  Sizes are fixed rather than inferred from the literal,
#! so a blank company does not produce a zero-length CSTRING.
#!----------------------------------------------------------------------------
#AT(%GlobalData),WHERE(%StampDisable=0)
%StampObject:Version   CSTRING(21)
%StampObject:Company   CSTRING(41)
%StampObject:AppName   CSTRING(41)
#ENDAT
#!
#!----------------------------------------------------------------------------
#! Populate the globals before anything else runs.
#!----------------------------------------------------------------------------
#AT(%ProgramSetup),PRIORITY(4000),WHERE(%StampDisable=0),DESCRIPTION('Build Stamp - populate')
  %StampObject:Version = '%StampVersion'
  %StampObject:Company = '%StampCompany'
  %StampObject:AppName = '%Application'
#ENDAT
#!
#!----------------------------------------------------------------------------
#! Optional startup banner.  PRIORITY is later than the populate block above so
#! the variables are already assigned.
#!----------------------------------------------------------------------------
#AT(%ProgramSetup),PRIORITY(5000),WHERE(%StampDisable=0 AND %StampShow=1),DESCRIPTION('Build Stamp - banner')
  MESSAGE(CLIP(%StampObject:AppName) & '  -  version ' & CLIP(%StampObject:Version) & '  -  ' & CLIP(%StampObject:Company),'%StampTitle',ICON:Asterisk)
#ENDAT
