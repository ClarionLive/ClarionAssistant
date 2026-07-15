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

! Bug L repro: a CLASS member that is itself a GROUP instantiation never got a
! symbol for its OWN name -- only classEndDepth bookkeeping happened at its declaration line
! (needed to correctly skip its body / know when the class itself closes), never symbol
! creation. GroupBugClass.InlineGroup and PeriodBugClass.InlineGroupPeriod above (both
! self-closing single-line forms) already exercise this for Bug H's depth-tracking fix, but
! neither ever produced a symbol for its own name until this fix -- confirmed by direct query
! (both now correctly appear). MultiLineGroup below additionally covers the genuine multi-line
! form (its own extra field, ExtraField, before its own separate closing END), and
! HiddenGroupMember covers PRIVATE-attribute exclusion for this construct, mirroring
! HiddenMember above for the simple-reference-member case. AttrTermGroup and
! AttrTermGroupPeriod cover the attrs+same-line-terminator forms ("...,DIM(2) END" /
! "...,DIM(2)."): the regex's "term" group never matches these (the ",.*" attrs alternative
! swallows the terminator), so without the end-of-line re-check they'd leak classEndDepth --
! AfterMultiLineGroupMember declared after them proves no leak (its Sign call still resolves).
MultiLineGroupBugClass CLASS,TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

HiddenGroupMember      GROUP(SmallGroupType), PRIVATE
                       END
MultiLineGroup         GROUP(SmallGroupType)
ExtraField                LONG
                       END
AttrTermGroup          GROUP(SmallGroupType),DIM(2) END
AttrTermGroupPeriod    GROUP(SmallGroupType),DIM(2).
AfterMultiLineGroupMember &WorkerClass
CallViaAfterMultiLineGroupMember PROCEDURE( ), LONG
            END
