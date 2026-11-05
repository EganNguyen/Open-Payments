-- Prefills one batch of finished sales, aged over the last 30 days, so runs don't measure
-- an empty database. Needs -v merchants=N -v batch=N. Repeat it for a larger prefill.
begin;
set local synchronous_commit = local;

create temp table b on commit drop as
select v.pid, uuidv7(v.age) as aid, uuidv7(v.age) as lid, uuidv7(v.age) as oid,
       now() + v.age as at, m,
       md5('m' || m)::uuid as mid,
       1000000 as amount
  from (select uuidv7(age) as pid, age, floor(power(random(), 3) * :merchants)::int + 1 as m
          from (select -(random() * interval '30 days') as age from generate_series(1, :batch)) a) v;

insert into payments (id, merchant_id, state, amount_minor, currency, captured_minor, attempt_count,
                      event_seq, expires_at, created_at, updated_at)
select pid, mid, 'CAPTURED', amount, 'USD', amount, 1, 1, at + interval '1 day', at, at from b;

insert into payment_attempts (attempt_id, payment_id, merchant_id, operation, psp, psp_account_id,
                              state, method_ref, method_type, psp_reference, created_at)
select aid, pid, mid, 'sale', 'mock', md5('a' || m)::uuid, 'SUCCEEDED', 'tok', 'card', aid::text, at from b;

insert into idempotency_keys (merchant_id, key, request_hash, payment_id, attempt_id, response_code,
                              response_body, created_at, resolved_at)
select mid, pid::text, '\x01', pid, aid, 200, '{"state":"CAPTURED"}', at, at from b;

insert into ledger_transactions (id, merchant_id, payment_id, kind, source_event_id, effective_at, created_at)
select lid, mid, pid, 'capture', lid, at, at from b;

insert into ledger_entries (txn_id, entry_no, merchant_id, account_id, subject_id, direction, amount_minor, currency, created_at)
select lid, e.no::smallint, mid, md5(e.type || ':' || m)::uuid, pid, e.dir, amount, 'USD', at
  from b, (values (1, 'psp_receivable', 'D'), (2, 'merchant_payable', 'C')) e(no, type, dir);

insert into outbox (id, merchant_id, aggregate_id, event_type, sequence, schema_version, payload, created_at, published_at)
select oid, mid, pid, 'payment.captured', 1, 1, '{}', at, at from b;

insert into psp_events (psp_account_id, psp_event_id, payload, received_at, processed_at)
select md5('a' || m)::uuid, oid::text, '{}', at, at from b;
commit;
