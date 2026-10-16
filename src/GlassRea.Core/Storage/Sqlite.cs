using System.Runtime.InteropServices;

namespace GlassRea.Core.Storage;

/// <summary>
/// A small binding to Windows' own SQLite (System32\winsqlite3.dll, Microsoft-signed). Used instead of a NuGet SQLite
/// wrapper because Smart App Control refuses to load unsigned downloaded libraries into the service.
/// Not thread-safe by itself; <see cref="Database"/> serializes access.
/// </summary>
public sealed class SqliteDb : IDisposable
{
    private nint _db;

    public SqliteDb(string path)
    {
        const int ReadWrite = 0x2, Create = 0x4, FullMutex = 0x10000;
        var rc = SqliteNative.sqlite3_open_v2(path, out _db, ReadWrite | Create | FullMutex, 0);
        if (rc != SqliteNative.Ok)
        {
            var message = SqliteNative.Message(_db);
            SqliteNative.sqlite3_close_v2(_db);
            throw new SqliteException($"Could not open {path}: {message}");
        }
        SqliteNative.sqlite3_busy_timeout(_db, 5000);
    }

    /// <summary>Runs one or more statements that take no parameters.</summary>
    public void Exec(string sql)
    {
        if (SqliteNative.sqlite3_exec(_db, sql, 0, 0, out var err) != SqliteNative.Ok)
        {
            var message = err != 0 ? Marshal.PtrToStringUTF8(err) : SqliteNative.Message(_db);
            if (err != 0) SqliteNative.sqlite3_free(err);
            throw new SqliteException(message ?? "SQLite error");
        }
    }

    public SqliteStatement Prepare(string sql)
    {
        if (SqliteNative.sqlite3_prepare_v2(_db, sql, -1, out var stmt, 0) != SqliteNative.Ok)
            throw new SqliteException(SqliteNative.Message(_db));
        return new SqliteStatement(_db, stmt);
    }

    public SqliteTransaction BeginTransaction()
    {
        Exec("BEGIN");
        return new SqliteTransaction(this);
    }

    public void Dispose()
    {
        if (_db == 0) return;
        SqliteNative.sqlite3_close_v2(_db);
        _db = 0;
    }
}

public sealed class SqliteTransaction(SqliteDb db) : IDisposable
{
    private bool _done;

    public void Commit() { db.Exec("COMMIT"); _done = true; }

    public void Dispose()
    {
        if (!_done) db.Exec("ROLLBACK");
        _done = true;
    }
}

/// <summary>A prepared statement. Parameters are named with their prefix ("$k"); values are long, int, bool, double, string or null.</summary>
public sealed class SqliteStatement : IDisposable
{
    private const int Integer = 1, Float = 2, Text = 3;
    private readonly nint _db;
    private nint _stmt;

    internal SqliteStatement(nint db, nint stmt) { _db = db; _stmt = stmt; }

    public object? this[string name]
    {
        set => Bind(name, value);
    }

    public void Bind(string name, object? value)
    {
        var i = SqliteNative.sqlite3_bind_parameter_index(_stmt, name);
        if (i == 0) throw new SqliteException($"No parameter {name} in statement.");
        var rc = value switch
        {
            null => SqliteNative.sqlite3_bind_null(_stmt, i),
            long l => SqliteNative.sqlite3_bind_int64(_stmt, i, l),
            int n => SqliteNative.sqlite3_bind_int64(_stmt, i, n),
            bool b => SqliteNative.sqlite3_bind_int64(_stmt, i, b ? 1 : 0),
            double d => SqliteNative.sqlite3_bind_double(_stmt, i, d),
            string s => SqliteNative.sqlite3_bind_text(_stmt, i, s, -1, SqliteNative.Transient),
            _ => throw new SqliteException($"Cannot bind a {value.GetType().Name}."),
        };
        if (rc != SqliteNative.Ok) throw new SqliteException(SqliteNative.Message(_db));
    }

