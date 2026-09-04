CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;

CREATE TABLE organizations (id uuid PRIMARY KEY, name text NOT NULL);
CREATE TABLE stores (id uuid PRIMARY KEY, organization_id uuid NOT NULL REFERENCES organizations(id), name text NOT NULL);
CREATE TABLE users (id uuid PRIMARY KEY, organization_id uuid NOT NULL REFERENCES organizations(id), name text NOT NULL);
CREATE TABLE flyway_schema_history (installed_rank integer PRIMARY KEY, version varchar(50), success boolean NOT NULL);
CREATE TABLE auth_sessions (id uuid PRIMARY KEY, revoked_at timestamptz);
CREATE TABLE cmed_import_runs (id uuid PRIMARY KEY, imported_by_user_id uuid REFERENCES users(id), published_at date);
CREATE TABLE cmed_products (id uuid PRIMARY KEY, import_run_id uuid NOT NULL REFERENCES cmed_import_runs(id));
CREATE TABLE label_templates (id uuid PRIMARY KEY, store_id uuid REFERENCES stores(id), is_system boolean NOT NULL, name text NOT NULL);
CREATE TABLE product_media (id uuid PRIMARY KEY, store_id uuid NOT NULL REFERENCES stores(id), url text NOT NULL, mime_type text, storage_key text);
CREATE TABLE sales (id uuid PRIMARY KEY, store_id uuid NOT NULL REFERENCES stores(id), created_at timestamptz NOT NULL, total_amount numeric(19,2) NOT NULL);
CREATE TABLE sale_payments (id uuid PRIMARY KEY, sale_id uuid NOT NULL REFERENCES sales(id), amount numeric(19,2) NOT NULL);
CREATE TABLE store_inventories (store_id uuid NOT NULL REFERENCES stores(id), product_id uuid NOT NULL, quantity numeric NOT NULL, reserved_quantity numeric NOT NULL, PRIMARY KEY(store_id,product_id));
CREATE TABLE inventory_lots (id uuid PRIMARY KEY, store_id uuid NOT NULL REFERENCES stores(id), product_id uuid NOT NULL, lot_number text NOT NULL, available_quantity numeric NOT NULL, reserved_quantity numeric NOT NULL);
CREATE TABLE inventory_transfers (id uuid PRIMARY KEY, origin_store_id uuid NOT NULL REFERENCES stores(id), destination_store_id uuid NOT NULL REFERENCES stores(id));
CREATE TABLE inventory_transfer_lot_items (id uuid PRIMARY KEY, source_inventory_lot_id uuid REFERENCES inventory_lots(id));
CREATE TABLE cash_movements (id uuid PRIMARY KEY, store_id uuid NOT NULL REFERENCES stores(id), created_at timestamptz NOT NULL, type text NOT NULL, amount numeric(19,2) NOT NULL);
CREATE TABLE purchase_invoices (id uuid PRIMARY KEY, store_id uuid NOT NULL REFERENCES stores(id), total_invoice numeric(19,2) NOT NULL);
CREATE TABLE stocktakes (id uuid PRIMARY KEY, store_id uuid NOT NULL REFERENCES stores(id), status text NOT NULL);

INSERT INTO flyway_schema_history VALUES (1, '54', true);
INSERT INTO organizations VALUES
    ('10000000-0000-0000-0000-000000000001', 'Organização A'),
    ('10000000-0000-0000-0000-000000000002', 'Organização B');
INSERT INTO stores VALUES
    ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001', 'Loja A'),
    ('20000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000002', 'Loja B');
INSERT INTO users VALUES
    ('30000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001', 'Usuário A'),
    ('30000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000002', 'Usuário B');
INSERT INTO cmed_import_runs VALUES ('40000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000002', current_date);
INSERT INTO cmed_products VALUES ('41000000-0000-0000-0000-000000000001', '40000000-0000-0000-0000-000000000001');
INSERT INTO label_templates VALUES
    ('50000000-0000-0000-0000-000000000001', NULL, true, 'Sistema'),
    ('50000000-0000-0000-0000-000000000002', '20000000-0000-0000-0000-000000000001', false, 'Loja A'),
    ('50000000-0000-0000-0000-000000000003', '20000000-0000-0000-0000-000000000002', false, 'Loja B');
INSERT INTO sales VALUES ('60000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', '2026-01-02T12:00:00Z', 42.50);
INSERT INTO sale_payments VALUES ('61000000-0000-0000-0000-000000000001', '60000000-0000-0000-0000-000000000001', 42.50);
INSERT INTO store_inventories VALUES ('20000000-0000-0000-0000-000000000001', '70000000-0000-0000-0000-000000000001', 10, 2);
INSERT INTO inventory_lots VALUES ('71000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', '70000000-0000-0000-0000-000000000001', 'L1', 8, 2);
INSERT INTO cash_movements VALUES ('80000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', '2026-01-02T12:00:00Z', 'SALE', 42.50);
INSERT INTO purchase_invoices VALUES ('81000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', 12.25);
INSERT INTO stocktakes VALUES ('82000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000001', 'COMPLETED');
