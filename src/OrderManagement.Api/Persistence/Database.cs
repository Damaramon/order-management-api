using Microsoft.Data.Sqlite;

namespace OrderManagement.Api.Persistence;

public sealed class Database
{
    private readonly string _connectionString;

    public Database(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configuredPath = configuration["Database:Path"] ?? "data/orders.db";
        var path = Path.GetFullPath(configuredPath, environment.ContentRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var timeout = configuration.GetValue("Database:TimeoutSeconds", 5);
        if (timeout is < 1 or > 60)
            throw new InvalidOperationException("Database timeout must be between 1 and 60 seconds.");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            DefaultTimeout = timeout,
            Pooling = true
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteScalar();
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        using var stream = typeof(Database).Assembly.GetManifestResourceStream("OrderManagement.Schema.sql")!;
        using var reader = new StreamReader(stream);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
