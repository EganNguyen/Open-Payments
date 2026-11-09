-- Local-only login users for the console, members of the roles 0003_console.sql creates.
-- Passwords are local stack values, like the rest of compose.yaml.
do $$
begin
  if not exists (select from pg_roles where rolname = 'console_read') then
    create role console_read login password 'op_local_only' in role op_console_read;
  end if;
  if not exists (select from pg_roles where rolname = 'console_write') then
    create role console_write login password 'op_local_only' in role op_console;
  end if;
end $$;
-- The first admin, matching the Keycloak realm's local user.
insert into console.staff (id, oidc_subject, email, role)
values ('00000000-0000-7000-8000-00000000ad01', '9b2d6c4e-0d6f-4f5e-9a4f-6f1c2a3b4c01', 'admin@console.local', 'admin')
on conflict (oidc_subject) do nothing;
