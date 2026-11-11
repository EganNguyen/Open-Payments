-- CK8 timeliness (D18, D63): a file that has not finished, or a line that is still unresolved,
-- is a settlement input that arrived outside its window. Matched, duplicate and ignored lines
-- are not violations: their treatment is complete. One row per offending input.
select 'file:' || f.psp_account_id::text || ':' || f.file_id as sample_id,
       0::bigint as impact_minor
from settlement_files f
where f.state <> 'done'
  and f.received_at < now() - interval '24 hours'
union all
select s.psp_account_id::text || ':' || s.file_id || ':' || s.line_no::text as sample_id,
       case when s.state in ('UNMATCHED', 'MISMATCH') then s.amount_minor else 0 end as impact_minor
from settlement_lines s
where s.state in ('RECEIVED', 'UNMATCHED', 'MISMATCH')
  and s.received_at < now() - interval '24 hours';
