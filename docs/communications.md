# Outbound mail and SMS

`Cntryl.Portia.Communications.Abstractions` defines provider-neutral senders: `IMailSender` and `ISmsSender`.
Application and domain assemblies reference this package without a provider SDK. Hosts add a provider:

| Channel | Provider | Package |
| --- | --- | --- |
| Mail | Amazon SES | `Cntryl.Portia.Communications.Aws` |
| Mail | Azure Communication Services | `Cntryl.Portia.Communications.Azure` |
| SMS | Twilio Programmable Messaging | `Cntryl.Portia.Communications.Twilio` |

A sender is a transport. It makes exactly one provider request per call and never retries. It does not store what
was sent and does not deduplicate. The application records sends in its own aggregates.

## Outcomes

`SendAsync` returns a `SendOutcome`:

- `Accepted`: the provider took the message. This is not delivery. `ProviderReference` is the provider's message ID.
- `Rejected`: the provider definitively refused it. `RejectionReason` is provider-neutral, for example `OptedOut`,
  `InvalidRecipient` or `SenderNotAuthorized`.
- `Unknown`: the request may have reached the provider, but the result is uncertain (a timeout, a server error, a
  cancellation after dispatch, or a connection reset). Do not send again. The provider write is not idempotent.

It throws when the message certainly did not reach the provider:

- `SendThrottledException`: the provider throttled the send. Send again later, at the caller's pace.
- `InvalidOperationException`: the sender is misconfigured (credentials, sender identity, a missing resource).
- A failure that proves no request was sent, such as failed DNS resolution (the provider's own exception).
  A general connection error does not prove this: the connection may have broken after the provider took the message.

Recipients and content never appear in exceptions, `ToString()` output or outcomes. Provider error text is not
repeated because it can name recipients.

## Tracking a send

Reactors run at least once, so the aggregate that owns the send decides whether to call the sender. A send
identity (`SendId`) names one logical message. Use the identity the aggregate records.

1. The aggregate records the send as pending under its `SendId`.
2. A reactor reads the aggregate. It calls the sender only while the send is pending and has no outcome.
3. The reactor records the outcome with a command: accepted, rejected with its reason, or unknown.
4. A delivery event later settles the send: any event proves the provider took the message.

A crash between steps 2 and 3 leaves the send pending with no outcome, and the provider may have taken it. On
replay, treat that state as unknown instead of sending again, unless a duplicate message is acceptable for that
send. An unknown send needs an explicit resolution: a delivery event, or a business decision to resend or give up.
A throttled send stays pending and can be sent again.

`SendId` is 1–128 ASCII letters, digits, `.`, `_` or `-`. It travels to providers as a tag or URL path segment, so
it must not contain personal data.

## Delivery events

A provider integration hands each verified provider event to the application's `IDeliveryEventReceiver` as a
`DeliveryEvent` with the tenant, channel, `SendId`, kind and time. The receiver usually sends a command to the
aggregate that recorded the send. Providers redeliver events, so handling must be idempotent. If the receiver
throws, the failure reaches the event source and the event is delivered again.

## Amazon SES

```csharp
var ses = new AmazonSimpleEmailServiceV2Client(new AmazonSimpleEmailServiceV2Config { MaxErrorRetry = 0 });
builder.Services.AddSingleton<IAwsMailTenantResolver, ShopMailTenantResolver>();
builder.Services.AddSingleton<IDeliveryEventReceiver, ShopDeliveryEventReceiver>();
builder.Services.AddAwsMail(options => options.UseClient(ses));
```

The client must have `MaxErrorRetry = 0`. The sender refuses a client that retries, because an SDK retry after a
lost response could send a message twice.

`IAwsMailTenantResolver` returns the tenant's from address and, optionally, an SES configuration set. The sender
tags each message with the tenant and `SendId`. To receive delivery events, give the configuration set an event
destination (SNS or EventBridge), deliver it to a queue that only that destination can write to, and pass each
message body to `AwsMailEventIngester.IngestAsync`. The ingester accepts the SES event on its own, inside an SNS
notification, or as an EventBridge event. It returns `false` for records that are not delivery events for mail this
sender sent, so they can be discarded. It does not authenticate the source; do not expose it on a public endpoint.

## Azure Communication Services

```csharp
builder.Services.AddSingleton<IAzureMailTenantResolver, ShopMailTenantResolver>();
builder.Services.AddAzureMail(options => options.UseEndpoint(endpoint, new DefaultAzureCredential()));
```

`UseConnectionString` is the alternative to `UseEndpoint`. Both create the client with retries off.
`IAzureMailTenantResolver` returns the tenant's sender address on a verified domain linked to the resource.

The sender submits the message and does not wait for the service to process it. Azure's documented Event Grid
delivery report identifies a message only by the service's message ID, not by anything the application supplies,
so this provider does not produce `DeliveryEvent`s. `SendOutcome.ProviderReference` is the operation ID the service returned.

## Twilio

```csharp
builder.Services.AddSingleton<ITwilioTenantResolver, ShopTwilioTenantResolver>();
builder.Services.AddSingleton<IDeliveryEventReceiver, ShopDeliveryEventReceiver>();
builder.Services.AddTwilioSms(options =>
    options.UseStatusCallbacks(new Uri("https://api.example.com/twilio/status")));

app.MapTwilioStatusCallbacks("/twilio/status");
```

`ITwilioTenantResolver` returns a `TwilioTenant`: the account or subaccount SID, an API key SID and secret, the
sender (a Messaging Service SID or a from number) and, for status callbacks, the account's auth token. Load the
secrets from the host's secret store. `TwilioTenant.ToString()` redacts them.

A recipient who opted out (Twilio error 21610) is a `Rejected` outcome with `SendRejectionReason.OptedOut`.

`UseStatusCallbacks` takes the public HTTPS address that Twilio reaches the mapped endpoint at. The endpoint
verifies `X-Twilio-Signature` against the tenant's auth token and that public address, and checks the account SID.
A callback that fails verification gets 403 and records nothing. The endpoint allows anonymous requests because the
signature is its authentication. Without `UseStatusCallbacks` the sender requests no callbacks.

## Testing

`AddFakeCommunications()` in `Cntryl.Portia.Testing` registers `FakeMailSender` and `FakeSmsSender`. Resolve a fake
to script outcomes (`Reject`, `ReturnUnknown`, `Throttle`, `Fail`) and to inspect `Submissions`. Feed delivery
events to the application's `IDeliveryEventReceiver` directly. Both fakes reject default tenant and send identities
before recording a submission, matching the production senders.

Test the race where the send is accepted and the outcome is never recorded, and the one where the outcome is
unknown and a delivery event arrives later. Assert that neither sends twice.
