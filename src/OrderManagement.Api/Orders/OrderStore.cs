using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OrderManagement.Api.Contracts;
using OrderManagement.Api.Persistence;
using OrderManagement.Api.Infrastructure;

namespace OrderManagement.Api.Orders;

public sealed record CreateOrderResult(Guid OrderId, string Json, bool Replayed);

public sealed class OrderStore(Database database, ILogger<OrderStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public CreateOrderResult Create(CreateOrderRequest request, string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var normalized = Normalize(request);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized, JsonOptions))));

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        cancellationToken.ThrowIfCancellationRequested();

        using (var lookup = Command(connection, transaction,
            "SELECT RequestHash, OrderId, ResponseJson FROM IdempotencyRequests WHERE Key = $key;", ("$key", key)))
        using (var reader = lookup.ExecuteReader())
        {
            if (reader.Read())
            {
                if (!string.Equals(reader.GetString(0), hash, StringComparison.Ordinal))
                    throw new ApiException(409, "idempotency_key_reused", "Key ini sudah dipakai untuk payload yang berbeda.");

                var replay = new CreateOrderResult(Guid.Parse(reader.GetString(1)), reader.GetString(2), true);
                logger.LogInformation("Replayed create for order {OrderId}", replay.OrderId);
                return replay;
            }
        }

        var orderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (var insert = Command(connection, transaction, """
            INSERT INTO Orders (Id, CustomerId, ShippingAddress, Status, Version, CreatedAt, UpdatedAt)
            VALUES ($id, $customer, $address, 'Pending', 1, $now, $now);
            """, ("$id", orderId.ToString()), ("$customer", normalized.CustomerId.ToString()),
            ("$address", normalized.ShippingAddress), ("$now", Timestamp(now))))
        {
            insert.ExecuteNonQuery();
        }

        var items = new List<OrderItemResponse>();
        foreach (var item in normalized.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name;
            long price;
            using (var product = Command(connection, transaction,
                "SELECT Name, Price FROM Products WHERE Id = $id;", ("$id", item.ProductId)))
            using (var reader = product.ExecuteReader())
            {
                if (!reader.Read())
                    throw new ApiException(404, "product_not_found", $"Produk {item.ProductId} tidak ditemukan.");
                name = reader.GetString(0);
                price = reader.GetInt64(1);
            }

            using (var deduct = Command(connection, transaction, """
                UPDATE Products SET StockQuantity = StockQuantity - $qty
                WHERE Id = $id AND StockQuantity >= $qty;
                """, ("$id", item.ProductId), ("$qty", item.Quantity)))
            {
                if (deduct.ExecuteNonQuery() != 1)
                    throw new ApiException(409, "insufficient_stock", $"Stok produk {item.ProductId} tidak mencukupi.");
            }

            using var insertItem = Command(connection, transaction, """
                INSERT INTO OrderItems (OrderId, ProductId, ProductName, Quantity, UnitPrice)
                VALUES ($order, $product, $name, $qty, $price);
                """, ("$order", orderId.ToString()), ("$product", item.ProductId),
                ("$name", name), ("$qty", item.Quantity), ("$price", price));
            insertItem.ExecuteNonQuery();
            items.Add(new(item.ProductId, name, item.Quantity, price));
        }

        var order = new OrderResponse(orderId, normalized.CustomerId, normalized.ShippingAddress,
            OrderStatus.Pending, 1, now, now, items);
        var json = JsonSerializer.Serialize(order, JsonOptions);
        using (var saveKey = Command(connection, transaction, """
            INSERT INTO IdempotencyRequests (Key, RequestHash, OrderId, ResponseJson, CreatedAt)
            VALUES ($key, $hash, $order, $json, $now);
            """, ("$key", key), ("$hash", hash), ("$order", orderId.ToString()),
            ("$json", json), ("$now", Timestamp(now))))
        {
            saveKey.ExecuteNonQuery();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        logger.LogInformation("Created order {OrderId} with {ItemCount} products", orderId, items.Count);
        return new(orderId, json, false);
    }

    public OrderResponse Get(Guid id)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        return ReadOrder(connection, transaction, id);
    }

    public PagedOrders List(ListOrdersQuery query)
    {
        if (query.DateFrom.HasValue && query.DateTo.HasValue && query.DateFrom >= query.DateTo)
            throw new ApiException(400, "invalid_date_range", "dateFrom harus lebih awal dari dateTo.");
        if (query.CustomerId == Guid.Empty)
            throw new ApiException(400, "invalid_customer", "customerId tidak boleh berupa GUID kosong.");

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        const string filter = """
            WHERE ($status IS NULL OR o.Status = $status)
              AND ($customer IS NULL OR o.CustomerId = $customer)
              AND ($from IS NULL OR o.CreatedAt >= $from)
              AND ($to IS NULL OR o.CreatedAt < $to)
            """;
        (string, object?)[] parameters =
        [
            ("$status", query.Status?.ToString()), ("$customer", query.CustomerId?.ToString()),
            ("$from", query.DateFrom.HasValue ? Timestamp(query.DateFrom.Value) : null),
            ("$to", query.DateTo.HasValue ? Timestamp(query.DateTo.Value) : null)
        ];
        using var count = Command(connection, transaction, "SELECT COUNT(*) FROM Orders o " + filter, parameters);
        var total = (long)count.ExecuteScalar()!;

        using var page = Command(connection, transaction, """
            SELECT o.Id, o.CustomerId, o.Status, o.Version, o.CreatedAt,
                (SELECT SUM(i.Quantity * i.UnitPrice) FROM OrderItems i WHERE i.OrderId = o.Id)
            FROM Orders o
            """ + "\n" + filter + " ORDER BY o.CreatedAt DESC, o.Id DESC LIMIT $limit OFFSET $offset;", parameters);
        page.Parameters.AddWithValue("$limit", query.PageSize);
        page.Parameters.AddWithValue("$offset", ((long)query.Page - 1) * query.PageSize);
        using var reader = page.ExecuteReader();
        var items = new List<OrderSummary>();
        while (reader.Read())
        {
            items.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Enum.Parse<OrderStatus>(reader.GetString(2)), reader.GetInt64(3),
                ParseTimestamp(reader.GetString(4)), reader.GetInt64(5)));
        }
        return new(items, query.Page, query.PageSize, total);
    }

    public OrderResponse ChangeStatus(Guid id, OrderStatus target, long expectedVersion, CancellationToken cancellationToken)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        cancellationToken.ThrowIfCancellationRequested();
        var current = ReadOrder(connection, transaction, id);
        if (current.Version != expectedVersion)
            throw new ApiException(409, "version_conflict", "Order sudah berubah. Ambil ulang detail order sebelum mencoba lagi.");
        if (!CanTransition(current.Status, target))
            throw new ApiException(422, "invalid_status_transition", $"Status {current.Status} tidak dapat diubah menjadi {target}.");

        var now = DateTimeOffset.UtcNow;
        using (var update = Command(connection, transaction, """
            UPDATE Orders SET Status = $target, Version = Version + 1, UpdatedAt = $now
            WHERE Id = $id AND Version = $version AND Status = $previous;
            """, ("$target", target.ToString()), ("$now", Timestamp(now)), ("$id", id.ToString()),
            ("$version", expectedVersion), ("$previous", current.Status.ToString())))
        {
            if (update.ExecuteNonQuery() != 1)
                throw new ApiException(409, "version_conflict", "Order sudah diubah oleh request lain.");
        }

        if (target == OrderStatus.Cancelled)
        {
            foreach (var item in current.Items)
            {
                using var restore = Command(connection, transaction, """
                    UPDATE Products SET StockQuantity = StockQuantity + $qty WHERE Id = $id;
                    """, ("$qty", item.Quantity), ("$id", item.ProductId));
                if (restore.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("An order references a missing product.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        logger.LogInformation("Changed order {OrderId} from {PreviousStatus} to {Status}, version {Version}",
            id, current.Status, target, current.Version + 1);
        return current with { Status = target, Version = current.Version + 1, UpdatedAt = now };
    }

    public IReadOnlyList<ProductResponse> Products()
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT Id, Name, StockQuantity, Price FROM Products ORDER BY Id;");
        using var reader = command.ExecuteReader();
        var products = new List<ProductResponse>();
        while (reader.Read())
            products.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3)));
        return products;
    }

    public static bool CanTransition(OrderStatus from, OrderStatus to) => (from, to) switch
    {
        (OrderStatus.Pending, OrderStatus.Confirmed or OrderStatus.Cancelled) => true,
        (OrderStatus.Confirmed, OrderStatus.Shipped or OrderStatus.Cancelled) => true,
        (OrderStatus.Shipped, OrderStatus.Delivered) => true,
        _ => false
    };

    private static OrderResponse ReadOrder(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        Guid customerId;
        string address;
        OrderStatus status;
        long version;
        DateTimeOffset createdAt, updatedAt;
        using (var command = Command(connection, transaction,
            "SELECT CustomerId, ShippingAddress, Status, Version, CreatedAt, UpdatedAt FROM Orders WHERE Id = $id;",
            ("$id", id.ToString())))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new ApiException(404, "order_not_found", "Order tidak ditemukan.");
            customerId = Guid.Parse(reader.GetString(0));
            address = reader.GetString(1);
            status = Enum.Parse<OrderStatus>(reader.GetString(2));
            version = reader.GetInt64(3);
            createdAt = ParseTimestamp(reader.GetString(4));
            updatedAt = ParseTimestamp(reader.GetString(5));
        }

        using var itemCommand = Command(connection, transaction, """
            SELECT ProductId, ProductName, Quantity, UnitPrice FROM OrderItems WHERE OrderId = $id ORDER BY ProductId;
            """, ("$id", id.ToString()));
        using var itemReader = itemCommand.ExecuteReader();
        var items = new List<OrderItemResponse>();
        while (itemReader.Read())
            items.Add(new(itemReader.GetInt32(0), itemReader.GetString(1), itemReader.GetInt32(2), itemReader.GetInt64(3)));
        return new(id, customerId, address, status, version, createdAt, updatedAt, items);
    }

    private static NormalizedOrder Normalize(CreateOrderRequest request)
    {
        if (request.CustomerId == Guid.Empty)
            throw new ApiException(400, "invalid_customer", "customerId wajib diisi dengan GUID yang valid.");
        if (string.IsNullOrWhiteSpace(request.ShippingAddress))
            throw new ApiException(400, "invalid_address", "shippingAddress wajib diisi.");
        if (request.Items.Any(item => item is null))
            throw new ApiException(400, "invalid_items", "Item tidak boleh null.");

        var items = request.Items.Select(item => item!).GroupBy(item => item.ProductId)
            .Select(group => new NormalizedItem(group.Key, group.Sum(item => item.Quantity)))
            .OrderBy(item => item.ProductId).ToArray();
        if (items.Any(item => item.Quantity > 10000))
            throw new ApiException(400, "invalid_quantity", "Total quantity per produk maksimal 10000.");
        return new(request.CustomerId, request.ShippingAddress.Trim(), items);
    }

    private static void ValidateKey(string key)
    {
        if (key.Length is < 1 or > 128 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
            throw new ApiException(400, "invalid_idempotency_key", "Header Idempotency-Key wajib diisi, maksimal 128 karakter (huruf, angka, -, _, ., :).");
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private sealed record NormalizedOrder(Guid CustomerId, string ShippingAddress, NormalizedItem[] Items);
    private sealed record NormalizedItem(int ProductId, int Quantity);
}
