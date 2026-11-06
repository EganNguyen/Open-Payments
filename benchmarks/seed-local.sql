-- Local-only merchant, API key and chart of accounts for the k6 ramp.
-- Token: benchmark-token (never used outside the local compose stack).
-- Usage: psql 'postgres://postgres:op_local_only@127.0.0.1:15432/openpayments' -f benchmarks/seed-local.sql
insert into psp_accounts (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s)
values (
  '22222222-2222-7222-8222-222222222222',
  '11111111-1111-7111-8111-111111111111',
  'mock', 'live', 'local-benchmark', 10000)
on conflict (id) do nothing;

insert into api_keys (key_hash, merchant_id, mode, scopes)
values (
  decode('3100adac55ea5e3e4cbcfb5bd88808d3d0b8152d1f261fcf60b84dbc217f8740', 'hex'),
  '11111111-1111-7111-8111-111111111111',
  'live', array['payments:write', 'payments:read'])
on conflict (key_hash) do nothing;

insert into accounts (id, merchant_id, account_type, sub_account, class, class_of_money, currency)
values
  ('33333333-3333-7333-8333-333333333331', '11111111-1111-7111-8111-111111111111', 'auth_hold', 0, 'clearing', 'logical', 'USD'),
  ('33333333-3333-7333-8333-333333333332', '11111111-1111-7111-8111-111111111111', 'auth_hold_contra', 0, 'clearing', 'logical', 'USD'),
  ('33333333-3333-7333-8333-333333333333', '11111111-1111-7111-8111-111111111111', 'psp_receivable', 0, 'terminal', 'real', 'USD'),
  ('33333333-3333-7333-8333-333333333334', '11111111-1111-7111-8111-111111111111', 'merchant_payable', 0, 'terminal', 'real', 'USD'),
  ('33333333-3333-7333-8333-333333333335', '11111111-1111-7111-8111-111111111111', 'refund_reserved', 0, 'clearing', 'real', 'USD'),
  ('33333333-3333-7333-8333-333333333336', '11111111-1111-7111-8111-111111111111', 'psp_refund_payable', 0, 'terminal', 'real', 'USD')
on conflict (merchant_id, account_type, sub_account, currency) do nothing;
