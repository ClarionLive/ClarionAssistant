!--------------------------------------------------------------------------------------------------
! Types for the member-scope completion repro.
!
! These MUST live in a separate file. The defect being reproduced is in a scan of the LIVE EDITOR
! BUFFER, which only ever sees the .clw it is invoked on -- so a type declared here is exactly the
! thing that scan cannot resolve, while the language server resolves it normally. Moving these
! declarations into the .clw would make the repro pass for the wrong reason.
!--------------------------------------------------------------------------------------------------

ItemQueueType        QUEUE,TYPE
Code                   LONG
RefDate                LONG
Description            STRING(40)
                     END

SettingsGroupType    GROUP,TYPE
Status                 STRING(20)
Retries                LONG
                     END
