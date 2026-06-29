

                                MEMBER('clbrws.clw')                  ! This is a MEMBER module


    INCLUDE('MENUStyle.INC'),ONCE

                                MAP
                                END

!!! <summary>
!!! Generated from procedure template - Frame
!!! Clarion for Windows Wizard Application
!!! </summary>
Main                            PROCEDURE

loc:wm                              &WindowManager

MyGroupMain                         GROUP,PRE(mgm)
GroupFieldMain                          LONG
                                    END

                                    mgm:

LocalRequest                        LONG                                  ! 
OriginalRequest                     LONG                                  ! 
LocalResponse                       LONG                                  ! 
FilesOpened                         LONG                                  ! 
WindowOpened                        LONG                                  ! 
WindowInitialized                   LONG                                  ! 
ForceRefresh                        LONG                                  ! 
CurrentTab                          STRING(80)                            ! 
!just a test here
SplashProcedureThread               LONG
MenuStyleMgr                        MenuStyleManager
AppFrame                            APPLICATION('Clarion Template Application (BROWSES)'),AT(,,529,341),FONT('MS Sans Serif',8), |
                                            RESIZE,CENTER,ICON('WAFRAME.ICO'),MAX,STATUS(-1,80,120,45),SYSTEM,IMM
                                        MENUBAR,USE(?MENUBAR),FONT(,,COLOR:MENUTEXT)
                                            MENU('&File'),USE(?FileMenu)
                                                ITEM('&Print Setup ...'),USE(?PrintSetup),MSG('Setup printer'),STD(STD:PrintSetup)
                                                ITEM,USE(?SEPARATOR1),SEPARATOR
                                                ITEM('E&xit'),USE(?Exit),MSG('Exit this application'),STD(STD:Close)
                                            END
                                            MENU('&Edit'),USE(?EditMenu)
                                                ITEM('Cu&t'),USE(?Cut),MSG('Remove item to Windows Clipboard'),STD(STD:Cut)
                                                ITEM('&Copy'),USE(?Copy),MSG('Copy item to Windows Clipboard'),STD(STD:Copy)
                                                ITEM('&Paste'),USE(?Paste),MSG('Paste contents of Windows Clipboard'),STD(STD:Paste)
                                            END
                                            MENU('&Browse'),USE(?BrowseMenu)
                                                ITEM('Styles (Jobs)'),USE(?BrowseJobs),MSG('Browse Jobs')
                                                ITEM('Relation Tree'),USE(?BrowseRelationTree)
                                                ITEM('QBE List (Titles)'),USE(?BrowseQueryList)
                                                ITEM('Flat List (Publishers)'),USE(?BrowsePublishers),MSG('Browse Publishers')
                                                ITEM('Graph (Jobs && Emps)'),USE(?BrowseGraphJobsEmps)
                                                ITEM('Filtered Locator (Authors)'),USE(?BrowseAuthors),MSG('Browse Authors')
                                                ITEM('RTF && IMAGE (Pub Info)'),USE(?BrowseRTFPubInfo)
                                                ITEM('List Format Manager (Titles)'),USE(?BrowseListFormatManager)
                                                ITEM('Auto-Size Columns (Employees)'),USE(?BrowseAutoSizeColumnsEmployees)
                                                ITEM('Jobs'),USE(?ITEM1)
                                            END
                                            MENU('More Browses'),USE(?MoreBrowses)
                                                MENU('Edit-In-Place'),USE(?BrowseEditInPlace2)
                                                    ITEM('Check (Authors)'),USE(?BrowseCheck)
                                                    ITEM('Spin Control (Jobs)'),USE(?MoreBrowsesEditInPlaceSpinControlJobs)
                                                    ITEM('Text Control (Titles)'),USE(?MoreBrowsesEditInPlaceTextTitles)
                                                    ITEM('DOS File Lookup (Employee)'),USE(?BrowseEditInPlaceDOSFileLookup)
                                                    ITEM('Droplist, Calendar, and Lookup (Sales)'),USE(?BrowseEditInPlace)
                                                END
                                                MENU('Add. Sort Order'),USE(?BrowseAdditionalSortOrder)
                                                    ITEM('Manual (Stores)'),USE(?BrowseStores),MSG('Browse Stores')
                                                    ITEM('Assisted (Discounts) '),USE(?BrowseDiscounts),MSG('Browse Discounts')
                                                END
                                                MENU('Colors  (Roysched)'),USE(?BrowseColors)
                                                    ITEM('All Columns '),USE(?BrowsePub_info),MSG('Browse Pub_info')
                                                    ITEM('Conditional COLORS'),USE(?BrowseColorsConditionalCOLORS)
                                                    MENU('Greenbar Effect '),USE(?BrowseColorsGreenbarEffect)
                                                        ITEM('All Columns'),USE(?BrowseRoysched),MSG('Browse Roysched')
                                                        ITEM('Alternate Columns'),USE(?BrowseColorsAlternateColumns)
                                                        ITEM('Selected Column(s)'),USE(?GreenbarEffectSelectedColumns)
                                                    END
                                                END
                                                MENU('Sort Order (TitleAuthors)'),USE(?BrowseSORTORDERCONTROLS)
                                                    ITEM('BUTTON Control'),USE(?BrowseTitleauthor),MSG('Browse Titleauthor')
                                                    ITEM('DROPLIST Control'),USE(?BrowseDROPLIST)
                                                END
                                            END
                                            MENU('&Window'),USE(?WindowMenu),STD(STD:WindowList)
                                                ITEM('T&ile'),USE(?Tile),MSG('Make all open windows visible'),STD(STD:TileWindow)
                                                ITEM('&Cascade'),USE(?Cascade),MSG('Stack all open windows'),STD(STD:CascadeWindow)
                                                ITEM('&Arrange Icons'),USE(?Arrange),MSG('Align all window icons'),STD(STD:ArrangeIcons)
                                            END
                                            MENU('&Help'),USE(?HelpMenu)
                                                ITEM('About...'),USE(?HelpAbout)
                                            END
                                        END
                                        TOOLBAR,AT(0,0,529,16),USE(?Toolbar)
                                            BUTTON,AT(4,2,14,14),USE(?TBarBrwTop, TBarBrwTop),ICON('WAVCRFIRST.ICO'),DISABLE,FLAT,TIP('Go to the ' & |
                                                    'First Page')
                                            BUTTON,AT(18,2,14,14),USE(?TBarBrwPageUp, TBarBrwPageUp),ICON('WAVCRPRIOR.ICO'),DISABLE,FLAT, |
                                                    TIP('Go to the Prior Page')
                                            BUTTON,AT(32,2,14,14),USE(?TBarBrwUp, TBarBrwUp),ICON('WAVCRUP.ICO'),DISABLE,FLAT,TIP('Go to the ' & |
                                                    'Prior Record')
                                            BUTTON,AT(46,2,14,14),USE(?TBarBrwLocate, TBarBrwLocate),ICON('WAFIND.ICO'),DISABLE,FLAT,TIP('Locate record')
                                            BUTTON,AT(60,2,14,14),USE(?TBarBrwDown, TBarBrwDown),ICON('WAVCRDOWN.ICO'),DISABLE,FLAT,TIP('Go to the ' & |
                                                    'Next Record')
                                            BUTTON,AT(74,2,14,14),USE(?TBarBrwPageDown, TBarBrwPageDown),ICON('WAVCRNEXT.ICO'),DISABLE, |
                                                    FLAT,TIP('Go to the Next Page')
                                            BUTTON,AT(88,2,14,14),USE(?TBarBrwBottom, TBarBrwBottom),ICON('WAVCRLAST.ICO'),DISABLE,FLAT, |
                                                    TIP('Go to the Last Page')
                                            BUTTON,AT(102,2,14,14),USE(?TBarBrwSelect, TBarBrwSelect),ICON('WAMARK.ICO'),DISABLE,FLAT, |
                                                    TIP('Select This Record')
                                            BUTTON,AT(116,2,14,14),USE(?TBarBrwInsert, TBarBrwInsert),ICON('WAINSERT.ICO'),DISABLE,FLAT, |
                                                    TIP('Insert a New Record')
                                            BUTTON,AT(130,2,14,14),USE(?TBarBrwChange, TBarBrwChange),ICON('WACHANGE.ICO'),DISABLE,FLAT, |
                                                    TIP('Edit This Record')
                                            BUTTON,AT(144,2,14,14),USE(?TBarBrwDelete, TBarBrwDelete),ICON('WADELETE.ICO'),DISABLE,FLAT, |
                                                    TIP('Delete This Record')
                                            BUTTON,AT(158,2,14,14),USE(?TBarBrwHistory, TBarBrwHistory),ICON('WADITTO.ICO'),DISABLE,FLAT, |
                                                    TIP('Previous value')
                                            BUTTON,AT(172,2,14,14),USE(?TBarBrwHelp, TBarBrwHelp),ICON('WAVCRHELP.ICO'),DISABLE,FLAT,TIP('Get Help')
                                        END
                                    END
    CODE
    PUSHBIND
    LocalRequest    = GlobalRequest
    OriginalRequest = GlobalRequest
    LocalResponse   = RequestCancelled
    ForceRefresh    = False
    CLEAR(GlobalRequest)
    CLEAR(GlobalResponse)
    IF KEYCODE() = MouseRight
        SETKEYCODE(0)
    END
    DO PrepareProcedure
  
    ACCEPT
        CASE EVENT()
        OF EVENT:DoResize
            ForceRefresh = True
            DO RefreshWindow
        OF EVENT:GainFocus
            ForceRefresh = True
            IF NOT WindowInitialized
                DO InitializeWindow
                WindowInitialized = True
            ELSE
                DO RefreshWindow
            END
        OF EVENT:OpenWindow
            SplashProcedureThread = START(SplashScreen)
            IF NOT WindowInitialized
                DO InitializeWindow
                WindowInitialized = True
            END
            SELECT(1)
        OF EVENT:Sized
            POST(EVENT:DoResize,0,THREAD())
        OF Event:Rejected
            BEEP
            DISPLAY(?)
            SELECT(?)
        ELSE
            IF SplashProcedureThread
                IF EVENT() = Event:Accepted
                    POST(Event:CloseWindow,,SplashProcedureThread)
                    SplashPRocedureThread = 0
                END
            END
            IF INRANGE(ACCEPTED(),TBarBrwFirst,TBarBrwLast) THEN            !Toolbar Browse box navigation control handler
                POST(EVENT:Accepted,ACCEPTED(),SYSTEM{PROP:Active})
                CYCLE
            END
        END
        CASE ACCEPTED()
        OF ?BrowseJobs
            START(BrowseJobs, 050000)
        OF ?BrowseRelationTree
            START(ViewAllPublishers, 50000)
        OF ?BrowseQueryList
            START(BrowseTitlesQuery, 50000)
        OF ?BrowsePublishers
            START(BrowsePublishers, 050000)
        OF ?BrowseGraphJobsEmps
            START(BrowseJobsGraphs, 25000)
        OF ?BrowseAuthors
            START(BrowseAuthors, 050000)
        OF ?BrowseRTFPubInfo
            START(BrowsePubInfo, 50000)
        OF ?BrowseListFormatManager
            START(BrowseTitles, 50000)
        OF ?BrowseAutoSizeColumnsEmployees
            START(BrowseEmployee, 50000)
        OF ?ITEM1
            START(SelectJobs, 25000)
        OF ?BrowseCheck
            START(BrowseAuthorsEIP, 25000)
        OF ?MoreBrowsesEditInPlaceSpinControlJobs
            START(BrowseJobsEIP, 50000)
        OF ?MoreBrowsesEditInPlaceTextTitles
            START(BrowseTitlesEIP, 50000)
        OF ?BrowseEditInPlaceDOSFileLookup
            START(BrowseEMPEIP, 50000)
        OF ?BrowseEditInPlace
            START(BrowseSalesEIP, 50000)
        OF ?BrowseStores
            START(BrowseStores, 050000)
        OF ?BrowseDiscounts
            START(BrowseDiscounts, 050000)
        OF ?BrowsePub_info
            START(BrowseRoyschColor, 050000)
        OF ?BrowseColorsConditionalCOLORS
            START(BrowseRoyschCondC, 25000)
        OF ?BrowseRoysched
            START(BrowseRoyschGB, 050000)
        OF ?BrowseColorsAlternateColumns
            START(BrowseRoyschGBAlt, 25000)
        OF ?GreenbarEffectSelectedColumns
            START(BrowseRoyschGBSC, 25000)
        OF ?BrowseTitleauthor
            START(BrowseTitleauthor, 050000)
        OF ?BrowseDROPLIST
            START(BrowseTitleauthorSODL, 25000)
        OF ?HelpAbout
            START(SplashScreen, 25000)
        END
    END
    DO ProcedureReturn
