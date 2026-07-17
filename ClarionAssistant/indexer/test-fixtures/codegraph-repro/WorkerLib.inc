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

! Bug K repro: an EQUATE-aliased scalar-type synonym, mirroring the real production case
! (CRYPT_CONTEXT EQUATE(LONG), used e.g. for ML_CryptBaseClass.HashContext) that motivated the
! ClassInstanceDeclRegex catch-all fallback -- NOT a class-in-a-class composition, which turned
! out not to compile (see LikeMemberBugClass below).
SmallHandleType EQUATE(LONG)

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

! Bug K repro: two CLASS data-member forms ParseIncFile's member-capture cascade never tried.
! GenCertData: LIKE(NamedType), borrowing another structure's layout. SomeHandle: typed via the
! EQUATE-aliased SmallHandleType synonym above -- mirrors the real production case
! (ML_CryptBaseClass.HashContext CRYPT_CONTEXT) that motivated the ClassInstanceDeclRegex
! catch-all fallback. PlainInstanceMember is a reference member (&WorkerClass) -- an EARLIER
! version of this repro tried a plain by-value CLASS,TYPE/MODULE/LINK/DLL-declared class
! instance here instead, which turned out NOT to compile at all (confirmed the hard way); a
! by-value class-in-a-class composition is a different, apparently illegal construct, distinct
! from the EQUATE-alias case this fix actually targets.
LikeMemberBugClass CLASS,TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

GenCertData                LIKE(SmallGroupType)
SomeHandle                 SmallHandleType
PlainInstanceMember        &WorkerClass
CallViaPlainInstanceMember PROCEDURE( ), LONG
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

! Bug M repro (inherited-member gap): BaseWorker is a CLASS data member declared here, on
! this BASE class, never redeclared on DerivedWorkerClass below. variablesByName only ever
! stores it as "BaseWorkerClass.BaseWorker" (keyed by the DECLARING class), and the
! dotted-call resolver's class-member fallback only ever tried "<currentClass>.<objName>" --
! i.e. "DerivedWorkerClass.BaseWorker" for a call made from DerivedWorkerClass's own method --
! which never exists, so the lookup silently missed. Expected before the fix:
! SELF.BaseWorker.Sign(...) in DerivedWorkerClass.CallViaInheritedMember (WorkerLib.clw) is
! invisible to CodeGraph, even though BaseWorker is a perfectly ordinary inherited member from
! Clarion's own point of view (the class already compiles and runs correctly). Expected after
! the fix: the resolver walks the inherits chain (DerivedWorkerClass -> BaseWorkerClass) when
! the direct same-class lookup misses, and the call resolves.
BaseWorkerClass CLASS,TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

BaseWorker    &WorkerClass
            END

DerivedWorkerClass CLASS(BaseWorkerClass),TYPE, MODULE('WorkerLib.CLW'), LINK('WorkerLib.CLW', 1 ), DLL( 0 )

CallViaInheritedMember PROCEDURE( ), LONG
            END
