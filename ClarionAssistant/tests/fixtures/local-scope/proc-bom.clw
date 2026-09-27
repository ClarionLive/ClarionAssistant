ProcA                PROCEDURE(LONG pId, *STRING pName, <BYTE pOpt>, LONG pDef=5, ? pAny, *QUEUE pQ)
LOC:Count            LONG                          ! the row count
MyGrp                GROUP
GrpA                   LONG
GrpB                   STRING(10)
                     END
PGrp                 GROUP,PRE(PG)
PgX                    LONG
                     END
ThisWindow           CLASS(WindowManager)
Init                   PROCEDURE(BYTE pMode),BYTE,PROC,DERIVED
Kill                   PROCEDURE(),BYTE,PROC,DERIVED
WinFlag                BYTE
                     END
  CODE
  Loc
  Mo
  Gr
  PG:
  MyGrp.
  pI
  Pr
  MG:
  LOC:Count = pId + ModCounter
  RETURN
