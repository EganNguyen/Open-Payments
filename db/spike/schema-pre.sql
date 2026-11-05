-- Shared tables that come before the partitioned ones. Follows docs/architecture.md.
drop schema if exists public cascade;
create schema public;
grant all on schema public to public;

create table currencies (
  code char(3) primary key, exponent smallint not null, rounding text not null);
insert into currencies values ('USD', 2, 'half_even'), ('JPY', 0, 'half_even'), ('KWD', 3, 'half_even');

create table accounts (
  id             uuid primary key,
  merchant_id    uuid,
  account_type   text not null,
  sub_account    smallint not null default 0 check (sub_account between 0 and 63),
  class          text not null check (class in ('clearing','terminal')),
  class_of_money text not null check (class_of_money in ('real','logical')),
  currency       char(3) not null references currencies(code),
  unique nulls not distinct (merchant_id, account_type, sub_account, currency));

create table api_keys (
  key_hash bytea primary key, merchant_id uuid not null,
  mode text not null check (mode in ('live','test')),
  scopes text[] not null, revoked_at timestamptz);

create table psp_accounts (
  id uuid primary key, merchant_id uuid not null, psp text not null,
  mode text not null check (mode in ('live','test')),
  credential_ref text not null, rate_limit_per_s int not null, disabled_at timestamptz);

-- Finds the UUIDv7 that starts at a moment, for range partition bounds.
create function spike_uuid_floor(ts timestamptz) returns uuid language sql immutable as $$
  select (lpad(to_hex((extract(epoch from ts) * 1000)::bigint), 12, '0') || '00000000000000000000')::uuid
$$;
