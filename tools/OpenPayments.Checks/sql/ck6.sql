-- CK6 is a trend check: the runner samples Prometheus over --from..--to in Ck6Trend.cs.
-- This query is the database side of outbox age, used when a run has no Prometheus yet.
select id::text as sample_id, 0::bigint as impact_minor
from outbox
where published_at is null
  and created_at < now() - interval '60 seconds';
