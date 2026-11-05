do $$
declare t text;
begin
  foreach t in array array['payments','payment_attempts','idempotency_keys','outbox','ledger_transactions','ledger_entries'] loop
    execute format('drop policy if exists spike_tenant on %I', t);
    execute format('alter table %I disable row level security', t);
  end loop;
end $$;
