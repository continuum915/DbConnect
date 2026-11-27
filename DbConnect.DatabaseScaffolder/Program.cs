using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DbConnect.DatabaseScaffolder;

/// <summary>
/// Quick tool for db scaffolding for test purposes.
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        string dbFile = "rain_ecommerce_template.db";
        string connString = $"Data Source={dbFile}";

        Console.WriteLine($"Scaffolding database at {dbFile}...");

        if (File.Exists(dbFile))
        {
            File.Delete(dbFile);
            Console.WriteLine("Deleted existing database file.");
        }

        using var conn = new SqliteConnection(connString);
        conn.Open();

        using var transaction = conn.BeginTransaction();

        try
        {
            // Create Table: Vendors
            ExecuteCmd(conn, transaction, @"
                CREATE TABLE Vendors (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ContactEmail TEXT
                );
            ");

            // Create Table: Products
            ExecuteCmd(conn, transaction, @"
                CREATE TABLE Products (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    VendorId INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Description TEXT,
                    Price REAL NOT NULL,
                    StockQuantity INTEGER NOT NULL,
                    FOREIGN KEY(VendorId) REFERENCES Vendors(Id)
                );
            ");

            // Create Table: Customers
            ExecuteCmd(conn, transaction, @"
                CREATE TABLE Customers (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Email TEXT NOT NULL
                );
            ");

            // Create Table: Orders
            ExecuteCmd(conn, transaction, @"
                CREATE TABLE Orders (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    CustomerId INTEGER NOT NULL,
                    OrderDate TEXT NOT NULL,
                    TotalAmount REAL NOT NULL,
                    Status TEXT NOT NULL,
                    FOREIGN KEY(CustomerId) REFERENCES Customers(Id)
                );
            ");

            // Create Table: OrderItems
            ExecuteCmd(conn, transaction, @"
                CREATE TABLE OrderItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OrderId INTEGER NOT NULL,
                    ProductId INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL,
                    UnitPrice REAL NOT NULL,
                    FOREIGN KEY(OrderId) REFERENCES Orders(Id),
                    FOREIGN KEY(ProductId) REFERENCES Products(Id)
                );
            ");

            // Seed Data: Vendors
            ExecuteCmd(conn, transaction, @"
                INSERT INTO Vendors (Name, ContactEmail) VALUES 
                ('TechGadgets Inc.', 'contact@techgadgets.com'),
                ('GreenLife Organics', 'hello@greenlife.com');
            ");

            // Seed Data: Products
            ExecuteCmd(conn, transaction, @"
                INSERT INTO Products (VendorId, Name, Description, Price, StockQuantity) VALUES 
                (1, 'Quantum Keyboard', 'Mechanical keyboard with quantum switches.', 149.99, 50),
                (1, 'HyperMouse Pro', 'Wireless gaming mouse with ultrafast response.', 89.99, 120),
                (2, 'Organic Bamboo Towel', 'Eco-friendly soft bamboo bath towel.', 24.50, 200),
                (2, 'Reusable Steel Straws', 'Set of 4 stainless steel straws with brush.', 9.99, 500);
            ");

            // Seed Data: Customers
            ExecuteCmd(conn, transaction, @"
                INSERT INTO Customers (Name, Email) VALUES 
                ('Alice Smith', 'alice.smith@example.com'),
                ('Bob Johnson', 'bob.j@example.com');
            ");

            // Seed Data: Orders & Items
            ExecuteCmd(conn, transaction, @"
                INSERT INTO Orders (CustomerId, OrderDate, TotalAmount, Status) VALUES 
                (1, '2026-03-15T10:30:00Z', 174.49, 'Shipped'),
                (2, '2026-03-18T14:20:00Z', 89.99, 'Processing');
            ");

            ExecuteCmd(conn, transaction, @"
                INSERT INTO OrderItems (OrderId, ProductId, Quantity, UnitPrice) VALUES 
                (1, 1, 1, 149.99),
                (1, 3, 1, 24.50),
                (2, 2, 1, 89.99);
            ");

            transaction.Commit();
            Console.WriteLine("Database created and seeded successfully.");
            Console.WriteLine($"Path: {Path.GetFullPath(dbFile)}");
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            Console.WriteLine($"Error scaffolding database: {ex.Message}");
        }
    }

    static void ExecuteCmd(SqliteConnection conn, SqliteTransaction transaction, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
