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
