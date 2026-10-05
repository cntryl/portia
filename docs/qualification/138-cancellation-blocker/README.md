# SDK 2.2.0 stdio cancellation limitation

Ordinary client token cancellation can finish locally without sending
`notifications/cancelled`. The lifecycle tests reproduce this on an SDK-only
stdio host for tools, resources, and prompts under both supported revisions.
Explicit notifications with the matching request ID cancel the handler and
allow its asynchronous scope to dispose.

The nine affected test rows are temporarily skipped pending a verified fix for
[upstream issue #1365](https://github.com/modelcontextprotocol/csharp-sdk/issues/1365).
HTTP cancellation, explicit notification controls, deadlines, and scope cleanup
remain tested. These skips do not establish ordinary stdio cancellation support.

The small `race-reproduction` program isolates the SDK's callback-registration,
`WaitAsync`, and registration-disposal pattern. Raw investigation output belongs
in CI artifacts rather than source control.
