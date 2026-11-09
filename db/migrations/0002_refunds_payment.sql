-- D50: Sql.LoadRefund filters refunds on payment_id, which had no index, so each lookup scanned
-- the table. Plain create index, because scripts/migrate.sh runs each file in one transaction.
create index refunds_payment on refunds (payment_id);
