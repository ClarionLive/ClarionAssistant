  MEMBER('bigprog.clw')
! fake-lsp-undeclared: GlobBefore,ADDONS,PINVDET,FxTailLocal,FxNoSuchThing

UseGlobals  PROCEDURE()
  CODE
  GlobBefore = 1
  OPEN(ADDONS)
  OPEN(PINVDET)
  FxTailLocal = 2
  FxNoSuchThing = 3
