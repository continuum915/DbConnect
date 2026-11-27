CREATE TABLE vendors (
    id integer NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name varchar(200) NOT NULL,
    contact_email varchar(320)
);

CREATE TABLE products (
    id integer NOT NULL AUTO_INCREMENT PRIMARY KEY,
    vendor_id integer NOT NULL,
    name varchar(200) NOT NULL,
    description text,
    price decimal(10, 2) NOT NULL,
    stock_quantity integer NOT NULL,
    CONSTRAINT fk_products_vendors FOREIGN KEY (vendor_id) REFERENCES vendors(id)
);

CREATE TABLE customers (
    id integer NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name varchar(200) NOT NULL,
    email varchar(320) NOT NULL
);

CREATE TABLE orders (
    id integer NOT NULL AUTO_INCREMENT PRIMARY KEY,
    customer_id integer NOT NULL,
    order_date datetime NOT NULL,
    total_amount decimal(10, 2) NOT NULL,
    status varchar(50) NOT NULL,
    CONSTRAINT fk_orders_customers FOREIGN KEY (customer_id) REFERENCES customers(id)
);

CREATE TABLE order_items (
    id integer NOT NULL AUTO_INCREMENT PRIMARY KEY,
    order_id integer NOT NULL,
    product_id integer NOT NULL,
    quantity integer NOT NULL,
    unit_price decimal(10, 2) NOT NULL,
    CONSTRAINT fk_order_items_orders FOREIGN KEY (order_id) REFERENCES orders(id),
    CONSTRAINT fk_order_items_products FOREIGN KEY (product_id) REFERENCES products(id)
);

INSERT INTO vendors (id, name, contact_email) VALUES
    (1, 'TechGadgets Inc.', 'contact@techgadgets.com'),
    (2, 'GreenLife Organics', 'hello@greenlife.com');

INSERT INTO products (id, vendor_id, name, description, price, stock_quantity) VALUES
    (1, 1, 'Quantum Keyboard', 'Mechanical keyboard with quantum switches.', 149.99, 50),
    (2, 1, 'HyperMouse Pro', 'Wireless gaming mouse with ultrafast response.', 89.99, 120),
    (3, 2, 'Organic Bamboo Towel', 'Eco-friendly soft bamboo bath towel.', 24.50, 200),
    (4, 2, 'Reusable Steel Straws', 'Set of 4 stainless steel straws with brush.', 9.99, 500);

INSERT INTO customers (id, name, email) VALUES
    (1, 'Alice Smith', 'alice.smith@example.com'),
    (2, 'Bob Johnson', 'bob.j@example.com');

INSERT INTO orders (id, customer_id, order_date, total_amount, status) VALUES
    (1, 1, '2026-03-15 10:30:00', 174.49, 'Shipped'),
    (2, 2, '2026-03-18 14:20:00', 89.99, 'Processing');

INSERT INTO order_items (id, order_id, product_id, quantity, unit_price) VALUES
    (1, 1, 1, 1, 149.99),
    (2, 1, 3, 1, 24.50),
    (3, 2, 2, 1, 89.99);

CREATE INDEX ix_products_vendor_id ON products(vendor_id);
CREATE INDEX ix_orders_customer_id ON orders(customer_id);
CREATE INDEX ix_order_items_order_id ON order_items(order_id);
CREATE INDEX ix_order_items_product_id ON order_items(product_id);

-- R0-005 fixture: nullable, binary, composite-key, view, and routine support.
CREATE TABLE rain_fixture_parent (
    id integer NOT NULL AUTO_INCREMENT PRIMARY KEY,
    code varchar(64) NOT NULL UNIQUE
);

CREATE TABLE rain_fixture_child (
    parent_id integer NOT NULL,
    ordinal integer NOT NULL,
    optional_text varchar(255) NULL,
    payload blob NULL,
    PRIMARY KEY (parent_id, ordinal),
    CONSTRAINT fk_fixture_child_parent FOREIGN KEY (parent_id) REFERENCES rain_fixture_parent(id)
);

INSERT INTO rain_fixture_parent (id, code) VALUES (1, 'mysql-parent');
INSERT INTO rain_fixture_child (parent_id, ordinal, optional_text, payload)
VALUES (1, 1, NULL, UNHEX('00FF10'));

CREATE VIEW rain_fixture_child_view AS
SELECT parent_id, ordinal, optional_text, payload
FROM rain_fixture_child;

DELIMITER //
CREATE FUNCTION rain_fixture_add(a integer, b integer)
RETURNS integer DETERMINISTIC
RETURN a + b//
DELIMITER ;
