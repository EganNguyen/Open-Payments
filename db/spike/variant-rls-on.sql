-- The tenant backstop on the tables the sale path touches. The application sets
-- app.merchant_id in each transaction, which works with transaction pooling.
do $$
declare t text;
begin
  foreach t in array array['payments','payment_attempts','idempotency_keys','outbox','ledger_transactions','ledger_entries'] loop
    execute format('alter table %I enable row level security', t);
    execute format('drop policy if exists spike_tenant on %I', t);
    execute format($p$create policy spike_tenant on %I using (merchant_id = current_setting('app.merchant_id', true)::uuid)$p$, t);
  end loop;
end $$;
