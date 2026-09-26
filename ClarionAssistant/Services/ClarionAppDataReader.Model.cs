using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The dictionary schema DTOs - TableDef, FieldDef, KeyDef, RelationDef, FieldMap - in a file of
    /// their own. They were nested in ClarionAppDataReader.cs, which also names AppTreeService and
    /// RedFileService (IDE-coupled), so the standalone mcp-server build could not compile any
    /// interface that mentioned them. IAppTreeService.ReadLiveDictionaryTables (GitHub #210) does.
    /// Same partial class, same nested names: every existing ClarionAppDataReader.TableDef reference
    /// is untouched. This file must stay free of IDE types - it is linked into mcp-server.csproj.
    /// </summary>
    public static partial class ClarionAppDataReader
    {
        public sealed class FieldDef
        {
            public string Name;
            public string Type;
            public List<FieldDef> Children;
            // Populated by the .txa source (ParseTxaProcedureData) / live dict — the APP's display metadata.
            public string Picture;
            public string Prompt;
            public string Header;
            public string Description; // dictionary column description (live dict)
            public string DerivedFrom; // source field label if this column is derived (live dict)
            // Extra display metadata off the .txa "!!>" line (a9aa19ba detail panels). Everything else the
            // panel shows (PRE/THREAD/EXTERNAL/DLL/STATIC/DIM/OVER/NAME/UPR/CAP + trailing !comment) is
            // already carried inside Type — the full declaration tail — and parsed page-side.
            public string Tooltip;  // TOOLTIP('...') — the FieldForm's Help/Tooltip text
            public string Message;  // MESSAGE('...') — the status-bar Msg text
            public string TypeMode; // TYPEMODE(INS|OVR|...) — typing mode
            public string Justify;  // JUSTIFY(RIGHT,1) etc. — justification + offset
            // Live-dictionary column detail (ReadLiveField, a9aa19ba round 2) — null/empty when the
            // source is the .txa (variables) or the field simply has no value.
            public string ExternalName;   // SQL/external column name
            public string InitialValue;
            public string Dimensions;     // "5" / "5,3" from Dimension1..4
            public string CaseText;       // Uppercase / Word Capitalized (Normal omitted)
            public string HelpId;
            public string RowPicture;     // only when it differs from ScreenPicture
            public string Validity;       // friendly validity-check summary
            public List<string> Flags;    // READ ONLY / PASSWORD / IMMEDIATE / AUTO-NUMBER / ...
        }

        // A dictionary key/index (rich form, populated by the live dictionary reader).
        public sealed class KeyDef
        {
            public string Name;                                  // unprefixed label (PK_Address_AddressID)
            public readonly List<FieldDef> Components = new List<FieldDef>(); // member columns (full detail)
            public string KeyType = "KEY";                       // KEY | INDEX
            public bool Primary;
            public bool Unique;
            public bool CaseSensitive;
            public bool AutoNumber;      // DDKey.AttributeAutoNum (a9aa19ba round 2)
            public bool ExcludeEmpty;    // DDKey.AttributeExclude — exclude empty keys
            public string Description;
        }

        // A dictionary relationship to another table (populated by the live dictionary reader). Named by the
        // RELATED table (matching Clarion's dict Relations view, which lists the other table per row).
        public sealed class RelationDef
        {
            public string Name;                 // the related (other) table's name — the row label
            public string Type;                 // "1:MANY" | "MANY:1" | "1:1"
            public string PrimaryKey;           // the key on the primary (parent) side
            public string ForeignKey;           // the key on the foreign (child) side
            public readonly List<FieldMap> Mappings = new List<FieldMap>(); // column pairings
        }

        // One column pairing in a relationship: this table's field ↔ the related table's field.
        public sealed class FieldMap
        {
            public string From;                 // a field on the row's table
            public string To;                   // the paired field on the related table
        }

        public sealed class TableDef
        {
            public string Name;
            public string Prefix = "";
            public readonly List<FieldDef> Fields = new List<FieldDef>();
            public readonly List<string> Keys = new List<string>();   // legacy name-only (clw/.dcv sources)
            // Rich attributes, populated by the live dictionary reader (ReadLiveDictionaryTables).
            public readonly List<KeyDef> KeyDefs = new List<KeyDef>();
            public readonly List<RelationDef> Relations = new List<RelationDef>();
            public string Driver = "";
            public string DriverOptions = "";
            public string Owner = "";
            public string FullName = "";
            public string Description = "";
            public bool Bindable;
            public bool Threaded;
        }
    }
}
