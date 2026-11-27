IF DB_ID(N'rain_ecommerce') IS NULL
BEGIN
    CREATE DATABASE rain_ecommerce;
END;
GO

USE rain_ecommerce;
GO

IF OBJECT_ID(N'dbo.Vendors', N'U') IS NOT NULL
BEGIN
    PRINT 'rain_ecommerce is already seeded. Skipping seed script.';
    RETURN;
END;
GO

CREATE TABLE dbo.Vendors (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Vendors PRIMARY KEY,
    Name nvarchar(200) NOT NULL,
    ContactEmail nvarchar(320) NULL
);

CREATE TABLE dbo.Products (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    VendorId int NOT NULL,
    Name nvarchar(200) NOT NULL,
    Description nvarchar(max) NULL,
    Price decimal(10, 2) NOT NULL,
    StockQuantity int NOT NULL,
    CONSTRAINT FK_Products_Vendors FOREIGN KEY (VendorId) REFERENCES dbo.Vendors(Id)
);

CREATE TABLE dbo.Customers (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Name nvarchar(200) NOT NULL,
    Email nvarchar(320) NOT NULL
);

CREATE TABLE dbo.Orders (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId int NOT NULL,
    OrderDate datetimeoffset NOT NULL,
    TotalAmount decimal(10, 2) NOT NULL,
    Status nvarchar(50) NOT NULL,
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers(Id)
);

CREATE TABLE dbo.OrderItems (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderItems PRIMARY KEY,
    OrderId int NOT NULL,
    ProductId int NOT NULL,
    Quantity int NOT NULL,
    UnitPrice decimal(10, 2) NOT NULL,
    CONSTRAINT FK_OrderItems_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id),
    CONSTRAINT FK_OrderItems_Products FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id)
);
GO

SET IDENTITY_INSERT dbo.Vendors ON;
INSERT INTO dbo.Vendors (Id, Name, ContactEmail) VALUES
    (1, N'TechGadgets Inc.', N'contact@techgadgets.com'),
    (2, N'GreenLife Organics', N'hello@greenlife.com');
SET IDENTITY_INSERT dbo.Vendors OFF;

SET IDENTITY_INSERT dbo.Products ON;
INSERT INTO dbo.Products (Id, VendorId, Name, Description, Price, StockQuantity) VALUES
    (1, 1, N'Quantum Keyboard', N'Mechanical keyboard with quantum switches.', 149.99, 50),
    (2, 1, N'HyperMouse Pro', N'Wireless gaming mouse with ultrafast response.', 89.99, 120),
    (3, 2, N'Organic Bamboo Towel', N'Eco-friendly soft bamboo bath towel.', 24.50, 200),
    (4, 2, N'Reusable Steel Straws', N'Set of 4 stainless steel straws with brush.', 9.99, 500);
SET IDENTITY_INSERT dbo.Products OFF;

SET IDENTITY_INSERT dbo.Customers ON;
INSERT INTO dbo.Customers (Id, Name, Email) VALUES
    (1, N'Alice Smith', N'alice.smith@example.com'),
    (2, N'Bob Johnson', N'bob.j@example.com');
SET IDENTITY_INSERT dbo.Customers OFF;

SET IDENTITY_INSERT dbo.Orders ON;
INSERT INTO dbo.Orders (Id, CustomerId, OrderDate, TotalAmount, Status) VALUES
    (1, 1, '2026-03-15T10:30:00Z', 174.49, N'Shipped'),
    (2, 2, '2026-03-18T14:20:00Z', 89.99, N'Processing');
SET IDENTITY_INSERT dbo.Orders OFF;

SET IDENTITY_INSERT dbo.OrderItems ON;
INSERT INTO dbo.OrderItems (Id, OrderId, ProductId, Quantity, UnitPrice) VALUES
    (1, 1, 1, 1, 149.99),
    (2, 1, 3, 1, 24.50),
    (3, 2, 2, 1, 89.99);
SET IDENTITY_INSERT dbo.OrderItems OFF;
GO

CREATE INDEX IX_Products_VendorId ON dbo.Products(VendorId);
CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);
CREATE INDEX IX_OrderItems_OrderId ON dbo.OrderItems(OrderId);
CREATE INDEX IX_OrderItems_ProductId ON dbo.OrderItems(ProductId);
GO

-- R0-005 fixture: nullable, binary, composite-key, view, and routine support.
CREATE TABLE dbo.DbConnectFixtureParent (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_DbConnectFixtureParent PRIMARY KEY,
    Code nvarchar(64) NOT NULL CONSTRAINT UQ_DbConnectFixtureParent_Code UNIQUE
);

CREATE TABLE dbo.DbConnectFixtureChild (
    ParentId int NOT NULL,
    Ordinal int NOT NULL,
    OptionalText nvarchar(255) NULL,
    Payload varbinary(max) NULL,
    CONSTRAINT PK_DbConnectFixtureChild PRIMARY KEY (ParentId, Ordinal),
    CONSTRAINT FK_DbConnectFixtureChild_Parent FOREIGN KEY (ParentId) REFERENCES dbo.DbConnectFixtureParent(Id)
);

INSERT INTO dbo.DbConnectFixtureParent (Code) VALUES (N'sqlserver-parent');
INSERT INTO dbo.DbConnectFixtureChild (ParentId, Ordinal, OptionalText, Payload)
SELECT Id, 1, NULL, 0x00FF10 FROM dbo.DbConnectFixtureParent WHERE Code = N'sqlserver-parent';

GO

CREATE VIEW dbo.DbConnectFixtureChildView AS
SELECT ParentId, Ordinal, OptionalText, Payload
FROM dbo.DbConnectFixtureChild;
GO

CREATE FUNCTION dbo.DbConnectFixtureAdd(@a int, @b int)
RETURNS int
AS
BEGIN
    RETURN @a + @b;
END;
GO
