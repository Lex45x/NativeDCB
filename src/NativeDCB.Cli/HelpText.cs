namespace NativeDCB.Cli;

internal static class HelpText
{
    public const string Main = """
                               NativeDCB CLI - all NativeDCB v1 RPCs

                               Usage:
                                 nativedcb [--server URL] <group> <command> [options]
                                 nativedcb <group> <command> --help

                               Global option:
                                 --server URL  gRPC endpoint (default: NATIVEDCB_SERVER or http://localhost:5010)

                               Commands (RPC):
                                 database list                         DatabaseService.ListDatabases
                                 database create                       DatabaseService.CreateDatabase
                                 database info                         DatabaseService.GetDatabaseInfo
                                 database health                       DatabaseService.GetHealth
                                 database capabilities                 DatabaseService.GetCapabilities
                                 database head                         DatabaseService.GetHead
                                 catalog register-event-schema         CatalogService.RegisterEventSchema
                                 catalog register-command-schema       CatalogService.RegisterCommandSchema
                                 catalog remove-schema                 CatalogService.RemoveSchema
                                 catalog register-handler              CatalogService.RegisterHandler
                                 catalog remove-handler                CatalogService.RemoveHandler
                                 catalog get-handler                   CatalogService.GetHandler
                                 catalog list-handlers                 CatalogService.ListHandlers
                                 catalog validate-ndl                  CatalogService.ValidateNdl
                                 command execute-handler               CommandService.ExecuteHandler
                                 command events-by-command-id          CommandService.GetEventsByCommandId
                                 event read-range                      EventService.ReadEventsByRange
                                 event read-query                      EventService.ReadEventsByQuery
                                 event read-type-and-keys              EventService.ReadEventsByTypeAndKeys
                                 event subscribe                       EventService.SubscribeEvents
                                 statement execute                     StatementService.ExecuteStatement
                                 statement explain                     StatementService.ExplainStatement
                                 admin list-partitions                 AdministrationService.ListPartitions
                                 admin list-indexes                    AdministrationService.ListIndexes
                                 admin state-file-status               AdministrationService.GetStateFileStatus
                                 admin rebuild-index                   AdministrationService.RequestIndexRebuild
                                 admin rebuild-state                   AdministrationService.RequestStateRebuild

                               Input conventions:
                                 JSON: --schema JSON | --schema-file PATH | --schema-stdin (similarly command, plan, query)
                                 NDL:  --ndl TEXT | --ndl-file PATH | --ndl-stdin (handler source uses --source variants)
                                 Keys: repeat --key name=value. Values may be empty and may contain '='.
                                 Query: complete protobuf JSON via --query variants; repeat --query-item JSON; or build one
                                        item with repeated --event-type and --key. Subscribe uses --query-key for query-item
                                        keys and --key for subscription-wide keys.
                                 Transient schemas: repeat --transient event:name=JSON, --transient-file event:name=PATH,
                                                    or --transient-stdin event:name; use command:name for command schemas.

                               Output and exits:
                                 Unary responses are one protobuf JSON object. Streams are JSONL, one object per line.
                                 0 success; 64 usage; 65 invalid input/application rejection; 69 unavailable/deadline;
                                 70 RPC/software error; 74 file/stdin I/O error; 77 authentication/authorization; 130 cancelled.
                               """;

