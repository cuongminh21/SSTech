# Partner Integration BFF (.NET 8)

A Backend-for-Frontend microservice that receives transactions from third-party partners, validates them, enriches them via an external verification API, and reliably queues them to RabbitMQ for legacy consumers.

## Architecture

```
POST /api/v1/partner/transactions
        │
        ▼
  Endpoint (Minimal API)  ──► TransactionService (use case orchestration)
                                  │  1. FluentValidation  (pure, no I/O)
                                  │  2. IPartnerVerificationClient  ──► Polly pipeline ──► Mock Partner API
                                  │  3. enrich (partner name) 
                                  ▼  4. ITransactionPublisher       ──► RabbitMQ (durable topic exchange)
                            TransactionResult ──► HTTP status mapping
```

**Why this shape**

| Choice | Rationale |
| --- | --- |
| Minimal APIs + thin endpoint layer | Endpoints only map HTTP ⇄ use case; all logic is testable without a web host. |
| `TransactionResult` result object | Business outcomes (unknown partner, outage) are *expected* flows, not exceptions — the exception handler is reserved for genuine faults. |
| FluentValidation | Validation rules stay pure and are covered by fast unit tests. |
| Typed `HttpClient` + Polly v8 (`Microsoft.Extensions.Http.Resilience`) | Resilience is configuration, not code sprinkled through the client. |
| Interface `ITransactionPublisher` | RabbitMQ is an infrastructure detail; tests use `InMemoryTransactionPublisher`. |
| `IExceptionHandler` + ProblemDetails | One consistent RFC-7807 error shape across the whole API. |
| `TimeProvider` injected | Deterministic timestamps in tests via `FakeTimeProvider`. |

### Resilience strategy

Pipeline order (outermost → innermost): **total timeout (10s) → retry → circuit breaker → per-attempt timeout (2s)**.

- Retries: 3 attempts, exponential backoff with jitter, only on `TimeoutException` / `HttpRequestException` / 5xx / 408.
- `404` is **not** retried — an unknown partner is a definitive answer.
- Circuit breaker: opens for 15s when ≥70% of ≥10 calls in a 30s window fail, so a sustained outage stops hammering the dependency.
- When the pipeline is exhausted, the client throws `PartnerVerificationUnavailableException`, the service converts it into `TransactionOutcome.VerificationUnavailable`, and the caller receives **503 + `Retry-After: 30`**. The incoming request never crashes.

### Reliability of the publish step

Messages are published to a **durable** topic exchange with **persistent** delivery mode and **publisher confirms** (`WaitForConfirmsOrDie`). A failed publish raises `MessagePublishException` → 503, so the partner can safely retry using the same `transactionReference`.

## Running

### Option A — Docker (API + RabbitMQ)

```bash
docker compose up --build
```

- Swagger UI: http://localhost:8080/swagger
- RabbitMQ management UI: http://localhost:15672 (guest / guest)
- Health: http://localhost:8080/health

### Option B — Local

```bash
docker compose up -d rabbitmq          # broker only
dotnet run --project src/PartnerIntegration.Api
```

To run with no broker at all: `RabbitMq__Enabled=false` (switches to the in-memory publisher).

### Calling the API

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/v1/dev/token | jq -r .access_token)

curl -i -X POST http://localhost:8080/api/v1/partner/transactions \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "partnerId": "P-1001",
        "transactionReference": "TXN-99823",
        "amount": 250.00,
        "currency": "USD",
        "timestamp": "2024-05-10T14:30:00Z"
      }'
