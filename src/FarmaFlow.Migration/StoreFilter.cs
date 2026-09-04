using Npgsql;

namespace FarmaFlow.Migration;

internal static class StoreFilter
{
    internal static async Task RunAsync(IReadOnlyDictionary<string, string> values)
    {
        string host = values.GetValueOrDefault("host", "127.0.0.1");
        int port = int.Parse(values.GetValueOrDefault("port", "54329"));
        string database = Required(values, "database");
        string username = values.GetValueOrDefault("username", "farmaflow");
        Guid storeId = Guid.Parse(Required(values, "store-id"));
        if (!database.Contains("staging", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O filtro só pode ser executado em um banco cujo nome contenha 'staging'.");

        string password = ReadSecret("Senha do PostgreSQL de staging: ");
        Console.Write($"Esta operação removerá do banco {database} tudo que não pertence à loja {storeId}. Digite o nome do banco para confirmar: ");
        if (!string.Equals(Console.ReadLine(), database, StringComparison.Ordinal))
            throw new InvalidOperationException("Confirmação recusada.");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Database = database, Username = username,
            Password = password, SslMode = SslMode.Prefer, Timeout = 30, CommandTimeout = 0
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            Guid organizationId = await OrganizationIdAsync(connection, transaction, storeId);
            List<TableInfo> tables = await ReadTablesAsync(connection, transaction);
            List<ForeignKeyInfo> foreignKeys = await ReadForeignKeysAsync(connection, transaction);

            await ExecuteAsync(connection, transaction, "SET LOCAL lock_timeout = '10s'; SET LOCAL statement_timeout = '0';");
            foreach (TableInfo table in tables)
                await ExecuteAsync(connection, transaction, $"LOCK TABLE {table.QualifiedName} IN SHARE ROW EXCLUSIVE MODE");

            await PrepareTransferSnapshotsAsync(connection, transaction, storeId);
            await ScrubSecretsAsync(connection, transaction, tables, organizationId);
            await ExecuteAsync(connection, transaction, "CREATE TEMP TABLE ff_selected(table_oid integer NOT NULL, row_tid tid NOT NULL, kind smallint NOT NULL, PRIMARY KEY(table_oid,row_tid)) ON COMMIT DROP");

            foreach (TableInfo table in tables)
            {
                string? predicate = SeedPredicate(table);
                if (predicate is null) continue;
                await using var command = new NpgsqlCommand(
                    $"INSERT INTO ff_selected(table_oid,row_tid,kind) SELECT @oid,ctid,1 FROM {table.QualifiedName} WHERE {predicate} ON CONFLICT DO NOTHING",
                    connection,
                    transaction);
                command.Parameters.AddWithValue("oid", table.Oid);
                command.Parameters.AddWithValue("store", storeId);
                command.Parameters.AddWithValue("organization", organizationId);
                await command.ExecuteNonQueryAsync();
            }

            await PropagateScopeAsync(connection, transaction, foreignKeys);
            await PropagateSupportAsync(connection, transaction, foreignKeys);

            await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role = replica");
            foreach (TableInfo table in tables.Where(table => table.Name != "flyway_schema_history"))
            {
                await using var delete = new NpgsqlCommand(
                    $"DELETE FROM {table.QualifiedName} row WHERE NOT EXISTS (SELECT 1 FROM ff_selected selected WHERE selected.table_oid=@oid AND selected.row_tid=row.ctid)",
                    connection,
                    transaction);
                delete.Parameters.AddWithValue("oid", table.Oid);
                await delete.ExecuteNonQueryAsync();
            }
            await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role = origin");

            await ValidateForeignKeysAsync(connection, transaction, foreignKeys);
            await ResetSequencesAsync(connection, transaction);
            long stores = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM public.stores"));
            long organizations = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM public.organizations"));
            if (stores != 1 || organizations != 1)
                throw new InvalidOperationException($"Isolamento inválido: {stores} lojas e {organizations} organizações permaneceram.");
            await transaction.CommitAsync();
            Console.WriteLine($"Staging filtrado com sucesso para a loja {storeId} e organização {organizationId}.");
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            password = string.Empty;
        }
    }

    private static string? SeedPredicate(TableInfo table) => SeedPredicateForTable(table.Name, table.Columns);

    internal static string? SeedPredicateForTable(string tableName, IReadOnlySet<string> columns)
    {
        if (tableName == "stores" && columns.Contains("id")) return "id=@store";
        if (tableName == "organizations" && columns.Contains("id")) return "id=@organization";
        if (tableName == "flyway_schema_history" || tableName.StartsWith("cmed_", StringComparison.Ordinal)) return "TRUE";
        if (tableName == "label_templates" && columns.Contains("store_id") && columns.Contains("is_system"))
            return "store_id=@store OR (store_id IS NULL AND is_system)";
        if (columns.Contains("store_id")) return "store_id=@store";
        if (columns.Contains("organization_id")) return "organization_id=@organization";
        return null;
    }

    private static async Task PropagateScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, List<ForeignKeyInfo> foreignKeys)
    {
        int inserted;
        do
        {
            inserted = 0;
            foreach (ForeignKeyInfo foreignKey in foreignKeys.Where(CanInheritScope))
            {
                string join = foreignKey.Join("child", "parent");
                string nonNull = string.Join(" AND ", foreignKey.ChildColumns.Select(column => $"child.{Quote(column)} IS NOT NULL"));
                string sql = $"""
                    INSERT INTO ff_selected(table_oid,row_tid,kind)
                    SELECT @child_oid,child.ctid,1
                    FROM {foreignKey.Child.QualifiedName} child
                    JOIN {foreignKey.Parent.QualifiedName} parent ON {join}
                    JOIN ff_selected selected ON selected.table_oid=@parent_oid AND selected.row_tid=parent.ctid AND selected.kind=1
                    WHERE {nonNull}
                    ON CONFLICT DO NOTHING
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("child_oid", foreignKey.Child.Oid);
                command.Parameters.AddWithValue("parent_oid", foreignKey.Parent.Oid);
                inserted += await command.ExecuteNonQueryAsync();
            }
        } while (inserted > 0);
    }

    private static bool CanInheritScope(ForeignKeyInfo foreignKey) =>
        foreignKey.Child.Name is not "stores" and not "organizations"
        && !foreignKey.Child.Columns.Contains("store_id")
        && !foreignKey.Child.Columns.Contains("organization_id");

    private static async Task PropagateSupportAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, List<ForeignKeyInfo> foreignKeys)
    {
        int inserted;
        do
        {
            inserted = 0;
            foreach (ForeignKeyInfo foreignKey in foreignKeys)
            {
                string join = foreignKey.Join("child", "parent");
                string sql = $"""
                    INSERT INTO ff_selected(table_oid,row_tid,kind)
                    SELECT @parent_oid,parent.ctid,2
                    FROM {foreignKey.Child.QualifiedName} child
                    JOIN ff_selected selected ON selected.table_oid=@child_oid AND selected.row_tid=child.ctid
                    JOIN {foreignKey.Parent.QualifiedName} parent ON {join}
                    ON CONFLICT DO NOTHING
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("child_oid", foreignKey.Child.Oid);
                command.Parameters.AddWithValue("parent_oid", foreignKey.Parent.Oid);
                inserted += await command.ExecuteNonQueryAsync();
            }
        } while (inserted > 0);
    }

    private static async Task PrepareTransferSnapshotsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid storeId)
    {
        const string sql = """
            ALTER TABLE public.inventory_transfers
                ADD COLUMN IF NOT EXISTS origin_store_snapshot_id UUID,
                ADD COLUMN IF NOT EXISTS origin_store_snapshot_name VARCHAR(255),
                ADD COLUMN IF NOT EXISTS destination_store_snapshot_id UUID,
                ADD COLUMN IF NOT EXISTS destination_store_snapshot_name VARCHAR(255);
            UPDATE public.inventory_transfers transfer
            SET origin_store_snapshot_id=origin.id, origin_store_snapshot_name=origin.name,
                destination_store_snapshot_id=destination.id, destination_store_snapshot_name=destination.name
            FROM public.stores origin, public.stores destination
            WHERE origin.id=transfer.origin_store_id AND destination.id=transfer.destination_store_id;
            ALTER TABLE public.inventory_transfers ALTER COLUMN origin_store_id DROP NOT NULL;
            ALTER TABLE public.inventory_transfers ALTER COLUMN destination_store_id DROP NOT NULL;
            UPDATE public.inventory_transfer_lot_items item
            SET source_inventory_lot_id=NULL
            FROM public.inventory_lots lot
            WHERE lot.id=item.source_inventory_lot_id AND lot.store_id<>@store;
            UPDATE public.inventory_transfers SET origin_store_id=NULL WHERE origin_store_id<>@store;
            UPDATE public.inventory_transfers SET destination_store_id=NULL WHERE destination_store_id<>@store;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("store", storeId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ScrubSecretsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, List<TableInfo> tables, Guid organizationId)
    {
        if (tables.Any(table => table.Name == "auth_sessions"))
            await ExecuteAsync(connection, transaction, "TRUNCATE TABLE public.auth_sessions");
        if (tables.Any(table => table.Name == "integration_outbox_events"))
            await ExecuteAsync(connection, transaction, "TRUNCATE TABLE public.integration_outbox_events");
        if (tables.Any(table => table.Name == "messaging_channels"))
            await ExecuteAsync(connection, transaction, "UPDATE public.messaging_channels SET credentials_ciphertext='LOCAL_DISABLED', webhook_secret=md5(id::text || clock_timestamp()::text), active=false, status='DISCONNECTED', external_account_id=NULL, business_account_id=NULL, business_portfolio_id=NULL");
        if (tables.Any(table => table.Name == "store_stations"))
            await ExecuteAsync(connection, transaction, "UPDATE public.store_stations SET agent_token_hash=NULL, agent_credential_hash=NULL, active=false");
        if (tables.Any(table => table.Name == "cmed_import_runs") && tables.Any(table => table.Name == "users"))
        {
            await using var command = new NpgsqlCommand("""
                UPDATE public.cmed_import_runs run
                SET imported_by_user_id=NULL
                WHERE imported_by_user_id IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM public.users value
                      WHERE value.id=run.imported_by_user_id AND value.organization_id=@organization
                  )
                """, connection, transaction);
            command.Parameters.AddWithValue("organization", organizationId);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task ValidateForeignKeysAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, List<ForeignKeyInfo> foreignKeys)
    {
        foreach (ForeignKeyInfo foreignKey in foreignKeys)
        {
            string join = foreignKey.Join("child", "parent");
            string nonNull = string.Join(" AND ", foreignKey.ChildColumns.Select(column => $"child.{Quote(column)} IS NOT NULL"));
            string missing = $"parent.{Quote(foreignKey.ParentColumns[0])} IS NULL";
            string sql = $"SELECT COUNT(*) FROM {foreignKey.Child.QualifiedName} child LEFT JOIN {foreignKey.Parent.QualifiedName} parent ON {join} WHERE {nonNull} AND {missing}";
            long orphanCount = Convert.ToInt64(await ScalarAsync(connection, transaction, sql));
            if (orphanCount > 0) throw new InvalidOperationException($"A filtragem criou {orphanCount} órfãos em {foreignKey.Name}.");
        }
    }

    private static async Task ResetSequencesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        const string sql = """
            SELECT format(
                'SELECT setval(%L, COALESCE((SELECT MAX(%I) FROM %I.%I), 1), (SELECT COUNT(*) > 0 FROM %I.%I))',
                schemaname || '.' || sequencename,
                replace(sequencename, '_seq', ''), schemaname, replace(sequencename, '_seq', ''), schemaname, replace(sequencename, '_seq', '')
            )
            FROM pg_catalog.pg_sequences
            WHERE schemaname='public'
            """;
        // Sequence ownership and column naming are not guaranteed, so use pg_get_serial_sequence instead.
        const string owned = """
            SELECT format('SELECT setval(%L, COALESCE(MAX(%I),1), COUNT(*)>0) FROM %I.%I',
                          pg_get_serial_sequence(format('%I.%I', n.nspname,c.relname), a.attname),
                          a.attname,n.nspname,c.relname)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0
            WHERE n.nspname='public' AND pg_get_serial_sequence(format('%I.%I',n.nspname,c.relname),a.attname) IS NOT NULL
            """;
        _ = sql;
        var commands = new List<string>();
        await using (var command = new NpgsqlCommand(owned, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) commands.Add(reader.GetString(0));
        foreach (string command in commands) await ExecuteAsync(connection, transaction, command);
    }

    private static async Task<Guid> OrganizationIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid storeId)
    {
        await using var command = new NpgsqlCommand("SELECT organization_id FROM public.stores WHERE id=@store", connection, transaction);
        command.Parameters.AddWithValue("store", storeId);
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Loja não encontrada no staging."));
    }

    private static async Task<List<TableInfo>> ReadTablesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        const string sql = """
            SELECT c.oid::integer,c.relname,a.attname
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            WHERE n.nspname='public' AND c.relkind IN ('r','p')
            ORDER BY c.oid,a.attnum
            """;
        var map = new Dictionary<int, TableInfo>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            int oid = reader.GetInt32(0);
            if (!map.TryGetValue(oid, out TableInfo? table)) map[oid] = table = new TableInfo(oid, reader.GetString(1));
            if (!reader.IsDBNull(2)) table.Columns.Add(reader.GetString(2));
        }
        return map.Values.ToList();
    }

    private static async Task<List<ForeignKeyInfo>> ReadForeignKeysAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        const string sql = """
            SELECT con.oid::integer,con.conname,child.oid::integer,child.relname,parent.oid::integer,parent.relname,
                   child_column.attname,parent_column.attname,keys.ordinality
            FROM pg_constraint con
            JOIN pg_class child ON child.oid=con.conrelid
            JOIN pg_class parent ON parent.oid=con.confrelid
            JOIN pg_namespace namespace ON namespace.oid=child.relnamespace AND namespace.nspname='public'
            JOIN LATERAL unnest(con.conkey,con.confkey) WITH ORDINALITY keys(child_attnum,parent_attnum,ordinality) ON true
            JOIN pg_attribute child_column ON child_column.attrelid=child.oid AND child_column.attnum=keys.child_attnum
            JOIN pg_attribute parent_column ON parent_column.attrelid=parent.oid AND parent_column.attnum=keys.parent_attnum
            WHERE con.contype='f'
            ORDER BY con.oid,keys.ordinality
            """;
        var tables = (await ReadTablesAsync(connection, transaction)).ToDictionary(table => table.Oid);
        var map = new Dictionary<int, ForeignKeyInfo>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            int oid = reader.GetInt32(0);
            if (!map.TryGetValue(oid, out ForeignKeyInfo? foreignKey))
                map[oid] = foreignKey = new ForeignKeyInfo(reader.GetString(1), tables[reader.GetInt32(2)], tables[reader.GetInt32(4)]);
            foreignKey.ChildColumns.Add(reader.GetString(6));
            foreignKey.ParentColumns.Add(reader.GetString(7));
        }
        return map.Values.ToList();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync();
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException($"Informe --{name}.");

    private static string ReadSecret(string prompt) => ProcessSecretReader.Read(prompt);

    private sealed class TableInfo(int oid, string name)
    {
        internal int Oid { get; } = oid;
        internal string Name { get; } = name;
        internal HashSet<string> Columns { get; } = new(StringComparer.Ordinal);
        internal string QualifiedName => $"public.{Quote(Name)}";
    }

    private sealed class ForeignKeyInfo(string name, TableInfo child, TableInfo parent)
    {
        internal string Name { get; } = name;
        internal TableInfo Child { get; } = child;
        internal TableInfo Parent { get; } = parent;
        internal List<string> ChildColumns { get; } = [];
        internal List<string> ParentColumns { get; } = [];
        internal string Join(string childAlias, string parentAlias) => string.Join(" AND ", ChildColumns.Zip(
            ParentColumns,
            (childColumn, parentColumn) => $"{childAlias}.{Quote(childColumn)}={parentAlias}.{Quote(parentColumn)}"));
    }
}
