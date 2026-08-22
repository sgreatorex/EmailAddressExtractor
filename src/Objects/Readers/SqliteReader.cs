using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

using HaveIBeenPwned.AddressExtractor.Objects.Attributes;

using Microsoft.Data.Sqlite;

namespace HaveIBeenPwned.AddressExtractor.Objects.Readers;

[ExtensionTypes(".sqlite")]
internal sealed class SqliteReader : ILineReader
{
    private readonly SqliteConnection _connection;

    public SqliteReader(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        _connection = new SqliteConnection(connectionString);
    }

    public async IAsyncEnumerable<string?> ReadLineAsync([EnumeratorCancellation] CancellationToken cancellation = default)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellation).ConfigureAwait(false);
        }

        var tableNames = await ReadTableNamesAsync(cancellation).ConfigureAwait(false);
        foreach (var tableName in tableNames)
        {
            await foreach (var row in ReadTableRowsAsync(tableName, cancellation).ConfigureAwait(false))
            {
                yield return row;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.CloseAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<List<string>> ReadTableNamesAsync(CancellationToken cancellation)
    {
        var tableNames = new List<string>();

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0) && reader.GetString(0) is { Length: > 0 } tableName)
            {
                tableNames.Add(tableName);
            }
        }

        return tableNames;
    }

    private async IAsyncEnumerable<string?> ReadTableRowsAsync(string tableName, [EnumeratorCancellation] CancellationToken cancellation)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(tableName)}";

        await using var reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();

            var values = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (TryFormatValue(reader.GetValue(i), out var value))
                {
                    values.Add(value);
                }
            }

            if (values.Count > 0)
            {
                yield return $"{tableName}\t{string.Join('\t', values)}";
            }
        }
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static bool TryFormatValue(object value, out string text)
    {
        switch (value)
        {
            case null:
            case DBNull:
                text = string.Empty;
                return false;
            case string s when string.IsNullOrWhiteSpace(s):
                text = string.Empty;
                return false;
            case string s:
                text = s;
                return true;
            case byte[] bytes when bytes.Length == 0:
                text = string.Empty;
                return false;
            case byte[] bytes:
                text = Encoding.UTF8.GetString(bytes);
                return !string.IsNullOrWhiteSpace(text);
            default:
                text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return !string.IsNullOrWhiteSpace(text);
        }
    }
}
