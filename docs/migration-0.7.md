# Migrating to Portia 0.7

Existing request-scenario signatures remain supported. Command, query and stream scenarios now
accept a caller cancellation token in `When(request, cancellationToken)`. To sample a finite
prefix of a long-running stream use `When(request, maxItems, cancellationToken)`; the limit must
be positive, early completion is accepted, and `ExpectItems` compares the collected prefix.
Each operation owns an asynchronous scope and disposes its enumerator on success, early stop,
cancellation or failure. Retain the returned immutable expectations when adding assertions.

Manual processors run bounded passes. Resume from returned checkpoints until one does not advance;
commit batch size is distinct from the source-record pass limit. See the compiled manual example
linked from [testing](testing.md). No existing processor signature changes.

Delivery process spans include acknowledgment, terminal disposition and asynchronous scope cleanup
so runner outcome/fault logs carry the process trace and span IDs. Execute span timing remains tied
to the request pipeline. Review duration dashboards if they previously assumed the process ended
at handler completion. Span counts and bounded dimensions remain unchanged.

Use the verified Prometheus names and queries in [observability](observability.md). Histogram names
include seconds/count/sum/bucket suffixes after underscore translation; active instruments are gauges.
Processor lag is event age observed at commit, not a stalled-backlog gauge. The pinned exporter recipe
uses cumulative metrics through the full HTTP/protobuf metrics endpoint.