    public int ExecuteNonQuery()
    {
        try { while (Step()) { } return SqliteNative.sqlite3_changes(_db); }
        finally { SqliteNative.sqlite3_reset(_stmt); }
    }

    /// <summary>First column of the first row, or null when there is no row.</summary>
    public object? ExecuteScalar()
    {
        try { return Step() ? Value(0) : null; }
        finally { SqliteNative.sqlite3_reset(_stmt); }
    }

    /// <summary>Iterate with Read(); disposing the reader resets the statement for reuse.</summary>
    public SqliteReader ExecuteReader() => new(this);

    /// <summary>Advances one row. False when done; throws on error.</summary>
    internal bool Step()
    {
        var rc = SqliteNative.sqlite3_step(_stmt);
        if (rc == SqliteNative.Row) return true;
        if (rc == SqliteNative.Done) return false;
        var message = SqliteNative.Message(_db);
        SqliteNative.sqlite3_reset(_stmt);
        throw new SqliteException(message);
    }

    internal void Reset() => SqliteNative.sqlite3_reset(_stmt);

    internal bool IsNull(int col) => SqliteNative.sqlite3_column_type(_stmt, col) == 5;

    internal long GetInt64(int col) => SqliteNative.sqlite3_column_int64(_stmt, col);

    internal string GetString(int col)
    {
        var p = SqliteNative.sqlite3_column_text(_stmt, col);
        return p == 0 ? "" : Marshal.PtrToStringUTF8(p, SqliteNative.sqlite3_column_bytes(_stmt, col));
    }

    private object? Value(int col) => SqliteNative.sqlite3_column_type(_stmt, col) switch
    {
        Integer => GetInt64(col),
        Float => SqliteNative.sqlite3_column_double(_stmt, col),
        Text => GetString(col),
        5 => null,
        _ => throw new SqliteException("Blob columns are not used."),
    };

    public void Dispose()
    {
        if (_stmt == 0) return;
        SqliteNative.sqlite3_finalize(_stmt);
        _stmt = 0;
    }
}

public readonly struct SqliteReader(SqliteStatement stmt) : IDisposable
{
    public bool Read() => stmt.Step();
    public bool IsDBNull(int col) => stmt.IsNull(col);
    public long GetInt64(int col) => stmt.GetInt64(col);
    public string GetString(int col) => stmt.GetString(col);
    public void Dispose() => stmt.Reset();
}

public sealed class SqliteException(string message) : Exception(message);

internal static partial class SqliteNative
{
    private const string Lib = "winsqlite3.dll";
    public const int Ok = 0, Row = 100, Done = 101;
    public static readonly nint Transient = -1; // SQLITE_TRANSIENT: SQLite copies the text before the call returns

    public static string Message(nint db) => db == 0 ? "SQLite error" : Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "SQLite error";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_open_v2(string filename, out nint db, int flags, nint vfs);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_close_v2(nint db);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_busy_timeout(nint db, int ms);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_exec(nint db, string sql, nint callback, nint arg, out nint errmsg);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial void sqlite3_free(nint p);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_prepare_v2(nint db, string sql, int nBytes, out nint stmt, nint tail);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_bind_parameter_index(nint stmt, string name);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_bind_null(nint stmt, int i);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_bind_int64(nint stmt, int i, long value);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_bind_double(nint stmt, int i, double value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_bind_text(nint stmt, int i, string value, int n, nint destructor);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_step(nint stmt);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_reset(nint stmt);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_finalize(nint stmt);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_changes(nint db);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_column_type(nint stmt, int col);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial long sqlite3_column_int64(nint stmt, int col);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial double sqlite3_column_double(nint stmt, int col);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint sqlite3_column_text(nint stmt, int col);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int sqlite3_column_bytes(nint stmt, int col);

    [LibraryImport(Lib)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial nint sqlite3_errmsg(nint db);
}
