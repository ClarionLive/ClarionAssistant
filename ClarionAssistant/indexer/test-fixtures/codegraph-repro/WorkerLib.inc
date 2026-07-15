WorkerClass CLASS,TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

Sign          PROCEDURE( LONG pData ), LONG
            END

OwnerClass CLASS,TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

MyWorker      &WorkerClass
CommentedWorker &WorkerClass   !Bug F repro: trailing comment on a CLASS data member
CallViaMember PROCEDURE( ), LONG
CallViaCommentedMember PROCEDURE( ), LONG
            END

SmallGroupType GROUP,TYPE
Field1            LONG
                 END

GroupBugClass CLASS,TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

HiddenMember            &WorkerClass, PRIVATE
InlineGroup             GROUP(SmallGroupType) END
AfterGroupMember        &WorkerClass
CallViaAfterGroupMember PROCEDURE( ), LONG
            END

PeriodBugClass CLASS,TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

InlineGroupPeriod        GROUP(SmallGroupType) .
AfterPeriodMember        &WorkerClass
CallViaAfterPeriodMember PROCEDURE( ), LONG
            END

AfterBugClass CLASS,TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

CallSomethingElse PROCEDURE( ), LONG
            END

! Bug J repro: a real (repro-owned, not third-party) class with a VIRTUAL method, so a
! procedure-local derived class overriding it can be tested without depending on a third-party
! library class (e.g. CapeSoft's jsonClass, which surfaced the real production case).
DerivableClass CLASS,TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

Compute       PROCEDURE( LONG pValue ), LONG, VIRTUAL
            END