    private static readonly Dictionary<string, string> Commands = new(StringComparer.Ordinal)
    {
        ["database list"] = "Usage: nativedcb database list [--server URL]",
        ["database create"] = "Usage: nativedcb database create --database NAME [--server URL]",
        ["database info"] = "Usage: nativedcb database info --database NAME [--server URL]",
        ["database health"] = "Usage: nativedcb database health [--server URL]",
        ["database capabilities"] = "Usage: nativedcb database capabilities [--server URL]",
        ["database head"] = "Usage: nativedcb database head --database NAME [--server URL]",
        ["catalog register-event-schema"] = SchemaRegistration("register-event-schema"),
        ["catalog register-command-schema"] = SchemaRegistration("register-command-schema"),
        ["catalog remove-schema"] =
            "Usage: nativedcb catalog remove-schema --database NAME --name NAME --kind event|command",
        ["catalog register-handler"] = """
                                       Usage: nativedcb catalog register-handler --database NAME --name NAME --command-type TYPE
                                                  [--source TEXT|--source-file PATH|--source-stdin]
                                                  [--plan JSON|--plan-file PATH|--plan-stdin] [--allow-incompatible]
                                       At least source or plan is required. Both may be supplied.
                                       """,
        ["catalog remove-handler"] = "Usage: nativedcb catalog remove-handler --database NAME --name NAME",
        ["catalog get-handler"] = "Usage: nativedcb catalog get-handler --database NAME --name NAME",
        ["catalog list-handlers"] = "Usage: nativedcb catalog list-handlers --database NAME",
        ["catalog validate-ndl"] = """
                                   Usage: nativedcb catalog validate-ndl --database NAME
                                              (--ndl TEXT|--ndl-file PATH|--ndl-stdin)
                                              [--transient event:name=JSON] [--transient-file command:name=PATH]
                                              [--transient-stdin event:name]
                                   Transient options are repeatable. Only one option may consume stdin.
                                   """,
        ["command execute-handler"] = """
                                      Usage: nativedcb command execute-handler --database NAME --handler NAME [--command-id ID]
                                                 (--command JSON|--command-file PATH|--command-stdin)
                                      """,
        ["command events-by-command-id"] =
            "Usage: nativedcb command events-by-command-id --database NAME --command-id ID",
        ["event read-range"] = """
                               Usage: nativedcb event read-range --database NAME [--after ID] [--through ID] [--limit N]
                                          [--mode snapshot|follow]
                               Follow streams until Ctrl+C.
                               """,
        ["event read-query"] = """
                               Usage: nativedcb event read-query --database NAME QUERY [--after ID] [--through ID] [--limit N]
                                          [--consistency eventual-index|committed-scan]
                               QUERY is --query JSON/--query-file PATH/--query-stdin, repeatable --query-item JSON,
                               or repeated --event-type TYPE and --key name=value convenience flags.
                               """,
        ["event read-type-and-keys"] = """
                                       Usage: nativedcb event read-type-and-keys --database NAME --event-type TYPE [--key name=value]
                                                  [--after ID] [--through ID] [--limit N]
                                                  [--consistency eventual-index|committed-scan]
                                       """,
        ["event subscribe"] = """
                              Usage: nativedcb event subscribe --database NAME [--after ID] [QUERY] [--key name=value]
                              QUERY is --query JSON/--query-file PATH/--query-stdin, repeatable --query-item JSON,
                              or repeated --event-type TYPE and --query-key name=value. Streams until Ctrl+C.
                              """,
        ["statement execute"] = """
                                Usage: nativedcb statement execute --database NAME (--ndl TEXT|--ndl-file PATH|--ndl-stdin)
                                           [--allow-incompatible]
                                """,
        ["statement explain"] =
            "Usage: nativedcb statement explain --database NAME (--ndl TEXT|--ndl-file PATH|--ndl-stdin)",
        ["admin list-partitions"] = "Usage: nativedcb admin list-partitions --database NAME",
        ["admin list-indexes"] = "Usage: nativedcb admin list-indexes --database NAME",
        ["admin state-file-status"] =
            "Usage: nativedcb admin state-file-status --database NAME --partition NUMBER",
        ["admin rebuild-index"] =
            "Usage: nativedcb admin rebuild-index --database NAME --event-type TYPE [--key name=value]",
        ["admin rebuild-state"] =
            "Usage: nativedcb admin rebuild-state --database NAME --partition NUMBER"
    };

    public static bool TryGetCommand(string command, out string text)
    {
        return Commands.TryGetValue(command, out text!);
    }

    public static bool IsGroup(string group)
    {
        return Commands.Keys.Any(key => key.StartsWith($"{group} ", StringComparison.Ordinal));
    }

    private static string SchemaRegistration(string command)
    {
        return $$"""
                 Usage: nativedcb catalog {{command}} --database NAME --name SCHEMA
                            (--schema JSON|--schema-file PATH|--schema-stdin) [--allow-incompatible]
                 """;
    }
}