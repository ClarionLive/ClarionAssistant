!ABCIncludeFile(Family)

OMIT('_EndOfInclude_',IFDef_ctClarionLive)
IFDef_ctClarionLive EQUATE(1)

!-----------------------------------------------------------------------
!Purpose        :
!Usage          :
!Created        :
!Updates        :
!Assumptions    :
!Known Bugs     :
!Future Features:
!-----------------------------------------------------------------------

!
!--------------------------
!ClarionLive Skeleton Class
!--------------------------

qtIDDescr     QUEUE,TYPE
ID                 LONG
Descr              STRING(42)
               END

!
ctClarionLive  CLASS,TYPE,MODULE('ctClarionLive.CLW'),LINK('ctClarionLive.CLW',_FamilyLinkMode_),DLL(_FamilyDllMode_)
!
!Properties
!----------
InDebug             BYTE
Q                  &qtIDDescr
!
!Methods
!-------
Construct           PROCEDURE()
Destruct            PROCEDURE()
Init                PROCEDURE()
Kill                PROCEDURE()
HelloWorld          PROCEDURE()
RaiseError          PROCEDURE(STRING pErrorMsg)
QFree               PROCEDURE()
QDel                PROCEDURE()
               END


 !END-OMIT('_EndOfInclude_',IFDef_ctClarionLive)



!-----------------------------------------------------------------------
 OMIT('Instructions on how to use this ClarionLive Skeleton')
!-----------------------------------------------------------------------
!  Search and Replace:  [Family] [YourFamily]
!                          set project defines for YourFamily (automatically done for properly templated classes)
!                           _YourFamilyLinkMode_=>1
!                           _YourFamilyDllMode_=>0
!                           or
!                           _YourFamilyLinkMode_=>0
!                           _YourFamilyDllMode_=>1
!                          You`ll use the first set, unless this class was in a DLL and was exported
!                          If you`re only doing single EXE work, you could just rem the conditional LINK/DLLL
!                             ctClarionLive  CLASS,TYPE,MODULE('ctClarionLive.CLW'),LINK('ctClarionLive.CLW',_FamilyLinkMode_),DLL(_FamilyDllMode_)
!                             becomes
!                             ctClarionLive  CLASS,TYPE,MODULE('ctClarionLive.CLW'),LINK('ctClarionLive.CLW') ! ,_FamilyLinkMode_),DLL(_FamilyDllMode_)
!
!
!  Search and Replace:  [ctClarionLive] [ctYourClass]
!  Save This files as:  [ctYourClass.inc]
!
!  Make Changes to   :  [CLiveSkeleton.clw]
!  Change the INCLUDE:  [INCLUDE('ctClarionLive.INC')] [INCLUDE('ctYourClass.INC')]
!  Save as              [ctYourClass.clw]
!
!  IF your class needs to access your global scope
!     THEN replace [MEMBER()] with [MEMBER('YourExe')]
!  NOTE: this will tie this class to that one EXE/DLL
!
!-----------------------------------------------------------------------
! END-OMIT('Instructions on how to use this ClarionLive Skeleton')
!-----------------------------------------------------------------------



!-----------------------------------------------------------------------
   OMIT('Internal Documentation')
!-----------------------------------------------------------------------
! This Document:
! Downloaded from ClarionLive.com - Live! Open Class project
! Created by: ___??___
! Updates   : Dec/30/2010 Mark.Goldberg - reformatted, added Instructions
!                                       - added the QUEUE work, as an example of how to add a QUEUE,TYPE to a class
!
!-----------------------------------------------------------------------
!-END OMIT('Internal Documentation')
!-----------------------------------------------------------------------
