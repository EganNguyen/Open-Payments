select s.psp_account_id::text || ':' || s.file_id as sample_id,
       0::bigint as impact_minor
from settlement_lines s
where s.received_at < now() - interval '24 hours';
