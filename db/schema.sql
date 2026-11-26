CREATE TABLE IF NOT EXISTS Products (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL CHECK (length(Name) BETWEEN 1 AND 200),
    StockQuantity INTEGER NOT NULL CHECK (StockQuantity BETWEEN 0 AND 2147483647),
    Price INTEGER NOT NULL CHECK (Price BETWEEN 0 AND 1000000000)
);

CREATE TABLE IF NOT EXISTS Orders (
    Id TEXT PRIMARY KEY NOT NULL,
    CustomerId TEXT NOT NULL,
    ShippingAddress TEXT NOT NULL CHECK (length(ShippingAddress) BETWEEN 1 AND 500),
    Status TEXT NOT NULL CHECK (Status IN ('Pending','Confirmed','Shipped','Delivered','Cancelled')),
    Version INTEGER NOT NULL CHECK (Version > 0),
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS OrderItems (
    OrderId TEXT NOT NULL REFERENCES Orders(Id),
    ProductId INTEGER NOT NULL REFERENCES Products(Id),
    ProductName TEXT NOT NULL,
    Quantity INTEGER NOT NULL CHECK (Quantity BETWEEN 1 AND 10000),
    UnitPrice INTEGER NOT NULL CHECK (UnitPrice BETWEEN 0 AND 1000000000),
    PRIMARY KEY (OrderId, ProductId)
);

CREATE TABLE IF NOT EXISTS IdempotencyRequests (
    Key TEXT PRIMARY KEY NOT NULL CHECK (length(Key) BETWEEN 1 AND 128),
    RequestHash TEXT NOT NULL,
    OrderId TEXT NOT NULL UNIQUE REFERENCES Orders(Id),
    ResponseJson TEXT NOT NULL,
    CreatedAt TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Orders_CreatedAt ON Orders(CreatedAt DESC, Id DESC);
CREATE INDEX IF NOT EXISTS IX_Orders_Customer_CreatedAt ON Orders(CustomerId, CreatedAt DESC, Id DESC);
CREATE INDEX IF NOT EXISTS IX_Orders_Status_CreatedAt ON Orders(Status, CreatedAt DESC, Id DESC);

INSERT OR IGNORE INTO Products (Id, Name, StockQuantity, Price) VALUES
    (1, 'Product X', 15, 75000),
    (2, 'Product Y', 25, 50000),
    (3, 'Product Z', 10, 125000);

PRAGMA user_version = 1;
