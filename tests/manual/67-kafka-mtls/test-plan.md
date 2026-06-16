# Manual Test Plan: Kafka mTLS / client-cert config (#681)

**STATUS: NEEDS-RERUN**

## Prerequisites

- Docker with a Kafka/Redpanda image that supports mTLS
- `openssl` available locally to generate test certificates
- Local Fleans stack started via `dotnet run --project Fleans.Aspire` with `FLEANS_STREAMING_PROVIDER=Kafka`

---

## Test A — OS trust-store WARN fires when SslCaLocation is absent

**Purpose:** Confirm that a silo using `SecurityProtocol = Ssl` with no explicit CA path emits a WARNING at startup (EventId 11100), confirming the OS trust store will be used.

**Setup:** Point Fleans at a Kafka broker with TLS enabled but use the OS trust store (broker cert from a public or system CA).

**Fleans config:**
```json
{
  "Fleans": {
    "Streaming": {
      "Kafka": {
        "SecurityProtocol": "Ssl"
      }
    }
  }
}
```

**Steps:**
1. Start `dotnet run --project Fleans.Aspire` with the above config.
2. Inspect startup logs for a WARNING from `KafkaQueueAdapter`, `KafkaQueueAdapterReceiver`, or `KafkaQueueAdapterFactory` with EventId 11100 and the text "configured without explicit SSL paths".

**Pass:** WARNING with EventId 11100 appears in startup logs; no unhandled exception at startup.

---

## Test B — CA-only mode (server-cert validation against private CA)

**Setup:** Generate a self-signed CA + broker cert using `openssl`, and start Redpanda with TLS enabled. No client cert.

```bash
# Generate CA
openssl req -x509 -newkey rsa:4096 -keyout ca.key -out ca.pem -days 365 -nodes -subj "/CN=TestCA"
# Generate broker key + CSR
openssl req -newkey rsa:2048 -keyout broker.key -out broker.csr -nodes -subj "/CN=localhost"
# Sign broker cert with CA
openssl x509 -req -in broker.csr -CA ca.pem -CAkey ca.key -CAcreateserial -out broker.pem -days 365
```

**Fleans config:**
```json
{
  "Fleans": {
    "Streaming": {
      "Kafka": {
        "SecurityProtocol": "Ssl",
        "SslCaLocation": "/absolute/path/to/ca.pem"
      }
    }
  }
}
```

**Steps:**
1. Start Fleans with the above config.
2. Run a hello-world workflow.
3. Verify the workflow completes.

**Pass:** Workflow completes; no TLS handshake errors in logs; EventId 11100 WARNING does NOT appear (CA path is set).

---

## Test C — Full mTLS (client + server cert)

**Setup:** Extend the CA from Test B with a client cert.

```bash
# Generate client key + CSR
openssl req -newkey rsa:2048 -keyout client.key -out client.csr -nodes -subj "/CN=fleans-client"
# Sign client cert with CA
openssl x509 -req -in client.csr -CA ca.pem -CAkey ca.key -CAcreateserial -out client.pem -days 365
```

Configure Redpanda to require client certs (`require_client_auth: true`).

**Fleans config:**
```json
{
  "Fleans": {
    "Streaming": {
      "Kafka": {
        "SecurityProtocol": "Ssl",
        "SslCaLocation": "/absolute/path/to/ca.pem",
        "SslCertificateLocation": "/absolute/path/to/client.pem",
        "SslKeyLocation": "/absolute/path/to/client.key"
      }
    }
  }
}
```

**Steps:**
1. Start Fleans with the above config.
2. Run a hello-world workflow.
3. Verify the workflow completes.

**Pass:** Workflow completes; no TLS or cert errors in logs.

---

## Test D — Misconfiguration is rejected at startup

**Sub-case D1 — SSL paths with Plaintext protocol:**
Set `SecurityProtocol=Plaintext` and `SslCaLocation=/some/path`. Expect `InvalidOperationException` at startup mentioning "require SecurityProtocol = Ssl or SaslSsl".

**Sub-case D2 — Cert without Key:**
Set `SslCertificateLocation=/path/client.pem` but leave `SslKeyLocation` empty with `SecurityProtocol=Ssl`. Expect `InvalidOperationException` mentioning "must be provided together".

**Sub-case D3 — Password without Key:**
Set `SslKeyPassword=s3cret` but leave `SslKeyLocation` empty with `SecurityProtocol=Ssl`. Expect `InvalidOperationException` mentioning "passphrase needs a key file".

**Steps (each sub-case):**
1. Start Fleans with the misconfiguration.
2. Verify the silo fails fast with a clear `InvalidOperationException` matching the expected message.

**Pass:** Silo startup fails with a descriptive `InvalidOperationException`; no silent misconfiguration.

---

## Test E — Default plaintext deployments unaffected

**Setup:** No SSL properties set (just `Brokers`). Same as before #681.

**Steps:**
1. Start Fleans. No new SSL config needed.
2. Run a hello-world workflow.
3. Verify startup succeeds and workflow runs normally.

**Pass:** Identical behavior to pre-#681 for plaintext Kafka deployments.