```

`PartnerIntegration.Api.http` contains the same calls for VS / VS Code REST Client.

Known mock partners: `P-1001`, `P-1002`, `P-2050`. Anything else returns 404.

## Security

The endpoint is protected by JWT bearer authentication plus a `PartnerScope` policy requiring the claim `scope = transactions.write`. The sample validates a symmetric-key token so it is self-contained; in production you would swap `AddJwtBearer` to your IdP's authority (Entra ID, Auth0) and keep the same policy. The `/api/v1/dev/token` helper is only mapped in the Development environment.

Further hardening that would follow in a real deployment: mTLS or HMAC request signing per partner, rate limiting (`AddRateLimiter`) keyed on `partnerId`, and idempotency on `transactionReference`.

## Responses

| Situation | Status |
| --- | --- |
| Accepted and queued | `202 Accepted` |
| Payload invalid | `400` ValidationProblemDetails |
| Missing/invalid token or scope | `401` / `403` |
| Unknown partner | `404` ProblemDetails |
| Partner inactive | `422` ProblemDetails |
| Verification or broker unavailable | `503` ProblemDetails (+ `Retry-After`) |

## Tests

```bash
dotnet test
```

With coverage:

```bash
dotnet test --collect:"XPlat Code Coverage"
reportgenerator -reports:"**/coverage.cobertura.xml" -targetdir:coveragereport -reporttypes:Html
```

Coverage focuses on the areas that matter:

- `PartnerTransactionRequestValidatorTests` — every validation rule, including boundary amounts and currency casing.
- `TransactionServiceTests` — happy path + enrichment, invalid payload short-circuits the external call, unknown/inactive partner, and graceful degradation on outage.
- `ResilienceTests` — drives the **real** Polly pipeline with a scripted `HttpMessageHandler`: transient timeouts retried, 5xx retried, 404 not retried, exhausted retries surfaced as a domain exception.
- `PartnerTransactionEndpointTests` — end-to-end through `WebApplicationFactory`: auth enforcement, status-code mapping, `Retry-After` header, and the 30%-failure mock API.

## Tester Guide: Running the System

There are two ways to test: **manual testing** (calling the real API) and **automated testing** (`dotnet test`).

### 1. Run the system with Docker

Requirement: Docker Desktop installed.

```bash
docker compose up --build
```

Once it's up, verify:

- Swagger UI: http://localhost:8080/swagger
- Health check: http://localhost:8080/health
- RabbitMQ UI: http://localhost:15672 (user/pass: `guest` / `guest`)

### 2. Manual testing

**Step 1: Get a token** (this endpoint only exists in the Development environment, which docker-compose already enables)

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/v1/dev/token | jq -r .access_token)
```

If you're on Windows/PowerShell or don't have `jq`, call the endpoint from Swagger, copy the `access_token`, then click **Authorize**.

**Step 2: Submit a transaction**

```bash
curl -i -X POST http://localhost:8080/api/v1/partner/transactions \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
        "partnerId": "P-1001",
        "transactionReference": "TXN-99823",
        "amount": 250.00,
        "currency": "USD",
        "timestamp": "2024-05-10T14:30:00Z"
      }'
```

There is also a `PartnerIntegration.Api.http` file you can run with Visual Studio / VS Code REST Client.

### 3. Suggested test cases

| Case | How to test | Expected result |
| --- | --- | --- |
| Success | Partner `P-1001`, `P-1002` or `P-2050`, valid payload | `202 Accepted` |
| Invalid payload | amount ≤ 0, malformed currency, missing fields | `400` (ValidationProblemDetails) |
| No token | Omit the `Authorization` header | `401` |
| Token missing scope | Use a token without `transactions.write` | `403` |
| Unknown partner | `partnerId` = `P-9999` | `404` |
| Inactive partner | Use a partner in inactive state (check the mock API code for which ID) | `422` |
| Verification API failure | The mock API fails about 30% of the time; send many requests in a row | Occasional retries; if retries are exhausted, `503` with header `Retry-After: 30` |
| RabbitMQ down | Run `docker stop partner-rabbitmq`, then send a request | `503` with `Retry-After` |

For the **RabbitMQ down** case, remember to run `docker start partner-rabbitmq` when you're done.

### 4. Verify the message reached RabbitMQ

Open http://localhost:15672, go to **Exchanges**, and find the application's topic exchange. If no queue is bound to the exchange, messages will not be retained. To inspect the content, create a queue and bind it to the exchange (routing key `#`), resend the request, then go to **Queues → Get messages**.

### 5. Run automated tests

Requirement: .NET 8 SDK installed.

```bash
dotnet test
```

Run with coverage:

```bash
dotnet test --collect:"XPlat Code Coverage"
reportgenerator -reports:"**/coverage.cobertura.xml" -targetdir:coveragereport -reporttypes:Html
```

The test suite covers: validator, service, resilience (Polly retry/circuit breaker), and endpoint (end-to-end via `WebApplicationFactory`). These tests do not require Docker or a real RabbitMQ.

### 6. Run without Docker (optional)

If you only want to test the API without a broker:

```bash
RabbitMq__Enabled=false dotnet run --project src/PartnerIntegration.Api
```

The system then uses the in-memory publisher. This is convenient for quick testing but does not exercise the real RabbitMQ flow.


## Trade-offs / next steps

- No persistence: a transaction is acknowledged only once the broker confirms it. An outbox table would be the next step for exactly-once semantics.
- Idempotency on `transactionReference` is not implemented (deliberately out of scope for the exercise).
- The mock verification API is hosted in the same process for convenience; in reality it would be a separate service and the base URL would point at it.
