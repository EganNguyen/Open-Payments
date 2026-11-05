-- Merchants and their accounts. Run once per schema. Needs -v merchants=N.
-- IDs are derived from the merchant number, so workloads can compute them.
insert into psp_accounts (id, merchant_id, psp, mode, credential_ref, rate_limit_per_s)
select md5('a' || n)::uuid, md5('m' || n)::uuid, 'mock', 'live', 'spike', 1000000
  from generate_series(1, :merchants) n;

insert into accounts (id, merchant_id, account_type, class, class_of_money, currency)
select md5(t.type || ':' || n)::uuid, md5('m' || n)::uuid, t.type, t.class, t.money, 'USD'
  from generate_series(1, :merchants) n,
       (values ('psp_receivable',     'clearing', 'real'),
               ('merchant_payable',   'terminal', 'real'),
               ('fee_revenue',        'terminal', 'real'),
               ('refund_reserved',    'clearing', 'real'),
               ('psp_refund_payable', 'clearing', 'real'),
               ('auth_hold',          'clearing', 'logical'),
               ('auth_hold_contra',   'clearing', 'logical')) t(type, class, money);
