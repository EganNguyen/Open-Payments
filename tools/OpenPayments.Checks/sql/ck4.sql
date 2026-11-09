select p.id::text as sample_id, 0::bigint as impact_minor
from payments p
where p.event_seq > 0
  and not exists (
    select 1 from outbox o
    where o.aggregate_id = p.id and o.sequence = p.event_seq)
union all
select o.id::text, 0::bigint
from outbox o
where not exists (select 1 from payments p where p.id = o.aggregate_id)
union all
select p.id::text, 0::bigint
from payments p
join (select aggregate_id, max(sequence) as max_seq
      from outbox group by aggregate_id) o on o.aggregate_id = p.id
where p.event_seq < o.max_seq;