!---------------------------------------------------------------------------
PrepareProcedure                ROUTINE
    FilesOpened = TRUE
    DO BindFields
    OPEN(AppFrame)
    WindowOpened=True
    !System{Prop:Icon} = '~Log1.ico'
    0{PROP:Icon} = '~<1>'
    AppFrame{PROP:TabBarVisible}  = False
    MenuStyleMgr.Init(?MENUBAR)
    MenuStyleMgr.SuspendRefresh()
    MenuStyleMgr.SetThemeColors('XPLunaBlue')
    MenuStyleMgr.SetImageBar(False)
    MenuStyleMgr.ApplyTheme()
    MenuStyleMgr.Refresh(TRUE)   
    INIRestoreWindow('Main','.\CLBRWS.INI')
    Do DefineListboxStyle

!---------------------------------------------------------------------------
BindFields                      ROUTINE
!---------------------------------------------------------------------------
UnBindFields                    ROUTINE
!---------------------------------------------------------------------------
ProcedureReturn                 ROUTINE
!|
!| This routine provides a common procedure exit point for all template
!| generated procedures.
!|
!| First, all of the files opened by this procedure are closed.
!|
!| Next, if it was opened by this procedure, the window is closed.
!|
!| Next, GlobalResponse is assigned a value to signal the calling procedure
!| what happened in this procedure.
!|
!| Next, we replace the BINDings that were in place when the procedure initialized
!| (and saved with PUSHBIND) using POPBIND.
!|
!| Finally, we return to the calling procedure.
!|
    IF FilesOpened
    END
    IF WindowOpened
        INISaveWindow('Main','.\CLBRWS.INI')
        CLOSE(AppFrame)
    END
    Do UnBindFields
    IF LocalResponse
        GlobalResponse = LocalResponse
    ELSE
        GlobalResponse = RequestCancelled
    END
    POPBIND
    RETURN
!---------------------------------------------------------------------------
InitializeWindow                ROUTINE
!|
!| This routine is used to prepare any control templates for use. It should be called once
!| per procedure.
!|
    DO RefreshWindow
!---------------------------------------------------------------------------
RefreshWindow                   ROUTINE
!|
!| This routine is used to keep all displays and control templates current.
!|
    IF AppFrame{Prop:AcceptAll} THEN EXIT.
    Do LookupRelated
    DISPLAY()
    ForceRefresh = False
!---------------------------------------------------------------------------
SyncWindow                      ROUTINE
!|
!| This routine is used to insure that any records pointed to in control
!| templates are fetched before any procedures are called via buttons or menu
!| options.
!|
    Do LookupRelated
!---------------------------------------------------------------------------
LookupRelated                   ROUTINE
!|
!| This routine fetch all related records.
!| It's called from SyncWindow and RefreshWindow
!|
!---------------------------------------------------------------------------
DefineListboxStyle              ROUTINE
!|
!| This routine create all the styles to be shared in this window
!| It's called after the window open
!|

 

!---------------------------------------------------------------------------
MyRoutine                       ROUTINE
    DATA
MyGroup GROUP,PRE(mg)
GroupField  LONG
        END

    CODE
  
  
  
  

