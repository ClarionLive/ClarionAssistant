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
