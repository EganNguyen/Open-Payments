FROM alpine:3.22
RUN apk add --no-cache pgbouncer && adduser -S -D -H pgbouncer
USER pgbouncer
ENTRYPOINT ["pgbouncer", "/etc/pgbouncer/pgbouncer.ini"]
